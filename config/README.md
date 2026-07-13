# Investigation profile rules

Profiles are the security boundary for incident collection. PagerDuty service IDs choose a base profile; selectors then require exact alert rule or label values. Equal-specificity matches are rejected.

All Nomad namespaces/jobs, GitLab projects/paths, Grafana dashboard/panel/datasource UIDs, and VictoriaLogs tenant/stream filters must be explicit. Connectors do not enumerate global resources.

GitLab `relevantPaths` also controls which commit diff hunks may be sent to LiteLLM or exposed as line-level diagnosis links. Keep these prefixes narrow enough to exclude generated code, vendored dependencies, secrets, and unrelated services.

Query templates may use only these placeholders:

- `{{service}}`
- `{{environment}}`
- `{{cluster}}`
- `{{region}}`
- `{{component}}`

Substituted values are limited to letters, numbers, `_`, `-`, `.`, `:`, and `/`. Arbitrary alert-provided query fragments are rejected.

Profiles only enable and scope evidence sources. API/MCP transport, endpoint, timeout, limit, and credential-variable-name settings belong under `EvidenceSources` in application configuration (`appsettings*` or environment-variable overrides). Secret values belong only in the deployment secret mechanism and must not be committed.
