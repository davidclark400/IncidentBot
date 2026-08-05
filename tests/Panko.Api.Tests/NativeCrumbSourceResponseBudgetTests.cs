using System.Net;
using System.Text;
using Panko.Api.Crumbs;
using Panko.Api.Domain;
using Panko.Api.Options;
using Panko.Api.Security;

namespace Panko.Api.Tests;

public sealed class NativeConnectorResponseBudgetTests
{
    [Fact]
    public async Task BudgetedReadsReserveFairSharesAndReuseUnusedCapacity()
    {
        var budget = new CrumbSourceResponseBudget(
            scopeMaximumBytes: 100,
            sourceMaximumBytes: 200,
            plannedOperations: 3);

        using var first = await budget.TryReadJsonAsync(
            "first", _ => Task.FromResult(JsonBytes(3)), CancellationToken.None);
        using var second = await budget.TryReadJsonAsync(
            "second", _ => Task.FromResult(JsonBytes(49)), CancellationToken.None);
        using var third = await budget.TryReadJsonAsync(
            "third", _ => Task.FromResult(JsonBytes(49)), CancellationToken.None);

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.NotNull(third);
        Assert.True(budget.IsPartial);
        Assert.Contains("used 100 of 100 bytes", budget.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("second", budget.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SkippedPlannedOperationRedistributesItsShare()
    {
        var budget = new CrumbSourceResponseBudget(100, 100, 3);
        budget.SkipPlannedOperation();

        using var first = await budget.TryReadJsonAsync(
            "first", _ => Task.FromResult(JsonBytes(50)), CancellationToken.None);
        using var second = await budget.TryReadJsonAsync(
            "second", _ => Task.FromResult(JsonBytes(50)), CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.False(budget.IsPartial);
        Assert.Null(budget.Diagnostic);
    }

    [Fact]
    public async Task BudgetDiagnosticIsBoundedAndDoesNotSendUnfundedOperations()
    {
        var budget = new CrumbSourceResponseBudget(0, 100, 20);
        var sends = 0;
        for (var index = 0; index < 20; index++)
        {
            using var json = await budget.TryReadJsonAsync(
                $"operation-{index}-{new string('x', 200)}",
                _ =>
                {
                    sends++;
                    return Task.FromResult(Json("[]"));
                },
                CancellationToken.None);
            Assert.Null(json);
        }

        Assert.Equal(0, sends);
        Assert.NotNull(budget.Diagnostic);
        Assert.True(budget.Diagnostic.Length <= 500);
    }

    [Fact]
    public async Task ChunkedOverflowUsesTheLastSourceByteAsProof()
    {
        var budget = new CrumbSourceResponseBudget(5, 100, 1);
        await using var stream = new NonSeekableMemoryStream(Encoding.UTF8.GetBytes("[123456789]"));

        using var json = await budget.TryReadJsonAsync(
            "chunked",
            _ => Task.FromResult(new HttpResponseMessage
            {
                Content = new StreamContent(stream)
            }),
            CancellationToken.None);

        Assert.Null(json);
        Assert.Equal(5, stream.BytesRead);
        Assert.Contains("used 5 of 5 bytes", budget.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeclaredOversizeExhaustsTheShareWithoutReadingTheBody()
    {
        var budget = new CrumbSourceResponseBudget(10, 100, 1);
        await using var stream = new NonSeekableMemoryStream(new byte[100]);

        using var json = await budget.TryReadJsonAsync(
            "declared",
            _ =>
            {
                var response = new HttpResponseMessage
                {
                    Content = new StreamContent(stream)
                };
                response.Content.Headers.ContentLength = 100;
                return Task.FromResult(response);
            },
            CancellationToken.None);

        Assert.Null(json);
        Assert.Equal(0, stream.BytesRead);
        Assert.Contains("used 10 of 10 bytes", budget.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BudgetedReadForwardsCancellationWithoutMarkingTheBudgetPartial()
    {
        var budget = new CrumbSourceResponseBudget(100, 100, 1);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            budget.TryReadJsonAsync(
                "cancelled",
                operationCancellationToken =>
                {
                    Assert.Equal(cancellation.Token, operationCancellationToken);
                    return Task.FromCanceled<HttpResponseMessage>(operationCancellationToken);
                },
                cancellation.Token));

        Assert.False(budget.IsPartial);
        Assert.Null(budget.Diagnostic);
    }

    [Fact]
    public async Task GrafanaOversizedQueryDoesNotStarveLaterQuery()
    {
        var queryCalls = 0;
        var totalCalls = 0;
        var handler = new DelegateHandler(request =>
        {
            Interlocked.Increment(ref totalCalls);
            if (request.RequestUri!.AbsolutePath.EndsWith("/api/annotations", StringComparison.Ordinal))
            {
                return Json("[]");
            }

            Assert.EndsWith("/api/ds/query", request.RequestUri.AbsolutePath, StringComparison.Ordinal);
            return Interlocked.Increment(ref queryCalls) == 1
                ? Text(new string('x', 600), "application/json")
                : Json("""
                    {"results":{"B":{"frames":[{"schema":{"fields":[{"type":"number"}]},"data":{"values":[[7]]}}]}}}
                    """);
        });
        var transport = Transport("https://grafana.example", maxBytes: 5000);
        var recipe = new Recipe
        {
            Id = "recipe",
            Grafana = new GrafanaScope
            {
                Queries =
                [
                    new GrafanaQuery
                    {
                        Name = "oversized",
                        DatasourceUid = "prometheus",
                        Expression = "oversized"
                    },
                    new GrafanaQuery
                    {
                        Name = "retained",
                        DatasourceUid = "prometheus",
                        Expression = "retained",
                        WarningThreshold = 5
                    }
                ]
            }
        };
        var connector = new GrafanaCrumbSource(
            new StubHttpClientFactory(handler), new ThrowingMcpAdapter(), new SafeTemplateRenderer(),
            TestConfiguration.CrumbSources(grafana: transport), TestConfiguration.Credentials());

        var result = await connector.CollectAsync(
            Context(recipe), Scope(maxBytes: 500), CancellationToken.None);

        Assert.Equal(CrumbSourceHealth.Partial, result.Health);
        Assert.Equal(3, totalCalls);
        Assert.DoesNotContain(result.Crumbs, crumb => crumb.Summary.StartsWith("oversized:", StringComparison.Ordinal));
        Assert.Contains(result.Crumbs, crumb => crumb.Summary == "retained: maximum observed value 7");
        Assert.Contains("500 bytes", result.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("oversized", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VictoriaLogsCollectsAllCountsBeforeSamplesAndPreservesLaterQuery()
    {
        var hitsCalls = 0;
        var paths = new List<string>();
        var handler = new DelegateHandler(request =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath.EndsWith("/select/logsql/hits", StringComparison.Ordinal))
            {
                return Interlocked.Increment(ref hitsCalls) == 1
                    ? Text(new string('x', 600), "application/json")
                    : Json("{\"hits\":[{\"total\":1}]}");
            }

            Assert.EndsWith("/select/logsql/query", request.RequestUri.AbsolutePath, StringComparison.Ordinal);
            return Text(
                "{\"_time\":\"2026-07-11T10:01:00Z\",\"_msg\":\"retained failure\"}",
                "application/x-ndjson");
        });
        var transport = Transport("https://logs.example", maxBytes: 5000);
        var recipe = new Recipe
        {
            Id = "recipe",
            VictoriaLogs = new VictoriaLogsScope
            {
                Queries =
                [
                    new VictoriaLogsQuery { Name = "oversized", Expression = "oversized" },
                    new VictoriaLogsQuery { Name = "retained", Expression = "retained" }
                ]
            }
        };
        var connector = new VictoriaLogsCrumbSource(
            new StubHttpClientFactory(handler), new ThrowingMcpAdapter(), new SafeTemplateRenderer(),
            TestConfiguration.CrumbSources(victoriaLogs: transport), TestConfiguration.Credentials());

        var result = await connector.CollectAsync(
            Context(recipe), Scope(maxBytes: 600), CancellationToken.None);

        Assert.Equal(CrumbSourceHealth.Partial, result.Health);
        Assert.Equal(
            ["/select/logsql/hits", "/select/logsql/hits", "/select/logsql/query"],
            paths);
        Assert.DoesNotContain(result.Crumbs, crumb => crumb.ObjectId == "oversized");
        Assert.Contains(result.Crumbs, crumb =>
            crumb.ObjectId == "retained" && crumb.Category == "log-count");
        Assert.Contains(result.Crumbs, crumb =>
            crumb.ObjectId == "retained" && crumb.Category == "first-error");
        Assert.Contains("oversized", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NomadCollectsEveryJobStateBeforeAnyJobDetails()
    {
        var paths = new List<string>();
        var handler = new DelegateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            paths.Add(path);
            if (path == "/v1/job/one")
            {
                return Text(new string('x', 600), "application/json");
            }

            if (path == "/v1/job/two")
            {
                return Json("{\"Status\":\"dead\",\"SubmitTime\":\"2026-07-11T10:00:00Z\"}");
            }

            return Json("[]");
        });
        var transport = Transport("https://nomad.example", maxBytes: 5000);
        var recipe = new Recipe
        {
            Id = "recipe",
            Nomad = new NomadScope
            {
                Namespaces =
                [
                    new NomadNamespace { Name = "production", Jobs = ["one", "two"] }
                ]
            }
        };
        var connector = new NomadCrumbSource(
            new StubHttpClientFactory(handler), new ThrowingMcpAdapter(),
            TestConfiguration.CrumbSources(nomad: transport), TestConfiguration.Credentials());

        var result = await connector.CollectAsync(
            Context(recipe), Scope(maxBytes: 1200), CancellationToken.None);

        Assert.Equal(CrumbSourceHealth.Partial, result.Health);
        Assert.True(paths.Count >= 2);
        Assert.Equal("/v1/job/one", paths[0]);
        Assert.Equal("/v1/job/two", paths[1]);
        Assert.DoesNotContain(result.Crumbs, crumb => crumb.ObjectId == "production/one");
        Assert.Contains(result.Crumbs, crumb =>
            crumb.ObjectId == "production/two" && crumb.Summary.Contains("is dead", StringComparison.Ordinal));
        Assert.Contains("production/one", result.Diagnostic, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(80, 5000)]
    [InlineData(5000, 80)]
    public async Task PagerDutyUsesTheLowerScopeOrConnectorLimit(int scopeBytes, int connectorBytes)
    {
        var calls = 0;
        var handler = new DelegateHandler(_ =>
        {
            Interlocked.Increment(ref calls);
            return Text(new string('x', 200), "application/json");
        });
        var transport = Transport("https://pagerduty.example/api", connectorBytes);
        var recipe = new Recipe
        {
            Id = "recipe",
            PagerDuty = new PagerDutyScope()
        };
        var connector = new PagerDutyCrumbSource(
            new StubHttpClientFactory(handler), new ThrowingMcpAdapter(),
            TestConfiguration.CrumbSources(pagerDuty: transport), TestConfiguration.Credentials());

        var result = await connector.CollectAsync(
            Context(recipe), Scope(scopeBytes), CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal(CrumbSourceHealth.Partial, result.Health);
        Assert.Empty(result.Crumbs);
        Assert.Contains("used 80 of 80 bytes", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GrafanaCapsLinksAndMarksItemTruncationPartial()
    {
        var transport = Transport("https://grafana.example", maxBytes: 5000, maxItems: 1);
        var recipe = new Recipe
        {
            Id = "recipe",
            Grafana = new GrafanaScope
            {
                Dashboards =
                [
                    new GrafanaDashboard { Uid = "one" },
                    new GrafanaDashboard { Uid = "two" }
                ]
            }
        };
        var connector = new GrafanaCrumbSource(
            new StubHttpClientFactory(new DelegateHandler(_ => Json("[]"))),
            new ThrowingMcpAdapter(),
            new SafeTemplateRenderer(),
            TestConfiguration.CrumbSources(grafana: transport),
            TestConfiguration.Credentials());

        var result = await connector.CollectAsync(
            Context(recipe), Scope(maxBytes: 5000), CancellationToken.None);

        Assert.Equal(CrumbSourceHealth.Partial, result.Health);
        Assert.Single(result.Links);
        Assert.Contains("item limit 1", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VictoriaLogsCapsCrumbsAndLinksAndMarksItemTruncationPartial()
    {
        var transport = Transport("https://logs.example", maxBytes: 5000, maxItems: 1);
        var recipe = new Recipe
        {
            Id = "recipe",
            VictoriaLogs = new VictoriaLogsScope
            {
                Queries =
                [
                    new VictoriaLogsQuery { Name = "one", Expression = "one" },
                    new VictoriaLogsQuery { Name = "two", Expression = "two" }
                ]
            }
        };
        var connector = new VictoriaLogsCrumbSource(
            new StubHttpClientFactory(new DelegateHandler(_ => Json("{\"hits\":[{\"total\":0}]}"))),
            new ThrowingMcpAdapter(),
            new SafeTemplateRenderer(),
            TestConfiguration.CrumbSources(victoriaLogs: transport),
            TestConfiguration.Credentials());

        var result = await connector.CollectAsync(
            Context(recipe), Scope(maxBytes: 5000), CancellationToken.None);

        Assert.Equal(CrumbSourceHealth.Partial, result.Health);
        Assert.Single(result.Crumbs);
        Assert.Single(result.Links);
        Assert.Contains("item limit 1", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NomadCapsAllOutputCollectionsAndMarksItemTruncationPartial()
    {
        var handler = new DelegateHandler(request =>
            request.RequestUri!.AbsolutePath is "/v1/job/one" or "/v1/job/two"
                ? Json("{\"Status\":\"running\",\"SubmitTime\":\"2026-07-11T10:00:00Z\"}")
                : Json("[]"));
        var transport = Transport("https://nomad.example", maxBytes: 5000, maxItems: 1);
        var recipe = new Recipe
        {
            Id = "recipe",
            Nomad = new NomadScope
            {
                Namespaces = [new NomadNamespace { Name = "production", Jobs = ["one", "two"] }]
            }
        };
        var connector = new NomadCrumbSource(
            new StubHttpClientFactory(handler), new ThrowingMcpAdapter(),
            TestConfiguration.CrumbSources(nomad: transport), TestConfiguration.Credentials());

        var result = await connector.CollectAsync(
            Context(recipe), Scope(maxBytes: 5000), CancellationToken.None);

        Assert.Equal(CrumbSourceHealth.Partial, result.Health);
        Assert.Single(result.Crumbs);
        Assert.Single(result.Trail);
        Assert.Single(result.Links);
        Assert.Contains("item limit 1", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    private static CaseContext Context(Recipe recipe) => new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        "PD-1",
        "payments",
        "Payments failing",
        "high",
        PagerDutyIncidentState.Triggered,
        DateTimeOffset.Parse("2026-07-11T10:00:00Z"),
        new Dictionary<string, string>(),
        recipe);

    private static CrumbScope Scope(int maxBytes) => new(
        DateTimeOffset.Parse("2026-07-11T09:30:00Z"),
        DateTimeOffset.Parse("2026-07-11T10:10:00Z"),
        "v1",
        100,
        maxBytes);

    private static ConnectorTransport Transport(string baseUrl, int maxBytes, int maxItems = 100) => new()
    {
        Mode = "api",
        BaseUrl = baseUrl,
        CredentialEnv = "TEST_TOKEN",
        TimeoutSeconds = 5,
        MaxItems = maxItems,
        MaxBytes = maxBytes
    };

    private static HttpResponseMessage Json(string value) => Text(value, "application/json");

    private static HttpResponseMessage JsonBytes(int byteCount) =>
        Json($"\"{new string('x', byteCount - 2)}\"");

    private static HttpResponseMessage Text(string value, string mediaType) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, mediaType)
    };

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(response(request));
    }

    private sealed class ThrowingMcpAdapter : IMcpCrumbSourceAdapter
    {
        public Task<CrumbSourceResult> CollectAsync(
            string source,
            McpToolConfiguration configuration,
            CaseContext context,
            CrumbScope scope,
            object allowedResources,
            string? allowedBaseUrl,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("MCP should not be called by native connector tests.");
    }

    private sealed class NonSeekableMemoryStream(byte[] buffer) : MemoryStream(buffer, writable: false)
    {
        public int BytesRead { get; private set; }
        public override bool CanSeek => false;

        public override async ValueTask<int> ReadAsync(
            Memory<byte> destination,
            CancellationToken cancellationToken = default)
        {
            var count = await base.ReadAsync(destination, cancellationToken);
            BytesRead += count;
            return count;
        }
    }
}
