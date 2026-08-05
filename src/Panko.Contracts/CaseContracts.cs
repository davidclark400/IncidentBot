using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Panko.Contracts;

public enum PagerDutyIncidentState
{
    [JsonStringEnumMemberName("triggered")]
    Triggered,
    [JsonStringEnumMemberName("acknowledged")]
    Acknowledged,
    [JsonStringEnumMemberName("escalated")]
    Escalated,
    [JsonStringEnumMemberName("reassigned")]
    Reassigned,
    [JsonStringEnumMemberName("resolved")]
    Resolved,
    [JsonStringEnumMemberName("unknown")]
    Unknown
}

public enum CaseOriginKind
{
    [JsonStringEnumMemberName("pagerDuty")]
    PagerDuty,
    [JsonStringEnumMemberName("agent")]
    Agent,
    [JsonStringEnumMemberName("manual")]
    Manual
}

public sealed record CaseOrigin(
    CaseOriginKind Kind,
    string? ExternalId);

public enum CrumbSourceHealth
{
    [JsonStringEnumMemberName("pending")]
    Pending,
    [JsonStringEnumMemberName("complete")]
    Complete,
    [JsonStringEnumMemberName("partial")]
    Partial,
    [JsonStringEnumMemberName("unavailable")]
    Unavailable,
    [JsonStringEnumMemberName("excluded")]
    Excluded
}

public enum CrumbSourceRequestState
{
    [JsonStringEnumMemberName("requested")]
    Requested,
    [JsonStringEnumMemberName("received")]
    Received,
    [JsonStringEnumMemberName("errored")]
    Errored
}

public enum SignatureStage
{
    [JsonStringEnumMemberName("provisional")]
    Provisional,
    [JsonStringEnumMemberName("final")]
    Final
}

public enum PatternLifecycleState
{
    [JsonStringEnumMemberName("new")]
    New,
    [JsonStringEnumMemberName("ongoing")]
    Ongoing,
    [JsonStringEnumMemberName("resolved")]
    Resolved,
    [JsonStringEnumMemberName("regressed")]
    Regressed,
    [JsonStringEnumMemberName("escalating")]
    Escalating
}

public sealed record SourceLink(string Label, string Url);

public sealed record AiSummaryPart(string Text, string? ReferenceId = null);

public sealed record AiSummaryReference(string Id, string Label, string Kind, string Href);

public sealed record CodeReference(
    string Id,
    string ProjectId,
    string CommitSha,
    string Path,
    int StartLine,
    int EndLine,
    string Url,
    string Excerpt);

public sealed record CausalMarker(
    string Id,
    string Category,
    string? Label,
    DateTimeOffset OccurredAt,
    string Summary,
    string Source,
    string CrumbId,
    string? Actor,
    string? Url,
    string? ObjectType,
    string? ObjectId,
    IReadOnlyList<CodeReference> CodeReferences);

public sealed record TrailEntry(
    string Id,
    DateTimeOffset OccurredAt,
    string Source,
    string Kind,
    string Summary,
    string Severity,
    string? Url,
    string? Actor,
    string? ObjectType,
    string? ObjectId);

public sealed record Crumb(
    string Id,
    string Source,
    DateTimeOffset OccurredAt,
    DateTimeOffset? EndedAt,
    string Category,
    string Severity,
    string Summary,
    string? Excerpt,
    string? Url,
    double Confidence,
    JsonObject Provenance,
    string? Actor,
    string? ObjectType,
    string? ObjectId,
    IReadOnlyList<CodeReference>? CodeReferences);

public sealed record CrumbSourceStatus(
    string Source,
    CrumbSourceHealth Health,
    int CrumbCount,
    long DurationMilliseconds,
    string? Diagnostic,
    IReadOnlyList<SourceLink> Links,
    CrumbSourceRequestState RequestState);

public enum CaseProgressPhase
{
    [JsonStringEnumMemberName("collecting")]
    Collecting,
    [JsonStringEnumMemberName("synthesizing")]
    Synthesizing,
    [JsonStringEnumMemberName("finalizing")]
    Finalizing,
    [JsonStringEnumMemberName("completed")]
    Completed
}

public enum CrumbSourceProgressState
{
    [JsonStringEnumMemberName("pending")]
    Pending,
    [JsonStringEnumMemberName("querying")]
    Querying,
    [JsonStringEnumMemberName("received")]
    Received,
    [JsonStringEnumMemberName("timedOut")]
    TimedOut,
    [JsonStringEnumMemberName("failed")]
    Failed,
    [JsonStringEnumMemberName("excluded")]
    Excluded
}

public enum AiSynthesisProgressState
{
    [JsonStringEnumMemberName("pending")]
    Pending,
    [JsonStringEnumMemberName("running")]
    Running,
    [JsonStringEnumMemberName("complete")]
    Complete,
    [JsonStringEnumMemberName("unavailable")]
    Unavailable,
    [JsonStringEnumMemberName("skipped")]
    Skipped
}

public sealed record CrumbSourceProgress(
    string Source,
    CrumbSourceProgressState RequestState,
    CrumbSourceHealth Health,
    int Pass,
    int LookbackMinutes,
    long DurationMilliseconds,
    int CrumbCount,
    string? Diagnostic,
    DateTimeOffset? StartedAt,
    DateTimeOffset UpdatedAt);

public sealed record CaseEarlyCrumb(
    string Id,
    string Source,
    DateTimeOffset OccurredAt,
    string Severity,
    string Summary,
    double Confidence);

public sealed record CaseProgressProjection(
    Guid CaseId,
    Guid AttemptId,
    long Revision,
    int BaseCaseFileVersion,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    long ElapsedDurationMilliseconds,
    CaseProgressPhase Phase,
    int CurrentPass,
    int CurrentLookbackMinutes,
    bool DeterministicCaseFileUsable,
    bool OnlyAiSynthesisRemaining,
    AiSynthesisProgressState AiSynthesisState,
    IReadOnlyList<CrumbSourceProgress> CrumbSources,
    IReadOnlyList<CaseEarlyCrumb> EarlyCrumbs);

public sealed record AiDiagnosis(
    string Summary,
    IReadOnlyList<string> CrumbIds,
    IReadOnlyList<CodeReference> CodeReferences,
    int Rank,
    int CrumbStrength);

public sealed record AiSynthesis(
    string Status,
    string? Summary,
    IReadOnlyList<string> PossibleContributors,
    IReadOnlyList<string> Unknowns,
    IReadOnlyList<string> RecommendedChecks,
    string? CrumbHash,
    IReadOnlyList<AiDiagnosis>? Diagnoses,
    IReadOnlyList<AiSummaryPart>? SummaryParts,
    IReadOnlyList<AiSummaryReference>? SummaryReferences);

public sealed record PatternOccurrence(
    Guid CaseId,
    string? PagerDutyIncidentId,
    PagerDutyIncidentState PagerDutyState,
    DateTimeOffset OccurredAt,
    DateTimeOffset UpdatedAt,
    string? CaseUrl);

public sealed record PossiblePatternMatch(
    string PatternKey,
    string MatchType,
    int Score,
    IReadOnlyList<string> MatchedFeatures,
    DateTimeOffset LastSeen);

public sealed record PatternContext(
    string Availability,
    string AlgorithmVersion,
    SignatureStage Stage,
    string? PatternKey,
    Guid? PatternId,
    PatternLifecycleState? LifecycleState,
    string? MatchType,
    int? MatchScore,
    IReadOnlyList<string> MatchedFeatures,
    int OccurrenceCount,
    DateTimeOffset? FirstSeen,
    DateTimeOffset? LastSeen,
    IReadOnlyList<PatternOccurrence> RecentOccurrences,
    IReadOnlyList<PossiblePatternMatch> PossibleMatches,
    double Completeness,
    string? Diagnostic);

public sealed record CaseFile(
    Guid CaseId,
    string? PagerDutyIncidentId,
    string ServiceId,
    string RecipeId,
    string RecipeRevision,
    string Title,
    string Urgency,
    PagerDutyIncidentState PagerDutyState,
    string Status,
    DateTimeOffset OpenedAt,
    DateTimeOffset UpdatedAt,
    int CaseFileVersion,
    string DeterministicSummary,
    AiSynthesis Ai,
    IReadOnlyList<TrailEntry> Trail,
    IReadOnlyList<Crumb> Crumbs,
    IReadOnlyList<CrumbSourceStatus> CrumbSources,
    IReadOnlyList<SourceLink> Links,
    IReadOnlyList<CausalMarker>? CausalMarkers,
    PatternContext? Pattern)
{
    public CaseOrigin Origin { get; init; } = new(
        CaseOriginKind.PagerDuty,
        PagerDutyIncidentId);

    public long InputVersion { get; init; }

    public long ProjectedInputVersion { get; init; }

    public string? CreatedBy { get; init; }
}

public sealed record CasePending(
    Guid CaseId,
    string? PagerDutyIncidentId,
    string ServiceId,
    string RecipeId,
    string Title,
    string Urgency,
    PagerDutyIncidentState PagerDutyState,
    string Status,
    DateTimeOffset OpenedAt,
    DateTimeOffset UpdatedAt,
    int CaseFileVersion)
{
    public CaseOrigin Origin { get; init; } = new(
        CaseOriginKind.PagerDuty,
        PagerDutyIncidentId);

    public long InputVersion { get; init; }

    public long ProjectedInputVersion { get; init; }

    public string? CreatedBy { get; init; }
}

public sealed record CaseStatus(
    Guid CaseId,
    string Status,
    PagerDutyIncidentState PagerDutyState,
    int CaseFileVersion,
    DateTimeOffset UpdatedAt,
    bool IsFrozen)
{
    public long InputVersion { get; init; }

    public long ProjectedInputVersion { get; init; }

    public CaseProgressProjection? Progress { get; init; }
}

public sealed record Page<T>(int Total, IReadOnlyList<T> Items);

public sealed record CaseUpdated(
    Guid CaseId,
    int CaseFileVersion,
    IReadOnlyList<string> ChangedSections,
    long InputVersion = 0,
    long ProjectedInputVersion = 0,
    string? Status = null);

public sealed record CaseStatusChanged(
    Guid CaseId,
    string Status,
    int CaseFileVersion,
    long InputVersion = 0,
    long ProjectedInputVersion = 0);

public sealed record DemoAvailability(
    bool Enabled,
    Guid CaseId,
    string CaseUrl);

public sealed record DemoReset(
    Guid CaseId,
    string CaseUrl,
    int CaseFileVersion);

public sealed record RecentPagerDutyIncident(
    string Id,
    int IncidentNumber,
    string Title,
    string Status,
    string Urgency,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastStatusChangeAt,
    string ServiceId,
    string ServiceName,
    IReadOnlyList<string> Assignees,
    string? HtmlUrl);

public sealed record RecentPagerDutyIncidents(
    DateTimeOffset Since,
    DateTimeOffset Until,
    bool HasMore,
    IReadOnlyList<RecentPagerDutyIncident> Incidents);

public sealed record OperationsCatalog(
    IReadOnlyList<TeamCatalogItem> Teams);

public sealed record TeamCatalogItem(
    string Id,
    IReadOnlyList<ServiceCollectionCatalogItem> ServiceCollections);

public sealed record ServiceCollectionCatalogItem(
    string Id,
    IReadOnlyList<ObservedServiceCatalogItem> Services);

public sealed record ObservedServiceCatalogItem(
    string RecipeId,
    string PagerDutyServiceId);

public sealed record CaseTriggerResult(
    Guid CaseId,
    string CaseUrl,
    bool Duplicate);
