using System.Text.Json;

namespace Panko.Observability.Tests;

public sealed class ServiceTelemetryEvidenceAndAssessmentTests
{
    [Theory]
    [InlineData(
        ServiceTelemetryWorkloadKind.RequestDriven,
        "availability,errors,latency,traffic",
        ServiceMetricCatalog.RequestDrivenContract)]
    [InlineData(
        ServiceTelemetryWorkloadKind.Worker,
        "availability,duration,failures,throughput",
        ServiceMetricCatalog.WorkerContract)]
    public void ContractTemplatesAreDeterministicAndContainEveryRequiredRole(
        string workloadKind,
        string expectedRoles,
        string expectedContract)
    {
        var first = ServiceTelemetryEvidenceTemplate.Create(
            "payments-production",
            workloadKind,
            "payments.api+edge",
            "prod.eu");
        var second = ServiceTelemetryEvidenceTemplate.Create(
            "payments-production",
            workloadKind,
            "payments.api+edge",
            "prod.eu");
        var expected = ServiceTelemetryEvidenceJson.Serialize(first);

        first.Metrics.Reverse();
        var reordered = ServiceTelemetryEvidenceJson.Serialize(first);
        var reparsed = ServiceTelemetryEvidenceJson.Deserialize(expected);

        Assert.Equal(expected, ServiceTelemetryEvidenceJson.Serialize(second));
        Assert.Equal(expected, reordered);
        Assert.Equal(expected, ServiceTelemetryEvidenceJson.Serialize(reparsed));
        Assert.EndsWith("\n", expected, StringComparison.Ordinal);
        Assert.Equal(ServiceTelemetryEvidenceStatus.Partial, reparsed.Status);
        Assert.Equal(
            expectedRoles.Split(','),
            reparsed.Metrics.Select(metric => metric.Definition.Role));
        Assert.All(reparsed.Metrics, metric =>
        {
            Assert.Equal(metric.Definition.Role, metric.Definition.Id);
            Assert.Equal("required", metric.Definition.Requirement);
        });
        Assert.Contains(expectedContract, Assert.Single(reparsed.Gaps), StringComparison.Ordinal);
    }

    [Fact]
    public void EvidenceJsonRejectsUnknownPropertiesDuplicatePropertiesAndDanglingReferences()
    {
        var json = ServiceTelemetryEvidenceJson.Serialize(
            ServiceTelemetryEvidenceTemplate.Create(
                "payments-production",
                ServiceTelemetryWorkloadKind.RequestDriven,
                "payments.api+edge",
                "prod.eu"));
        var unknownProperty = string.Concat("{\n  \"unexpected\": true,", json.AsSpan(1));
        var duplicateProperty = ReplaceFirst(
            json,
            "  \"version\": 1,",
            "  \"version\": 1,\n  \"version\": 1,");
        var danglingReference = ReplaceFirst(
            json,
            "\"sourceRefs\": []",
            "\"sourceRefs\": [\"missing-source\"]");

        var unknownError = Assert.Throws<JsonException>(() =>
            ServiceTelemetryEvidenceJson.Deserialize(unknownProperty));
        var duplicateError = Assert.Throws<InvalidOperationException>(() =>
            ServiceTelemetryEvidenceJson.Deserialize(duplicateProperty));
        var danglingError = Assert.Throws<InvalidOperationException>(() =>
            ServiceTelemetryEvidenceJson.Deserialize(danglingReference));

        Assert.Contains("unexpected", unknownError.Message, StringComparison.Ordinal);
        Assert.Contains("duplicate property 'version'", duplicateError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("missing-source", danglingError.Message, StringComparison.Ordinal);
        Assert.Contains("was not found", danglingError.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvidenceJsonRejectsConnectorEndpointsAndContradictoryVerifiedOutcomes()
    {
        var endpointEvidence = Evidence();
        endpointEvidence.Sources[0] = new ServiceTelemetryEvidenceSource
        {
            Id = "metric-source",
            Kind = "grafana-dashboard",
            Authority = ServiceTelemetryEvidenceAuthority.MetricDefinition,
            Locator = "https://grafana.example/api/dashboards/uid/payments",
            Revision = "revision-42"
        };

        var endpointError = Assert.Throws<InvalidOperationException>(() =>
            ServiceTelemetryEvidenceJson.Serialize(endpointEvidence));
        var contradictoryJson = ReplaceFirst(
            ServiceTelemetryEvidenceJson.Serialize(Evidence()),
            "\"seriesCount\": 1",
            "\"seriesCount\": 2");
        var verificationError = Assert.Throws<InvalidOperationException>(() =>
            ServiceTelemetryEvidenceJson.Deserialize(contradictoryJson));

        Assert.Contains("connector endpoints", endpointError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("one logical series", verificationError.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("apiKey=abcdefghijklmno")]
    [InlineData("access_key=abcdefghijklmno")]
    [InlineData("credential: abcdefghijklmno")]
    [InlineData("Bearer abcdefghijklmno")]
    [InlineData("authorization=Bearer-abcdefghijklmno")]
    public void EvidenceJsonRejectsCredentialMaterialFromGapsAndMetricText(string sensitiveText)
    {
        var gapEvidence = Evidence();
        gapEvidence.Gaps.Add($"Discovery returned {sensitiveText}");

        var gapError = Assert.Throws<InvalidOperationException>(() =>
            ServiceTelemetryEvidenceJson.Serialize(gapEvidence));

        var metricEvidence = Evidence();
        var original = metricEvidence.Metrics[0];
        metricEvidence.Metrics[0] = new ServiceTelemetryMetricEvidence
        {
            Definition = Copy(original.Definition, title: sensitiveText),
            Provenance = original.Provenance,
            LiveVerification = original.LiveVerification
        };
        var metricError = Assert.Throws<InvalidOperationException>(() =>
            ServiceTelemetryEvidenceJson.Serialize(metricEvidence));

        Assert.Contains("credential", gapError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("credential", metricError.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EquivalentSemanticsReusePackAndReturnThresholdOverridesDespitePresentationDifferences()
    {
        var evidence = Evidence(definition => Copy(
            definition,
            id: $"observed-{definition.Role}",
            title: $"Observed {definition.Role}",
            dashboardRow: "Saturation",
            warningThreshold: definition.Role switch
            {
                "availability" => 0.98,
                "errors" => 0.02,
                "latency" => 1.5,
                _ => null
            },
            criticalThreshold: definition.Role switch
            {
                "availability" => 0.8,
                "errors" => 0.08,
                "latency" => 3,
                _ => null
            }));

        var assessment = Assess(evidence);

        Assert.Equal(ServiceMetricPackDecision.Reuse, assessment.Decision);
        Assert.Equal("request-pack-v1", assessment.SelectedMetricPackId);
        Assert.Equal(["request-pack-v1"], assessment.MatchingMetricPackIds);
        Assert.Empty(assessment.Blockers);
        Assert.Equal(
            ["availability", "error-ratio", "latency-p99"],
            assessment.ThresholdOverrides.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(0.98, assessment.ThresholdOverrides["availability"].Warning);
        Assert.Equal(0.8, assessment.ThresholdOverrides["availability"].Critical);
        Assert.Equal(0.02, assessment.ThresholdOverrides["error-ratio"].Warning);
        Assert.Equal(0.08, assessment.ThresholdOverrides["error-ratio"].Critical);
        Assert.Equal(1.5, assessment.ThresholdOverrides["latency-p99"].Warning);
        Assert.Equal(3, assessment.ThresholdOverrides["latency-p99"].Critical);
        Assert.Equal(4, assessment.RoleMappings.Count);
        Assert.All(assessment.RoleMappings, mapping =>
            Assert.Equal($"observed-{mapping.Role}", mapping.EvidenceMetricId));
    }

    [Fact]
    public void OneSemanticDifferenceCreatesANewPackFromTheKnownContract()
    {
        var evidence = Evidence(definition =>
            definition.Role == "latency"
                ? Copy(definition, unit: "milliseconds")
                : definition);

        var assessment = Assess(evidence);

        Assert.Equal(ServiceMetricPackDecision.NewPackFromContract, assessment.Decision);
        Assert.Equal(ServiceMetricCatalog.RequestDrivenContract, assessment.Contract);
        Assert.Null(assessment.SelectedMetricPackId);
        Assert.Empty(assessment.MatchingMetricPackIds);
        Assert.Empty(assessment.Blockers);
        Assert.Equal(4, assessment.ProposedMetrics.Count);
        Assert.Equal(
            "milliseconds",
            assessment.ProposedMetrics.Single(metric => metric.Role == "latency").Unit);
    }

    [Fact]
    public void PartialMissingRoleAndContextOnlyProvenanceAreBlocked()
    {
        var partial = Assess(Evidence(status: ServiceTelemetryEvidenceStatus.Partial));

        var missingRoleEvidence = Evidence();
        missingRoleEvidence.Metrics.RemoveAll(metric => metric.Definition.Role == "latency");
        var missingRole = Assess(missingRoleEvidence);

        var contextOnly = Assess(Evidence(
            metricDefinitionAuthority: ServiceTelemetryEvidenceAuthority.WorkloadContext));

        Assert.Equal(ServiceMetricPackDecision.Blocked, partial.Decision);
        Assert.Contains(
            partial.Blockers,
            blocker => blocker.Contains("status is 'partial'", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(ServiceMetricPackDecision.Blocked, missingRole.Decision);
        Assert.Contains(
            missingRole.Blockers,
            blocker => blocker.Contains("required 'latency' role", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(ServiceMetricPackDecision.Blocked, contextOnly.Decision);
        Assert.Contains(
            contextOnly.Blockers,
            blocker => blocker.Contains("lacks metric-definition evidence", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void HybridWorkloadRequiresContractDesignReview()
    {
        var assessment = Assess(Evidence(workloadKind: ServiceTelemetryWorkloadKind.Hybrid));

        Assert.Equal(ServiceMetricPackDecision.ContractDesignReview, assessment.Decision);
        Assert.Null(assessment.Contract);
        Assert.Null(assessment.SelectedMetricPackId);
        Assert.Contains(
            assessment.Blockers,
            blocker => blocker.Contains("Hybrid workloads", StringComparison.Ordinal));
    }

    [Fact]
    public void LiveQueryEvidenceAloneCannotEstablishTheWorkloadBoundary()
    {
        var evidence = Evidence();
        evidence.Workload.SourceRefs.Clear();
        evidence.Workload.SourceRefs.Add("live-source");

        var assessment = Assess(evidence);

        Assert.Equal(ServiceMetricPackDecision.Blocked, assessment.Decision);
        Assert.Contains(
            assessment.Blockers,
            blocker => blocker.Contains("workload-context evidence", StringComparison.Ordinal));
    }

    [Fact]
    public void MultipleEquivalentPacksBlockUntilDuplicateAuthorityIsResolved()
    {
        var firstPackStart = ServiceMetricTestData.PackYaml.IndexOf(
            "  - id: request-pack-v1",
            StringComparison.Ordinal);
        Assert.True(firstPackStart >= 0);
        var equivalentPack = ServiceMetricTestData.PackYaml[firstPackStart..]
            .Replace("id: request-pack-v1", "id: request-copy-v1", StringComparison.Ordinal)
            .Replace(
                "title: Request service fixture",
                "title: Equivalent request fixture",
                StringComparison.Ordinal);
        var catalog = ServiceMetricCatalog.Parse(
            $"{ServiceMetricTestData.PackYaml.TrimEnd()}\n{equivalentPack}");

        var assessment = new ServiceMetricPackAssessor().Assess(Evidence(), catalog);

        Assert.Equal(ServiceMetricPackDecision.Blocked, assessment.Decision);
        Assert.Equal(
            ["request-copy-v1", "request-pack-v1"],
            assessment.MatchingMetricPackIds);
        Assert.Contains(
            assessment.Blockers,
            blocker => blocker.Contains("Multiple service metric packs", StringComparison.Ordinal));
    }

    [Fact]
    public void LiveVerificationIsReportedAsOutstandingWithoutChangingTheSemanticDecision()
    {
        var outstanding = Assess(Evidence(unverifiedRole: "latency"));
        var verified = Assess(Evidence());

        Assert.Equal(ServiceMetricPackDecision.Reuse, outstanding.Decision);
        Assert.Equal(["latency-p99"], outstanding.OutstandingLiveVerification);
        Assert.Equal(ServiceMetricPackDecision.Reuse, verified.Decision);
        Assert.Empty(verified.OutstandingLiveVerification);
    }

    [Fact]
    public void ThresholdAssessmentIsMinimalAndValidationComparesEffectiveValues()
    {
        var evidence = Evidence(definition => definition.Role == "latency"
            ? Copy(definition, warningThreshold: 0.75)
            : definition);
        var assessment = Assess(evidence);
        var latencyOverride = Assert.Single(assessment.ThresholdOverrides).Value;

        Assert.Equal(0.75, latencyOverride.Warning);
        Assert.Null(latencyOverride.Critical);

        var catalog = ServiceMetricCatalog.Parse(ServiceMetricTestData.PackYaml);
        var minimalScope = ServiceMetricTestData.Scope(new()
        {
            ["latency-p99"] = new ServiceMetricThresholdOverride { Warning = 0.75 }
        });
        var redundantScope = ServiceMetricTestData.Scope(new()
        {
            ["latency-p99"] = new ServiceMetricThresholdOverride
            {
                Warning = 0.75,
                Critical = 2
            }
        });
        var generator = new ServiceDashboardGenerator();
        var dashboard = generator.Generate(
            "payments-production",
            catalog.CompilePlan(minimalScope));
        var validator = new ServiceOnboardingValidator(generator);

        Assert.True(validator.Validate(
            "payments-production",
            minimalScope,
            catalog,
            dashboard,
            evidence).IsValid);
        Assert.True(validator.Validate(
            "payments-production",
            redundantScope,
            catalog,
            dashboard,
            evidence).IsValid);
    }

    [Fact]
    public void OnboardingValidationRejectsEvidenceThatNoLongerSelectsTheRecipePack()
    {
        var catalog = ServiceMetricCatalog.Parse(ServiceMetricTestData.PackYaml);
        var scope = ServiceMetricTestData.Scope();
        var generator = new ServiceDashboardGenerator();
        var dashboard = generator.Generate("payments-production", catalog.CompilePlan(scope));
        var validator = new ServiceOnboardingValidator(generator);

        var valid = validator.Validate(
            "payments-production",
            scope,
            catalog,
            dashboard,
            Evidence());
        var drifted = validator.Validate(
            "payments-production",
            scope,
            catalog,
            dashboard,
            Evidence(definition => definition.Role == "latency"
                ? Copy(definition, unit: "milliseconds")
                : definition));

        Assert.True(valid.IsValid);
        Assert.False(drifted.IsValid);
        Assert.Contains(
            drifted.Errors,
            error => error.Contains("new-pack-from-contract", StringComparison.Ordinal));
    }

    private static ServiceMetricPackAssessment Assess(ServiceTelemetryEvidenceDocument evidence) =>
        new ServiceMetricPackAssessor().Assess(
            evidence,
            ServiceMetricCatalog.Parse(ServiceMetricTestData.PackYaml));

    internal static ServiceTelemetryEvidenceDocument Evidence(
        Func<ServiceMetricDefinition, ServiceMetricDefinition>? transform = null,
        string status = ServiceTelemetryEvidenceStatus.Complete,
        string workloadKind = ServiceTelemetryWorkloadKind.RequestDriven,
        string metricDefinitionAuthority = ServiceTelemetryEvidenceAuthority.MetricDefinition,
        string? unverifiedRole = null)
    {
        transform ??= definition => definition;
        return new ServiceTelemetryEvidenceDocument
        {
            Version = ServiceTelemetryEvidenceJson.SupportedVersion,
            RecipeId = "payments-production",
            Status = status,
            Workload = new ServiceTelemetryWorkloadEvidence
            {
                Kind = workloadKind,
                Service = "payments.api+edge",
                Environment = "prod.eu",
                SourceRefs = ["workload-source"]
            },
            Sources =
            [
                new ServiceTelemetryEvidenceSource
                {
                    Id = "metric-source",
                    Kind = "grafana-dashboard",
                    Authority = metricDefinitionAuthority,
                    Locator = "grafana/dashboard/request-service",
                    Revision = "revision-42"
                },
                new ServiceTelemetryEvidenceSource
                {
                    Id = "workload-source",
                    Kind = "service-catalog",
                    Authority = ServiceTelemetryEvidenceAuthority.WorkloadContext,
                    Locator = "catalog/payments-production",
                    Revision = "revision-7"
                },
                new ServiceTelemetryEvidenceSource
                {
                    Id = "live-source",
                    Kind = "prometheus-query",
                    Authority = ServiceTelemetryEvidenceAuthority.LiveVerification,
                    Locator = "prometheus/query-review",
                    Revision = "observed-20260803"
                }
            ],
            Metrics = RequestMetricDefinitions()
                .Select(transform)
                .Select(definition => new ServiceTelemetryMetricEvidence
                {
                    Definition = definition,
                    Provenance = new ServiceTelemetryMetricProvenance
                    {
                        Semantics = ["metric-source"],
                        Query = ["metric-source"],
                        Scope = ["metric-source"],
                        Datasource = ["metric-source"],
                        Unit = ["metric-source"],
                        Reducer = ["metric-source"],
                        Thresholds = definition.WarningThreshold.HasValue
                            ? ["metric-source"]
                            : []
                    },
                    LiveVerification = definition.Role == unverifiedRole
                        ? new ServiceTelemetryLiveVerification
                        {
                            Status = ServiceTelemetryLiveVerificationStatus.NotRun
                        }
                        : new ServiceTelemetryLiveVerification
                        {
                            Status = ServiceTelemetryLiveVerificationStatus.Verified,
                            SourceRefs = ["live-source"],
                            NonEmptyNumeric = true,
                            SeriesCount = 1
                        }
                })
                .ToList()
        };
    }

    private static IReadOnlyList<ServiceMetricDefinition> RequestMetricDefinitions() =>
    [
        new ServiceMetricDefinition
        {
            Id = "availability",
            Title = "Available instances",
            Role = "availability",
            PromQl = "min(up{service=~\"{{serviceRegex}}\",environment=~\"{{environmentRegex}}\"})",
            DatasourceUid = "prometheus-main",
            Unit = "ratio",
            TimeReducer = "minimum",
            CrumbMode = "anomaly",
            Requirement = "required",
            WarningThreshold = 0.99,
            CriticalThreshold = 0.9,
            Direction = "below",
            DashboardRow = "Availability"
        },
        new ServiceMetricDefinition
        {
            Id = "traffic-rate",
            Title = "Request rate",
            Role = "traffic",
            PromQl = "sum(rate(http_requests_total{service=~\"{{serviceRegex}}\",environment=~\"{{environmentRegex}}\"}[5m]))",
            DatasourceUid = "prometheus-main",
            Unit = "requests/s",
            TimeReducer = "maximum",
            CrumbMode = "context",
            Requirement = "required",
            Direction = "above",
            DashboardRow = "Overview"
        },
        new ServiceMetricDefinition
        {
            Id = "error-ratio",
            Title = "Error ratio",
            Role = "errors",
            PromQl = "sum(rate(http_errors_total{service=~\"{{serviceRegex}}\",environment=~\"{{environmentRegex}}\"}[5m])) / clamp_min(sum(rate(http_requests_total{service=~\"{{serviceRegex}}\",environment=~\"{{environmentRegex}}\"}[5m])), 1)",
            DatasourceUid = "prometheus-main",
            Unit = "ratio",
            TimeReducer = "maximum",
            CrumbMode = "anomaly",
            Requirement = "required",
            WarningThreshold = 0.01,
            CriticalThreshold = 0.05,
            Direction = "above",
            DashboardRow = "Traffic"
        },
        new ServiceMetricDefinition
        {
            Id = "latency-p99",
            Title = "p99 latency",
            Role = "latency",
            PromQl = "histogram_quantile(0.99, sum by (le) (rate(http_request_duration_seconds_bucket{service=~\"{{serviceRegex}}\",environment=~\"{{environmentRegex}}\"}[5m])))",
            DatasourceUid = "prometheus-main",
            Unit = "seconds",
            TimeReducer = "maximum",
            CrumbMode = "anomaly",
            Requirement = "required",
            WarningThreshold = 1,
            CriticalThreshold = 2,
            Direction = "above",
            DashboardRow = "Traffic"
        }
    ];

    private static ServiceMetricDefinition Copy(
        ServiceMetricDefinition source,
        string? id = null,
        string? title = null,
        string? unit = null,
        string? dashboardRow = null,
        double? warningThreshold = null,
        double? criticalThreshold = null) => new()
        {
            Id = id ?? source.Id,
            Title = title ?? source.Title,
            Role = source.Role,
            PromQl = source.PromQl,
            DatasourceUid = source.DatasourceUid,
            Unit = unit ?? source.Unit,
            TimeReducer = source.TimeReducer,
            CrumbMode = source.CrumbMode,
            Requirement = source.Requirement,
            WarningThreshold = warningThreshold ?? source.WarningThreshold,
            CriticalThreshold = criticalThreshold ?? source.CriticalThreshold,
            Direction = source.Direction,
            DashboardRow = dashboardRow ?? source.DashboardRow
        };

    private static string ReplaceFirst(string value, string oldValue, string newValue)
    {
        var index = value.IndexOf(oldValue, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Fixture did not contain '{oldValue}'.");
        return string.Concat(
            value.AsSpan(0, index),
            newValue,
            value.AsSpan(index + oldValue.Length));
    }
}
