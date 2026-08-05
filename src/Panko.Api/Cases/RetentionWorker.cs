using Panko.Api.Patterns;
using Panko.Api.Signatures;
using Panko.Api.Options;
using Microsoft.Extensions.Options;

namespace Panko.Api.Cases;

public sealed class RetentionWorker(
    ICaseStore repository,
    IOptions<PankoOptions> options,
    TimeProvider timeProvider,
    IPatternRepository patterns,
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
                if (deleted > 0) logger.LogInformation("Purged {CaseCount} expired Cases", deleted);
                var signatureCutoff = timeProvider.GetUtcNow() - TimeSpan.FromDays(options.Value.SignatureRetentionDays);
                var signatureRecordsDeleted = await patterns.PurgeAsync(signatureCutoff, stoppingToken);
                if (signatureRecordsDeleted > 0) logger.LogInformation("Purged {SignatureRecordCount} expired Signature records", signatureRecordsDeleted);
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
