using System.Diagnostics;
using Panko.Api.Crumbs;
using Panko.Api.Domain;
using Panko.Api.Cases;
using Panko.Api.Options;
using Panko.Api.Recipes;
using Panko.Api.Security;
using Microsoft.Extensions.Options;

namespace Panko.Api.Cases;

public sealed class CaseWorkHandler(
    ICaseInputStore repository,
    RecipeStore recipes,
    CrumbSourceRegistry crumbSources,
    AdaptiveCrumbCollector crumbCollector,
    CaseFileProjectionBuilder projections,
    ICaseFileSynthesizer synthesizer,
    ICaseUpdatePublisher updates,
    IOptions<PankoOptions> options,
    CaseTelemetry telemetry,
    TimeProvider timeProvider,
    ILogger<CaseWorkHandler> logger)
{
    public Task ProcessAsync(WorkItem item, CancellationToken cancellationToken)
    {
        if (CaseWorkKinds.IsProject(item.Kind))
        {
            return ProjectAsync(item, cancellationToken);
        }
        if (CaseWorkKinds.IsRefreshSources(item.Kind))
        {
            return RefreshSourcesAsync(item, cancellationToken);
        }
        if (CaseWorkKinds.IsAnalyse(item.Kind))
        {
            return AnalyseAsync(item, cancellationToken);
        }

        throw new InvalidOperationException($"Unknown Case work kind '{item.Kind}'.");
    }

    private async Task ProjectAsync(WorkItem item, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var caseRecord = await RequireCaseAsync(item.CaseId, cancellationToken);
        var target = item.TargetInputVersion ?? caseRecord.InputVersion;
        if (item.TargetWorkflowGeneration is { } targetWorkflowGeneration
            && (caseRecord.WorkflowGeneration > targetWorkflowGeneration
                || caseRecord.ProjectedWorkflowGeneration >= targetWorkflowGeneration))
        {
            return;
        }
        if (caseRecord.ProjectedInputVersion > target)
        {
            return;
        }
        var recipe = ResolveOwnedRecipe(caseRecord);
        var inputs = await repository.ListInputsAsync(
            caseRecord.Id, target, includeInactive: false, cancellationToken);
        var crumbSourceResults = await repository.GetLatestCrumbSourceResultsAsync(
            caseRecord.Id, cancellationToken);
        var previous = await repository.GetCaseFileAsync(caseRecord.Id, cancellationToken);
        var caseFile = projections.Build(
            caseRecord,
            recipe,
            recipes.Revision,
            target,
            inputs,
            crumbSourceResults,
            new AiSynthesis("pending", null, [], [], [], null),
            previous?.Pattern);
        int? version;
        try
        {
            version = await repository.CommitProjectionAsync(
                caseRecord,
                caseFile,
                target,
                cancellationToken,
                item.TargetWorkflowGeneration);
        }
        catch (CaseConflictException)
        {
            telemetry.ProjectionRetried();
            throw;
        }
        if (version is null)
        {
            var current = await RequireCaseAsync(caseRecord.Id, cancellationToken);
            if (current.ProjectedInputVersion > target)
            {
                return;
            }
            if (item.TargetWorkflowGeneration is { } committedWorkflowGeneration
                && (current.WorkflowGeneration > committedWorkflowGeneration
                    || current.ProjectedWorkflowGeneration >= committedWorkflowGeneration))
            {
                return;
            }
            telemetry.ProjectionRetried();
            throw new InvalidOperationException(
                $"Case '{caseRecord.Id}' changed while input version {target} was projected.");
        }

        var committed = await RequireCaseAsync(caseRecord.Id, cancellationToken);
        await updates.PublishCaseFileAsync(
            committed.Id,
            version.Value,
            committed.InputVersion,
            committed.ProjectedInputVersion,
            committed.Status,
            CaseFileTransitions.ChangedSections(
                CaseFileTransition.InputsProjected),
            cancellationToken);
        telemetry.ProjectionCompleted(stopwatch.Elapsed);
        telemetry.ProjectionLag(committed.InputVersion, committed.ProjectedInputVersion);
    }

    private async Task RefreshSourcesAsync(WorkItem item, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var caseRecord = await RequireCaseAsync(item.CaseId, cancellationToken);
        if (caseRecord.IsFrozen) return;
        if (!options.Value.CrumbCollectionEnabled)
        {
            throw new CaseConflictException(
                "Crumb collection is disabled by deployment policy.");
        }
        var recipe = ResolveOwnedRecipe(caseRecord);

        var started = await RequireCaseAsync(caseRecord.Id, cancellationToken);
        await updates.PublishCaseFileAsync(
            started.Id,
            started.Version,
            started.InputVersion,
            started.ProjectedInputVersion,
            started.Status,
            CaseFileTransitions.ChangedSections(
                CaseFileTransition.SourceRefreshStarted),
            cancellationToken);

        var context = new CaseContext(
            caseRecord.Id,
            caseRecord.PagerDutyIncidentId,
            caseRecord.ServiceId,
            caseRecord.Title,
            caseRecord.Urgency,
            caseRecord.PagerDutyState,
            caseRecord.OpenedAt,
            caseRecord.Labels,
            recipe)
        {
            AcknowledgedAt = caseRecord.AcknowledgedAt,
            ResolvedAt = caseRecord.ResolvedAt
        };
        var selectedSources = crumbSources.Select(recipe)
            .Where(source => caseRecord.PagerDutyIncidentId is not null
                || !string.Equals(source.Source, CrumbSourceRegistry.PagerDuty, StringComparison.Ordinal))
            .ToArray();
        var collection = await crumbCollector.CollectAsync(
            context,
            recipes.Revision,
            selectedSources,
            cancellationToken);
        await repository.SaveCrumbSourceSnapshotsAsync(
            caseRecord.Id, collection.SourceResults, cancellationToken);

        var completed = await RequireCaseAsync(caseRecord.Id, cancellationToken);
        await updates.PublishCaseFileAsync(
            completed.Id,
            completed.Version,
            completed.InputVersion,
            completed.ProjectedInputVersion,
            completed.Status,
            CaseFileTransitions.ChangedSections(
                CaseFileTransition.SourceRefreshCompleted),
            cancellationToken);
        telemetry.SourceRefreshCompleted(stopwatch.Elapsed);
    }

    private async Task AnalyseAsync(WorkItem item, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var caseRecord = await RequireCaseAsync(item.CaseId, cancellationToken);
        var target = item.TargetInputVersion ?? caseRecord.ProjectedInputVersion;
        if (item.TargetWorkflowGeneration is { } workflowGeneration
            && caseRecord.WorkflowGeneration > workflowGeneration)
        {
            telemetry.LlmCallAvoided("newer-workflow");
            return;
        }
        if (caseRecord.ProjectedInputVersion > target)
        {
            telemetry.LlmCallAvoided("newer-projection");
            return;
        }
        if (caseRecord.InputVersion > target)
        {
            telemetry.LlmCallAvoided("newer-input");
            return;
        }
        if (caseRecord.ProjectedInputVersion < target)
        {
            throw new InvalidOperationException(
                $"Analysis for Case '{caseRecord.Id}' is waiting for projection {target}.");
        }
        ResolveOwnedRecipe(caseRecord);
        var caseFile = await repository.GetCaseFileAsync(caseRecord.Id, cancellationToken)
            ?? throw new InvalidOperationException($"Case '{caseRecord.Id}' has no projected Case File.");
        if (!string.Equals(caseFile.Ai.Status, "pending", StringComparison.Ordinal))
        {
            telemetry.LlmCallAvoided("analysis-already-completed");
            return;
        }
        if (caseFile.Crumbs.Count == 0)
        {
            return;
        }

        var analysisCase = await RequireCaseAsync(caseRecord.Id, cancellationToken);
        if (analysisCase.InputVersion > target
            || analysisCase.ProjectedInputVersion != target
            || item.TargetWorkflowGeneration is { } analysisWorkflowGeneration
            && analysisCase.WorkflowGeneration > analysisWorkflowGeneration)
        {
            telemetry.LlmCallAvoided("analysis-superseded-before-call");
            return;
        }
        if (analysisCase.Version != caseRecord.Version)
        {
            telemetry.LlmCallAvoided("newer-case-file-before-call");
            return;
        }
        await updates.PublishCaseFileAsync(
            analysisCase.Id,
            analysisCase.Version,
            analysisCase.InputVersion,
            analysisCase.ProjectedInputVersion,
            analysisCase.Status,
            CaseFileTransitions.ChangedSections(
                CaseFileTransition.AnalysisStarted),
            cancellationToken);

        var groupedResults = caseFile.Crumbs
            .GroupBy(crumb => crumb.Source, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var source = caseFile.CrumbSources.FirstOrDefault(item => item.Source == group.Key);
                return new CrumbSourceResult(
                    group.Key,
                    source?.Health ?? CrumbSourceHealth.Complete,
                    group.ToArray(),
                    caseFile.Trail.Where(item => item.Source == group.Key).ToArray(),
                    source?.Links ?? [],
                    source?.DurationMilliseconds ?? 0,
                    source?.Diagnostic);
            })
            .ToArray();
        var ai = await synthesizer.SynthesizeAsync(
            CaseSubject.FromCase(analysisCase),
            groupedResults,
            caseFile.Ai,
            cancellationToken);
        var analysed = caseFile with
        {
            Ai = ai,
            UpdatedAt = timeProvider.GetUtcNow(),
            Status = analysisCase.IsFrozen ? CaseProgression.Resolved : CaseProgression.Ready,
            InputVersion = analysisCase.InputVersion,
            ProjectedInputVersion = target
        };
        var version = await repository.CommitAnalysisAsync(
            analysisCase,
            analysed,
            target,
            cancellationToken,
            item.TargetWorkflowGeneration);
        if (version is null)
        {
            var current = await RequireCaseAsync(caseRecord.Id, cancellationToken);
            if (current.ProjectedInputVersion > target || current.InputVersion > target)
            {
                return;
            }
            if (current.Version > analysisCase.Version)
            {
                return;
            }
            throw new InvalidOperationException(
                $"Case '{caseRecord.Id}' changed while projection {target} was analysed.");
        }
        var committed = await RequireCaseAsync(caseRecord.Id, cancellationToken);
        await updates.PublishCaseFileAsync(
            committed.Id,
            version.Value,
            committed.InputVersion,
            committed.ProjectedInputVersion,
            committed.Status,
            CaseFileTransitions.ChangedSections(
                CaseFileTransition.AnalysisCompleted),
            cancellationToken);
        telemetry.AnalysisCompleted(stopwatch.Elapsed);
        logger.LogInformation(
            "Case analysis completed for {CaseId} at input version {InputVersion} with synthesis status {SynthesisStatus}",
            analysisCase.Id,
            target,
            ai.Status);
    }

    private async Task<CaseRecord> RequireCaseAsync(
        Guid caseId,
        CancellationToken cancellationToken) =>
        await repository.GetCaseAsync(caseId, cancellationToken)
        ?? throw new CaseNotFoundException(caseId);

    private Recipe ResolveOwnedRecipe(CaseRecord caseRecord)
    {
        var recipe = recipes.ResolveById(caseRecord.RecipeId);
        if (!TeamKey.IsCanonical(caseRecord.Team)
            || !string.Equals(recipe.Team, caseRecord.Team, StringComparison.Ordinal))
        {
            throw new CaseConflictException(
                $"Case '{caseRecord.Id}' ownership no longer matches its current Recipe configuration.");
        }

        return recipe;
    }
}
