using System.Text.Json;
using System.Text.Json.Nodes;

namespace Panko.Observability.Onboarding;

public sealed class ServiceDashboardGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public string Generate(string recipeId, ServiceMetricPlan plan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipeId);
        ArgumentNullException.ThrowIfNull(plan);

        var panels = new JsonArray();
        var panelId = 1;
        var y = 0;

        foreach (var row in ServiceMetricCatalog.DashboardRows)
        {
            var metrics = plan.Metrics
                .Where(metric => metric.DashboardRow == row)
                .ToArray();
            if (metrics.Length == 0) continue;

            panels.Add(new JsonObject
            {
                ["id"] = panelId++,
                ["title"] = row,
                ["type"] = "row",
                ["collapsed"] = false,
                ["gridPos"] = Grid(24, 1, 0, y)
            });
            y++;

            for (var index = 0; index < metrics.Length; index++)
            {
                var metric = metrics[index];
                var panelY = y + index / 2 * 8;
                panels.Add(MetricPanel(panelId++, metric, index % 2 * 12, panelY));
            }
            y += Math.Max(1, (int)Math.Ceiling(metrics.Length / 2d)) * 8;
        }

        var dashboard = new JsonObject
        {
            ["annotations"] = new JsonObject { ["list"] = new JsonArray() },
            ["description"] =
                $"Generated Panko Crumb dashboard for Recipe {recipeId}; "
                + $"metric pack {plan.MetricPackId}; service {plan.Service}; environment {plan.Environment}.",
            ["editable"] = false,
            ["fiscalYearStartMonth"] = 0,
            ["graphTooltip"] = 1,
            ["id"] = null,
            ["links"] = new JsonArray(),
            ["liveNow"] = false,
            ["panels"] = panels,
            ["refresh"] = "30s",
            ["schemaVersion"] = 39,
            ["tags"] = new JsonArray("panko", "service", "generated", recipeId),
            ["templating"] = new JsonObject { ["list"] = new JsonArray() },
            ["time"] = new JsonObject { ["from"] = "now-30m", ["to"] = "now" },
            ["timepicker"] = new JsonObject(),
            ["timezone"] = "browser",
            ["title"] = $"Panko Service — {recipeId}",
            ["uid"] = ServiceDashboardIdentity.Uid(recipeId),
            ["version"] = 1,
            ["weekStart"] = ""
        };

        return dashboard.ToJsonString(JsonOptions).ReplaceLineEndings("\n") + "\n";
    }

    public bool Check(
        string outputPath,
        string recipeId,
        ServiceMetricPlan plan,
        out string diagnostic)
    {
        var expected = Generate(recipeId, plan);
        if (!File.Exists(outputPath))
        {
            diagnostic = $"Generated service dashboard is missing: {outputPath}";
            return false;
        }

        var actual = File.ReadAllText(outputPath).ReplaceLineEndings("\n");
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            diagnostic = $"Generated service dashboard is stale: {outputPath}";
            return false;
        }

        diagnostic = "Service dashboard is current.";
        return true;
    }

    private static JsonObject MetricPanel(
        int panelId,
        ServicePlannedMetric metric,
        int x,
        int y)
    {
        return new JsonObject
        {
            ["id"] = panelId,
            ["title"] = metric.Title,
            ["description"] =
                $"Panko metric {metric.Id}; role {metric.Role}; Crumb mode {metric.CrumbMode}; "
                + $"reducer {metric.TimeReducer}; unit {metric.Unit}.",
            ["type"] = "timeseries",
            ["datasource"] = Datasource(metric.DatasourceUid),
            ["fieldConfig"] = new JsonObject
            {
                ["defaults"] = new JsonObject
                {
                    ["color"] = new JsonObject { ["mode"] = "palette-classic" },
                    ["custom"] = new JsonObject
                    {
                        ["drawStyle"] = "line",
                        ["fillOpacity"] = 10,
                        ["lineWidth"] = 1,
                        ["showPoints"] = "never"
                    },
                    ["thresholds"] = new JsonObject
                    {
                        ["mode"] = "absolute",
                        ["steps"] = ThresholdSteps(metric.Thresholds)
                    },
                    ["unit"] = GrafanaUnit(metric.Unit)
                },
                ["overrides"] = new JsonArray()
            },
            ["gridPos"] = Grid(12, 8, x, y),
            ["options"] = new JsonObject
            {
                ["legend"] = new JsonObject
                {
                    ["calcs"] = LegendCalculations(metric.TimeReducer),
                    ["displayMode"] = "table",
                    ["placement"] = "bottom",
                    ["showLegend"] = true
                },
                ["tooltip"] = new JsonObject { ["mode"] = "multi", ["sort"] = "desc" }
            },
            ["targets"] = new JsonArray(new JsonObject
            {
                ["datasource"] = Datasource(metric.DatasourceUid),
                ["editorMode"] = "code",
                ["expr"] = metric.PromQl,
                ["legendFormat"] = "__auto",
                ["range"] = true,
                ["refId"] = "A"
            })
        };
    }

    private static JsonArray ThresholdSteps(ServiceMetricThresholds? thresholds)
    {
        if (thresholds is null)
        {
            return new JsonArray(new JsonObject { ["color"] = "green", ["value"] = null });
        }

        if (thresholds.Direction == "above")
        {
            return new JsonArray(
                new JsonObject { ["color"] = "green", ["value"] = null },
                new JsonObject { ["color"] = "yellow", ["value"] = thresholds.Warning },
                new JsonObject { ["color"] = "red", ["value"] = thresholds.Critical });
        }

        var steps = new JsonArray(new JsonObject { ["color"] = "red", ["value"] = null });
        if (thresholds.Critical < thresholds.Warning && thresholds.Critical < double.MaxValue)
        {
            steps.Add(new JsonObject
            {
                ["color"] = "yellow",
                ["value"] = Math.BitIncrement(thresholds.Critical)
            });
        }
        if (thresholds.Warning < double.MaxValue)
        {
            steps.Add(new JsonObject
            {
                ["color"] = "green",
                ["value"] = Math.BitIncrement(thresholds.Warning)
            });
        }
        return steps;
    }

    private static JsonObject Datasource(string uid) => new()
    {
        ["type"] = "prometheus",
        ["uid"] = uid
    };

    private static JsonArray LegendCalculations(string reducer) => reducer switch
    {
        "minimum" => new JsonArray("min"),
        "last" => new JsonArray("lastNotNull"),
        _ => new JsonArray("max")
    };

    private static JsonObject Grid(int width, int height, int x, int y) => new()
    {
        ["h"] = height,
        ["w"] = width,
        ["x"] = x,
        ["y"] = y
    };

    private static string GrafanaUnit(string unit) => unit switch
    {
        "bytes/s" => "Bps",
        "seconds" => "s",
        "milliseconds" => "ms",
        "percent" => "percent",
        "ratio" => "percentunit",
        "requests/s" or "requests/second" or "jobs/s" or "operations/s" or "errors/s" => "ops",
        _ => "short"
    };
}
