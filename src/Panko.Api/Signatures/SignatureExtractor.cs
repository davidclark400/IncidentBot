using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Panko.Api.Domain;

namespace Panko.Api.Signatures;

public sealed partial class SignatureExtractor(SignatureNormalizer normalizer)
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
        "first-error", "log-sample", "exception", "error", "workload-failure", "pagerduty-incident"
    };
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "at", "for", "from", "in", "is", "of", "on", "the", "to", "with",
        "<count>", "<duration>", "<id>", "<timestamp>"
    };

    public SignatureFeatures Extract(
        CaseRecord caseRecord,
        IReadOnlyList<Crumb> crumbs)
    {
        // Agent-submitted observations remain useful in the Case File, but cannot mint authoritative
        // Pattern identity until a trusted Crumb source independently observes the same feature.
        var authoritativeCrumbs = crumbs
            .Where(crumb => !string.Equals(crumb.Source, "submitted", StringComparison.Ordinal))
            .ToList();
        var title = normalizer.Normalize(caseRecord.Title);
        var scopes = ValuesFromLabels(caseRecord.Labels, ScopeKeys)
            .Concat(KafkaClusterScopes(authoritativeCrumbs));
        var components = ValuesFromLabels(caseRecord.Labels, ComponentKeys)
            .Append(SignatureNormalizer.SafeIdentifier(caseRecord.ServiceId));
        var symptomCategories = authoritativeCrumbs
            .Where(crumb => !IsChangeCategory(crumb.Category) && !IsKafkaContext(crumb))
            .Select(crumb => SignatureNormalizer.SafeIdentifier(crumb.Category));
        var errors = authoritativeCrumbs
            .Where(crumb => !IsChangeCategory(crumb.Category) && !IsKafkaContext(crumb)
                && (ErrorCategories.Contains(crumb.Category)
                    || IsKafkaAnomaly(crumb)
                    || ErrorWord().IsMatch(crumb.Summary)))
            .SelectMany(crumb => new[] { RemoveActor(crumb.Summary, crumb.Actor), RemoveActor(crumb.Excerpt, crumb.Actor) })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => normalizer.Normalize(value));
        var crumbComponents = authoritativeCrumbs
            .Where(crumb => crumb.ObjectType is not null
                && !string.IsNullOrWhiteSpace(crumb.ObjectId)
                && IsStableComponentType(crumb.ObjectType))
            .Select(crumb => IsKafkaCrumb(crumb)
                ? $"{SignatureNormalizer.SafeIdentifier(crumb.ObjectType)}:{SignatureNormalizer.SafeIdentifier(crumb.ObjectId)}"
                : normalizer.Normalize(crumb.ObjectId))
            .Where(value => value.Length > 0 && !value.Contains("<id>", StringComparison.Ordinal));
        var locations = authoritativeCrumbs
            .Where(crumb => !IsChangeCategory(crumb.Category))
            .SelectMany(crumb => crumb.CodeReferences ?? [])
            .Select(reference => normalizer.NormalizeCodeLocation(reference.ProjectId, reference.Path));
        var members = authoritativeCrumbs
            .Where(crumb => !IsChangeCategory(crumb.Category))
            .SelectMany(crumb => MemberIdentity().Matches(crumb.Summary).Select(match => match.Value))
            .Select(member => $"member:{SignatureNormalizer.SafeIdentifier(member)}");

        return new SignatureFeatures(
            SignatureNormalizer.SafeIdentifier(caseRecord.ServiceId),
            SignatureNormalizer.SafeIdentifier(caseRecord.RecipeId),
            Bound(scopes),
            title,
            Bound(Tokenize(title)),
            Bound(symptomCategories),
            Bound(errors),
            Bound(components.Concat(crumbComponents)),
            Bound(locations.Concat(members)));
    }

    private static IEnumerable<string> ValuesFromLabels(IReadOnlyDictionary<string, string> labels, IEnumerable<string> keys) =>
        keys.SelectMany(key => labels
                .Where(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                .Select(pair => $"{CanonicalScopeKey(key)}:{SignatureNormalizer.SafeIdentifier(pair.Value)}"));

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
        .Select(value => value.Length <= SignatureNormalizer.MaximumFeatureLength
            ? value
            : value[..SignatureNormalizer.MaximumFeatureLength])
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .Take(MaximumItems)
        .ToArray();

    private static IEnumerable<string> KafkaClusterScopes(IEnumerable<Crumb> crumbs) => crumbs
        .Where(IsKafkaCrumb)
        .Select(crumb => ScopeValue(crumb, "cluster"))
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => $"cluster:{SignatureNormalizer.SafeIdentifier(value)}");

    private static bool IsStableComponentType(string objectType) =>
        objectType.Contains("component", StringComparison.OrdinalIgnoreCase)
        || objectType.Contains("dependency", StringComparison.OrdinalIgnoreCase)
        || objectType.Contains("workload", StringComparison.OrdinalIgnoreCase)
        || objectType.Contains("job", StringComparison.OrdinalIgnoreCase)
        || objectType.StartsWith("kafka-", StringComparison.Ordinal);

    private static bool IsKafkaCrumb(Crumb crumb) =>
        string.Equals(crumb.Source, "kafka", StringComparison.Ordinal)
        && crumb.Category.StartsWith("kafka-", StringComparison.Ordinal);

    private static bool IsKafkaContext(Crumb crumb) =>
        IsKafkaCrumb(crumb)
        && string.Equals(ScopeValue(crumb, "crumbMode"), "context", StringComparison.OrdinalIgnoreCase);

    private static bool IsKafkaAnomaly(Crumb crumb) =>
        IsKafkaCrumb(crumb)
        && string.Equals(ScopeValue(crumb, "crumbMode"), "anomaly", StringComparison.OrdinalIgnoreCase)
        && ScopeValue(crumb, "thresholdState") is "warning" or "critical";

    private static string? ScopeValue(Crumb crumb, string name)
    {
        if (crumb.Provenance["scope"] is not JsonObject scope) return null;
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
