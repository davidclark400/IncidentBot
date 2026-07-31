using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using IncidentBot.Api.Options;
using IncidentBot.Kafka;
using Microsoft.Extensions.Options;

namespace IncidentBot.Api.Profiles;

/// <summary>
/// Loads the repository-owned Kafka metric catalog once and caches immutable plans
/// by their complete profile scope.
/// </summary>
public sealed class KafkaMetricPlanStore
{
    private readonly KafkaMetricCatalog catalog;
    private readonly ConcurrentDictionary<string, Lazy<KafkaMetricPlan>> plans =
        new(StringComparer.Ordinal);

    public KafkaMetricPlanStore(KafkaMetricCatalog catalog)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public KafkaMetricPlanStore(
        IOptions<IncidentBotOptions> options,
        IWebHostEnvironment environment)
    {
        var configuredPath = options.Value.KafkaMetricPacksPath;
        var path = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(environment.ContentRootPath, configuredPath);

        if (!File.Exists(path))
        {
            var outputPath = Path.Combine(AppContext.BaseDirectory, configuredPath);
            path = File.Exists(outputPath) ? outputPath : path;
        }

        catalog = KafkaMetricCatalog.Load(path);
    }

    public KafkaMetricPlan Resolve(KafkaProfileScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return plans.GetOrAdd(
            CacheKey(scope),
            _ => new Lazy<KafkaMetricPlan>(
                () => catalog.CompilePlan(scope),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private static string CacheKey(KafkaProfileScope scope)
    {
        var overrides = scope.ThresholdOverrides
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => Encode(
            [
                item.Key,
                Number(item.Value?.Warning),
                Number(item.Value?.Critical)
            ]));
        return Encode(
        [
            scope.MetricPackId,
            scope.Cluster,
            Encode(scope.Topics.Order(StringComparer.Ordinal)),
            Encode(scope.ConsumerGroups.Order(StringComparer.Ordinal)),
            Encode(overrides)
        ]);
    }

    private static string Encode(IEnumerable<string?> values)
    {
        var encoded = new StringBuilder();
        foreach (var value in values)
        {
            if (value is null)
            {
                encoded.Append("-1:");
                continue;
            }
            encoded.Append(value.Length.ToString(CultureInfo.InvariantCulture));
            encoded.Append(':');
            encoded.Append(value);
        }
        return encoded.ToString();
    }

    private static string Number(double? value) =>
        value?.ToString("R", CultureInfo.InvariantCulture) ?? "null";
}
