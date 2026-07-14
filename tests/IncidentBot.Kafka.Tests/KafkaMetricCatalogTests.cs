namespace IncidentBot.Kafka.Tests;

public sealed class KafkaMetricCatalogTests
{
    [Fact]
    public void SharedSyntheticPackIsVersionedAndComplete()
    {
        var catalog = SharedCatalog();
        var pack = catalog.GetPack("synthetic-fixture-kafka-v1");

        Assert.True(pack.Metrics.Count >= 20);
        Assert.All(pack.Metrics, metric =>
        {
            Assert.StartsWith("kafka-", metric.Category, StringComparison.Ordinal);
            Assert.Contains("incidentbot_fixture_kafka_", metric.PromQl, StringComparison.Ordinal);
            Assert.DoesNotContain("{{environment}}", metric.PromQl, StringComparison.Ordinal);
            Assert.NotNull(metric.WarningThreshold);
            Assert.NotNull(metric.CriticalThreshold);
        });
        Assert.Contains(pack.Metrics, metric => metric.Category == "kafka-consumer-lag-growth");
        Assert.Contains(pack.Metrics, metric => metric.Category == "kafka-producer-buffer-pressure");
        Assert.Contains(pack.Metrics, metric => metric.Category == "kafka-under-replicated-partitions");
        Assert.Contains(pack.Metrics, metric => metric.Category == "kafka-jvm-gc");
    }

    [Theory]
    [InlineData("{{rawPromQl}}", "placeholder")]
    [InlineData("{{clusterRegex}}", "warning and critical")]
    public void InvalidMetricPacksAreRejected(string promQl, string expected)
    {
        var yaml = $$"""
            version: 1
            packs:
              - id: pack-v1
                title: Pack
                metrics:
                  - id: metric-one
                    title: Metric one
                    category: kafka-metric-one
                    promQl: 'sum(metric{cluster=~"{{promQl}}"})'
                    datasourceUid: prometheus
                    resourceScope: cluster
                    unit: count
                    timeReducer: maximum
                    evidenceMode: anomaly
                    requirement: required
                    direction: above
                    dashboardRow: Overview
            """;

        var error = Assert.Throws<InvalidOperationException>(() => KafkaMetricCatalog.Parse(yaml));

        Assert.Contains(expected, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProfileRequiresKnownOverridesAndAnAllowlistedTopic()
    {
        var catalog = SharedCatalog();
        var missingTopic = Scope().WithTopics([]);
        var error = Assert.Throws<InvalidOperationException>(() => catalog.ValidateProfile(missingTopic));
        Assert.Contains("at least one", error.Message, StringComparison.OrdinalIgnoreCase);

        var unknownOverride = Scope(overrides: new Dictionary<string, KafkaMetricThresholdOverride>
        {
            ["not-in-pack"] = new() { Warning = 1 }
        });
        error = Assert.Throws<InvalidOperationException>(() => catalog.ValidateProfile(unknownOverride));
        Assert.Contains("not defined", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RequiredConsumerGroupMetricRequiresAConsumerGroupAllowlist()
    {
        var catalog = KafkaMetricCatalog.Parse("""
            version: 1
            packs:
              - id: pack-v1
                title: Required consumer metrics
                metrics:
                  - id: consumer-lag
                    title: Consumer lag
                    category: kafka-consumer-lag
                    promQl: 'max(metric{cluster=~"{{clusterRegex}}",topic=~"{{topicRegex}}",consumer_group=~"{{consumerGroupRegex}}"})'
                    datasourceUid: prometheus
                    resourceScope: consumer-group
                    unit: messages
                    timeReducer: maximum
                    evidenceMode: anomaly
                    requirement: required
                    warningThreshold: 10
                    criticalThreshold: 100
                    direction: above
                    dashboardRow: Consumers
            """);
        var scope = new KafkaProfileScope
        {
            MetricPackId = "pack-v1",
            Cluster = "production",
            Topics = ["orders"],
            ConsumerGroups = []
        };

        var error = Assert.Throws<InvalidOperationException>(() => catalog.ValidateProfile(scope));

        Assert.Contains("requires consumer-group evidence", error.Message, StringComparison.Ordinal);
        Assert.Contains("no consumer groups", error.Message, StringComparison.Ordinal);
    }

    internal static KafkaMetricCatalog SharedCatalog() => KafkaMetricCatalog.Load(
        Path.Combine(AppContext.BaseDirectory, "config", "kafka-metric-packs.yaml"));

    internal static KafkaProfileScope Scope(
        Dictionary<string, KafkaMetricThresholdOverride>? overrides = null) => new()
        {
            MetricPackId = "synthetic-fixture-kafka-v1",
            Cluster = "prod.eu-1",
            Topics = ["orders.v1", "payments+retry"],
            ConsumerGroups = ["payments-workers"],
            ThresholdOverrides = overrides ?? []
        };
}

file static class KafkaScopeTestExtensions
{
    public static KafkaProfileScope WithTopics(this KafkaProfileScope scope, List<string> topics) => new()
    {
        MetricPackId = scope.MetricPackId,
        Cluster = scope.Cluster,
        Topics = topics,
        ConsumerGroups = scope.ConsumerGroups,
        ThresholdOverrides = scope.ThresholdOverrides
    };
}
