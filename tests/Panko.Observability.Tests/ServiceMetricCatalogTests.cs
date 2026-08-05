namespace Panko.Observability.Tests;

public sealed class ServiceMetricCatalogTests
{
    [Fact]
    public void RequestDrivenPackCompilesToFixedImmutablePlan()
    {
        var scope = ServiceMetricTestData.Scope();
        var plan = ServiceMetricCatalog.Parse(ServiceMetricTestData.PackYaml).CompilePlan(scope);
        scope.ThresholdOverrides["latency-p99"] = new ServiceMetricThresholdOverride
        {
            Warning = 100,
            Critical = 200
        };

        Assert.Equal("request-pack-v1", plan.MetricPackId);
        Assert.Equal("Request service fixture", plan.MetricPackTitle);
        Assert.Equal(ServiceMetricCatalog.RequestDrivenContract, plan.Contract);
        Assert.Equal("payments.api+edge", plan.Service);
        Assert.Equal("prod.eu", plan.Environment);
        Assert.Equal(
            ["availability", "error-ratio", "latency-p99", "traffic-rate"],
            plan.Metrics.Select(metric => metric.Id));
        Assert.All(plan.Metrics, metric =>
        {
            Assert.DoesNotContain("{{", metric.PromQl, StringComparison.Ordinal);
            Assert.Contains(
                "service=~\"(payments\\\\.api\\\\+edge)\"",
                metric.PromQl,
                StringComparison.Ordinal);
            Assert.Contains(
                "environment=~\"(prod\\\\.eu)\"",
                metric.PromQl,
                StringComparison.Ordinal);
        });
        Assert.Equal(1, plan.Metrics.Single(metric => metric.Id == "latency-p99").Thresholds!.Warning);
        Assert.Empty(typeof(ServiceMetricPlan).GetConstructors());
        Assert.Empty(typeof(ServicePlannedMetric).GetConstructors());
    }

    [Fact]
    public void WorkerContractCompilesWhenItsRequiredRolesAreCovered()
    {
        var yaml = ServiceMetricTestData.PackYaml
            .Replace("contract: request-driven-v1", "contract: worker-v1", StringComparison.Ordinal)
            .Replace("role: traffic", "role: throughput", StringComparison.Ordinal)
            .Replace("role: errors", "role: failures", StringComparison.Ordinal)
            .Replace("role: latency", "role: duration", StringComparison.Ordinal);

        var plan = ServiceMetricCatalog.Parse(yaml).CompilePlan(ServiceMetricTestData.Scope());

        Assert.Equal(ServiceMetricCatalog.WorkerContract, plan.Contract);
        Assert.Equal(
            ["availability", "duration", "failures", "throughput"],
            plan.Metrics.Select(metric => metric.Role).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ContextMetricMayDeliberatelyHaveNoThresholds()
    {
        var metric = ServiceMetricTestData.Plan().Metrics.Single(item => item.Id == "traffic-rate");

        Assert.True(metric.IsRequired);
        Assert.False(metric.IsAnomaly);
        Assert.Null(metric.Thresholds);
    }

    [Fact]
    public void ThresholdOverridesAreCompiledWithoutMutatingTheCatalogDefaults()
    {
        var catalog = ServiceMetricCatalog.Parse(ServiceMetricTestData.PackYaml);
        var overridden = catalog.CompilePlan(ServiceMetricTestData.Scope(new()
        {
            ["latency-p99"] = new ServiceMetricThresholdOverride
            {
                Warning = 0.5,
                Critical = 1.25
            },
            ["traffic-rate"] = new ServiceMetricThresholdOverride
            {
                Warning = 100,
                Critical = 200
            }
        }));
        var defaults = catalog.CompilePlan(ServiceMetricTestData.Scope());

        var latency = overridden.Metrics.Single(metric => metric.Id == "latency-p99");
        Assert.Equal(0.5, latency.Thresholds!.Warning);
        Assert.Equal(1.25, latency.Thresholds.Critical);
        Assert.Equal("warning", latency.Thresholds.State(0.5));
        Assert.Equal("critical", latency.Thresholds.State(1.25));
        Assert.Equal(1, defaults.Metrics.Single(metric => metric.Id == "latency-p99").Thresholds!.Warning);
        Assert.Equal(100, overridden.Metrics.Single(metric => metric.Id == "traffic-rate").Thresholds!.Warning);
        Assert.Null(defaults.Metrics.Single(metric => metric.Id == "traffic-rate").Thresholds);
    }

    [Fact]
    public void APartialOverrideRetainsTheOtherReviewedDefault()
    {
        var plan = ServiceMetricTestData.Plan(new()
        {
            ["latency-p99"] = new ServiceMetricThresholdOverride { Warning = 0.75 }
        });

        var thresholds = plan.Metrics.Single(metric => metric.Id == "latency-p99").Thresholds!;
        Assert.Equal(0.75, thresholds.Warning);
        Assert.Equal(2, thresholds.Critical);
    }

    [Theory]
    [MemberData(nameof(InvalidPackDocuments))]
    public void InvalidPackDocumentsFailClosed(string _, string yaml, string expected)
    {
        var error = Assert.Throws<InvalidOperationException>(() => ServiceMetricCatalog.Parse(yaml));

        Assert.Contains(expected, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    public static IEnumerable<object[]> InvalidPackDocuments()
    {
        yield return Case(
            "unsupported version",
            Replace("version: 1", "version: 2"),
            "version 2");
        yield return Case(
            "unknown YAML property",
            Replace("title: Request service fixture", "title: Request service fixture\n    unexpected: true"),
            "YAML");
        yield return Case(
            "duplicate YAML key",
            Replace("contract: request-driven-v1", "contract: request-driven-v1\n    contract: worker-v1"),
            "YAML");
        yield return Case(
            "null pack entry",
            "version: 1\npacks: [null]\n",
            "null pack");
        yield return Case(
            "null metric entry",
            "version: 1\npacks:\n  - id: request-pack-v1\n    title: Request service fixture\n    contract: request-driven-v1\n    metrics: [null]\n",
            "null metric");
        yield return Case(
            "invalid pack id",
            Replace("id: request-pack-v1", "id: Request_Pack"),
            "pack id");
        yield return Case(
            "unsupported contract",
            Replace("contract: request-driven-v1", "contract: universal-v1"),
            "contract");
        yield return Case(
            "duplicate metric id",
            Replace("id: traffic-rate", "id: availability"),
            "Duplicate service metric id");
        yield return Case(
            "missing contract role",
            Replace("role: latency", "role: response-time"),
            "requires a required 'latency'");
        yield return Case(
            "unsupported reducer",
            Replace("timeReducer: minimum", "timeReducer: percentile"),
            "timeReducer");
        yield return Case(
            "unsupported Crumb mode",
            Replace("crumbMode: context", "crumbMode: narrative"),
            "crumbMode");
        yield return Case(
            "unsupported requirement",
            Replace("requirement: required", "requirement: mandatory", occurrence: 1),
            "requirement");
        yield return Case(
            "unsupported direction",
            Replace("direction: below", "direction: sideways"),
            "direction");
        yield return Case(
            "unsupported dashboard row",
            Replace("dashboardRow: Availability", "dashboardRow: Everything"),
            "dashboardRow");
        yield return Case(
            "invalid PromQL",
            Replace(
                "min(up{service=~\"{{serviceRegex}}\",environment=~\"{{environmentRegex}}\"})",
                "sum(up{service=~\"{{serviceRegex}}\",environment=~\"{{environmentRegex}}\"}) +"),
            "valid supported expression");
        yield return Case(
            "unknown placeholder",
            Replace("{{serviceRegex}}", "{{tenantRegex}}"),
            "not allowlisted");
        yield return Case(
            "missing environment scope",
            Replace(",environment=~\"{{environmentRegex}}\"", ""),
            "environmentRegex");
        yield return Case(
            "placeholder outside selector",
            Replace(
                "min(up{service=~\"{{serviceRegex}}\",environment=~\"{{environmentRegex}}\"})",
                "label_replace(up{service=~\"{{serviceRegex}}\",environment=~\"{{environmentRegex}}\"}, \"instance\", \"{{serviceRegex}}\", \"instance\", \"(.*)\")"),
            "complete value of a regex label matcher");
        yield return Case(
            "unscoped nested selector",
            Replace(
                "sum(rate(http_requests_total{service=~\"{{serviceRegex}}\",environment=~\"{{environmentRegex}}\"}[5m]))",
                "sum(rate(http_requests_total{service=~\"{{serviceRegex}}\",environment=~\"{{environmentRegex}}\"}[5m])) + sum(rate(global_requests_total[5m]))"),
            "Every service PromQL vector selector");
        yield return Case(
            "anomaly without thresholds",
            Replace("        warningThreshold: 0.99\n        criticalThreshold: 0.9\n", ""),
            "requires default warning and critical");
        yield return Case(
            "context with half a threshold pair",
            Replace(
                "        requirement: required\n        direction: above\n        dashboardRow: Overview",
                "        requirement: required\n        warningThreshold: 100\n        direction: above\n        dashboardRow: Overview"),
            "both warningThreshold and criticalThreshold");
        yield return Case(
            "direction-conflicting thresholds",
            Replace("criticalThreshold: 0.9", "criticalThreshold: 1"),
            "conflict with direction");
        yield return Case(
            "non-finite threshold",
            Replace("warningThreshold: 0.99", "warningThreshold: .nan"),
            "finite numbers");
    }

    [Theory]
    [InlineData("", "production", "service")]
    [InlineData(" payments", "production", "service")]
    [InlineData("payments$prod", "production", "service")]
    [InlineData("payments", "prod : escape", "environment")]
    [InlineData("payments", "{{production}}", "environment")]
    public void UnsafeOrEmptyRecipeScopeCannotReachPromQl(
        string service,
        string environment,
        string expected)
    {
        var scope = new ServiceMetricScope
        {
            MetricPackId = "request-pack-v1",
            Service = service,
            Environment = environment
        };

        var error = Assert.Throws<InvalidOperationException>(() =>
            ServiceMetricCatalog.Parse(ServiceMetricTestData.PackYaml).CompilePlan(scope));

        Assert.Contains(expected, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownEmptyAndConflictingOverridesFailClosed()
    {
        var catalog = ServiceMetricCatalog.Parse(ServiceMetricTestData.PackYaml);

        var unknown = Assert.Throws<InvalidOperationException>(() => catalog.CompilePlan(
            ServiceMetricTestData.Scope(new()
            {
                ["not-in-pack"] = new ServiceMetricThresholdOverride { Warning = 1 }
            })));
        var empty = Assert.Throws<InvalidOperationException>(() => catalog.CompilePlan(
            ServiceMetricTestData.Scope(new()
            {
                ["latency-p99"] = new ServiceMetricThresholdOverride()
            })));
        var conflicting = Assert.Throws<InvalidOperationException>(() => catalog.CompilePlan(
            ServiceMetricTestData.Scope(new()
            {
                ["latency-p99"] = new ServiceMetricThresholdOverride
                {
                    Warning = 5,
                    Critical = 2
                }
            })));

        Assert.Contains("not defined", unknown.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("must set", empty.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("conflict", conflicting.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static object[] Case(string name, string yaml, string expected) => [name, yaml, expected];

    private static string Replace(string oldValue, string newValue, int? occurrence = null)
    {
        if (occurrence is null)
        {
            Assert.Contains(oldValue, ServiceMetricTestData.PackYaml, StringComparison.Ordinal);
            return ServiceMetricTestData.PackYaml.Replace(oldValue, newValue, StringComparison.Ordinal);
        }

        var start = 0;
        var output = ServiceMetricTestData.PackYaml;
        for (var current = 1; current <= occurrence.Value; current++)
        {
            var index = output.IndexOf(oldValue, start, StringComparison.Ordinal);
            Assert.True(index >= 0, $"Fixture did not contain occurrence {occurrence} of '{oldValue}'.");
            if (current == occurrence.Value)
            {
                return string.Concat(output.AsSpan(0, index), newValue, output.AsSpan(index + oldValue.Length));
            }
            start = index + oldValue.Length;
        }

        throw new InvalidOperationException("Fixture replacement occurrence was not found.");
    }
}
