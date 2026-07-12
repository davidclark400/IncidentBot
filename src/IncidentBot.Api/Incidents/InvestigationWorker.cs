using IncidentBot.Api.Domain;

namespace IncidentBot.Api.Incidents;

public sealed class InvestigationWorker(
    IDurableQueue<WorkItem> queue,
    InvestigationRunner runner,
    InvestigationRunRegistry runs,
    ILogger<InvestigationWorker> logger) :
    DurableWorker<WorkItem>(queue, TimeSpan.FromMilliseconds(500), logger)
{
    protected override async Task ProcessAsync(WorkItem item, CancellationToken cancellationToken)
    {
        if (!runs.TryBegin(item.IncidentId, cancellationToken, out var runCancellation))
        {
            logger.LogWarning(
                "Skipping duplicate investigation work item {WorkItemId} because incident {IncidentId} is already running",
                item.Id, item.IncidentId);
            return;
        }

        try
        {
            await runner.RunAsync(item.IncidentId, runCancellation.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && runCancellation.IsCancellationRequested)
        {
            logger.LogWarning(
                "Investigation run cancelled for incident {IncidentId}; the work item will be completed because a restart was requested",
                item.IncidentId);
        }
        finally
        {
            runs.Complete(item.IncidentId, runCancellation);
        }
    }
}
