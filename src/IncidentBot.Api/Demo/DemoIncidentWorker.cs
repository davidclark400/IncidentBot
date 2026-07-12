using IncidentBot.Api.Incidents;
using IncidentBot.Api.Options;
using Microsoft.Extensions.Options;

namespace IncidentBot.Api.Demo;

public sealed class DemoIncidentWorker(
    DemoIncidentStore store,
    IIncidentUpdatePublisher updates,
    IOptions<DemoOptions> options,
    ILogger<DemoIncidentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        store.Reset();
        await foreach (var generation in store.ReadStartsAsync(stoppingToken))
        {
            logger.LogInformation("Starting demo incident replay generation {Generation}", generation);
            for (var phase = 1; phase <= 6; phase++)
            {
                await Task.Delay(TimeSpan.FromSeconds(options.Value.StepDelaySeconds), stoppingToken);
                var report = store.Advance(generation, phase);
                if (report is null) break;
                await updates.PublishReportAsync(
                    DemoIncidentStore.IncidentId,
                    report.Version,
                    report.Status,
                    ChangedSections(phase),
                    stoppingToken);
            }
        }
    }

    private static string[] ChangedSections(int phase) => phase switch
    {
        1 or 2 => ["timeline", "evidence", "causalEvents", "sources"],
        3 or 4 or 5 => ["summary", "timeline", "evidence", "causalEvents", "sources", "links"],
        6 => ["summary", "ai", "status"],
        _ => ["status"]
    };
}
