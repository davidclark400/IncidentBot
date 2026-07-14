namespace IncidentBot.Kafka.Tests;

public sealed class KafkaOnboardingValidatorTests
{
    private static readonly KafkaInventoryEvidence Evidence =
        new("src/App.java", 12, "spring-kafka", "consumer", "@KafkaListener(...)");

    [Fact]
    public void MatchingInventoryProfileAndDashboardPassCoverage()
    {
        var scope = KafkaMetricCatalogTests.Scope();
        var catalog = KafkaMetricCatalogTests.SharedCatalog();
        var dashboard = new KafkaDashboardGenerator().Generate("orders-production", scope, catalog);
        var inventory = Inventory(
            new(KafkaInventoryResourceKinds.Cluster, scope.Cluster, [Evidence]),
            new(KafkaInventoryResourceKinds.Topic, "orders.v1", [Evidence]),
            new(KafkaInventoryResourceKinds.ConsumerGroup, "payments-workers", [Evidence]));

        var result = new KafkaOnboardingValidator(new KafkaDashboardGenerator()).Validate(
            inventory, "orders-production", scope, catalog, dashboard);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public void RequiredDynamicsAndMissingCoverageFail()
    {
        var scope = KafkaMetricCatalogTests.Scope();
        var catalog = KafkaMetricCatalogTests.SharedCatalog();
        var dashboard = new KafkaDashboardGenerator().Generate("orders-production", scope, catalog);
        var inventory = new KafkaApplicationInventory(
            1,
            "production",
            [
                new(KafkaInventoryResourceKinds.Cluster, scope.Cluster, [Evidence]),
                new(KafkaInventoryResourceKinds.Topic, "not-allowlisted", [Evidence])
            ],
            [new(KafkaInventoryResourceKinds.ConsumerGroup, "${GROUP_ID}", "No literal value", true, [Evidence])]);

        var result = new KafkaOnboardingValidator(new KafkaDashboardGenerator()).Validate(
            inventory, "orders-production", scope, catalog, dashboard);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("Unresolved required", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("not-allowlisted", StringComparison.Ordinal));
    }

    [Fact]
    public void DashboardPackDriftFailsCoverage()
    {
        var scope = KafkaMetricCatalogTests.Scope();
        var catalog = KafkaMetricCatalogTests.SharedCatalog();
        var dashboard = new KafkaDashboardGenerator().Generate("orders-production", scope, catalog)
            .Replace("incidentbot_fixture_kafka_topic_bytes_total", "drifted_metric", StringComparison.Ordinal);

        var result = new KafkaOnboardingValidator(new KafkaDashboardGenerator()).Validate(
            Inventory(
                new(KafkaInventoryResourceKinds.Cluster, scope.Cluster, [Evidence]),
                new(KafkaInventoryResourceKinds.Topic, "orders.v1", [Evidence])),
            "orders-production", scope, catalog, dashboard);

        Assert.Contains(result.Errors, error => error.Contains("regenerate", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void UnsupportedInventoryVersionFailsBeforeCoverage(int version)
    {
        var scope = KafkaMetricCatalogTests.Scope();
        var catalog = KafkaMetricCatalogTests.SharedCatalog();
        var dashboard = new KafkaDashboardGenerator().Generate("orders-production", scope, catalog);
        var inventory = new KafkaApplicationInventory(version, "production", [], []);

        var result = new KafkaOnboardingValidator(new KafkaDashboardGenerator()).Validate(
            inventory, "orders-production", scope, catalog, dashboard);

        var error = Assert.Single(result.Errors);
        Assert.Contains("Unsupported Kafka inventory schema version", error, StringComparison.Ordinal);
    }

    [Fact]
    public void InventoryJsonUsedByCliRejectsUnsupportedVersion()
    {
        var inventory = new KafkaApplicationInventory(2, "production", [], []);
        var json = """
            {
              "version": 2,
              "environment": "production",
              "resources": [],
              "unresolvedReferences": []
            }
            """;

        var serializeError = Assert.Throws<InvalidOperationException>(() => KafkaInventoryJson.Serialize(inventory));
        var deserializeError = Assert.Throws<InvalidOperationException>(() => KafkaInventoryJson.Deserialize(json));

        Assert.Contains("schema version 2", serializeError.Message, StringComparison.Ordinal);
        Assert.Contains("schema version 2", deserializeError.Message, StringComparison.Ordinal);
    }

    private static KafkaApplicationInventory Inventory(params KafkaInventoryResource[] resources) =>
        new(1, "production", resources, []);
}
