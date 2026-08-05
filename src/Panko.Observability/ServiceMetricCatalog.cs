using System.Collections.Immutable;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Panko.Observability;

public sealed partial class ServiceMetricCatalog
{
    public const int SupportedVersion = 1;
    public const string RequestDrivenContract = "request-driven-v1";
    public const string WorkerContract = "worker-v1";

    public static readonly IReadOnlyList<string> DashboardRows =
        ["Overview", "Availability", "Traffic", "Dependencies", "Work", "Saturation"];

    private static readonly HashSet<string> Reducers =
        new(["maximum", "minimum", "last"], StringComparer.Ordinal);
    private static readonly HashSet<string> CrumbModes =
        new(["context", "anomaly"], StringComparer.Ordinal);
    private static readonly HashSet<string> Requirements =
        new(["required", "optional"], StringComparer.Ordinal);
    private static readonly HashSet<string> Directions =
        new(["above", "below"], StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, string[]> RequiredRolesByContract =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [RequestDrivenContract] = ["availability", "traffic", "errors", "latency"],
            [WorkerContract] = ["availability", "throughput", "failures", "duration"]
        };

    private readonly IReadOnlyDictionary<string, ServiceMetricPack> packs;

    internal IEnumerable<ServiceMetricPack> Packs => packs.Values;

    private ServiceMetricCatalog(ServiceMetricPackDocument document)
    {
        packs = document.Packs.ToDictionary(pack => pack.Id, StringComparer.Ordinal);
    }

    public static ServiceMetricCatalog Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"Service metric-pack file was not found: {path}");
        }
        return Parse(File.ReadAllText(path));
    }

    public static ServiceMetricCatalog Parse(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            throw new InvalidOperationException("Service metric-pack file is empty.");
        }

        try
        {
            var document = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .WithDuplicateKeyChecking()
                .Build()
                .Deserialize<ServiceMetricPackDocument>(yaml)
                ?? throw new InvalidOperationException("Service metric-pack file is empty.");
            Validate(document);
            return new ServiceMetricCatalog(document);
        }
        catch (YamlException exception)
        {
            throw new InvalidOperationException(
                $"Service metric-pack YAML is invalid: {exception.Message}",
                exception);
        }
    }

    public ServiceMetricPlan CompilePlan(ServiceMetricScope scope)
    {
        ServicePromQlRenderer.ValidateScope(scope);
        var pack = packs.TryGetValue(scope.MetricPackId, out var selected)
            ? selected
            : throw new InvalidOperationException(
                $"Service metric pack '{scope.MetricPackId}' was not found.");

        foreach (var (metricId, thresholdOverride) in scope.ThresholdOverrides)
        {
            var metric = pack.Metrics.SingleOrDefault(item => item.Id == metricId)
                ?? throw new InvalidOperationException(
                    $"Service threshold override '{metricId}' is not defined by metric pack '{pack.Id}'.");
            if (thresholdOverride is null
                || thresholdOverride.Warning is null && thresholdOverride.Critical is null)
            {
                throw new InvalidOperationException(
                    $"Service threshold override '{metricId}' must set warning or critical.");
            }
            _ = EffectiveThresholds(metric, thresholdOverride);
        }

        var metrics = pack.Metrics
            .OrderBy(metric => metric.Id, StringComparer.Ordinal)
            .Select(metric => new ServicePlannedMetric(
                    metric.Id,
                    metric.Title,
                    metric.Role,
                    metric.DatasourceUid,
                    metric.Unit,
                    metric.TimeReducer,
                    metric.CrumbMode,
                    metric.Requirement,
                    metric.DashboardRow,
                    EffectiveThresholds(
                        metric,
                        scope.ThresholdOverrides.GetValueOrDefault(metric.Id)),
                    ServicePromQlRenderer.Render(metric.PromQl, scope)))
            .ToImmutableArray();

        return new ServiceMetricPlan(
            pack.Id,
            pack.Title,
            pack.Contract,
            scope.Service,
            scope.Environment,
            metrics);
    }

    public static IReadOnlyList<string> RequiredRolesFor(string contract) =>
        RequiredRolesByContract.TryGetValue(contract, out var roles)
            ? Array.AsReadOnly(roles)
            : throw new InvalidOperationException(
                $"Unsupported service metric contract '{contract}'.");

    internal static void ValidateCandidate(ServiceMetricPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        Validate(new ServiceMetricPackDocument
        {
            Version = SupportedVersion,
            Packs = [pack]
        });
    }

    private static ServiceMetricThresholds? EffectiveThresholds(
        ServiceMetricDefinition metric,
        ServiceMetricThresholdOverride? thresholdOverride = null)
    {
        var warning = thresholdOverride?.Warning ?? metric.WarningThreshold;
        var critical = thresholdOverride?.Critical ?? metric.CriticalThreshold;
        if (warning is null && critical is null)
        {
            if (metric.IsAnomaly)
            {
                throw new InvalidOperationException(
                    $"Anomaly service metric '{metric.Id}' requires warning and critical thresholds.");
            }
            return null;
        }
        if (warning is null || critical is null)
        {
            throw new InvalidOperationException(
                $"Service metric '{metric.Id}' must resolve both warning and critical thresholds.");
        }

        var direction = string.IsNullOrEmpty(metric.Direction) ? "above" : metric.Direction;
        ValidateThresholds(metric.Id, direction, warning.Value, critical.Value);
        return new ServiceMetricThresholds(warning.Value, critical.Value, direction);
    }

    private static void Validate(ServiceMetricPackDocument document)
    {
        if (document.Version != SupportedVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported service metric-pack schema version {document.Version}; expected {SupportedVersion}.");
        }
        if (document.Packs is null || document.Packs.Count == 0)
        {
            throw new InvalidOperationException("Service metric-pack file must contain at least one pack.");
        }
        if (document.Packs.Any(pack => pack is null))
        {
            throw new InvalidOperationException("Service metric-pack file cannot contain null pack entries.");
        }
        RejectDuplicate(document.Packs.Select(pack => pack.Id), "service metric pack id");

        foreach (var pack in document.Packs)
        {
            ValidateIdentifier(pack.Id, "Service metric pack id");
            ValidateText(pack.Title, 120, $"Service metric pack '{pack.Id}' title");
            if (!RequiredRolesByContract.TryGetValue(pack.Contract, out var requiredRoles))
            {
                throw new InvalidOperationException(
                    $"Service metric pack '{pack.Id}' contract must be one of: "
                    + $"{string.Join(", ", RequiredRolesByContract.Keys.Order(StringComparer.Ordinal))}.");
            }
            if (pack.Metrics is null || pack.Metrics.Count == 0)
            {
                throw new InvalidOperationException($"Service metric pack '{pack.Id}' contains no metrics.");
            }
            if (pack.Metrics.Any(metric => metric is null))
            {
                throw new InvalidOperationException(
                    $"Service metric pack '{pack.Id}' cannot contain null metric entries.");
            }
            RejectDuplicate(pack.Metrics.Select(metric => metric.Id), $"service metric id in pack '{pack.Id}'");
            RejectDuplicate(pack.Metrics.Select(metric => metric.Title), $"service metric title in pack '{pack.Id}'");
            foreach (var metric in pack.Metrics) ValidateMetric(pack.Id, metric);
            foreach (var role in requiredRoles)
            {
                if (!pack.Metrics.Any(metric => metric.IsRequired && metric.Role == role))
                {
                    throw new InvalidOperationException(
                        $"Service metric pack '{pack.Id}' contract '{pack.Contract}' requires a required '{role}' metric.");
                }
            }
        }
    }

    private static void ValidateMetric(string packId, ServiceMetricDefinition metric)
    {
        ValidateIdentifier(metric.Id, $"Service metric id in pack '{packId}'");
        ValidateText(metric.Title, 160, $"Service metric '{metric.Id}' title");
        ValidateIdentifier(metric.Role, $"Service metric '{metric.Id}' role");
        ValidateText(metric.DatasourceUid, 128, $"Service metric '{metric.Id}' datasourceUid");
        ValidateText(metric.Unit, 64, $"Service metric '{metric.Id}' unit");
        RequireValue(metric.TimeReducer, Reducers, metric.Id, "timeReducer");
        RequireValue(metric.CrumbMode, CrumbModes, metric.Id, "crumbMode");
        RequireValue(metric.Requirement, Requirements, metric.Id, "requirement");
        if (metric.IsAnomaly)
        {
            RequireValue(metric.Direction, Directions, metric.Id, "direction");
        }
        else if (!string.IsNullOrEmpty(metric.Direction))
        {
            RequireValue(metric.Direction, Directions, metric.Id, "direction");
        }
        if (!DashboardRows.Contains(metric.DashboardRow, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Service metric '{metric.Id}' dashboardRow must be one of: {string.Join(", ", DashboardRows)}.");
        }

        var placeholders = ServicePromQlRenderer.ValidateTemplate(metric.PromQl);
        foreach (var required in new[] { "serviceRegex", "environmentRegex" })
        {
            if (!placeholders.Contains(required))
            {
                throw new InvalidOperationException(
                    $"Service metric '{metric.Id}' must scope PromQL by {required}.");
            }
        }

        var requiredSelectorScopes = new HashSet<string>(
            ["serviceRegex", "environmentRegex"],
            StringComparer.Ordinal);
        var scopeLabelKeys = ServicePromQlRenderer.ValidateSelectorScopes(
            metric.PromQl,
            requiredSelectorScopes);
        foreach (var placeholder in placeholders)
        {
            if (scopeLabelKeys[placeholder].Count == 0)
            {
                throw new InvalidOperationException(
                    $"Service metric '{metric.Id}' must use {placeholder} only as a PromQL regex label matcher.");
            }
        }

        if (metric.IsAnomaly
            && (metric.WarningThreshold is null || metric.CriticalThreshold is null))
        {
            throw new InvalidOperationException(
                $"Anomaly service metric '{metric.Id}' requires default warning and critical thresholds.");
        }
        if (metric.WarningThreshold is null ^ metric.CriticalThreshold is null)
        {
            throw new InvalidOperationException(
                $"Service metric '{metric.Id}' must define both warningThreshold and criticalThreshold or neither.");
        }
        if (metric.WarningThreshold is not null && metric.CriticalThreshold is not null)
        {
            ValidateThresholds(
                metric.Id,
                string.IsNullOrEmpty(metric.Direction) ? "above" : metric.Direction,
                metric.WarningThreshold.Value,
                metric.CriticalThreshold.Value);
        }
    }

    private static void ValidateThresholds(
        string metricId,
        string direction,
        double warning,
        double critical)
    {
        if (!double.IsFinite(warning) || !double.IsFinite(critical))
        {
            throw new InvalidOperationException(
                $"Service metric '{metricId}' thresholds must be finite numbers.");
        }
        if (direction == "above" && critical < warning
            || direction == "below" && critical > warning)
        {
            throw new InvalidOperationException(
                $"Service metric '{metricId}' warning/critical thresholds conflict with direction '{direction}'.");
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
                $"Service metric '{metricId}' {field} must be one of: {string.Join(", ", allowed.Order())}.");
        }
    }

    private static void ValidateIdentifier(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || !Identifier().IsMatch(value))
        {
            throw new InvalidOperationException(
                $"{field} must contain 2-64 lowercase letters, digits, or hyphens and start with a letter.");
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
