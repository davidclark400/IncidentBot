using System.Text.Json.Serialization;

namespace Panko.Observability;

public sealed class ServiceMetricPackDocument
{
    public int Version { get; init; }
    public List<ServiceMetricPack> Packs { get; init; } = [];
}

public sealed class ServiceMetricPack
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Contract { get; init; } = "";
    public List<ServiceMetricDefinition> Metrics { get; init; } = [];
}

public sealed class ServiceMetricDefinition
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Role { get; init; } = "";
    public string PromQl { get; init; } = "";
    public string DatasourceUid { get; init; } = "";
    public string Unit { get; init; } = "";
    public string TimeReducer { get; init; } = "";
    public string CrumbMode { get; init; } = "";
    public string Requirement { get; init; } = "";
    public double? WarningThreshold { get; init; }
    public double? CriticalThreshold { get; init; }
    public string Direction { get; init; } = "";
    public string DashboardRow { get; init; } = "";

    [JsonIgnore]
    public bool IsRequired => string.Equals(Requirement, "required", StringComparison.Ordinal);

    [JsonIgnore]
    public bool IsAnomaly => string.Equals(CrumbMode, "anomaly", StringComparison.Ordinal);
}

public sealed class ServiceMetricScope
{
    public string MetricPackId { get; init; } = "";
    public string Service { get; init; } = "";
    public string Environment { get; init; } = "";
    public Dictionary<string, ServiceMetricThresholdOverride> ThresholdOverrides { get; init; } = [];
}

public sealed class ServiceMetricThresholdOverride
{
    public double? Warning { get; init; }
    public double? Critical { get; init; }
}

public sealed record ServiceMetricThresholds(double Warning, double Critical, string Direction)
{
    public string State(double value)
    {
        if (Direction == "above")
        {
            if (value >= Critical) return "critical";
            if (value >= Warning) return "warning";
        }
        else
        {
            if (value <= Critical) return "critical";
            if (value <= Warning) return "warning";
        }

        return "info";
    }
}
