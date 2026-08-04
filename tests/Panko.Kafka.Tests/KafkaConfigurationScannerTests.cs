namespace Panko.Kafka.Tests;

public sealed class KafkaConfigurationScannerTests
{
    [Fact]
    public void ScanUsesOnlyRequestedSpringProfileAndKafkaScopedConfiguration()
    {
        using var app = new TemporaryApplication();
        app.Write("application.yml", """
            unrelated:
              topic: rabbit-events
              group: rabbit-workers
            ---
            spring:
              config:
                activate:
                  on-profile: production
              kafka:
                bootstrap-servers: prod-kafka:9092
                template:
                  default-topic: prod-events
            ---
            spring:
              config:
                activate:
                  on-profile: staging
              kafka:
                bootstrap-servers: staging-kafka:9092
                template:
                  default-topic: staging-events
            """);
        app.Write("deploy/overlays/production/kafka.yaml", "KAFKA_TOPIC: prod-overlay-events");
        app.Write("deploy/overlays/staging/kafka.yaml", "KAFKA_TOPIC: staging-overlay-events");

        var inventory = new KafkaApplicationScanner().Scan(app.Root, "production");
        var resources = inventory.Resources.Select(resource => (resource.Kind, resource.Name)).ToHashSet();

        Assert.Contains((KafkaInventoryResourceKinds.Cluster, "prod-kafka:9092"), resources);
        Assert.Contains((KafkaInventoryResourceKinds.Topic, "prod-events"), resources);
        Assert.Contains((KafkaInventoryResourceKinds.Topic, "prod-overlay-events"), resources);
        Assert.DoesNotContain(resources, resource => resource.Name.Contains("staging", StringComparison.Ordinal));
        Assert.DoesNotContain(resources, resource => resource.Name.StartsWith("rabbit-", StringComparison.Ordinal));
    }

    [Fact]
    public void ScanPrefersEnvironmentOverlayWithGenericFilenameOverBaseConfiguration()
    {
        using var app = new TemporaryApplication();
        app.Write("deploy/base/config.yaml", "KAFKA_TOPIC: base-events.v1");
        app.Write("deploy/overlays/production/patch.yaml", "KAFKA_TOPIC: production-events.v1");

        var inventory = new KafkaApplicationScanner().Scan(app.Root, "production");
        var topics = inventory.Resources
            .Where(resource => resource.Kind == KafkaInventoryResourceKinds.Topic)
            .Select(resource => resource.Name)
            .ToArray();

        Assert.Contains("production-events.v1", topics);
        Assert.DoesNotContain("base-events.v1", topics);
    }

    private sealed class TemporaryApplication : IDisposable
    {
        public TemporaryApplication() => Root = Directory.CreateTempSubdirectory("panko-kafka-config-").FullName;
        public string Root { get; }

        public void Write(string relativePath, string content)
        {
            var path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content.ReplaceLineEndings("\n"));
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
