using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentBot.Api.Connectors;
using IncidentBot.Api.Domain;
using IncidentBot.Api.Profiles;
using IncidentBot.Kafka;

namespace IncidentBot.Api.Tests;

public sealed class KafkaEvidenceConnectorTests
{
    private static readonly DateTimeOffset TriggeredAt = DateTimeOffset.Parse("2026-07-14T10:00:00Z");

    [Fact]
    public async Task TargetsAreSortedAndBatchedAtEightWithoutResourceFanout()
    {
        var requestBodies = new List<JsonNode>();
        var handler = new RecordingHandler(async request =>
        {
            Assert.Equal("/api/ds/query", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("read-token", request.Headers.Authorization.Parameter);
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
            requestBodies.Add(body);
            return Json(EmptyResults(body["queries"]!.AsArray().Count));
        });
        var connector = Connector(
            handler,
            KafkaMetricCatalog.Load(Path.Combine(AppContext.BaseDirectory, "config", "kafka-metric-packs.yaml")),
            maxItems: 100);

        await connector.CollectAsync(
            Context(new KafkaProfileScope
            {
                MetricPackId = "synthetic-fixture-kafka-v1",
                Cluster = "prod",
                Topics = ["orders", "payments"],
                ConsumerGroups = ["workers-a", "workers-b"]
            }),
            Scope(),
            CancellationToken.None);

        Assert.Equal([8, 8, 8, 2], requestBodies.Select(body => body["queries"]!.AsArray().Count));
        var expressions = requestBodies
            .SelectMany(body => body["queries"]!.AsArray())
            .Select(query => query!["expr"]!.GetValue<string>())
            .ToArray();
        Assert.Equal(26, expressions.Length);
        Assert.All(expressions, expression =>
        {
            Assert.Contains("(prod)", expression, StringComparison.Ordinal);
            Assert.DoesNotContain("{{", expression, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task ParsesReducersThresholdsTimestampsAndStableIds()
    {
        var catalog = Catalog(
            Metric("consumer-lag", ConsumerExpression, "consumer-group", "maximum", "anomaly", "required", 10, 20),
            Metric("topic-rate", TopicExpression, "topic", "average", "context", "required", 100, 200));
        var handler = new RecordingHandler(_ => Task.FromResult(Json("""
            {
              "results": {
                "A": {"frames":[{"schema":{"fields":[
                  {"name":"Time","type":"time"},
                  {"name":"lag","type":"number","labels":{"cluster":"prod","topic":"orders","consumer_group":"workers"}}
                ]},"data":{"values":[[1784023140000,1784023200000],[5,25]]}}]},
                "B": {"frames":[{"schema":{"fields":[
                  {"name":"Time","type":"time"},
                  {"name":"rate","type":"number","labels":{"cluster":"prod","topic":"orders"}}
                ]},"data":{"values":[[1784023140000,1784023200000],[100,200]]}}]}
              }
            }
            """)));
        var connector = Connector(handler, catalog);
        var profile = new KafkaProfileScope
        {
            MetricPackId = "test-pack",
            Cluster = "prod",
            Topics = ["orders"],
            ConsumerGroups = ["workers"]
        };

        var first = await connector.CollectAsync(Context(profile), Scope(), CancellationToken.None);
        var second = await connector.CollectAsync(
            Context(profile),
            Scope(TriggeredAt.AddHours(-2), TriggeredAt.AddHours(-1)),
            CancellationToken.None);

        Assert.Equal(SourceHealth.Complete, first.Health);
        Assert.Equal(2, first.Findings.Count);
        var lag = Assert.Single(first.Findings, finding => finding.Category == "kafka-consumer-lag");
        Assert.Equal("critical", lag.Severity);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1784023200000), lag.OccurredAt);
        Assert.Equal("prod/orders/workers", lag.ObjectId);
        Assert.True(lag.Provenance["scope"]!["timestampSupported"]!.GetValue<bool>());
        Assert.Equal("critical", lag.Provenance["scope"]!["thresholdState"]!.GetValue<string>());
        var context = Assert.Single(first.Findings, finding => finding.Category == "kafka-topic-rate");
        Assert.Equal("info", context.Severity);
        Assert.Equal(TriggeredAt.AddMinutes(10), context.OccurredAt);
        Assert.False(context.Provenance["scope"]!["timestampSupported"]!.GetValue<bool>());
        Assert.Single(first.Timeline);
        var responderLink = Assert.Single(first.Links);
        Assert.Contains("/d/incidentbot-kafka-orders-production", responderLink.Url, StringComparison.Ordinal);
        Assert.DoesNotContain("var-", responderLink.Url, StringComparison.Ordinal);
        Assert.Equal(
            first.Findings.OrderBy(item => item.Category).Select(item => item.Id),
            second.Findings.OrderBy(item => item.Category).Select(item => item.Id));
    }

    [Fact]
    public async Task RejectsOutOfScopeLabelsUsingPackDefinedLabelNames()
    {
        var expression =
            "max(metric{kafka_cluster=~\"{{clusterRegex}}\",kafka_topic=~\"{{topicRegex}}\"})";
        var catalog = Catalog(Metric(
            "topic-errors", expression, "topic", "maximum", "anomaly", "required", 1, 2));
        var handler = new RecordingHandler(_ => Task.FromResult(Json("""
            {"results":{"A":{"frames":[{"schema":{"fields":[
              {"name":"Time","type":"time"},
              {"name":"value","type":"number","labels":{"kafka_cluster":"prod","kafka_topic":"outside"}}
            ]},"data":{"values":[[1784023200000],[5]]}}]}}}
            """)));

        var result = await Connector(handler, catalog).CollectAsync(
            Context(new KafkaProfileScope
            {
                MetricPackId = "test-pack",
                Cluster = "prod",
                Topics = ["orders"]
            }),
            Scope(),
            CancellationToken.None);

        Assert.Equal(SourceHealth.Partial, result.Health);
        Assert.Empty(result.Findings);
        Assert.Contains("topic label is not allowlisted", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContinuesAfterBatchFailureAndHonorsItemLimit()
    {
        var metrics = Enumerable.Range(1, 10)
            .Select(index => Metric(
                $"metric-{index:00}",
                TopicExpression.Replace("metric", $"metric_{index}", StringComparison.Ordinal),
                "topic", "last", "context", "optional", 1, 2))
            .ToArray();
        var calls = 0;
        var handler = new RecordingHandler(_ =>
        {
            calls++;
            return Task.FromResult(calls == 1
                ? new HttpResponseMessage(HttpStatusCode.BadGateway)
                {
                    Content = new StringContent("upstream unavailable")
                }
                : Json("""
                    {"results":{
                      "A":{"frames":[{"schema":{"fields":[{"name":"Time","type":"time"},{"name":"value","type":"number","labels":{"cluster":"prod","topic":"orders"}}]},"data":{"values":[[1784023200000],[1]]}}]},
                      "B":{"frames":[{"schema":{"fields":[{"name":"Time","type":"time"},{"name":"value","type":"number","labels":{"cluster":"prod","topic":"orders"}}]},"data":{"values":[[1784023200000],[2]]}}]}
                    }}
                    """));
        });

        var result = await Connector(handler, Catalog(metrics), maxItems: 1).CollectAsync(
            Context(new KafkaProfileScope
            {
                MetricPackId = "test-pack",
                Cluster = "prod",
                Topics = ["orders"]
            }),
            Scope(),
            CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.Equal(SourceHealth.Partial, result.Health);
        Assert.Single(result.Findings);
        Assert.Contains("batch 1 failed", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("item limit 1", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CumulativeByteBudgetMarksOversizedBatchPartialAndContinues()
    {
        var metrics = Enumerable.Range(1, 9)
            .Select(index => Metric(
                $"metric-{index:00}",
                TopicExpression.Replace("metric", $"metric_{index}", StringComparison.Ordinal),
                "topic", "last", "context", "optional", 1, 2))
            .ToArray();
        var calls = 0;
        var handler = new RecordingHandler(_ =>
        {
            calls++;
            return Task.FromResult(calls == 1
                ? Json("{\"padding\":\"" + new string('x', 1_000) + "\"}")
                : Json(EmptyResults(1)));
        });

        var result = await Connector(handler, Catalog(metrics), maxBytes: 1_500).CollectAsync(
            Context(new KafkaProfileScope
            {
                MetricPackId = "test-pack",
                Cluster = "prod",
                Topics = ["orders"]
            }),
            new EvidenceScope(
                TriggeredAt.AddMinutes(-30),
                TriggeredAt.AddMinutes(10),
                "v1",
                100,
                1_500),
            CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.Equal(SourceHealth.Partial, result.Health);
        Assert.Contains("byte budget", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("limited 1 request", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EveryFailedBatchReturnsUnavailable()
    {
        var metrics = Enumerable.Range(1, 9)
            .Select(index => Metric(
                $"metric-{index:00}",
                TopicExpression.Replace("metric", $"metric_{index}", StringComparison.Ordinal),
                "topic", "last", "context", "optional", 1, 2))
            .ToArray();
        var calls = 0;
        var handler = new RecordingHandler(_ =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent("upstream unavailable")
            });
        });

        var result = await Connector(handler, Catalog(metrics)).CollectAsync(
            Context(new KafkaProfileScope
            {
                MetricPackId = "test-pack",
                Cluster = "prod",
                Topics = ["orders"]
            }),
            Scope(),
            CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.Equal(SourceHealth.Unavailable, result.Health);
        Assert.Empty(result.Findings);
        Assert.Contains("batch 1 failed", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("batch 2 failed", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    private static KafkaEvidenceConnector Connector(
        HttpMessageHandler handler,
        KafkaMetricCatalog catalog,
        int maxItems = 50,
        int maxBytes = 262144) => new(
        new StubHttpClientFactory(handler),
        new KafkaMetricPlanStore(catalog),
        TestConfiguration.EvidenceSources(kafka: new IncidentBot.Api.Options.ConnectorTransport
        {
            Mode = "api",
            BaseUrl = "https://grafana.test",
            CredentialEnv = "KAFKA_READ_TOKEN",
            TimeoutSeconds = 5,
            MaxItems = maxItems,
            MaxBytes = maxBytes
        }),
        TestConfiguration.Credentials(("KAFKA_READ_TOKEN", "read-token")));

    private static InvestigationContext Context(KafkaProfileScope kafka) => new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        "PD-1",
        "orders",
        "Orders delayed",
        "high",
        IncidentState.Triggered,
        TriggeredAt,
        new Dictionary<string, string>(),
        new InvestigationProfile { Id = "orders-production", Kafka = kafka });

    private static EvidenceScope Scope() => Scope(TriggeredAt.AddMinutes(-30), TriggeredAt.AddMinutes(10));

    private static EvidenceScope Scope(DateTimeOffset start, DateTimeOffset end) =>
        new(start, end, "v1", 100, 262144);

    private static KafkaMetricCatalog Catalog(params MetricSpec[] metrics)
    {
        var yaml = new StringBuilder("version: 1\npacks:\n  - id: test-pack\n    title: Test pack\n    metrics:\n");
        foreach (var metric in metrics)
        {
            yaml.AppendLine($"      - id: {metric.Id}");
            yaml.AppendLine($"        title: {metric.Id}");
            yaml.AppendLine($"        category: kafka-{metric.Id}");
            yaml.AppendLine($"        promQl: '{metric.Expression}'");
            yaml.AppendLine("        datasourceUid: prometheus");
            yaml.AppendLine($"        resourceScope: {metric.ResourceScope}");
            yaml.AppendLine("        unit: items");
            yaml.AppendLine($"        timeReducer: {metric.Reducer}");
            yaml.AppendLine($"        evidenceMode: {metric.EvidenceMode}");
            yaml.AppendLine($"        requirement: {metric.Requirement}");
            yaml.AppendLine($"        warningThreshold: {metric.Warning}");
            yaml.AppendLine($"        criticalThreshold: {metric.Critical}");
            yaml.AppendLine("        direction: above");
            yaml.AppendLine("        dashboardRow: Overview");
        }
        return KafkaMetricCatalog.Parse(yaml.ToString());
    }

    private static MetricSpec Metric(
        string id,
        string expression,
        string resourceScope,
        string reducer,
        string evidenceMode,
        string requirement,
        double warning,
        double critical) =>
        new(id, expression, resourceScope, reducer, evidenceMode, requirement, warning, critical);

    private static string EmptyResults(int count)
    {
        var results = new JsonObject();
        for (var index = 0; index < count; index++)
        {
            results[((char)('A' + index)).ToString()] = new JsonObject { ["frames"] = new JsonArray() };
        }
        return new JsonObject { ["results"] = results }.ToJsonString();
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private const string TopicExpression =
        "sum(metric{cluster=~\"{{clusterRegex}}\",topic=~\"{{topicRegex}}\"})";
    private const string ConsumerExpression =
        "max(metric{cluster=~\"{{clusterRegex}}\",topic=~\"{{topicRegex}}\",consumer_group=~\"{{consumerGroupRegex}}\"})";

    private sealed record MetricSpec(
        string Id,
        string Expression,
        string ResourceScope,
        string Reducer,
        string EvidenceMode,
        string Requirement,
        double Warning,
        double Critical);

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => response(request);
    }
}
