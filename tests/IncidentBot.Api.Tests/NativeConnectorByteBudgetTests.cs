using System.Net;
using System.Text;
using IncidentBot.Api.Connectors;
using IncidentBot.Api.Domain;
using IncidentBot.Api.Security;

namespace IncidentBot.Api.Tests;

public sealed class NativeConnectorByteBudgetTests
{
    [Fact]
    public void BudgetReservesFairSharesAndReusesUnusedCapacity()
    {
        var budget = new ConnectorByteBudget(
            scopeMaximumBytes: 100,
            connectorMaximumBytes: 200,
            plannedOperations: 3);

        var first = budget.BeginOperation("first");
        Assert.Equal(33, first);
        budget.ObserveBytesRead(3);

        var second = budget.BeginOperation("second");
        Assert.Equal(48, second);
        budget.ObserveBytesRead(second);

        var third = budget.BeginOperation("third");
        Assert.Equal(49, third);
        Assert.Equal(49, budget.RemainingBytes);
        Assert.Equal(100, budget.MaximumBytes);
    }

    [Fact]
    public void BudgetDiagnosticIsBounded()
    {
        var budget = new ConnectorByteBudget(100, 100, 20);
        for (var index = 0; index < 20; index++)
        {
            budget.RecordLimited($"operation-{index}-{new string('x', 200)}");
        }

        Assert.NotNull(budget.Diagnostic);
        Assert.True(budget.Diagnostic.Length <= 500);
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
        var profile = new InvestigationProfile
        {
            Id = "profile",
            Grafana = new GrafanaScope
            {
                Connector = Transport("https://grafana.example", maxBytes: 5000),
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
                        WarningAbove = 5
                    }
                ]
            }
        };
        var connector = new GrafanaEvidenceConnector(
            new StubHttpClientFactory(handler), new ThrowingMcpAdapter(), new SafeTemplateRenderer());

        var result = await connector.CollectAsync(
            Context(profile), Scope(maxBytes: 500), CancellationToken.None);

        Assert.Equal(SourceHealth.Partial, result.Health);
        Assert.Equal(3, totalCalls);
        Assert.DoesNotContain(result.Findings, finding => finding.Summary.StartsWith("oversized:", StringComparison.Ordinal));
        Assert.Contains(result.Findings, finding => finding.Summary == "retained: maximum observed value 7");
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
        var profile = new InvestigationProfile
        {
            Id = "profile",
            VictoriaLogs = new VictoriaLogsScope
            {
                Connector = Transport("https://logs.example", maxBytes: 5000),
                Queries =
                [
                    new VictoriaLogsQuery { Name = "oversized", Expression = "oversized" },
                    new VictoriaLogsQuery { Name = "retained", Expression = "retained" }
                ]
            }
        };
        var connector = new VictoriaLogsEvidenceConnector(
            new StubHttpClientFactory(handler), new ThrowingMcpAdapter(), new SafeTemplateRenderer());

        var result = await connector.CollectAsync(
            Context(profile), Scope(maxBytes: 600), CancellationToken.None);

        Assert.Equal(SourceHealth.Partial, result.Health);
        Assert.Equal(
            ["/select/logsql/hits", "/select/logsql/hits", "/select/logsql/query"],
            paths);
        Assert.DoesNotContain(result.Findings, finding => finding.ObjectId == "oversized");
        Assert.Contains(result.Findings, finding =>
            finding.ObjectId == "retained" && finding.Category == "log-count");
        Assert.Contains(result.Findings, finding =>
            finding.ObjectId == "retained" && finding.Category == "first-error");
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
        var profile = new InvestigationProfile
        {
            Id = "profile",
            Nomad = new NomadScope
            {
                Connector = Transport("https://nomad.example", maxBytes: 5000),
                Namespaces =
                [
                    new NomadNamespace { Name = "production", Jobs = ["one", "two"] }
                ]
            }
        };
        var connector = new NomadEvidenceConnector(
            new StubHttpClientFactory(handler), new ThrowingMcpAdapter());

        var result = await connector.CollectAsync(
            Context(profile), Scope(maxBytes: 1200), CancellationToken.None);

        Assert.Equal(SourceHealth.Partial, result.Health);
        Assert.True(paths.Count >= 2);
        Assert.Equal("/v1/job/one", paths[0]);
        Assert.Equal("/v1/job/two", paths[1]);
        Assert.DoesNotContain(result.Findings, finding => finding.ObjectId == "production/one");
        Assert.Contains(result.Findings, finding =>
            finding.ObjectId == "production/two" && finding.Summary.Contains("is dead", StringComparison.Ordinal));
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
        var profile = new InvestigationProfile
        {
            Id = "profile",
            PagerDuty = new PagerDutyScope
            {
                Connector = Transport("https://pagerduty.example/api", connectorBytes)
            }
        };
        var connector = new PagerDutyEvidenceConnector(
            new StubHttpClientFactory(handler), new ThrowingMcpAdapter());

        var result = await connector.CollectAsync(
            Context(profile), Scope(scopeBytes), CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal(SourceHealth.Partial, result.Health);
        Assert.Empty(result.Findings);
        Assert.Contains("used 80 of 80 bytes", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GrafanaCapsLinksAndMarksItemTruncationPartial()
    {
        var profile = new InvestigationProfile
        {
            Id = "profile",
            Grafana = new GrafanaScope
            {
                Connector = Transport("https://grafana.example", maxBytes: 5000, maxItems: 1),
                Dashboards =
                [
                    new GrafanaDashboard { Uid = "one" },
                    new GrafanaDashboard { Uid = "two" }
                ]
            }
        };
        var connector = new GrafanaEvidenceConnector(
            new StubHttpClientFactory(new DelegateHandler(_ => Json("[]"))),
            new ThrowingMcpAdapter(),
            new SafeTemplateRenderer());

        var result = await connector.CollectAsync(
            Context(profile), Scope(maxBytes: 5000), CancellationToken.None);

        Assert.Equal(SourceHealth.Partial, result.Health);
        Assert.Single(result.Links);
        Assert.Contains("item limit 1", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VictoriaLogsCapsFindingsAndLinksAndMarksItemTruncationPartial()
    {
        var profile = new InvestigationProfile
        {
            Id = "profile",
            VictoriaLogs = new VictoriaLogsScope
            {
                Connector = Transport("https://logs.example", maxBytes: 5000, maxItems: 1),
                Queries =
                [
                    new VictoriaLogsQuery { Name = "one", Expression = "one" },
                    new VictoriaLogsQuery { Name = "two", Expression = "two" }
                ]
            }
        };
        var connector = new VictoriaLogsEvidenceConnector(
            new StubHttpClientFactory(new DelegateHandler(_ => Json("{\"hits\":[{\"total\":0}]}"))),
            new ThrowingMcpAdapter(),
            new SafeTemplateRenderer());

        var result = await connector.CollectAsync(
            Context(profile), Scope(maxBytes: 5000), CancellationToken.None);

        Assert.Equal(SourceHealth.Partial, result.Health);
        Assert.Single(result.Findings);
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
        var profile = new InvestigationProfile
        {
            Id = "profile",
            Nomad = new NomadScope
            {
                Connector = Transport("https://nomad.example", maxBytes: 5000, maxItems: 1),
                Namespaces = [new NomadNamespace { Name = "production", Jobs = ["one", "two"] }]
            }
        };
        var connector = new NomadEvidenceConnector(
            new StubHttpClientFactory(handler), new ThrowingMcpAdapter());

        var result = await connector.CollectAsync(
            Context(profile), Scope(maxBytes: 5000), CancellationToken.None);

        Assert.Equal(SourceHealth.Partial, result.Health);
        Assert.Single(result.Findings);
        Assert.Single(result.Timeline);
        Assert.Single(result.Links);
        Assert.Contains("item limit 1", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    private static InvestigationContext Context(InvestigationProfile profile) => new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        "PD-1",
        "payments",
        "Payments failing",
        "high",
        IncidentState.Triggered,
        DateTimeOffset.Parse("2026-07-11T10:00:00Z"),
        new Dictionary<string, string>(),
        profile);

    private static EvidenceScope Scope(int maxBytes) => new(
        DateTimeOffset.Parse("2026-07-11T09:30:00Z"),
        DateTimeOffset.Parse("2026-07-11T10:10:00Z"),
        "v1",
        100,
        maxBytes);

    private static ConnectorTransport Transport(string baseUrl, int maxBytes, int maxItems = 100) => new()
    {
        Mode = "api",
        BaseUrl = baseUrl,
        TimeoutSeconds = 5,
        MaxItems = maxItems,
        MaxBytes = maxBytes
    };

    private static HttpResponseMessage Json(string value) => Text(value, "application/json");

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

    private sealed class ThrowingMcpAdapter : IMcpEvidenceAdapter
    {
        public Task<ConnectorResult> CollectAsync(
            string source,
            McpToolConfiguration configuration,
            InvestigationContext context,
            EvidenceScope scope,
            object allowedResources,
            string? allowedBaseUrl,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("MCP should not be called by native connector tests.");
    }
}
