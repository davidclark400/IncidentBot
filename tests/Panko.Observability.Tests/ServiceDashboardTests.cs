using System.Text.Json.Nodes;

namespace Panko.Observability.Tests;

public sealed class ServiceDashboardTests
{
    [Fact]
    public void GenerationIsDeterministicAndContainsOnlyCompiledFixedScopeQueries()
    {
        var plan = ServiceMetricTestData.Plan();
        var generator = new ServiceDashboardGenerator();

        var first = generator.Generate("payments-production", plan);
        var second = generator.Generate("payments-production", plan);
        var root = JsonNode.Parse(first)!.AsObject();
        var panels = root["panels"]!.AsArray();
        var metricPanels = panels
            .Where(panel => panel?["type"]?.GetValue<string>() == "timeseries")
            .ToArray();

        Assert.Equal(first, second);
        Assert.EndsWith("\n", first, StringComparison.Ordinal);
        Assert.Contains(
            "Panko Crumb dashboard",
            root["description"]!.GetValue<string>(),
            StringComparison.Ordinal);
        Assert.False(root["editable"]!.GetValue<bool>());
        Assert.Equal("panko-service-payments-production", root["uid"]!.GetValue<string>());
        Assert.Empty(root["templating"]!["list"]!.AsArray());
        Assert.Equal(plan.Metrics.Length, metricPanels.Length);
        Assert.Equal(
            ["Overview", "Availability", "Traffic"],
            panels.Where(panel => panel?["type"]?.GetValue<string>() == "row")
                .Select(panel => panel!["title"]!.GetValue<string>()));

        foreach (var metric in plan.Metrics)
        {
            var panel = Assert.Single(
                metricPanels,
                candidate => candidate!["title"]!.GetValue<string>() == metric.Title)!;
            Assert.Equal(metric.PromQl, panel["targets"]![0]!["expr"]!.GetValue<string>());
            Assert.Contains(
                "Crumb mode",
                panel["description"]!.GetValue<string>(),
                StringComparison.Ordinal);
            Assert.Equal(metric.DatasourceUid, panel["datasource"]!["uid"]!.GetValue<string>());
            Assert.Equal(metric.DatasourceUid, panel["targets"]![0]!["datasource"]!["uid"]!.GetValue<string>());
            var expectedCalculation = metric.TimeReducer switch
            {
                "minimum" => "min",
                "last" => "lastNotNull",
                _ => "max"
            };
            Assert.Equal(
                [expectedCalculation],
                panel["options"]!["legend"]!["calcs"]!.AsArray()
                    .Select(item => item!.GetValue<string>()));
            Assert.DoesNotContain("{{", panel.ToJsonString(), StringComparison.Ordinal);
        }

        var contextPanel = Assert.Single(
            metricPanels,
            panel => panel!["title"]!.GetValue<string>() == "Request rate")!;
        var contextSteps = contextPanel["fieldConfig"]!["defaults"]!["thresholds"]!["steps"]!.AsArray();
        Assert.Single(contextSteps);
        Assert.Equal("green", contextSteps[0]!["color"]!.GetValue<string>());
        Assert.Null(contextSteps[0]!["value"]);
    }

    [Fact]
    public void BelowThresholdDashboardColorsMatchInclusiveRuntimeBoundaries()
    {
        var plan = ServiceMetricTestData.Plan();
        var availability = plan.Metrics.Single(metric => metric.Id == "availability");
        var thresholds = availability.Thresholds!;
        var root = JsonNode.Parse(
            new ServiceDashboardGenerator().Generate("payments-production", plan))!.AsObject();
        var steps = root["panels"]!.AsArray()
            .Single(panel => panel?["title"]?.GetValue<string>() == availability.Title)!["fieldConfig"]!["defaults"]!["thresholds"]!["steps"]!
            .AsArray();

        Assert.Equal("critical", thresholds.State(thresholds.Critical));
        Assert.Equal("warning", thresholds.State(thresholds.Warning));
        Assert.Equal("red", ThresholdColor(steps, thresholds.Critical));
        Assert.Equal("yellow", ThresholdColor(steps, thresholds.Warning));
        Assert.Equal("green", ThresholdColor(steps, Math.BitIncrement(thresholds.Warning)));
    }

    [Fact]
    public void CheckIsNonMutatingAndReportsMissingCurrentAndStaleArtifacts()
    {
        var directory = Directory.CreateTempSubdirectory("panko-service-dashboard-");
        var path = Path.Combine(directory.FullName, "dashboard.json");
        try
        {
            var generator = new ServiceDashboardGenerator();
            var plan = ServiceMetricTestData.Plan();

            Assert.False(generator.Check(path, "payments-production", plan, out var missing));
            Assert.Contains("missing", missing, StringComparison.OrdinalIgnoreCase);

            File.WriteAllText(path, generator.Generate("payments-production", plan));
            Assert.True(generator.Check(path, "payments-production", plan, out var current));
            Assert.Contains("current", current, StringComparison.OrdinalIgnoreCase);

            File.AppendAllText(path, " ");
            var staleContents = File.ReadAllText(path);
            Assert.False(generator.Check(path, "payments-production", plan, out var stale));
            Assert.Contains("stale", stale, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(staleContents, File.ReadAllText(path));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void DashboardUidIsStableBoundedAndCollisionResistantAfterNormalization()
    {
        var simple = ServiceDashboardIdentity.Uid("payments-production");
        var normalized = ServiceDashboardIdentity.Uid("Payments / Production / Europe West / Very Long Recipe Name");

        Assert.Equal("panko-service-payments-production", simple);
        Assert.Equal(normalized, ServiceDashboardIdentity.Uid("Payments / Production / Europe West / Very Long Recipe Name"));
        Assert.StartsWith("panko-service-", normalized, StringComparison.Ordinal);
        Assert.True(normalized.Length <= 40);
        Assert.NotEqual(normalized, ServiceDashboardIdentity.Uid("payments-production-europe-west-very-long-recipe-name"));
    }

    [Fact]
    public void OnboardingValidationAcceptsTheGeneratedDashboardAndRejectsDrift()
    {
        var scope = ServiceMetricTestData.Scope();
        var catalog = ServiceMetricCatalog.Parse(ServiceMetricTestData.PackYaml);
        var generator = new ServiceDashboardGenerator();
        var validator = new ServiceOnboardingValidator(generator);
        var generated = generator.Generate("payments-production", catalog.CompilePlan(scope));
        var drifted = JsonNode.Parse(generated)!.AsObject();
        drifted["title"] = "Hand-edited dashboard";

        var evidence = ServiceTelemetryEvidenceAndAssessmentTests.Evidence();
        var valid = validator.Validate("payments-production", scope, catalog, generated, evidence);
        var invalid = validator.Validate(
            "payments-production",
            scope,
            catalog,
            drifted.ToJsonString(),
            evidence);

        Assert.True(valid.IsValid);
        Assert.Empty(valid.Errors);
        Assert.False(invalid.IsValid);
        Assert.Contains(
            invalid.Errors,
            error => error.Contains("does not match", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OnboardingValidationReturnsPlanErrorsWithoutTrustingDashboardJson()
    {
        var scope = new ServiceMetricScope
        {
            MetricPackId = "request-pack-v1",
            Service = "",
            Environment = "production"
        };
        var result = new ServiceOnboardingValidator(new ServiceDashboardGenerator()).Validate(
            "payments-production",
            scope,
            ServiceMetricCatalog.Parse(ServiceMetricTestData.PackYaml),
            "not-json",
            ServiceTelemetryEvidenceAndAssessmentTests.Evidence());

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.Contains("service", StringComparison.OrdinalIgnoreCase));
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
