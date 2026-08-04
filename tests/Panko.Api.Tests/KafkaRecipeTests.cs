using Panko.Api.Crumbs;
using Panko.Api.Options;
using Panko.Api.Recipes;
using Panko.Kafka;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;

namespace Panko.Api.Tests;

public sealed class KafkaRecipeTests
{
    [Fact]
    public void KafkaRecipeLoadsReviewedPackAndResourceScope()
    {
        using var recipeFile = RecipeFile("""
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
        var store = Store(recipeFile.Path);

        var recipe = store.Resolve("P123", new Dictionary<string, string>());

        Assert.Equal("synthetic-fixture-kafka-v1", recipe.Kafka!.MetricPackId);
        Assert.Equal(["orders.v1"], recipe.Kafka.Topics);
        Assert.Contains(
            store.ConfiguredCrumbSources(),
            source => source.Source == CrumbSourceRegistry.Kafka);
    }

    [Fact]
    public void MetricPlanStoreReusesEquivalentScopesAndKeepsPlansImmutable()
    {
        var plans = new KafkaMetricPlanStore(KafkaMetricCatalog.Load(
            Path.Combine(AppContext.BaseDirectory, "config", "kafka-metric-packs.yaml")));
        var firstScope = new KafkaRecipeScope
        {
            MetricPackId = "synthetic-fixture-kafka-v1",
            Cluster = "prod",
            Topics = ["payments", "orders"],
            ConsumerGroups = ["workers"]
        };
        var equivalentScope = new KafkaRecipeScope
        {
            MetricPackId = firstScope.MetricPackId,
            Cluster = firstScope.Cluster,
            Topics = ["orders", "payments"],
            ConsumerGroups = ["workers"]
        };

        var first = plans.Resolve(firstScope);
        var equivalent = plans.Resolve(equivalentScope);
        firstScope.Topics[0] = "mutated";
        var changed = plans.Resolve(firstScope);

        Assert.Same(first, equivalent);
        Assert.NotSame(first, changed);
        Assert.Equal(new[] { "orders", "payments" }, first.Topics.ToArray());
    }

    [Theory]
    [InlineData("topics: []", "at least one")]
    [InlineData("topics: [orders]\n  thresholdOverrides:\n    unknown-metric:\n      warning: 1", "not defined")]
    [InlineData("topics: [orders]\n  thresholdOverrides:\n    consumer-lag:\n      warning: 5000\n      critical: 500", "conflict")]
    public void InvalidKafkaScopeOrOverridesFailRecipeLoading(string kafkaBody, string expected)
    {
        using var recipeFile = RecipeFile($$"""
            kafka:
              metricPackId: synthetic-fixture-kafka-v1
              cluster: prod
              {{kafkaBody}}
            """);

        var error = Assert.Throws<InvalidOperationException>(() => Store(recipeFile.Path));

        Assert.Contains(expected, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static RecipeStore Store(string recipesPath) => new(
        Microsoft.Extensions.Options.Options.Create(new PankoOptions
        {
            RecipesPath = recipesPath,
            KafkaMetricPacksPath = Path.Combine(AppContext.BaseDirectory, "config", "kafka-metric-packs.yaml")
        }),
        new TestEnvironment(),
        new CrumbSourceRegistry(Array.Empty<ICrumbSourceAdapter>(), TestConfiguration.CrumbSources()),
        new KafkaMetricPlanStore(KafkaMetricCatalog.Load(
            Path.Combine(AppContext.BaseDirectory, "config", "kafka-metric-packs.yaml"))));

    private static TemporaryRecipe RecipeFile(string kafkaYaml)
    {
        var indentedKafka = string.Join('\n', kafkaYaml.ReplaceLineEndings("\n")
            .Split('\n')
            .Select(line => "    " + line));
        var yaml = $$"""
            version: 3
            revision: kafka-test.1
            fallbackSlackChannel: "#cases"
            recipes:
              - id: kafka-app
                pagerDutyServiceId: P123
                team: platform
                slackChannel: "#cases"
            {{indentedKafka}}
            """;
        return new TemporaryRecipe(yaml);
    }

    private sealed class TemporaryRecipe : IDisposable
    {
        public TemporaryRecipe(string yaml)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"panko-kafka-recipe-{Guid.NewGuid():N}.yaml");
            File.WriteAllText(Path, yaml);
        }

        public string Path { get; }
        public void Dispose() => File.Delete(Path);
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Panko.Api.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
