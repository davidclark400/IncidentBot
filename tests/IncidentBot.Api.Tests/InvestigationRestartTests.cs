using IncidentBot.Api.Domain;
using IncidentBot.Api.Incidents;
using Microsoft.Extensions.Logging.Abstractions;

namespace IncidentBot.Api.Tests;

public sealed class InvestigationRestartTests
{
    [Fact]
    public async Task SuccessfulRestartForwardsSlackIdentityAndCancelsTheActiveRun()
    {
        var incidentId = Guid.NewGuid();
        var repository = new RecordingRepository(restartResult: true);
        var runs = new InvestigationRunRegistry();
        Assert.True(runs.TryBegin(incidentId, CancellationToken.None, out var activeRun));
        var service = new InvestigationRestartService(
            repository, runs, NullLogger<InvestigationRestartService>.Instance);

        var restarted = await service.RestartAsync(
            incidentId, "C123", "171234.5678", CancellationToken.None);

        Assert.True(restarted);
        Assert.True(activeRun.IsCancellationRequested);
        Assert.Equal((incidentId, "C123", "171234.5678"), repository.RestartRequest);
        runs.Complete(incidentId, activeRun);
    }

    [Fact]
    public async Task RejectedRestartLeavesTheActiveRunAlone()
    {
        var incidentId = Guid.NewGuid();
        var repository = new RecordingRepository(restartResult: false);
        var runs = new InvestigationRunRegistry();
        Assert.True(runs.TryBegin(incidentId, CancellationToken.None, out var activeRun));
        var service = new InvestigationRestartService(
            repository, runs, NullLogger<InvestigationRestartService>.Instance);

        var restarted = await service.RestartAsync(
            incidentId, "C123", "171234.5678", CancellationToken.None);

        Assert.False(restarted);
        Assert.False(activeRun.IsCancellationRequested);
        runs.Complete(incidentId, activeRun);
    }

    private sealed class RecordingRepository(bool restartResult) : IIncidentStore
    {
        public (Guid IncidentId, string? SlackChannel, string? SlackTimestamp)? RestartRequest { get; private set; }

        public Task<IncidentRecord?> GetIncidentAsync(Guid incidentId, CancellationToken cancellationToken) =>
            Task.FromResult<IncidentRecord?>(null);

        public Task<InvestigationReport?> GetReportAsync(Guid incidentId, CancellationToken cancellationToken) =>
            Task.FromResult<InvestigationReport?>(null);

        public Task<int> SaveReportAsync(
            IncidentRecord incident,
            InvestigationReport report,
            CancellationToken cancellationToken) => Task.FromResult(0);

        public Task SetStatusAsync(Guid incidentId, string status, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<bool> RestartInvestigationAsync(
            Guid incidentId,
            string? slackChannel,
            string? slackTimestamp,
            CancellationToken cancellationToken)
        {
            RestartRequest = (incidentId, slackChannel, slackTimestamp);
            return Task.FromResult(restartResult);
        }

        public Task<(Guid IncidentId, bool IsDuplicate)> AcceptWebhookAsync(
            PagerDutyWebhookEvent webhook,
            InvestigationProfile profile,
            ReadOnlyMemory<byte> rawPayload,
            CancellationToken cancellationToken) => Task.FromResult((Guid.NewGuid(), false));

        public Task SetSlackTimestampAsync(Guid incidentId, string timestamp, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken) =>
            Task.FromResult(0);
    }
}
