---
name: onboard-kafka-app
description: Discover Kafka resources used by a Java, Kotlin, Spring, Kafka Streams, Spring Cloud Stream, or .NET Confluent.Kafka application and safely add a reviewed Kafka scope, real exporter metric pack, and deterministic Panko-generated Grafana dashboard. Use when onboarding an application to Panko Kafka diagnosis or checking an existing Kafka onboarding for resource and dashboard coverage.
---

# Onboard Kafka Application

Perform the entire workflow offline. Never connect to Kafka brokers and never call or modify live Grafana, Prometheus, Kafka, or deployment systems.

## Require inputs

Obtain all five inputs before scanning:

1. Application repository root.
2. Panko repository root.
3. Existing Panko Recipe ID.
4. Deployment environment.
5. The environment's real Kafka exporter/metric catalog, including exact Prometheus metric names, label names, datasource UIDs, units, and reviewed thresholds.

Do not infer exporter metrics or production mappings. Read applicable repository instructions before changing files. Read [references/onboarding-contract.md](references/onboarding-contract.md) for the CLI and YAML contract.

## Execute workflow

1. Run the repository `scan` command against the application root and write its deterministic inventory outside the application source tree or to a repository-approved generated path.
2. Review every evidence location. Treat cluster endpoints, topic/group properties, framework annotations, Helm values, and Kubernetes configuration as evidence, not as an exporter-label mapping unless the catalog confirms it. When a scanned resource differs from its exact exporter label, record the catalog-backed mapping with its evidence file and line in a version-1 resource-mapping YAML document.
3. Stop before patching if the inventory contains any unresolved required reference, lacks a resolved cluster or topic, or the exporter catalog does not map discovered resources to exact metric labels and datasource UIDs. Report file/line blockers; never guess or create an unsupported mapping.
4. Patch only the selected Recipe's `kafka` scope, the relevant pack in `config/kafka-metric-packs.yaml`, and the resource-mapping document when one is required. Preserve unrelated YAML ordering, comments, Recipes, packs, and mappings. Add a new reviewed pack when existing definitions do not exactly match the exporter catalog. Never edit the synthetic fixture pack into apparent production metrics.
5. Increment the Recipe document `revision` after any Recipe or pack onboarding change.
6. Generate the Panko dashboard JSON with `generate-dashboard`. Do not import it into Grafana.
7. Run `validate` with the scan inventory, Recipe, pack, and generated dashboard. Pass `--mappings` when scanned resources require reviewed exporter-label mappings. Fix every failure offline.
8. Run `generate-dashboard --check` to prove deterministic/idempotent generation, then run the relevant Panko tests and skill validation.

## Guardrails

- Keep Kafka transport under `CrumbSources:Kafka` in Panko application configuration; only `api` mode is supported.
- Keep Grafana URLs and credential environment-variable names out of Recipe YAML. Require a read-only Grafana credential at deployment time.
- Allow only `{{clusterRegex}}`, `{{topicRegex}}`, and `{{consumerGroupRegex}}` in pack PromQL. Use no Grafana variables or user-authored fragments in runtime templates.
- Require at least one exact topic; consumer groups are optional only when the application genuinely uses none or the exporter catalog cannot expose group metrics.
- Keep the dashboard Panko-generated from the shared PromQL pack. Do not create an application-specific React screen or live dashboard changes.
- Finish with a concise list of changed files, resolved resources, remaining blockers, and offline verification results.
