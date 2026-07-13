using IncidentBot.Api.Domain;
using IncidentBot.Api.Connectors;

namespace IncidentBot.Api.Incidents;

public sealed class ReportComposer(TimeProvider timeProvider, EvidenceSourceRegistry evidenceSources)
{
    private const int MaximumRetainedEvidence = 500;
    private const int MaximumRetainedTimeline = 250;

    public InvestigationReport ComposeInitial(
        IncidentRecord incident,
        InvestigationProfile profile,
        string profileRevision,
        ProblemContext? problem = null)
    {
        var sources = evidenceSources.EnabledSources(profile)
            .Select(source => new SourceReport(
                source, SourceHealth.Pending, 0, 0, null, [], SourceRequestState.Requested))
            .ToList();
        var incidentSeverity = incident.Urgency == "high" ? "critical" : "warning";
        var timeline = new List<TimelineCandidate>
        {
            new(incident.TriggeredAt, "pagerduty", "incident-triggered", "PagerDuty incident triggered",
                incidentSeverity, null)
        };
        if (incident.State != IncidentState.Triggered)
        {
            timeline.Add(new TimelineCandidate(
                incident.UpdatedAt,
                "pagerduty",
                "incident-state",
                $"PagerDuty incident {incident.State.ToString().ToLowerInvariant()}",
                "info",
                null));
        }
        return new InvestigationReport(
            incident.Id, incident.PagerDutyIncidentId, incident.ServiceId, profile.Id, profileRevision,
            incident.Title, incident.Urgency, incident.State, IncidentProgression.Collecting, incident.TriggeredAt,
            timeProvider.GetUtcNow(), incident.Version,
            "Investigation started. Evidence collectors are running.",
            new AiSynthesis("pending", null, [], [], [], null), timeline, [], sources, [], [], problem);
    }

    public InvestigationReport ComposeCollectionStarted(
        IncidentRecord incident,
        InvestigationProfile profile,
        string profileRevision,
        InvestigationReport? previous,
        ProblemContext? problem = null)
    {
        if (previous is null)
        {
            return ComposeInitial(incident, profile, profileRevision, problem);
        }

        var previousSources = previous.Sources.ToDictionary(source => source.Source, StringComparer.Ordinal);
        var requestedSources = evidenceSources.EnabledSources(profile)
            .Select(source => previousSources.TryGetValue(source, out var existing)
                ? existing with
                {
                    Health = SourceHealth.Pending,
                    FindingCount = 0,
                    DurationMilliseconds = 0,
                    Diagnostic = null,
                    RequestState = SourceRequestState.Requested
                }
                : new SourceReport(
                    source, SourceHealth.Pending, 0, 0, null, [], SourceRequestState.Requested))
            .ToList();

        return previous with
        {
            ProfileId = profile.Id,
            ProfileRevision = profileRevision,
            Title = incident.Title,
            Urgency = incident.Urgency,
            State = incident.State,
            Status = IncidentProgression.Collecting,
            UpdatedAt = timeProvider.GetUtcNow(),
            Sources = requestedSources,
            Problem = problem ?? previous.Problem
        };
    }

    public InvestigationReport Compose(
        IncidentRecord incident,
        InvestigationProfile profile,
        string profileRevision,
        IReadOnlyList<ConnectorResult> results,
        InvestigationReport? previous,
        AiSynthesis ai,
        ProblemContext? problem = null,
        EvidenceCollectionOutcome? collectionOutcome = null)
    {
        var evidence = (previous?.Evidence ?? [])
            .Concat(results.SelectMany(result => result.Findings))
            .GroupBy(finding => finding.Id, StringComparer.Ordinal)
            .Select(group => group.Last())
            .ToList();
        evidence = EvidenceRankingPolicy.OrderForReport(evidence, incident.TriggeredAt, MaximumRetainedEvidence).ToList();
        var timeline = RetainTimeline(
            (previous?.Timeline ?? []).Concat(results.SelectMany(result => result.Timeline)),
            incident.TriggeredAt,
            MaximumRetainedTimeline);
        var sources = results.OrderBy(result => result.Source, StringComparer.Ordinal)
            .Select(result => new SourceReport(result.Source, result.Health, result.Findings.Count,
                result.DurationMilliseconds, result.Diagnostic, result.Links,
                result.Health == SourceHealth.Unavailable
                    ? SourceRequestState.Errored
                    : SourceRequestState.Received))
            .ToList();
        var links = results.SelectMany(result => result.Links)
            .DistinctBy(link => link.Url, StringComparer.Ordinal)
            .OrderBy(link => link.Label, StringComparer.Ordinal)
            .Take(100)
            .ToList();
        var signalGroups = evidence.Where(EvidenceRankingPolicy.IsHighSignal)
            .Select(EvidenceRankingPolicy.GroupKey)
            .Distinct(StringComparer.Ordinal)
            .Count();
        var signalSources = evidence.Where(EvidenceRankingPolicy.IsHighSignal)
            .Select(item => item.Source)
            .Distinct(StringComparer.Ordinal)
            .Count();
        var unavailable = sources.Where(source => source.Health == SourceHealth.Unavailable).Select(source => source.Source).ToList();
        var causalEvents = BuildCausalEvents(evidence);
        var summary = collectionOutcome is { Clarity.IsClear: false }
            ? InconclusiveSummary(collectionOutcome, signalGroups, signalSources)
            : signalGroups == 0
                ? "No high-signal anomalies were identified in the configured evidence window."
                : $"Found {signalGroups} high-signal evidence group{(signalGroups == 1 ? "" : "s")} across {signalSources} source{(signalSources == 1 ? "" : "s")}.";
        if (unavailable.Count > 0) summary += $" Unavailable sources: {string.Join(", ", unavailable)}.";
        if (causalEvents.Count > 1)
        {
            var outline = causalEvents.Select(item => CausalLabel(item.Category)).Distinct(StringComparer.Ordinal).Take(5);
            summary += $" Candidate sequence: {string.Join(" → ", outline)}.";
        }
        var status = IncidentProgression.ForCompletedCollection(incident.IsFrozen, sources);

        return new InvestigationReport(
            incident.Id, incident.PagerDutyIncidentId, incident.ServiceId, profile.Id, profileRevision,
            incident.Title, incident.Urgency, incident.State, status, incident.TriggeredAt,
            timeProvider.GetUtcNow(), incident.Version, summary, ai, timeline, evidence, sources, links, causalEvents, problem);
    }

    private static string InconclusiveSummary(
        EvidenceCollectionOutcome outcome,
        int signalGroups,
        int signalSources)
    {
        var summary = outcome.CompletionReason switch
        {
            EvidenceCollectionCompletionReason.MaximumWindowReached =>
                $"Evidence collection reached the bounded {outcome.FinalLookbackMinutes}-minute lookback without a clear deterministic result.",
            EvidenceCollectionCompletionReason.NoExpandableConnectors =>
                "Evidence collection completed without a clear deterministic result; no selected source supports wider historical search.",
            EvidenceCollectionCompletionReason.NoConnectors =>
                "Evidence collection completed without a clear deterministic result because no connector was selected.",
            _ => "Evidence collection completed without a clear deterministic result."
        };
        if (signalGroups > 0)
        {
            summary += $" Retained {signalGroups} high-signal evidence group{(signalGroups == 1 ? "" : "s")} across {signalSources} source{(signalSources == 1 ? "" : "s")} for review.";
        }
        return summary;
    }

    private static string CausalLabel(string category) => category switch
    {
        "merge-request-created" => "MR created",
        "merge-request-merged" => "MR merged",
        "pipeline" => "pipeline failed",
        "pipeline-job-output" => "failed pipeline step",
        "deployment" => "production deployment",
        "workload-failure" => "Nomad failure",
        "first-error" => "first log error",
        _ => category
    };

    internal static IReadOnlyList<CausalEvent> BuildCausalEvents(IReadOnlyList<EvidenceFinding> evidence)
    {
        var categoryOrder = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["merge-request-created"] = 1,
            ["merge-request-merged"] = 2,
            ["pipeline"] = 3,
            ["pipeline-job-output"] = 3,
            ["deployment"] = 4,
            ["workload-failure"] = 5,
            ["first-error"] = 6
        };

        return evidence
            .Where(finding => categoryOrder.ContainsKey(finding.Category)
                && (finding.Category is not ("pipeline" or "pipeline-job-output")
                    || EvidenceRankingPolicy.IsHighSignal(finding)))
            .GroupBy(
                finding => finding.Category is "pipeline" or "pipeline-job-output"
                    ? EvidenceRankingPolicy.GroupKey(finding)
                    : finding.Id,
                StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(finding => finding.Category == "pipeline-job-output")
                .ThenByDescending(finding => finding.Confidence)
                .First())
            .OrderBy(finding => finding.OccurredAt)
            .ThenBy(finding => categoryOrder[finding.Category])
            .ThenBy(finding => finding.Id, StringComparer.Ordinal)
            .Select(finding => new CausalEvent(
                $"causal-{finding.Id}", finding.Category, CausalLabel(finding.Category),
                finding.OccurredAt, finding.Summary, finding.Source,
                finding.Id, finding.Actor, finding.Url, finding.ObjectType, finding.ObjectId, finding.CodeReferences ?? []))
            .Take(50)
            .ToList();
    }

    internal static IReadOnlyList<TimelineCandidate> RetainTimeline(
        IEnumerable<TimelineCandidate> candidates,
        DateTimeOffset incidentTriggeredAt,
        int maximumItems = MaximumRetainedTimeline)
    {
        if (maximumItems <= 0) return [];

        var unique = candidates
            .GroupBy(TimelineIdentity, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(item => TimelineSeverityRank(item.Severity))
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
        var selected = new Dictionary<string, TimelineCandidate>(StringComparer.Ordinal);

        Add(unique
            .OrderByDescending(item => item.OccurredAt)
            .ThenBy(item => item.Source, StringComparer.Ordinal)
            .ThenBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Summary, StringComparer.Ordinal), newestReservation);

        Add(unique
            .Where(item => TimelineSeverityRank(item.Severity) >= 2)
            .OrderByDescending(item => TimelineSeverityRank(item.Severity))
            .ThenBy(item => ProximityTicks(item.OccurredAt, incidentTriggeredAt))
            .ThenByDescending(item => item.OccurredAt)
            .ThenBy(item => item.Source, StringComparer.Ordinal)
            .ThenBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Summary, StringComparer.Ordinal), highSignalReservation);

        Add(unique
            .OrderBy(item => ProximityTicks(item.OccurredAt, incidentTriggeredAt))
            .ThenByDescending(item => TimelineSeverityRank(item.Severity))
            .ThenByDescending(item => item.OccurredAt)
            .ThenBy(item => item.Source, StringComparer.Ordinal)
            .ThenBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Summary, StringComparer.Ordinal), maximumItems - selected.Count);

        return Chronological(selected.Values);

        void Add(IEnumerable<TimelineCandidate> ordered, int requested)
        {
            if (requested <= 0 || selected.Count >= maximumItems) return;
            var target = Math.Min(maximumItems, selected.Count + requested);
            foreach (var item in ordered)
            {
                selected.TryAdd(TimelineIdentity(item), item);
                if (selected.Count >= target) break;
            }
        }
    }

    private static IReadOnlyList<TimelineCandidate> Chronological(IEnumerable<TimelineCandidate> candidates) =>
        candidates
            .OrderBy(item => item.OccurredAt)
            .ThenBy(item => item.Source, StringComparer.Ordinal)
            .ThenBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Summary, StringComparer.Ordinal)
            .ToList();

    private static string TimelineIdentity(TimelineCandidate item) =>
        $"{item.OccurredAt:O}|{item.Source}|{item.Kind}|{item.Summary}";

    private static long ProximityTicks(DateTimeOffset at, DateTimeOffset incidentTriggeredAt) =>
        Math.Abs((at - incidentTriggeredAt).Ticks);

    private static int TimelineSeverityRank(string severity) => severity.ToLowerInvariant() switch
    {
        "critical" => 4,
        "error" => 3,
        "warning" => 2,
        _ => 1
    };
}
