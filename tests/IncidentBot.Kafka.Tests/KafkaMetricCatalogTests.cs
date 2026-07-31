namespace IncidentBot.Kafka.Tests;

public sealed class KafkaMetricCatalogTests
{
    [Fact]
    public void SharedSyntheticPackIsVersionedAndComplete()
    {
        var catalog = SharedCatalog();
        var plan = catalog.CompilePlan(Scope());

        Assert.Equal("synthetic-fixture-kafka-v1", plan.MetricPackId);
        Assert.True(plan.Metrics.Length >= 20);
        Assert.Equal(
            plan.Metrics.OrderBy(metric => metric.Id, StringComparer.Ordinal),
            plan.Metrics);
        Assert.All(plan.Metrics, metric =>
        {
            Assert.StartsWith("kafka-", metric.Category, StringComparison.Ordinal);
            Assert.Contains("incidentbot_fixture_kafka_", metric.RuntimePromQl, StringComparison.Ordinal);
            Assert.DoesNotContain("{{", metric.RuntimePromQl, StringComparison.Ordinal);
            Assert.DoesNotContain("{{", metric.DashboardPromQl, StringComparison.Ordinal);
            Assert.True(double.IsFinite(metric.Thresholds.Warning));
            Assert.True(double.IsFinite(metric.Thresholds.Critical));
            Assert.NotEmpty(metric.ExpectedScopeLabels.Cluster);
        });
        Assert.Contains(plan.Metrics, metric => metric.Category == "kafka-consumer-lag-growth");
        Assert.Contains(plan.Metrics, metric => metric.Category == "kafka-producer-buffer-pressure");
        Assert.Contains(plan.Metrics, metric => metric.Category == "kafka-under-replicated-partitions");
        Assert.Contains(plan.Metrics, metric => metric.Category == "kafka-jvm-gc");
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

    [Theory]
    [InlineData(
        "cluster",
        "sum(unscoped_metric) or sum(marker{cluster=~\"{{clusterRegex}}\"})")]
    [InlineData(
        "topic",
        "sum(topic_metric{cluster=~\"{{clusterRegex}}\",topic=~\"{{topicRegex}}\"}) or sum(cluster_metric{cluster=~\"{{clusterRegex}}\"})")]
    [InlineData(
        "consumer-group",
        "sum(group_metric{cluster=~\"{{clusterRegex}}\",topic=~\"{{topicRegex}}\",consumer_group=~\"{{consumerGroupRegex}}\"}) or sum(topic_metric{cluster=~\"{{clusterRegex}}\",topic=~\"{{topicRegex}}\"})")]
    public void EveryVectorSelectorMustCarryTheMetricResourceScope(
        string resourceScope,
        string promQl)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            KafkaMetricCatalog.Parse(PackYaml(resourceScope, promQl)));

        Assert.Contains("Every Kafka PromQL vector selector", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TypeInvalidPromQlIsRejected()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            KafkaMetricCatalog.Parse(PackYaml(
                "cluster",
                "rate(metric{cluster=~\"{{clusterRegex}}\"})")));

        Assert.Contains("range vector", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RootRangeVectorPromQlIsRejected()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            KafkaMetricCatalog.Parse(PackYaml(
                "cluster",
                "metric{cluster=~\"{{clusterRegex}}\"}[5m]")));

        Assert.Contains("scalar or instant vector", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Matrix", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MultipleFullyScopedSelectorsAreSupported()
    {
        var catalog = KafkaMetricCatalog.Parse(PackYaml(
            "topic",
            "sum(rate(metric_a{cluster=~\"{{clusterRegex}}\",topic=~\"{{topicRegex}}\"}[5m])) / " +
            "sum by (topic) (rate(metric_b{cluster=~\"{{clusterRegex}}\",topic=~\"{{topicRegex}}\"}[5m]))"));

        var plan = catalog.CompilePlan(new KafkaProfileScope
        {
            MetricPackId = "pack-v1",
            Cluster = "prod.eu-1",
            Topics = ["orders.v1", "payments+retry"],
            ConsumerGroups = ["payments-workers"]
        });

        Assert.Single(plan.Metrics);
    }

    [Theory]
    [InlineData(
        "metric{cluster=~\"{{clusterRegex}}\"} # marker{cluster=~\"{{clusterRegex}}\"}")]
    [InlineData(
        "label_replace(metric{cluster=~\"{{clusterRegex}}\"}, \"dst\", 'marker{cluster=~\"{{clusterRegex}}\"}', \"src\", \"(.*)\")")]
    public void PlaceholderTextOutsideAParsedSelectorMatcherCannotProveScope(string promQl)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            KafkaMetricCatalog.Parse(PackYaml("cluster", promQl)));

        Assert.Contains("must belong to a parsed vector-selector", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProfileRequiresKnownOverridesAndAnAllowlistedTopic()
    {
        var catalog = SharedCatalog();
        var missingTopic = Scope().WithTopics([]);
        var error = Assert.Throws<InvalidOperationException>(() => catalog.CompilePlan(missingTopic));
        Assert.Contains("at least one", error.Message, StringComparison.OrdinalIgnoreCase);

        var unknownOverride = Scope(overrides: new Dictionary<string, KafkaMetricThresholdOverride>
        {
            ["not-in-pack"] = new() { Warning = 1 }
        });
        error = Assert.Throws<InvalidOperationException>(() => catalog.CompilePlan(unknownOverride));
        Assert.Contains("not defined", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompiledPlanFreezesScopeRenderingsThresholdsAndExpectedLabels()
    {
        var scope = Scope(new Dictionary<string, KafkaMetricThresholdOverride>
        {
            ["consumer-lag"] = new() { Warning = 500, Critical = 5_000 }
        });

        var plan = SharedCatalog().CompilePlan(scope);
        var metric = plan.Metrics.Single(item => item.Id == "consumer-lag");
        scope.Topics[0] = "mutated-after-compilation";
        scope.ThresholdOverrides["consumer-lag"] = new() { Warning = 1, Critical = 2 };

        Assert.Equal(new[] { "orders.v1", "payments+retry" }, plan.Topics.ToArray());
        Assert.Equal(500, metric.Thresholds.Warning);
        Assert.Equal(5_000, metric.Thresholds.Critical);
        Assert.Contains("orders\\\\.v1|payments\\\\+retry", metric.RuntimePromQl, StringComparison.Ordinal);
        Assert.Contains("${topicRegex:regex}", metric.DashboardPromQl, StringComparison.Ordinal);
        Assert.Contains("cluster", metric.ExpectedScopeLabels.Cluster);
        Assert.Contains("topic", metric.ExpectedScopeLabels.Topic);
        Assert.Contains("consumer_group", metric.ExpectedScopeLabels.ConsumerGroup);
        Assert.Empty(typeof(KafkaMetricPlan).GetConstructors());
        Assert.Empty(typeof(KafkaPlannedMetric).GetConstructors());
        Assert.Empty(typeof(KafkaExpectedScopeLabels).GetConstructors());
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

        var error = Assert.Throws<InvalidOperationException>(() => catalog.CompilePlan(scope));

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

    private static string PackYaml(string resourceScope, string promQl) => $$"""
        version: 1
        packs:
          - id: pack-v1
            title: Pack
            metrics:
              - id: metric-one
                title: Metric one
                category: kafka-metric-one
                promQl: >-
                  {{promQl}}
                datasourceUid: prometheus
                resourceScope: {{resourceScope}}
                unit: count
                timeReducer: maximum
                evidenceMode: anomaly
                requirement: required
                warningThreshold: 1
                criticalThreshold: 2
                direction: above
                dashboardRow: Overview
        """;
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
