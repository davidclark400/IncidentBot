using System.Diagnostics;
using Panko.Api.Domain;

namespace Panko.Api.Cases;

public interface IDurableQueue<TItem>
{
    Task<TItem?> LeaseAsync(CancellationToken cancellationToken);
    Task CompleteAsync(TItem item, CancellationToken cancellationToken);
    Task FailAsync(TItem item, Exception exception, CancellationToken cancellationToken);
}

public abstract class DurableWorker<TItem>(
    IDurableQueue<TItem> queue,
    TimeSpan idleDelay,
    ILogger logger) : BackgroundService
{
    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var consecutiveFailures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await ProcessNextAsync(stoppingToken))
                {
                    await Task.Delay(idleDelay, stoppingToken);
                }
                if (consecutiveFailures > 0)
                {
                    logger.LogInformation(
                        "Durable worker {WorkerName} recovered after {ConsecutiveFailureCount} consecutive queue failures",
                        GetType().Name, consecutiveFailures);
                    consecutiveFailures = 0;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                consecutiveFailures++;
                var retryDelay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(consecutiveFailures, 5))));
                logger.LogError(
                    exception,
                    "Durable worker {WorkerName} queue operation failed ({ConsecutiveFailureCount} consecutive failures); retrying in {RetryDelaySeconds} seconds",
                    GetType().Name, consecutiveFailures, retryDelay.TotalSeconds);
                await Task.Delay(retryDelay, stoppingToken);
            }
        }
    }

    internal async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        var item = await queue.LeaseAsync(cancellationToken);
        if (item is null)
        {
            return false;
        }

        var (itemId, itemKind, attempt) = Describe(item);
        var scopeValues = new Dictionary<string, object>
        {
            ["DurableItemType"] = typeof(TItem).Name,
            ["DurableItemId"] = itemId,
            ["DurableItemKind"] = itemKind,
            ["DurableAttempt"] = attempt
        };
        if (item is WorkItem workItem)
        {
            scopeValues["CaseId"] = workItem.CaseId;
        }
        using var logScope = logger.BeginScope(scopeValues);
        var stopwatch = Stopwatch.StartNew();
        using var watchdogCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var watchdog = WatchForSlowProcessingAsync(stopwatch, watchdogCancellation.Token);
        logger.LogDebug("Durable item processing started");
        if (attempt == 3)
        {
            logger.LogWarning("Durable item entered a repeated retry cycle at attempt {DurableAttempt}", attempt);
        }
        else if (attempt >= 10 && attempt % 10 == 0)
        {
            logger.LogCritical("Durable item remains in a retry cycle at attempt {DurableAttempt}", attempt);
        }
        try
        {
            await ProcessAsync(item, cancellationToken);
            await queue.CompleteAsync(item, cancellationToken);
            logger.LogDebug(
                "Durable item processing completed in {DurationMilliseconds} ms",
                stopwatch.ElapsedMilliseconds);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(
                exception,
                "Durable item processing failed after {DurationMilliseconds} ms; it will be released for retry",
                stopwatch.ElapsedMilliseconds);
            await queue.FailAsync(item, exception, cancellationToken);
        }
        finally
        {
            await watchdogCancellation.CancelAsync();
            await watchdog;
        }

        return true;
    }

    protected abstract Task ProcessAsync(TItem item, CancellationToken cancellationToken);

    private async Task WatchForSlowProcessingAsync(Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        var elapsedMinutes = 0;
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
                elapsedMinutes++;
                if (elapsedMinutes <= 3 || elapsedMinutes % 5 == 0)
                {
                    logger.LogWarning(
                        "Durable item is still processing after {ElapsedSeconds} seconds; a dependency may be stalled",
                        (long)stopwatch.Elapsed.TotalSeconds);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static (object ItemId, string ItemKind, int Attempt) Describe(TItem item) => item switch
    {
        WorkItem work => (work.Id, work.Kind, work.Attempts),
        OutboxItem outbox => (outbox.Id, outbox.Kind, outbox.Attempts),
        _ => ("unknown", typeof(TItem).Name, 0)
    };
}
