# Kafka onboarding contract

Run commands from the IncidentBot root. They are offline and make no network calls.

```bash
dotnet run --project tools/IncidentBot.KafkaOnboarding -- scan \
  --app-root <application-root> \
  --environment <environment> \
  --output <inventory.json>

dotnet run --project tools/IncidentBot.KafkaOnboarding -- generate-dashboard \
  --profiles config/investigation-profiles.yaml \
  --profile-id <profile-id> \
  --metric-packs config/kafka-metric-packs.yaml \
  --output config/grafana/<profile-id>-kafka.json

dotnet run --project tools/IncidentBot.KafkaOnboarding -- validate \
  --inventory <inventory.json> \
  --profiles config/investigation-profiles.yaml \
  --profile-id <profile-id> \
  --metric-packs config/kafka-metric-packs.yaml \
  --dashboard config/grafana/<profile-id>-kafka.json \
  --mappings config/kafka-resource-mappings.yaml
```

Omit `--mappings` when every scanned resource exactly matches its profile allowlist value. Repeat `generate-dashboard` with `--check` in CI. It compares bytes without rewriting the file.

When a scanner-observed resource such as a bootstrap endpoint differs from the exporter's exact label, record only the mapping proven by the supplied catalog:

```yaml
version: 1
mappings:
  - kind: cluster # cluster | topic | consumer-group
    inventoryResource: kafka.internal:9092
    profileResource: production-kafka
    evidenceFile: exporter/catalog.yaml
    evidenceLine: 42
```

Each `(kind, inventoryResource)` may appear once. The source must exist in the scan inventory, the target must be in the selected profile allowlist, and the evidence file/line must identify the catalog proof. A mapping cannot resolve a dynamic scanner reference.

The selected profile owns resource scope only:

```yaml
kafka:
  metricPackId: reviewed-exporter-v1
  cluster: exact-exporter-cluster-label
  topics:
    - exact-topic
  consumerGroups:
    - exact-consumer-group
  thresholdOverrides:
    consumer-lag:
      warning: 500
      critical: 5000
```

Each version-1 metric definition requires every field shown here:

```yaml
- id: consumer-lag
  title: Consumer lag
  category: kafka-consumer-lag
  promQl: 'max(real_exporter_metric{cluster=~"{{clusterRegex}}",topic=~"{{topicRegex}}",consumer_group=~"{{consumerGroupRegex}}"})'
  datasourceUid: exact-grafana-datasource-uid
  resourceScope: consumer-group # cluster | topic | consumer-group
  unit: messages
  timeReducer: maximum          # maximum | minimum | last | average | sum
  evidenceMode: anomaly         # context | anomaly
  requirement: required         # required | optional
  warningThreshold: 500
  criticalThreshold: 5000
  direction: above              # above | below
  dashboardRow: Consumers       # Overview | Availability | Consumers | Producers | Broker | JVM
```

Copy expressions, label keys, and datasource UIDs from the supplied exporter catalog. If the catalog cannot support a required category, record that as a blocker; do not substitute a plausible metric name.
