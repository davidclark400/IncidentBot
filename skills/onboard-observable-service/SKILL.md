---
name: onboard-observable-service
description: Discover an existing service's authorized Grafana and reviewed repository telemetry, normalize auditable metric evidence, select or create a Panko service metric pack, compile exact service/environment scope, and generate a deterministic Grafana dashboard. Use when onboarding an already-observable request-driven service or worker, performing a read-only telemetry inventory, migrating existing dashboard queries, deciding whether a standard contract can be reused, or checking evidence, metric-pack, Recipe, and dashboard conformance.
---

# Onboard Observable Service

Keep connector discovery read-only and targeted. The onboarding CLI is offline: it never discovers or mutates live systems.

Read [references/evidence-manifest.md](references/evidence-manifest.md) before using connectors or editing evidence. Read [references/onboarding-contract.md](references/onboarding-contract.md) before editing metric packs, Recipes, or dashboards.

## Establish the boundary

1. Start from an existing reviewed Recipe. Obtain its exact Recipe ID, service, environment, deployment ownership, and logical workload boundary. Never invent PagerDuty routing, team, Slack channel, or selectors.
2. Classify one workload as `request-driven` or `worker`. Use separate Recipes for API+worker, cron+stream, or other hybrids unless a reviewed normalization deliberately gives them one contract. Route unsupported or unresolved workload shapes to `contract-design-review`.
3. Identify the specific dashboards, repositories, rules, and connector scopes the user authorized. Connector availability is not authorization. Do not enumerate an organization, datasource, repository estate, or service fleet globally.

## Build the evidence manifest

Create the deterministic starting document:

```bash
dotnet run --project tools/Panko.ServiceOnboarding -- init-evidence \
  --recipe-id <recipe-id> \
  --workload-kind <request-driven|worker> \
  --service <exact-service> \
  --environment <exact-environment> \
  --output config/observability-evidence/<recipe-id>.json
```

Then:

1. Use only read-only connectors actually available in the current session. Inspect the authorized Grafana dashboards/panels and reviewed repository artifacts that are likely to describe this workload; do not claim inaccessible sources were checked.
2. Take metric truth only from targeted Grafana artifacts or reviewed repository artifacts. Use service catalogs, PagerDuty incident metadata, chat, logs, traces, and other sources only to corroborate workload scope; they cannot establish a metric query, semantic role, unit, reducer, datasource, or threshold.
3. Normalize proven facts into the manifest with field-level source references. Never infer a role from a metric name alone. Record conflicts and missing proof in `gaps` instead of choosing silently.
4. Store only sanitized metadata: stable dashboard UID/panel or repository/rule references, revision, normalized PromQL, datasource UID, units, reducers, thresholds, and verification outcomes. Never store endpoints, credentials, headers, raw samples, PagerDuty incident content, chat content, or full sensitive dashboard payloads.
5. Run live queries only when explicitly authorized. Keep them read-only and narrowly scoped to a representative window. Record the outcome and logical series count, not returned samples. Lack of live access is outstanding verification, not permission to invent evidence.

## Assess before editing

```bash
dotnet run --project tools/Panko.ServiceOnboarding -- assess \
  --evidence config/observability-evidence/<recipe-id>.json \
  --metric-packs config/service-metric-packs.yaml \
  --output config/observability-evidence/<recipe-id>.assessment.json
```

Honor the assessment decision:

- `reuse`: select the matching immutable pack and apply only the assessment's evidenced threshold overrides; do not clone it for different scope or threshold values.
- `new-pack-from-contract`: add a narrowly named, versioned pack from the evidenced proposed metrics for the supported request-driven or worker contract.
- `blocked`: stop with the missing evidence or required role; keep the manifest partial.
- `contract-design-review`: stop instead of forcing an unsupported workload or ambiguous semantic mapping into a known template.

Treat proposed metrics as an evidenced candidate, not permission to repair or re-instrument a live service. Never change a published pack's query semantics in place.

## Materialize and verify

1. Add or select the pack. After adding a new pack, rerun `assess`; proceed only when the final assessment returns `reuse`. Add the deployment-owned `observability` scope and evidenced threshold overrides to the reviewed Recipe. Remove migrated inline `grafana.queries`; preserve responder links and annotations. Increment the Recipe revision after a Recipe or pack migration.
2. Generate `config/grafana/<recipe-id>-service.json`; never hand-edit it or import it into live Grafana. Hand the artifact to the existing provisioning/GitOps owner.
3. Run `assess --check`, `validate --evidence`, `explain`, and `generate-dashboard --check`, then focused tests. Offline checks prove artifact agreement, not live telemetry or full application deployability.
4. Report changed files, workload boundary, decision, contract and pack, role mappings and source references, live-verification status, provisioning handoff, gaps, and verification results.
