using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Panko.Api.Domain;

public enum PagerDutyIncidentState
{
    Triggered,
    Acknowledged,
    Escalated,
    Reassigned,
    Resolved,
    Unknown
}

public enum CaseOriginKind
{
    PagerDuty,
    Agent,
    Manual
}

public sealed record CaseOrigin(
    CaseOriginKind Kind,
    string? ExternalId);

public enum CrumbSourceHealth
{
    Pending,
    Complete,
    Partial,
    Unavailable,
    Excluded
}

public enum CrumbSourceRequestState
{
    Requested,
    Received,
    Errored
}

public sealed record CaseContext(
    Guid CaseId,
    string? PagerDutyIncidentId,
    string ServiceId,
    string Title,
    string Urgency,
    PagerDutyIncidentState PagerDutyState,
    DateTimeOffset OpenedAt,
    IReadOnlyDictionary<string, string> Labels,
    Recipe Recipe)
{
    public DateTimeOffset? AcknowledgedAt { get; init; }

    public DateTimeOffset? ResolvedAt { get; init; }
}

public sealed record CaseSubject(
    string ServiceId,
    string Title,
    string Urgency,
    PagerDutyIncidentState PagerDutyState,
    DateTimeOffset OpenedAt)
{
    public static CaseSubject FromCase(CaseRecord @case) => new(
        @case.ServiceId,
        @case.Title,
        @case.Urgency,
        @case.PagerDutyState,
        @case.OpenedAt);
}

public sealed record CrumbScope(
    DateTimeOffset Start,
    DateTimeOffset End,
    string RecipeRevision,
    int MaxItems,
    int MaxBytes);

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
    string? Actor = null,
    string? ObjectType = null,
    string? ObjectId = null,
    IReadOnlyList<CodeReference>? CodeReferences = null);

[method: JsonConstructor]
public sealed record TrailCandidate(
    string Id,
    DateTimeOffset OccurredAt,
    string Source,
    string Kind,
    string Summary,
    string Severity,
    string? Url,
    string? Actor = null,
    string? ObjectType = null,
    string? ObjectId = null)
{
    /// <summary>
    /// Convenience constructor that derives the stable Trail identity from its content.
    /// </summary>
    public TrailCandidate(
        DateTimeOffset OccurredAt,
        string Source,
        string Kind,
        string Summary,
        string Severity,
        string? Url,
        string? Actor = null,
        string? ObjectType = null,
        string? ObjectId = null)
        : this(
            TrailCandidateIdentity.Create(
                OccurredAt, Source, Kind, Summary, Url, Actor, ObjectType, ObjectId),
            OccurredAt,
            Source,
            Kind,
            Summary,
            Severity,
            Url,
            Actor,
            ObjectType,
            ObjectId)
    {
    }

    [JsonIgnore]
    public string StableId => string.IsNullOrWhiteSpace(Id)
        ? TrailCandidateIdentity.Create(
            OccurredAt, Source, Kind, Summary, Url, Actor, ObjectType, ObjectId)
        : Id;
}

public static class TrailCandidateIdentity
{
    public static string Create(
        DateTimeOffset occurredAt,
        string source,
        string kind,
        string summary,
        string? url = null,
        string? actor = null,
        string? objectType = null,
        string? objectId = null)
    {
        var naturalIdentity = !string.IsNullOrWhiteSpace(objectType) || !string.IsNullOrWhiteSpace(objectId)
            ? string.Join('\u001f', source, kind, objectType, objectId, occurredAt.UtcTicks)
            : string.Join('\u001f', source, kind, url, actor, occurredAt.UtcTicks, summary);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(naturalIdentity));
        return $"trail-{Convert.ToHexStringLower(digest.AsSpan(0, 16))}";
    }
}

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

public sealed record AiDiagnosis(
    string Summary,
    IReadOnlyList<string> CrumbIds,
    IReadOnlyList<CodeReference> CodeReferences,
    int Rank = 0,
    int CrumbStrength = 0);

public sealed record AiSummaryPart(
    string Text,
    string? ReferenceId = null);

public sealed record AiSummaryReference(
    string Id,
    string Label,
    string Kind,
    string Href);

public sealed record SourceLink(string Label, string Url);

public enum SignatureStage
{
    Provisional,
    Final
}

public enum PatternLifecycleState
{
    New,
    Ongoing,
    Resolved,
    Regressed,
    Escalating
}

public sealed record SignatureFeatures(
    string ServiceId,
    string RecipeId,
    IReadOnlyList<string> Scopes,
    string NormalizedTitle,
    IReadOnlyList<string> TitleTokens,
    IReadOnlyList<string> SymptomCategories,
    IReadOnlyList<string> ErrorTemplates,
    IReadOnlyList<string> Components,
    IReadOnlyList<string> CodeLocations);

public sealed record CaseSignature(
    string AlgorithmVersion,
    SignatureStage Stage,
    string FamilyHash,
    string ExactHash,
    SignatureFeatures Features,
    double Completeness);

public sealed record PatternOccurrenceSummary(
    Guid CaseId,
    string? PagerDutyIncidentId,
    PagerDutyIncidentState PagerDutyState,
    DateTimeOffset OccurredAt,
    DateTimeOffset UpdatedAt,
    string? CaseUrl = null);

public sealed record PatternMatch(
    Guid PatternId,
    string PatternKey,
    string MatchType,
    int Score,
    IReadOnlyList<string> MatchedFeatures,
    PatternLifecycleState LifecycleState,
    int OccurrenceCount,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    IReadOnlyList<PatternOccurrenceSummary> RecentOccurrences);

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
    IReadOnlyList<PatternOccurrenceSummary> RecentOccurrences,
    IReadOnlyList<PossiblePatternMatch> PossibleMatches,
    double Completeness,
    string? Diagnostic = null);

public sealed record CrumbSourceResult(
    string Source,
    CrumbSourceHealth Health,
    IReadOnlyList<Crumb> Crumbs,
    IReadOnlyList<TrailCandidate> Trail,
    IReadOnlyList<SourceLink> Links,
    long DurationMilliseconds,
    string? Diagnostic)
{
    public static CrumbSourceResult Excluded(string source) =>
        new(source, CrumbSourceHealth.Excluded, [], [], [], 0, null);

    public static CrumbSourceResult Unavailable(string source, long durationMilliseconds, string diagnostic) =>
        new(source, CrumbSourceHealth.Unavailable, [], [], [], durationMilliseconds, diagnostic);
}

public sealed record CrumbSourceStatus(
    string Source,
    CrumbSourceHealth Health,
    int CrumbCount,
    long DurationMilliseconds,
    string? Diagnostic,
    IReadOnlyList<SourceLink> Links,
    // Null only when deserializing a Case File persisted before request-state tracking.
    CrumbSourceRequestState? RequestState);

public enum CaseProgressPhase
{
    Collecting,
    Synthesizing,
    Finalizing,
    Completed
}

public enum CrumbSourceProgressState
{
    Pending,
    Querying,
    Received,
    TimedOut,
    Failed,
    Excluded
}

public enum AiSynthesisProgressState
{
    Pending,
    Running,
    Complete,
    Unavailable,
    Skipped
}

/// <summary>
/// Small, responder-facing metadata for one source. It deliberately excludes Crumb payloads.
/// </summary>
public sealed record CaseSourceProgress(
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

/// <summary>
/// Lightweight mutable projection for live collection progress. The canonical Case File and its
/// Crumbs remain versioned separately and are written only by Case File transitions.
/// </summary>
public sealed record CaseProgress(
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
    IReadOnlyList<CaseSourceProgress> CrumbSources,
    IReadOnlyList<CaseEarlyCrumb> EarlyCrumbs);

public sealed record AiSynthesis(
    string Status,
    string? Summary,
    IReadOnlyList<string> PossibleContributors,
    IReadOnlyList<string> Unknowns,
    IReadOnlyList<string> RecommendedChecks,
    string? CrumbHash,
    IReadOnlyList<AiDiagnosis>? Diagnoses = null,
    IReadOnlyList<AiSummaryPart>? SummaryParts = null,
    IReadOnlyList<AiSummaryReference>? SummaryReferences = null);

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
    IReadOnlyList<TrailCandidate> Trail,
    IReadOnlyList<Crumb> Crumbs,
    IReadOnlyList<CrumbSourceStatus> CrumbSources,
    IReadOnlyList<SourceLink> Links,
    IReadOnlyList<CausalMarker>? CausalMarkers = null,
    PatternContext? Pattern = null)
{
    public CaseOrigin Origin { get; init; } = new(
        CaseOriginKind.PagerDuty,
        PagerDutyIncidentId);

    public long InputVersion { get; init; }

    public long ProjectedInputVersion { get; init; }

    public string? CreatedBy { get; init; }
}

public sealed record CaseRecord(
    Guid Id,
    string? PagerDutyIncidentId,
    string ServiceId,
    string RecipeId,
    string Title,
    string Urgency,
    PagerDutyIncidentState PagerDutyState,
    DateTimeOffset OpenedAt,
    DateTimeOffset UpdatedAt,
    int Version,
    string Status,
    bool IsFrozen,
    string? CaseFileJson,
    string SlackChannel,
    string? SlackTimestamp,
    IReadOnlyDictionary<string, string> Labels)
{
    public string Team { get; init; } = "unmapped";

    public DateTimeOffset? AcknowledgedAt { get; init; }

    public DateTimeOffset? ResolvedAt { get; init; }

    public CaseOrigin Origin { get; init; } = new(
        CaseOriginKind.PagerDuty,
        PagerDutyIncidentId);

    public long InputVersion { get; init; }

    public long ProjectedInputVersion { get; init; }

    public long WorkflowGeneration { get; init; }

    public long ProjectedWorkflowGeneration { get; init; }

    public string? CreatedBy { get; init; }

    public bool PublishToSlack { get; init; } = true;
}

public sealed record PagerDutyWebhookEvent(
    string EventId,
    string EventType,
    string PagerDutyIncidentId,
    string ServiceId,
    string Title,
    string Urgency,
    string? HtmlUrl,
    DateTimeOffset TriggeredAt,
    DateTimeOffset OccurredAt,
    IReadOnlyDictionary<string, string> Labels);

public sealed record WorkItem(
    long Id,
    Guid CaseId,
    string Kind,
    int Attempts,
    long? TargetInputVersion = null,
    long? TargetWorkflowGeneration = null);
public sealed record OutboxItem(long Id, string Kind, string Payload, int Attempts);
