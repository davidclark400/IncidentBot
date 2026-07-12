using IncidentBot.Api.Fingerprinting;
using IncidentBot.Api.Options;
using Microsoft.Extensions.Options;

namespace IncidentBot.Api.Incidents;

public sealed class RetentionWorker(
    IIncidentStore repository,
    IOptions<IncidentBotOptions> options,
    TimeProvider timeProvider,
    IProblemRepository problems,
    ILogger<RetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextDelay = TimeSpan.Zero;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (nextDelay > TimeSpan.Zero)
            {
                await Task.Delay(nextDelay, timeProvider, stoppingToken);
            }

            try
            {
                logger.LogDebug("Retention pass started");
                var cutoff = timeProvider.GetUtcNow() - TimeSpan.FromDays(options.Value.RetentionDays);
                var deleted = await repository.PurgeOlderThanAsync(cutoff, stoppingToken);
                if (deleted > 0) logger.LogInformation("Purged {IncidentCount} expired incident investigations", deleted);
                var fingerprintCutoff = timeProvider.GetUtcNow() - TimeSpan.FromDays(options.Value.FingerprintRetentionDays);
                var compactDeleted = await problems.PurgeAsync(fingerprintCutoff, stoppingToken);
                if (compactDeleted > 0) logger.LogInformation("Purged {FingerprintRecordCount} expired compact fingerprint records", compactDeleted);
                logger.LogDebug("Retention pass completed");
                nextDelay = TimeSpan.FromHours(6);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                nextDelay = TimeSpan.FromMinutes(5);
                logger.LogError(
                    exception,
                    "Retention pass failed; retrying in {RetryDelayMinutes} minutes",
                    nextDelay.TotalMinutes);
            }
        }
    }
}
