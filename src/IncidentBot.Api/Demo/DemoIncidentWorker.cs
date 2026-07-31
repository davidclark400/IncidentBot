namespace IncidentBot.Api.Demo;

public sealed class DemoIncidentWorker(
    DemoReplay replay) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        replay.RunAsync(stoppingToken);
}
