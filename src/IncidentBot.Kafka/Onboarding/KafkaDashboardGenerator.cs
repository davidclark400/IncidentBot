using System.Text.Json;
using System.Text.Json.Nodes;

namespace IncidentBot.Kafka.Onboarding;

public sealed class KafkaDashboardGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public string Generate(
        string profileId,
        KafkaMetricPlan plan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentNullException.ThrowIfNull(plan);
        var panels = new JsonArray();
        var panelId = 1;
        var y = 0;

        foreach (var row in KafkaMetricCatalog.DashboardRows)
        {
            var metrics = plan.Metrics
                .Where(metric => metric.DashboardRow == row)
                .ToArray();
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
                $"Bot-only Kafka evidence dashboard for IncidentBot profile {profileId}; generated from metric pack {plan.MetricPackId}.",
            ["editable"] = false,
            ["fiscalYearStartMonth"] = 0,
            ["graphTooltip"] = 1,
            ["id"] = null,
            ["links"] = new JsonArray(),
            ["liveNow"] = false,
            ["panels"] = panels,
            ["refresh"] = "30s",
            ["schemaVersion"] = 39,
            ["tags"] = new JsonArray("incidentbot", "kafka", "bot-only", profileId),
            ["templating"] = new JsonObject
            {
                ["list"] = new JsonArray(
                    Variable("profile", "IncidentBot profile", [profileId], multi: false),
                    Variable("clusterRegex", "Kafka cluster", [plan.Cluster], multi: false),
                    Variable("topicRegex", "Kafka topics", plan.Topics, multi: true),
                    Variable(
                        "consumerGroupRegex",
                        "Kafka consumer groups",
                        plan.ConsumerGroups,
                        multi: true))
            },
            ["time"] = new JsonObject { ["from"] = "now-30m", ["to"] = "now" },
            ["timepicker"] = new JsonObject(),
            ["timezone"] = "browser",
            ["title"] = $"IncidentBot Kafka — {profileId}",
            ["uid"] = KafkaDashboardIdentity.Uid(profileId),
            ["version"] = 1,
            ["weekStart"] = ""
        };
        return dashboard.ToJsonString(JsonOptions).ReplaceLineEndings("\n") + "\n";
    }

    public bool Check(
        string outputPath,
        string profileId,
        KafkaMetricPlan plan,
        out string diagnostic)
    {
        var expected = Generate(profileId, plan);
        if (!File.Exists(outputPath))
        {
            diagnostic = $"Generated Kafka dashboard is missing: {outputPath}";
            return false;
        }
        var actual = File.ReadAllText(outputPath).ReplaceLineEndings("\n");
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            diagnostic = $"Generated Kafka dashboard is stale: {outputPath}";
            return false;
        }
        diagnostic = "Kafka dashboard is current.";
        return true;
    }

    private static JsonObject MetricPanel(
        int panelId,
        KafkaPlannedMetric metric,
        int x,
        int y)
    {
        var steps = ThresholdSteps(metric.Thresholds.Direction, metric.Thresholds);
        return new JsonObject
        {
            ["id"] = panelId,
            ["title"] = metric.Title,
            ["description"] =
                $"IncidentBot metric {metric.Id}; {metric.EvidenceMode} evidence; reducer {metric.TimeReducer}; unit {metric.Unit}.",
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
                    ["thresholds"] = new JsonObject { ["mode"] = "absolute", ["steps"] = steps },
                    ["unit"] = GrafanaUnit(metric.Unit)
                },
                ["overrides"] = new JsonArray()
            },
            ["gridPos"] = Grid(12, 8, x, y),
            ["options"] = new JsonObject
            {
                ["legend"] = new JsonObject
                {
                    ["calcs"] = new JsonArray("lastNotNull", "max"),
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
                ["expr"] = metric.DashboardPromQl,
                ["legendFormat"] = "__auto",
                ["range"] = true,
                ["refId"] = "A"
            })
        };
    }

    private static JsonArray ThresholdSteps(string direction, KafkaEffectiveThresholds thresholds)
    {
        if (direction == "above")
        {
            return new JsonArray(
                new JsonObject { ["color"] = "green", ["value"] = null },
                new JsonObject { ["color"] = "yellow", ["value"] = thresholds.Warning },
                new JsonObject { ["color"] = "red", ["value"] = thresholds.Critical });
        }

        var steps = new JsonArray(new JsonObject { ["color"] = "red", ["value"] = null });

        // Grafana changes color at value >= step, while runtime "below" thresholds are inclusive <=.
        // Transitioning at the next representable number preserves the exact warning/critical boundaries.
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

    private static JsonObject Variable(string name, string label, IEnumerable<string> allowed, bool multi)
    {
        var values = allowed.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var options = new JsonArray(values.Select((value, index) => new JsonObject
        {
            ["selected"] = multi || index == 0,
            ["text"] = value,
            ["value"] = value
        }).ToArray());
        JsonNode current = values.Length == 0
            ? new JsonObject
            {
                ["selected"] = false,
                ["text"] = "No consumer groups configured",
                ["value"] = ""
            }
            : multi
            ? new JsonObject
            {
                ["selected"] = true,
                ["text"] = new JsonArray(values.Select(value => JsonValue.Create(value)).ToArray()),
                ["value"] = new JsonArray(values.Select(value => JsonValue.Create(value)).ToArray())
            }
            : new JsonObject { ["selected"] = true, ["text"] = values[0], ["value"] = values[0] };
        return new JsonObject
        {
            ["name"] = name,
            ["label"] = label,
            ["type"] = "custom",
            ["hide"] = 0,
            ["includeAll"] = false,
            ["multi"] = multi,
            ["query"] = string.Join(',', values.Select(EscapeCustomVariableValue)),
            ["options"] = options,
            ["current"] = current,
            // URL-provided variable values are not constrained by the custom option list.
            // Keep URL sync disabled so dashboard queries remain profile-allowlisted.
            ["skipUrlSync"] = true
        };
    }

    private static string EscapeCustomVariableValue(string value) =>
        value.Replace(",", "\\,", StringComparison.Ordinal);

    private static JsonObject Datasource(string uid) => new()
    {
        ["type"] = "prometheus",
        ["uid"] = uid
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
        "messages/s" or "errors/s" or "retries/s" or "operations/s" => "ops",
        _ => "short"
    };
}
