namespace Panko.Observability.Onboarding;

public sealed class ServiceMetricPackAssessor
{
    private static readonly string[] RequiredMetricFields =
    [
        "title", "role", "promQl", "datasourceUid", "unit", "timeReducer",
        "crumbMode", "requirement", "dashboardRow"
    ];

    public ServiceMetricPackAssessment Assess(
        ServiceTelemetryEvidenceDocument evidence,
        ServiceMetricCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(catalog);
        ServiceTelemetryEvidenceJson.Validate(evidence);

        var outstandingVerification = evidence.Metrics
            .Where(metric => !IsLiveVerified(metric, evidence.Sources))
            .Select(metric => metric.Definition.Id)
            .Order(StringComparer.Ordinal)
            .ToList();
        var contract = Contract(evidence.Workload.Kind);
        if (contract is null)
        {
            var reviewReason = evidence.Workload.Kind == ServiceTelemetryWorkloadKind.Hybrid
                ? "Hybrid workloads require separate reviewed Recipes or an explicitly reviewed normalized contract."
                : $"Workload kind '{evidence.Workload.Kind}' is not covered by a supported service metric contract.";
            return Result(
                evidence,
                ServiceMetricPackDecision.ContractDesignReview,
                contract: null,
                blockers: evidence.Gaps.Prepend(reviewReason),
                outstandingVerification: outstandingVerification);
        }

        var blockers = EvidenceBlockers(evidence, contract);
        if (blockers.Count > 0)
        {
            return Result(
                evidence,
                ServiceMetricPackDecision.Blocked,
                contract,
                blockers: blockers,
                outstandingVerification: outstandingVerification);
        }

        var candidate = new ServiceMetricPack
        {
            Id = "evidence-candidate-v1",
            Title = "Reviewed evidence candidate",
            Contract = contract,
            Metrics = evidence.Metrics.Select(metric => metric.Definition).ToList()
        };
        try
        {
            ServiceMetricCatalog.ValidateCandidate(candidate);
        }
        catch (InvalidOperationException exception)
        {
            return Result(
                evidence,
                ServiceMetricPackDecision.Blocked,
                contract,
                blockers: [exception.Message],
                outstandingVerification: outstandingVerification);
        }

        var matches = catalog.Packs
            .Where(pack => string.Equals(pack.Contract, contract, StringComparison.Ordinal))
            .Select(pack => new { Pack = pack, Pairs = EquivalentPairs(evidence.Metrics, pack.Metrics) })
            .Where(candidateMatch => candidateMatch.Pairs is not null)
            .OrderBy(candidateMatch => candidateMatch.Pack.Id, StringComparer.Ordinal)
            .ToArray();
        if (matches.Length > 1)
        {
            return Result(
                evidence,
                ServiceMetricPackDecision.Blocked,
                contract,
                matchingPackIds: matches.Select(match => match.Pack.Id),
                blockers:
                [
                    "Multiple service metric packs have equivalent semantics; review and retire the duplicate authority before onboarding."
                ],
                outstandingVerification: outstandingVerification);
        }

        if (matches.Length == 0)
        {
            return Result(
                evidence,
                ServiceMetricPackDecision.NewPackFromContract,
                contract,
                roleMappings: evidence.Metrics.Select(metric => Mapping(metric, metric.Definition.Id)),
                outstandingVerification: outstandingVerification,
                proposedMetrics: evidence.Metrics.Select(metric => metric.Definition));
        }

        var match = matches[0];
        var thresholdOverrides = new Dictionary<string, ServiceMetricThresholdOverride>(StringComparer.Ordinal);
        foreach (var pair in match.Pairs!)
        {
            var observed = pair.Evidence.Definition;
            var existing = pair.Pack;
            if (observed.WarningThreshold != existing.WarningThreshold
                || observed.CriticalThreshold != existing.CriticalThreshold)
            {
                thresholdOverrides.Add(existing.Id, new ServiceMetricThresholdOverride
                {
                    Warning = observed.WarningThreshold != existing.WarningThreshold
                        ? observed.WarningThreshold
                        : null,
                    Critical = observed.CriticalThreshold != existing.CriticalThreshold
                        ? observed.CriticalThreshold
                        : null
                });
            }
        }

        return Result(
            evidence,
            ServiceMetricPackDecision.Reuse,
            contract,
            selectedPackId: match.Pack.Id,
            matchingPackIds: [match.Pack.Id],
            thresholdOverrides: thresholdOverrides,
            roleMappings: match.Pairs.Select(pair => Mapping(pair.Evidence, pair.Pack.Id)),
            outstandingVerification: outstandingVerification);
    }

    private static List<string> EvidenceBlockers(
        ServiceTelemetryEvidenceDocument evidence,
        string contract)
    {
        var blockers = new List<string>();
        if (evidence.Status != ServiceTelemetryEvidenceStatus.Complete)
        {
            blockers.Add(
                $"Evidence snapshot status is '{evidence.Status}'; complete discovery before editing observability configuration.");
        }
        blockers.AddRange(evidence.Gaps.Select(gap => $"Evidence gap: {gap}"));
        var sources = evidence.Sources.ToDictionary(source => source.Id, StringComparer.Ordinal);
        if (evidence.Workload.SourceRefs.Count == 0)
        {
            blockers.Add("The logical workload boundary has no evidence source reference.");
        }
        else if (!evidence.Workload.SourceRefs.Any(sourceRef =>
                     sources[sourceRef].Authority is ServiceTelemetryEvidenceAuthority.MetricDefinition
                         or ServiceTelemetryEvidenceAuthority.WorkloadContext))
        {
            blockers.Add(
                "The logical workload boundary requires metric-definition or workload-context evidence.");
        }
        try
        {
            ServicePromQlRenderer.ValidateScope(new ServiceMetricScope
            {
                MetricPackId = "evidence-candidate-v1",
                Service = evidence.Workload.Service,
                Environment = evidence.Workload.Environment
            });
        }
        catch (InvalidOperationException exception)
        {
            blockers.Add(exception.Message);
        }

        foreach (var metric in evidence.Metrics.OrderBy(metric => metric.Definition.Id, StringComparer.Ordinal))
        {
            var missingFields = MissingFields(metric.Definition);
            if (missingFields.Count > 0)
            {
                blockers.Add(
                    $"Metric '{metric.Definition.Id}' is missing reviewed fields: {string.Join(", ", missingFields)}.");
            }

            var missingEvidence = MissingDefinitionEvidence(metric, sources);
            if (missingEvidence.Count > 0)
            {
                blockers.Add(
                    $"Metric '{metric.Definition.Id}' lacks metric-definition evidence for: "
                    + $"{string.Join(", ", missingEvidence)}.");
            }
        }

        foreach (var role in ServiceMetricCatalog.RequiredRolesFor(contract))
        {
            if (!evidence.Metrics.Any(metric =>
                    string.Equals(metric.Definition.Role, role, StringComparison.Ordinal)
                    && string.Equals(metric.Definition.Requirement, "required", StringComparison.Ordinal)))
            {
                blockers.Add(
                    $"Contract '{contract}' requires a reviewed required '{role}' role mapping.");
            }
        }
        return blockers.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    }

    private static List<string> MissingFields(ServiceMetricDefinition metric)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["title"] = metric.Title,
            ["role"] = metric.Role,
            ["promQl"] = metric.PromQl,
            ["datasourceUid"] = metric.DatasourceUid,
            ["unit"] = metric.Unit,
            ["timeReducer"] = metric.TimeReducer,
            ["crumbMode"] = metric.CrumbMode,
            ["requirement"] = metric.Requirement,
            ["dashboardRow"] = metric.DashboardRow
        };
        return RequiredMetricFields.Where(field => string.IsNullOrWhiteSpace(values[field])).ToList();
    }

    private static List<string> MissingDefinitionEvidence(
        ServiceTelemetryMetricEvidence metric,
        IReadOnlyDictionary<string, ServiceTelemetryEvidenceSource> sources)
    {
        var provenance = metric.Provenance;
        var required = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal)
        {
            ["semantics"] = provenance.Semantics,
            ["query"] = provenance.Query,
            ["scope"] = provenance.Scope,
            ["datasource"] = provenance.Datasource,
            ["unit"] = provenance.Unit,
            ["reducer"] = provenance.Reducer
        };
        if (metric.Definition.IsAnomaly
            || metric.Definition.WarningThreshold.HasValue
            || metric.Definition.CriticalThreshold.HasValue)
        {
            required.Add("thresholds", provenance.Thresholds);
        }
        return required
            .Where(item => !item.Value.Any(sourceRef =>
                sources.TryGetValue(sourceRef, out var source)
                && source.Authority == ServiceTelemetryEvidenceAuthority.MetricDefinition))
            .Select(item => item.Key)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsLiveVerified(
        ServiceTelemetryMetricEvidence metric,
        IReadOnlyCollection<ServiceTelemetryEvidenceSource> sources)
    {
        var verification = metric.LiveVerification;
        if (verification.Status != ServiceTelemetryLiveVerificationStatus.Verified
            || verification.NonEmptyNumeric != true
            || verification.SeriesCount != 1)
        {
            return false;
        }
        var sourceById = sources.ToDictionary(source => source.Id, StringComparer.Ordinal);
        return verification.SourceRefs.Any(sourceRef =>
            sourceById.TryGetValue(sourceRef, out var source)
            && source.Authority == ServiceTelemetryEvidenceAuthority.LiveVerification);
    }

    private static IReadOnlyList<MetricPair>? EquivalentPairs(
        IReadOnlyCollection<ServiceTelemetryMetricEvidence> evidenceMetrics,
        IReadOnlyCollection<ServiceMetricDefinition> packMetrics)
    {
        if (evidenceMetrics.Count != packMetrics.Count) return null;
        var evidenceGroups = evidenceMetrics.GroupBy(metric => MetricShapeFor(metric.Definition))
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.Definition.Id, StringComparer.Ordinal).ToArray());
        var packGroups = packMetrics.GroupBy(MetricShapeFor)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray());
        if (evidenceGroups.Count != packGroups.Count) return null;

        var pairs = new List<MetricPair>();
        foreach (var shape in evidenceGroups.Keys.OrderBy(MetricShapeKey, StringComparer.Ordinal))
        {
            if (!packGroups.TryGetValue(shape, out var packGroup)) return null;
            var evidenceGroup = evidenceGroups[shape];
            if (evidenceGroup.Length != packGroup.Length) return null;
            if (evidenceGroup.Length == 1)
            {
                pairs.Add(new MetricPair(evidenceGroup[0], packGroup[0]));
                continue;
            }

            var packById = packGroup.ToDictionary(metric => metric.Id, StringComparer.Ordinal);
            if (evidenceGroup.Any(metric => !packById.ContainsKey(metric.Definition.Id))) return null;
            pairs.AddRange(evidenceGroup.Select(metric =>
                new MetricPair(metric, packById[metric.Definition.Id])));
        }
        return pairs.OrderBy(pair => pair.Pack.Id, StringComparer.Ordinal).ToArray();
    }

    private static MetricShape MetricShapeFor(ServiceMetricDefinition metric) => new(
        metric.Role,
        metric.PromQl,
        metric.DatasourceUid,
        metric.Unit,
        metric.TimeReducer,
        metric.CrumbMode,
        metric.Requirement,
        string.IsNullOrEmpty(metric.Direction) ? "above" : metric.Direction,
        metric.WarningThreshold.HasValue || metric.CriticalThreshold.HasValue);

    private static string MetricShapeKey(MetricShape shape) => string.Join(
        '\u001f',
        shape.Role,
        shape.PromQl,
        shape.DatasourceUid,
        shape.Unit,
        shape.TimeReducer,
        shape.CrumbMode,
        shape.Requirement,
        shape.Direction,
        shape.HasThresholds ? "thresholds" : "no-thresholds");

    private static ServiceMetricRoleMapping Mapping(
        ServiceTelemetryMetricEvidence metric,
        string packMetricId) => new()
        {
            Role = metric.Definition.Role,
            EvidenceMetricId = metric.Definition.Id,
            PackMetricId = packMetricId,
            SourceRefs = metric.Provenance.AllSourceRefs()
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList()
        };

    private static string? Contract(string workloadKind) => workloadKind switch
    {
        ServiceTelemetryWorkloadKind.RequestDriven => ServiceMetricCatalog.RequestDrivenContract,
        ServiceTelemetryWorkloadKind.Worker => ServiceMetricCatalog.WorkerContract,
        _ => null
    };

    private static ServiceMetricPackAssessment Result(
        ServiceTelemetryEvidenceDocument evidence,
        string decision,
        string? contract,
        string? selectedPackId = null,
        IEnumerable<string>? matchingPackIds = null,
        IReadOnlyDictionary<string, ServiceMetricThresholdOverride>? thresholdOverrides = null,
        IEnumerable<ServiceMetricRoleMapping>? roleMappings = null,
        IEnumerable<string>? blockers = null,
        IEnumerable<string>? outstandingVerification = null,
        IEnumerable<ServiceMetricDefinition>? proposedMetrics = null) => new()
        {
            RecipeId = evidence.RecipeId,
            Decision = decision,
            Contract = contract,
            SelectedMetricPackId = selectedPackId,
            MatchingMetricPackIds = (matchingPackIds ?? [])
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList(),
            ThresholdOverrides = (thresholdOverrides ?? new Dictionary<string, ServiceMetricThresholdOverride>())
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal),
            RoleMappings = (roleMappings ?? [])
                .OrderBy(mapping => mapping.PackMetricId, StringComparer.Ordinal)
                .ThenBy(mapping => mapping.EvidenceMetricId, StringComparer.Ordinal)
                .ToList(),
            Blockers = (blockers ?? [])
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList(),
            OutstandingLiveVerification = (outstandingVerification ?? [])
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList(),
            ProposedMetrics = (proposedMetrics ?? [])
                .OrderBy(metric => metric.Id, StringComparer.Ordinal)
                .ToList()
        };

    private sealed record MetricPair(
        ServiceTelemetryMetricEvidence Evidence,
        ServiceMetricDefinition Pack);

    private sealed record MetricShape(
        string Role,
        string PromQl,
        string DatasourceUid,
        string Unit,
        string TimeReducer,
        string CrumbMode,
        string Requirement,
        string Direction,
        bool HasThresholds);
}
