using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace IncidentBot.Kafka.Onboarding;

public sealed class KafkaResourceMappingDocument
{
    public int Version { get; init; }
    public List<KafkaResourceMapping> Mappings { get; init; } = [];
}

public sealed class KafkaResourceMapping
{
    public string Kind { get; init; } = "";
    public string InventoryResource { get; init; } = "";
    public string ProfileResource { get; init; } = "";
    public string EvidenceFile { get; init; } = "";
    public int EvidenceLine { get; init; }
}

public static class KafkaResourceMappingLoader
{
    public const int SupportedVersion = 1;

    private const int MaximumResourceLength = 256;
    private const int MaximumEvidenceFileLength = 512;
    private static readonly HashSet<string> SupportedKinds = new(StringComparer.Ordinal)
    {
        KafkaInventoryResourceKinds.Cluster,
        KafkaInventoryResourceKinds.Topic,
        KafkaInventoryResourceKinds.ConsumerGroup
    };

    public static KafkaResourceMappingDocument Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"Kafka resource-mapping file was not found: {path}");
        }
        return Parse(File.ReadAllText(path));
    }

    public static KafkaResourceMappingDocument Parse(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            throw new InvalidOperationException("Kafka resource-mapping file is empty.");
        }

        try
        {
            var document = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .WithDuplicateKeyChecking()
                .Build()
                .Deserialize<KafkaResourceMappingDocument>(yaml)
                ?? throw new InvalidOperationException("Kafka resource-mapping file is empty.");
            Validate(document);
            return document;
        }
        catch (YamlException exception)
        {
            throw new InvalidOperationException(
                $"Kafka resource-mapping YAML is invalid: {exception.Message}",
                exception);
        }
    }

    internal static void Validate(KafkaResourceMappingDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Version != SupportedVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported Kafka resource-mapping schema version {document.Version}; expected {SupportedVersion}.");
        }
        if (document.Mappings is null)
        {
            throw new InvalidOperationException("Kafka resource mappings must be a YAML sequence.");
        }

        foreach (var mapping in document.Mappings)
        {
            if (!SupportedKinds.Contains(mapping.Kind))
            {
                throw new InvalidOperationException(
                    $"Kafka resource mapping kind '{mapping.Kind}' must be one of: "
                    + $"{string.Join(", ", SupportedKinds.Order(StringComparer.Ordinal))}.");
            }
            ValidateText(
                mapping.InventoryResource,
                MaximumResourceLength,
                $"Kafka {mapping.Kind} mapping inventoryResource");
            ValidateText(
                mapping.ProfileResource,
                MaximumResourceLength,
                $"Kafka {mapping.Kind} mapping profileResource");
            ValidateText(
                mapping.EvidenceFile,
                MaximumEvidenceFileLength,
                $"Kafka {mapping.Kind} mapping evidenceFile");
            if (mapping.EvidenceLine <= 0)
            {
                throw new InvalidOperationException(
                    $"Kafka {mapping.Kind} mapping evidenceLine must be a positive integer.");
            }
        }

        var duplicate = document.Mappings
            .GroupBy(mapping => (mapping.Kind, mapping.InventoryResource))
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Duplicate Kafka resource mapping for {duplicate.Key.Kind} "
                + $"'{duplicate.Key.InventoryResource}'.");
        }
    }

    private static void ValidateText(string value, int maximumLength, string field)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maximumLength
            || value.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                $"{field} is required and must be at most {maximumLength} characters.");
        }
    }
}
