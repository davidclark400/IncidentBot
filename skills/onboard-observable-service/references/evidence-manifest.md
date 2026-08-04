# Normalized telemetry evidence

Use `config/observability-evidence/<recipe-id>.json` as the auditable seam between connector-specific discovery and offline pack assessment. Keep the deterministic derived decision beside it as `<recipe-id>.assessment.json` so CI can run `assess --check`. Create the source document with `init-evidence`, then edit only normalized facts proven by authorized sources; never hand-edit the assessment.

## Source authority

Authority applies to the fact being cited, not merely to the connector name.

| Authority | Permitted sources | What it may prove |
| --- | --- | --- |
| `metric-definition` | Targeted Grafana dashboards/panels, Grafana-managed alert or recording rules, and reviewed repository dashboard/rule/instrumentation artifacts | Metric meaning, exact query and labels, datasource UID, unit, reducer, thresholds, and contract-role semantics when explicitly documented |
| `workload-context` | Reviewed service/deployment catalogs and authorized PagerDuty incident, ownership, log, trace, or chat connectors | Exact workload boundary, service/environment values, ownership, and corroborating scope only |
| `live-verification` | Explicitly authorized read-only execution of a targeted Grafana query | Whether the normalized query returned numeric data and how many logical series it produced in the chosen window |

Apply these rules:

- Require a `metric-definition` reference for `semantics`, `query`, `scope`, `datasource`, `unit`, and `reducer`, plus `thresholds` whenever either threshold is present. A context source may corroborate scope values but cannot prove a label name, PromQL expression, semantic role, datasource, unit, reducer, or threshold.
- Do not treat a returned time series as semantic proof. Live verification confirms behavior in one window; it does not establish what a metric means or whether its threshold is operationally approved.
- Do not infer a role from a metric name, panel title, log phrase, or connector type alone. Require documentation in the artifact, a reviewed rule, or an owner-reviewed repository definition.
- Preserve disagreements as explicit `gaps`. Mark the evidence `partial` and choose `blocked` or `contract-design-review` rather than silently preferring one source.

Repository connector capabilities are deliberately asymmetric. Grafana and reviewed dashboard/rule/instrumentation files can establish metric definitions. Nomad and Consul can corroborate deployed workload identity and topology; PagerDuty can corroborate ownership and incident context; VictoriaLogs and GitLab operational history can corroborate activity or failure vocabulary. None of those context connectors proves PromQL or exporter labels. Route Kafka-specific resource and metric discovery through `$onboard-kafka-app` and its separate pack contract. Panko's MCP Crumb tools are Case-window collectors, not organization-wide onboarding discovery tools; do not repurpose them for global scans.

## Manifest fields

The version-1 document contains:

- `version`: schema version `1`.
- `recipeId`: the existing reviewed Panko Recipe ID.
- `status`: `partial` or `complete`. `complete` means the supported contract and every required field are evidenced and `gaps` is empty; it does not imply live verification ran.
- `workload`: `kind`, exact `service`, exact `environment`, and `sourceRefs`. Use one `request-driven` or `worker` workload.
- `sources`: stable sanitized records with `id`, `kind`, `authority`, `locator`, and `revision`.
- `metrics`: a normalized pack-compatible `definition`, field-level `provenance`, and optional `liveVerification` outcome.
- `gaps`: actionable missing proof, conflicts, or access limitations that prevent contract proof. Record unavailable optional live verification as `not-run`, not as a gap.

Each metric `definition` uses the metric-pack fields: `id`, `title`, `role`, `promQl`, `datasourceUid`, `unit`, `timeReducer`, `crumbMode`, `requirement`, optional `warningThreshold` and `criticalThreshold`, `direction`, and `dashboardRow`.

Each metric `provenance` contains source-reference arrays for `semantics`, `query`, `scope`, `datasource`, `unit`, `reducer`, and `thresholds`. Cite the smallest adequate set of source ids for each field. A source id must resolve within the same document.

Each `liveVerification` contains `status` (`not-run`, `verified`, or `failed`), `sourceRefs`, `nonEmptyNumeric`, and `seriesCount`. Keep `not-run` explicit when access was not authorized or available. A verified required query should return non-empty numeric data and one logical series after aggregation; record contrary results as a gap.

## Normalize without inventing

1. Preserve the evidenced query's metric names, labels, filters, aggregation, rate window, and denominator.
2. Replace only the proven service and environment matcher values with the complete `{{serviceRegex}}` and `{{environmentRegex}}` placeholders. Do not broaden any other selector. Escape and compile the exact values through Panko rather than authoring a wider regex.
3. Preserve datasource UIDs, not datasource URLs. Preserve numeric thresholds only when a dashboard, alert/SLO rule, or reviewed repository artifact establishes their units and direction.
4. Use `context` with no thresholds when no reviewed threshold exists. Use `anomaly` only when both warning and critical values and their direction are evidenced.
5. Aggregate distributed replicas to one logical result without changing the evidenced meaning. Require review when that normalization is not already proven.

## Sanitize source records

Keep only references needed for audit, such as:

- `dashboard:<uid>/panel:<id>` plus dashboard version;
- `rule:<uid-or-reviewed-name>` plus rule version;
- `repo:<repository>/path:<relative-path>` plus commit or reviewed revision.

Never persist connector endpoints, datasource URLs, organization-wide exports, tokens, authorization headers, environment-variable values, raw query samples, log/trace bodies, PagerDuty incident content, chat content, or full sensitive dashboard payloads. A datasource UID, normalized PromQL expression, unit, threshold, dashboard UID/panel id, repository-relative path, and artifact revision are expected normalized evidence.

Use a narrow, whitespace-free locator made from stable identifiers and repository-relative path characters rather than copying source content; use a similarly compact revision such as a dashboard version or hexadecimal commit/content hash. The validator rejects URI schemes, authorization values, common credential assignments, and unsafe locator characters across retained manifest text. If even a locator is sensitive, assign an opaque local source id and describe the inaccessible detail in the final handoff rather than committing it.
