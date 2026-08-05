using System.Security.Claims;
using Panko.Api.Security;

namespace Panko.Api.Cases;

public interface ICaseAuthorization
{
    Task AuthorizeRecipeAsync(
        ClaimsPrincipal principal,
        string recipeId,
        CasePermission permission,
        CancellationToken cancellationToken);

    Task AuthorizeTeamAsync(
        ClaimsPrincipal principal,
        string team,
        CasePermission permission,
        CancellationToken cancellationToken);
}

public sealed class CaseAuthorization(ITeamAuthorization teams) : ICaseAuthorization
{
    public const string PermissionClaimType = "panko:permission";

    public Task AuthorizeRecipeAsync(
        ClaimsPrincipal principal,
        string recipeId,
        CasePermission permission,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (caller, permissionName) = RequirePermission(principal, permission);
        if (teams.TryAuthorizeRecipe(principal, recipeId) is null)
        {
            throw new CaseAuthorizationException(
                $"Caller '{caller}' is not authorized for '{permissionName}' on Recipe '{recipeId}'.");
        }

        return Task.CompletedTask;
    }

    public Task AuthorizeTeamAsync(
        ClaimsPrincipal principal,
        string team,
        CasePermission permission,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (caller, permissionName) = RequirePermission(principal, permission);
        if (!teams.ResolveScope(principal).Allows(team))
        {
            throw new CaseAuthorizationException(
                $"Caller '{caller}' is not authorized for '{permissionName}' on the Case team.");
        }

        return Task.CompletedTask;
    }

    private static (string Caller, string PermissionName) RequirePermission(
        ClaimsPrincipal principal,
        CasePermission permission)
    {
        var caller = principal.FindFirstValue("sub") ?? principal.Identity?.Name;
        if (principal.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(caller))
        {
            throw new CaseAuthorizationException("An authenticated caller identity is required.");
        }

        var permissionName = PermissionName(permission);
        var permitted = principal.Claims
            .Where(claim => claim.Type == PermissionClaimType)
            .Any(claim => claim.Value is "*"
                || string.Equals(claim.Value, permissionName, StringComparison.Ordinal));
        if (!permitted)
        {
            throw new CaseAuthorizationException(
                $"Caller '{caller}' is not authorized for '{permissionName}'.");
        }

        return (caller, permissionName);
    }

    public static string PermissionName(CasePermission permission) => permission switch
    {
        CasePermission.Create => "case:create",
        CasePermission.Read => "case:read",
        CasePermission.Append => "case:append",
        CasePermission.Rebuild => "case:rebuild",
        CasePermission.RefreshSources => "case:refresh-sources",
        CasePermission.Close => "case:close",
        _ => throw new ArgumentOutOfRangeException(nameof(permission), permission, null)
    };

}
