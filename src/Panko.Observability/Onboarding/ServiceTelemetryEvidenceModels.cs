namespace Panko.Observability.Onboarding;

public static class ServiceTelemetryEvidenceStatus
{
    public const string Complete = "complete";
    public const string Partial = "partial";
}

public static class ServiceTelemetryEvidenceAuthority
{
    public const string MetricDefinition = "metric-definition";
    public const string WorkloadContext = "workload-context";
    public const string LiveVerification = "live-verification";
}

public static class ServiceTelemetryWorkloadKind
{
    public const string RequestDriven = "request-driven";
    public const string Worker = "worker";
    public const string Hybrid = "hybrid";
    public const string Unknown = "unknown";
}

public static class ServiceTelemetryLiveVerificationStatus
{
    public const string NotRun = "not-run";
    public const string Verified = "verified";
    public const string Failed = "failed";
}

public sealed class ServiceTelemetryEvidenceDocument
{
    public int Version { get; init; }
    public string RecipeId { get; init; } = "";
    public string Status { get; init; } = "";
    public ServiceTelemetryWorkloadEvidence Workload { get; init; } = new();
    public List<ServiceTelemetryEvidenceSource> Sources { get; init; } = [];
    public List<ServiceTelemetryMetricEvidence> Metrics { get; init; } = [];
    public List<string> Gaps { get; init; } = [];
}

public sealed class ServiceTelemetryWorkloadEvidence
{
    public string Kind { get; init; } = "";
    public string Service { get; init; } = "";
    public string Environment { get; init; } = "";
    public List<string> SourceRefs { get; init; } = [];
}

public sealed class ServiceTelemetryEvidenceSource
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Authority { get; init; } = "";
    public string Locator { get; init; } = "";
    public string Revision { get; init; } = "";
}

public sealed class ServiceTelemetryMetricEvidence
{
    public ServiceMetricDefinition Definition { get; init; } = new();
    public ServiceTelemetryMetricProvenance Provenance { get; init; } = new();
    public ServiceTelemetryLiveVerification LiveVerification { get; init; } = new();
}

public sealed class ServiceTelemetryMetricProvenance
{
    public List<string> Semantics { get; init; } = [];
    public List<string> Query { get; init; } = [];
    public List<string> Scope { get; init; } = [];
    public List<string> Datasource { get; init; } = [];
    public List<string> Unit { get; init; } = [];
    public List<string> Reducer { get; init; } = [];
    public List<string> Thresholds { get; init; } = [];

    internal IEnumerable<string> AllSourceRefs() =>
        Semantics.Concat(Query)
            .Concat(Scope)
            .Concat(Datasource)
            .Concat(Unit)
            .Concat(Reducer)
            .Concat(Thresholds);
}

public sealed class ServiceTelemetryLiveVerification
{
    public string Status { get; init; } = ServiceTelemetryLiveVerificationStatus.NotRun;
    public List<string> SourceRefs { get; init; } = [];
    public bool? NonEmptyNumeric { get; init; }
    public int? SeriesCount { get; init; }
}
