using Panko.Api.Domain;

namespace Panko.Api.Cases;

public sealed class CaseWorker(
    IDurableQueue<WorkItem> queue,
    CaseFileBuilder runner,
    CaseRunRegistry runs,
    ILogger<CaseWorker> logger,
    CaseWorkHandler? caseWork = null) :
    DurableWorker<WorkItem>(queue, TimeSpan.FromMilliseconds(500), logger)
{
    protected override async Task ProcessAsync(WorkItem item, CancellationToken cancellationToken)
    {
        if (!CaseWorkKinds.IsBuild(item.Kind))
        {
            if (caseWork is null)
            {
                throw new InvalidOperationException(
                    $"No Case work handler is registered for '{item.Kind}'.");
            }
            await caseWork.ProcessAsync(item, cancellationToken);
            return;
        }

        if (!runs.TryBegin(item.CaseId, cancellationToken, out var runCancellation))
        {
            logger.LogWarning(
                "Skipping duplicate Case work item {WorkItemId} because Case {CaseId} is already running",
                item.Id, item.CaseId);
            return;
        }

        try
        {
            await runner.RunAsync(item.CaseId, runCancellation.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && runCancellation.IsCancellationRequested)
        {
            logger.LogWarning(
                "Case File build cancelled for Case {CaseId}; the work item will complete because a rebuild was requested",
                item.CaseId);
        }
        finally
        {
            runs.Complete(item.CaseId, runCancellation);
        }
    }
}
