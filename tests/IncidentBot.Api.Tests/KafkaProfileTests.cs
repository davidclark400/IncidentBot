using IncidentBot.Api.Connectors;
using IncidentBot.Api.Options;
using IncidentBot.Api.Profiles;
using IncidentBot.Kafka;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;

namespace IncidentBot.Api.Tests;

public sealed class KafkaProfileTests
{
    [Fact]
    public void KafkaProfileLoadsReviewedPackAndResourceScope()
    {
        using var profileFile = ProfileFile("""
            kafka:
              metricPackId: synthetic-fixture-kafka-v1
              cluster: prod.eu-1
              topics: [orders.v1]
              consumerGroups: [payments-workers]
              thresholdOverrides:
                consumer-lag:
                  warning: 500
                  critical: 5000
            """);
        var store = Store(profileFile.Path);

        var profile = store.Resolve("P123", new Dictionary<string, string>());

        Assert.Equal("synthetic-fixture-kafka-v1", profile.Kafka!.MetricPackId);
        Assert.Equal(["orders.v1"], profile.Kafka.Topics);
        Assert.Contains(
            store.ConfiguredEvidenceSources(),
            source => source.Source == EvidenceSourceRegistry.Kafka);
    }

    [Theory]
    [InlineData("topics: []", "at least one")]
    [InlineData("topics: [orders]\n  thresholdOverrides:\n    unknown-metric:\n      warning: 1", "not defined")]
    [InlineData("topics: [orders]\n  thresholdOverrides:\n    consumer-lag:\n      warning: 5000\n      critical: 500", "conflict")]
    public void InvalidKafkaScopeOrOverridesFailProfileLoading(string kafkaBody, string expected)
    {
        using var profileFile = ProfileFile($$"""
            kafka:
              metricPackId: synthetic-fixture-kafka-v1
              cluster: prod
              {{kafkaBody}}
            """);

        var error = Assert.Throws<InvalidOperationException>(() => Store(profileFile.Path));

        Assert.Contains(expected, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static InvestigationProfileStore Store(string profilesPath) => new(
        Microsoft.Extensions.Options.Options.Create(new IncidentBotOptions
        {
            ProfilesPath = profilesPath,
            KafkaMetricPacksPath = Path.Combine(AppContext.BaseDirectory, "config", "kafka-metric-packs.yaml")
        }),
        new TestEnvironment(),
        new EvidenceSourceRegistry(Array.Empty<IIncidentEvidenceConnector>(), TestConfiguration.EvidenceSources()),
        new KafkaMetricPackStore(KafkaMetricCatalog.Load(
            Path.Combine(AppContext.BaseDirectory, "config", "kafka-metric-packs.yaml"))));

    private static TemporaryProfile ProfileFile(string kafkaYaml)
    {
        var indentedKafka = string.Join('\n', kafkaYaml.ReplaceLineEndings("\n")
            .Split('\n')
            .Select(line => "    " + line));
        var yaml = $$"""
            version: 2
            revision: kafka-test.1
            fallbackSlackChannel: "#incidents"
            profiles:
              - id: kafka-app
                pagerDutyServiceId: P123
                team: platform
                slackChannel: "#incidents"
            {{indentedKafka}}
            """;
        return new TemporaryProfile(yaml);
    }

    private sealed class TemporaryProfile : IDisposable
    {
        public TemporaryProfile(string yaml)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"incidentbot-kafka-profile-{Guid.NewGuid():N}.yaml");
            File.WriteAllText(Path, yaml);
        }

        public string Path { get; }
        public void Dispose() => File.Delete(Path);
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "IncidentBot.Api.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
