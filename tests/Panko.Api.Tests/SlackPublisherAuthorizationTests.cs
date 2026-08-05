using Panko.Api.Domain;
using Panko.Api.Cases;
using Panko.Api.Infrastructure;
using Panko.Api.Options;
using Panko.Api.Security;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Panko.Api.Tests;

public sealed class SlackPublisherAuthorizationTests
{
    [Fact]
    public async Task ChannelTeamMismatchIsAuditedAndBlocksCaseFileReadAndDelivery()
    {
        var caseId = Guid.NewGuid();
        var store = new RecordingCaseStore(new CaseRecord(
            caseId,
            "PD123",
            "payments-api",
            "payments-production",
            "Payment timeouts",
            "high",
            PagerDutyIncidentState.Triggered,
            DateTimeOffset.Parse("2026-08-03T09:45:00Z"),
            DateTimeOffset.Parse("2026-08-03T09:50:00Z"),
            1,
            "ready",
            false,
            null,
            "C_SEARCH",
            null,
            new Dictionary<string, string>())
        {
            Team = "payments"
        });
        var http = new RecordingHttpClientFactory();
        var audit = new RecordingAuditTrail();
        var publisher = new SlackPublisher(
            http,
            store,
            OptionsFactory.Create(new SlackOptions
            {
                Enabled = true,
                ChannelTeams = new Dictionary<string, string>
                {
                    ["C_SEARCH"] = "search"
                }
            }),
            OptionsFactory.Create(new PankoOptions()),
            new StubCredentialProvider(),
            audit,
            TimeProvider.System,
            NullLogger<SlackPublisher>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => publisher.PublishAsync(caseId, CancellationToken.None));

        Assert.Contains("not authorized", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, store.CaseFileReads);
        Assert.Equal(0, http.CreateClientCalls);
        var auditEvent = Assert.Single(audit.Events);
        Assert.Equal(SecurityAuditActions.CaseFileAccess, auditEvent.Action);
        Assert.Equal("denied", auditEvent.Outcome);
        Assert.Equal("payments", auditEvent.TargetTeam);
        Assert.Equal("slack-publication", auditEvent.Metadata?["surface"]);
        Assert.Equal("C_SEARCH", auditEvent.Metadata?["channel_id"]);
    }

    private sealed class RecordingCaseStore(CaseRecord caseRecord) : ICaseStore
    {
        public int CaseFileReads { get; private set; }

        public Task<CaseRecord?> GetCaseAsync(
            Guid caseId,
            CancellationToken cancellationToken) =>
            Task.FromResult<CaseRecord?>(caseRecord.Id == caseId ? caseRecord : null);

        public Task<CaseFile?> GetCaseFileAsync(
            Guid caseId,
            CancellationToken cancellationToken)
        {
            CaseFileReads++;
            return Task.FromResult<CaseFile?>(null);
        }

        public Task<(Guid CaseId, bool IsDuplicate)> AcceptOriginEventAsync(
            AcceptCaseOriginEvent originEvent,
            Recipe recipe,
            CaseOriginEventReceipt receipt,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<CaseProgress?> GetProgressAsync(
            Guid caseId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<long?> BeginProgressAsync(
            CaseProgress progress,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<long?> UpdateProgressAsync(
            CaseProgress progress,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> SaveCaseFileAsync(
            CaseRecord caseRecord,
            CaseFile caseFile,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SetStatusAsync(
            Guid caseId,
            string status,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> RebuildCaseAsync(
            Guid caseId,
            string slackChannel,
            string slackTimestamp,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SetSlackTimestampAsync(
            Guid caseId,
            string timestamp,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> PurgeOlderThanAsync(
            DateTimeOffset cutoff,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingHttpClientFactory : IHttpClientFactory
    {
        public int CreateClientCalls { get; private set; }

        public HttpClient CreateClient(string name)
        {
            CreateClientCalls++;
            return new HttpClient();
        }
    }

    private sealed class StubCredentialProvider : ICredentialProvider
    {
        public string? Get(string environmentVariableName) => "unused";
    }

    private sealed class RecordingAuditTrail : ISecurityAuditTrail
    {
        public List<SecurityAuditEvent> Events { get; } = [];

        public Task RecordAsync(
            SecurityAuditEvent auditEvent,
            CancellationToken cancellationToken)
        {
            Events.Add(auditEvent);
            return Task.CompletedTask;
        }
    }
}
