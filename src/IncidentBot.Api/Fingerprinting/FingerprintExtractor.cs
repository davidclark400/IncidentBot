using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using IncidentBot.Api.Domain;

namespace IncidentBot.Api.Fingerprinting;

public sealed partial class FingerprintExtractor(FingerprintNormalizer normalizer)
{
    private const int MaximumItems = 32;
    private static readonly string[] ScopeKeys = ["environment", "env", "region", "namespace", "cluster"];
    private static readonly string[] ComponentKeys = ["component", "workload", "dependency", "job"];
    private static readonly HashSet<string> ChangeCategories = new(StringComparer.Ordinal)
    {
        "merge-request-created", "merge-request-merged", "deployment", "pipeline", "pipeline-job-output", "code-diff", "change"
    };
    private static readonly HashSet<string> ErrorCategories = new(StringComparer.Ordinal)
    {
        "first-error", "log-sample", "exception", "error", "workload-failure", "incident"
    };
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "at", "for", "from", "in", "is", "of", "on", "the", "to", "with",
        "<count>", "<duration>", "<id>", "<timestamp>"
    };

    public FingerprintFeatures Extract(
        IncidentRecord incident,
        IReadOnlyList<EvidenceFinding> evidence)
    {
        var title = normalizer.Normalize(incident.Title);
        var scopes = ValuesFromLabels(incident.Labels, ScopeKeys)
            .Concat(KafkaClusterScopes(evidence));
        var components = ValuesFromLabels(incident.Labels, ComponentKeys)
            .Append(FingerprintNormalizer.SafeIdentifier(incident.ServiceId));
        var symptomCategories = evidence
            .Where(finding => !IsChangeCategory(finding.Category) && !IsKafkaContext(finding))
            .Select(finding => FingerprintNormalizer.SafeIdentifier(finding.Category));
        var errors = evidence
            .Where(finding => !IsChangeCategory(finding.Category) && !IsKafkaContext(finding)
                && (ErrorCategories.Contains(finding.Category)
                    || IsKafkaAnomaly(finding)
                    || ErrorWord().IsMatch(finding.Summary)))
            .SelectMany(finding => new[] { RemoveActor(finding.Summary, finding.Actor), RemoveActor(finding.Excerpt, finding.Actor) })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => normalizer.Normalize(value));
        var evidenceComponents = evidence
            .Where(finding => finding.ObjectType is not null
                && !string.IsNullOrWhiteSpace(finding.ObjectId)
                && IsStableComponentType(finding.ObjectType))
            .Select(finding => IsKafkaFinding(finding)
                ? $"{FingerprintNormalizer.SafeIdentifier(finding.ObjectType)}:{FingerprintNormalizer.SafeIdentifier(finding.ObjectId)}"
                : normalizer.Normalize(finding.ObjectId))
            .Where(value => value.Length > 0 && !value.Contains("<id>", StringComparison.Ordinal));
        var locations = evidence
            .Where(finding => !IsChangeCategory(finding.Category))
            .SelectMany(finding => finding.CodeReferences ?? [])
            .Select(reference => normalizer.NormalizeCodeLocation(reference.ProjectId, reference.Path));
        var members = evidence
            .Where(finding => !IsChangeCategory(finding.Category))
            .SelectMany(finding => MemberIdentity().Matches(finding.Summary).Select(match => match.Value))
            .Select(member => $"member:{FingerprintNormalizer.SafeIdentifier(member)}");

        return new FingerprintFeatures(
            FingerprintNormalizer.SafeIdentifier(incident.ServiceId),
            FingerprintNormalizer.SafeIdentifier(incident.ProfileId),
            Bound(scopes),
            title,
            Bound(Tokenize(title)),
            Bound(symptomCategories),
            Bound(errors),
            Bound(components.Concat(evidenceComponents)),
            Bound(locations.Concat(members)));
    }

    private static IEnumerable<string> ValuesFromLabels(IReadOnlyDictionary<string, string> labels, IEnumerable<string> keys) =>
        keys.SelectMany(key => labels
                .Where(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                .Select(pair => $"{CanonicalScopeKey(key)}:{FingerprintNormalizer.SafeIdentifier(pair.Value)}"));

    private static string CanonicalScopeKey(string key) => key switch
    {
        "env" => "environment",
        _ => key
    };

    private static IEnumerable<string> Tokenize(string title) => Word().Matches(title)
        .Select(match => match.Value)
        .Where(token => !StopWords.Contains(token) && token.Length > 1);

    private static IReadOnlyList<string> Bound(IEnumerable<string> values) => values
        .Where(value => !string.IsNullOrWhiteSpace(value) && !value.Contains("<redacted>", StringComparison.Ordinal))
        .Select(value => value.Length <= FingerprintNormalizer.MaximumFeatureLength
            ? value
            : value[..FingerprintNormalizer.MaximumFeatureLength])
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .Take(MaximumItems)
        .ToArray();

    private static IEnumerable<string> KafkaClusterScopes(IEnumerable<EvidenceFinding> evidence) => evidence
        .Where(IsKafkaFinding)
        .Select(finding => ScopeValue(finding, "cluster"))
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => $"cluster:{FingerprintNormalizer.SafeIdentifier(value)}");

    private static bool IsStableComponentType(string objectType) =>
        objectType.Contains("component", StringComparison.OrdinalIgnoreCase)
        || objectType.Contains("dependency", StringComparison.OrdinalIgnoreCase)
        || objectType.Contains("workload", StringComparison.OrdinalIgnoreCase)
        || objectType.Contains("job", StringComparison.OrdinalIgnoreCase)
        || objectType.StartsWith("kafka-", StringComparison.Ordinal);

    private static bool IsKafkaFinding(EvidenceFinding finding) =>
        string.Equals(finding.Source, "kafka", StringComparison.Ordinal)
        && finding.Category.StartsWith("kafka-", StringComparison.Ordinal);

    private static bool IsKafkaContext(EvidenceFinding finding) =>
        IsKafkaFinding(finding)
        && string.Equals(ScopeValue(finding, "evidenceMode"), "context", StringComparison.OrdinalIgnoreCase);

    private static bool IsKafkaAnomaly(EvidenceFinding finding) =>
        IsKafkaFinding(finding)
        && string.Equals(ScopeValue(finding, "evidenceMode"), "anomaly", StringComparison.OrdinalIgnoreCase)
        && ScopeValue(finding, "thresholdState") is "warning" or "critical";

    private static string? ScopeValue(EvidenceFinding finding, string name)
    {
        if (finding.Provenance["scope"] is not JsonObject scope) return null;
        var property = scope.FirstOrDefault(item =>
            string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase));
        return property.Value is JsonValue value && value.TryGetValue<string>(out var text)
            ? text.ToLowerInvariant()
            : null;
    }

    private static bool IsChangeCategory(string category) =>
        ChangeCategories.Contains(category)
        || category.StartsWith("pipeline-job", StringComparison.Ordinal);

    private static string? RemoveActor(string? value, string? actor) =>
        string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(actor)
            ? value
            : value.Replace(actor, "<actor>", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\b(error|exception|failed|failure|timeout|timed out|unavailable|refused|panic)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ErrorWord();
    [GeneratedRegex(@"[a-z0-9_.<>/-]+", RegexOptions.CultureInvariant)]
    private static partial Regex Word();
    [GeneratedRegex(@"\b[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+\b", RegexOptions.CultureInvariant)]
    private static partial Regex MemberIdentity();
}
