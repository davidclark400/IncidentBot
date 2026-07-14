using System.Globalization;
using YamlDotNet.RepresentationModel;

namespace IncidentBot.Kafka.Onboarding;

public static class KafkaProfileScopeLoader
{
    private static readonly HashSet<string> AllowedKafkaKeys = new(StringComparer.Ordinal)
    {
        "metricPackId", "cluster", "topics", "consumerGroups", "thresholdOverrides"
    };

    public static KafkaProfileScope Load(string profilesPath, string profileId)
    {
        if (!File.Exists(profilesPath))
        {
            throw new InvalidOperationException($"Investigation profile file was not found: {profilesPath}");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        using var reader = File.OpenText(profilesPath);
        var stream = new YamlStream();
        stream.Load(reader);
        if (stream.Documents.Count != 1
            || stream.Documents[0].RootNode is not YamlMappingNode root
            || Get(root, "profiles") is not YamlSequenceNode profiles)
        {
            throw new InvalidOperationException("Investigation profile YAML must contain one profiles sequence.");
        }

        var profile = profiles.Children
            .OfType<YamlMappingNode>()
            .SingleOrDefault(candidate => Scalar(candidate, "id") == profileId)
            ?? throw new InvalidOperationException($"Investigation profile '{profileId}' was not found.");
        if (Get(profile, "kafka") is not YamlMappingNode kafka)
        {
            throw new InvalidOperationException($"Investigation profile '{profileId}' does not enable Kafka.");
        }
        var unknown = kafka.Children.Keys.OfType<YamlScalarNode>()
            .Select(node => node.Value ?? "")
            .FirstOrDefault(key => !AllowedKafkaKeys.Contains(key));
        if (unknown is not null)
        {
            throw new InvalidOperationException($"Kafka profile scope contains unsupported key '{unknown}'.");
        }

        return new KafkaProfileScope
        {
            MetricPackId = Scalar(kafka, "metricPackId") ?? "",
            Cluster = Scalar(kafka, "cluster") ?? "",
            Topics = Sequence(kafka, "topics"),
            ConsumerGroups = Sequence(kafka, "consumerGroups"),
            ThresholdOverrides = ThresholdOverrides(kafka)
        };
    }

    private static Dictionary<string, KafkaMetricThresholdOverride> ThresholdOverrides(YamlMappingNode kafka)
    {
        if (Get(kafka, "thresholdOverrides") is not YamlMappingNode overrides) return [];
        var result = new Dictionary<string, KafkaMetricThresholdOverride>(StringComparer.Ordinal);
        foreach (var (keyNode, valueNode) in overrides.Children)
        {
            var metricId = (keyNode as YamlScalarNode)?.Value ?? "";
            if (string.IsNullOrWhiteSpace(metricId) || valueNode is not YamlMappingNode value)
            {
                throw new InvalidOperationException("Kafka thresholdOverrides entries must map metric ids to warning/critical values.");
            }
            var unknown = value.Children.Keys.OfType<YamlScalarNode>()
                .Select(node => node.Value ?? "")
                .FirstOrDefault(name => name is not ("warning" or "critical"));
            if (unknown is not null)
            {
                throw new InvalidOperationException(
                    $"Kafka threshold override '{metricId}' contains unsupported key '{unknown}'.");
            }
            result.Add(metricId, new KafkaMetricThresholdOverride
            {
                Warning = Number(value, "warning"),
                Critical = Number(value, "critical")
            });
        }
        return result;
    }

    private static List<string> Sequence(YamlMappingNode parent, string key) =>
        Get(parent, key) is not YamlSequenceNode sequence
            ? []
            : sequence.Children.Select(node => (node as YamlScalarNode)?.Value ?? "").ToList();

    private static double? Number(YamlMappingNode parent, string key)
    {
        var value = Scalar(parent, key);
        if (value is null) return null;
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"Kafka threshold override '{key}' must be a number.");
    }

    private static string? Scalar(YamlMappingNode parent, string key) =>
        Get(parent, key) is YamlScalarNode scalar ? scalar.Value : null;

    private static YamlNode? Get(YamlMappingNode parent, string key) =>
        parent.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;
}
