using System.Collections.Concurrent;
using Panko.Api.Options;
using Panko.Api.Security;
using Microsoft.Extensions.Options;

namespace Panko.Api.Cases;

public sealed class CaseRunRegistry
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> activeRuns = [];

    public bool TryBegin(
        Guid caseId,
        CancellationToken hostCancellationToken,
        out CancellationTokenSource runCancellation)
    {
        runCancellation = CancellationTokenSource.CreateLinkedTokenSource(hostCancellationToken);
        if (activeRuns.TryAdd(caseId, runCancellation))
        {
            return true;
        }

        runCancellation.Dispose();
        runCancellation = null!;
        return false;
    }

    public bool Cancel(Guid caseId) =>
        activeRuns.TryGetValue(caseId, out var run) && Cancel(run);

    public void Complete(Guid caseId, CancellationTokenSource runCancellation)
    {
        if (activeRuns.TryGetValue(caseId, out var active) && ReferenceEquals(active, runCancellation))
        {
            activeRuns.TryRemove(caseId, out _);
        }

        runCancellation.Dispose();
    }

    private static bool Cancel(CancellationTokenSource run)
    {
        if (run.IsCancellationRequested)
        {
            return false;
        }

        run.Cancel();
        return true;
    }
}

public sealed record SlackRebuildRequest(
    Guid CaseId,
    string WorkspaceId,
    string UserId,
    string ChannelId,
    string MessageTimestamp);

public sealed class CaseRebuildService(
    ICaseStore repository,
    CaseRunRegistry runs,
    IOptions<SlackOptions> options,
    ISecurityAuditTrail audit,
    ILogger<CaseRebuildService> logger)
{
    public async Task<bool> RebuildAsync(
        SlackRebuildRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.UserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ChannelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MessageTimestamp);

        var channelTeam = SlackChannelAuthorization.ResolveTeam(
            options.Value,
            request.ChannelId);
        if (channelTeam is null)
        {
            await audit.RecordAsync(
                RebuildAudit("denied", "channel_unmapped", request, null, null, null),
                cancellationToken);
            return false;
        }

        var caseRecord = await repository.GetCaseAsync(request.CaseId, cancellationToken);
        if (caseRecord is null)
        {
            await audit.RecordAsync(
                RebuildAudit(
                    "not_found",
                    "case_not_found",
                    request,
                    channelTeam,
                    channelTeam,
                    null),
                cancellationToken);
            return false;
        }

        var channelScope = TeamAccessScope.Restricted([channelTeam]);
        if (!channelScope.Allows(caseRecord.Team))
        {
            await audit.RecordAsync(
                RebuildAudit(
                    "denied",
                    "team_mismatch",
                    request,
                    channelTeam,
                    caseRecord.Team,
                    caseRecord.RecipeId),
                cancellationToken);
            return false;
        }

        if (!string.Equals(caseRecord.SlackChannel, request.ChannelId, StringComparison.Ordinal) ||
            !string.Equals(caseRecord.SlackTimestamp, request.MessageTimestamp, StringComparison.Ordinal))
        {
            await audit.RecordAsync(
                RebuildAudit(
                    "denied",
                    "message_mismatch",
                    request,
                    channelTeam,
                    caseRecord.Team,
                    caseRecord.RecipeId),
                cancellationToken);
            return false;
        }

        // The durable audit write is deliberately completed before the rebuild mutation.
        await audit.RecordAsync(
            RebuildAudit(
                "allowed",
                "authorized",
                request,
                channelTeam,
                caseRecord.Team,
                caseRecord.RecipeId),
            cancellationToken);
        var rebuilt = await repository.RebuildCaseAsync(
            request.CaseId,
            request.ChannelId,
            request.MessageTimestamp,
            cancellationToken);
        if (!rebuilt)
        {
            return false;
        }

        var cancelled = runs.Cancel(request.CaseId);
        logger.LogWarning(
            "Case File rebuild requested for Case {CaseId}; active run cancelled: {ActiveRunCancelled}",
            request.CaseId, cancelled);
        return true;
    }

    private static SecurityAuditEvent RebuildAudit(
        string outcome,
        string reason,
        SlackRebuildRequest request,
        string? actorTeam,
        string? targetTeam,
        string? recipeId) => new(
        SecurityAuditActions.CaseRebuildRequested,
        outcome,
        SecurityAuditActor.Slack(request.WorkspaceId, request.UserId, actorTeam),
        targetTeam,
        recipeId,
        request.CaseId,
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["channel_id"] = request.ChannelId,
            ["message_timestamp"] = request.MessageTimestamp,
            ["reason"] = reason
        });
}
