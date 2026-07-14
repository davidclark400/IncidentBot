using System.Text.Json.Nodes;

namespace IncidentBot.Kafka.Tests;

public sealed class KafkaDashboardTests
{
    [Fact]
    public void GenerationIsDeterministicAndUsesSixSharedRows()
    {
        var generator = new KafkaDashboardGenerator();
        var catalog = KafkaMetricCatalogTests.SharedCatalog();
        var first = generator.Generate("orders-production", KafkaMetricCatalogTests.Scope(), catalog);
        var second = generator.Generate("orders-production", KafkaMetricCatalogTests.Scope(), catalog);
        var root = JsonNode.Parse(first)!.AsObject();
        var panels = root["panels"]!.AsArray();

        Assert.Equal(first, second);
        Assert.EndsWith("\n", first, StringComparison.Ordinal);
        Assert.Equal(
            KafkaMetricCatalog.DashboardRows,
            panels.Where(panel => panel!["type"]!.GetValue<string>() == "row")
                .Select(panel => panel!["title"]!.GetValue<string>()));
        Assert.All(
            root["templating"]!["list"]!.AsArray(),
            variable => Assert.True(variable!["skipUrlSync"]!.GetValue<bool>()));
        Assert.All(
            panels.Where(panel => panel!["type"]!.GetValue<string>() == "timeseries"),
            panel =>
            {
                var expression = panel!["targets"]![0]!["expr"]!.GetValue<string>();
                Assert.Contains("${clusterRegex:regex}", expression, StringComparison.Ordinal);
                Assert.DoesNotContain("{{", expression, StringComparison.Ordinal);
            });
    }

    [Fact]
    public void CheckIsNonMutatingAndDetectsDrift()
    {
        var directory = Directory.CreateTempSubdirectory("incidentbot-kafka-dashboard-");
        var path = Path.Combine(directory.FullName, "dashboard.json");
        try
        {
            var generator = new KafkaDashboardGenerator();
            var catalog = KafkaMetricCatalogTests.SharedCatalog();
            var generated = generator.Generate("orders-production", KafkaMetricCatalogTests.Scope(), catalog);
            File.WriteAllText(path, generated);

            Assert.True(generator.Check(path, "orders-production", KafkaMetricCatalogTests.Scope(), catalog, out _));
            File.AppendAllText(path, " ");
            Assert.False(generator.Check(path, "orders-production", KafkaMetricCatalogTests.Scope(), catalog, out var diagnostic));
            Assert.Contains("stale", diagnostic, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void EmptyConsumerGroupScopeCannotQueryAnInventedGroup()
    {
        var original = KafkaMetricCatalogTests.Scope();
        var scope = new KafkaProfileScope
        {
            MetricPackId = original.MetricPackId,
            Cluster = original.Cluster,
            Topics = original.Topics,
            ConsumerGroups = []
        };
        var root = JsonNode.Parse(
            new KafkaDashboardGenerator().Generate(
                "orders-production",
                scope,
                KafkaMetricCatalogTests.SharedCatalog()))!.AsObject();
        var groupVariable = root["templating"]!["list"]!.AsArray()
            .Single(variable => variable!["name"]!.GetValue<string>() == "consumerGroupRegex");
        var groupExpressions = root["panels"]!.AsArray()
            .Where(panel => panel?["targets"] is JsonArray targets
                && targets.Count > 0
                && targets[0]?["expr"]!.GetValue<string>()
                    .Contains("consumer_group", StringComparison.Ordinal) == true)
            .Select(panel => panel!["targets"]![0]!["expr"]!.GetValue<string>())
            .ToArray();

        Assert.Empty(groupVariable!["options"]!.AsArray());
        Assert.NotEmpty(groupExpressions);
        Assert.All(groupExpressions, expression =>
        {
            Assert.Contains("consumer_group=~\"a^\"", expression, StringComparison.Ordinal);
            Assert.DoesNotContain("consumerGroupRegex", expression, StringComparison.Ordinal);
            Assert.DoesNotContain("__none__", expression, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void CustomVariableQueryEscapesCommasWithoutChangingAllowedValues()
    {
        var original = KafkaMetricCatalogTests.Scope();
        var scope = new KafkaProfileScope
        {
            MetricPackId = original.MetricPackId,
            Cluster = original.Cluster,
            Topics = original.Topics,
            ConsumerGroups = ["workers,europe"]
        };
        var root = JsonNode.Parse(
            new KafkaDashboardGenerator().Generate(
                "orders-production",
                scope,
                KafkaMetricCatalogTests.SharedCatalog()))!.AsObject();
        var variable = root["templating"]!["list"]!.AsArray()
            .Single(item => item!["name"]!.GetValue<string>() == "consumerGroupRegex");

        Assert.Equal("workers\\,europe", variable!["query"]!.GetValue<string>());
        Assert.Equal("workers,europe", variable["options"]![0]!["value"]!.GetValue<string>());
    }

    [Fact]
    public void BelowThresholdsMatchInclusiveRuntimeBoundariesForDiscreteMetrics()
    {
        var catalog = KafkaMetricCatalogTests.SharedCatalog();
        var scope = KafkaMetricCatalogTests.Scope();
        var metric = catalog.GetPack(scope.MetricPackId).Metrics
            .Single(item => item.Id == "broker-availability");
        var thresholds = catalog.EffectiveThresholds(metric);
        var root = JsonNode.Parse(
            new KafkaDashboardGenerator().Generate("orders-production", scope, catalog))!.AsObject();
        var steps = root["panels"]!.AsArray()
            .Single(panel => panel?["title"]?.GetValue<string>() == metric.Title)!["fieldConfig"]!["defaults"]!["thresholds"]!["steps"]!
            .AsArray();

        Assert.Equal("critical", thresholds.State(thresholds.Critical));
        Assert.Equal("warning", thresholds.State(thresholds.Warning));
        Assert.Equal("red", ThresholdColor(steps, thresholds.Critical));
        Assert.Equal("yellow", ThresholdColor(steps, thresholds.Warning));
        Assert.Equal("green", ThresholdColor(steps, Math.BitIncrement(thresholds.Warning)));
        Assert.Equal(Math.BitIncrement(thresholds.Critical), steps[1]!["value"]!.GetValue<double>());
        Assert.Equal(Math.BitIncrement(thresholds.Warning), steps[2]!["value"]!.GetValue<double>());
    }

    private static string ThresholdColor(JsonArray steps, double value)
    {
        var color = steps[0]!["color"]!.GetValue<string>();
        foreach (var step in steps.Skip(1))
        {
            if (value >= step!["value"]!.GetValue<double>())
            {
                color = step["color"]!.GetValue<string>();
            }
        }
        return color;
    }
}
