using System.Security.Claims;
using Panko.Api.Cases;
using Panko.Api.Options;
using Panko.Api.Security;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Panko.Api.Tests;

public sealed class CaseAuthorizationTests
{
    private const string RecipeId = "payments-production";

    [Theory]
    [InlineData(CasePermission.Create, "case:create")]
    [InlineData(CasePermission.Read, "case:read")]
    [InlineData(CasePermission.Append, "case:append")]
    [InlineData(CasePermission.Rebuild, "case:rebuild")]
    [InlineData(CasePermission.RefreshSources, "case:refresh-sources")]
    [InlineData(CasePermission.Close, "case:close")]
    public async Task ExactPermissionAndRecipeClaimsAuthorizeOnlyTheNamedOperation(
        CasePermission permission,
        string permissionClaim)
    {
        var authorization = Authorization();
        var principal = Principal(
            new Claim(CaseAuthorization.PermissionClaimType, permissionClaim),
            new Claim("panko:team", "payments"));

        await authorization.AuthorizeRecipeAsync(
            principal,
            RecipeId,
            permission,
            CancellationToken.None);

        Assert.Equal(permissionClaim, CaseAuthorization.PermissionName(permission));
    }

    [Fact]
    public async Task AuthorizationIsDenyByDefaultForMissingIdentityPermissionOrRecipe()
    {
        var authorization = Authorization();
        var unauthenticated = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "agent@example.internal")]));
        var noClaims = Principal();
        var permissionOnly = Principal(
            new Claim(CaseAuthorization.PermissionClaimType, "case:append"));
        var recipeOnly = Principal(new Claim("panko:team", "payments"));

        await Assert.ThrowsAsync<CaseAuthorizationException>(() => authorization.AuthorizeRecipeAsync(
            unauthenticated, RecipeId, CasePermission.Append, CancellationToken.None));
        await Assert.ThrowsAsync<CaseAuthorizationException>(() => authorization.AuthorizeRecipeAsync(
            noClaims, RecipeId, CasePermission.Append, CancellationToken.None));
        await Assert.ThrowsAsync<CaseAuthorizationException>(() => authorization.AuthorizeRecipeAsync(
            permissionOnly, RecipeId, CasePermission.Append, CancellationToken.None));
        await Assert.ThrowsAsync<CaseAuthorizationException>(() => authorization.AuthorizeRecipeAsync(
            recipeOnly, RecipeId, CasePermission.Append, CancellationToken.None));
    }

    [Fact]
    public async Task PermissionAndRecipeClaimsAreBothScopedAndCaseSensitive()
    {
        var authorization = Authorization();
        var wrongPermission = Principal(
            new Claim(CaseAuthorization.PermissionClaimType, "case:read"),
            new Claim("panko:team", "payments"));
        var wrongRecipe = Principal(
            new Claim(CaseAuthorization.PermissionClaimType, "case:append"),
            new Claim("panko:team", "search"));
        var wrongPermissionCase = Principal(
            new Claim(CaseAuthorization.PermissionClaimType, "Case:Append"),
            new Claim("panko:team", "payments"));
        var wrongTeamCase = Principal(
            new Claim(CaseAuthorization.PermissionClaimType, "case:append"),
            new Claim("panko:team", "Payments"));

        await Assert.ThrowsAsync<CaseAuthorizationException>(() => authorization.AuthorizeRecipeAsync(
            wrongPermission, RecipeId, CasePermission.Append, CancellationToken.None));
        await Assert.ThrowsAsync<CaseAuthorizationException>(() => authorization.AuthorizeRecipeAsync(
            wrongRecipe, RecipeId, CasePermission.Append, CancellationToken.None));
        await Assert.ThrowsAsync<CaseAuthorizationException>(() => authorization.AuthorizeRecipeAsync(
            wrongPermissionCase, RecipeId, CasePermission.Append, CancellationToken.None));
        await Assert.ThrowsAsync<CaseAuthorizationException>(() => authorization.AuthorizeRecipeAsync(
            wrongTeamCase, RecipeId, CasePermission.Append, CancellationToken.None));
    }

    [Fact]
    public async Task PermissionWildcardStillRequiresAnAuthorizedRecipeTeam()
    {
        var authorization = Authorization();
        var wildcardPermission = Principal(
            new Claim(CaseAuthorization.PermissionClaimType, "*"),
            new Claim("panko:team", "payments"));
        var wildcardWithoutTeam = Principal(
            new Claim(CaseAuthorization.PermissionClaimType, "*"));

        await authorization.AuthorizeRecipeAsync(
            wildcardPermission,
            RecipeId,
            CasePermission.RefreshSources,
            CancellationToken.None);
        await Assert.ThrowsAsync<CaseAuthorizationException>(() => authorization.AuthorizeRecipeAsync(
            wildcardWithoutTeam,
            RecipeId,
            CasePermission.Close,
            CancellationToken.None));
    }

    [Fact]
    public async Task ConfiguredGroupClaimsAuthorizeOnlyRecipesOwnedByTheirMappedTeam()
    {
        var authorization = Authorization();
        var principal = Principal(
            new Claim(CaseAuthorization.PermissionClaimType, "case:read"),
            new Claim("groups", "payments-responders"));

        await authorization.AuthorizeRecipeAsync(
            principal,
            RecipeId,
            CasePermission.Read,
            CancellationToken.None);
        await Assert.ThrowsAsync<CaseAuthorizationException>(() => authorization.AuthorizeRecipeAsync(
            principal,
            "search-production",
            CasePermission.Read,
            CancellationToken.None));
    }

    [Fact]
    public async Task PersistedTeamAuthorizationRequiresTheExactPermissionAndTeam()
    {
        var authorization = Authorization();
        var allowed = Principal(
            new Claim(CaseAuthorization.PermissionClaimType, "case:read"),
            new Claim("panko:team", "payments"));
        var wrongTeam = Principal(
            new Claim(CaseAuthorization.PermissionClaimType, "case:read"),
            new Claim("panko:team", "search"));

        await authorization.AuthorizeTeamAsync(
            allowed,
            "payments",
            CasePermission.Read,
            CancellationToken.None);
        await Assert.ThrowsAsync<CaseAuthorizationException>(() => authorization.AuthorizeTeamAsync(
            wrongTeam,
            "payments",
            CasePermission.Read,
            CancellationToken.None));
        await Assert.ThrowsAsync<CaseAuthorizationException>(() => authorization.AuthorizeTeamAsync(
            allowed,
            TeamKey.Unmapped,
            CasePermission.Read,
            CancellationToken.None));
    }

    [Fact]
    public async Task SubjectClaimMayIdentifyACallerWithoutANameClaim()
    {
        var authorization = Authorization();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("sub", "agent-service-account"),
                new Claim(CaseAuthorization.PermissionClaimType, "case:read"),
                new Claim("panko:team", "payments")
            ],
            "test"));

        await authorization.AuthorizeRecipeAsync(
            principal,
            RecipeId,
            CasePermission.Read,
            CancellationToken.None);
    }

    [Fact]
    public async Task AuthenticatedIdentityWithoutANameOrSubjectIsRejected()
    {
        var authorization = Authorization();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(CaseAuthorization.PermissionClaimType, "*"),
                new Claim("panko:team", "payments")
            ],
            "test"));

        await Assert.ThrowsAsync<CaseAuthorizationException>(() => authorization.AuthorizeRecipeAsync(
            principal,
            RecipeId,
            CasePermission.Read,
            CancellationToken.None));
    }

    [Fact]
    public async Task CancellationIsObservedBeforeAuthorizationWork()
    {
        var authorization = Authorization();
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => authorization.AuthorizeRecipeAsync(
            Principal(),
            RecipeId,
            CasePermission.Read,
            source.Token));
    }

    [Fact]
    public async Task DevelopmentOpenAccessAuthorizesEveryConfiguredTeamAndPermission()
    {
        var teams = new TeamAuthorization(
            new RecipeCatalog(),
            OptionsFactory.Create(new JwtIdentityOptions { Required = false }),
            OptionsFactory.Create(new TeamAuthorizationOptions()));
        var authorization = new CaseAuthorization(teams);
        var principal = DevelopmentOpenAccessIdentity.CreatePrincipal();

        foreach (var permission in Enum.GetValues<CasePermission>())
        {
            await authorization.AuthorizeRecipeAsync(
                principal,
                "search-production",
                permission,
                CancellationToken.None);
            await authorization.AuthorizeTeamAsync(
                principal,
                "payments",
                permission,
                CancellationToken.None);
        }

        Assert.Equal(
            DevelopmentOpenAccessIdentity.Subject,
            new CallerIdentity(principal).PrincipalName);
    }

    private static CaseAuthorization Authorization()
    {
        var teams = new TeamAuthorization(
            new RecipeCatalog(),
            OptionsFactory.Create(new JwtIdentityOptions { Required = true }),
            OptionsFactory.Create(new TeamAuthorizationOptions
            {
                TeamClaimTypes = ["panko:team"],
                GroupClaimTypes = ["groups"],
                GroupTeamMappings = new Dictionary<string, string>
                {
                    ["payments-responders"] = "payments"
                }
            }));
        return new CaseAuthorization(teams);
    }

    private static ClaimsPrincipal Principal(params Claim[] claims)
    {
        var allClaims = new[] { new Claim(ClaimTypes.Name, "agent@example.internal") }
            .Concat(claims);
        return new ClaimsPrincipal(new ClaimsIdentity(allClaims, "Bearer"));
    }

    private sealed class RecipeCatalog : IRecipeOwnershipCatalog
    {
        public IReadOnlyList<RecipeOwnership> All { get; } =
        [
            new(RecipeId, "payments", "P123PAYMENTS"),
            new("search-production", "search", "P123SEARCH")
        ];

        public bool TryGet(string recipeId, out RecipeOwnership ownership)
        {
            var match = All.SingleOrDefault(item => item.RecipeId == recipeId);
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
