using ApiContract = IncidentBot.Contracts;
using Domain = IncidentBot.Api.Domain;

namespace IncidentBot.Api.Contracts;

public static class IncidentContractMapper
{
    public static ApiContract.IncidentPending ToPending(this Incidents.IncidentReportState state) => new(
        state.Id,
        state.PagerDutyIncidentId,
        state.ServiceId,
        state.ProfileId,
        state.Title,
        state.Urgency,
        ToContract(state.State),
        state.Status,
        state.TriggeredAt,
        state.UpdatedAt,
        state.Version);

    public static ApiContract.IncidentStatus ToStatus(this Incidents.IncidentReportState state) => new(
        state.Id,
        state.Status,
        ToContract(state.State),
        state.Version,
        state.UpdatedAt,
        state.IsFrozen);

    public static ApiContract.InvestigationReport ToContract(this Domain.InvestigationReport report) => new(
        report.Id,
        report.PagerDutyIncidentId,
        report.ServiceId,
        report.ProfileId,
        report.ProfileRevision,
        report.Title,
        report.Urgency,
        ToContract(report.State),
        report.Status,
        report.TriggeredAt,
        report.UpdatedAt,
        report.Version,
        report.DeterministicSummary,
        ToContract(report.Ai),
        report.Timeline.Select(ToContract).ToArray(),
        report.Evidence.Select(ToContract).ToArray(),
        report.Sources.Select(ToContract).ToArray(),
        report.Links.Select(ToContract).ToArray(),
        report.CausalEvents?.Select(ToContract).ToArray(),
        report.Problem is null ? null : ToContract(report.Problem));

    public static ApiContract.TimelineEvent ToContract(this Domain.TimelineCandidate item) => new(
        item.OccurredAt,
        item.Source,
        item.Kind,
        item.Summary,
        item.Severity,
        item.Url,
        item.Actor,
        item.ObjectType,
        item.ObjectId);

    public static ApiContract.EvidenceFinding ToContract(this Domain.EvidenceFinding item) => new(
        item.Id,
        item.Source,
        item.OccurredAt,
        item.EndedAt,
        item.Category,
        item.Severity,
        item.Summary,
        item.Excerpt,
        item.Url,
        item.Confidence,
        item.Provenance,
        item.Actor,
        item.ObjectType,
        item.ObjectId,
        item.CodeReferences?.Select(ToContract).ToArray());

    private static ApiContract.CausalEvent ToContract(Domain.CausalEvent item) => new(
        item.Id,
        item.Category,
        item.Label,
        item.OccurredAt,
        item.Summary,
        item.Source,
        item.EvidenceId,
        item.Actor,
        item.Url,
        item.ObjectType,
        item.ObjectId,
        item.CodeReferences.Select(ToContract).ToArray());

    private static ApiContract.CodeReference ToContract(Domain.CodeReference item) => new(
        item.Id,
        item.ProjectId,
        item.CommitSha,
        item.Path,
        item.StartLine,
        item.EndLine,
        item.Url,
        item.Excerpt);

    private static ApiContract.SourceReport ToContract(Domain.SourceReport item) => new(
        item.Source,
        ToContract(item.Health),
        item.FindingCount,
        item.DurationMilliseconds,
        item.Diagnostic,
        item.Links.Select(ToContract).ToArray());

    private static ApiContract.SourceLink ToContract(Domain.SourceLink item) => new(item.Label, item.Url);

    private static ApiContract.AiSynthesis ToContract(Domain.AiSynthesis item) => new(
        item.Status,
        item.Summary,
        item.PossibleContributors,
        item.Unknowns,
        item.RecommendedChecks,
        item.EvidenceHash,
        item.Diagnoses?.Select(diagnosis => new ApiContract.AiDiagnosis(
            diagnosis.Summary,
            diagnosis.EvidenceIds,
            diagnosis.CodeReferences.Select(ToContract).ToArray(),
            diagnosis.Rank,
            diagnosis.EvidenceStrength)).ToArray(),
        item.SummaryParts?.Select(part => new ApiContract.AiSummaryPart(part.Text, part.ReferenceId)).ToArray(),
        item.SummaryReferences?.Select(reference => new ApiContract.AiSummaryReference(
            reference.Id,
            reference.Label,
            reference.Kind,
            reference.Href)).ToArray());

    private static ApiContract.ProblemContext ToContract(Domain.ProblemContext item) => new(
        item.Availability,
        item.AlgorithmVersion,
        ToContract(item.Stage),
        item.ProblemKey,
        item.ProblemGroupId,
        item.LifecycleState is null ? null : ToContract(item.LifecycleState.Value),
        item.MatchType,
        item.MatchScore,
        item.MatchedFeatures,
        item.OccurrenceCount,
        item.FirstSeen,
        item.LastSeen,
        item.RecentOccurrences.Select(occurrence => new ApiContract.ProblemOccurrence(
            occurrence.IncidentId,
            occurrence.PagerDutyIncidentId,
            ToContract(occurrence.State),
            occurrence.OccurredAt,
            occurrence.UpdatedAt,
            occurrence.ReportUrl)).ToArray(),
        item.PossibleMatches.Select(match => new ApiContract.PossibleProblemMatch(
            match.ProblemKey,
            match.MatchType,
            match.Score,
            match.MatchedFeatures,
            match.LastSeen)).ToArray(),
        item.Completeness,
        item.Diagnostic);

    private static ApiContract.IncidentState ToContract(Domain.IncidentState value) => value switch
    {
        Domain.IncidentState.Triggered => ApiContract.IncidentState.Triggered,
        Domain.IncidentState.Acknowledged => ApiContract.IncidentState.Acknowledged,
        Domain.IncidentState.Escalated => ApiContract.IncidentState.Escalated,
        Domain.IncidentState.Reassigned => ApiContract.IncidentState.Reassigned,
        Domain.IncidentState.Resolved => ApiContract.IncidentState.Resolved,
        _ => ApiContract.IncidentState.Unknown
    };

    private static ApiContract.SourceHealth ToContract(Domain.SourceHealth value) => value switch
    {
        Domain.SourceHealth.Pending => ApiContract.SourceHealth.Pending,
        Domain.SourceHealth.Complete => ApiContract.SourceHealth.Complete,
        Domain.SourceHealth.Partial => ApiContract.SourceHealth.Partial,
        Domain.SourceHealth.Unavailable => ApiContract.SourceHealth.Unavailable,
        Domain.SourceHealth.Excluded => ApiContract.SourceHealth.Excluded,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };

    private static ApiContract.FingerprintStage ToContract(Domain.FingerprintStage value) => value switch
    {
        Domain.FingerprintStage.Provisional => ApiContract.FingerprintStage.Provisional,
        Domain.FingerprintStage.Final => ApiContract.FingerprintStage.Final,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };

    private static ApiContract.ProblemLifecycleState ToContract(Domain.ProblemLifecycleState value) => value switch
    {
        Domain.ProblemLifecycleState.New => ApiContract.ProblemLifecycleState.New,
        Domain.ProblemLifecycleState.Ongoing => ApiContract.ProblemLifecycleState.Ongoing,
        Domain.ProblemLifecycleState.Resolved => ApiContract.ProblemLifecycleState.Resolved,
        Domain.ProblemLifecycleState.Regressed => ApiContract.ProblemLifecycleState.Regressed,
        Domain.ProblemLifecycleState.Escalating => ApiContract.ProblemLifecycleState.Escalating,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };
}
