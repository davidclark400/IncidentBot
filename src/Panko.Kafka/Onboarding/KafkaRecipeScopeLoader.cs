using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Panko.Kafka.Onboarding;

public static class KafkaRecipeScopeLoader
{
    private static readonly HashSet<string> AllowedKafkaKeys = new(StringComparer.Ordinal)
    {
        "metricPackId", "cluster", "topics", "consumerGroups", "thresholdOverrides"
    };

    public static KafkaRecipeScope Load(string recipesPath, string recipeId)
    {
        if (!File.Exists(recipesPath))
        {
            throw new InvalidOperationException($"Recipe file was not found: {recipesPath}");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(recipeId);

        try
        {
            using var reader = File.OpenText(recipesPath);
            var stream = new YamlStream();
            stream.Load(reader);
            if (stream.Documents.Count != 1
                || stream.Documents[0].RootNode is not YamlMappingNode root)
            {
                throw new InvalidOperationException(
                    "Recipe YAML must contain one recipes sequence.");
            }

            var recipes = RecipeSequence(root);
            if (recipes.Children.Any(node => node is not YamlMappingNode))
            {
                throw new InvalidOperationException(
                    "Recipe YAML recipes entries must be mappings.");
            }

            var matches = recipes.Children
                .OfType<YamlMappingNode>()
                .Where(candidate => Scalar(candidate, "id") == recipeId)
                .ToArray();
            if (matches.Length == 0)
            {
                throw new InvalidOperationException($"Recipe '{recipeId}' was not found.");
            }
            if (matches.Length > 1)
            {
                throw new InvalidOperationException($"Recipe '{recipeId}' is duplicated.");
            }

            var recipe = matches[0];
            if (Get(recipe, "kafka") is not YamlMappingNode kafka)
            {
                throw new InvalidOperationException($"Recipe '{recipeId}' does not enable Kafka.");
            }
            var unknown = kafka.Children.Keys.OfType<YamlScalarNode>()
                .Select(node => node.Value ?? "")
                .FirstOrDefault(key => !AllowedKafkaKeys.Contains(key));
            if (unknown is not null)
            {
                throw new InvalidOperationException($"Kafka Recipe scope contains unsupported key '{unknown}'.");
            }

            return new KafkaRecipeScope
            {
                MetricPackId = Scalar(kafka, "metricPackId") ?? "",
                Cluster = Scalar(kafka, "cluster") ?? "",
                Topics = Sequence(kafka, "topics"),
                ConsumerGroups = Sequence(kafka, "consumerGroups"),
                ThresholdOverrides = ThresholdOverrides(kafka)
            };
        }
        catch (YamlException exception)
        {
            throw new InvalidOperationException(
                $"Recipe YAML is invalid: {exception.Message}",
                exception);
        }
    }

    private static YamlSequenceNode RecipeSequence(YamlMappingNode root)
    {
        if (Get(root, "recipes") is YamlSequenceNode recipes) return recipes;
        throw new InvalidOperationException("Recipe YAML must contain one recipes sequence.");
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
