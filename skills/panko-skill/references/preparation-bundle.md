# Panko service preparation bundle

The version-1 JSON bundle is the portable interface between distributed discovery and central Panko onboarding. Discovery agents produce it; Panko validates and compiles it. Do not place Panko implementation files or generated dashboards in the bundle.

## Top-level fields

- `version`: integer `1`.
- `status`: `complete`, `partial`, or `blocked`.
- `observedService`: resolved identity and ownership facts.
- `sources`: sanitized evidence-source records.
- `provenance`: JSON-pointer-like field paths mapped to source IDs.
- `coverage`: one assessment for each required capability.
- `serviceMetrics`: `null` or normalized request/worker metric evidence.
- `messaging`: zero or more normalized messaging scopes.
- `gaps`: bounded actionable gaps.

Do not add top-level fields. Keep the encoded document below 1 MiB.

## Observed service

Use this shape:

```json
{
  "name": "payments-api",
  "environment": "production",
  "workloadKind": "request-driven",
  "team": "payments",
  "serviceCollection": "payments-platform",
  "existingRecipeId": null
}
```

`name` is required. Other values may be `null` while status is `partial` or `blocked`. `workloadKind` is `request-driven`, `worker`, `contract-design-review`, or `null`. A `complete` bundle requires environment, workload kind, and team.

## Sources and provenance

Each source contains:

- `id`: a unique lowercase key containing letters, digits, `_`, or `-`;
- `kind`: `ownership`, `service-catalog`, `deployment`, `repository`, `metric-definition`, `log-definition`, `messaging-definition`, `case-origin`, `publication`, `live-verification`, or `other`;
- `authority`: `identity`, `ownership`, `signal-definition`, or `live-verification`;
- `locator`: a stable sanitized identifier such as `dashboard:payments/panel:7` or `catalog:services/payments-api`;
- `revision`: a bounded revision string or `null` when the connector supplies none.

`provenance` maps a bundle path to one or more source IDs, for example:

```json
{
  "/observedService/environment": ["deploy-payments"],
  "/coverage/metrics": ["dashboard-payments"]
}
```

Every non-null ownership or identity field requires provenance. Use field-level metric provenance inside each metric definition as described below.

Never retain URLs, endpoints, credentials, authorization values, environment-variable assignments, raw samples, logs, traces, PagerDuty incident content, chat content, or full source payloads. Keep only normalized facts and stable references.

## Coverage

Include exactly one item for every capability listed in the source-routing completeness pass. Each item contains:

- `capability`: the required capability key;
- `status`: `configured-and-verified`, `proven-not-configured`, `configured-not-verified`, `not-applicable`, or `blocked`;
- `scope`: a bounded object containing normalized source-specific facts, or `{}`;
- `sourceRefs`: source IDs supporting the disposition;
- `gaps`: short actionable strings.

Every disposition except `blocked` requires at least one source reference. `blocked` requires at least one gap. A complete bundle may contain `not-applicable`, but cannot contain `blocked`, `proven-not-configured`, `configured-not-verified`, or gaps.

## Service metrics

Use `null` when service metrics are not proven. Otherwise provide:

```json
{
  "contract": "request-driven-v1",
  "definitions": []
}
```

`contract` is `request-driven-v1` or `worker-v1`. Each definition requires:

- `id`, `title`, `role`, `promQl`, `datasourceUid`, `unit`, and `dashboardRow`;
- `timeReducer`: `maximum`, `minimum`, `last`, `average`, or `sum`;
- `crumbMode`: `context` or `anomaly`;
- `requirement`: `required` or `optional`;
- `direction`: `above` or `below`;
- `warningThreshold` and `criticalThreshold`, both numbers or both `null`; and
- `provenance`: source-ID arrays for `semantics`, `query`, `scope`, `datasource`, `unit`, `reducer`, and `thresholds`.

Every referenced source must have `signal-definition` authority. The `thresholds` array may be empty only when both thresholds are null. `anomaly` requires both thresholds. Every PromQL vector selector must include the complete `{{serviceRegex}}` and `{{environmentRegex}}` scope placeholders.

The bundle does not choose a shared pack. Central Panko onboarding compares these definitions with reviewed packs and decides exact reuse, new versioned creation, blocking, or contract-design review.

## Messaging

Each messaging item contains:

- `kind`: for version 1, `kafka`;
- `resources`: exact `cluster`, `topic`, or `consumer-group` names with source references;
- `metricDefinitions`: normalized exporter definitions when proven, otherwise an empty array; and
- `gaps`: unresolved resource, catalog, or metric-contract issues.

Each messaging metric definition requires `id`, `title`, `category`, `promQl`, `datasourceUid`, `resourceScope`, `unit`, `timeReducer`, `crumbMode`, `requirement`, both nullable thresholds, `direction`, `dashboardRow`, and field-level `provenance`. Use `cluster`, `topic`, or `consumer-group` for `resourceScope`; the query must contain the corresponding complete `{{clusterRegex}}`, `{{topicRegex}}`, and, when applicable, `{{consumerGroupRegex}}` placeholders. Messaging metric provenance uses `semantics`, `query`, `resources`, `datasource`, `unit`, `reducer`, and `thresholds`, all backed by `signal-definition` sources.

Do not translate a bootstrap endpoint into an exporter cluster label without reviewed mapping evidence. Central Panko onboarding owns resource-mapping and metric-pack compilation.

## Gaps and status

Each gap contains a stable lowercase `code`, a responder-readable `message`, and `sourceRefs`. Do not include secrets or raw source content in messages.

Use:

- `complete` only when identity is complete, every capability has a final verified/not-applicable disposition, all references resolve, and `gaps` is empty;
- `partial` when useful grounded facts exist but work remains; or
- `blocked` when the target workload or authorization cannot be resolved safely.

Machine validation proves bundle conformance and sanitization. It does not prove the external sources, approve ownership, select packs, compile a Recipe, generate dashboards, or provision live systems.
