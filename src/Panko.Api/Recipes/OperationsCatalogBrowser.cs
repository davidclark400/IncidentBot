using System.Security.Claims;
using Panko.Api.Security;
using Panko.Contracts;

namespace Panko.Api.Recipes;

public sealed class OperationsCatalogBrowser(
    IRecipeOwnershipCatalog recipes,
    ITeamAuthorization authorization)
{
    public OperationsCatalog Browse(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var scope = authorization.ResolveScope(principal);
        var teams = recipes.All
            .Where(recipe => scope.Allows(recipe.Team))
            .GroupBy(recipe => recipe.Team, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(team => new TeamCatalogItem(
                team.Key,
                team.GroupBy(recipe => recipe.ServiceCollection, StringComparer.Ordinal)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .Select(collection => new ServiceCollectionCatalogItem(
                        collection.Key,
                        collection.OrderBy(recipe => recipe.RecipeId, StringComparer.Ordinal)
                            .Select(recipe => new ObservedServiceCatalogItem(
                                recipe.RecipeId,
                                recipe.PagerDutyServiceId))
                            .ToArray()))
                    .ToArray()))
            .ToArray();
        return new OperationsCatalog(teams);
    }
}
