# Source discovery and routing

Use source names only as internal routing details. The user supplies a service and desired outcome, not an observability inventory.

## Evidence authority

Authority applies to a fact, not to a connector brand.

| Authority | What it may prove |
| --- | --- |
| `identity` | Exact service, environment, workload kind, deployed resource, and topology identity. |
| `ownership` | Team, service collection, operational owner, Case origin, and publication routing. |
| `signal-definition` | Exact query, labels, datasource identifier, unit, reducer, threshold, log filter, anchor, or messaging resource mapping when explicitly reviewed. |
| `live-verification` | Whether one exact scoped query or resource returned a usable result in one bounded window. It does not prove semantics. |

Prefer stable catalog IDs, dashboard/panel IDs, rule IDs, deployment resource names, and reviewed configuration revisions. A live result is not permission to infer meaning, thresholds, or ownership.

## Identity and ownership

Start with service catalogs, deployment inventories, alert routing, and ownership systems that can perform an exact service-scoped lookup. Resolve:

- canonical service and environment;
- one request-driven, worker, or unresolved workload;
- team and optional service collection;
- existing Panko Recipe ID when exposed by a catalog; and
- Case-origin and publication identifiers only when explicitly reviewed.

Do not infer team, PagerDuty service, or Slack destination from a name, dashboard folder, or chat mention.

## Deployment and topology

Use exact service-scoped deployment and discovery records. Retain normalized resource names, regions, namespaces, jobs, registrations, datacenters, and partitions—not full manifests or responses. Separate workloads that have different processing contracts even when they deploy together.

## Changes

Use reviewed source-control or deployment metadata to retain project IDs, branches, environments, and narrow relevant paths. A repository connector is optional. Do not block preparation merely because source code is unavailable; mark change coverage unverified when no exact change source can be found.

## Service metrics

Use targeted reviewed dashboards, alert/recording rules, or instrumentation catalogs as `signal-definition` evidence. A metric definition requires exact query, datasource UID, unit, time reducer, semantic role, Crumb mode, requirement, direction, and sourced thresholds when thresholds are present.

Supported `request-driven-v1` roles are availability, traffic, errors, and latency. Supported `worker-v1` roles are availability, throughput, failures, and duration. Map roles by documented meaning, never by metric name or panel title alone. Aggregate replicas to one logical numeric series without changing the reviewed denominator.

Normalize only proven service and environment matcher values to `{{serviceRegex}}` and `{{environmentRegex}}`. Preserve every other selector and aggregation. Use `context` without thresholds when no threshold is reviewed; use `anomaly` only with sourced warning and critical values. Put unresolved semantics or unsupported workload shapes in gaps.

## Logs

Retain only reviewed tenant/project identifiers, stream filters, selected fields, named bounded queries, redaction expressions, and named failure anchors. Do not retain returned lines. Logs may establish operational vocabulary and validate scope, but cannot establish metric semantics.

## Messaging

When application, deployment, or catalog evidence proves asynchronous messaging, retain exact cluster, topic/stream, and consumer-group identifiers. For Kafka-like metrics, require an exporter catalog or reviewed dashboard/rule to prove exporter label mappings, query expressions, datasource UID, units, reducers, and thresholds. Never derive exporter labels from bootstrap endpoints or client configuration alone.

## Completeness pass

Assess these capability keys exactly once in the bundle:

- `case-origin`
- `deployment`
- `topology`
- `changes`
- `metrics`
- `logs`
- `messaging`
- `publication`

Use `not-applicable` only when a reviewed source proves non-use. When access is absent, use `configured-not-verified` if configuration is known or `blocked` if relevance cannot be resolved.
