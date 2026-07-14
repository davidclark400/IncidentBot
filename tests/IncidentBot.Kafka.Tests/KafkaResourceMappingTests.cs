using IncidentBot.Kafka.Onboarding;

namespace IncidentBot.Kafka.Tests;

public sealed class KafkaResourceMappingTests
{
    private static readonly KafkaInventoryEvidence Evidence =
        new("config/application.yaml", 8, "configuration", "bootstrap-servers", "kafka.internal:9092");

    [Fact]
    public void ReviewedEndpointToClusterLabelMappingPassesCoverage()
    {
        var scope = KafkaMetricCatalogTests.Scope();
        var catalog = KafkaMetricCatalogTests.SharedCatalog();
        var dashboard = new KafkaDashboardGenerator().Generate("orders-production", scope, catalog);
        var inventory = Inventory(
            new(KafkaInventoryResourceKinds.Cluster, "kafka.internal:9092", [Evidence]),
            new(KafkaInventoryResourceKinds.Topic, "orders.v1", [Evidence]));
        var mappings = KafkaResourceMappingLoader.Parse("""
            version: 1
            mappings:
              - kind: cluster
                inventoryResource: kafka.internal:9092
                profileResource: prod.eu-1
                evidenceFile: exporter/catalog.yaml
                evidenceLine: 42
            """);

        var result = new KafkaOnboardingValidator(new KafkaDashboardGenerator()).Validate(
            inventory, "orders-production", scope, catalog, dashboard, mappings);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public void ResourceWithNoIdentityOrReviewedMappingFailsCoverage()
    {
        var scope = KafkaMetricCatalogTests.Scope();
        var catalog = KafkaMetricCatalogTests.SharedCatalog();
        var dashboard = new KafkaDashboardGenerator().Generate("orders-production", scope, catalog);

        var result = new KafkaOnboardingValidator(new KafkaDashboardGenerator()).Validate(
            Inventory(
                new(KafkaInventoryResourceKinds.Cluster, "kafka.internal:9092", [Evidence]),
                new(KafkaInventoryResourceKinds.Topic, "orders.v1", [Evidence])),
            "orders-production", scope, catalog, dashboard);

        Assert.Contains(
            result.Errors,
            error => error.Contains("no reviewed resource mapping", StringComparison.Ordinal));
    }

    [Fact]
    public void InvalidKindAndDuplicateSourceMappingsAreRejected()
    {
        var invalidKind = Assert.Throws<InvalidOperationException>(() => KafkaResourceMappingLoader.Parse("""
            version: 1
            mappings:
              - kind: broker-endpoint
                inventoryResource: kafka.internal:9092
                profileResource: production-kafka
                evidenceFile: exporter/catalog.yaml
                evidenceLine: 42
            """));
        var duplicate = Assert.Throws<InvalidOperationException>(() => KafkaResourceMappingLoader.Parse("""
            version: 1
            mappings:
              - kind: cluster
                inventoryResource: kafka.internal:9092
                profileResource: production-kafka
                evidenceFile: exporter/catalog.yaml
                evidenceLine: 42
              - kind: cluster
                inventoryResource: kafka.internal:9092
                profileResource: other-label
                evidenceFile: exporter/catalog.yaml
                evidenceLine: 43
            """));

        Assert.Contains("must be one of", invalidKind.Message, StringComparison.Ordinal);
        Assert.Contains("Duplicate Kafka resource mapping", duplicate.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VersionFieldsBoundsAndEvidenceLineAreValidated()
    {
        var unsupportedVersion = Assert.Throws<InvalidOperationException>(() => KafkaResourceMappingLoader.Parse("""
            version: 2
            mappings: []
            """));
        var emptyField = Assert.Throws<InvalidOperationException>(() => KafkaResourceMappingLoader.Parse("""
            version: 1
            mappings:
              - kind: topic
                inventoryResource: ''
                profileResource: orders.v1
                evidenceFile: exporter/catalog.yaml
                evidenceLine: 42
            """));
        var overlongField = Assert.Throws<InvalidOperationException>(() => KafkaResourceMappingLoader.Parse($$"""
            version: 1
            mappings:
              - kind: topic
                inventoryResource: {{new string('x', 257)}}
                profileResource: orders.v1
                evidenceFile: exporter/catalog.yaml
                evidenceLine: 42
            """));
        var invalidLine = Assert.Throws<InvalidOperationException>(() => KafkaResourceMappingLoader.Parse("""
            version: 1
            mappings:
              - kind: topic
                inventoryResource: orders
                profileResource: orders.v1
                evidenceFile: exporter/catalog.yaml
                evidenceLine: 0
            """));

        Assert.Contains("schema version", unsupportedVersion.Message, StringComparison.Ordinal);
        Assert.Contains("inventoryResource", emptyField.Message, StringComparison.Ordinal);
        Assert.Contains("at most 256", overlongField.Message, StringComparison.Ordinal);
        Assert.Contains("positive integer", invalidLine.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OrphanMappingAndTargetOutsideProfileScopeFail()
    {
        var scope = KafkaMetricCatalogTests.Scope();
        var catalog = KafkaMetricCatalogTests.SharedCatalog();
        var dashboard = new KafkaDashboardGenerator().Generate("orders-production", scope, catalog);
        var mappings = KafkaResourceMappingLoader.Parse("""
            version: 1
            mappings:
              - kind: cluster
                inventoryResource: absent.internal:9092
                profileResource: production-kafka
                evidenceFile: exporter/catalog.yaml
                evidenceLine: 42
              - kind: topic
                inventoryResource: orders.v1
                profileResource: unreviewed-topic
                evidenceFile: exporter/catalog.yaml
                evidenceLine: 47
            """);

        var result = new KafkaOnboardingValidator(new KafkaDashboardGenerator()).Validate(
            Inventory(
                new(KafkaInventoryResourceKinds.Cluster, scope.Cluster, [Evidence]),
                new(KafkaInventoryResourceKinds.Topic, "orders.v1", [Evidence])),
            "orders-production", scope, catalog, dashboard, mappings);

        Assert.Contains(result.Errors, error => error.Contains("does not match a scan inventory resource", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("outside the profile allowlist", StringComparison.Ordinal));
    }

    private static KafkaApplicationInventory Inventory(params KafkaInventoryResource[] resources) =>
        new(1, "production", resources, []);
}
