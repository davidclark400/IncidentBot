using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Panko.Observability.Onboarding;

public static class ServiceRecipeScopeLoader
{
    private static readonly HashSet<string> AllowedKeys = new(StringComparer.Ordinal)
    {
        "metricPackId", "service", "environment", "thresholdOverrides"
    };

    public static ServiceMetricScope Load(string recipesPath, string recipeId)
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
            if (Get(recipe, "observability") is not YamlMappingNode observability)
            {
                throw new InvalidOperationException(
                    $"Recipe '{recipeId}' does not enable observability.");
            }
            if (Get(recipe, "grafana") is YamlMappingNode grafana
                && Get(grafana, "queries") is { } inlineQueries
                && (inlineQueries is not YamlSequenceNode sequence || sequence.Children.Count > 0))
            {
                throw new InvalidOperationException(
                    $"Recipe '{recipeId}' cannot combine observability metric packs with inline Grafana queries.");
            }

            var unknown = observability.Children.Keys.OfType<YamlScalarNode>()
                .Select(node => node.Value ?? "")
                .FirstOrDefault(key => !AllowedKeys.Contains(key));
            if (unknown is not null)
            {
                throw new InvalidOperationException(
                    $"Service observability Recipe scope contains unsupported key '{unknown}'.");
            }

            return new ServiceMetricScope
            {
                MetricPackId = Scalar(observability, "metricPackId") ?? "",
                Service = Scalar(observability, "service") ?? "",
                Environment = Scalar(observability, "environment") ?? "",
                ThresholdOverrides = ThresholdOverrides(observability)
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

    private static Dictionary<string, ServiceMetricThresholdOverride> ThresholdOverrides(
        YamlMappingNode observability)
    {
        var node = Get(observability, "thresholdOverrides");
        if (node is null) return [];
        if (node is not YamlMappingNode overrides)
        {
            throw new InvalidOperationException(
                "Service observability thresholdOverrides must be a mapping.");
        }

        var result = new Dictionary<string, ServiceMetricThresholdOverride>(StringComparer.Ordinal);
        foreach (var (keyNode, valueNode) in overrides.Children)
        {
            var metricId = (keyNode as YamlScalarNode)?.Value ?? "";
            if (string.IsNullOrWhiteSpace(metricId) || valueNode is not YamlMappingNode value)
            {
                throw new InvalidOperationException(
                    "Service thresholdOverrides entries must map metric ids to warning/critical values.");
            }

            var unknown = value.Children.Keys.OfType<YamlScalarNode>()
                .Select(item => item.Value ?? "")
                .FirstOrDefault(name => name is not ("warning" or "critical"));
            if (unknown is not null)
            {
                throw new InvalidOperationException(
                    $"Service threshold override '{metricId}' contains unsupported key '{unknown}'.");
            }

            result.Add(metricId, new ServiceMetricThresholdOverride
            {
                Warning = Number(value, "warning", metricId),
                Critical = Number(value, "critical", metricId)
            });
        }
        return result;
    }

    private static double? Number(YamlMappingNode parent, string key, string metricId)
    {
        var node = Get(parent, key);
        if (node is null) return null;
        var value = (node as YamlScalarNode)?.Value;
        return value is not null
               && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new InvalidOperationException(
                $"Service threshold override '{metricId}' {key} must be a number.");
    }

    private static string? Scalar(YamlMappingNode parent, string key) =>
        Get(parent, key) is YamlScalarNode scalar ? scalar.Value : null;

    private static YamlNode? Get(YamlMappingNode parent, string key) =>
        parent.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;
}
