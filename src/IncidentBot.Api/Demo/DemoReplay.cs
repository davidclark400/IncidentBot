using IncidentBot.Api.Domain;
using IncidentBot.Api.Incidents;
using IncidentBot.Api.Options;
using Microsoft.Extensions.Options;

namespace IncidentBot.Api.Demo;

public sealed record DemoReplayStart
{
    internal DemoReplayStart(int generation, InvestigationReport report)
    {
        Generation = generation;
        Report = report;
    }

    internal int Generation { get; }
    public InvestigationReport Report { get; }
}

/// <summary>
/// Owns staged Demo report transitions and their live-update metadata.
/// </summary>
public sealed class DemoReplay(
    DemoIncidentStore store,
    IIncidentUpdatePublisher updates,
    IOptions<DemoOptions> options,
    ILogger<DemoReplay> logger)
{
    private static readonly string[] ResetSections =
        ["status", "summary", "timeline", "evidence", "sources", "causalEvents", "ai", "problem"];
    private readonly SemaphoreSlim transitionGate = new(1, 1);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await ResetAsync(cancellationToken);
        await foreach (var generation in store.ReadStartsAsync(cancellationToken))
        {
            logger.LogInformation("Starting demo incident replay generation {Generation}", generation);
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
            await PublishAsync(reset.Report, ResetSections, cancellationToken);
            return new DemoReplayStart(reset.Generation, reset.Report);
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
            var report = store.Advance(generation, phase);
            if (report is null)
            {
                return false;
            }

            await PublishAsync(report, ChangedSections(phase), cancellationToken);
            return true;
        }
        finally
        {
            transitionGate.Release();
        }
    }

    private Task PublishAsync(
        InvestigationReport report,
        IReadOnlyList<string> changedSections,
        CancellationToken cancellationToken) =>
        updates.PublishReportAsync(
            DemoIncidentStore.IncidentId,
            report.Version,
            report.Status,
            changedSections,
            cancellationToken);

    private static string[] ChangedSections(int phase) => phase switch
    {
        1 => ["summary", "timeline", "evidence", "causalEvents", "sources", "problem"],
        2 or 3 or 4 or 5 => ["summary", "timeline", "evidence", "causalEvents", "sources", "problem"],
        6 => ["summary", "ai", "status", "problem"],
        _ => ["status"]
    };
}
