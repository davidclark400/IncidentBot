namespace Panko.Observability.Tests;

public sealed class ServiceRecipeAndExplainTests
{
    [Fact]
    public void RecipeScopeLoaderReadsOnlyTheSelectedReviewedObservabilityScope()
    {
        using var recipe = new TemporaryYaml("""
            version: 3
            revision: test.1
            fallbackSlackChannel: "#incidents"
            recipes:
              - id: another-recipe
                observability:
                  metricPackId: ignored-pack
                  service: ignored
                  environment: ignored
              - id: payments-production
                pagerDutyServiceId: P123
                observability:
                  metricPackId: request-pack-v1
                  service: payments.api+edge
                  environment: prod.eu
                  thresholdOverrides:
                    latency-p99:
                      warning: 0.75
                      critical: 1.5
            """);

        var scope = ServiceRecipeScopeLoader.Load(recipe.Path, "payments-production");
        var plan = ServiceMetricCatalog.Parse(ServiceMetricTestData.PackYaml).CompilePlan(scope);

        Assert.Equal("request-pack-v1", scope.MetricPackId);
        Assert.Equal("payments.api+edge", scope.Service);
        Assert.Equal("prod.eu", scope.Environment);
        var thresholds = plan.Metrics.Single(metric => metric.Id == "latency-p99").Thresholds!;
        Assert.Equal(0.75, thresholds.Warning);
        Assert.Equal(1.5, thresholds.Critical);
    }

    [Theory]
    [MemberData(nameof(InvalidRecipeDocuments))]
    public void InvalidRecipeScopeDocumentsFailClosed(string _, string yaml, string expected)
    {
        using var recipe = new TemporaryYaml(yaml);

        var error = Assert.Throws<InvalidOperationException>(() =>
            ServiceRecipeScopeLoader.Load(recipe.Path, "payments-production"));

        Assert.Contains(expected, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    public static IEnumerable<object[]> InvalidRecipeDocuments()
    {
        yield return RecipeCase("missing recipes", "version: 3", "recipes sequence");
        yield return RecipeCase(
            "recipe not found",
            """
            recipes:
              - id: another-recipe
            """,
            "was not found");
        yield return RecipeCase(
            "duplicate Recipe",
            """
            recipes:
              - id: payments-production
                observability: {}
              - id: payments-production
                observability: {}
            """,
            "duplicated");
        yield return RecipeCase(
            "observability absent",
            """
            recipes:
              - id: payments-production
            """,
            "does not enable observability");
        yield return RecipeCase(
            "unknown scope key",
            BaseRecipe("datasourceUid: attacker-selected"),
            "unsupported key");
        yield return RecipeCase(
            "overrides not mapping",
            BaseRecipe("thresholdOverrides: [latency-p99]"),
            "must be a mapping");
        yield return RecipeCase(
            "unknown override key",
            BaseRecipe("""
                thresholdOverrides:
                  latency-p99:
                    warning: 1
                    expression: attacker-selected
                """),
            "unsupported key");
        yield return RecipeCase(
            "non-numeric threshold",
            BaseRecipe("""
                thresholdOverrides:
                  latency-p99:
                    warning: fast
                """),
            "must be a number");
        yield return RecipeCase(
            "non-scalar threshold",
            BaseRecipe("""
                thresholdOverrides:
                  latency-p99:
                    warning: [1]
                    critical: 2
                """),
            "must be a number");
        yield return RecipeCase(
            "inline Grafana authority",
            """
            recipes:
              - id: payments-production
                observability:
                  metricPackId: request-pack-v1
                  service: payments
                  environment: production
                grafana:
                  queries:
                    - name: Inline latency
                      datasourceUid: prometheus
                      expression: up
            """,
            "cannot combine");
    }

    [Fact]
    public void MissingRecipeFileIsReportedWithoutFallbackDiscovery()
    {
        var missing = Path.Combine(
            Path.GetTempPath(),
            $"panko-missing-recipe-{Guid.NewGuid():N}.yaml");

        var error = Assert.Throws<InvalidOperationException>(() =>
            ServiceRecipeScopeLoader.Load(missing, "payments-production"));

        Assert.Contains("not found", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExplainOutputIsDeterministicCompleteAndContainsOnlyRenderedQueries()
    {
        var plan = ServiceMetricTestData.Plan();

        var first = ServiceMetricPlanExplainFormatter.Format("payments-production", plan);
        var second = ServiceMetricPlanExplainFormatter.Format("payments-production", plan);

        Assert.Equal(first, second);
        Assert.EndsWith("\n", first, StringComparison.Ordinal);
        Assert.Contains("Recipe: payments-production\n", first, StringComparison.Ordinal);
        Assert.Contains("Service: payments.api+edge\n", first, StringComparison.Ordinal);
        Assert.Contains("Environment: prod.eu\n", first, StringComparison.Ordinal);
        Assert.Contains("Metric pack: request-pack-v1 (Request service fixture)\n", first, StringComparison.Ordinal);
        Assert.Contains("Contract: request-driven-v1\n", first, StringComparison.Ordinal);
        Assert.Contains("Metrics: 4\n", first, StringComparison.Ordinal);
        Assert.Contains("[traffic-rate] Request rate", first, StringComparison.Ordinal);
        Assert.Contains("  thresholds: none\n", first, StringComparison.Ordinal);
        Assert.Contains(
            "thresholds: warning=1, critical=2, direction=above",
            first,
            StringComparison.Ordinal);
        Assert.Contains("service=~\"(payments\\\\.api\\\\+edge)\"", first, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", first, StringComparison.Ordinal);
    }

    private static object[] RecipeCase(string name, string yaml, string expected) =>
        [name, yaml, expected];

    private static string BaseRecipe(string additionalObservabilityYaml)
    {
        var indented = string.Join(
            '\n',
            additionalObservabilityYaml.ReplaceLineEndings("\n")
                .Split('\n')
                .Select(line => "      " + line));
        return $$"""
            recipes:
              - id: payments-production
                observability:
                  metricPackId: request-pack-v1
                  service: payments
                  environment: production
            {{indented}}
            """;
    }

    private sealed class TemporaryYaml : IDisposable
    {
        public TemporaryYaml(string yaml)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"panko-service-recipe-{Guid.NewGuid():N}.yaml");
            File.WriteAllText(Path, yaml.ReplaceLineEndings("\n"));
        }

        public string Path { get; }

        public void Dispose() => File.Delete(Path);
    }
}
