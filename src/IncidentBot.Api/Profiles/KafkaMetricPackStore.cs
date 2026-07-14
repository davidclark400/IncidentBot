using IncidentBot.Api.Options;
using IncidentBot.Kafka;
using Microsoft.Extensions.Options;

namespace IncidentBot.Api.Profiles;

/// <summary>
/// Loads and validates the repository-owned Kafka metric catalog once at startup.
/// </summary>
public sealed class KafkaMetricPackStore
{
    private readonly KafkaMetricCatalog catalog;

    public KafkaMetricPackStore(KafkaMetricCatalog catalog)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public KafkaMetricPackStore(
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

    public KafkaMetricPack GetPack(string id) => catalog.GetPack(id);

    public void ValidateProfile(KafkaProfileScope scope) => catalog.ValidateProfile(scope);

    public KafkaEffectiveThresholds EffectiveThresholds(
        KafkaMetricDefinition metric,
        KafkaMetricThresholdOverride? thresholdOverride = null) =>
        catalog.EffectiveThresholds(metric, thresholdOverride);
}
