namespace IncidentBot.Kafka;

public sealed class KafkaMetricPackDocument
{
    public int Version { get; init; }
    public List<KafkaMetricPack> Packs { get; init; } = [];
}

public sealed class KafkaMetricPack
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public List<KafkaMetricDefinition> Metrics { get; init; } = [];
}

public sealed class KafkaMetricDefinition
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Category { get; init; } = "";
    public string PromQl { get; init; } = "";
    public string DatasourceUid { get; init; } = "";
    public string ResourceScope { get; init; } = "";
    public string Unit { get; init; } = "";
    public string TimeReducer { get; init; } = "";
    public string EvidenceMode { get; init; } = "";
    public string Requirement { get; init; } = "";
    public double? WarningThreshold { get; init; }
    public double? CriticalThreshold { get; init; }
    public string Direction { get; init; } = "";
    public string DashboardRow { get; init; } = "";

    public bool IsRequired => string.Equals(Requirement, "required", StringComparison.Ordinal);
}

public sealed class KafkaProfileScope
{
    public string MetricPackId { get; init; } = "";
    public string Cluster { get; init; } = "";
    public List<string> Topics { get; init; } = [];
    public List<string> ConsumerGroups { get; init; } = [];
    public Dictionary<string, KafkaMetricThresholdOverride> ThresholdOverrides { get; init; } = [];
}

public sealed class KafkaMetricThresholdOverride
{
    public double? Warning { get; init; }
    public double? Critical { get; init; }
}

public sealed record KafkaEffectiveThresholds(double Warning, double Critical, string Direction)
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
