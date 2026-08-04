using Panko.Api.Domain;
using Panko.Api.Crumbs;
using System.Text.Json.Nodes;

namespace Panko.Api.CaseFiles;

public sealed class CaseFileComposer(TimeProvider timeProvider, CrumbSourceRegistry crumbSources)
{
    private const int MaximumRetainedCrumbs = 500;
    private const int MaximumRetainedTrailEntries = 250;

    public CaseFile ComposeInitial(
        CaseRecord caseRecord,
        Recipe recipe,
        string recipeRevision,
        PatternContext? pattern = null)
    {
        var sources = crumbSources.EnabledSources(recipe)
            .Select(source => new CrumbSourceStatus(
                source, CrumbSourceHealth.Pending, 0, 0, null, [], CrumbSourceRequestState.Requested))
            .ToList();
        var caseSeverity = caseRecord.Urgency == "high" ? "critical" : "warning";
        var (originSource, originKind, originSummary) = caseRecord.Origin.Kind switch
        {
            CaseOriginKind.Agent => ("submitted", "case-created", "Case created by agent"),
            CaseOriginKind.Manual => ("manual", "case-created", "Case created"),
            _ => ("pagerduty", "pagerduty-incident-triggered", "PagerDuty incident triggered")
        };
        var trail = new List<TrailCandidate>
        {
            new(caseRecord.OpenedAt, originSource, originKind, originSummary,
                caseSeverity, null)
        };
        if (caseRecord.PagerDutyState != PagerDutyIncidentState.Triggered && caseRecord.Origin.Kind == CaseOriginKind.PagerDuty)
        {
            trail.Add(new TrailCandidate(
                caseRecord.UpdatedAt,
                "pagerduty",
                "pagerduty-incident-state",
                $"PagerDuty incident {caseRecord.PagerDutyState.ToString().ToLowerInvariant()}",
                "info",
                null));
        }
        return new CaseFile(
            caseRecord.Id, caseRecord.PagerDutyIncidentId, caseRecord.ServiceId, recipe.Id, recipeRevision,
            caseRecord.Title, caseRecord.Urgency, caseRecord.PagerDutyState, CaseProgression.Collecting, caseRecord.OpenedAt,
            timeProvider.GetUtcNow(), caseRecord.Version,
            "Case opened. Crumb collectors are running.",
            new AiSynthesis("pending", null, [], [], [], null), trail, [], sources, [], [], pattern)
        {
            Origin = caseRecord.Origin,
            InputVersion = caseRecord.InputVersion,
            ProjectedInputVersion = caseRecord.ProjectedInputVersion,
            CreatedBy = caseRecord.CreatedBy
        };
    }

    public CaseFile ComposeCollectionStarted(
        CaseRecord caseRecord,
        Recipe recipe,
        string recipeRevision,
        CaseFile? previous,
        PatternContext? pattern = null)
    {
        if (previous is null)
        {
            return ComposeInitial(caseRecord, recipe, recipeRevision, pattern);
        }

        var previousSources = previous.CrumbSources.ToDictionary(source => source.Source, StringComparer.Ordinal);
        var requestedSources = crumbSources.EnabledSources(recipe)
            .Select(source => previousSources.TryGetValue(source, out var existing)
                ? existing with
                {
                    Health = CrumbSourceHealth.Pending,
                    CrumbCount = 0,
                    DurationMilliseconds = 0,
                    Diagnostic = null,
                    RequestState = CrumbSourceRequestState.Requested
                }
                : new CrumbSourceStatus(
                    source, CrumbSourceHealth.Pending, 0, 0, null, [], CrumbSourceRequestState.Requested))
            .ToList();

        return previous with
        {
            RecipeId = recipe.Id,
            RecipeRevision = recipeRevision,
            Title = caseRecord.Title,
            Urgency = caseRecord.Urgency,
            PagerDutyState = caseRecord.PagerDutyState,
            Status = CaseProgression.Collecting,
            UpdatedAt = timeProvider.GetUtcNow(),
            CrumbSources = requestedSources,
            Pattern = pattern ?? previous.Pattern,
            Origin = caseRecord.Origin,
            InputVersion = caseRecord.InputVersion,
            CreatedBy = caseRecord.CreatedBy
        };
    }

    public CaseFile Compose(
        CaseRecord caseRecord,
        Recipe recipe,
        string recipeRevision,
        IReadOnlyList<CrumbSourceResult> results,
        CaseFile? previous,
        AiSynthesis ai,
        PatternContext? pattern = null,
        CrumbCollectionOutcome? collectionOutcome = null)
    {
        var crumbs = (previous?.Crumbs ?? [])
            .Concat(results.SelectMany(result => result.Crumbs))
            .GroupBy(crumb => crumb.Id, StringComparer.Ordinal)
            .Select(group => group.Last())
            .ToList();
        crumbs = CrumbRankingPolicy.OrderForCaseFile(crumbs, caseRecord.OpenedAt, MaximumRetainedCrumbs).ToList();
        var trail = RetainTrail(
            (previous?.Trail ?? []).Concat(results.SelectMany(result => result.Trail)),
            caseRecord.OpenedAt,
            MaximumRetainedTrailEntries);
        var sources = results.OrderBy(result => result.Source, StringComparer.Ordinal)
            .Select(result => new CrumbSourceStatus(result.Source, result.Health, result.Crumbs.Count,
                result.DurationMilliseconds, result.Diagnostic, result.Links,
                result.Health == CrumbSourceHealth.Unavailable
                    ? CrumbSourceRequestState.Errored
                    : CrumbSourceRequestState.Received))
            .ToList();
        var links = results.SelectMany(result => result.Links)
            .DistinctBy(link => link.Url, StringComparer.Ordinal)
            .OrderBy(link => link.Label, StringComparer.Ordinal)
            .Take(100)
            .ToList();
        var signalGroups = crumbs.Where(CrumbRankingPolicy.IsHighSignal)
            .Select(CrumbRankingPolicy.GroupKey)
            .Distinct(StringComparer.Ordinal)
            .Count();
        var signalSources = crumbs.Where(CrumbRankingPolicy.IsHighSignal)
            .Select(item => item.Source)
            .Distinct(StringComparer.Ordinal)
            .Count();
        var unavailable = sources.Where(source => source.Health == CrumbSourceHealth.Unavailable).Select(source => source.Source).ToList();
        var causalMarkers = BuildCausalMarkers(crumbs);
        var summary = collectionOutcome is { Clarity.IsClear: false }
            ? InconclusiveSummary(collectionOutcome, signalGroups, signalSources)
            : signalGroups == 0
                ? "No high-signal anomalies were identified in the configured Crumb window."
                : $"Found {signalGroups} high-signal Crumb group{(signalGroups == 1 ? "" : "s")} across {signalSources} source{(signalSources == 1 ? "" : "s")}.";
        if (unavailable.Count > 0) summary += $" Unavailable sources: {string.Join(", ", unavailable)}.";
        if (causalMarkers.Count > 1)
        {
            var outline = causalMarkers.Select(item => CausalLabel(item.Category)).Distinct(StringComparer.Ordinal).Take(5);
            summary += $" Candidate sequence: {string.Join(" → ", outline)}.";
        }
        var status = CaseProgression.ForCompletedCollection(caseRecord.IsFrozen, sources);

        return new CaseFile(
            caseRecord.Id, caseRecord.PagerDutyIncidentId, caseRecord.ServiceId, recipe.Id, recipeRevision,
            caseRecord.Title, caseRecord.Urgency, caseRecord.PagerDutyState, status, caseRecord.OpenedAt,
            timeProvider.GetUtcNow(), caseRecord.Version, summary, ai, trail, crumbs, sources, links, causalMarkers, pattern)
        {
            Origin = caseRecord.Origin,
            InputVersion = caseRecord.InputVersion,
            ProjectedInputVersion = caseRecord.ProjectedInputVersion,
            CreatedBy = caseRecord.CreatedBy
        };
    }

    internal static string InconclusiveSummary(
        CrumbCollectionOutcome outcome,
        int signalGroups,
        int signalSources)
    {
        var summary = outcome.CompletionReason switch
        {
            CrumbCollectionCompletionReason.MaximumWindowReached =>
                $"Crumb collection reached the bounded {outcome.FinalLookbackMinutes}-minute lookback without a clear deterministic result.",
            CrumbCollectionCompletionReason.NoExpandableCrumbSources =>
                "Crumb collection completed without a clear deterministic result; no selected source supports wider historical search.",
            CrumbCollectionCompletionReason.NoCrumbSources =>
                "Crumb collection completed without a clear deterministic result because no Crumb source was selected.",
            _ => "Crumb collection completed without a clear deterministic result."
        };
        if (signalGroups > 0)
        {
            summary += $" Retained {signalGroups} high-signal Crumb group{(signalGroups == 1 ? "" : "s")} across {signalSources} source{(signalSources == 1 ? "" : "s")} for review.";
        }
        return summary;
    }

    internal static string CausalLabel(string category)
    {
        if (category.StartsWith("kafka-", StringComparison.Ordinal))
        {
            if (category.Contains("lag", StringComparison.Ordinal)) return "Kafka lag observed";
            if (category.Contains("rebalance", StringComparison.Ordinal)
                || category.Contains("election", StringComparison.Ordinal)) return "Kafka state-change activity";
            if (category.Contains("availability", StringComparison.Ordinal)
                || category.Contains("offline", StringComparison.Ordinal)
                || category.Contains("replic", StringComparison.Ordinal)
                || category.Contains("leader", StringComparison.Ordinal)) return "Kafka availability anomaly";
            if (category.Contains("producer", StringComparison.Ordinal)) return "Kafka producer anomaly";
            if (category.Contains("consumer", StringComparison.Ordinal)) return "Kafka consumer anomaly";
            if (category.Contains("broker", StringComparison.Ordinal)) return "Kafka broker anomaly";
            if (category.Contains("jvm", StringComparison.Ordinal)) return "Kafka JVM pressure";
            return "Kafka anomaly observed";
        }

        return category switch
        {
            "merge-request-created" => "MR created",
            "merge-request-merged" => "MR merged",
            "pipeline" => "pipeline failed",
            "pipeline-job-output" => "failed pipeline step",
            "deployment" => "production deployment",
            "service-registration" => "Consul registration failure",
            "service-health" => "Consul health failure",
            "workload-failure" => "Nomad failure",
            "metric" => "metric threshold breach",
            "first-error" => "first log error",
            _ => category
        };
    }

    internal static IReadOnlyList<CausalMarker> BuildCausalMarkers(IReadOnlyList<Crumb> crumbs)
    {
        var categoryOrder = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["merge-request-created"] = 1,
            ["merge-request-merged"] = 2,
            ["pipeline"] = 3,
            ["pipeline-job-output"] = 3,
            ["deployment"] = 4,
            ["workload-failure"] = 5,
            ["metric"] = 6,
            ["first-error"] = 7
        };

        return crumbs
            .Where(crumb => IsCausalCrumb(crumb, categoryOrder))
            .GroupBy(
                crumb => CrumbRankingPolicy.IsKafkaCrumb(crumb)
                    ? $"{crumb.Category}|{CrumbRankingPolicy.GroupKey(crumb)}"
                    : crumb.Category is "pipeline" or "pipeline-job-output"
                    ? CrumbRankingPolicy.GroupKey(crumb)
                    : crumb.Id,
                StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(crumb => crumb.Category == "pipeline-job-output")
                .ThenByDescending(crumb => CausalSeverityRank(crumb.Severity))
                .ThenByDescending(crumb => crumb.Confidence)
                .First())
            .OrderBy(CausalOccurredAt)
            .ThenBy(crumb => categoryOrder.GetValueOrDefault(crumb.Category, 5))
            .ThenBy(crumb => crumb.Id, StringComparer.Ordinal)
            .Select(crumb => new CausalMarker(
                $"causal-{crumb.Id}", crumb.Category, CausalLabel(crumb.Category),
                CausalOccurredAt(crumb), crumb.Summary, crumb.Source,
                crumb.Id, crumb.Actor, crumb.Url, crumb.ObjectType, crumb.ObjectId, crumb.CodeReferences ?? []))
            .Take(50)
            .ToList();
    }

    private static DateTimeOffset CausalOccurredAt(Crumb crumb) =>
        crumb.Category == "metric" ? MetricCrumb.Start(crumb) : crumb.OccurredAt;

    private static int CausalSeverityRank(string severity) => severity switch
    {
        "critical" => 3,
        "warning" => 2,
        "info" => 1,
        _ => 0
    };

    private static bool IsCausalCrumb(
        Crumb crumb,
        IReadOnlyDictionary<string, int> categoryOrder)
    {
        if (string.Equals(crumb.Source, "submitted", StringComparison.Ordinal)
            && string.Equals(
                crumb.Provenance["inputType"]?.GetValue<string>(),
                "note",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (categoryOrder.ContainsKey(crumb.Category))
        {
            if (crumb.Category == "metric")
            {
                return CrumbRankingPolicy.IsHighSignal(crumb)
                    && MetricCrumb.HasReliableTimestamp(crumb);
            }
            return crumb.Category is not ("pipeline" or "pipeline-job-output")
                || CrumbRankingPolicy.IsHighSignal(crumb);
        }

        return CrumbRankingPolicy.IsKafkaAnomaly(crumb)
            && ScopeBoolean(crumb, "timestampSupported");
    }

    private static bool ScopeBoolean(Crumb crumb, string name)
    {
        if (crumb.Provenance["scope"] is not JsonObject scope) return false;
        var property = scope.FirstOrDefault(item =>
            string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase));
        return property.Value is JsonValue value
            && (value.TryGetValue<bool>(out var boolean) && boolean
                || value.TryGetValue<string>(out var text)
                && bool.TryParse(text, out var parsed)
                && parsed);
    }

    internal static IReadOnlyList<TrailCandidate> RetainTrail(
        IEnumerable<TrailCandidate> candidates,
        DateTimeOffset caseOpenedAt,
        int maximumItems = MaximumRetainedTrailEntries)
    {
        if (maximumItems <= 0) return [];

        var unique = candidates
            .GroupBy(TrailIdentity, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(item => TrailSeverityRank(item.Severity))
                .ThenByDescending(item => item.Url is not null)
                .ThenBy(item => item.Url, StringComparer.Ordinal)
                .ThenBy(item => item.Actor, StringComparer.Ordinal)
                .ThenBy(item => item.ObjectType, StringComparer.Ordinal)
                .ThenBy(item => item.ObjectId, StringComparer.Ordinal)
                .First())
            .ToList();
        if (unique.Count <= maximumItems) return Chronological(unique);

        var newestReservation = Math.Max(1, maximumItems / 5);
        var highSignalReservation = Math.Max(1, maximumItems * 2 / 5);
        var selected = new Dictionary<string, TrailCandidate>(StringComparer.Ordinal);

        Add(unique
            .OrderByDescending(item => item.OccurredAt)
            .ThenBy(item => item.Source, StringComparer.Ordinal)
            .ThenBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Summary, StringComparer.Ordinal), newestReservation);

        Add(unique
            .Where(item => TrailSeverityRank(item.Severity) >= 2)
            .OrderByDescending(item => TrailSeverityRank(item.Severity))
            .ThenBy(item => ProximityTicks(item.OccurredAt, caseOpenedAt))
            .ThenByDescending(item => item.OccurredAt)
            .ThenBy(item => item.Source, StringComparer.Ordinal)
            .ThenBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Summary, StringComparer.Ordinal), highSignalReservation);

        Add(unique
            .OrderBy(item => ProximityTicks(item.OccurredAt, caseOpenedAt))
            .ThenByDescending(item => TrailSeverityRank(item.Severity))
            .ThenByDescending(item => item.OccurredAt)
            .ThenBy(item => item.Source, StringComparer.Ordinal)
            .ThenBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Summary, StringComparer.Ordinal), maximumItems - selected.Count);

        return Chronological(selected.Values);

        void Add(IEnumerable<TrailCandidate> ordered, int requested)
        {
            if (requested <= 0 || selected.Count >= maximumItems) return;
            var target = Math.Min(maximumItems, selected.Count + requested);
            foreach (var item in ordered)
            {
                selected.TryAdd(TrailIdentity(item), item);
                if (selected.Count >= target) break;
            }
        }
    }

    private static IReadOnlyList<TrailCandidate> Chronological(IEnumerable<TrailCandidate> candidates) =>
        candidates
            .OrderBy(item => item.OccurredAt)
            .ThenBy(item => item.StableId, StringComparer.Ordinal)
            .ToList();

    private static string TrailIdentity(TrailCandidate item) => item.StableId;

    private static long ProximityTicks(DateTimeOffset at, DateTimeOffset caseOpenedAt) =>
        Math.Abs((at - caseOpenedAt).Ticks);

    private static int TrailSeverityRank(string severity) => severity.ToLowerInvariant() switch
    {
        "critical" => 4,
        "error" => 3,
        "warning" => 2,
        _ => 1
    };
}
