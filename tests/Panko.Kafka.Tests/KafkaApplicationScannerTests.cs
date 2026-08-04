namespace Panko.Kafka.Tests;

public sealed class KafkaApplicationScannerTests
{
    [Fact]
    public void ScanDiscoversSupportedJvmDotNetAndDeploymentResources()
    {
        using var app = new TemporaryApplication();
        app.Write("src/main/resources/application-production.properties", """
            spring.kafka.bootstrap-servers=prod-kafka:9092
            spring.kafka.consumer.group-id=payments-workers
            orders.topic=orders.v1
            orders.group=payments-workers
            spring.cloud.stream.bindings.events-in.destination=cloud-events.v1
            spring.cloud.stream.bindings.events-in.group=cloud-workers
            spring.kafka.streams.application-id=orders-streams
            """);
        app.Write("src/main/java/App.java", """
            import org.springframework.kafka.annotation.KafkaListener;
            import org.springframework.kafka.core.KafkaTemplate;
            class App {
              KafkaTemplate<String, String> template;
              @KafkaListener(topics = "${orders.topic}", groupId = "${orders.group}") void receive() {}
              void send() { template.send("audit.v1", "value"); }
            }
            """);
        app.Write("src/main/kotlin/Topology.kt", """
            import org.apache.kafka.streams.StreamsBuilder
            fun build(builder: StreamsBuilder) {
              builder.stream<String, String>("streams-in.v1").to("streams-out.v1")
            }
            """);
        app.Write("src/main/java/Plain.java", """
            import org.apache.kafka.clients.consumer.KafkaConsumer;
            import org.apache.kafka.clients.producer.ProducerRecord;
            class Plain {
              void run(KafkaConsumer<String,String> consumer) {
                consumer.subscribe(java.util.List.of("plain-in.v1"));
                new ProducerRecord<String,String>("plain-out.v1", "value");
              }
            }
            """);
        app.Write("src/Worker.cs", """
            using Confluent.Kafka;
            class Worker {
              void Run(IConsumer<string,string> consumer, IProducer<string,string> producer) {
                var config = new ConsumerConfig { BootstrapServers = "dotnet-kafka:9092", GroupId = "dotnet-workers" };
                consumer.Subscribe("dotnet-in.v1");
                producer.ProduceAsync("dotnet-out.v1", new Message<string,string>());
              }
            }
            """);
        app.Write("deploy/k8s.yaml", """
            apiVersion: v1
            kind: Pod
            spec:
              containers:
                - name: app
                  env:
                    - name: KAFKA_BOOTSTRAP_SERVERS
                      value: k8s-kafka:9092
                    - name: KAFKA_TOPIC
                      value: k8s-events.v1
                    - name: KAFKA_GROUP_ID
                      value: k8s-workers
            """);
        app.Write("vendor/generated/App.java", """
            import org.apache.kafka.clients.consumer.KafkaConsumer;
            class Generated { void x(KafkaConsumer c) { c.subscribe(java.util.List.of("ignored-topic")); } }
            """);

        var inventory = new KafkaApplicationScanner().Scan(app.Root, "production");
        var resources = inventory.Resources.Select(item => (item.Kind, item.Name)).ToHashSet();

        Assert.Contains((KafkaInventoryResourceKinds.Cluster, "prod-kafka:9092"), resources);
        Assert.Contains((KafkaInventoryResourceKinds.Cluster, "dotnet-kafka:9092"), resources);
        Assert.Contains((KafkaInventoryResourceKinds.Cluster, "k8s-kafka:9092"), resources);
        Assert.Contains((KafkaInventoryResourceKinds.Topic, "orders.v1"), resources);
        Assert.Contains((KafkaInventoryResourceKinds.Topic, "audit.v1"), resources);
        Assert.Contains((KafkaInventoryResourceKinds.Topic, "streams-in.v1"), resources);
        Assert.Contains((KafkaInventoryResourceKinds.Topic, "streams-out.v1"), resources);
        Assert.Contains((KafkaInventoryResourceKinds.Topic, "plain-in.v1"), resources);
        Assert.Contains((KafkaInventoryResourceKinds.Topic, "plain-out.v1"), resources);
        Assert.Contains((KafkaInventoryResourceKinds.Topic, "dotnet-in.v1"), resources);
        Assert.Contains((KafkaInventoryResourceKinds.Topic, "dotnet-out.v1"), resources);
        Assert.Contains((KafkaInventoryResourceKinds.Topic, "cloud-events.v1"), resources);
        Assert.Contains((KafkaInventoryResourceKinds.Topic, "k8s-events.v1"), resources);
        Assert.Contains((KafkaInventoryResourceKinds.ConsumerGroup, "payments-workers"), resources);
        Assert.Contains((KafkaInventoryResourceKinds.ConsumerGroup, "orders-streams"), resources);
        Assert.Contains((KafkaInventoryResourceKinds.ConsumerGroup, "dotnet-workers"), resources);
        Assert.Contains((KafkaInventoryResourceKinds.ConsumerGroup, "cloud-workers"), resources);
        Assert.Contains((KafkaInventoryResourceKinds.ConsumerGroup, "k8s-workers"), resources);
        Assert.DoesNotContain(resources, item => item.Name == "ignored-topic");
        Assert.All(inventory.Resources.SelectMany(resource => resource.Evidence), evidence =>
        {
            Assert.DoesNotContain(app.Root, evidence.File, StringComparison.Ordinal);
            Assert.True(evidence.Line > 0);
        });
    }

    [Fact]
    public void ScanResolvesJavaStreamBridgeBindingAtCallSiteBeyondDeclarationDistance()
    {
        using var app = new TemporaryApplication();
        app.Write("src/main/resources/application-production.properties", """
            spring.cloud.stream.bindings.events-out-0.destination=cloud-events.v1
            """);
        app.Write("src/main/java/Publisher.java", """
            import org.springframework.cloud.stream.function.StreamBridge;
            class Publisher {
              private final StreamBridge bridge;
              Publisher(StreamBridge bridge) { this.bridge = bridge; }
              private static final String PADDING = "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx";
              void publish(Object payload) {
                bridge.send("events-out-0", payload);
              }
            }
            """);

        var inventory = new KafkaApplicationScanner().Scan(app.Root, "production");
        var destination = Assert.Single(inventory.Resources, item =>
            item.Kind == KafkaInventoryResourceKinds.Topic
            && item.Name == "cloud-events.v1");

        Assert.Contains(destination.Evidence, evidence =>
            evidence.File == "src/main/java/Publisher.java"
            && evidence.Line == 7
            && evidence.Detector == "spring-cloud-stream"
            && evidence.Snippet.Contains("bridge.send", StringComparison.Ordinal));
    }

    [Fact]
    public void ScanReportsKotlinStreamBridgeDynamicAndMissingMappingsAtCallSites()
    {
        using var app = new TemporaryApplication();
        app.Write("src/main/kotlin/Publisher.kt", """
            import org.springframework.cloud.stream.function.StreamBridge
            class Publisher(private val bridge: StreamBridge) {
              fun publish(binding: String, payload: Any) {
                bridge.send(binding, payload)
                bridge.send("missing-out-0", payload)
              }
            }
            """);

        var inventory = new KafkaApplicationScanner().Scan(app.Root, "production");

        Assert.Contains(inventory.UnresolvedReferences, item =>
            item.Kind == KafkaInventoryResourceKinds.Topic
            && item.Expression == "binding"
            && item.Required
            && item.Evidence.Any(evidence =>
                evidence.File == "src/main/kotlin/Publisher.kt"
                && evidence.Line == 4
                && evidence.Snippet.Contains("bridge.send", StringComparison.Ordinal)));
        Assert.Contains(inventory.UnresolvedReferences, item =>
            item.Kind == KafkaInventoryResourceKinds.Topic
            && item.Expression == "binding:missing-out-0"
            && item.Required
            && item.Reason.Contains("no resolved destination mapping", StringComparison.Ordinal)
            && item.Evidence.Any(evidence =>
                evidence.File == "src/main/kotlin/Publisher.kt"
                && evidence.Line == 5
                && evidence.Snippet.Contains("bridge.send", StringComparison.Ordinal)));
    }

    [Fact]
    public void ScanUsesConfiguredKafkaTemplateDefaultTopicInsteadOfPayload()
    {
        using var app = new TemporaryApplication();
        app.Write("src/main/resources/application-production.properties", """
            spring.kafka.template.default-topic=default-events.v1
            """);
        app.Write("src/main/java/DefaultPublisher.java", """
            import org.springframework.kafka.core.KafkaTemplate;
            class DefaultPublisher {
              private KafkaTemplate<String, String> template;
              void publish() {
                template.sendDefault("not-a-topic-payload");
              }
            }
            """);

        var inventory = new KafkaApplicationScanner().Scan(app.Root, "production");
        var defaultTopic = Assert.Single(inventory.Resources, item =>
            item.Kind == KafkaInventoryResourceKinds.Topic
            && item.Name == "default-events.v1");

        Assert.Contains(defaultTopic.Evidence, evidence =>
            evidence.File == "src/main/java/DefaultPublisher.java"
            && evidence.Line == 5
            && evidence.Usage == "default-producer-topic");
        Assert.DoesNotContain(inventory.Resources, item => item.Name == "not-a-topic-payload");
        Assert.DoesNotContain(inventory.UnresolvedReferences, item =>
            item.Expression.Contains("not-a-topic-payload", StringComparison.Ordinal));
    }

    [Fact]
    public void ScanReportsMissingKafkaTemplateDefaultTopicFromKotlinCallSite()
    {
        using var app = new TemporaryApplication();
        app.Write("src/main/kotlin/DefaultPublisher.kt", """
            import org.springframework.kafka.core.KafkaTemplate
            class DefaultPublisher(private val template: KafkaTemplate<String, String>) {
              fun publish(payload: String) {
                template.sendDefault(payload)
              }
            }
            """);

        var inventory = new KafkaApplicationScanner().Scan(app.Root, "production");

        Assert.Contains(inventory.UnresolvedReferences, item =>
            item.Kind == KafkaInventoryResourceKinds.Topic
            && item.Expression == "spring.kafka.template.default-topic"
            && item.Required
            && item.Reason.Contains("sendDefault", StringComparison.Ordinal)
            && item.Evidence.Any(evidence =>
                evidence.File == "src/main/kotlin/DefaultPublisher.kt"
                && evidence.Line == 4
                && evidence.Usage == "default-producer-topic"));
    }

    [Fact]
    public void ScanReportsUnresolvedDynamicsWithStableFileAndLine()
    {
        using var app = new TemporaryApplication();
        app.Write("src/Dynamic.cs", """
            using Confluent.Kafka;
            class Dynamic {
              void Run(IConsumer<string,string> consumer, string topicName) {
                consumer.Subscribe(topicName);
              }
            }
            """);
        app.Write("src/Listener.kt", """
            import org.springframework.kafka.annotation.KafkaListener
            class Listener {
              @KafkaListener(topicPattern = "events-.*") fun receive() {}
            }
            """);

        var first = new KafkaApplicationScanner().Scan(app.Root, "production");
        var second = new KafkaApplicationScanner().Scan(app.Root, "production");

        Assert.Equal(KafkaInventoryJson.Serialize(first), KafkaInventoryJson.Serialize(second));
        Assert.Contains(first.UnresolvedReferences, item =>
            item.Kind == KafkaInventoryResourceKinds.Topic
            && item.Expression.Contains("topicName", StringComparison.Ordinal)
            && item.Required
            && item.Evidence.Any(evidence => evidence.File == "src/Dynamic.cs" && evidence.Line == 4));
        Assert.Contains(first.UnresolvedReferences, item =>
            item.Kind == KafkaInventoryResourceKinds.Topic
            && item.Reason.Contains("exact topic allowlist", StringComparison.Ordinal)
            && item.Evidence.Any(evidence => evidence.File == "src/Listener.kt" && evidence.Line == 3));
    }

    [Fact]
    public void ScanFailsClosedForDuplicateKotlinLocalNamesInSeparateMethods()
    {
        using var app = new TemporaryApplication();
        app.Write("src/main/kotlin/Publisher.kt", """
            import org.springframework.kafka.core.KafkaTemplate
            class Publisher(private val template: KafkaTemplate<String, String>) {
              fun publishOrders(payload: String) {
                val topic = "orders.v1"
                template.send(topic, payload)
              }
              fun publishPayments(payload: String) {
                val topic = "payments.v1"
                template.send(topic, payload)
              }
            }
            """);

        var inventory = new KafkaApplicationScanner().Scan(app.Root, "production");

        Assert.DoesNotContain(inventory.Resources, item =>
            item.Name is "orders.v1" or "payments.v1");
        var unresolved = Assert.Single(inventory.UnresolvedReferences, item =>
            item.Kind == KafkaInventoryResourceKinds.Topic
            && item.Expression == "topic"
            && item.Required);
        Assert.Equal(2, unresolved.Evidence.Count);
        Assert.Contains(unresolved.Evidence, evidence =>
            evidence.File == "src/main/kotlin/Publisher.kt" && evidence.Line == 5);
        Assert.Contains(unresolved.Evidence, evidence =>
            evidence.File == "src/main/kotlin/Publisher.kt" && evidence.Line == 9);
    }

    [Fact]
    public void ScanReportsBothKotlinStringTemplateFormsAsUnresolved()
    {
        using var app = new TemporaryApplication();
        app.Write("application-production.properties", "tenant=configured-tenant");
        app.Write("src/main/kotlin/Publisher.kt", """
            import org.springframework.kafka.core.KafkaTemplate
            class Publisher(private val template: KafkaTemplate<String, String>) {
              fun publish(tenant: String, payload: String) {
                template.send("events-$tenant", payload)
                template.send("${tenant}", payload)
              }
            }
            """);

        var inventory = new KafkaApplicationScanner().Scan(app.Root, "production");

        Assert.DoesNotContain(inventory.Resources, item =>
            item.Kind == KafkaInventoryResourceKinds.Topic);
        Assert.Contains(inventory.UnresolvedReferences, item =>
            item.Kind == KafkaInventoryResourceKinds.Topic
            && item.Expression == "\"events-$tenant\""
            && item.Required
            && item.Reason.Contains("Kotlin-interpolated", StringComparison.Ordinal));
        Assert.Contains(inventory.UnresolvedReferences, item =>
            item.Kind == KafkaInventoryResourceKinds.Topic
            && item.Expression == "\"${tenant}\""
            && item.Required
            && item.Reason.Contains("Kotlin-interpolated", StringComparison.Ordinal));
    }

    [Fact]
    public void ScanOnlyTreatsCallsOnKafkaTemplateReceiversAsKafkaSends()
    {
        using var app = new TemporaryApplication();
        app.Write("src/main/java/Publisher.java", """
            import org.springframework.kafka.core.KafkaTemplate;
            class Publisher {
              private KafkaTemplate<String, String> kafkaTemplate;
              private Mailer mailer;
              void publish(String payload) {
                kafkaTemplate.send("kafka-events.v1", payload);
                mailer.send("welcome-email", payload);
              }
            }
            """);

        var inventory = new KafkaApplicationScanner().Scan(app.Root, "production");

        var topic = Assert.Single(inventory.Resources, item =>
            item.Kind == KafkaInventoryResourceKinds.Topic);
        Assert.Equal("kafka-events.v1", topic.Name);
        Assert.DoesNotContain(inventory.Resources, item => item.Name == "welcome-email");
        Assert.DoesNotContain(inventory.UnresolvedReferences, item =>
            item.Expression.Contains("welcome-email", StringComparison.Ordinal));
    }

    private sealed class TemporaryApplication : IDisposable
    {
        public TemporaryApplication() => Root = Directory.CreateTempSubdirectory("panko-kafka-app-").FullName;
        public string Root { get; }

        public void Write(string relativePath, string content)
        {
            var path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content.ReplaceLineEndings("\n"));
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
