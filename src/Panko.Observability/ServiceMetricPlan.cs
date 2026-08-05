using System.Collections.Immutable;

namespace Panko.Observability;

/// <summary>
/// Immutable, Recipe-scoped resolution of one reviewed service metric pack.
/// Only the validated catalog can construct a plan.
/// </summary>
public sealed class ServiceMetricPlan
{
    internal ServiceMetricPlan(
        string metricPackId,
        string metricPackTitle,
        string contract,
        string service,
        string environment,
        ImmutableArray<ServicePlannedMetric> metrics)
    {
        MetricPackId = metricPackId;
        MetricPackTitle = metricPackTitle;
        Contract = contract;
        Service = service;
        Environment = environment;
        Metrics = metrics;
    }

    public string MetricPackId { get; }
    public string MetricPackTitle { get; }
    public string Contract { get; }
    public string Service { get; }
    public string Environment { get; }
    public ImmutableArray<ServicePlannedMetric> Metrics { get; }
}

/// <summary>
/// One validated metric with every Recipe-dependent decision compiled once.
/// </summary>
public sealed class ServicePlannedMetric
{
    internal ServicePlannedMetric(
        string id,
        string title,
        string role,
        string datasourceUid,
        string unit,
        string timeReducer,
        string crumbMode,
        string requirement,
        string dashboardRow,
        ServiceMetricThresholds? thresholds,
        string promQl)
    {
        Id = id;
        Title = title;
        Role = role;
        DatasourceUid = datasourceUid;
        Unit = unit;
        TimeReducer = timeReducer;
        CrumbMode = crumbMode;
        Requirement = requirement;
        DashboardRow = dashboardRow;
        Thresholds = thresholds;
        PromQl = promQl;
    }

    public string Id { get; }
    public string Title { get; }
    public string Role { get; }
    public string DatasourceUid { get; }
    public string Unit { get; }
    public string TimeReducer { get; }
    public string CrumbMode { get; }
    public string Requirement { get; }
    public string DashboardRow { get; }
    public ServiceMetricThresholds? Thresholds { get; }
    public string PromQl { get; }
    public bool IsRequired => string.Equals(Requirement, "required", StringComparison.Ordinal);
    public bool IsAnomaly => string.Equals(CrumbMode, "anomaly", StringComparison.Ordinal);
}
