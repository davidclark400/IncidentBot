using ApiContract = Panko.Contracts;
using Domain = Panko.Api.Domain;

namespace Panko.Api.Contracts;

public static class CaseContractMapper
{
    public static ApiContract.CasePending ToPending(this CaseFileState state) => new(
        state.CaseId,
        state.PagerDutyIncidentId,
        state.ServiceId,
        state.RecipeId,
        state.Title,
        state.Urgency,
        ToContract(state.PagerDutyState),
        state.Status,
        state.OpenedAt,
        state.UpdatedAt,
        state.CaseFileVersion)
    {
        Origin = ToContract(state.Origin),
        InputVersion = state.InputVersion,
        ProjectedInputVersion = state.ProjectedInputVersion,
        CreatedBy = state.CreatedBy
    };

    public static ApiContract.CaseStatus ToStatus(
        this CaseFileState state,
        Domain.CaseProgress? progress = null) => new(
        state.CaseId,
        state.Status,
        ToContract(state.PagerDutyState),
        state.CaseFileVersion,
        state.UpdatedAt,
        state.IsFrozen)
        {
            InputVersion = state.InputVersion,
            ProjectedInputVersion = state.ProjectedInputVersion,
            Progress = progress?.ToContract()
        };

    public static ApiContract.CaseFile ToContract(this Domain.CaseFile caseFile) => new(
        caseFile.CaseId,
        caseFile.PagerDutyIncidentId,
        caseFile.ServiceId,
        caseFile.RecipeId,
        caseFile.RecipeRevision,
        caseFile.Title,
        caseFile.Urgency,
        ToContract(caseFile.PagerDutyState),
        caseFile.Status,
        caseFile.OpenedAt,
        caseFile.UpdatedAt,
        caseFile.CaseFileVersion,
        caseFile.DeterministicSummary,
        ToContract(caseFile.Ai),
        caseFile.Trail.Select(ToContract).ToArray(),
        caseFile.Crumbs.Select(ToContract).ToArray(),
        caseFile.CrumbSources.Select(ToContract).ToArray(),
        caseFile.Links.Select(ToContract).ToArray(),
        caseFile.CausalMarkers?.Select(ToContract).ToArray(),
        caseFile.Pattern is null ? null : ToContract(caseFile.Pattern))
    {
        Origin = ToContract(caseFile.Origin),
        InputVersion = caseFile.InputVersion,
        ProjectedInputVersion = caseFile.ProjectedInputVersion,
        CreatedBy = caseFile.CreatedBy
    };

    public static ApiContract.TrailEntry ToContract(this Domain.TrailCandidate item) => new(
        item.StableId,
        item.OccurredAt,
        item.Source,
        item.Kind,
        item.Summary,
        item.Severity,
        item.Url,
        item.Actor,
        item.ObjectType,
        item.ObjectId);

    private static ApiContract.CaseOrigin ToContract(Domain.CaseOrigin origin) => new(
        origin.Kind switch
        {
            Domain.CaseOriginKind.Agent => ApiContract.CaseOriginKind.Agent,
            Domain.CaseOriginKind.Manual => ApiContract.CaseOriginKind.Manual,
            _ => ApiContract.CaseOriginKind.PagerDuty
        },
        origin.ExternalId);

    public static ApiContract.Crumb ToContract(this Domain.Crumb item) => new(
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

    private static ApiContract.CausalMarker ToContract(Domain.CausalMarker item) => new(
        item.Id,
        item.Category,
        item.Label,
        item.OccurredAt,
        item.Summary,
        item.Source,
        item.CrumbId,
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

    private static ApiContract.CrumbSourceStatus ToContract(Domain.CrumbSourceStatus item) => new(
        item.Source,
        ToContract(item.Health),
        item.CrumbCount,
        item.DurationMilliseconds,
        item.Diagnostic,
        item.Links.Select(ToContract).ToArray(),
        ToContract(item.RequestState ?? RequestStateFor(item.Health)));

    public static ApiContract.CaseProgressProjection ToContract(
        this Domain.CaseProgress progress) => new(
        progress.CaseId,
        progress.AttemptId,
        progress.Revision,
        progress.BaseCaseFileVersion,
        progress.StartedAt,
        progress.UpdatedAt,
        progress.ElapsedDurationMilliseconds,
        ToContract(progress.Phase),
        progress.CurrentPass,
        progress.CurrentLookbackMinutes,
        progress.DeterministicCaseFileUsable,
        progress.OnlyAiSynthesisRemaining,
        ToContract(progress.AiSynthesisState),
        progress.CrumbSources.Select(ToContract).ToArray(),
        progress.EarlyCrumbs.Select(ToContract).ToArray());

    private static ApiContract.CrumbSourceProgress ToContract(
        Domain.CaseSourceProgress source) => new(
        source.Source,
        ToContract(source.RequestState),
        ToContract(source.Health),
        source.Pass,
        source.LookbackMinutes,
        source.DurationMilliseconds,
        source.CrumbCount,
        source.Diagnostic,
        source.StartedAt,
        source.UpdatedAt);

    private static ApiContract.CaseEarlyCrumb ToContract(
        Domain.CaseEarlyCrumb crumb) => new(
        crumb.Id,
        crumb.Source,
        crumb.OccurredAt,
        crumb.Severity,
        crumb.Summary,
        crumb.Confidence);

    private static ApiContract.SourceLink ToContract(Domain.SourceLink item) => new(item.Label, item.Url);

    private static ApiContract.AiSynthesis ToContract(Domain.AiSynthesis item) => new(
        item.Status,
        item.Summary,
        item.PossibleContributors,
        item.Unknowns,
        item.RecommendedChecks,
        item.CrumbHash,
        item.Diagnoses?.Select(diagnosis => new ApiContract.AiDiagnosis(
            diagnosis.Summary,
            diagnosis.CrumbIds,
            diagnosis.CodeReferences.Select(ToContract).ToArray(),
            diagnosis.Rank,
            diagnosis.CrumbStrength)).ToArray(),
        item.SummaryParts?.Select(part => new ApiContract.AiSummaryPart(part.Text, part.ReferenceId)).ToArray(),
        item.SummaryReferences?.Select(reference => new ApiContract.AiSummaryReference(
            reference.Id,
            reference.Label,
            reference.Kind,
            reference.Href)).ToArray());

    private static ApiContract.PatternContext ToContract(Domain.PatternContext item) => new(
        item.Availability,
        item.AlgorithmVersion,
        ToContract(item.Stage),
        item.PatternKey,
        item.PatternId,
        item.LifecycleState is null ? null : ToContract(item.LifecycleState.Value),
        item.MatchType,
        item.MatchScore,
        item.MatchedFeatures,
        item.OccurrenceCount,
        item.FirstSeen,
        item.LastSeen,
        item.RecentOccurrences.Select(occurrence => new ApiContract.PatternOccurrence(
            occurrence.CaseId,
            occurrence.PagerDutyIncidentId,
            ToContract(occurrence.PagerDutyState),
            occurrence.OccurredAt,
            occurrence.UpdatedAt,
            occurrence.CaseUrl)).ToArray(),
        item.PossibleMatches.Select(match => new ApiContract.PossiblePatternMatch(
            match.PatternKey,
            match.MatchType,
            match.Score,
            match.MatchedFeatures,
            match.LastSeen)).ToArray(),
        item.Completeness,
        item.Diagnostic);

    private static ApiContract.PagerDutyIncidentState ToContract(Domain.PagerDutyIncidentState value) => value switch
    {
        Domain.PagerDutyIncidentState.Triggered => ApiContract.PagerDutyIncidentState.Triggered,
        Domain.PagerDutyIncidentState.Acknowledged => ApiContract.PagerDutyIncidentState.Acknowledged,
        Domain.PagerDutyIncidentState.Escalated => ApiContract.PagerDutyIncidentState.Escalated,
        Domain.PagerDutyIncidentState.Reassigned => ApiContract.PagerDutyIncidentState.Reassigned,
        Domain.PagerDutyIncidentState.Resolved => ApiContract.PagerDutyIncidentState.Resolved,
        _ => ApiContract.PagerDutyIncidentState.Unknown
    };

    private static ApiContract.CrumbSourceHealth ToContract(Domain.CrumbSourceHealth value) => value switch
    {
        Domain.CrumbSourceHealth.Pending => ApiContract.CrumbSourceHealth.Pending,
        Domain.CrumbSourceHealth.Complete => ApiContract.CrumbSourceHealth.Complete,
        Domain.CrumbSourceHealth.Partial => ApiContract.CrumbSourceHealth.Partial,
        Domain.CrumbSourceHealth.Unavailable => ApiContract.CrumbSourceHealth.Unavailable,
        Domain.CrumbSourceHealth.Excluded => ApiContract.CrumbSourceHealth.Excluded,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };

    private static ApiContract.CrumbSourceRequestState ToContract(Domain.CrumbSourceRequestState value) => value switch
    {
        Domain.CrumbSourceRequestState.Received => ApiContract.CrumbSourceRequestState.Received,
        Domain.CrumbSourceRequestState.Requested => ApiContract.CrumbSourceRequestState.Requested,
        Domain.CrumbSourceRequestState.Errored => ApiContract.CrumbSourceRequestState.Errored,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };

    private static ApiContract.CaseProgressPhase ToContract(
        Domain.CaseProgressPhase value) => value switch
        {
            Domain.CaseProgressPhase.Collecting => ApiContract.CaseProgressPhase.Collecting,
            Domain.CaseProgressPhase.Synthesizing => ApiContract.CaseProgressPhase.Synthesizing,
            Domain.CaseProgressPhase.Finalizing => ApiContract.CaseProgressPhase.Finalizing,
            Domain.CaseProgressPhase.Completed => ApiContract.CaseProgressPhase.Completed,
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
        };

    private static ApiContract.CrumbSourceProgressState ToContract(Domain.CrumbSourceProgressState value) => value switch
    {
        Domain.CrumbSourceProgressState.Pending => ApiContract.CrumbSourceProgressState.Pending,
        Domain.CrumbSourceProgressState.Querying => ApiContract.CrumbSourceProgressState.Querying,
        Domain.CrumbSourceProgressState.Received => ApiContract.CrumbSourceProgressState.Received,
        Domain.CrumbSourceProgressState.TimedOut => ApiContract.CrumbSourceProgressState.TimedOut,
        Domain.CrumbSourceProgressState.Failed => ApiContract.CrumbSourceProgressState.Failed,
        Domain.CrumbSourceProgressState.Excluded => ApiContract.CrumbSourceProgressState.Excluded,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };

    private static ApiContract.AiSynthesisProgressState ToContract(
        Domain.AiSynthesisProgressState value) => value switch
        {
            Domain.AiSynthesisProgressState.Pending => ApiContract.AiSynthesisProgressState.Pending,
            Domain.AiSynthesisProgressState.Running => ApiContract.AiSynthesisProgressState.Running,
            Domain.AiSynthesisProgressState.Complete => ApiContract.AiSynthesisProgressState.Complete,
            Domain.AiSynthesisProgressState.Unavailable => ApiContract.AiSynthesisProgressState.Unavailable,
            Domain.AiSynthesisProgressState.Skipped => ApiContract.AiSynthesisProgressState.Skipped,
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
        };

    private static Domain.CrumbSourceRequestState RequestStateFor(Domain.CrumbSourceHealth health) => health switch
    {
        Domain.CrumbSourceHealth.Pending => Domain.CrumbSourceRequestState.Requested,
        Domain.CrumbSourceHealth.Unavailable => Domain.CrumbSourceRequestState.Errored,
        _ => Domain.CrumbSourceRequestState.Received
    };

    private static ApiContract.SignatureStage ToContract(Domain.SignatureStage value) => value switch
    {
        Domain.SignatureStage.Provisional => ApiContract.SignatureStage.Provisional,
        Domain.SignatureStage.Final => ApiContract.SignatureStage.Final,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };

    private static ApiContract.PatternLifecycleState ToContract(Domain.PatternLifecycleState value) => value switch
    {
        Domain.PatternLifecycleState.New => ApiContract.PatternLifecycleState.New,
        Domain.PatternLifecycleState.Ongoing => ApiContract.PatternLifecycleState.Ongoing,
        Domain.PatternLifecycleState.Resolved => ApiContract.PatternLifecycleState.Resolved,
        Domain.PatternLifecycleState.Regressed => ApiContract.PatternLifecycleState.Regressed,
        Domain.PatternLifecycleState.Escalating => ApiContract.PatternLifecycleState.Escalating,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };
}
