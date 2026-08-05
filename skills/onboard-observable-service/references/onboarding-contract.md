# Observable service onboarding contract

Run commands from the Panko repository root. Every command is offline and deterministic.

```bash
dotnet run --project tools/Panko.ServiceOnboarding -- init-evidence \
  --recipe-id <recipe-id> \
  --workload-kind <request-driven|worker> \
  --service <service> \
  --environment <environment> \
  --output config/observability-evidence/<recipe-id>.json

dotnet run --project tools/Panko.ServiceOnboarding -- assess \
  --evidence config/observability-evidence/<recipe-id>.json \
  --metric-packs config/service-metric-packs.yaml \
  --output config/observability-evidence/<recipe-id>.assessment.json

dotnet run --project tools/Panko.ServiceOnboarding -- explain \
  --recipes config/recipes.yaml \
  --recipe-id <recipe-id> \
  --metric-packs config/service-metric-packs.yaml

dotnet run --project tools/Panko.ServiceOnboarding -- generate-dashboard \
  --recipes config/recipes.yaml \
  --recipe-id <recipe-id> \
  --metric-packs config/service-metric-packs.yaml \
  --output config/grafana/<recipe-id>-service.json

dotnet run --project tools/Panko.ServiceOnboarding -- validate \
  --recipes config/recipes.yaml \
  --recipe-id <recipe-id> \
  --metric-packs config/service-metric-packs.yaml \
  --dashboard config/grafana/<recipe-id>-service.json \
  --evidence config/observability-evidence/<recipe-id>.json
```

Repeat `assess` and `generate-dashboard` with `--check` in CI. Their checks compare deterministic bytes without rewriting artifacts. `init-evidence` also accepts `--check` when an unchanged generated starting document is expected. `validate` proves evidence, metric-pack syntax, exact scope compilation, and dashboard equality. It does not contact Grafana or prove full Recipe deployability; application startup remains the complete configuration check.

The selected Recipe is a prerequisite. Complete the separate ownership/routing review first if it does not exist. This workflow must not invent a PagerDuty service ID, team, Slack channel, or selectors.

## Recipe scope

The Recipe supplies deployment-owned scope and optional threshold overrides. It does not contain PromQL.

```yaml
observability:
  metricPackId: reviewed-http-v1
  service: payments-api
  environment: production
  thresholdOverrides:
    latency-p99:
      warning: 1
      critical: 2
```

Do not combine `observability` with inline `grafana.queries`. Existing `grafana.dashboards` and `annotationTags` may remain as responder context.

One scope represents one logical workload in one environment. It may cover many replicas, regions, or allocations when they deliberately use the same exact service label and every expression aggregates to one logical result. Never widen the escaped service value into a regex. Use selector-specific Recipes or a reviewed normalized recording rule for mixed-contract systems; otherwise require `contract-design-review`.

## Supported contracts

Map roles by meaning, not by metric name.

| Contract | Role | Required meaning |
| --- | --- | --- |
| request-driven | availability | User-visible successful availability, or an explicitly documented health proxy when no SLI exists. |
| request-driven | traffic | Accepted/requested work rate; state whether it measures attempts or completions. |
| request-driven | errors | Failed-request ratio or rate with denominator, unit, and status classes explicit. |
| request-driven | latency | User-visible request duration with quantile, unit, and histogram or recording-rule aggregation explicit. |
| worker | availability | Ability to accept or execute work, using a reviewed heartbeat, replica, or SLI signal. |
| worker | throughput | Completed work rate unless the reviewed operational KPI explicitly uses attempts. |
| worker | failures | Failed-work ratio, rate, or count with retry and dead-letter treatment explicit. |
| worker | duration | Work-processing duration; queue age or consumer lag is a separate optional saturation/work metric. |

Distributed expressions must aggregate replicas while preserving the intended denominator. Prefer reviewed recording rules. Each query must reduce to one logical numeric series; especially avoid `last` over multiple indistinguishable series. Use `context` when no reviewed threshold exists and `anomaly` only with sourced warning and critical thresholds.

## Metric pack

`assess` matches a pack only when the complete normalized metric set has the same contract, PromQL, datasource UID, unit, reducer, role, Crumb mode, requirement, effective direction, and threshold presence. Exact service/environment values are Recipe scope; numeric threshold differences become minimal explicit Recipe overrides, and validation compares their effective values. Metric IDs are mapped deterministically, while titles and dashboard rows are pack-owned presentation. A merely similar or nearest pack is never selected.

```yaml
version: 1
packs:
  - id: reviewed-http-v1
    title: Reviewed HTTP service metrics
    contract: request-driven-v1
    metrics:
      - id: latency-p99
        title: p99 latency
        role: latency
        promQl: 'histogram_quantile(0.99, sum by (le) (rate(http_duration_bucket{service=~"{{serviceRegex}}",environment=~"{{environmentRegex}}"}[5m])))'
        datasourceUid: exact-datasource-uid
        unit: seconds
        timeReducer: maximum
        crumbMode: anomaly
        requirement: required
        warningThreshold: 1
        criticalThreshold: 2
        direction: above
        dashboardRow: Traffic
```

Copy and normalize only evidenced expressions, labels, units, datasource UIDs, reducers, and thresholds. Every vector selector must include both complete scope placeholders. Report the gap rather than substituting an assumed metric for a required role.

Treat a published pack ID as immutable. Changes to query, role, unit, reducer, Crumb mode, requirement, or default threshold require a new versioned ID. Find and deliberately update every Recipe that references it; regenerate every affected dashboard.

The generated JSON is a deployment artifact, not a live Grafana mutation. Record its target folder/path and provisioning owner. Panko's deterministic dashboard link may return 404 until provisioning completes.
