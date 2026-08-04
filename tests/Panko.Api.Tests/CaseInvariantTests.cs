using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Panko.Api.Domain;
using Panko.Api.Cases;
using Panko.Api.Mcp;
using Panko.Api.Security;
using Panko.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PagerDutyState = Panko.Api.Domain.PagerDutyIncidentState;
using DomainCaseOrigin = Panko.Api.Domain.CaseOrigin;
using DomainCaseOriginKind = Panko.Api.Domain.CaseOriginKind;
using DomainCaseFile = Panko.Api.Domain.CaseFile;

namespace Panko.Api.Tests;

public sealed class CaseInvariantTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-08-03T10:00:00Z");

    private static readonly JsonSerializerOptions TransportJson = CreateTransportJson();

    [Fact]
    public async Task RestTransportSupportsAuthenticatedCaseLifecycle()
    {
        await using var factory = new CaseApiFactory();
        using var client = factory.CreateClient();
        const string idempotencyKey = "transport-create-001";

        using var createRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/cases")
        {
            Content = JsonContent.Create(
                new CreateCaseRequest(
                    "payments-production",
                    "Payment timeouts",
                    "payments-api",
                    "high",
                    Now,
                    new Dictionary<string, string> { ["environment"] = "production" }),
                options: TransportJson)
        };
        createRequest.Headers.Add("Idempotency-Key", idempotencyKey);

        using var createResponse = await client.SendAsync(createRequest);

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = Assert.IsType<CreateCaseResponse>(
            await createResponse.Content.ReadFromJsonAsync<CreateCaseResponse>(
                TransportJson));
        Assert.Equal(factory.Commands.CaseId, created.CaseId);
        Assert.Equal(Panko.Contracts.CaseOriginKind.Agent, created.Origin);
        Assert.Equal("open", created.Status);
        Assert.Equal(
            $"/api/cases/{created.CaseId:D}",
            createResponse.Headers.Location?.OriginalString);

        using var appendResponse = await client.PostAsJsonAsync(
            $"/api/cases/{created.CaseId:D}/crumbs",
            new AppendCrumbsRequest(
                "transport-batch-001",
                [new SubmittedCrumb(
                    "event-001",
                    SubmittedCrumbKind.Event,
                    Now.AddMinutes(-15),
                    "deployment",
                    "warning",
                    "Deployment completed")]),
            TransportJson);

        Assert.Equal(HttpStatusCode.OK, appendResponse.StatusCode);
        var appended = Assert.IsType<AppendCrumbsResponse>(
            await appendResponse.Content.ReadFromJsonAsync<AppendCrumbsResponse>(
                TransportJson));
        Assert.Equal(1, appended.Accepted);
        Assert.Equal(1, appended.InputVersion);

        using var rebuildResponse = await client.PostAsync(
            $"/api/cases/{created.CaseId:D}/rebuild",
            content: null);

        Assert.Equal(HttpStatusCode.Accepted, rebuildResponse.StatusCode);
        var rebuilt = Assert.IsType<RebuildCaseFileResponse>(
            await rebuildResponse.Content.ReadFromJsonAsync<RebuildCaseFileResponse>(
                TransportJson));
        Assert.Equal(1, rebuilt.TargetInputVersion);
        Assert.True(rebuilt.RebuildQueued);

        using var refreshResponse = await client.PostAsync(
            $"/api/cases/{created.CaseId:D}/refresh-sources",
            content: null);

        Assert.Equal(HttpStatusCode.Accepted, refreshResponse.StatusCode);
        var refreshed = Assert.IsType<RefreshCaseSourcesResponse>(
            await refreshResponse.Content.ReadFromJsonAsync<RefreshCaseSourcesResponse>(
                TransportJson));
        Assert.Equal(1, refreshed.TargetInputVersion);
        Assert.True(refreshed.RefreshQueued);

        using var closeResponse = await client.PostAsync(
            $"/api/cases/{created.CaseId:D}/close",
            content: null);

        Assert.Equal(HttpStatusCode.NoContent, closeResponse.StatusCode);
        Assert.Equal(idempotencyKey, factory.Commands.LastCreate?.IdempotencyKey);
        Assert.Equal(SubmittedCrumbKind.Event, factory.Commands.LastAppend?.Crumbs.Single().Kind);
        Assert.Equal(
            ["create", "append", "rebuild", "refresh", "close"],
            factory.Commands.Calls.Select(call => call.Operation));
        Assert.All(
            factory.Commands.Calls,
            call => Assert.Equal("transport-agent", call.Caller));
        Assert.All(
            factory.Commands.Calls,
            call => Assert.Equal(factory.Commands.CaseId, call.CaseId));
    }

    [Fact]
    public async Task RestTransportDeniesAnonymousCreateBeforeCommandDispatch()
    {
        await using var factory = new CaseApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.AnonymousHeader, "true");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/cases")
        {
            Content = JsonContent.Create(
                new CreateCaseRequest(
                    "payments-production",
                    "Payment timeouts",
                    "payments-api",
                    "high",
                    Now,
                    IdempotencyKey: "anonymous-create-001"),
                options: TransportJson)
        };
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, factory.Commands.CreateCalls);
        Assert.Empty(factory.Commands.Calls);
    }

    [Fact]
    public async Task McpStreamableHttpInitializesListsToolsAndInvokesCaseCommands()
    {
        await using var factory = new CaseApiFactory();
        using var httpClient = factory.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var transport = CreateMcpTransport(httpClient);
        await using var mcpClient = await McpClient.CreateAsync(
            transport,
            new McpClientOptions
            {
                ProtocolVersion = "2025-11-25",
                ClientInfo = new Implementation
                {
                    Name = "panko-transport-tests",
                    Version = "1.0.0"
                }
            },
            NullLoggerFactory.Instance,
            timeout.Token);

        Assert.Equal("2025-11-25", mcpClient.NegotiatedProtocolVersion);
        Assert.Equal("panko", mcpClient.ServerInfo.Name);

        var tools = await mcpClient.ListToolsAsync(cancellationToken: timeout.Token);

        Assert.Contains(
            tools,
            tool => tool.Name == CaseMcpToolNames.Create);
        Assert.Contains(
            tools,
            tool => tool.Name == CaseMcpToolNames.Append);

        var created = await mcpClient.CallToolAsync(
            CaseMcpToolNames.Create,
            new Dictionary<string, object?>
            {
                ["idempotencyKey"] = "mcp-transport-create-001",
                ["recipeId"] = "payments-production",
                ["title"] = "Payment timeouts",
                ["serviceId"] = "payments-api",
                ["urgency"] = "high",
                ["referenceTime"] = Now,
                ["labels"] = new Dictionary<string, string>
                {
                    ["environment"] = "production"
                }
            },
            cancellationToken: timeout.Token);

        Assert.False(created.IsError ?? false);
        Assert.NotNull(created.StructuredContent);

        var appended = await mcpClient.CallToolAsync(
            CaseMcpToolNames.Append,
            new Dictionary<string, object?>
            {
                ["caseId"] = factory.Commands.CaseId,
                ["batchId"] = "mcp-transport-batch-001",
                ["crumbs"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["clientCrumbId"] = "mcp-crumb-001",
                        ["type"] = "event",
                        ["occurredAt"] = Now.AddMinutes(-10),
                        ["category"] = "deployment",
                        ["severity"] = "warning",
                        ["summary"] = "Deployment completed"
                    }
                }
            },
            cancellationToken: timeout.Token);

        Assert.False(appended.IsError ?? false);
        Assert.Equal(
            ["create", "append"],
            factory.Commands.Calls.Select(call => call.Operation));
        Assert.All(
            factory.Commands.Calls,
            call => Assert.Equal("transport-agent", call.Caller));
    }

    [Fact]
    public async Task McpStreamableHttpDeniesAnonymousInitializationBeforeToolDispatch()
    {
        await using var factory = new CaseApiFactory();
        using var httpClient = factory.CreateClient();
        httpClient.DefaultRequestHeaders.Add(TestAuthenticationHandler.AnonymousHeader, "true");
        await using var transport = CreateMcpTransport(httpClient);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            McpClient.CreateAsync(
                transport,
                new McpClientOptions { ProtocolVersion = "2025-11-25" },
                NullLoggerFactory.Instance,
                timeout.Token));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.Empty(factory.Commands.Calls);
    }

    [Fact]
    public async Task RestAppendRejectsNumericSubmittedInputTypeBeforeCommandDispatch()
    {
        await using var factory = new CaseApiFactory();
        using var client = factory.CreateClient();
        using var content = new StringContent(
            """
            {
              "batchId": "batch-001",
              "crumbs": [
                {
                  "clientCrumbId": "crumb-001",
                  "kind": 0,
                  "occurredAt": "2026-08-03T09:45:00Z",
                  "category": "deployment",
                  "severity": "warning",
                  "summary": "Deployment completed"
                }
              ]
            }
            """,
            Encoding.UTF8,
            "application/json");

        using var response = await client.PostAsync(
            $"/api/cases/{Guid.NewGuid():D}/crumbs",
            content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, factory.Commands.AppendCalls);
    }

    [Theory]
    [InlineData("append", CasePermission.Append)]
    [InlineData("rebuild", CasePermission.Rebuild)]
    [InlineData("refresh", CasePermission.RefreshSources)]
    [InlineData("close", CasePermission.Close)]
    public async Task MutableCommandsRejectPagerDutyOriginBeforeAnyMutation(
        string operation,
        CasePermission expectedPermission)
    {
        var caseRecord = PagerDutyCase();
        var repository = new RepositoryStub(caseRecord, []);
        var authorization = new AllowAuthorization();
        var commands = new CaseCommands(
            repository,
            null!,
            authorization,
            null!,
            null!,
            null!,
            null!,
            new CaseTelemetry(),
            TimeProvider.System);
        var caller = Caller();

        Task InvokeAsync() => operation switch
        {
            "append" => commands.AppendCrumbsAsync(
                caseRecord.Id,
                new AppendCrumbs("batch-001", []),
                caller,
                CancellationToken.None),
            "rebuild" => commands.QueueRebuildAsync(
                caseRecord.Id,
                caller,
                CancellationToken.None),
            "refresh" => commands.QueueSourceRefreshAsync(
                caseRecord.Id,
                caller,
                CancellationToken.None),
            "close" => commands.CloseAsync(
                caseRecord.Id,
                caller,
                CancellationToken.None),
            _ => throw new InvalidOperationException($"Unknown operation '{operation}'.")
        };

        var exception = await Assert.ThrowsAsync<CaseConflictException>(InvokeAsync);

        Assert.Contains("PagerDuty", exception.Message, StringComparison.Ordinal);
        Assert.Equal([expectedPermission], authorization.Permissions);
        Assert.Equal(0, repository.MutationCalls);
    }

    [Fact]
    public async Task AuditMarksInputsProjectedOnlyWhenTheExactProjectionTargetContainsThem()
    {
        var caseRecord = AgentCase(inputVersion: 2, projectedInputVersion: 1);
        var originalId = Guid.Parse("11111111-1111-5111-8111-111111111111");
        var replacementId = Guid.Parse("22222222-2222-5222-8222-222222222222");
        var original = Input(
            caseRecord.Id,
            originalId,
            sequence: 1,
            inputVersion: 1,
            clientCrumbId: "original") with
        {
            RetractedAt = Now.AddMinutes(2),
            RetractedInputVersion = 2
        };
        var replacement = Input(
            caseRecord.Id,
            replacementId,
            sequence: 2,
            inputVersion: 2,
            clientCrumbId: "replacement") with
        {
            SupersedesCrumbId = originalId
        };
        var repository = new RepositoryStub(caseRecord, [original, replacement]);
        var queries = new CaseQueries(repository, new AllowCaseAccess());

        var atTargetOne = await queries.ListInputsAsync(
            caseRecord.Id,
            0,
            100,
            Caller(),
            CancellationToken.None);

        var originalAtTargetOne = Assert.Single(
            atTargetOne.Items,
            item => item.ClientCrumbId == "original");
        var replacementAtTargetOne = Assert.Single(
            atTargetOne.Items,
            item => item.ClientCrumbId == "replacement");
        Assert.Equal(replacementId, originalAtTargetOne.SupersededByCrumbId);
        Assert.Equal(originalId, replacementAtTargetOne.SupersedesCrumbId);
        Assert.Equal(1, originalAtTargetOne.ProjectedInInputVersion);
        Assert.Null(replacementAtTargetOne.ProjectedInInputVersion);

        repository.StoredCase = caseRecord with { ProjectedInputVersion = 2 };
        var atTargetTwo = await queries.ListInputsAsync(
            caseRecord.Id,
            0,
            100,
            Caller(),
            CancellationToken.None);

        var originalAtTargetTwo = Assert.Single(
            atTargetTwo.Items,
            item => item.ClientCrumbId == "original");
        var replacementAtTargetTwo = Assert.Single(
            atTargetTwo.Items,
            item => item.ClientCrumbId == "replacement");
        Assert.Null(originalAtTargetTwo.ProjectedInInputVersion);
        Assert.Equal(2, replacementAtTargetTwo.ProjectedInInputVersion);
    }

    private static CallerIdentity Caller()
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "agent@example.internal")],
            "test");
        return new CallerIdentity(new ClaimsPrincipal(identity));
    }

    private static JsonSerializerOptions CreateTransportJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(
            JsonNamingPolicy.CamelCase,
            allowIntegerValues: false));
        return options;
    }

    private static HttpClientTransport CreateMcpTransport(HttpClient httpClient) => new(
        new HttpClientTransportOptions
        {
            Endpoint = new Uri(httpClient.BaseAddress!, "/api/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            EnableStandaloneGetStream = false,
            Name = "panko-transport-tests"
        },
        httpClient,
        NullLoggerFactory.Instance,
        ownsHttpClient: false);

    private static CaseRecord PagerDutyCase() => new(
        Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
        "PD-123",
        "payments-api",
        "payments-production",
        "Payment timeouts",
        "high",
        PagerDutyState.Triggered,
        Now,
        Now,
        1,
        CaseProgression.Ready,
        false,
        null,
        string.Empty,
        null,
        new Dictionary<string, string>())
    {
        Team = "payments",
        Origin = new DomainCaseOrigin(DomainCaseOriginKind.PagerDuty, "PD-123"),
        InputVersion = 0,
        ProjectedInputVersion = 0
    };

    private static CaseRecord AgentCase(long inputVersion, long projectedInputVersion) => new(
        Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"),
        null,
        "payments-api",
        "payments-production",
        "Payment timeouts",
        "high",
        PagerDutyState.Triggered,
        Now,
        Now,
        1,
        CaseProgression.Rebuilding,
        false,
        null,
        string.Empty,
        null,
        new Dictionary<string, string>())
    {
        Team = "payments",
        Origin = new DomainCaseOrigin(DomainCaseOriginKind.Agent, null),
        CreatedBy = "agent@example.internal",
        InputVersion = inputVersion,
        ProjectedInputVersion = projectedInputVersion,
        PublishToSlack = false
    };

    private static Panko.Api.Cases.CaseInput Input(
        Guid caseId,
        Guid inputId,
        long sequence,
        long inputVersion,
        string clientCrumbId) => new(
        inputId,
        caseId,
        sequence,
        inputVersion,
        "agent@example.internal",
        clientCrumbId,
        SubmittedCrumbKind.Event,
        Now.AddMinutes(sequence),
        Now.AddMinutes(sequence),
        "deployment",
        "warning",
        $"Event {clientCrumbId}",
        null,
        "gitlab",
        null,
        null,
        "agent@example.internal",
        "deployment",
        clientCrumbId,
        new JsonObject(),
        "submitted",
        $"payload-{clientCrumbId}",
        null,
        null,
        null);

    private sealed class CaseApiFactory : WebApplicationFactory<Program>
    {
        public RecordingCommands Commands { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Demo:Enabled", "false");
            builder.UseSetting("JwtIdentity:Required", "true");
            builder.UseSetting(
                "ConnectionStrings:Panko",
                "Host=localhost;Database=panko-tests;Username=panko;Password=panko");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Demo:Enabled"] = "false",
                    ["JwtIdentity:Required"] = "true",
                    ["ConnectionStrings:Panko"] =
                        "Host=localhost;Database=panko-tests;Username=panko;Password=panko"
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<ICaseCommands>();
                services.RemoveAll<ICaseQueries>();
                services.AddSingleton<ICaseCommands>(Commands);
                services.AddSingleton<ICaseQueries>(Commands);
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = TestAuthenticationHandler.SchemeName;
                        options.DefaultChallengeScheme = TestAuthenticationHandler.SchemeName;
                    })
                    .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                        TestAuthenticationHandler.SchemeName,
                        _ => { });
            });
        }
    }

    private sealed class RecordingCommands :
        ICaseCommands,
        ICaseQueries
    {
        public Guid CaseId { get; } =
            Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc");

        public List<(string Operation, Guid CaseId, string Caller)> Calls { get; } = [];

        public CreateCase? LastCreate { get; private set; }

        public AppendCrumbs? LastAppend { get; private set; }

        public int CreateCalls { get; private set; }

        public int AppendCalls { get; private set; }

        private CaseRecord? StoredCase { get; set; }

        public Task<CreateCaseResult> CreateAsync(
            CreateCase command,
            CallerIdentity caller,
            CancellationToken cancellationToken)
        {
            CreateCalls++;
            LastCreate = command;
            StoredCase = new CaseRecord(
                CaseId,
                null,
                command.ServiceId,
                command.RecipeId,
                command.Title,
                command.Urgency,
                PagerDutyState.Triggered,
                command.ReferenceTime,
                Now,
                1,
                CaseProgression.Open,
                false,
                null,
                string.Empty,
                null,
                command.Labels)
            {
                Team = "payments",
                Origin = new DomainCaseOrigin(
                    DomainCaseOriginKind.Agent,
                    null),
                CreatedBy = caller.PrincipalName,
                InputVersion = 0,
                ProjectedInputVersion = 0,
                PublishToSlack = false
            };
            Record("create", CaseId, caller);
            return Task.FromResult(new CreateCaseResult(StoredCase, false));
        }

        public Task<AppendCrumbsResult> AppendCrumbsAsync(
            Guid caseId,
            AppendCrumbs command,
            CallerIdentity caller,
            CancellationToken cancellationToken)
        {
            AppendCalls++;
            LastAppend = command;
            var caseRecord = RequireCase(caseId);
            StoredCase = caseRecord with
            {
                InputVersion = caseRecord.InputVersion + command.Crumbs.Count,
                UpdatedAt = Now.AddMinutes(1)
            };
            Record("append", caseId, caller);
            return Task.FromResult(new AppendCrumbsResult(
                command.Crumbs.Count,
                0,
                StoredCase.InputVersion,
                StoredCase.ProjectedInputVersion,
                true,
                false));
        }

        public Task<RebuildCaseResult> QueueRebuildAsync(
            Guid caseId,
            CallerIdentity caller,
            CancellationToken cancellationToken)
        {
            var caseRecord = RequireCase(caseId);
            Record("rebuild", caseId, caller);
            return Task.FromResult(new RebuildCaseResult(
                caseId,
                caseRecord.InputVersion,
                true));
        }

        public Task<RefreshCaseResult> QueueSourceRefreshAsync(
            Guid caseId,
            CallerIdentity caller,
            CancellationToken cancellationToken)
        {
            var caseRecord = RequireCase(caseId);
            Record("refresh", caseId, caller);
            return Task.FromResult(new RefreshCaseResult(
                caseId,
                caseRecord.InputVersion,
                true));
        }

        public Task CloseAsync(
            Guid caseId,
            CallerIdentity caller,
            CancellationToken cancellationToken)
        {
            var caseRecord = RequireCase(caseId);
            StoredCase = caseRecord with
            {
                IsFrozen = true,
                Status = CaseProgression.Resolved,
                UpdatedAt = Now.AddMinutes(2)
            };
            Record("close", caseId, caller);
            return Task.CompletedTask;
        }

        public Task<CaseStatusResponse> GetAsync(
            Guid caseId,
            CallerIdentity caller,
            CancellationToken cancellationToken)
        {
            var caseRecord = RequireCase(caseId);
            Record("get", caseId, caller);
            return Task.FromResult(new CaseStatusResponse(
                caseRecord.Id,
                Panko.Contracts.CaseOriginKind.Agent,
                caseRecord.Status,
                caseRecord.RecipeId,
                caseRecord.ServiceId,
                caseRecord.Title,
                caseRecord.InputVersion,
                caseRecord.ProjectedInputVersion,
                caseRecord.Version,
                caseRecord.CreatedBy,
                caseRecord.UpdatedAt,
                "Transport test summary",
                $"/cases/{caseRecord.Id}"));
        }

        public Task<Page<Panko.Contracts.CaseInput>> ListInputsAsync(
            Guid caseId,
            int offset,
            int limit,
            CallerIdentity caller,
            CancellationToken cancellationToken)
        {
            RequireCase(caseId);
            Record("list-inputs", caseId, caller);
            return Task.FromResult(new Page<Panko.Contracts.CaseInput>(0, []));
        }

        public Task<RecentCases> ListRecentAsync(
            int limit,
            CallerIdentity caller,
            CancellationToken cancellationToken)
        {
            var caseRecord = StoredCase ?? throw new InvalidOperationException("No Case exists.");
            Record("list-recent", caseRecord.Id, caller);
            return Task.FromResult(new RecentCases(0, []));
        }

        private CaseRecord RequireCase(Guid caseId)
        {
            if (StoredCase is not { } caseRecord || caseRecord.Id != caseId)
            {
                throw new CaseNotFoundException(caseId);
            }
            return caseRecord;
        }

        private void Record(string operation, Guid caseId, CallerIdentity caller) =>
            Calls.Add((operation, caseId, caller.PrincipalName));
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "TransportTest";
        public const string AnonymousHeader = "X-Test-Anonymous";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Headers.ContainsKey(AnonymousHeader))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [
                    new Claim("sub", "transport-agent"),
                    new Claim(ClaimTypes.Name, "transport-agent")
                ],
                SchemeName);
            var principal = new ClaimsPrincipal(identity);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, SchemeName)));
        }
    }

    private sealed class AllowAuthorization : ICaseAuthorization
    {
        public List<CasePermission> Permissions { get; } = [];

        public Task AuthorizeRecipeAsync(
            ClaimsPrincipal principal,
            string recipeId,
            CasePermission permission,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AuthorizeTeamAsync(
            ClaimsPrincipal principal,
            string team,
            CasePermission permission,
            CancellationToken cancellationToken)
        {
            Permissions.Add(permission);
            return Task.CompletedTask;
        }
    }

    private sealed class AllowCaseAccess : ICaseAccessAuthorizer
    {
        public Task<CaseAccessGrant?> AuthorizeAsync(
            ClaimsPrincipal principal,
            Guid caseId,
            CaseAccessKind kind,
            CancellationToken cancellationToken) =>
            Task.FromResult<CaseAccessGrant?>(
                new CaseAccessGrant(null!, TeamAccessScope.Unrestricted));
    }

    private sealed class RepositoryStub(
        CaseRecord caseRecord,
        IReadOnlyList<Panko.Api.Cases.CaseInput> inputs) : ICaseInputStore
    {
        public CaseRecord StoredCase { get; set; } = caseRecord;

        public int MutationCalls { get; private set; }

        public Task<CreateCaseResult> CreateAsync(
            CaseRecord proposed,
            DomainCaseFile initialCaseFile,
            Panko.Api.Cases.CaseInput createdInput,
            string producerPrincipal,
            string idempotencyKey,
            string requestHash,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CaseRecord?> GetCaseAsync(
            Guid caseId,
            CancellationToken cancellationToken) =>
            Task.FromResult<CaseRecord?>(StoredCase.Id == caseId ? StoredCase : null);

        public Task<DomainCaseFile?> GetCaseFileAsync(
            Guid caseId,
            CancellationToken cancellationToken) =>
            Task.FromResult<DomainCaseFile?>(null);

        public Task<AppendCrumbsResult> AppendAsync(
            Guid caseId,
            string producerPrincipal,
            string batchId,
            string requestHash,
            IReadOnlyList<NormalizedCrumb> normalizedCrumbs,
            int maximumCrumbsPerCase,
            CancellationToken cancellationToken)
        {
            MutationCalls++;
            throw new InvalidOperationException("PagerDuty Case was mutated.");
        }

        public Task<IReadOnlyList<Panko.Api.Cases.CaseInput>> ListInputsAsync(
            Guid caseId,
            long? throughInputVersion,
            bool includeInactive,
            CancellationToken cancellationToken) =>
            Task.FromResult(inputs);

        public Task<bool> QueueProjectionAsync(
            Guid caseId,
            long targetInputVersion,
            CancellationToken cancellationToken)
        {
            MutationCalls++;
            throw new InvalidOperationException("PagerDuty Case was mutated.");
        }

        public Task<bool> QueueRefreshAsync(
            Guid caseId,
            long targetInputVersion,
            CancellationToken cancellationToken)
        {
            MutationCalls++;
            throw new InvalidOperationException("PagerDuty Case was mutated.");
        }

        public Task<bool> QueueAnalysisAsync(
            Guid caseId,
            long targetInputVersion,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task CloseAsync(
            Guid caseId,
            string producerPrincipal,
            CancellationToken cancellationToken)
        {
            MutationCalls++;
            throw new InvalidOperationException("PagerDuty Case was mutated.");
        }

        public Task<IReadOnlyList<CaseRecord>> ListRecentAsync(
            int limit,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<long> SaveCrumbSourceSnapshotsAsync(
            Guid caseId,
            IReadOnlyList<CrumbSourceResult> results,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CrumbSourceResult>> GetLatestCrumbSourceResultsAsync(
            Guid caseId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SetStatusAsync(
            Guid caseId,
            string status,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int?> CommitProjectionAsync(
            CaseRecord expected,
            DomainCaseFile caseFile,
            long targetInputVersion,
            CancellationToken cancellationToken,
            long? targetWorkflowGeneration = null) =>
            throw new NotSupportedException();

        public Task<int?> CommitAnalysisAsync(
            CaseRecord expected,
            DomainCaseFile caseFile,
            long projectedInputVersion,
            CancellationToken cancellationToken,
            long? targetWorkflowGeneration = null) =>
            throw new NotSupportedException();
    }
}
