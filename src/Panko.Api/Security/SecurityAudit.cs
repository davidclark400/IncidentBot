using System.Security.Claims;

namespace Panko.Api.Security;

public static class SecurityAuditActions
{
    public const string CaseFileAccess = "case-file.access";
    public const string CrumbAccess = "crumb.access";
    public const string CaseRebuildRequested = "case.rebuild.requested";
    public const string SlackPrompt = "slack.prompt";
    public const string CaseFileExport = "case-file.export";
}

public sealed record SecurityAuditActor(
    string Id,
    string AuthenticationSource,
    IReadOnlyList<string> Teams)
{
    public static SecurityAuditActor FromPrincipal(ClaimsPrincipal principal, TeamAccessScope scope)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(scope);
        var id = principal.FindFirstValue("sub")
            ?? principal.Identity?.Name
            ?? "anonymous";
        var source = principal.Identity?.AuthenticationType ?? "anonymous";
        var teams = scope.IsUnrestricted
            ? Array.Empty<string>()
            : scope.Teams.Order(StringComparer.Ordinal).ToArray();
        return new SecurityAuditActor(id, source, teams);
    }

    public static SecurityAuditActor Slack(string workspaceId, string userId, string? team) => new(
        $"slack:{workspaceId}:{userId}",
        "slack-socket-mode",
        string.IsNullOrWhiteSpace(team) ? [] : [team]);
}

public sealed record SecurityAuditEvent(
    string Action,
    string Outcome,
    SecurityAuditActor Actor,
    string? TargetTeam = null,
    string? RecipeId = null,
    Guid? CaseId = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

public interface ISecurityAuditTrail
{
    Task RecordAsync(SecurityAuditEvent auditEvent, CancellationToken cancellationToken);
}

public sealed class LoggingSecurityAuditTrail(
    ILogger<LoggingSecurityAuditTrail> logger) : ISecurityAuditTrail
{
    public Task RecordAsync(SecurityAuditEvent auditEvent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        logger.LogInformation(
            "Security audit {AuditAction} {AuditOutcome} by {AuditActor} for team {TargetTeam}, Recipe {RecipeId}, Case {CaseId}",
            auditEvent.Action,
            auditEvent.Outcome,
            auditEvent.Actor.Id,
            auditEvent.TargetTeam,
            auditEvent.RecipeId,
            auditEvent.CaseId);
        return Task.CompletedTask;
    }
}
