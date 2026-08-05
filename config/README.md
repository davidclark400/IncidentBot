# Recipe rules

Recipes are the security boundary for Case admission, Crumb collection, and team ownership. Every Recipe requires a canonical lowercase `team` key; all Recipes sharing one PagerDuty service must have the same team. PagerDuty service IDs choose a base Recipe, then selectors require exact alert-rule or label values. Equal-specificity matches are rejected, and PagerDuty incidents without a team-owned Recipe cannot open a Case.

Each Recipe is also one observed service in the responder-facing hierarchy. The optional `serviceCollection` key groups related Recipes under their owning team, for example the API, workers, and data stores that make up one distributed system. Collection identity is team-scoped: `payments/checkout-platform` and `search/checkout-platform` are distinct collections, and a collection never grants access beyond its Recipe's `team`. Keys must start with a lowercase ASCII letter or digit, contain only lowercase ASCII letters, digits, and hyphens, and be at most 64 characters. When `serviceCollection` is omitted, the Recipe is placed in that team's `uncategorized` collection. Recipe files use version 3 and the `recipes` collection.

All selector-specific Recipes sharing one `pagerDutyServiceId` must use the same effective `serviceCollection` as well as the same `team`. The recent PagerDuty feed knows the PagerDuty service before it has resolved alert-specific selectors, so allowing those Recipes to span collections would make the browse hierarchy ambiguous. Startup rejects that configuration.

```yaml
version: 3
recipes:
  - id: payments-api-production
    pagerDutyServiceId: P123PAYMENTS
    team: payments
    serviceCollection: payments-platform # optional; defaults to uncategorized
    slackChannel: C0123456789
```

The Recipe team is snapshotted onto each admitted Case and Pattern. Later Recipe edits cannot transfer historical access: signed identity claims are compared with the persisted team, and queued collection fails closed if the current Recipe has been reassigned. The reserved `unmapped` team is never authorized.

All Nomad namespaces/jobs, expected Consul services/namespaces, GitLab projects/paths, observed-service metric packs and service/environment values, Grafana dashboard/panel/datasource UIDs, Kafka metric packs/clusters/topics/consumer groups, and VictoriaLogs tenant/stream filters must be explicit. Connectors do not enumerate global resources.

Consul `services` are the expected registration allowlist. Each entry has a required `name` and an optional Enterprise `namespace`; `datacenter` and Enterprise `partition` are optional Recipe-wide scope. Panko calls `/v1/health/service/{service}` without filtering to passing instances, records an empty result as unregistered, and summarizes registered instance health. Omit namespace and partition for Consul OSS.

GitLab `relevantPaths` also controls which commit diff hunks may be sent to LiteLLM or exposed as line-level diagnosis links. Keep these prefixes narrow enough to exclude generated code, vendored dependencies, secrets, and unrelated services.

Query templates may use only these placeholders:

- `{{service}}`
- `{{environment}}`
- `{{cluster}}`
- `{{region}}`
- `{{component}}`

Substituted values are limited to letters, numbers, `_`, `-`, `.`, `:`, and `/`. Arbitrary alert-provided query fragments are rejected.

Each inline Grafana query may declare `reducer` (`maximum`, `minimum`, or `last`), `warningThreshold`, `criticalThreshold`, `direction` (`above` or `below`), and a bounded display `unit`. Pack-backed queries receive those fields from the reviewed pack plus Recipe threshold overrides. Panko pairs numeric frame columns with their time column, retains the timestamp of the selected sample, records the contiguous threshold-breach interval, and applies the same reducer to samples before the Case reference time for a baseline comparison. This policy cannot be supplied by an alert or Slack prompt.

An `observability` Recipe scope selects one reviewed pack from `service-metric-packs.yaml` and supplies exact `service` and `environment` values plus optional threshold overrides. The compiler regex-escapes those values and requires both placeholders in every PromQL vector selector. Pack-backed Recipes cannot also define inline `grafana.queries`; the compiled metrics are materialized into the same reviewed query interface used by runtime collection, Slack narrowing, and MCP output checks.

One scope means one logical workload and contract, although it may aggregate any number of replicas. Split API/worker or other mixed-contract systems into selector-specific Recipes unless reviewed recording rules deliberately normalize them under one canonical service label. Required pack metrics that return no usable telemetry evidence make collection partial; `context` metrics remain informational even if they carry a reviewed display threshold.

Store the sanitized, normalized source ledger for each onboarding at `observability-evidence/<recipe-id>.json` and its deterministic derived decision at `observability-evidence/<recipe-id>.assessment.json`. Metric-definition evidence may cite exact Grafana dashboards/rules or reviewed repository artifacts; Nomad, Consul, logs, PagerDuty incident metadata, and topology are workload context and cannot establish metric-pack facts. Locators must be stable references without endpoints, credentials, raw responses, or samples.

Run `tools/Panko.ServiceOnboarding` to initialize the request/worker evidence checklist, assess exact pack reuse versus a new contract-backed pack, explain the compiled plan, generate the immutable dashboard, validate evidence/Recipe/dashboard agreement, and perform non-mutating `--check` operations. These are offline conformance checks, not live-query or full-Recipe deployability checks. Use the repository-owned `$onboard-observable-service` skill for bounded authorized discovery, then hand the generated dashboard JSON to the deployment-owned Grafana provisioning path.

VictoriaLogs queries may declare up to 20 named `anchorPatterns`. Each pattern is a .NET non-backtracking regular expression evaluated against the bounded NDJSON lines returned for that query. The first sampled line and the first sampled line matching each configured pattern become independently citable `first-error` Crumbs and Trail entries. Use stable names that describe the operational event, such as `Payment authorisation timeout`, and patterns narrow enough to identify a known failure signature. Matching happens before redaction; retained summaries and excerpts are always redacted. Slack prompt planning can select the reviewed query name, but cannot see, create, or alter its LogSQL expression or anchor patterns.

Kafka uses the separate, versioned `kafka-metric-packs.yaml` authority. Runtime Kafka PromQL permits only `{{clusterRegex}}`, `{{topicRegex}}`, and `{{consumerGroupRegex}}`; every configured allowlist value is regex-escaped before one batched Grafana query is built. The checked-in `synthetic-fixture-kafka-v1` pack is for tests and schema guidance only. Do not enable it for a real Recipe or rename its synthetic metrics into apparent production metrics; onboard from the environment's actual exporter catalog.

Run `tools/Panko.KafkaOnboarding` offline to scan an application, generate its Panko-only dashboard, and validate inventory/Recipe/dashboard coverage. The scanner reports resolved resources and unresolved dynamics with file/line evidence and ignores build, generated, and vendor trees. Dashboard generation derives Grafana-variable expressions from the same pack templates and supports non-mutating `--check` verification.

Slack-created Cases may only substitute the exact values in a Recipe's `slackPromptLabels` map. Keep that map as narrow as the channel authorization; the planning model must copy it exactly and cannot infer another environment, service, cluster, region, or component from free text.

Use an exact Slack channel ID, not a `#channel-name`, for `slackChannel`. Application configuration must map that ID to the Recipe's team under `Slack:ChannelTeams`; prompt-enabled channels must also map to the same-team Recipe under `Slack:PromptChannelRecipes`. Channel IDs are case-sensitive. A cross-team or incomplete mapping fails closed before a prompt, Case File rebuild, or Crumb-source request.

Recipes only enable and scope Crumb sources. API/MCP transport, endpoint, timeout, limit, and credential-variable-name settings belong under `CrumbSources` in application configuration (`appsettings*` or environment-variable overrides). Kafka supports only API transport through Grafana and requires a read-only credential. Secret values belong only in the deployment secret mechanism and must not be committed.

`agentCases` is an explicit per-Recipe authorization boundary for durable agent-created Cases. Keep it disabled unless the Recipe is intended for agent admission. Its input category allowlist is enforced after server-side bounding, `publishToSlack` is never caller-selectable, and `allowSourceRefresh` controls whether an authorized caller may run the Recipe's reviewed Crumb-source adapters.
