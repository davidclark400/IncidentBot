using Panko.Api.Crumbs;
using Panko.Api.Domain;
using Panko.Api.Patterns;
using Panko.Api.Signatures;
using Panko.Api.Cases;
using Panko.Api.Options;
using Panko.Api.Recipes;
using Panko.Api.Security;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace Panko.Api.CaseFiles;

public interface IRecipeProvider
{
    string Revision { get; }
    Recipe Resolve(string serviceId, IReadOnlyDictionary<string, string> labels);
}

public interface ICaseFileSynthesizer
{
    Task<AiSynthesis> SynthesizeAsync(
        CaseSubject subject,
        IReadOnlyList<CrumbSourceResult> results,
        AiSynthesis? previous,
        CancellationToken cancellationToken);
}

public interface ICaseUpdatePublisher
{
    Task PublishStatusAsync(Guid caseId, int version, string status, CancellationToken cancellationToken);
    Task PublishStatusAsync(
        Guid caseId,
        int version,
        long inputVersion,
        long projectedInputVersion,
        string status,
        CancellationToken cancellationToken) =>
        PublishStatusAsync(caseId, version, status, cancellationToken);
    Task PublishCaseFileAsync(
        Guid caseId,
        int version,
        string status,
        IReadOnlyList<string> changedSections,
        CancellationToken cancellationToken);
    Task PublishCaseFileAsync(
        Guid caseId,
        int version,
        long inputVersion,
        long projectedInputVersion,
        string status,
        IReadOnlyList<string> changedSections,
        CancellationToken cancellationToken) =>
        PublishCaseFileAsync(caseId, version, status, changedSections, cancellationToken);

    Task PublishProgressAsync(
        CaseProgress progress,
        CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class CaseFileBuilder(
    ICaseStore repository,
    IRecipeProvider recipes,
    CrumbSourceRegistry crumbSources,
    AdaptiveCrumbCollector crumbCollector,
    CaseFileProjectionBuilder projectionBuilder,
    ICaseFileSynthesizer synthesizer,
    CaseFileTransitions transitions,
    IOptions<PankoOptions> options,
    TimeProvider timeProvider,
    IPatternCoordinator patterns,
    ILogger<CaseFileBuilder> logger)
{
    public async Task RunAsync(Guid caseId, CancellationToken cancellationToken)
    {
        var caseStopwatch = Stopwatch.StartNew();
        var caseRecord = await repository.GetCaseAsync(caseId, cancellationToken)
            ?? throw new InvalidOperationException($"Case '{caseId}' no longer exists.");
        var targetInputVersion = caseRecord.InputVersion;
        var projectionInputs = await repository.GetProjectionInputsAsync(
            caseRecord.Id,
            targetInputVersion,
            cancellationToken);
        var recipe = recipes.Resolve(caseRecord.ServiceId, caseRecord.Labels);
        if (!TeamKey.IsCanonical(caseRecord.Team)
            || !string.Equals(recipe.Id, caseRecord.RecipeId, StringComparison.Ordinal)
            || !string.Equals(recipe.Team, caseRecord.Team, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Case '{caseRecord.Id}' ownership no longer matches its current Recipe configuration.");
        }
        using var logScope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["CaseId"] = caseRecord.Id,
            ["PagerDutyIncidentId"] = caseRecord.PagerDutyIncidentId,
            ["ServiceId"] = caseRecord.ServiceId,
            ["RecipeId"] = recipe.Id,
            ["CaseFileVersion"] = caseRecord.Version
        });
        logger.LogInformation(
            "Case File build started with Recipe revision {RecipeRevision}; Crumb collection enabled: {CrumbCollectionEnabled}",
            recipes.Revision, options.Value.CrumbCollectionEnabled);
        var provisionalContext = await patterns.ResolveProvisionalAsync(
            caseRecord, options.Value.CrumbCollectionEnabled, cancellationToken);

        var createdInitialCaseFile = false;
        if (caseRecord.Version == 0)
        {
            var initial = projectionBuilder.Build(
                caseRecord,
                recipe,
                recipes.Revision,
                targetInputVersion,
                projectionInputs.ActiveInputs,
                projectionInputs.RetainedCrumbSourceResults,
                new AiSynthesis("pending", null, [], [], [], null),
                provisionalContext);
            initial = options.Value.CrumbCollectionEnabled
                ? initial with
                {
                    Status = CaseProgression.Collecting,
                    DeterministicSummary = "Case opened. Crumb collectors are running.",
                    CrumbSources = PendingSources(recipe)
                }
                : initial with
                {
                    Status = CaseProgression.ForDisabledCollection(caseRecord.IsFrozen)
                };
            var committed = await transitions.CommitAsync(
                caseRecord,
                initial,
                CaseFileTransition.Initial,
                cancellationToken);
            caseRecord = RebaseExpectedCase(caseRecord, committed);
            createdInitialCaseFile = true;
        }

        if (!options.Value.CrumbCollectionEnabled)
        {
            if (!createdInitialCaseFile)
            {
                var disabledPrevious = await repository.GetCaseFileAsync(caseRecord.Id, cancellationToken);
                var updated = projectionBuilder.Build(
                    caseRecord,
                    recipe,
                    recipes.Revision,
                    targetInputVersion,
                    projectionInputs.ActiveInputs,
                    projectionInputs.RetainedCrumbSourceResults,
                    disabledPrevious?.Ai ?? new AiSynthesis("pending", null, [], [], [], null),
                    provisionalContext) with
                {
                    Status = CaseProgression.ForDisabledCollection(caseRecord.IsFrozen)
                };
                await transitions.CommitAsync(
                    caseRecord,
                    updated,
                    CaseFileTransition.CollectionDisabled,
                    cancellationToken);
            }
            logger.LogInformation(
                "Case File build completed without Crumb collection in {DurationMilliseconds} ms",
                caseStopwatch.ElapsedMilliseconds);
            return;
        }

        var collecting = await transitions.SetStatusAsync(
            caseRecord,
            CaseProgression.Collecting,
            cancellationToken);
        caseRecord = RebaseExpectedCase(caseRecord, collecting);

        var context = new CaseContext(
            caseRecord.Id, caseRecord.PagerDutyIncidentId, caseRecord.ServiceId, caseRecord.Title, caseRecord.Urgency,
            caseRecord.PagerDutyState, caseRecord.OpenedAt, caseRecord.Labels, recipe)
        {
            AcknowledgedAt = caseRecord.AcknowledgedAt,
            ResolvedAt = caseRecord.ResolvedAt
        };
        var enabledSources = crumbSources.EnabledSources(recipe);
        var selectedSources = crumbSources.Select(recipe);
        if (selectedSources.Count == 0)
        {
            logger.LogWarning(
                "Crumb collection has no selected Crumb sources; enabled Recipe sources were {EnabledSources}",
                string.Join(',', enabledSources));
        }
        else
        {
            logger.LogInformation(
                "Crumb collection started for {CrumbSourceCount} sources: {CrumbSources}",
                selectedSources.Count, string.Join(',', selectedSources.Select(source => source.Source)));
        }
        var progress = await transitions.StartProgressAsync(
            caseRecord,
            selectedSources.Select(source => source.Source).ToArray(),
            options.Value.CrumbWindowMinutes,
            timeProvider,
            cancellationToken);
        var collection = await crumbCollector.CollectAsync(
            context,
            recipes.Revision,
            selectedSources,
            progress,
            cancellationToken);
        var results = collection.SourceResults;
        var previous = await repository.GetCaseFileAsync(caseRecord.Id, cancellationToken);
        var caseFile = projectionBuilder.Build(
            caseRecord,
            recipe,
            recipes.Revision,
            targetInputVersion,
            projectionInputs.ActiveInputs,
            results,
            new AiSynthesis("pending", null, [], [], [], null),
            provisionalContext,
            collection.Outcome);
        caseFile = caseFile with
        {
            Pattern = await patterns.ResolveFinalAsync(caseRecord, caseFile.Crumbs, cancellationToken)
        };
        await progress.CollectionCompletedAsync(collection.Outcome, results, cancellationToken);
        logger.LogDebug("Synthesis started with {CrumbSourceResultCount} Crumb-source results", results.Count);
        var ai = await synthesizer.SynthesizeAsync(
            CaseSubject.FromCase(caseRecord),
            results,
            previous?.Ai,
            cancellationToken);
        caseFile = caseFile with
        {
            Ai = ai,
            UpdatedAt = timeProvider.GetUtcNow()
        };
        await progress.SynthesisCompletedAsync(ai, cancellationToken);
        caseRecord = await transitions.CommitAsync(
            caseRecord,
            caseFile,
            CaseFileTransition.Completed,
            cancellationToken,
            progress.AttemptId,
            results);
        logger.LogInformation(
            "Case File build completed in {DurationMilliseconds} ms with Case File version {CaseFileVersion}, status {CaseFileStatus}, synthesis status {SynthesisStatus}, and Crumb completion reason {CrumbCompletionReason}",
            caseStopwatch.ElapsedMilliseconds,
            caseRecord.Version,
            caseFile.Status,
            ai.Status,
            collection.Outcome.CompletionReason);
    }

    private IReadOnlyList<CrumbSourceStatus> PendingSources(Recipe recipe) =>
        crumbSources.EnabledSources(recipe)
            .Select(source => new CrumbSourceStatus(
                source,
                CrumbSourceHealth.Pending,
                0,
                0,
                null,
                [],
                CrumbSourceRequestState.Requested))
            .ToList();

    private static CaseRecord RebaseExpectedCase(
        CaseRecord expected,
        CaseRecord current) =>
        expected with
        {
            Version = current.Version,
            Status = current.Status,
            UpdatedAt = current.UpdatedAt,
            ProjectedInputVersion = current.ProjectedInputVersion
        };

}
