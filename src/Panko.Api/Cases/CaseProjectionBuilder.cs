using System.Text.Json.Nodes;
using Panko.Api.Domain;
using Panko.Api.Cases;
using SubmittedCrumbKind = Panko.Contracts.SubmittedCrumbKind;

namespace Panko.Api.Cases;

/// <summary>
/// Rebuilds a Case File exclusively from durable inputs and retained Crumb-source snapshots.
/// Previous Case File Crumb and Trail state are intentionally not accepted by this API.
/// </summary>
public sealed class CaseFileProjectionBuilder(TimeProvider timeProvider)
{
    private static readonly HashSet<string> CollectedLifecycleCategories = new(StringComparer.Ordinal)
    {
        "pagerduty-incident-triggered",
        "pagerduty-incident-acknowledged",
        "pagerduty-incident-escalated",
        "pagerduty-incident-reassigned",
        "pagerduty-incident-resolved",
        "pagerduty-incident-reopened"
    };

    private const int MaximumCrumbs = 500;
    private const int MaximumTrailEntries = 250;

    public CaseFile Build(
        CaseRecord caseRecord,
        Recipe recipe,
        string recipeRevision,
        long targetInputVersion,
        IReadOnlyList<CaseInput> activeInputs,
        IReadOnlyList<CrumbSourceResult> retainedCrumbSourceResults,
        AiSynthesis ai,
        PatternContext? pattern,
        CrumbCollectionOutcome? collectionOutcome = null)
    {
        if (targetInputVersion < 0 || targetInputVersion > caseRecord.InputVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetInputVersion),
                "A projection target must be an accepted Case input version.");
        }

        var inputs = activeInputs
            .Where(input => input.InputVersion <= targetInputVersion)
            .OrderBy(input => input.Sequence)
            .ToList();
        var projected = inputs.Select(Project).ToList();

        var crumbs = projected
            .Select(item => item.Crumb)
            .Where(item => item is not null)
            .Cast<Crumb>()
            .Concat(retainedCrumbSourceResults.SelectMany(result => result.Crumbs))
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => CrumbRankingPolicy.Rank(group, caseRecord.OpenedAt)[0])
            .ToList();
        crumbs = CrumbRankingPolicy.OrderForCaseFile(crumbs, caseRecord.OpenedAt, MaximumCrumbs).ToList();

        var sequenceByTrailId = projected.ToDictionary(
            item => item.Trail.StableId,
            item => item.Sequence,
            StringComparer.Ordinal);
        var createdTrail = CreatedTrailWhenMissing(caseRecord, inputs);
        var trailCandidates = projected.Select(item => item.Trail)
            .Concat(retainedCrumbSourceResults.SelectMany(result => result.Trail))
            .Append(createdTrail)
            .GroupBy(item => item.StableId, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(item => SeverityRank(item.Severity))
                .ThenByDescending(item => item.Url is not null)
                .ThenBy(item => item.Summary, StringComparer.Ordinal)
                .First())
            .ToList();
        var trail = SelectBoundedTrail(
                trailCandidates,
                sequenceByTrailId,
                createdTrail.StableId)
            .OrderBy(item => item.OccurredAt)
            .ThenBy(item => sequenceByTrailId.GetValueOrDefault(item.StableId, long.MaxValue))
            .ThenBy(item => item.StableId, StringComparer.Ordinal)
            .ToList();

        var sources = retainedCrumbSourceResults
            .OrderBy(result => result.Source, StringComparer.Ordinal)
            .Select(result => new CrumbSourceStatus(
                result.Source,
                result.Health,
                result.Crumbs.Count,
                result.DurationMilliseconds,
                result.Diagnostic,
                result.Links,
                result.Health == CrumbSourceHealth.Unavailable
                    ? CrumbSourceRequestState.Errored
                    : CrumbSourceRequestState.Received))
            .ToList();
        if (inputs.Any(input => string.Equals(input.TrustLevel, "submitted", StringComparison.Ordinal)))
        {
            sources.Add(new CrumbSourceStatus(
                "submitted",
                CrumbSourceHealth.Complete,
                projected.Count(item => item.Crumb is not null),
                0,
                "Agent-submitted inputs are retained as unverified Crumbs.",
                [],
                CrumbSourceRequestState.Received));
        }
        sources = sources
            .GroupBy(source => source.Source, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(source => source.Source, StringComparer.Ordinal)
            .ToList();

        var links = retainedCrumbSourceResults.SelectMany(result => result.Links)
            .DistinctBy(link => link.Url, StringComparer.Ordinal)
            .OrderBy(link => link.Label, StringComparer.Ordinal)
            .ThenBy(link => link.Url, StringComparer.Ordinal)
            .Take(100)
            .ToList();
        var causalMarkers = CaseFileComposer.BuildCausalMarkers(crumbs);
        var signalGroups = crumbs.Where(CrumbRankingPolicy.IsHighSignal)
            .Select(CrumbRankingPolicy.GroupKey)
            .Distinct(StringComparer.Ordinal)
            .Count();
        var signalSources = crumbs.Where(CrumbRankingPolicy.IsHighSignal)
            .Select(item => item.Source)
            .Distinct(StringComparer.Ordinal)
            .Count();
        var submittedInputCount = inputs.Count(input =>
            string.Equals(input.TrustLevel, "submitted", StringComparison.Ordinal)
            && !IsCaseCreatedCategory(input.Category));
        var effectiveSourceLabel = inputs.Any(input =>
            string.Equals(input.TrustLevel, "submitted", StringComparison.Ordinal))
            ? "effective source"
            : "source";
        var summary = collectionOutcome is { Clarity.IsClear: false }
            ? CaseFileComposer.InconclusiveSummary(collectionOutcome, signalGroups, signalSources)
            : signalGroups == 0
                ? submittedInputCount == 0
                    ? "No high-signal anomalies were identified in the configured Crumb window."
                    : $"Projected {submittedInputCount} submitted input{(submittedInputCount == 1 ? "" : "s")}; no high-signal anomaly was identified."
                : $"Found {signalGroups} high-signal Crumb group{(signalGroups == 1 ? "" : "s")} across {signalSources} {effectiveSourceLabel}{(signalSources == 1 ? "" : "s")}.";
        var unavailable = sources
            .Where(source => source.Health == CrumbSourceHealth.Unavailable)
            .Select(source => source.Source)
            .ToList();
        if (unavailable.Count > 0)
        {
            summary += $" Unavailable sources: {string.Join(", ", unavailable)}.";
        }
        if (causalMarkers.Count > 1)
        {
            var outline = causalMarkers
                .Select(item => CaseFileComposer.CausalLabel(item.Category))
                .Distinct(StringComparer.Ordinal)
                .Take(5);
            summary += $" Candidate sequence: {string.Join(" → ", outline)}.";
        }
        var status = collectionOutcome is not null
            ? CaseProgression.ForCompletedCollection(caseRecord.IsFrozen, sources)
            : caseRecord.IsFrozen
                ? CaseProgression.Resolved
                : caseRecord.InputVersion > targetInputVersion
                    ? CaseProgression.Rebuilding
                    : CaseProgression.Ready;

        return new CaseFile(
            caseRecord.Id,
            caseRecord.PagerDutyIncidentId,
            caseRecord.ServiceId,
            recipe.Id,
            recipeRevision,
            caseRecord.Title,
            caseRecord.Urgency,
            caseRecord.PagerDutyState,
            status,
            caseRecord.OpenedAt,
            timeProvider.GetUtcNow(),
            caseRecord.Version,
            summary,
            ai,
            trail,
            crumbs,
            sources,
            links,
            causalMarkers,
            pattern)
        {
            Origin = caseRecord.Origin,
            InputVersion = caseRecord.InputVersion,
            ProjectedInputVersion = targetInputVersion,
            CreatedBy = caseRecord.CreatedBy
        };
    }

    private static ProjectedInput Project(CaseInput input)
    {
        var submitted = string.Equals(input.TrustLevel, "submitted", StringComparison.Ordinal);
        var source = submitted
            ? "submitted"
            : string.IsNullOrWhiteSpace(input.DeclaredSource) ? "system" : input.DeclaredSource;
        var trail = new TrailCandidate(
            $"case-input:{input.Id:N}:trail",
            input.OccurredAt,
            source,
            input.Category,
            input.Summary,
            input.Severity,
            input.Url,
            input.Actor,
            input.ObjectType,
            input.ObjectId);

        if (!submitted
            && (IsCaseCreatedCategory(input.Category)
                || CollectedLifecycleCategories.Contains(input.Category)))
        {
            return new ProjectedInput(input.Sequence, trail, null);
        }

        var provenance = new JsonObject
        {
            ["caseInputId"] = input.Id.ToString(),
            ["clientEventId"] = input.ClientCrumbId,
            ["sequence"] = input.Sequence,
            ["inputVersion"] = input.InputVersion,
            ["inputType"] = input.Kind.ToString().ToLowerInvariant(),
            ["producerPrincipal"] = input.ProducerPrincipal,
            ["declaredSource"] = input.DeclaredSource,
            ["sourceReference"] = input.SourceReference,
            ["trustLevel"] = input.TrustLevel,
            ["receivedAt"] = input.ReceivedAt.ToUniversalTime().ToString("O"),
            ["attributes"] = input.Attributes.DeepClone()
        };
        var confidence = input.Kind switch
        {
            SubmittedCrumbKind.Crumb => 0.55,
            SubmittedCrumbKind.Note => 0.20,
            _ => 0.50
        };
        var crumb = new Crumb(
            $"case-input:{input.Id:N}:crumb",
            source,
            input.OccurredAt,
            null,
            input.Category,
            input.Severity,
            input.Summary,
            input.Excerpt,
            input.Url,
            confidence,
            provenance,
            input.Actor,
            input.ObjectType,
            input.ObjectId,
            CodeReferences: null);
        return new ProjectedInput(input.Sequence, trail, crumb);
    }

    private static TrailCandidate CreatedTrailWhenMissing(
        CaseRecord caseRecord,
        IReadOnlyList<CaseInput> inputs)
    {
        var creationCategory = caseRecord.Origin.Kind == CaseOriginKind.PagerDuty
            ? "pagerduty-incident-triggered"
            : "case-created";
        if (inputs.FirstOrDefault(input => input.Category == creationCategory) is { } existing)
        {
            // The duplicate ID is discarded; this keeps the append-based composition expression simple.
            return Project(existing).Trail;
        }

        var (source, kind, summary) = caseRecord.Origin.Kind switch
        {
            CaseOriginKind.Agent => ("submitted", "case-created", "Case created by agent"),
            CaseOriginKind.Manual => ("manual", "case-created", "Case created"),
            _ => ("pagerduty", "pagerduty-incident-triggered", "PagerDuty incident triggered")
        };
        return new TrailCandidate(
            $"case-created:{caseRecord.Id:N}",
            caseRecord.OpenedAt,
            source,
            kind,
            summary,
            caseRecord.Urgency == "high" ? "critical" : "warning",
            null);
    }

    private static int SeverityRank(string severity) => severity switch
    {
        "critical" => 3,
        "warning" => 2,
        _ => 1
    };

    private static bool IsCaseCreatedCategory(string category) => category == "case-created";

    private static IReadOnlyList<TrailCandidate> SelectBoundedTrail(
        IReadOnlyList<TrailCandidate> candidates,
        IReadOnlyDictionary<string, long> sequenceByTrailId,
        string createdTrailId)
    {
        if (candidates.Count <= MaximumTrailEntries) return candidates;

        var byId = candidates.ToDictionary(item => item.StableId, StringComparer.Ordinal);
        var selected = new List<TrailCandidate>(MaximumTrailEntries);
        var selectedIds = new HashSet<string>(StringComparer.Ordinal);

        Add(createdTrailId);

        // Durable inputs take precedence over snapshot-only entries. Select them by append
        // sequence so a newly accepted (possibly backdated) input cannot disappear merely
        // because the final presentation is ordered by occurrence time.
        foreach (var trailId in sequenceByTrailId
                     .Where(item => !string.Equals(item.Key, createdTrailId, StringComparison.Ordinal))
                     .OrderByDescending(item => item.Value)
                     .ThenBy(item => item.Key, StringComparer.Ordinal)
                     .Select(item => item.Key))
        {
            Add(trailId);
            if (selected.Count == MaximumTrailEntries) return selected;
        }

        // Use the remaining capacity for the newest retained snapshot entries, then restore
        // chronological order at the call site.
        foreach (var candidate in candidates
                     .Where(item => !selectedIds.Contains(item.StableId))
                     .OrderByDescending(item => item.OccurredAt)
                     .ThenBy(item => item.StableId, StringComparer.Ordinal))
        {
            Add(candidate.StableId);
            if (selected.Count == MaximumTrailEntries) break;
        }

        return selected;

        void Add(string trailId)
        {
            if (selectedIds.Add(trailId) && byId.TryGetValue(trailId, out var candidate))
            {
                selected.Add(candidate);
            }
        }
    }

    private sealed record ProjectedInput(
        long Sequence,
        TrailCandidate Trail,
        Crumb? Crumb);
}
