using System.Security.Claims;
using Panko.Api.Domain;
using Panko.Api.Options;
using Microsoft.Extensions.Options;

namespace Panko.Api.Security;

public sealed record RecipeOwnership(
    string RecipeId,
    string Team,
    string PagerDutyServiceId,
    string ServiceCollection = ServiceCollectionKey.Default);

public interface IRecipeOwnershipCatalog
{
    IReadOnlyList<RecipeOwnership> All { get; }
    bool TryGet(string recipeId, out RecipeOwnership ownership);
}

public sealed class DemoRecipeOwnershipCatalog : IRecipeOwnershipCatalog
{
    private static readonly RecipeOwnership Demo = new(
        "payments-production",
        "payments",
        "payments-api",
        "payments-platform");

    public IReadOnlyList<RecipeOwnership> All { get; } = [Demo];

    public bool TryGet(string recipeId, out RecipeOwnership ownership)
    {
        if (string.Equals(recipeId, Demo.RecipeId, StringComparison.Ordinal))
        {
            ownership = Demo;
            return true;
        }

        ownership = null!;
        return false;
    }
}

public sealed class TeamAccessScope
{
    private readonly HashSet<string> teams;

    private TeamAccessScope(IEnumerable<string> teams, bool unrestricted)
    {
        this.teams = teams.ToHashSet(StringComparer.Ordinal);
        IsUnrestricted = unrestricted;
    }

    public static TeamAccessScope Unrestricted { get; } = new([], unrestricted: true);

    public static TeamAccessScope Restricted(IEnumerable<string> teams) => new(teams, unrestricted: false);

    public bool IsUnrestricted { get; }

    public IReadOnlySet<string> Teams => teams;

    public bool Allows(string? team) =>
        !string.IsNullOrWhiteSpace(team)
        && !string.Equals(team, TeamKey.Unmapped, StringComparison.Ordinal)
        && (IsUnrestricted || teams.Contains(team));
}

public sealed record TeamAuthorizationGrant(
    RecipeOwnership Ownership,
    TeamAccessScope Scope);

public interface ITeamAuthorization
{
    TeamAccessScope ResolveScope(ClaimsPrincipal principal);

    TeamAuthorizationGrant? TryAuthorizeRecipe(ClaimsPrincipal principal, string recipeId);

    IReadOnlyList<string> AuthorizedPagerDutyServiceIds(TeamAccessScope scope);
}

public sealed class TeamAuthorization(
    IRecipeOwnershipCatalog recipes,
    IOptions<JwtIdentityOptions> identityOptions,
    IOptions<TeamAuthorizationOptions> authorizationOptions) : ITeamAuthorization
{
    public TeamAccessScope ResolveScope(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (!identityOptions.Value.Required)
        {
            return TeamAccessScope.Unrestricted;
        }

        if (principal.Identity?.IsAuthenticated != true)
        {
            return TeamAccessScope.Restricted([]);
        }

        var options = authorizationOptions.Value;
        var teams = new HashSet<string>(StringComparer.Ordinal);
        foreach (var claim in principal.Claims)
        {
            if (options.TeamClaimTypes.Contains(claim.Type, StringComparer.Ordinal)
                && TeamKey.IsCanonical(claim.Value))
            {
                teams.Add(claim.Value);
            }

            if (options.GroupClaimTypes.Contains(claim.Type, StringComparer.Ordinal)
                && options.GroupTeamMappings.TryGetValue(claim.Value, out var mappedTeam)
                && TeamKey.IsCanonical(mappedTeam))
            {
                teams.Add(mappedTeam);
            }
        }

        return TeamAccessScope.Restricted(teams);
    }

    public TeamAuthorizationGrant? TryAuthorizeRecipe(ClaimsPrincipal principal, string recipeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipeId);
        var scope = ResolveScope(principal);
        return recipes.TryGet(recipeId, out var ownership) && scope.Allows(ownership.Team)
            ? new TeamAuthorizationGrant(ownership, scope)
            : null;
    }

    public IReadOnlyList<string> AuthorizedPagerDutyServiceIds(TeamAccessScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return recipes.All
            .Where(recipe => scope.Allows(recipe.Team))
            .Select(recipe => recipe.PagerDutyServiceId)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }
}
