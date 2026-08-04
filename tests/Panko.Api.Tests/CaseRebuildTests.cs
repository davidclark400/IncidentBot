using Panko.Api.Domain;
using Panko.Api.Cases;
using Panko.Api.Options;
using Panko.Api.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Panko.Api.Tests;

public sealed class CaseRebuildTests
{
    [Fact]
    public async Task SuccessfulRebuildForwardsSlackIdentityAndCancelsTheActiveRun()
    {
        var caseId = Guid.NewGuid();
        var operations = new List<string>();
        var repository = new RecordingRepository(BuildCase(caseId), rebuildResult: true, operations);
        var audit = new RecordingAudit(operations);
        var runs = new CaseRunRegistry();
        Assert.True(runs.TryBegin(caseId, CancellationToken.None, out var activeRun));
        var service = new CaseRebuildService(
            repository,
            runs,
            SlackOptions(),
            audit,
            NullLogger<CaseRebuildService>.Instance);

        var rebuilt = await service.RebuildAsync(
            Request(caseId),
            CancellationToken.None);

        Assert.True(rebuilt);
        Assert.True(activeRun.IsCancellationRequested);
        Assert.Equal((caseId, "C123", "171234.5678"), repository.RebuildRequest);
        Assert.Equal(["audit", "mutation"], operations);
        var auditEvent = Assert.Single(audit.Events);
        Assert.Equal(SecurityAuditActions.CaseRebuildRequested, auditEvent.Action);
        Assert.Equal("allowed", auditEvent.Outcome);
        Assert.Equal("slack:T123:U123", auditEvent.Actor.Id);
        Assert.Equal(["payments"], auditEvent.Actor.Teams);
        Assert.Equal("payments", auditEvent.TargetTeam);
        Assert.Equal("payments-production", auditEvent.RecipeId);
        Assert.Equal(caseId, auditEvent.CaseId);
        runs.Complete(caseId, activeRun);
    }

    [Fact]
    public async Task RejectedRebuildLeavesTheActiveRunAlone()
    {
        var caseId = Guid.NewGuid();
        var repository = new RecordingRepository(BuildCase(caseId), rebuildResult: false);
        var runs = new CaseRunRegistry();
        Assert.True(runs.TryBegin(caseId, CancellationToken.None, out var activeRun));
        var service = new CaseRebuildService(
            repository,
            runs,
            SlackOptions(),
            new RecordingAudit(),
            NullLogger<CaseRebuildService>.Instance);

        var rebuilt = await service.RebuildAsync(
            Request(caseId),
            CancellationToken.None);

        Assert.False(rebuilt);
        Assert.False(activeRun.IsCancellationRequested);
        runs.Complete(caseId, activeRun);
    }

    [Fact]
    public async Task CrossTeamRebuildIsAuditedAndRejectedBeforeMutation()
    {
        var caseId = Guid.NewGuid();
        var operations = new List<string>();
        var repository = new RecordingRepository(
            BuildCase(caseId) with { Team = "orders" },
            rebuildResult: true,
            operations);
        var audit = new RecordingAudit(operations);
        var service = new CaseRebuildService(
            repository,
            new CaseRunRegistry(),
            SlackOptions(),
            audit,
            NullLogger<CaseRebuildService>.Instance);

        var rebuilt = await service.RebuildAsync(Request(caseId), CancellationToken.None);

        Assert.False(rebuilt);
        Assert.Null(repository.RebuildRequest);
        Assert.Equal(["audit"], operations);
        var auditEvent = Assert.Single(audit.Events);
        Assert.Equal("denied", auditEvent.Outcome);
        Assert.Equal(["payments"], auditEvent.Actor.Teams);
        Assert.Equal("orders", auditEvent.TargetTeam);
        Assert.Equal("team_mismatch", auditEvent.Metadata!["reason"]);
    }

    [Fact]
    public async Task SlackIdentityAndMessageCoordinatesAreMandatory()
    {
        var caseId = Guid.NewGuid();
        var repository = new RecordingRepository(BuildCase(caseId), rebuildResult: true);
        var audit = new RecordingAudit();
        var service = new CaseRebuildService(
            repository,
            new CaseRunRegistry(),
            SlackOptions(),
            audit,
            NullLogger<CaseRebuildService>.Instance);
        var request = Request(caseId);
        var invalid = new[]
        {
            request with { WorkspaceId = "" },
            request with { UserId = "" },
            request with { ChannelId = "" },
            request with { MessageTimestamp = "" }
        };

        foreach (var candidate in invalid)
        {
            await Assert.ThrowsAsync<ArgumentException>(() =>
                service.RebuildAsync(candidate, CancellationToken.None));
        }

        Assert.Null(repository.RebuildRequest);
        Assert.Empty(audit.Events);
    }

    private static IOptions<SlackOptions> SlackOptions() =>
        Microsoft.Extensions.Options.Options.Create(new SlackOptions
        {
            ChannelTeams = new Dictionary<string, string>
            {
                ["C123"] = "payments"
            }
        });

    private static SlackRebuildRequest Request(Guid caseId) => new(
        caseId,
        "T123",
        "U123",
        "C123",
        "171234.5678");

    private static CaseRecord BuildCase(Guid caseId) => new(
        caseId,
        "PD123",
        "payments-api",
        "payments-production",
        "Payment failures",
        "high",
        PagerDutyIncidentState.Triggered,
        DateTimeOffset.Parse("2026-07-13T10:00:00Z"),
        DateTimeOffset.Parse("2026-07-13T10:05:00Z"),
        1,
        CaseProgression.Ready,
        false,
        null,
        "C123",
        "171234.5678",
        new Dictionary<string, string>())
    {
        Team = "payments"
    };

    private sealed class RecordingRepository(
        CaseRecord storedCase,
        bool rebuildResult,
        List<string>? operations = null) : ICaseStore
    {
        public (Guid CaseId, string SlackChannel, string SlackTimestamp)? RebuildRequest { get; private set; }

        public Task<CaseRecord?> GetCaseAsync(Guid caseId, CancellationToken cancellationToken) =>
            Task.FromResult<CaseRecord?>(caseId == storedCase.Id ? storedCase : null);

        public Task<CaseFile?> GetCaseFileAsync(Guid caseId, CancellationToken cancellationToken) =>
            Task.FromResult<CaseFile?>(null);

        public Task<CaseProgress?> GetProgressAsync(
            Guid caseId,
            CancellationToken cancellationToken) =>
            Task.FromResult<CaseProgress?>(null);

        public Task<long?> BeginProgressAsync(
            CaseProgress progress,
            CancellationToken cancellationToken) => Task.FromResult<long?>(1);

        public Task<long?> UpdateProgressAsync(
            CaseProgress progress,
            CancellationToken cancellationToken) => Task.FromResult<long?>(progress.Revision + 1);

        public Task<int> SaveCaseFileAsync(
            CaseRecord caseRecord,
            CaseFile caseFile,
            CancellationToken cancellationToken) => Task.FromResult(0);

        public Task SetStatusAsync(Guid caseId, string status, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<bool> RebuildCaseAsync(
            Guid caseId,
            string slackChannel,
            string slackTimestamp,
            CancellationToken cancellationToken)
        {
            operations?.Add("mutation");
            RebuildRequest = (caseId, slackChannel, slackTimestamp);
            return Task.FromResult(rebuildResult);
        }

        public Task<(Guid CaseId, bool IsDuplicate)> AcceptOriginEventAsync(
            AcceptCaseOriginEvent originEvent,
            Recipe recipe,
            CaseOriginEventReceipt receipt,
            CancellationToken cancellationToken) => Task.FromResult((Guid.NewGuid(), false));

        public Task SetSlackTimestampAsync(Guid caseId, string timestamp, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken) =>
            Task.FromResult(0);
    }

    private sealed class RecordingAudit(List<string>? operations = null) : ISecurityAuditTrail
    {
        public List<SecurityAuditEvent> Events { get; } = [];

        public Task RecordAsync(SecurityAuditEvent auditEvent, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            operations?.Add("audit");
            Events.Add(auditEvent);
            return Task.CompletedTask;
        }
    }
}
