using System.Collections.Concurrent;

namespace IncidentBot.Api.Incidents;

public sealed class InvestigationRunRegistry
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> activeRuns = [];

    public bool TryBegin(
        Guid incidentId,
        CancellationToken hostCancellationToken,
        out CancellationTokenSource runCancellation)
    {
        runCancellation = CancellationTokenSource.CreateLinkedTokenSource(hostCancellationToken);
        if (activeRuns.TryAdd(incidentId, runCancellation))
        {
            return true;
        }

        runCancellation.Dispose();
        runCancellation = null!;
        return false;
    }

    public bool Cancel(Guid incidentId) =>
        activeRuns.TryGetValue(incidentId, out var run) && Cancel(run);

    public void Complete(Guid incidentId, CancellationTokenSource runCancellation)
    {
        if (activeRuns.TryGetValue(incidentId, out var active) && ReferenceEquals(active, runCancellation))
        {
            activeRuns.TryRemove(incidentId, out _);
        }

        runCancellation.Dispose();
    }

    private static bool Cancel(CancellationTokenSource run)
    {
        if (run.IsCancellationRequested)
        {
            return false;
        }

        run.Cancel();
        return true;
    }
}

public sealed class InvestigationRestartService(
    IIncidentStore repository,
    InvestigationRunRegistry runs,
    ILogger<InvestigationRestartService> logger)
{
    public async Task<bool> RestartAsync(
        Guid incidentId,
        string? slackChannel,
        string? slackTimestamp,
        CancellationToken cancellationToken)
    {
        var restarted = await repository.RestartInvestigationAsync(
            incidentId, slackChannel, slackTimestamp, cancellationToken);
        if (!restarted)
        {
            return false;
        }

        var cancelled = runs.Cancel(incidentId);
        logger.LogWarning(
            "Investigation restart requested for incident {IncidentId}; active run cancelled: {ActiveRunCancelled}",
            incidentId, cancelled);
        return true;
    }
}
