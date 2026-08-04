using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Panko.Observability.Onboarding;

public static partial class ServiceTelemetryEvidenceJson
{
    public const int SupportedVersion = 1;
    private const int MaximumJsonCharacters = 1_048_576;

    private static readonly HashSet<string> Statuses =
        new([ServiceTelemetryEvidenceStatus.Complete, ServiceTelemetryEvidenceStatus.Partial], StringComparer.Ordinal);
    private static readonly HashSet<string> Authorities =
        new([
            ServiceTelemetryEvidenceAuthority.MetricDefinition,
            ServiceTelemetryEvidenceAuthority.WorkloadContext,
            ServiceTelemetryEvidenceAuthority.LiveVerification
        ], StringComparer.Ordinal);
    private static readonly HashSet<string> VerificationStatuses =
        new([
            ServiceTelemetryLiveVerificationStatus.NotRun,
            ServiceTelemetryLiveVerificationStatus.Verified,
            ServiceTelemetryLiveVerificationStatus.Failed
        ], StringComparer.Ordinal);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        MaxDepth = 32
    };

    public static ServiceTelemetryEvidenceDocument Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"Service telemetry evidence file was not found: {path}");
        }
        return Deserialize(File.ReadAllText(path));
    }

    public static ServiceTelemetryEvidenceDocument Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException("Service telemetry evidence JSON is empty.");
        }
        if (json.Length > MaximumJsonCharacters)
        {
            throw new InvalidOperationException(
                $"Service telemetry evidence JSON exceeds {MaximumJsonCharacters} characters.");
        }

        using var parsed = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32
        });
        RejectDuplicateProperties(parsed.RootElement, "$");
        var evidence = JsonSerializer.Deserialize<ServiceTelemetryEvidenceDocument>(json, Options)
            ?? throw new InvalidOperationException("Service telemetry evidence JSON is empty.");
        Validate(evidence);
        return evidence;
    }

    public static string Serialize(ServiceTelemetryEvidenceDocument evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        Validate(evidence);
        return JsonSerializer.Serialize(Canonical(evidence), Options).ReplaceLineEndings("\n") + "\n";
    }

    internal static void Validate(ServiceTelemetryEvidenceDocument evidence)
    {
        if (evidence.Version != SupportedVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported service telemetry evidence schema version {evidence.Version}; expected {SupportedVersion}.");
        }
        ValidateText(evidence.RecipeId, 128, "Service telemetry evidence recipeId field");
        if (!Statuses.Contains(evidence.Status))
        {
            throw new InvalidOperationException(
                $"Service telemetry evidence status must be one of: {string.Join(", ", Statuses.Order())}.");
        }
        if (evidence.Workload is null)
        {
            throw new InvalidOperationException("Service telemetry evidence workload is required.");
        }
        if (evidence.Sources is null || evidence.Metrics is null || evidence.Gaps is null)
        {
            throw new InvalidOperationException(
                "Service telemetry evidence sources, metrics, and gaps must be arrays.");
        }
        if (evidence.Sources.Count > 100 || evidence.Metrics.Count > 100 || evidence.Gaps.Count > 100)
        {
            throw new InvalidOperationException(
                "Service telemetry evidence is limited to 100 sources, metrics, and gaps.");
        }

        ValidateIdentifier(evidence.Workload.Kind, "Service telemetry workload kind");
        ValidateOptionalText(evidence.Workload.Service, 256, "Service telemetry workload service");
        ValidateOptionalText(evidence.Workload.Environment, 256, "Service telemetry workload environment");
        RequireList(evidence.Workload.SourceRefs, "Service telemetry workload sourceRefs");

        RejectDuplicate(evidence.Sources.Select(source => source?.Id ?? ""), "service telemetry source id");
        foreach (var source in evidence.Sources)
        {
            if (source is null)
            {
                throw new InvalidOperationException("Service telemetry evidence sources cannot contain null entries.");
            }
            ValidateIdentifier(source.Id, "Service telemetry source id");
            ValidateIdentifier(source.Kind, $"Service telemetry source '{source.Id}' kind");
            if (!Authorities.Contains(source.Authority))
            {
                throw new InvalidOperationException(
                    $"Service telemetry source '{source.Id}' authority must be one of: "
                    + $"{string.Join(", ", Authorities.Order())}.");
            }
            ValidateText(source.Locator, 512, $"Service telemetry source '{source.Id}' locator");
            ValidateText(source.Revision, 160, $"Service telemetry source '{source.Id}' revision");
            if (!SafeLocator().IsMatch(source.Locator)
                || !SafeRevision().IsMatch(source.Revision))
            {
                throw new InvalidOperationException(
                    $"Service telemetry source '{source.Id}' locator and revision must use sanitized stable-reference characters.");
            }
        }

        var sources = evidence.Sources.ToDictionary(source => source.Id, StringComparer.Ordinal);
        ValidateRefs(evidence.Workload.SourceRefs, sources, "workload");
        RejectDuplicate(evidence.Metrics.Select(metric => metric?.Definition?.Id ?? ""), "service telemetry metric id");
        foreach (var metric in evidence.Metrics)
        {
            if (metric?.Definition is null || metric.Provenance is null || metric.LiveVerification is null)
            {
                throw new InvalidOperationException(
                    "Service telemetry metrics require definition, provenance, and liveVerification objects.");
            }
            ValidateIdentifier(metric.Definition.Id, "Service telemetry metric id");
            ValidateMetricText(metric.Definition);
            ValidateProvenance(metric.Definition.Id, metric.Provenance, sources);
            ValidateLiveVerification(metric.Definition.Id, metric.LiveVerification, sources);
        }

        foreach (var gap in evidence.Gaps)
        {
            ValidateText(gap, 512, "Service telemetry evidence gap");
        }
        RejectDuplicate(evidence.Gaps, "service telemetry evidence gap");
    }

    private static void ValidateMetricText(ServiceMetricDefinition metric)
    {
        ValidateOptionalText(metric.Title, 160, $"Service telemetry metric '{metric.Id}' title");
        ValidateOptionalText(metric.Role, 64, $"Service telemetry metric '{metric.Id}' role");
        ValidateOptionalText(metric.PromQl, 4096, $"Service telemetry metric '{metric.Id}' promQl");
        ValidateOptionalText(
            metric.DatasourceUid,
            128,
            $"Service telemetry metric '{metric.Id}' datasourceUid");
        ValidateOptionalText(metric.Unit, 64, $"Service telemetry metric '{metric.Id}' unit");
        ValidateOptionalText(
            metric.TimeReducer,
            64,
            $"Service telemetry metric '{metric.Id}' timeReducer");
        ValidateOptionalText(
            metric.CrumbMode,
            64,
            $"Service telemetry metric '{metric.Id}' crumbMode");
        ValidateOptionalText(
            metric.Requirement,
            64,
            $"Service telemetry metric '{metric.Id}' requirement");
        ValidateOptionalText(
            metric.Direction,
            64,
            $"Service telemetry metric '{metric.Id}' direction");
        ValidateOptionalText(
            metric.DashboardRow,
            64,
            $"Service telemetry metric '{metric.Id}' dashboardRow");
    }

    private static void ValidateProvenance(
        string metricId,
        ServiceTelemetryMetricProvenance provenance,
        IReadOnlyDictionary<string, ServiceTelemetryEvidenceSource> sources)
    {
        var fields = new Dictionary<string, List<string>?>(StringComparer.Ordinal)
        {
            ["semantics"] = provenance.Semantics,
            ["query"] = provenance.Query,
            ["scope"] = provenance.Scope,
            ["datasource"] = provenance.Datasource,
            ["unit"] = provenance.Unit,
            ["reducer"] = provenance.Reducer,
            ["thresholds"] = provenance.Thresholds
        };
        foreach (var (field, refs) in fields)
        {
            RequireList(refs, $"Service telemetry metric '{metricId}' provenance.{field}");
            ValidateRefs(refs!, sources, $"metric '{metricId}' provenance.{field}");
        }
    }

    private static void ValidateLiveVerification(
        string metricId,
        ServiceTelemetryLiveVerification verification,
        IReadOnlyDictionary<string, ServiceTelemetryEvidenceSource> sources)
    {
        if (!VerificationStatuses.Contains(verification.Status))
        {
            throw new InvalidOperationException(
                $"Service telemetry metric '{metricId}' liveVerification.status must be one of: "
                + $"{string.Join(", ", VerificationStatuses.Order())}.");
        }
        RequireList(
            verification.SourceRefs,
            $"Service telemetry metric '{metricId}' liveVerification.sourceRefs");
        ValidateRefs(verification.SourceRefs, sources, $"metric '{metricId}' liveVerification");
        if (verification.SeriesCount is < 0 or > 10_000)
        {
            throw new InvalidOperationException(
                $"Service telemetry metric '{metricId}' liveVerification.seriesCount must be between 0 and 10000.");
        }
        if (verification.Status == ServiceTelemetryLiveVerificationStatus.NotRun
            && (verification.SourceRefs.Count > 0
                || verification.NonEmptyNumeric.HasValue
                || verification.SeriesCount.HasValue))
        {
            throw new InvalidOperationException(
                $"Service telemetry metric '{metricId}' liveVerification must not contain outcomes when status is 'not-run'.");
        }
        if ((verification.Status is ServiceTelemetryLiveVerificationStatus.Verified
                or ServiceTelemetryLiveVerificationStatus.Failed)
            && !verification.SourceRefs.Any(sourceRef =>
                sources[sourceRef].Authority == ServiceTelemetryEvidenceAuthority.LiveVerification))
        {
            throw new InvalidOperationException(
                $"Service telemetry metric '{metricId}' liveVerification requires a live-verification source.");
        }
        if (verification.Status == ServiceTelemetryLiveVerificationStatus.Verified
            && (verification.NonEmptyNumeric != true || verification.SeriesCount != 1))
        {
            throw new InvalidOperationException(
                $"Service telemetry metric '{metricId}' verified live evidence must be non-empty numeric data with one logical series.");
        }
    }

    private static void ValidateRefs(
        IReadOnlyCollection<string> refs,
        IReadOnlyDictionary<string, ServiceTelemetryEvidenceSource> sources,
        string field)
    {
        RejectDuplicate(refs, $"source reference in {field}");
        foreach (var sourceRef in refs)
        {
            ValidateIdentifier(sourceRef, $"Source reference in {field}");
            if (!sources.ContainsKey(sourceRef))
            {
                throw new InvalidOperationException(
                    $"Source reference '{sourceRef}' in {field} was not found in the evidence sources.");
            }
        }
    }

    private static void RequireList<T>(IReadOnlyCollection<T>? values, string field)
    {
        if (values is null || values.Count > 100)
        {
            throw new InvalidOperationException($"{field} must be an array with at most 100 entries.");
        }
    }

    private static ServiceTelemetryEvidenceDocument Canonical(ServiceTelemetryEvidenceDocument evidence) =>
        new()
        {
            Version = evidence.Version,
            RecipeId = evidence.RecipeId,
            Status = evidence.Status,
            Workload = new ServiceTelemetryWorkloadEvidence
            {
                Kind = evidence.Workload.Kind,
                Service = evidence.Workload.Service,
                Environment = evidence.Workload.Environment,
                SourceRefs = Sorted(evidence.Workload.SourceRefs)
            },
            Sources = evidence.Sources.OrderBy(source => source.Id, StringComparer.Ordinal).ToList(),
            Metrics = evidence.Metrics
                .OrderBy(metric => metric.Definition.Id, StringComparer.Ordinal)
                .Select(Canonical)
                .ToList(),
            Gaps = evidence.Gaps.Order(StringComparer.Ordinal).ToList()
        };

    private static ServiceTelemetryMetricEvidence Canonical(ServiceTelemetryMetricEvidence metric) =>
        new()
        {
            Definition = metric.Definition,
            Provenance = new ServiceTelemetryMetricProvenance
            {
                Semantics = Sorted(metric.Provenance.Semantics),
                Query = Sorted(metric.Provenance.Query),
                Scope = Sorted(metric.Provenance.Scope),
                Datasource = Sorted(metric.Provenance.Datasource),
                Unit = Sorted(metric.Provenance.Unit),
                Reducer = Sorted(metric.Provenance.Reducer),
                Thresholds = Sorted(metric.Provenance.Thresholds)
            },
            LiveVerification = new ServiceTelemetryLiveVerification
            {
                Status = metric.LiveVerification.Status,
                SourceRefs = Sorted(metric.LiveVerification.SourceRefs),
                NonEmptyNumeric = metric.LiveVerification.NonEmptyNumeric,
                SeriesCount = metric.LiveVerification.SeriesCount
            }
        };

    private static List<string> Sorted(IEnumerable<string> values) =>
        values.Order(StringComparer.Ordinal).ToList();

    private static void RejectDuplicateProperties(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidOperationException(
                        $"Service telemetry evidence JSON contains duplicate property '{property.Name}' at {path}.");
                }
                RejectDuplicateProperties(property.Value, $"{path}.{property.Name}");
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item, $"{path}[{index++}]");
            }
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
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                $"{field} is required, must contain no control characters, and must be at most {maximumLength} characters.");
        }
        if (SensitiveContent().IsMatch(value))
        {
            throw new InvalidOperationException(
                $"{field} must not contain connector endpoints, authorization values, or credential assignments.");
        }
    }

    private static void ValidateOptionalText(string value, int maximumLength, string field)
    {
        if (!string.IsNullOrEmpty(value)) ValidateText(value, maximumLength, field);
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

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:/@#=,+%\\-]{0,511}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeLocator();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:/@#,+%\\-]{0,159}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeRevision();

    [GeneratedRegex(
        "(?:(?<![A-Za-z0-9])(?:api[-_]?key|access[-_]?key(?:[-_]?id)?|client[-_]?secret|private[-_]?key|secret|token|password|passwd|credential|authorization)(?![A-Za-z0-9])\\s*(?:=~|!~|!=|=|:)\\s*\\S+|(?<![A-Za-z0-9])bearer\\s+\\S{8,}|(?<![A-Za-z0-9])[a-z][a-z0-9+.-]{1,15}://)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveContent();
}
