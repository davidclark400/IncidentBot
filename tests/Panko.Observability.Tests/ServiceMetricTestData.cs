namespace Panko.Observability.Tests;

internal static class ServiceMetricTestData
{
    public const string PackYaml = """
        version: 1
        packs:
          - id: request-pack-v1
            title: Request service fixture
            contract: request-driven-v1
            metrics:
              - id: availability
                title: Available instances
                role: availability
                promQl: 'min(up{service=~"{{serviceRegex}}",environment=~"{{environmentRegex}}"})'
                datasourceUid: prometheus-main
                unit: ratio
                timeReducer: minimum
                crumbMode: anomaly
                requirement: required
                warningThreshold: 0.99
                criticalThreshold: 0.9
                direction: below
                dashboardRow: Availability
              - id: traffic-rate
                title: Request rate
                role: traffic
                promQl: 'sum(rate(http_requests_total{service=~"{{serviceRegex}}",environment=~"{{environmentRegex}}"}[5m]))'
                datasourceUid: prometheus-main
                unit: requests/s
                timeReducer: maximum
                crumbMode: context
                requirement: required
                direction: above
                dashboardRow: Overview
              - id: error-ratio
                title: Error ratio
                role: errors
                promQl: 'sum(rate(http_errors_total{service=~"{{serviceRegex}}",environment=~"{{environmentRegex}}"}[5m])) / clamp_min(sum(rate(http_requests_total{service=~"{{serviceRegex}}",environment=~"{{environmentRegex}}"}[5m])), 1)'
                datasourceUid: prometheus-main
                unit: ratio
                timeReducer: maximum
                crumbMode: anomaly
                requirement: required
                warningThreshold: 0.01
                criticalThreshold: 0.05
                direction: above
                dashboardRow: Traffic
              - id: latency-p99
                title: p99 latency
                role: latency
                promQl: 'histogram_quantile(0.99, sum by (le) (rate(http_request_duration_seconds_bucket{service=~"{{serviceRegex}}",environment=~"{{environmentRegex}}"}[5m])))'
                datasourceUid: prometheus-main
                unit: seconds
                timeReducer: maximum
                crumbMode: anomaly
                requirement: required
                warningThreshold: 1
                criticalThreshold: 2
                direction: above
                dashboardRow: Traffic
        """;

    public static ServiceMetricScope Scope(
        Dictionary<string, ServiceMetricThresholdOverride>? overrides = null) => new()
        {
            MetricPackId = "request-pack-v1",
            Service = "payments.api+edge",
            Environment = "prod.eu",
            ThresholdOverrides = overrides ?? []
        };

    public static ServiceMetricPlan Plan(
        Dictionary<string, ServiceMetricThresholdOverride>? overrides = null) =>
        ServiceMetricCatalog.Parse(PackYaml).CompilePlan(Scope(overrides));
}
