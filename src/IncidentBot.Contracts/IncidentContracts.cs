using System.Text.Json.Nodes;

namespace IncidentBot.Contracts;

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

public sealed record TimelineEvent(
    DateTimeOffset OccurredAt,
    string Source,
    string Kind,
    string Summary,
    string Severity,
    string? Url,
    string? Actor,
    string? ObjectType,
    string? ObjectId);

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
    string? Actor,
    string? ObjectType,
    string? ObjectId,
    IReadOnlyList<CodeReference>? CodeReferences);

public sealed record SourceReport(
    string Source,
    SourceHealth Health,
    int FindingCount,
    long DurationMilliseconds,
    string? Diagnostic,
    IReadOnlyList<SourceLink> Links);

public sealed record AiDiagnosis(
    string Summary,
    IReadOnlyList<string> EvidenceIds,
    IReadOnlyList<CodeReference> CodeReferences,
    int Rank,
    int EvidenceStrength);

public sealed record AiSynthesis(
    string Status,
    string? Summary,
    IReadOnlyList<string> PossibleContributors,
    IReadOnlyList<string> Unknowns,
    IReadOnlyList<string> RecommendedChecks,
    string? EvidenceHash,
    IReadOnlyList<AiDiagnosis>? Diagnoses,
    IReadOnlyList<AiSummaryPart>? SummaryParts,
    IReadOnlyList<AiSummaryReference>? SummaryReferences);

public sealed record ProblemOccurrence(
    Guid IncidentId,
    string PagerDutyIncidentId,
    IncidentState State,
    DateTimeOffset OccurredAt,
    DateTimeOffset UpdatedAt,
    string? ReportUrl);

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
    IReadOnlyList<ProblemOccurrence> RecentOccurrences,
    IReadOnlyList<PossibleProblemMatch> PossibleMatches,
    double Completeness,
    string? Diagnostic);

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
    IReadOnlyList<TimelineEvent> Timeline,
    IReadOnlyList<EvidenceFinding> Evidence,
    IReadOnlyList<SourceReport> Sources,
    IReadOnlyList<SourceLink> Links,
    IReadOnlyList<CausalEvent>? CausalEvents,
    ProblemContext? Problem);

public sealed record IncidentPending(
    Guid Id,
    string PagerDutyIncidentId,
    string ServiceId,
    string ProfileId,
    string Title,
    string Urgency,
    IncidentState State,
    string Status,
    DateTimeOffset TriggeredAt,
    DateTimeOffset UpdatedAt,
    int Version);

public sealed record IncidentStatus(
    Guid Id,
    string Status,
    IncidentState State,
    int Version,
    DateTimeOffset UpdatedAt,
    bool IsFrozen);

public sealed record Page<T>(int Total, IReadOnlyList<T> Items);

public sealed record IncidentUpdated(
    Guid IncidentId,
    int Version,
    IReadOnlyList<string> ChangedSections);

public sealed record IncidentStatusChanged(Guid IncidentId, string Status, int Version);

public sealed record DemoAvailability(bool Enabled, Guid IncidentId, string IncidentUrl);

public sealed record DemoReset(Guid IncidentId, string IncidentUrl, int Version);
