using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Panko.Api.Options;
using Panko.Observability;
using Microsoft.Extensions.Options;

namespace Panko.Api.Recipes;

/// <summary>
/// Loads the repository-owned service metric catalog once and caches immutable plans by their
/// complete deployment-owned scope.
/// </summary>
public sealed class ServiceMetricPlanStore
{
    private readonly ServiceMetricCatalog catalog;
    private readonly ConcurrentDictionary<string, Lazy<ServiceMetricPlan>> plans =
        new(StringComparer.Ordinal);

    public ServiceMetricPlanStore(ServiceMetricCatalog catalog)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public ServiceMetricPlanStore(
        IOptions<PankoOptions> options,
        IWebHostEnvironment environment)
    {
        var configuredPath = options.Value.ServiceMetricPacksPath;
        var path = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(environment.ContentRootPath, configuredPath);

        if (!File.Exists(path))
        {
            var outputPath = Path.Combine(AppContext.BaseDirectory, configuredPath);
            path = File.Exists(outputPath) ? outputPath : path;
        }

        catalog = ServiceMetricCatalog.Load(path);
    }

    public ServiceMetricPlan Resolve(ServiceMetricScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.ThresholdOverrides is null)
        {
            throw new InvalidOperationException(
                "Service observability thresholdOverrides must be a mapping.");
        }
        return plans.GetOrAdd(
            CacheKey(scope),
            _ => new Lazy<ServiceMetricPlan>(
                () => catalog.CompilePlan(scope),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private static string CacheKey(ServiceMetricScope scope)
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
            scope.Service,
            scope.Environment,
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
