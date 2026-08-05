using System.Security.Claims;
using Panko.Api.Options;
using Panko.Api.Recipes;
using Panko.Api.Security;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Panko.Api.Tests;

public sealed class OperationsCatalogTests
{
    [Fact]
    public void BrowseReturnsOnlyAuthorizedTeamsWithStableCollectionAndServiceOrdering()
    {
        var recipes = new RecipeCatalog();
        var teams = Authorization(recipes, required: true);
        var browser = new OperationsCatalogBrowser(recipes, teams);
        var principal = Principal(new Claim("panko:team", "payments"));

        var catalog = browser.Browse(principal);

        var team = Assert.Single(catalog.Teams);
        Assert.Equal("payments", team.Id);
        var collection = Assert.Single(team.ServiceCollections);
        Assert.Equal("payments-platform", collection.Id);
        Assert.Equal(
            ["payments-api-production", "payments-worker-production"],
            collection.Services.Select(service => service.RecipeId));
        Assert.DoesNotContain(
            catalog.Teams,
            candidate => candidate.Id == "search");
    }

    [Fact]
    public void DevelopmentOpenAccessCanBrowseEveryMappedTeamButNeverUnmappedRecipes()
    {
        var recipes = new RecipeCatalog();
        var teams = Authorization(recipes, required: false);
        var browser = new OperationsCatalogBrowser(recipes, teams);

        var catalog = browser.Browse(DevelopmentOpenAccessIdentity.CreatePrincipal());

        Assert.Equal(["payments", "search"], catalog.Teams.Select(team => team.Id));
        Assert.DoesNotContain(
            catalog.Teams.SelectMany(team => team.ServiceCollections)
                .SelectMany(collection => collection.Services),
            service => service.RecipeId == "unmapped-recipe");
    }

    [Fact]
    public void MissingTeamClaimsProduceAnEmptyCatalog()
    {
        var recipes = new RecipeCatalog();
        var browser = new OperationsCatalogBrowser(
            recipes,
            Authorization(recipes, required: true));

        var catalog = browser.Browse(Principal());

        Assert.Empty(catalog.Teams);
    }

    private static TeamAuthorization Authorization(
        IRecipeOwnershipCatalog recipes,
        bool required) => new(
        recipes,
        OptionsFactory.Create(new JwtIdentityOptions { Required = required }),
        OptionsFactory.Create(new TeamAuthorizationOptions
        {
            TeamClaimTypes = ["panko:team"]
        }));

    private static ClaimsPrincipal Principal(params Claim[] claims) => new(
        new ClaimsIdentity(
            new[] { new Claim("sub", "operator@example.internal") }.Concat(claims),
            "Bearer"));

    private sealed class RecipeCatalog : IRecipeOwnershipCatalog
    {
        public IReadOnlyList<RecipeOwnership> All { get; } =
        [
            new("payments-worker-production", "payments", "P-WORKER", "payments-platform"),
            new("search-production", "search", "P-SEARCH", "search-platform"),
            new("payments-api-production", "payments", "P-API", "payments-platform"),
            new("unmapped-recipe", TeamKey.Unmapped, "P-UNMAPPED")
        ];

        public bool TryGet(string recipeId, out RecipeOwnership ownership)
        {
            var match = All.SingleOrDefault(recipe => recipe.RecipeId == recipeId);
            if (match is null)
            {
                ownership = null!;
                return false;
            }

            ownership = match;
            return true;
        }
    }
}
