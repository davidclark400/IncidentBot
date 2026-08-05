using Panko.Api.Crumbs;
using Panko.Api.Options;
using Panko.Api.Recipes;
using Panko.Observability;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;

namespace Panko.Api.Tests;

public sealed class ServiceMetricRecipeTests
{
    [Fact]
    public void PackBackedRecipeMaterializesTheReviewedGrafanaInterface()
    {
        using var recipeFile = new TemporaryRecipe("""
            version: 3
            revision: test-v1
            fallbackSlackChannel: "#cases"
            recipes:
              - id: payments-production
                pagerDutyServiceId: P123
                team: payments
                slackChannel: "#payments"
                observability:
                  metricPackId: example-prometheus-http-v1
                  service: payments-api
                  environment: production
                  thresholdOverrides:
                    latency-p99:
                      warning: 1
                      critical: 2
                grafana:
                  organizationId: 7
                  annotationTags: [payments]
            """);
        var store = CreateStore(recipeFile.Path, Plans());

        var recipe = store.ResolveById("payments-production");

        Assert.Equal(4, recipe.Grafana!.Queries.Count);
        var latency = Assert.Single(recipe.Grafana.Queries, query => query.Name == "p99 latency");
        Assert.Equal(1, latency.WarningThreshold);
        Assert.Equal(2, latency.CriticalThreshold);
        Assert.Equal("latency-p99", latency.MetricId);
        Assert.Equal("latency", latency.Role);
        Assert.Equal("anomaly", latency.CrumbMode);
        Assert.Equal("required", latency.Requirement);
        Assert.Contains("service=~\"(payments-api)\"", latency.Expression, StringComparison.Ordinal);
        Assert.Contains("environment=~\"(production)\"", latency.Expression, StringComparison.Ordinal);
        Assert.Contains(
            recipe.Grafana.Dashboards,
            dashboard => dashboard.Uid == ServiceDashboardIdentity.Uid(recipe.Id));
        Assert.Equal(["grafana"], store.ConfiguredCrumbSources().Select(source => source.Source));
        var requestRate = Assert.Single(recipe.Grafana.Queries, query => query.MetricId == "request-rate");
        Assert.Equal("context", requestRate.CrumbMode);
    }

    [Fact]
    public void PackBackedRecipeRejectsASecondInlineMetricAuthority()
    {
        using var recipeFile = new TemporaryRecipe("""
            version: 3
            revision: test-v1
            fallbackSlackChannel: "#cases"
            recipes:
              - id: payments-production
                pagerDutyServiceId: P123
                team: payments
                slackChannel: "#payments"
                observability:
                  metricPackId: example-prometheus-http-v1
                  service: payments-api
                  environment: production
                grafana:
                  queries:
                    - name: Inline latency
                      datasourceUid: prometheus-production
                      expression: up
            """);

        var error = Assert.Throws<InvalidOperationException>(() =>
            CreateStore(recipeFile.Path, Plans()));

        Assert.Contains("cannot combine", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PackBackedRecipeRequiresTheCatalogAtStartup()
    {
        using var recipeFile = new TemporaryRecipe("""
            version: 3
            revision: test-v1
            fallbackSlackChannel: "#cases"
            recipes:
              - id: payments-production
                pagerDutyServiceId: P123
                team: payments
                slackChannel: "#payments"
                observability:
                  metricPackId: example-prometheus-http-v1
                  service: payments-api
                  environment: production
            """);

        var error = Assert.Throws<InvalidOperationException>(() =>
            CreateStore(recipeFile.Path, serviceMetricPlans: null));

        Assert.Contains("no service metric catalog", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PackBackedRecipeRejectsNullThresholdOverrideMappingExplicitly()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Plans().Resolve(new ServiceMetricScope
        {
            MetricPackId = "example-prometheus-http-v1",
            Service = "payments-api",
            Environment = "production",
            ThresholdOverrides = null!
        }));

        Assert.Contains("must be a mapping", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RecipeYamlRejectsDuplicateObservabilityScopeKeys()
    {
        using var recipeFile = new TemporaryRecipe("""
            version: 3
            revision: test-v1
            fallbackSlackChannel: "#cases"
            recipes:
              - id: payments-production
                pagerDutyServiceId: P123
                team: payments
                slackChannel: "#payments"
                observability:
                  metricPackId: example-prometheus-http-v1
                  service: payments-api
                  service: attacker-selected
                  environment: production
            """);

        Assert.Throws<YamlDotNet.Core.YamlException>(() =>
            CreateStore(recipeFile.Path, Plans()));
    }

    private static RecipeStore CreateStore(
        string recipesPath,
        ServiceMetricPlanStore? serviceMetricPlans) => new(
        Microsoft.Extensions.Options.Options.Create(new PankoOptions
        {
            RecipesPath = recipesPath
        }),
        new TestEnvironment(),
        new CrumbSourceRegistry(
            Array.Empty<ICrumbSourceAdapter>(),
            TestConfiguration.CrumbSources()),
        serviceMetricPlans: serviceMetricPlans);

    private static ServiceMetricPlanStore Plans() => new(
        ServiceMetricCatalog.Load(
            Path.Combine(AppContext.BaseDirectory, "config", "service-metric-packs.yaml")));

    private sealed class TemporaryRecipe : IDisposable
    {
        public TemporaryRecipe(string yaml)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"panko-service-recipe-{Guid.NewGuid():N}.yaml");
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
