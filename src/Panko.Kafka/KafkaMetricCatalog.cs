using System.Collections.Immutable;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Panko.Kafka;

public sealed partial class KafkaMetricCatalog
{
    public const int SupportedVersion = 1;
    public static readonly IReadOnlyList<string> DashboardRows =
        ["Overview", "Availability", "Consumers", "Producers", "Broker", "JVM"];

    private static readonly HashSet<string> ResourceScopes =
        new(["cluster", "topic", "consumer-group"], StringComparer.Ordinal);
    private static readonly HashSet<string> Reducers =
        new(["maximum", "minimum", "last", "average", "sum"], StringComparer.Ordinal);
    private static readonly HashSet<string> CrumbModes =
        new(["context", "anomaly"], StringComparer.Ordinal);
    private static readonly HashSet<string> Requirements =
        new(["required", "optional"], StringComparer.Ordinal);
    private static readonly HashSet<string> Directions =
        new(["above", "below"], StringComparer.Ordinal);
    private readonly IReadOnlyDictionary<string, KafkaMetricPack> packs;

    private KafkaMetricCatalog(KafkaMetricPackDocument document)
    {
        packs = document.Packs.ToDictionary(pack => pack.Id, StringComparer.Ordinal);
    }

    public static KafkaMetricCatalog Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"Kafka metric-pack file was not found: {path}");
        }
        return Parse(File.ReadAllText(path));
    }

    public static KafkaMetricCatalog Parse(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            throw new InvalidOperationException("Kafka metric-pack file is empty.");
        }
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithDuplicateKeyChecking()
            .Build();
        var document = deserializer.Deserialize<KafkaMetricPackDocument>(yaml)
            ?? throw new InvalidOperationException("Kafka metric-pack file is empty.");
        Validate(document);
        return new KafkaMetricCatalog(document);
    }

    public KafkaMetricPlan CompilePlan(KafkaRecipeScope scope)
    {
        KafkaPromQlRenderer.ValidateScope(scope);
        var pack = packs.TryGetValue(scope.MetricPackId, out var selected)
            ? selected
            : throw new InvalidOperationException(
                $"Kafka metric pack '{scope.MetricPackId}' was not found.");
        if (scope.ConsumerGroups.Count == 0
            && pack.Metrics.Any(metric => metric.IsRequired && metric.ResourceScope == "consumer-group"))
        {
            throw new InvalidOperationException(
                $"Kafka metric pack '{pack.Id}' requires consumer-group evidence, but the Recipe contains no consumer groups.");
        }
        foreach (var (metricId, thresholdOverride) in scope.ThresholdOverrides)
        {
            var metric = pack.Metrics.SingleOrDefault(item => item.Id == metricId)
                ?? throw new InvalidOperationException(
                    $"Kafka threshold override '{metricId}' is not defined by metric pack '{pack.Id}'.");
            _ = EffectiveThresholds(metric, thresholdOverride);
        }

        var metrics = pack.Metrics
            .OrderBy(metric => metric.Id, StringComparer.Ordinal)
            .Select(metric =>
            {
                var labels = KafkaPromQlRenderer.ScopeLabelKeys(metric.PromQl);
                return new KafkaPlannedMetric(
                    metric.Id,
                    metric.Title,
                    metric.Category,
                    metric.DatasourceUid,
                    metric.ResourceScope,
                    metric.Unit,
                    metric.TimeReducer,
                    metric.CrumbMode,
                    metric.Requirement,
                    metric.DashboardRow,
                    EffectiveThresholds(
                        metric,
                        scope.ThresholdOverrides.GetValueOrDefault(metric.Id)),
                    KafkaPromQlRenderer.Render(metric.PromQl, scope),
                    KafkaPromQlRenderer.RenderForGrafanaVariables(metric.PromQl, scope),
                    new KafkaExpectedScopeLabels(
                        labels["clusterRegex"].ToImmutableHashSet(StringComparer.Ordinal),
                        labels["topicRegex"].ToImmutableHashSet(StringComparer.Ordinal),
                        labels["consumerGroupRegex"].ToImmutableHashSet(StringComparer.Ordinal)));
            })
            .ToImmutableArray();

        return new KafkaMetricPlan(
            pack.Id,
            pack.Title,
            scope.Cluster,
            scope.Topics.Order(StringComparer.Ordinal).ToImmutableArray(),
            scope.ConsumerGroups.Order(StringComparer.Ordinal).ToImmutableArray(),
            metrics);
    }

    private static KafkaEffectiveThresholds EffectiveThresholds(
        KafkaMetricDefinition metric,
        KafkaMetricThresholdOverride? thresholdOverride = null)
    {
        var warning = thresholdOverride?.Warning ?? metric.WarningThreshold
            ?? throw new InvalidOperationException($"Kafka metric '{metric.Id}' has no warning threshold.");
        var critical = thresholdOverride?.Critical ?? metric.CriticalThreshold
            ?? throw new InvalidOperationException($"Kafka metric '{metric.Id}' has no critical threshold.");
        ValidateThresholds(metric.Id, metric.Direction, warning, critical);
        return new KafkaEffectiveThresholds(warning, critical, metric.Direction);
    }

    private static void Validate(KafkaMetricPackDocument document)
    {
        if (document.Version != SupportedVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported Kafka metric-pack schema version {document.Version}; expected {SupportedVersion}.");
        }
        if (document.Packs.Count == 0)
        {
            throw new InvalidOperationException("Kafka metric-pack file must contain at least one pack.");
        }
        RejectDuplicate(document.Packs.Select(pack => pack.Id), "Kafka metric pack id");

        foreach (var pack in document.Packs)
        {
            ValidateIdentifier(pack.Id, "Kafka metric pack id");
            ValidateText(pack.Title, 120, $"Kafka metric pack '{pack.Id}' title");
            if (pack.Metrics.Count == 0)
            {
                throw new InvalidOperationException($"Kafka metric pack '{pack.Id}' contains no metrics.");
            }
            RejectDuplicate(pack.Metrics.Select(metric => metric.Id), $"Kafka metric id in pack '{pack.Id}'");
            RejectDuplicate(pack.Metrics.Select(metric => metric.Title), $"Kafka metric title in pack '{pack.Id}'");
            foreach (var metric in pack.Metrics) ValidateMetric(pack.Id, metric);
        }
    }

    private static void ValidateMetric(string packId, KafkaMetricDefinition metric)
    {
        ValidateIdentifier(metric.Id, $"Kafka metric id in pack '{packId}'");
        ValidateText(metric.Title, 160, $"Kafka metric '{metric.Id}' title");
        if (!metric.Category.StartsWith("kafka-", StringComparison.Ordinal)
            || !Identifier().IsMatch(metric.Category))
        {
            throw new InvalidOperationException(
                $"Kafka metric '{metric.Id}' category must be a stable 'kafka-' identifier.");
        }
        ValidateText(metric.DatasourceUid, 128, $"Kafka metric '{metric.Id}' datasourceUid");
        ValidateText(metric.Unit, 64, $"Kafka metric '{metric.Id}' unit");
        RequireValue(metric.ResourceScope, ResourceScopes, metric.Id, "resourceScope");
        RequireValue(metric.TimeReducer, Reducers, metric.Id, "timeReducer");
        RequireValue(metric.CrumbMode, CrumbModes, metric.Id, "crumbMode");
        RequireValue(metric.Requirement, Requirements, metric.Id, "requirement");
        RequireValue(metric.Direction, Directions, metric.Id, "direction");
        if (!DashboardRows.Contains(metric.DashboardRow, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Kafka metric '{metric.Id}' dashboardRow must be one of: {string.Join(", ", DashboardRows)}.");
        }

        var placeholders = KafkaPromQlRenderer.ValidateTemplate(metric.PromQl);
        if (!placeholders.Contains("clusterRegex"))
        {
            throw new InvalidOperationException($"Kafka metric '{metric.Id}' must scope PromQL by clusterRegex.");
        }
        if (metric.ResourceScope is "topic" or "consumer-group" && !placeholders.Contains("topicRegex"))
        {
            throw new InvalidOperationException($"Kafka metric '{metric.Id}' must scope PromQL by topicRegex.");
        }
        if (metric.ResourceScope == "consumer-group" && !placeholders.Contains("consumerGroupRegex"))
        {
            throw new InvalidOperationException(
                $"Kafka metric '{metric.Id}' must scope PromQL by consumerGroupRegex.");
        }
        var requiredSelectorScopes = metric.ResourceScope switch
        {
            "cluster" => new HashSet<string>(["clusterRegex"], StringComparer.Ordinal),
            "topic" => new HashSet<string>(["clusterRegex", "topicRegex"], StringComparer.Ordinal),
            "consumer-group" => new HashSet<string>(
                ["clusterRegex", "topicRegex", "consumerGroupRegex"],
                StringComparer.Ordinal),
            _ => throw new InvalidOperationException(
                $"Kafka metric '{metric.Id}' has unsupported resource scope '{metric.ResourceScope}'.")
        };
        var scopeLabelKeys = KafkaPromQlRenderer.ValidateSelectorScopes(
            metric.PromQl,
            requiredSelectorScopes);
        foreach (var placeholder in placeholders)
        {
            if (scopeLabelKeys[placeholder].Count == 0)
            {
                throw new InvalidOperationException(
                    $"Kafka metric '{metric.Id}' must use {placeholder} only as a PromQL regex label matcher.");
            }
        }
        if (metric.WarningThreshold is null || metric.CriticalThreshold is null)
        {
            throw new InvalidOperationException(
                $"Kafka metric '{metric.Id}' requires default warning and critical thresholds.");
        }
        ValidateThresholds(metric.Id, metric.Direction, metric.WarningThreshold.Value, metric.CriticalThreshold.Value);
    }

    private static void ValidateThresholds(string metricId, string direction, double warning, double critical)
    {
        if (!double.IsFinite(warning) || !double.IsFinite(critical))
        {
            throw new InvalidOperationException($"Kafka metric '{metricId}' thresholds must be finite numbers.");
        }
        if (direction == "above" && critical < warning
            || direction == "below" && critical > warning)
        {
            throw new InvalidOperationException(
                $"Kafka metric '{metricId}' warning/critical thresholds conflict with direction '{direction}'.");
        }
    }

    private static void RequireValue(
        string value,
        IReadOnlySet<string> allowed,
        string metricId,
        string field)
    {
        if (!allowed.Contains(value))
        {
            throw new InvalidOperationException(
                $"Kafka metric '{metricId}' {field} must be one of: {string.Join(", ", allowed.Order())}.");
        }
    }

    private static void ValidateIdentifier(string value, string field)
    {
        if (!Identifier().IsMatch(value))
        {
            throw new InvalidOperationException(
                $"{field} must contain 2-64 lowercase letters, digits, or hyphens and start with a letter.");
        }
    }

    private static void ValidateText(string value, int maximumLength, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Any(char.IsControl))
        {
            throw new InvalidOperationException($"{field} is required and must be at most {maximumLength} characters.");
        }
    }

    private static void RejectDuplicate(IEnumerable<string> values, string field)
    {
        var duplicate = values.GroupBy(value => value, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Duplicate {field} '{duplicate.Key}'.");
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9-]{1,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();
}
