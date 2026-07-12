using System.Net;
using System.Text;
using IncidentBot.Api.Connectors;
using IncidentBot.Api.Domain;
using IncidentBot.Api.Incidents;
using IncidentBot.Api.Security;

namespace IncidentBot.Api.Tests;

public sealed class EvidenceSnapshotConnectorTests
{
    private static readonly DateTimeOffset WindowStart = DateTimeOffset.Parse("2026-07-11T09:30:00Z");
    private static readonly DateTimeOffset FirstWindowEnd = DateTimeOffset.Parse("2026-07-11T10:05:00Z");
    private static readonly DateTimeOffset SecondWindowEnd = DateTimeOffset.Parse("2026-07-11T10:10:00Z");

    [Fact]
    public async Task GrafanaMetricSnapshot_RecollectionUsesSameIdentityAndLatestValue()
    {
        var metricCalls = 0;
        var handler = new DelegateHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/api/annotations", StringComparison.Ordinal))
            {
                return Json("[]");
            }

            Assert.EndsWith("/api/ds/query", request.RequestUri.AbsolutePath, StringComparison.Ordinal);
            var maximum = Interlocked.Increment(ref metricCalls) == 1 ? 5 : 9;
            return Json($$"""
                {
                  "results": {
                    "A": {
                      "frames": [{
                        "schema": { "fields": [{ "type": "time" }, { "type": "number" }] },
                        "data": { "values": [[1, 2], [{{maximum - 1}}, {{maximum}}]] }
                      }]
                    }
                  }
                }
                """);
        });
        var profile = new InvestigationProfile
        {
            Id = "profile",
            Grafana = new GrafanaScope
            {
                Connector = Transport("https://grafana.example"),
                OrganizationId = 42,
                Queries =
                [
                    new GrafanaQuery
                    {
                        Name = "request failures",
                        DatasourceUid = "prometheus-main",
                        Expression = "sum(rate(http_requests_failed_total[5m]))",
                        WarningAbove = 3
                    }
                ]
            }
        };
        var connector = new GrafanaEvidenceConnector(
            new StubHttpClientFactory(handler), new ThrowingMcpAdapter(), new SafeTemplateRenderer());

        var first = await connector.CollectAsync(Context(profile), Scope(FirstWindowEnd), CancellationToken.None);
        var second = await connector.CollectAsync(Context(profile), Scope(SecondWindowEnd), CancellationToken.None);

        var firstMetric = Assert.Single(first.Findings);
        var secondMetric = Assert.Single(second.Findings);
        Assert.Equal(firstMetric.Id, secondMetric.Id);
        Assert.Equal("metric-query", secondMetric.ObjectType);
        Assert.Equal("prometheus-main:request failures", secondMetric.ObjectId);
        Assert.Contains("maximum observed value 5", firstMetric.Summary, StringComparison.Ordinal);
        Assert.Contains("maximum observed value 9", secondMetric.Summary, StringComparison.Ordinal);

        var report = ComposeTwice(first, second);

        var accumulatedMetric = Assert.Single(report.Evidence);
        Assert.Equal(secondMetric.Id, accumulatedMetric.Id);
        Assert.Equal(secondMetric.Summary, accumulatedMetric.Summary);
        Assert.Equal(SecondWindowEnd, accumulatedMetric.OccurredAt);
    }

    [Fact]
    public async Task VictoriaLogsRecollection_ReplacesCountAndPreservesDistinctRealEvents()
    {
        var hitsCalls = 0;
        var sampleCalls = 0;
        const string firstEvent = "{\"_time\":\"2026-07-11T10:01:00Z\",\"_msg\":\"connection reset\"}";
        const string secondEvent = "{\"_time\":\"2026-07-11T10:02:00Z\",\"_msg\":\"connection reset\"}";
        var handler = new DelegateHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/select/logsql/hits", StringComparison.Ordinal))
            {
                var total = Interlocked.Increment(ref hitsCalls);
                return Json($$"""{"hits":[{"total":{{total}}}]}""");
            }

            Assert.EndsWith("/select/logsql/query", request.RequestUri.AbsolutePath, StringComparison.Ordinal);
            var sample = Interlocked.Increment(ref sampleCalls) == 1
                ? firstEvent
                : $"{firstEvent}\n{secondEvent}";
            return Text(sample);
        });
        var profile = new InvestigationProfile
        {
            Id = "profile",
            VictoriaLogs = new VictoriaLogsScope
            {
                Connector = Transport("https://logs.example"),
                AccountId = "12",
                ProjectId = "payments",
                StreamFilters = new Dictionary<string, string> { ["environment"] = "production" },
                Queries =
                [
                    new VictoriaLogsQuery
                    {
                        Name = "connection errors",
                        Expression = "_msg:~\"connection reset\""
                    }
                ]
            }
        };
        var connector = new VictoriaLogsEvidenceConnector(
            new StubHttpClientFactory(handler), new ThrowingMcpAdapter(), new SafeTemplateRenderer());

        var first = await connector.CollectAsync(Context(profile), Scope(FirstWindowEnd), CancellationToken.None);
        var second = await connector.CollectAsync(Context(profile), Scope(SecondWindowEnd), CancellationToken.None);

        var firstCount = Assert.Single(first.Findings, finding => finding.Category == "log-count");
        var secondCount = Assert.Single(second.Findings, finding => finding.Category == "log-count");
        Assert.Equal(firstCount.Id, secondCount.Id);
        Assert.Equal("log-query", secondCount.ObjectType);
        Assert.Equal("connection errors", secondCount.ObjectId);
        Assert.Contains(": 1 matching log events", firstCount.Summary, StringComparison.Ordinal);
        Assert.Contains(": 2 matching log events", secondCount.Summary, StringComparison.Ordinal);

        var firstLogEvent = Assert.Single(first.Findings, finding => finding.Category == "first-error");
        var secondRunEvents = second.Findings
            .Where(finding => finding.Category is "first-error" or "log-sample")
            .OrderBy(finding => finding.OccurredAt)
            .ToList();
        Assert.Equal(2, secondRunEvents.Count);
        Assert.Equal(firstLogEvent.Id, secondRunEvents[0].Id);
        Assert.NotEqual(secondRunEvents[0].Id, secondRunEvents[1].Id);

        var report = ComposeTwice(first, second);

        Assert.Single(report.Evidence, finding => finding.Category == "log-count");
        var accumulatedEvents = report.Evidence
            .Where(finding => finding.Category is "first-error" or "log-sample")
            .OrderBy(finding => finding.OccurredAt)
            .ToList();
        Assert.Equal(2, accumulatedEvents.Count);
        Assert.Equal(new[]
        {
            DateTimeOffset.Parse("2026-07-11T10:01:00Z"),
            DateTimeOffset.Parse("2026-07-11T10:02:00Z")
        }, accumulatedEvents.Select(finding => finding.OccurredAt));
    }

    [Fact]
    public async Task PagerDutyRecollection_ReplacesMutableIncidentStatusSnapshot()
    {
        var calls = 0;
        var handler = new DelegateHandler(_ =>
        {
            var status = Interlocked.Increment(ref calls) == 1 ? "triggered" : "resolved";
            return Json($$"""
                {
                  "incident": {
                    "id": "PD-1",
                    "status": "{{status}}",
                    "created_at": "2026-07-11T10:00:00Z",
                    "html_url": "https://pagerduty.example/incidents/PD-1"
                  }
                }
                """);
        });
        var profile = new InvestigationProfile
        {
            Id = "profile",
            PagerDuty = new PagerDutyScope { Connector = Transport("https://pagerduty.example/api") }
        };
        var connector = new PagerDutyEvidenceConnector(
            new StubHttpClientFactory(handler), new ThrowingMcpAdapter());

        var first = await connector.CollectAsync(Context(profile), Scope(FirstWindowEnd), CancellationToken.None);
        var second = await connector.CollectAsync(Context(profile), Scope(SecondWindowEnd), CancellationToken.None);

        var firstIncident = Assert.Single(first.Findings);
        var secondIncident = Assert.Single(second.Findings);
        Assert.Equal(firstIncident.Id, secondIncident.Id);
        Assert.Contains("triggered", firstIncident.Summary, StringComparison.Ordinal);
        Assert.Contains("resolved", secondIncident.Summary, StringComparison.Ordinal);

        var report = ComposeTwice(first, second);

        var retained = Assert.Single(report.Evidence);
        Assert.Equal(secondIncident.Id, retained.Id);
        Assert.Equal(secondIncident.Summary, retained.Summary);
    }

    [Fact]
    public async Task NomadRecollection_ReplacesMutableStatusesForStableObjects()
    {
        var collection = 0;
        var handler = new DelegateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/v1/job/payments", StringComparison.Ordinal))
            {
                var current = Interlocked.Increment(ref collection);
                var status = current == 1 ? "pending" : "dead";
                return Json($$"""{"Status":"{{status}}","SubmitTime":"2026-07-11T10:00:00Z"}""");
            }

            var secondCollection = Volatile.Read(ref collection) > 1;
            if (path.EndsWith("/allocations", StringComparison.Ordinal))
            {
                var status = secondCollection ? "lost" : "failed";
                return Json($$"""[{"ID":"alloc-123","ClientStatus":"{{status}}","ModifyTime":"2026-07-11T10:01:00Z"}]""");
            }
            if (path.EndsWith("/deployments", StringComparison.Ordinal))
            {
                var status = secondCollection ? "failed" : "running";
                return Json($$"""[{"ID":"deploy-123","Status":"{{status}}","ModifyTime":"2026-07-11T10:02:00Z"}]""");
            }

            Assert.EndsWith("/evaluations", path, StringComparison.Ordinal);
            var evaluationStatus = secondCollection ? "canceled" : "blocked";
            return Json($$"""[{"ID":"eval-123","Status":"{{evaluationStatus}}","ModifyTime":"2026-07-11T10:03:00Z"}]""");
        });
        var profile = new InvestigationProfile
        {
            Id = "profile",
            Nomad = new NomadScope
            {
                Connector = Transport("https://nomad.example"),
                Region = "global",
                Namespaces = [new NomadNamespace { Name = "production", Jobs = ["payments"] }]
            }
        };
        var connector = new NomadEvidenceConnector(
            new StubHttpClientFactory(handler), new ThrowingMcpAdapter());

        var first = await connector.CollectAsync(Context(profile), Scope(FirstWindowEnd), CancellationToken.None);
        var second = await connector.CollectAsync(Context(profile), Scope(SecondWindowEnd), CancellationToken.None);

        var firstByObject = first.Findings.ToDictionary(FindingObjectIdentity, StringComparer.Ordinal);
        var secondByObject = second.Findings.ToDictionary(FindingObjectIdentity, StringComparer.Ordinal);
        Assert.Equal(firstByObject.Keys.Order(StringComparer.Ordinal), secondByObject.Keys.Order(StringComparer.Ordinal));
        Assert.All(firstByObject, pair => Assert.Equal(pair.Value.Id, secondByObject[pair.Key].Id));
        Assert.Contains(second.Findings, finding => finding.Summary.Contains("is dead", StringComparison.Ordinal));
        Assert.Contains(second.Findings, finding => finding.Summary.Contains("is lost", StringComparison.Ordinal));

        var report = ComposeTwice(first, second);

        Assert.Equal(second.Findings.Count, report.Evidence.Count);
        Assert.All(second.Findings, expected =>
            Assert.Equal(expected.Summary, report.Evidence.Single(actual => actual.Id == expected.Id).Summary));
    }

    private static InvestigationReport ComposeTwice(ConnectorResult first, ConnectorResult second)
    {
        var composer = new ReportComposer(
            TimeProvider.System,
            new EvidenceSourceRegistry(Array.Empty<IIncidentEvidenceConnector>()));
        var incident = new IncidentRecord(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "PD-1",
            "payments",
            "profile",
            "Payments failing",
            "high",
            IncidentState.Triggered,
            DateTimeOffset.Parse("2026-07-11T10:00:00Z"),
            DateTimeOffset.Parse("2026-07-11T10:00:01Z"),
            1,
            "collecting",
            false,
            null,
            "#incidents",
            null,
            new Dictionary<string, string>());
        var profile = new InvestigationProfile { Id = "profile" };
        var ai = new AiSynthesis("unavailable", null, [], [], [], null);
        var initial = composer.Compose(incident, profile, "v1", [first], null, ai);
        return composer.Compose(incident, profile, "v1", [second], initial, ai);
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

    private static string FindingObjectIdentity(EvidenceFinding finding) =>
        $"{finding.ObjectType}:{finding.ObjectId}";

    private static EvidenceScope Scope(DateTimeOffset end) => new(WindowStart, end, "v1", 100, 262144);

    private static ConnectorTransport Transport(string baseUrl) => new()
    {
        Mode = "api",
        BaseUrl = baseUrl,
        TimeoutSeconds = 5,
        MaxItems = 100,
        MaxBytes = 262144
    };

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Text(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/x-ndjson")
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
