namespace Panko.Api.Demo;

public sealed class DemoCaseWorker(
    DemoReplay replay) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        replay.RunAsync(stoppingToken);
}
