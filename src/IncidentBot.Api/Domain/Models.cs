using System.Text.Json.Nodes;

namespace IncidentBot.Api.Domain;

public enum IncidentState
{
    Triggered,
    Acknowledged,
    Escalated,
    Reassigned,
    Resolved,
    Unknown
}

public enum SourceHealth
{
    Pending,
    Complete,
    Partial,
    Unavailable,
    Excluded
}

public enum SourceRequestState
{
    Requested,
    Received,
    Errored
}

public sealed record InvestigationContext(
    Guid IncidentId,
    string PagerDutyIncidentId,
    string ServiceId,
    string Title,
    string Urgency,
    IncidentState State,
    DateTimeOffset TriggeredAt,
    IReadOnlyDictionary<string, string> Labels,
    InvestigationProfile Profile);

public sealed record EvidenceScope(
    DateTimeOffset Start,
    DateTimeOffset End,
    string ProfileRevision,
    int MaxItems,
    int MaxBytes);

public sealed record EvidenceFinding(
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

public sealed record TimelineCandidate(
    DateTimeOffset OccurredAt,
    string Source,
    string Kind,
    string Summary,
    string Severity,
    string? Url,
    string? Actor = null,
    string? ObjectType = null,
    string? ObjectId = null);

public sealed record CodeReference(
    string Id,
    string ProjectId,
    string CommitSha,
    string Path,
    int StartLine,
    int EndLine,
    string Url,
    string Excerpt);

public sealed record CausalEvent(
    string Id,
    string Category,
    string? Label,
    DateTimeOffset OccurredAt,
    string Summary,
    string Source,
    string EvidenceId,
    string? Actor,
    string? Url,
    string? ObjectType,
    string? ObjectId,
    IReadOnlyList<CodeReference> CodeReferences);

public sealed record AiDiagnosis(
    string Summary,
    IReadOnlyList<string> EvidenceIds,
    IReadOnlyList<CodeReference> CodeReferences,
    int Rank = 0,
    int EvidenceStrength = 0);

public sealed record AiSummaryPart(
    string Text,
    string? ReferenceId = null);

public sealed record AiSummaryReference(
    string Id,
    string Label,
    string Kind,
    string Href);

public sealed record SourceLink(string Label, string Url);

public enum FingerprintStage
{
    Provisional,
    Final
}

public enum ProblemLifecycleState
{
    New,
    Ongoing,
    Resolved,
    Regressed,
    Escalating
}

public sealed record FingerprintFeatures(
    string ServiceId,
    string ProfileId,
    IReadOnlyList<string> Scopes,
    string NormalizedTitle,
    IReadOnlyList<string> TitleTokens,
    IReadOnlyList<string> SymptomCategories,
    IReadOnlyList<string> ErrorTemplates,
    IReadOnlyList<string> Components,
    IReadOnlyList<string> CodeLocations);

public sealed record IncidentFingerprint(
    string AlgorithmVersion,
    FingerprintStage Stage,
    string FamilyHash,
    string ExactHash,
    FingerprintFeatures Features,
    double Completeness);

public sealed record ProblemOccurrenceSummary(
    Guid IncidentId,
    string PagerDutyIncidentId,
    IncidentState State,
    DateTimeOffset OccurredAt,
    DateTimeOffset UpdatedAt,
    string? ReportUrl = null);

public sealed record ProblemMatch(
    Guid ProblemGroupId,
    string ProblemKey,
    string MatchType,
    int Score,
    IReadOnlyList<string> MatchedFeatures,
    ProblemLifecycleState LifecycleState,
    int OccurrenceCount,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    IReadOnlyList<ProblemOccurrenceSummary> RecentOccurrences);

public sealed record PossibleProblemMatch(
    string ProblemKey,
    string MatchType,
    int Score,
    IReadOnlyList<string> MatchedFeatures,
    DateTimeOffset LastSeen);

public sealed record ProblemContext(
    string Availability,
    string AlgorithmVersion,
    FingerprintStage Stage,
    string? ProblemKey,
    Guid? ProblemGroupId,
    ProblemLifecycleState? LifecycleState,
    string? MatchType,
    int? MatchScore,
    IReadOnlyList<string> MatchedFeatures,
    int OccurrenceCount,
    DateTimeOffset? FirstSeen,
    DateTimeOffset? LastSeen,
    IReadOnlyList<ProblemOccurrenceSummary> RecentOccurrences,
    IReadOnlyList<PossibleProblemMatch> PossibleMatches,
    double Completeness,
    string? Diagnostic = null);

public sealed record ConnectorResult(
    string Source,
    SourceHealth Health,
    IReadOnlyList<EvidenceFinding> Findings,
    IReadOnlyList<TimelineCandidate> Timeline,
    IReadOnlyList<SourceLink> Links,
    long DurationMilliseconds,
    string? Diagnostic)
{
    public static ConnectorResult Excluded(string source) =>
        new(source, SourceHealth.Excluded, [], [], [], 0, null);

    public static ConnectorResult Unavailable(string source, long durationMilliseconds, string diagnostic) =>
        new(source, SourceHealth.Unavailable, [], [], [], durationMilliseconds, diagnostic);
}

public sealed record SourceReport(
    string Source,
    SourceHealth Health,
    int FindingCount,
    long DurationMilliseconds,
    string? Diagnostic,
    IReadOnlyList<SourceLink> Links,
    // Null only when deserializing a report persisted before request-state tracking.
    SourceRequestState? RequestState);

public sealed record AiSynthesis(
    string Status,
    string? Summary,
    IReadOnlyList<string> PossibleContributors,
    IReadOnlyList<string> Unknowns,
    IReadOnlyList<string> RecommendedChecks,
    string? EvidenceHash,
    IReadOnlyList<AiDiagnosis>? Diagnoses = null,
    IReadOnlyList<AiSummaryPart>? SummaryParts = null,
    IReadOnlyList<AiSummaryReference>? SummaryReferences = null);

public sealed record InvestigationReport(
    Guid Id,
    string PagerDutyIncidentId,
    string ServiceId,
    string ProfileId,
    string ProfileRevision,
    string Title,
    string Urgency,
    IncidentState State,
    string Status,
    DateTimeOffset TriggeredAt,
    DateTimeOffset UpdatedAt,
    int Version,
    string DeterministicSummary,
    AiSynthesis Ai,
    IReadOnlyList<TimelineCandidate> Timeline,
    IReadOnlyList<EvidenceFinding> Evidence,
    IReadOnlyList<SourceReport> Sources,
    IReadOnlyList<SourceLink> Links,
    IReadOnlyList<CausalEvent>? CausalEvents = null,
    ProblemContext? Problem = null);

public sealed record IncidentRecord(
    Guid Id,
    string PagerDutyIncidentId,
    string ServiceId,
    string ProfileId,
    string Title,
    string Urgency,
    IncidentState State,
    DateTimeOffset TriggeredAt,
    DateTimeOffset UpdatedAt,
    int Version,
    string Status,
    bool IsFrozen,
    string? ReportJson,
    string SlackChannel,
    string? SlackTimestamp,
    IReadOnlyDictionary<string, string> Labels);

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

public sealed record WorkItem(long Id, Guid IncidentId, string Kind, int Attempts);
public sealed record OutboxItem(long Id, string Kind, string Payload, int Attempts);
