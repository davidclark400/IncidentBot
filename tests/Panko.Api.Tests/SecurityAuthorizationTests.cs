using System.Net;
using System.Security.Claims;
using Panko.Api.Domain;
using Panko.Api.Hubs;
using Panko.Api.Cases;
using Panko.Api.Options;
using Panko.Api.Security;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Panko.Api.Tests;

public sealed class TeamAuthorizationTests
{
    [Fact]
    public void RequiredIdentityFailsClosedForAnAnonymousCaller()
    {
        var authorization = Create(required: true);

        var scope = authorization.ResolveScope(new ClaimsPrincipal());

        Assert.False(scope.IsUnrestricted);
        Assert.Empty(scope.Teams);
        Assert.Null(authorization.TryAuthorizeRecipe(new ClaimsPrincipal(), "payments-production"));
    }

    [Fact]
    public void UnauthenticatedClaimsCannotForgeATeamScope()
    {
        var authorization = Create(required: true);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "forged@example.internal"),
            new Claim("panko:team", "payments")
        ]));

        var scope = authorization.ResolveScope(principal);

        Assert.False(scope.IsUnrestricted);
        Assert.Empty(scope.Teams);
        Assert.Null(authorization.TryAuthorizeRecipe(principal, "payments-production"));
    }

    [Fact]
    public void DisabledDevelopmentIdentityAllowsTheLocalAnonymousCaller()
    {
        var authorization = Create(required: false);

        var scope = authorization.ResolveScope(new ClaimsPrincipal());

        Assert.True(scope.IsUnrestricted);
        Assert.NotNull(authorization.TryAuthorizeRecipe(new ClaimsPrincipal(), "payments-production"));
    }

    [Fact]
    public void DisabledDevelopmentIdentityDoesNotRequireTeamClaims()
    {
        var authorization = Create(required: false);
        var principal = Principal();

        var scope = authorization.ResolveScope(principal);

        Assert.True(scope.IsUnrestricted);
        Assert.Empty(scope.Teams);
        Assert.NotNull(authorization.TryAuthorizeRecipe(principal, "payments-production"));
    }

    [Fact]
    public void DevelopmentOpenAccessIdentityIsAuthenticatedAndCarriesEveryCasePermission()
    {
        var principal = DevelopmentOpenAccessIdentity.CreatePrincipal();

        Assert.True(principal.Identity?.IsAuthenticated);
        Assert.Equal(DevelopmentOpenAccessIdentity.Subject, principal.FindFirstValue("sub"));
        Assert.Equal(
            "*",
            principal.FindFirstValue(CaseAuthorization.PermissionClaimType));
        Assert.Equal(
            DevelopmentOpenAccessIdentity.AuthenticationType,
            principal.Identity?.AuthenticationType);
    }

    [Fact]
    public void SignedTeamAndMappedGroupClaimsOnlyGrantCanonicalExactTeams()
    {
        var authorization = Create(required: true);
        var principal = Principal(
            new Claim("panko:team", "payments"),
            new Claim("panko:team", "unknown"),
            new Claim("panko:team", "Payments"),
            new Claim("groups", "search-responders"),
            new Claim("groups", "unmapped-group"));

        var scope = authorization.ResolveScope(principal);

        Assert.False(scope.IsUnrestricted);
        Assert.Equal(["payments", "search", "unknown"], scope.Teams.Order(StringComparer.Ordinal));
        Assert.NotNull(authorization.TryAuthorizeRecipe(principal, "payments-production"));
        Assert.NotNull(authorization.TryAuthorizeRecipe(principal, "search-production"));
        Assert.Null(authorization.TryAuthorizeRecipe(principal, "unknown-recipe"));
        Assert.Null(authorization.TryAuthorizeRecipe(principal, "unmapped-recipe"));
        Assert.Equal(
            ["P123PAYMENTS", "P123SEARCH"],
            authorization.AuthorizedPagerDutyServiceIds(scope));
    }

    private static TeamAuthorization Create(bool required) => new(
        new RecipeCatalog(),
        OptionsFactory.Create(new JwtIdentityOptions { Required = required }),
        OptionsFactory.Create(new TeamAuthorizationOptions
        {
            TeamClaimTypes = ["panko:team"],
            GroupClaimTypes = ["groups"],
            GroupTeamMappings = new Dictionary<string, string>
            {
                ["search-responders"] = "search"
            }
        }));

    internal static ClaimsPrincipal Principal(params Claim[] claims) => new(
        new ClaimsIdentity(
            new[] { new Claim("sub", "operator@example.internal") }.Concat(claims),
            "Bearer"));

    internal sealed class RecipeCatalog : IRecipeOwnershipCatalog
    {
        public IReadOnlyList<RecipeOwnership> All { get; } =
        [
            new("payments-production", "payments", "P123PAYMENTS"),
            new("search-production", "search", "P123SEARCH"),
            new("unmapped-recipe", TeamKey.Unmapped, "P123UNMAPPED")
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

public sealed class CaseAccessAuthorizationTests
{
    [Fact]
    public async Task AccessUsesTheCaseTeamAndRecordsAllowedAndDeniedDecisions()
    {
        var caseId = Guid.NewGuid();
        var state = State(caseId, "payments");
        var caseFiles = new StubCaseFiles(state);
        var audit = new RecordingAuditTrail();
        var teams = new TeamAuthorization(
            new TeamAuthorizationTests.RecipeCatalog(),
            OptionsFactory.Create(new JwtIdentityOptions { Required = true }),
            OptionsFactory.Create(new TeamAuthorizationOptions()));
        var authorization = new CaseAccessAuthorizer(
            caseFiles,
            teams,
            new CaseAuthorization(teams),
            OptionsFactory.Create(new JwtIdentityOptions { Required = true }),
            audit);

        var allowed = await authorization.AuthorizeAsync(
            TeamAuthorizationTests.Principal(
                new Claim("panko:team", "payments"),
                new Claim(
                    CaseAuthorization.PermissionClaimType,
                    "case:read")),
            caseId,
            CaseAccessKind.LiveUpdates,
            CancellationToken.None);
        var denied = await authorization.AuthorizeAsync(
            TeamAuthorizationTests.Principal(new Claim("panko:team", "search")),
            caseId,
            CaseAccessKind.Crumbs,
            CancellationToken.None);

        Assert.Same(state, allowed?.State);
        Assert.Null(denied);
        Assert.Collection(
            audit.Events,
            item =>
            {
                Assert.Equal("allowed", item.Outcome);
                Assert.Equal("signalr", item.Metadata?["surface"]);
            },
            item =>
            {
                Assert.Equal("denied", item.Outcome);
                Assert.Equal(SecurityAuditActions.CrumbAccess, item.Action);
                Assert.Equal("payments", item.TargetTeam);
            });
    }

    [Fact]
    public async Task TeamMemberWithoutCaseReadPermissionIsDenied()
    {
        var caseId = Guid.NewGuid();
        var audit = new RecordingAuditTrail();
        var identity = OptionsFactory.Create(new JwtIdentityOptions { Required = true });
        var teams = new TeamAuthorization(
            new TeamAuthorizationTests.RecipeCatalog(),
            identity,
            OptionsFactory.Create(new TeamAuthorizationOptions()));
        var authorization = new CaseAccessAuthorizer(
            new StubCaseFiles(State(caseId, "payments")),
            teams,
            new CaseAuthorization(teams),
            identity,
            audit);

        var grant = await authorization.AuthorizeAsync(
            TeamAuthorizationTests.Principal(new Claim("panko:team", "payments")),
            caseId,
            CaseAccessKind.CaseFile,
            CancellationToken.None);

        Assert.Null(grant);
        Assert.Equal("denied", Assert.Single(audit.Events).Outcome);
    }

    [Fact]
    public async Task AnonymousDemoReadRemainsAvailableWhenIdentityIsOptional()
    {
        var caseId = Guid.NewGuid();
        var audit = new RecordingAuditTrail();
        var identity = OptionsFactory.Create(new JwtIdentityOptions { Required = false });
        var teams = new TeamAuthorization(
            new TeamAuthorizationTests.RecipeCatalog(),
            identity,
            OptionsFactory.Create(new TeamAuthorizationOptions()));
        var authorization = new CaseAccessAuthorizer(
            new StubCaseFiles(State(caseId, "payments")),
            teams,
            new CaseAuthorization(teams),
            identity,
            audit);

        var grant = await authorization.AuthorizeAsync(
            new ClaimsPrincipal(),
            caseId,
            CaseAccessKind.CaseFile,
            CancellationToken.None);

        Assert.NotNull(grant);
        Assert.Equal("allowed", Assert.Single(audit.Events).Outcome);
    }

    [Fact]
    public async Task JoinCaseRejectsACrossTeamCallerWithoutAddingTheConnectionToTheGroup()
    {
        var caseId = Guid.NewGuid();
        var audit = new RecordingAuditTrail();
        var identity = OptionsFactory.Create(new JwtIdentityOptions { Required = true });
        var teams = new TeamAuthorization(
            new TeamAuthorizationTests.RecipeCatalog(),
            identity,
            OptionsFactory.Create(new TeamAuthorizationOptions()));
        var authorization = new CaseAccessAuthorizer(
            new StubCaseFiles(State(caseId, "payments")),
            teams,
            new CaseAuthorization(teams),
            identity,
            audit);
        var principal = TeamAuthorizationTests.Principal(
            new Claim("panko:team", "search"),
            new Claim(
                CaseAuthorization.PermissionClaimType,
                "case:read"));
        var groups = new RecordingGroupManager();
        var hub = new CaseHub(authorization)
        {
            Context = new StubHubCallerContext(principal),
            Groups = groups
        };

        var exception = await Assert.ThrowsAsync<HubException>(
            () => hub.JoinCase(caseId.ToString()));

        Assert.Equal("Case not found.", exception.Message);
        Assert.Empty(groups.Added);
        var auditEvent = Assert.Single(audit.Events);
        Assert.Equal("denied", auditEvent.Outcome);
        Assert.Equal("signalr", auditEvent.Metadata?["surface"]);
    }

    private static CaseFileState State(Guid id, string team) => new(
        id,
        "PD123",
        "payments-api",
        "payments-production",
        "Payment timeouts",
        "high",
        PagerDutyIncidentState.Triggered,
        "ready",
        DateTimeOffset.Parse("2026-08-03T09:45:00Z"),
        DateTimeOffset.Parse("2026-08-03T09:50:00Z"),
        2,
        false,
        null)
    {
        Team = team
    };

    private sealed class StubCaseFiles(CaseFileState? state) : ICaseFileReader
    {
        public Task<CaseFileState?> GetAsync(Guid caseId, CancellationToken cancellationToken) =>
            Task.FromResult(state?.CaseId == caseId ? state : null);
    }

    private sealed class RecordingAuditTrail : ISecurityAuditTrail
    {
        public List<SecurityAuditEvent> Events { get; } = [];

        public Task RecordAsync(SecurityAuditEvent auditEvent, CancellationToken cancellationToken)
        {
            Events.Add(auditEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class StubHubCallerContext(ClaimsPrincipal principal) : HubCallerContext
    {
        public override string ConnectionId => "cross-team-connection";

        public override string? UserIdentifier => principal.FindFirstValue("sub");

        public override ClaimsPrincipal User => principal;

        public override IDictionary<object, object?> Items { get; } =
            new Dictionary<object, object?>();

        public override IFeatureCollection Features { get; } = new FeatureCollection();

        public override CancellationToken ConnectionAborted => CancellationToken.None;

        public override void Abort()
        {
        }
    }

    private sealed class RecordingGroupManager : IGroupManager
    {
        public List<(string ConnectionId, string GroupName)> Added { get; } = [];

        public Task AddToGroupAsync(
            string connectionId,
            string groupName,
            CancellationToken cancellationToken = default)
        {
            Added.Add((connectionId, groupName));
            return Task.CompletedTask;
        }

        public Task RemoveFromGroupAsync(
            string connectionId,
            string groupName,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

public sealed class TrustedProxyConfigurationTests
{
    [Fact]
    public void OnlyExplicitConfiguredProxyAddressesAndNetworksAreAccepted()
    {
        var configured = new TrustedProxyOptions
        {
            ForwardLimit = 2,
            KnownProxies = ["10.42.1.10"],
            KnownNetworks = ["10.42.0.0/16"]
        };

        var options = TrustedProxyConfiguration.Create(configured);

        Assert.True(TrustedProxyConfiguration.IsValid(configured));
        Assert.Equal(2, options.ForwardLimit);
        Assert.True(options.RequireHeaderSymmetry);
        Assert.Single(options.KnownProxies);
        Assert.Contains(IPAddress.Parse("10.42.1.10"), options.KnownProxies);
        Assert.DoesNotContain(IPAddress.Loopback, options.KnownProxies);
        Assert.DoesNotContain(IPAddress.IPv6Loopback, options.KnownProxies);
        Assert.Single(options.KnownIPNetworks);
        Assert.False(TrustedProxyConfiguration.IsValid(new TrustedProxyOptions
        {
            KnownProxies = ["0.0.0.0"]
        }));
        Assert.False(TrustedProxyConfiguration.IsValid(new TrustedProxyOptions
        {
            KnownNetworks = ["::/0"]
        }));
        Assert.False(TrustedProxyConfiguration.IsValid(new TrustedProxyOptions
        {
            KnownProxies = ["not-an-address"]
        }));
        Assert.Throws<InvalidOperationException>(() => TrustedProxyConfiguration.Create(
            new TrustedProxyOptions { KnownNetworks = ["0.0.0.0/0"] }));
    }
}
