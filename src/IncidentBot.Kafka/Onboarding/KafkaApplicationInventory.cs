namespace IncidentBot.Kafka.Onboarding;

public static class KafkaInventoryResourceKinds
{
    public const string Cluster = "cluster";
    public const string Topic = "topic";
    public const string ConsumerGroup = "consumer-group";
}

public sealed record KafkaApplicationInventory(
    int Version,
    string Environment,
    IReadOnlyList<KafkaInventoryResource> Resources,
    IReadOnlyList<KafkaUnresolvedReference> UnresolvedReferences)
{
    public const int SupportedVersion = 1;

    internal void EnsureSupportedVersion()
    {
        if (Version != SupportedVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported Kafka inventory schema version {Version}; expected {SupportedVersion}.");
        }
    }
}

public sealed record KafkaInventoryResource(
    string Kind,
    string Name,
    IReadOnlyList<KafkaInventoryEvidence> Evidence);

public sealed record KafkaUnresolvedReference(
    string Kind,
    string Expression,
    string Reason,
    bool Required,
    IReadOnlyList<KafkaInventoryEvidence> Evidence);

public sealed record KafkaInventoryEvidence(
    string File,
    int Line,
    string Detector,
    string Usage,
    string Snippet);
