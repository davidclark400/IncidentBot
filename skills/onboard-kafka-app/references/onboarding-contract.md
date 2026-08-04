# Kafka onboarding contract

Run commands from the Panko root. They are offline and make no network calls.

```bash
dotnet run --project tools/Panko.KafkaOnboarding -- scan \
  --app-root <application-root> \
  --environment <environment> \
  --output <inventory.json>

dotnet run --project tools/Panko.KafkaOnboarding -- generate-dashboard \
  --recipes config/recipes.yaml \
  --recipe-id <recipe-id> \
  --metric-packs config/kafka-metric-packs.yaml \
  --output config/grafana/<recipe-id>-kafka.json

dotnet run --project tools/Panko.KafkaOnboarding -- validate \
  --inventory <inventory.json> \
  --recipes config/recipes.yaml \
  --recipe-id <recipe-id> \
  --metric-packs config/kafka-metric-packs.yaml \
  --dashboard config/grafana/<recipe-id>-kafka.json \
  --mappings config/kafka-resource-mappings.yaml
```

Omit `--mappings` when every scanned resource exactly matches its Recipe allowlist value. Repeat `generate-dashboard` with `--check` in CI. It compares bytes without rewriting the file.

When a scanner-observed resource such as a bootstrap endpoint differs from the exporter's exact label, record only the mapping proven by the supplied catalog:

```yaml
version: 1
mappings:
  - kind: cluster # cluster | topic | consumer-group
    inventoryResource: kafka.internal:9092
    recipeResource: production-kafka
    evidenceFile: exporter/catalog.yaml
    evidenceLine: 42
```

Each `(kind, inventoryResource)` may appear once. The source must exist in the scan inventory, the target must be in the selected Recipe allowlist, and the evidence file/line must identify the catalog proof. A mapping cannot resolve a dynamic scanner reference. Mappings use `recipeResource`.

The selected Recipe owns resource scope only:

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
  crumbMode: anomaly         # context | anomaly
  requirement: required         # required | optional
  warningThreshold: 500
  criticalThreshold: 5000
  direction: above              # above | below
  dashboardRow: Consumers       # Overview | Availability | Consumers | Producers | Broker | JVM
```

Copy expressions, label keys, and datasource UIDs from the supplied exporter catalog. If the catalog cannot support a required category, record that as a blocker; do not substitute a plausible metric name.
