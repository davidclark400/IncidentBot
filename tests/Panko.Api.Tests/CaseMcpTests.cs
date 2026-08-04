using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Panko.Api.Cases;
using Panko.Api.Mcp;
using Panko.Api.Options;
using Panko.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using ContractOriginKind = Panko.Contracts.CaseOriginKind;

namespace Panko.Api.Tests;

public sealed class CaseMcpTests
{
    [Fact]
    public void ToolSurfaceContainsOnlyTheSixCanonicalCaseTools()
    {
        var names = typeof(CaseMcpTools)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>())
            .Where(attribute => attribute is not null)
            .Select(attribute => attribute!.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var canonical = new[]
        {
            CaseMcpToolNames.Append,
            CaseMcpToolNames.Close,
            CaseMcpToolNames.Create,
            CaseMcpToolNames.Get,
            CaseMcpToolNames.Rebuild,
            CaseMcpToolNames.Refresh
        };
        Assert.Equal(canonical.Order(StringComparer.Ordinal), names);
    }

    [Fact]
    public void RegistrationPublishesRestrictedStringEnumSchemas()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions<CaseOptions>();
        services.AddSingleton<ICaseCommands, RecordingCommands>();
        services.AddSingleton<ICaseQueries, StubQueries>();
        services.AddSingleton<CaseTelemetry>();
        services.AddCaseMcp();
        using var provider = services.BuildServiceProvider();

        var tools = provider.GetServices<McpServerTool>().ToArray();
        var append = Assert.Single(
            tools,
            tool => tool.ProtocolTool.Name == CaseMcpToolNames.Append);
        var schemaJson = JsonSerializer.Serialize(
            append.ProtocolTool.InputSchema,
            McpJsonUtilities.DefaultOptions);

        Assert.Equal(6, tools.Length);
        Assert.Contains("\"event\"", schemaJson, StringComparison.Ordinal);
        Assert.Contains("\"crumb\"", schemaJson, StringComparison.Ordinal);
        Assert.Contains("\"note\"", schemaJson, StringComparison.Ordinal);
        Assert.DoesNotContain("connectorUrl", schemaJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", schemaJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("slackChannel", schemaJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("trustLevel", schemaJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AppendDelegatesWithTheCurrentAuthenticatedBearerIdentity()
    {
        var commands = new RecordingCommands();
        var tools = CreateTools(commands, new StubQueries(), AuthenticatedAccessor("mcp-agent"));
        var caseId = Guid.NewGuid();

        var result = await tools.AppendAsync(
            caseId,
            "batch-4",
            [],
            CancellationToken.None);

        Assert.Equal("mcp-agent", commands.Caller?.PrincipalName);
        Assert.Equal(caseId, commands.CaseId);
        Assert.Equal("batch-4", commands.AppendCommand?.BatchId);
        Assert.Equal(2, result.Accepted);
        Assert.True(result.RebuildQueued);
    }

    [Fact]
    public async Task GetTruncatesSummaryWithinTheConfiguredResponseLimit()
    {
        const int maximumBytes = 1024;
        var queries = new StubQueries
        {
            Status = new CaseStatusResponse(
                Guid.NewGuid(),
                ContractOriginKind.Agent,
                "ready",
                "payments-production",
                "payments-api",
                "Payments failing",
                12,
                12,
                7,
                "mcp-agent",
                DateTimeOffset.Parse("2026-08-03T10:00:00Z"),
                new string('x', 4000),
                "/cases/test")
        };
        var tools = CreateTools(
            new RecordingCommands(),
            queries,
            AuthenticatedAccessor("mcp-agent"),
            maximumBytes);

        var result = await tools.GetAsync(queries.Status.CaseId, CancellationToken.None);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            result,
            McpJsonUtilities.DefaultOptions).Length;

        Assert.True(bytes <= maximumBytes, $"Serialized result used {bytes} bytes.");
        Assert.NotNull(result.DeterministicSummary);
        Assert.EndsWith("\u2026", result.DeterministicSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingAuthenticatedIdentityReturnsASafeToolError()
    {
        var tools = CreateTools(
            new RecordingCommands(),
            new StubQueries(),
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() });

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            tools.GetAsync(Guid.NewGuid(), CancellationToken.None));

        Assert.Equal("The caller is not authorized to perform this operation.", exception.Message);
    }

    private static CaseMcpTools CreateTools(
        ICaseCommands commands,
        ICaseQueries queries,
        IHttpContextAccessor accessor,
        int maximumResponseBytes = 64 * 1024)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new CaseOptions
        {
            MaximumMcpResponseBytes = maximumResponseBytes
        });
        var router = new McpToolRouter(
            options,
            new CaseTelemetry(),
            NullLogger<McpToolRouter>.Instance);
        return new CaseMcpTools(commands, queries, accessor, router);
    }

    private static IHttpContextAccessor AuthenticatedAccessor(string name)
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, name)],
            "Bearer");
        return new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(identity)
            }
        };
    }

    private sealed class RecordingCommands : ICaseCommands
    {
        public Guid? CaseId { get; private set; }

        public AppendCrumbs? AppendCommand { get; private set; }

        public CallerIdentity? Caller { get; private set; }

        public Task<CreateCaseResult> CreateAsync(
            CreateCase command,
            CallerIdentity caller,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AppendCrumbsResult> AppendCrumbsAsync(
            Guid caseId,
            AppendCrumbs command,
            CallerIdentity caller,
            CancellationToken cancellationToken)
        {
            CaseId = caseId;
            AppendCommand = command;
            Caller = caller;
            return Task.FromResult(new AppendCrumbsResult(2, 1, 4, 1, true, false));
        }

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
        public CaseStatusResponse Status { get; init; } =
            new(
                Guid.NewGuid(),
                ContractOriginKind.Agent,
                "ready",
                "payments-production",
                "payments-api",
                "Payments failing",
                0,
                0,
                1,
                "mcp-agent",
                DateTimeOffset.Parse("2026-08-03T10:00:00Z"),
                "No Crumbs submitted.",
                "/cases/test");

        public Task<CaseStatusResponse> GetAsync(
            Guid caseId,
            CallerIdentity caller,
            CancellationToken cancellationToken) => Task.FromResult(Status);

        public Task<Page<Panko.Contracts.CaseInput>> ListInputsAsync(
            Guid caseId,
            int offset,
            int limit,
            CallerIdentity caller,
            CancellationToken cancellationToken) =>
            Task.FromResult(new Page<Panko.Contracts.CaseInput>(0, []));

        public Task<RecentCases> ListRecentAsync(
            int limit,
            CallerIdentity caller,
            CancellationToken cancellationToken) =>
            Task.FromResult(new RecentCases(0, []));
    }
}
