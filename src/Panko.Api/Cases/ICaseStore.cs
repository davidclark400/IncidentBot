using Panko.Api.Domain;
using Panko.Api.Cases;

namespace Panko.Api.Cases;

public sealed record CaseProjectionInputs(
    IReadOnlyList<CaseInput> ActiveInputs,
    IReadOnlyList<CrumbSourceResult> RetainedCrumbSourceResults);

/// <summary>
/// Domain-facing persistence surface for a Case and its durable Case state.
/// PostgreSQL details remain inside the infrastructure adapter.
/// </summary>
public interface ICaseStore
{
    Task<(Guid CaseId, bool IsDuplicate)> AcceptOriginEventAsync(
        AcceptCaseOriginEvent originEvent,
        Recipe recipe,
        CaseOriginEventReceipt receipt,
        CancellationToken cancellationToken);

    Task<CaseRecord?> GetCaseAsync(Guid caseId, CancellationToken cancellationToken);

    Task<CaseFile?> GetCaseFileAsync(Guid caseId, CancellationToken cancellationToken);

    Task<CaseProjectionInputs> GetProjectionInputsAsync(
        Guid caseId,
        long targetInputVersion,
        CancellationToken cancellationToken) =>
        Task.FromResult(new CaseProjectionInputs([], []));

    Task<CaseProgress?> GetProgressAsync(
        Guid caseId,
        CancellationToken cancellationToken);

    Task<long?> BeginProgressAsync(
        CaseProgress progress,
        CancellationToken cancellationToken);

    Task<long?> UpdateProgressAsync(
        CaseProgress progress,
        CancellationToken cancellationToken);

    Task<int> SaveCaseFileAsync(
        CaseRecord caseRecord,
        CaseFile caseFile,
        CancellationToken cancellationToken);

    Task<int> SaveCaseFileAsync(
        CaseRecord caseRecord,
        CaseFile caseFile,
        Guid? progressAttemptId,
        CancellationToken cancellationToken)
    {
        if (progressAttemptId is not null)
        {
            throw new NotSupportedException(
                $"{GetType().Name} must implement attempt-aware Case File persistence before it can commit a Case progress attempt.");
        }

        return SaveCaseFileAsync(caseRecord, caseFile, cancellationToken);
    }

    Task<int> SaveCaseFileAsync(
        CaseRecord caseRecord,
        CaseFile caseFile,
        Guid? progressAttemptId,
        IReadOnlyList<CrumbSourceResult>? crumbSourceSnapshot,
        CancellationToken cancellationToken)
    {
        if (crumbSourceSnapshot is not null)
        {
            throw new NotSupportedException(
                $"{GetType().Name} must implement Crumb-source snapshot persistence before it can commit collected Crumb-source results.");
        }

        return SaveCaseFileAsync(caseRecord, caseFile, progressAttemptId, cancellationToken);
    }

    Task SetStatusAsync(Guid caseId, string status, CancellationToken cancellationToken);

    Task<bool> RebuildCaseAsync(
        Guid caseId,
        string slackChannel,
        string slackTimestamp,
        CancellationToken cancellationToken);

    Task SetSlackTimestampAsync(Guid caseId, string timestamp, CancellationToken cancellationToken);

    Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);
}
