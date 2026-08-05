using Panko.Api.Options;
using Panko.Api.Recipes;
using Panko.Api.Domain;
using Panko.Api.Crumbs;
using Panko.Observability;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using YamlDotNet.Core;

namespace Panko.Api.Tests;

public sealed class RecipeTests
{
    [Fact]
    public void ExampleRecipe_LoadsAndNarrowsAllSources()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "config", "recipes.yaml");
        var options = Microsoft.Extensions.Options.Options.Create(new PankoOptions { RecipesPath = path });
        var store = new RecipeStore(
            options,
            new TestEnvironment(),
            EmptySources(),
            serviceMetricPlans: ExampleServiceMetricPlans());

        var recipe = store.Resolve("P123PAYMENTS", new Dictionary<string, string>
        {
            ["environment"] = "production",
            ["service"] = "P123PAYMENTS"
        });

        Assert.Equal("payments-production", recipe.Id);
        Assert.Equal("payments-platform", recipe.ServiceCollection);
        Assert.NotEmpty(recipe.Nomad!.Namespaces.Single().Jobs);
        Assert.NotEmpty(recipe.Consul!.Services);
        Assert.NotEmpty(recipe.Grafana!.Dashboards);
        Assert.Equal("example-prometheus-http-v1", recipe.Observability!.MetricPackId);
        Assert.Equal(4, recipe.Grafana.Queries.Count);
        Assert.Contains(
            recipe.Grafana.Dashboards,
            dashboard => dashboard.Uid == ServiceDashboardIdentity.Uid(recipe.Id));
        Assert.All(recipe.Grafana.Queries, query =>
        {
            Assert.Contains("service=~\"(payments-api)\"", query.Expression, StringComparison.Ordinal);
            Assert.Contains("environment=~\"(production)\"", query.Expression, StringComparison.Ordinal);
            Assert.DoesNotContain("{{", query.Expression, StringComparison.Ordinal);
        });
        Assert.NotEmpty(recipe.VictoriaLogs!.StreamFilters);
        Assert.NotEmpty(recipe.VictoriaLogs.Queries.SelectMany(query => query.AnchorPatterns));
        Assert.NotEmpty(recipe.GitLab!.Projects);
        Assert.Equal("production", recipe.SlackPromptLabels["environment"]);
    }

    [Fact]
    public void OmittedServiceCollectionFallsBackToTheTeamLocalUncategorizedCollection()
    {
        var path = Path.Combine(Path.GetTempPath(), $"panko-recipe-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, """
            version: 3
            revision: test-v2
            fallbackSlackChannel: "C0000000000"
            recipes:
              - id: payments-default
                pagerDutyServiceId: P123
                team: payments
                slackChannel: "C0123456789"
              - id: payments-priority
                pagerDutyServiceId: P123
                team: payments
                slackChannel: "C0123456789"
                selectors:
                  - labels:
                      urgency: high
            """);
        try
        {
            var store = new RecipeStore(
                Microsoft.Extensions.Options.Options.Create(new PankoOptions { RecipesPath = path }),
                new TestEnvironment(),
                EmptySources());

            Assert.All(
                store.All,
                recipe => Assert.Equal(ServiceCollectionKey.Default, recipe.ServiceCollection));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("Payments Platform")]
    [InlineData("payments_platform")]
    [InlineData("-payments")]
    public void ExplicitServiceCollectionMustUseACanonicalKey(string collection)
    {
        var path = Path.Combine(Path.GetTempPath(), $"panko-recipe-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, $$"""
            version: 3
            revision: test-v2
            fallbackSlackChannel: "C0000000000"
            recipes:
              - id: payments
                pagerDutyServiceId: P123
                team: payments
                serviceCollection: {{collection}}
                slackChannel: "C0123456789"
            """);
        try
        {
            Assert.Throws<InvalidOperationException>(() => new RecipeStore(
                Microsoft.Extensions.Options.Options.Create(new PankoOptions { RecipesPath = path }),
                new TestEnvironment(),
                EmptySources()));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RecipesForOnePagerDutyServiceCannotSpanServiceCollections()
    {
        var path = Path.Combine(Path.GetTempPath(), $"panko-recipe-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, """
            version: 3
            revision: test-v2
            fallbackSlackChannel: "C0000000000"
            recipes:
              - id: payments-api
                pagerDutyServiceId: P123
                team: payments
                serviceCollection: payments-platform
                slackChannel: "C0123456789"
              - id: payments-worker
                pagerDutyServiceId: P123
                team: payments
                serviceCollection: settlement-platform
                slackChannel: "C0123456789"
                selectors:
                  - labels:
                      component: worker
            """);
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                new RecipeStore(
                    Microsoft.Extensions.Options.Options.Create(new PankoOptions { RecipesPath = path }),
                    new TestEnvironment(),
                    EmptySources()));
            Assert.Contains("different service collections", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ExampleRecipe_ListsDistinctConfiguredCrumbSources()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "config", "recipes.yaml");
        var store = new RecipeStore(
            Microsoft.Extensions.Options.Options.Create(new PankoOptions { RecipesPath = path }),
            new TestEnvironment(),
            EmptySources(),
            serviceMetricPlans: ExampleServiceMetricPlans());

        var sources = store.ConfiguredCrumbSources();

        Assert.Equal(
            ["consul", "gitlab", "grafana", "nomad", "pagerduty", "victorialogs"],
            sources.Select(source => source.Source));
        Assert.All(sources, source => Assert.Equal(["payments-production"], source.RecipeIds));
    }

    [Fact]
    public void ExampleRecipe_UsesApplicationLevelTransportConfiguration()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "config", "recipes.yaml");
        var store = new RecipeStore(
            Microsoft.Extensions.Options.Options.Create(new PankoOptions { RecipesPath = path }),
            new TestEnvironment(),
            EmptySources(),
            serviceMetricPlans: ExampleServiceMetricPlans());

        var source = Assert.Single(
            store.ConfiguredCrumbSources(),
            configured => configured.Source == CrumbSourceRegistry.Nomad);

        Assert.Equal("https://nomad.internal.example", source.Transport.BaseUrl);
        Assert.Null(typeof(NomadScope).GetProperty("Connector"));
    }

    [Fact]
    public void RecipeConnectorOverride_IsRejected()
    {
        var path = Path.Combine(Path.GetTempPath(), $"panko-recipe-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, """
            version: 3
            revision: test-v2
            fallbackSlackChannel: "#cases"
            recipes:
              - id: payments
                pagerDutyServiceId: P123
                team: payments
                slackChannel: "#payments-cases"
                nomad:
                  connector:
                    baseUrl: https://wrong.example
                  namespaces:
                    - name: production
                      jobs: [payments]
            """);
        try
        {
            Assert.Throws<YamlException>(() => new RecipeStore(
                Microsoft.Extensions.Options.Options.Create(new PankoOptions { RecipesPath = path }),
                new TestEnvironment(),
                EmptySources()));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [MemberData(nameof(DocumentsMissingRequiredMetadata))]
    public void RequiredRecipeMetadata_CannotBeOmitted(string yaml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"panko-recipe-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, yaml);
        try
        {
            Assert.Throws<InvalidOperationException>(() => new RecipeStore(
                Microsoft.Extensions.Options.Options.Create(new PankoOptions { RecipesPath = path }),
                new TestEnvironment(),
                EmptySources()));
        }
        finally
        {
            File.Delete(path);
        }
    }

    public static IEnumerable<object[]> DocumentsMissingRequiredMetadata()
    {
        yield return
        [
            """
            revision: test-v2
            fallbackSlackChannel: "#cases"
            recipes: []
            """
        ];
        yield return
        [
            """
            version: 3
            fallbackSlackChannel: "#cases"
            recipes: []
            """
        ];
        yield return
        [
            """
            version: 3
            revision: test-v2
            recipes: []
            """
        ];
    }

    [Fact]
    public void UnmappedService_UsesFallbackWithoutGlobalDiscovery()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "config", "recipes.yaml");
        var store = new RecipeStore(
            Microsoft.Extensions.Options.Options.Create(new PankoOptions { RecipesPath = path }),
            new TestEnvironment(),
            EmptySources(),
            serviceMetricPlans: ExampleServiceMetricPlans());

        var recipe = store.Resolve("UNKNOWN", new Dictionary<string, string>());

        Assert.Equal("unmapped", recipe.Id);
        Assert.Null(recipe.Nomad);
        Assert.Null(recipe.Consul);
        Assert.Null(recipe.Grafana);
        Assert.Null(recipe.VictoriaLogs);
    }

    [Theory]
    [MemberData(nameof(InvalidNamedQueryDocuments))]
    public void QueryTemplateAuthorityKeysMustBeNamedAndUnique(string yaml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"panko-recipe-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, yaml);
        try
        {
            Assert.Throws<InvalidOperationException>(() => new RecipeStore(
                Microsoft.Extensions.Options.Options.Create(new PankoOptions { RecipesPath = path }),
                new TestEnvironment(),
                EmptySources()));
        }
        finally
        {
            File.Delete(path);
        }
    }

    public static IEnumerable<object[]> InvalidNamedQueryDocuments()
    {
        yield return
        [
            """
            version: 3
            revision: test-v2
            fallbackSlackChannel: "#cases"
            recipes:
              - id: payments
                pagerDutyServiceId: P123
                grafana:
                  queries:
                    - name: Errors
                      datasourceUid: prometheus
                      expression: up
                    - name: Errors
                      datasourceUid: prometheus
                      expression: rate(errors[5m])
            """
        ];
        yield return
        [
            """
            version: 3
            revision: test-v2
            fallbackSlackChannel: "#cases"
            recipes:
              - id: payments
                pagerDutyServiceId: P123
                victoriaLogs:
                  streamFilters:
                    service: payments
                  queries:
                    - name: ""
                      expression: level:error
            """
        ];
    }

    [Fact]
    public void GrafanaMetricRecipe_LoadsCanonicalThresholdConfiguration()
    {
        var path = Path.Combine(Path.GetTempPath(), $"panko-recipe-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, """
            version: 3
            revision: test-v2
            fallbackSlackChannel: "#cases"
            recipes:
              - id: payments
                pagerDutyServiceId: P123
                team: payments
                slackChannel: "#payments-cases"
                grafana:
                  queries:
                    - name: Request failures
                      datasourceUid: prometheus
                      expression: failures
                      reducer: maximum
                      warningThreshold: 5
                      criticalThreshold: 10
                      direction: above
                      unit: requests
            """);
        try
        {
            var store = new RecipeStore(
                Microsoft.Extensions.Options.Options.Create(new PankoOptions { RecipesPath = path }),
                new TestEnvironment(),
                EmptySources());

            var query = Assert.Single(store.ResolveById("payments").Grafana!.Queries);
            Assert.Equal("maximum", query.Reducer);
            Assert.Equal(5, query.WarningThreshold);
            Assert.Equal(10, query.CriticalThreshold);
            Assert.Equal("above", query.Direction);
            Assert.Equal("requests", query.Unit);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [MemberData(nameof(InvalidVictoriaLogsAnchorPatternDocuments))]
    public void VictoriaLogsAnchorPatternsMustBeNamedUniqueAndNonBacktracking(string yaml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"panko-recipe-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, yaml);
        try
        {
            Assert.Throws<InvalidOperationException>(() => new RecipeStore(
                Microsoft.Extensions.Options.Options.Create(new PankoOptions { RecipesPath = path }),
                new TestEnvironment(),
                EmptySources()));
        }
        finally
        {
            File.Delete(path);
        }
    }

    public static IEnumerable<object[]> InvalidVictoriaLogsAnchorPatternDocuments()
    {
        yield return
        [
            """
            version: 3
            revision: test-v2
            fallbackSlackChannel: "#cases"
            recipes:
              - id: payments
                pagerDutyServiceId: P123
                victoriaLogs:
                  streamFilters:
                    service: payments
                  queries:
                    - name: Errors
                      expression: level:error
                      anchorPatterns:
                        - name: ""
                          pattern: timeout
            """
        ];
        yield return
        [
            """
            version: 3
            revision: test-v2
            fallbackSlackChannel: "#cases"
            recipes:
              - id: payments
                pagerDutyServiceId: P123
                victoriaLogs:
                  streamFilters:
                    service: payments
                  queries:
                    - name: Errors
                      expression: level:error
                      anchorPatterns:
                        - name: Timeout
                          pattern: timeout
                        - name: Timeout
                          pattern: deadline exceeded
            """
        ];
        yield return
        [
            """
            version: 3
            revision: test-v2
            fallbackSlackChannel: "#cases"
            recipes:
              - id: payments
                pagerDutyServiceId: P123
                victoriaLogs:
                  streamFilters:
                    service: payments
                  queries:
                    - name: Errors
                      expression: level:error
                      anchorPatterns:
                        - name: Backreference
                          pattern: '(error)\1'
            """
        ];
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

    private static CrumbSourceRegistry EmptySources() => new(
        Array.Empty<ICrumbSourceAdapter>(),
        new CrumbSourceConfiguration(Microsoft.Extensions.Options.Options.Create(CrumbSources())));

    private static ServiceMetricPlanStore ExampleServiceMetricPlans() => new(
        ServiceMetricCatalog.Load(
            Path.Combine(AppContext.BaseDirectory, "config", "service-metric-packs.yaml")));

    private static CrumbSourceOptions CrumbSources() => new()
    {
        PagerDuty = Transport("https://api.pagerduty.com", "PAGERDUTY_API_TOKEN"),
        Nomad = Transport("https://nomad.internal.example", "NOMAD_TOKEN"),
        Consul = Transport("https://consul.internal.example", "CONSUL_HTTP_TOKEN"),
        GitLab = Transport("https://gitlab.internal.example", "GITLAB_READ_TOKEN"),
        Grafana = Transport("https://grafana.internal.example", "GRAFANA_SERVICE_TOKEN"),
        Kafka = Transport("https://grafana.internal.example", "GRAFANA_KAFKA_READ_TOKEN"),
        VictoriaLogs = Transport("https://victorialogs.internal.example", "VICTORIALOGS_TOKEN")
    };

    private static ConnectorTransport Transport(string baseUrl, string credentialEnv) => new()
    {
        Mode = "api",
        BaseUrl = baseUrl,
        CredentialEnv = credentialEnv
    };
}
