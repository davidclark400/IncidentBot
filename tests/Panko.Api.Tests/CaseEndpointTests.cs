using System.Security.Claims;
using Panko.Api.Domain;
using Panko.Api.Cases;
using Panko.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PagerDutyState = Panko.Api.Domain.PagerDutyIncidentState;
using DomainCaseOrigin = Panko.Api.Domain.CaseOrigin;
using DomainCaseOriginKind = Panko.Api.Domain.CaseOriginKind;

namespace Panko.Api.Tests;

public sealed class CaseEndpointTests
{
    [Fact]
    public async Task CreateUsesIdempotencyHeaderAndReturnsCreatedContract()
    {
        var id = Guid.NewGuid();
        CreateCase? observed = null;
        var commands = new StubCommands
        {
            Create = (command, _, _) =>
            {
                observed = command;
                return Task.FromResult(new CreateCaseResult(BuildCase(id), Duplicate: false));
            }
        };
        var request = new CreateCaseRequest(
            "payments-production",
            "Payment timeouts",
            "payments-api",
            "high",
            DateTimeOffset.Parse("2026-08-03T09:45:00Z"),
            new Dictionary<string, string> { ["environment"] = "production" });

        var result = await CaseEndpoints.CreateAsync(
            request,
            "agent-run-001",
            AuthenticatedContext(),
            commands,
            CancellationToken.None);

        Assert.Equal("agent-run-001", observed?.IdempotencyKey);
        Assert.Equal(StatusCodes.Status201Created, StatusCode(result));
        var response = Assert.IsType<CreateCaseResponse>(
            Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
        Assert.Equal(id, response.CaseId);
        Assert.Equal(1, response.CaseFileVersion);
        Assert.False(response.Duplicate);
    }

    [Fact]
    public async Task CreateRejectsMismatchedHeaderAndBodyIdempotencyKeys()
    {
        var commands = new StubCommands();
        var request = new CreateCaseRequest(
            "payments-production",
            "Payment timeouts",
            "payments-api",
            "high",
            DateTimeOffset.Parse("2026-08-03T09:45:00Z"),
            IdempotencyKey: "body-key");

        var result = await CaseEndpoints.CreateAsync(
            request,
            "header-key",
            AuthenticatedContext(),
            commands,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusCode(result));
        Assert.Equal(0, commands.CreateCalls);
    }

    [Fact]
    public async Task ProtectedEndpointRejectsAnUnauthenticatedCallerBeforeDispatch()
    {
        var commands = new StubCommands();
        var request = new CreateCaseRequest(
            "payments-production",
            "Payment timeouts",
            "payments-api",
            "high",
            DateTimeOffset.Parse("2026-08-03T09:45:00Z"));

        var result = await CaseEndpoints.CreateAsync(
            request,
            "agent-run-001",
            new DefaultHttpContext(),
            commands,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status401Unauthorized, StatusCode(result));
        Assert.Equal(0, commands.CreateCalls);
    }

    [Theory]
    [InlineData("forbidden", StatusCodes.Status403Forbidden)]
    [InlineData("missing", StatusCodes.Status404NotFound)]
    [InlineData("conflict", StatusCodes.Status409Conflict)]
    public async Task ExpectedDomainFailuresUseConsistentProblemStatusCodes(
        string failure,
        int expectedStatus)
    {
        var caseId = Guid.NewGuid();
        var queries = new StubQueries
        {
            Get = (_, _, _) => Task.FromException<CaseStatusResponse>(failure switch
            {
                "forbidden" => new CaseAuthorizationException("denied"),
                "missing" => new CaseNotFoundException(caseId),
                _ => new CaseConflictException("conflict")
            })
        };

        var result = await CaseEndpoints.GetAsync(
            caseId,
            AuthenticatedContext(),
            queries,
            CancellationToken.None);

        Assert.Equal(expectedStatus, StatusCode(result));
    }

    [Theory]
    [InlineData(-5, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(500, 500)]
    [InlineData(501, 500)]
    public void PageLimitsAreBounded(int requested, int expected) =>
        Assert.Equal(expected, CaseEndpoints.BoundLimit(requested, 100));

    [Fact]
    public async Task InputAuditPagingIsBoundedBeforeQueryDispatch()
    {
        var caseId = Guid.NewGuid();
        var queries = new StubQueries();
        var context = AuthenticatedContext();

        var result = await CaseEndpoints.ListInputsAsync(
            caseId,
            offset: -20,
            limit: 5_000,
            context,
            queries,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, StatusCode(result));
        Assert.NotNull(queries.InputPageRequest);
        Assert.Equal((caseId, 0, 500), queries.InputPageRequest.Value);
        Assert.Equal("private, no-store", context.Response.Headers.CacheControl);
    }

    [Fact]
    public async Task MapsEveryCanonicalCaseRouteWithAUniqueOperationName()
    {
        var builder = WebApplication.CreateBuilder();
        await using var app = builder.Build();
        app.MapCaseManagementApi();

        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => (
                Pattern: endpoint.RoutePattern.RawText,
                Name: endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName))
            .ToArray();

        Assert.Equal(7, routes.Length);
        Assert.All(routes, route => Assert.NotNull(route.Name));
        Assert.Equal(7, routes.Select(route => route.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(routes, route => route.Pattern == "/api/cases/");
        Assert.Contains(routes, route => route.Pattern == "/api/cases/{id:guid}/crumbs");
        Assert.Contains(routes, route => route.Pattern == "/api/cases/{id:guid}/inputs");
        Assert.Contains(routes, route => route.Pattern == "/api/cases/{id:guid}/refresh-sources");
    }

    private static DefaultHttpContext AuthenticatedContext()
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "agent@example.internal")],
            "test"));
        return context;
    }

    private static int? StatusCode(IResult result) =>
        Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode;

    private static CaseRecord BuildCase(Guid id)
    {
        var timestamp = DateTimeOffset.Parse("2026-08-03T09:45:00Z");
        return new CaseRecord(
            id,
            null,
            "payments-api",
            "payments-production",
            "Payment timeouts",
            "high",
            PagerDutyState.Triggered,
            timestamp,
            timestamp,
            1,
            "open",
            false,
            null,
            string.Empty,
            null,
            new Dictionary<string, string>())
        {
            Origin = new DomainCaseOrigin(DomainCaseOriginKind.Agent, null),
            CreatedBy = "agent@example.internal"
        };
    }

    private sealed class StubCommands : ICaseCommands
    {
        public Func<CreateCase, CallerIdentity, CancellationToken, Task<CreateCaseResult>>? Create
        {
            get;
            init;
        }

        public int CreateCalls { get; private set; }

        public Task<CreateCaseResult> CreateAsync(
            CreateCase command,
            CallerIdentity caller,
            CancellationToken cancellationToken)
        {
            CreateCalls++;
            return Create?.Invoke(command, caller, cancellationToken)
                ?? throw new InvalidOperationException("Unexpected create dispatch.");
        }

        public Task<AppendCrumbsResult> AppendCrumbsAsync(
            Guid caseId,
            AppendCrumbs command,
            CallerIdentity caller,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RebuildCaseResult> QueueRebuildAsync(
            Guid caseId,
            CallerIdentity caller,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RefreshCaseResult> QueueSourceRefreshAsync(
            Guid caseId,
            CallerIdentity caller,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task CloseAsync(
            Guid caseId,
            CallerIdentity caller,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubQueries : ICaseQueries
    {
        public Func<Guid, CallerIdentity, CancellationToken, Task<CaseStatusResponse>>? Get
        {
            get;
            init;
        }

        public (Guid CaseId, int Offset, int Limit)? InputPageRequest { get; private set; }

        public Task<CaseStatusResponse> GetAsync(
            Guid caseId,
            CallerIdentity caller,
            CancellationToken cancellationToken) =>
            Get?.Invoke(caseId, caller, cancellationToken)
            ?? throw new NotSupportedException();

        public Task<Page<Panko.Contracts.CaseInput>> ListInputsAsync(
            Guid caseId,
            int offset,
            int limit,
            CallerIdentity caller,
            CancellationToken cancellationToken)
        {
            InputPageRequest = (caseId, offset, limit);
            return Task.FromResult(new Page<Panko.Contracts.CaseInput>(0, []));
        }

        public Task<RecentCases> ListRecentAsync(
            int limit,
            CallerIdentity caller,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
