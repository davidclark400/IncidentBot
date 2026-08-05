using Panko.Kafka.Onboarding;

namespace Panko.Kafka.Tests;

public sealed class KafkaRecipeScopeLoaderTests
{
    [Fact]
    public void CanonicalRecipesAreReadable()
    {
        using var yaml = new TemporaryYaml("""
            version: 3
            recipes:
              - id: orders-production
                kafka:
                  metricPackId: kafka-pack-v1
                  cluster: prod.eu-1
                  topics: [orders.v1]
                  consumerGroups: [orders-workers]
            """);

        var scope = KafkaRecipeScopeLoader.Load(yaml.Path, "orders-production");

        Assert.Equal("kafka-pack-v1", scope.MetricPackId);
        Assert.Equal("prod.eu-1", scope.Cluster);
        Assert.Equal(["orders.v1"], scope.Topics);
        Assert.Equal(["orders-workers"], scope.ConsumerGroups);
    }

    private sealed class TemporaryYaml : IDisposable
    {
        public TemporaryYaml(string yaml)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"panko-kafka-recipe-{Guid.NewGuid():N}.yaml");
            File.WriteAllText(Path, yaml.ReplaceLineEndings("\n"));
        }

        public string Path { get; }

        public void Dispose() => File.Delete(Path);
    }
}
