using System.Security.Claims;
using Panko.Api.Cases;
using Panko.Api.Options;
using Microsoft.Extensions.Options;

namespace Panko.Api.Security;

public enum CaseAccessKind
{
    CaseFile,
    Status,
    Trail,
    Crumbs,
    LiveUpdates,
    Export
}

public sealed record CaseAccessGrant(
    CaseFileState State,
    TeamAccessScope Scope);

public interface ICaseAccessAuthorizer
{
    Task<CaseAccessGrant?> AuthorizeAsync(
        ClaimsPrincipal principal,
        Guid caseId,
        CaseAccessKind kind,
        CancellationToken cancellationToken);
}

public sealed class CaseAccessAuthorizer(
    ICaseFileReader caseFiles,
    ITeamAuthorization teams,
    ICaseAuthorization cases,
    IOptions<JwtIdentityOptions> identityOptions,
    ISecurityAuditTrail audit) : ICaseAccessAuthorizer
{
    public async Task<CaseAccessGrant?> AuthorizeAsync(
        ClaimsPrincipal principal,
        Guid caseId,
        CaseAccessKind kind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var scope = teams.ResolveScope(principal);
        var actor = SecurityAuditActor.FromPrincipal(principal, scope);
        var state = await caseFiles.GetAsync(caseId, cancellationToken);
        if (state is null)
        {
            await audit.RecordAsync(Event("not_found", actor, null, null, caseId, kind), cancellationToken);
            return null;
        }

        var allowed = scope.Allows(state.Team);
        if (allowed && (identityOptions.Value.Required || principal.Identity?.IsAuthenticated == true))
        {
            try
            {
                await cases.AuthorizeTeamAsync(
                    principal,
                    state.Team,
                    CasePermission.Read,
                    cancellationToken);
            }
            catch (CaseAuthorizationException)
            {
                allowed = false;
            }
        }

        if (!allowed)
        {
            await audit.RecordAsync(
                Event("denied", actor, state.Team, state.RecipeId, caseId, kind),
                cancellationToken);
            return null;
        }

        await audit.RecordAsync(
            Event("allowed", actor, state.Team, state.RecipeId, caseId, kind),
            cancellationToken);
        return new CaseAccessGrant(state, scope);
    }

    private static SecurityAuditEvent Event(
        string outcome,
        SecurityAuditActor actor,
        string? team,
        string? recipeId,
        Guid caseId,
        CaseAccessKind kind) => new(
        Action(kind),
        outcome,
        actor,
        team,
        recipeId,
        caseId,
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["surface"] = Surface(kind)
        });

    private static string Action(CaseAccessKind kind) => kind switch
    {
        CaseAccessKind.Crumbs => SecurityAuditActions.CrumbAccess,
        CaseAccessKind.Export => SecurityAuditActions.CaseFileExport,
        _ => SecurityAuditActions.CaseFileAccess
    };

    private static string Surface(CaseAccessKind kind) => kind switch
    {
        CaseAccessKind.LiveUpdates => "signalr",
        _ => kind.ToString().ToLowerInvariant()
    };
}
