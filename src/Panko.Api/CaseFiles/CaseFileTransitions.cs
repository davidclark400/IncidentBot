using Panko.Api.Domain;

namespace Panko.Api.CaseFiles;

public enum CaseFileTransition
{
    Initial,
    CollectionDisabled,
    CollectionStarted,
    Completed,
    InputsAccepted,
    InputsProjected,
    SourceRefreshStarted,
    SourceRefreshCompleted,
    AnalysisStarted,
    AnalysisCompleted
}

/// <summary>
/// Owns versioned Case File persistence and the matching live-update publication.
/// </summary>
public sealed class CaseFileTransitions(
    ICaseStore cases,
    ICaseUpdatePublisher updates)
{
    public async Task<CaseProgressTracker> StartProgressAsync(
        CaseRecord caseRecord,
        IReadOnlyList<string> selectedSources,
        int initialLookbackMinutes,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var progressTracker = new CaseProgressTracker(
            caseRecord,
            selectedSources,
            initialLookbackMinutes,
            timeProvider,
            CommitProgressAsync);
        await progressTracker.InitializeAsync(cancellationToken);
        return progressTracker;
    }

    public async Task<CaseRecord> CommitAsync(
        CaseRecord caseRecord,
        CaseFile caseFile,
        CaseFileTransition transition,
        CancellationToken cancellationToken,
        Guid? progressAttemptId = null,
        IReadOnlyList<CrumbSourceResult>? crumbSourceSnapshot = null)
    {
        if (transition == CaseFileTransition.Completed
            && (!progressAttemptId.HasValue || progressAttemptId.Value == Guid.Empty))
        {
            throw new ArgumentException(
                "The completed Case File transition must consume its Case progress attempt.",
                nameof(progressAttemptId));
        }

        if (transition != CaseFileTransition.Completed && progressAttemptId is not null)
        {
            throw new ArgumentException(
                $"The {transition} Case File transition cannot consume a Case progress attempt.",
                nameof(progressAttemptId));
        }

        if (transition != CaseFileTransition.Completed && crumbSourceSnapshot is not null)
        {
            throw new ArgumentException(
                $"The {transition} Case File transition cannot persist a Crumb-source snapshot.",
                nameof(crumbSourceSnapshot));
        }
        if (caseFile.CaseId != caseRecord.Id
            || caseFile.InputVersion != caseRecord.InputVersion
            || caseFile.ProjectedInputVersion != caseRecord.InputVersion)
        {
            throw new InvalidOperationException(
                "A Case File must project exactly the Case input version captured by its commit attempt.");
        }

        var version = await cases.SaveCaseFileAsync(
            caseRecord,
            caseFile,
            progressAttemptId,
            crumbSourceSnapshot,
            cancellationToken);
        var current = await cases.GetCaseAsync(caseRecord.Id, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Case '{caseRecord.Id}' disappeared after the {transition} Case File transition.");
        await updates.PublishCaseFileAsync(
            current.Id,
            version,
            current.InputVersion,
            current.ProjectedInputVersion,
            current.Status,
            ChangedSections(transition),
            cancellationToken);
        return current;
    }

    public async Task<CaseRecord> SetStatusAsync(
        CaseRecord caseRecord,
        string status,
        CancellationToken cancellationToken)
    {
        await cases.SetStatusAsync(caseRecord.Id, status, cancellationToken);
        var current = await cases.GetCaseAsync(caseRecord.Id, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Case '{caseRecord.Id}' disappeared after the '{status}' status transition.");
        await updates.PublishStatusAsync(
            current.Id,
            current.Version,
            current.InputVersion,
            current.ProjectedInputVersion,
            current.Status,
            cancellationToken);
        return current;
    }

    internal static IReadOnlyList<string> ChangedSections(CaseFileTransition transition) =>
        transition switch
        {
            CaseFileTransition.Initial => ["status", "trail", "crumbSources", "pattern"],
            CaseFileTransition.CollectionDisabled => ["status", "pattern"],
            CaseFileTransition.CollectionStarted => ["status", "crumbSources", "pattern"],
            CaseFileTransition.Completed =>
                ["status", "summary", "ai", "trail", "crumbs", "crumbSources", "links", "causalMarkers", "pattern"],
            CaseFileTransition.InputsAccepted => ["status", "inputVersion"],
            CaseFileTransition.InputsProjected =>
                ["status", "summary", "ai", "trail", "crumbs", "crumbSources", "links", "causalMarkers", "inputVersion", "projectedInputVersion"],
            CaseFileTransition.SourceRefreshStarted => ["status", "crumbSources"],
            CaseFileTransition.SourceRefreshCompleted => ["status", "crumbSources"],
            CaseFileTransition.AnalysisStarted => ["status", "ai"],
            CaseFileTransition.AnalysisCompleted => ["status", "ai"],
            _ => throw new ArgumentOutOfRangeException(nameof(transition), transition, null)
        };

    private async Task<CaseProgress?> CommitProgressAsync(
        CaseProgress progress,
        bool begin,
        CancellationToken cancellationToken)
    {
        var revision = begin
            ? await cases.BeginProgressAsync(progress, cancellationToken)
            : await cases.UpdateProgressAsync(progress, cancellationToken);
        if (revision is null) return null;

        var stored = progress with { Revision = revision.Value };
        await updates.PublishProgressAsync(stored, cancellationToken);
        return stored;
    }
}
