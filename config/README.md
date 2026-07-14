# Investigation profile rules

Profiles are the security boundary for incident collection. PagerDuty service IDs choose a base profile; selectors then require exact alert rule or label values. Equal-specificity matches are rejected.

All Nomad namespaces/jobs, GitLab projects/paths, Grafana dashboard/panel/datasource UIDs, Kafka metric packs/clusters/topics/consumer groups, and VictoriaLogs tenant/stream filters must be explicit. Connectors do not enumerate global resources.

GitLab `relevantPaths` also controls which commit diff hunks may be sent to LiteLLM or exposed as line-level diagnosis links. Keep these prefixes narrow enough to exclude generated code, vendored dependencies, secrets, and unrelated services.

Query templates may use only these placeholders:

- `{{service}}`
- `{{environment}}`
- `{{cluster}}`
- `{{region}}`
- `{{component}}`

Substituted values are limited to letters, numbers, `_`, `-`, `.`, `:`, and `/`. Arbitrary alert-provided query fragments are rejected.

Kafka uses the separate, versioned `kafka-metric-packs.yaml` authority. Runtime Kafka PromQL permits only `{{clusterRegex}}`, `{{topicRegex}}`, and `{{consumerGroupRegex}}`; every configured allowlist value is regex-escaped before one batched Grafana query is built. The checked-in `synthetic-fixture-kafka-v1` pack is for tests and schema guidance only. Do not enable it for a real profile or rename its synthetic metrics into apparent production metrics; onboard from the environment's actual exporter catalog.

Run `tools/IncidentBot.KafkaOnboarding` offline to scan an application, generate its bot-only dashboard, and validate inventory/profile/dashboard coverage. The scanner reports resolved resources and unresolved dynamics with file/line evidence and ignores build, generated, and vendor trees. Dashboard generation derives Grafana-variable expressions from the same pack templates and supports non-mutating `--check` verification.

Slack prompt investigations may only substitute the exact values in a profile's `slackPromptLabels` map. Keep that map as narrow as the channel authorization; the planning model must copy it exactly and cannot infer another environment, service, cluster, region, or component from free text.

Profiles only enable and scope evidence sources. API/MCP transport, endpoint, timeout, limit, and credential-variable-name settings belong under `EvidenceSources` in application configuration (`appsettings*` or environment-variable overrides). Kafka supports only API transport through Grafana and requires a read-only credential. Secret values belong only in the deployment secret mechanism and must not be committed.
