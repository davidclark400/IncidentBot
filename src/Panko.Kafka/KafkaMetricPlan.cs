using System.Collections.Immutable;

namespace Panko.Kafka;

/// <summary>
/// Immutable, Recipe-scoped resolution of one reviewed Kafka metric pack.
/// Only the validated catalog can construct a plan.
/// </summary>
public sealed class KafkaMetricPlan
{
    internal KafkaMetricPlan(
        string metricPackId,
        string metricPackTitle,
        string cluster,
        ImmutableArray<string> topics,
        ImmutableArray<string> consumerGroups,
        ImmutableArray<KafkaPlannedMetric> metrics)
    {
        MetricPackId = metricPackId;
        MetricPackTitle = metricPackTitle;
        Cluster = cluster;
        Topics = topics;
        ConsumerGroups = consumerGroups;
        Metrics = metrics;
    }

    public string MetricPackId { get; }
    public string MetricPackTitle { get; }
    public string Cluster { get; }
    public ImmutableArray<string> Topics { get; }
    public ImmutableArray<string> ConsumerGroups { get; }
    public ImmutableArray<KafkaPlannedMetric> Metrics { get; }
}

/// <summary>
/// One validated metric with every Recipe-dependent decision compiled once.
/// </summary>
public sealed class KafkaPlannedMetric
{
    internal KafkaPlannedMetric(
        string id,
        string title,
        string category,
        string datasourceUid,
        string resourceScope,
        string unit,
        string timeReducer,
        string crumbMode,
        string requirement,
        string dashboardRow,
        KafkaEffectiveThresholds thresholds,
        string runtimePromQl,
        string dashboardPromQl,
        KafkaExpectedScopeLabels expectedScopeLabels)
    {
        Id = id;
        Title = title;
        Category = category;
        DatasourceUid = datasourceUid;
        ResourceScope = resourceScope;
        Unit = unit;
        TimeReducer = timeReducer;
        CrumbMode = crumbMode;
        Requirement = requirement;
        DashboardRow = dashboardRow;
        Thresholds = thresholds;
        RuntimePromQl = runtimePromQl;
        DashboardPromQl = dashboardPromQl;
        ExpectedScopeLabels = expectedScopeLabels;
    }

    public string Id { get; }
    public string Title { get; }
    public string Category { get; }
    public string DatasourceUid { get; }
    public string ResourceScope { get; }
    public string Unit { get; }
    public string TimeReducer { get; }
    public string CrumbMode { get; }
    public string Requirement { get; }
    public string DashboardRow { get; }
    public KafkaEffectiveThresholds Thresholds { get; }
    public string RuntimePromQl { get; }
    public string DashboardPromQl { get; }
    public KafkaExpectedScopeLabels ExpectedScopeLabels { get; }
    public bool IsRequired => string.Equals(Requirement, "required", StringComparison.Ordinal);
}

/// <summary>
/// Label names whose returned values must match the Recipe allowlists.
/// </summary>
public sealed class KafkaExpectedScopeLabels
{
    internal KafkaExpectedScopeLabels(
        ImmutableHashSet<string> cluster,
        ImmutableHashSet<string> topic,
        ImmutableHashSet<string> consumerGroup)
    {
        Cluster = cluster;
        Topic = topic;
        ConsumerGroup = consumerGroup;
    }

    public ImmutableHashSet<string> Cluster { get; }
    public ImmutableHashSet<string> Topic { get; }
    public ImmutableHashSet<string> ConsumerGroup { get; }

    public bool Contains(string label) =>
        Cluster.Contains(label) || Topic.Contains(label) || ConsumerGroup.Contains(label);
}
