using Panko.Api.Domain;
using Panko.Api.Cases;
using Panko.Api.Options;
using Microsoft.Extensions.Options;

namespace Panko.Api.Demo;

public sealed record DemoReplayStart
{
    internal DemoReplayStart(int generation, CaseFile caseFile)
    {
        Generation = generation;
        CaseFile = caseFile;
    }

    internal int Generation { get; }
    public CaseFile CaseFile { get; }
}

/// <summary>
/// Owns the Demo's initial/final Case File transitions and lightweight in-flight progress updates.
/// </summary>
public sealed class DemoReplay(
    DemoCaseStore store,
    ICaseUpdatePublisher updates,
    IOptions<DemoOptions> options,
    ILogger<DemoReplay> logger)
{
    private static readonly string[] ResetSections =
        ["status", "summary", "trail", "crumbs", "crumbSources", "causalMarkers", "ai", "pattern"];
    private static readonly string[] CompletedSections =
        ["status", "summary", "ai", "trail", "crumbs", "crumbSources", "links", "causalMarkers", "pattern"];
    private readonly SemaphoreSlim transitionGate = new(1, 1);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await ResetAsync(cancellationToken);
        await foreach (var generation in store.ReadStartsAsync(cancellationToken))
        {
            logger.LogInformation("Starting Demo Case replay generation {Generation}", generation);
            for (var phase = 1; phase <= 6; phase++)
            {
                if (!store.IsCurrentGeneration(generation)) break;

                await Task.Delay(
                    TimeSpan.FromSeconds(options.Value.StepDelaySeconds),
                    cancellationToken);
                if (!await AdvanceAsync(generation, phase, cancellationToken)) break;
            }
        }
    }

    public async Task<DemoReplayStart> ResetAsync(CancellationToken cancellationToken)
    {
        await transitionGate.WaitAsync(cancellationToken);
        try
        {
            var reset = store.Reset();
            await PublishAsync(reset.CaseFile, ResetSections, cancellationToken);
            await updates.PublishProgressAsync(reset.Progress, cancellationToken);
            return new DemoReplayStart(reset.Generation, reset.CaseFile);
        }
        finally
        {
            transitionGate.Release();
        }
    }

    internal async Task<bool> AdvanceAsync(
        int generation,
        int phase,
        CancellationToken cancellationToken)
    {
        await transitionGate.WaitAsync(cancellationToken);
        try
        {
            var transition = store.Advance(generation, phase);
            if (transition is null)
            {
                return false;
            }

            if (transition.Progress is not null)
            {
                await updates.PublishProgressAsync(transition.Progress, cancellationToken);
                return true;
            }

            var caseFile = transition.CaseFile
                ?? throw new InvalidOperationException($"Demo phase {phase} produced no transition payload.");
            await PublishAsync(caseFile, CompletedSections, cancellationToken);
            return true;
        }
        finally
        {
            transitionGate.Release();
        }
    }

    private Task PublishAsync(
        CaseFile caseFile,
        IReadOnlyList<string> changedSections,
        CancellationToken cancellationToken) =>
        updates.PublishCaseFileAsync(
            DemoCaseStore.CaseId,
            caseFile.CaseFileVersion,
            caseFile.Status,
            changedSections,
            cancellationToken);
}
