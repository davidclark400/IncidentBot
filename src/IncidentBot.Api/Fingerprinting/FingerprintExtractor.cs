using System.Text.RegularExpressions;
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
        var scopes = ValuesFromLabels(incident.Labels, ScopeKeys);
        var components = ValuesFromLabels(incident.Labels, ComponentKeys)
            .Append(FingerprintNormalizer.SafeIdentifier(incident.ServiceId));
        var symptomCategories = evidence
            .Where(finding => !IsChangeCategory(finding.Category))
            .Select(finding => FingerprintNormalizer.SafeIdentifier(finding.Category));
        var errors = evidence
            .Where(finding => !IsChangeCategory(finding.Category)
                && (ErrorCategories.Contains(finding.Category) || ErrorWord().IsMatch(finding.Summary)))
            .SelectMany(finding => new[] { RemoveActor(finding.Summary, finding.Actor), RemoveActor(finding.Excerpt, finding.Actor) })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => normalizer.Normalize(value));
        var evidenceComponents = evidence
            .Where(finding => finding.ObjectType is not null && IsStableComponentType(finding.ObjectType))
            .Select(finding => normalizer.Normalize(finding.ObjectId))
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

    private static bool IsStableComponentType(string objectType) =>
        objectType.Contains("component", StringComparison.OrdinalIgnoreCase)
        || objectType.Contains("dependency", StringComparison.OrdinalIgnoreCase)
        || objectType.Contains("workload", StringComparison.OrdinalIgnoreCase)
        || objectType.Contains("job", StringComparison.OrdinalIgnoreCase);

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
