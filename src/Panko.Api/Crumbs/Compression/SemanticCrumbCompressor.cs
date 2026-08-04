using System.Text.Json.Nodes;
using Panko.Api.Crumbs;
using Panko.Api.Domain;
using Panko.Api.Patterns;
using Panko.Api.Signatures;

namespace Panko.Api.Crumbs.Compression;

/// <summary>
/// Controls the bounded representatives retained when adaptive compression is required.
/// </summary>
internal sealed record CrumbCompressionOptions
{
    public int MaximumRepresentativesPerGroup { get; init; } = 3;
    public int MaximumCodeReferencesPerGroup { get; init; } = 8;
}

internal sealed record OwnedCodeReference(string CrumbId, CodeReference Reference);

/// <summary>
/// A loss-auditable semantic group. MemberCrumbIds records every source crumb represented by
/// the group; Representatives are the bounded crumbs serialized for synthesis.
/// </summary>
internal sealed record SemanticCrumbGroup(
    string Source,
    string Category,
    string Strategy,
    string SemanticKey,
    int OccurrenceCount,
    DateTimeOffset FirstOccurredAt,
    DateTimeOffset LastOccurredAt,
    string Severity,
    double Confidence,
    string Summary,
    IReadOnlyList<Crumb> Representatives,
    IReadOnlyList<string> MemberCrumbIds,
    IReadOnlyList<OwnedCodeReference> CodeReferences)
{
    public bool IsCompressed => OccurrenceCount > 1;
}

internal sealed record CrumbCompressionResult(
    IReadOnlyList<SemanticCrumbGroup> Groups,
    int InputCrumbCount,
    int DuplicateCrumbCount,
    int SemanticallyCollapsedCrumbCount)
{
    public int OutputGroupCount => Groups.Count;
    public int SuppressedCrumbCount => DuplicateCrumbCount + SemanticallyCollapsedCrumbCount;
}

/// <summary>
/// Deterministically removes duplicate IDs and then collapses only source-specific semantic
/// equivalence classes. It never mutates the original CrumbSourceResult or Crumb objects.
/// </summary>
internal sealed class SemanticCrumbCompressor
{
    private const char Separator = '\u001f';
    private readonly CrumbCompressionOptions options;
    private readonly SignatureNormalizer normalizer = new();

    public SemanticCrumbCompressor(CrumbCompressionOptions? options = null)
    {
        this.options = options ?? new CrumbCompressionOptions();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(this.options.MaximumRepresentativesPerGroup);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(this.options.MaximumCodeReferencesPerGroup);
    }

    public CrumbCompressionResult Compress(
        IEnumerable<CrumbSourceResult> results,
        DateTimeOffset caseOpenedAt,
        bool collapseSemantically = true)
    {
        ArgumentNullException.ThrowIfNull(results);

        var input = results.SelectMany(result => result.Crumbs).ToList();
        var canonical = input
            .GroupBy(crumb => Join(crumb.Source, crumb.Id), StringComparer.Ordinal)
            .Select(CanonicalizeDuplicate)
            .ToList();

        var groups = canonical
            .Select(crumb => new ClassifiedCrumb(
                crumb,
                collapseSemantically ? Classify(crumb) : Preserve(crumb)))
            .GroupBy(item => item.Identity)
            .Select(group => BuildGroup(group.Key, group.Select(item => item.Crumb).ToList(), caseOpenedAt))
            .OrderByDescending(group => CrumbRankingPolicy.Score(group.Representatives[0], caseOpenedAt))
            .ThenBy(group => group.Source, StringComparer.Ordinal)
            .ThenBy(group => group.FirstOccurredAt)
            .ThenBy(group => group.SemanticKey, StringComparer.Ordinal)
            .ToList();

        return new CrumbCompressionResult(
            groups,
            input.Count,
            input.Count - canonical.Count,
            canonical.Count - groups.Count);
    }

    private SemanticCrumbGroup BuildGroup(
        CompressionIdentity identity,
        IReadOnlyList<Crumb> members,
        DateTimeOffset caseOpenedAt)
    {
        var chronological = members
            .OrderBy(crumb => crumb.OccurredAt)
            .ThenBy(crumb => crumb.Id, StringComparer.Ordinal)
            .ThenBy(StableIdentity, StringComparer.Ordinal)
            .ToList();
        var ranked = CrumbRankingPolicy.Rank(members, caseOpenedAt);
        var representatives = SelectRepresentatives(ranked, chronological);
        var strongest = ranked[0];
        var first = chronological[0].OccurredAt;
        var last = chronological[^1].EndedAt ?? chronological[^1].OccurredAt;
        var memberIds = chronological
            .Select(crumb => crumb.Id)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var codeReferences = ranked
            .SelectMany(crumb => (crumb.CodeReferences ?? [])
                .Select(reference => new OwnedCodeReference(crumb.Id, reference)))
            .GroupBy(item => item.Reference.Id, StringComparer.Ordinal)
            .Select(group => group
                .OrderBy(item => item.CrumbId, StringComparer.Ordinal)
                .ThenBy(item => item.Reference.Path, StringComparer.Ordinal)
                .ThenBy(item => item.Reference.StartLine)
                .First())
            .OrderBy(item => item.Reference.Path, StringComparer.Ordinal)
            .ThenBy(item => item.Reference.StartLine)
            .ThenBy(item => item.Reference.Id, StringComparer.Ordinal)
            .Take(options.MaximumCodeReferencesPerGroup)
            .ToList();

        return new SemanticCrumbGroup(
            identity.Source,
            identity.Category,
            identity.Strategy,
            identity.Key,
            chronological.Count,
            first,
            last,
            members.OrderByDescending(crumb => SeverityRank(crumb.Severity)).First().Severity,
            members.Max(crumb => Math.Clamp(crumb.Confidence, 0, 1)),
            Describe(identity, chronological, strongest),
            representatives,
            memberIds,
            codeReferences);
    }

    private IReadOnlyList<Crumb> SelectRepresentatives(
        IReadOnlyList<Crumb> ranked,
        IReadOnlyList<Crumb> chronological)
    {
        var output = new List<Crumb>(options.MaximumRepresentativesPerGroup);
        var selected = new HashSet<string>(StringComparer.Ordinal);

        Add(ranked[0]);
        Add(chronological[0]);
        Add(chronological[^1]);
        foreach (var crumb in ranked) Add(crumb);
        return output;

        void Add(Crumb crumb)
        {
            if (output.Count >= options.MaximumRepresentativesPerGroup || !selected.Add(crumb.Id)) return;
            output.Add(crumb);
        }
    }

    private CompressionIdentity Classify(Crumb crumb)
    {
        if ((crumb.CodeReferences?.Count ?? 0) > 0) return Preserve(crumb);
        var identity = crumb.Source switch
        {
            CrumbSourceRegistry.VictoriaLogs => ClassifyVictoriaLogs(crumb),
            CrumbSourceRegistry.Nomad => ClassifyNomad(crumb),
            _ => Preserve(crumb)
        };
        return identity with { Source = crumb.Source, Category = crumb.Category };
    }

    private CompressionIdentity ClassifyVictoriaLogs(Crumb crumb)
    {
        var query = ScopeValue(crumb, "name") ?? crumb.ObjectId ?? "query";
        return crumb.Category switch
        {
            "log-sample" => Aggregate(
                "victorialogs.log-template",
                query,
                Normalized(crumb.Summary)),
            "log-count" => Aggregate(
                "victorialogs.query-count",
                query,
                Normalized(crumb.Summary)),
            // The first observed error is a causal/time anchor and must remain independently citable.
            _ => Preserve(crumb)
        };
    }

    private CompressionIdentity ClassifyNomad(Crumb crumb)
    {
        if (crumb.Category != "workload-failure" || crumb.ObjectType != "nomad-allocation")
        {
            return Preserve(crumb);
        }
        return Aggregate(
            "nomad.failure-template",
            ScopeValue(crumb, "namespace") ?? "namespace",
            ScopeValue(crumb, "job") ?? "job",
            crumb.ObjectType ?? "object",
            Normalized(crumb.Summary));
    }

    private string Describe(
        CompressionIdentity identity,
        IReadOnlyList<Crumb> chronological,
        Crumb strongest)
    {
        if (chronological.Count == 1) return strongest.Summary;

        var range = $"{chronological[0].OccurredAt:O} to {(chronological[^1].EndedAt ?? chronological[^1].OccurredAt):O}";
        var example = Truncate(strongest.Summary, 420);
        var noun = identity.Strategy switch
        {
            "victorialogs.log-template" => "similar log events",
            "victorialogs.query-count" => "equivalent log-count snapshots",
            "nomad.failure-template" => "similar Nomad failures",
            _ => "equivalent Crumbs"
        };
        return $"{chronological.Count} {noun} from {range}. Representative: {example}";
    }

    private CompressionIdentity Preserve(Crumb crumb) =>
        new(crumb.Source, crumb.Category, "preserve", Join("id", crumb.Id));

    private static CompressionIdentity Aggregate(string strategy, params string[] parts) =>
        new("", "", strategy, Join(parts));

    private string Normalized(string? value)
    {
        var normalized = normalizer.Normalize(value);
        return string.IsNullOrEmpty(normalized) ? "empty" : normalized;
    }

    private static Crumb CanonicalizeDuplicate(IGrouping<string, Crumb> group)
    {
        var ordered = group
            .OrderByDescending(crumb => SeverityRank(crumb.Severity))
            .ThenByDescending(crumb => Math.Clamp(crumb.Confidence, 0, 1))
            .ThenByDescending(crumb => crumb.CodeReferences?.Count ?? 0)
            .ThenByDescending(crumb => crumb.Excerpt?.Length ?? 0)
            .ThenBy(StableIdentity, StringComparer.Ordinal)
            .ToList();
        var selected = ordered[0];
        var excerpt = ordered
            .Where(crumb => !string.IsNullOrWhiteSpace(crumb.Excerpt))
            .OrderByDescending(crumb => crumb.Excerpt!.Length)
            .ThenBy(crumb => crumb.Excerpt, StringComparer.Ordinal)
            .Select(crumb => crumb.Excerpt)
            .FirstOrDefault();
        var references = ordered
            .SelectMany(crumb => crumb.CodeReferences ?? [])
            .GroupBy(reference => reference.Id, StringComparer.Ordinal)
            .Select(reference => reference
                .OrderBy(item => item.Path, StringComparer.Ordinal)
                .ThenBy(item => item.StartLine)
                .First())
            .OrderBy(reference => reference.Path, StringComparer.Ordinal)
            .ThenBy(reference => reference.StartLine)
            .ThenBy(reference => reference.Id, StringComparer.Ordinal)
            .ToList();

        return selected with
        {
            OccurredAt = ordered.Min(crumb => crumb.OccurredAt),
            EndedAt = ordered.Select(crumb => crumb.EndedAt).Where(value => value.HasValue).Max(),
            Excerpt = excerpt,
            Confidence = ordered.Max(crumb => Math.Clamp(crumb.Confidence, 0, 1)),
            CodeReferences = references
        };
    }

    private static string? ScopeValue(Crumb crumb, string name)
    {
        if (crumb.Provenance["scope"] is not JsonObject scope) return null;
        var property = scope.FirstOrDefault(item => string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase));
        return property.Value switch
        {
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            JsonValue value when value.TryGetValue<long>(out var number) => number.ToString(),
            JsonValue value when value.TryGetValue<bool>(out var boolean) => boolean.ToString().ToLowerInvariant(),
            _ => property.Value?.ToJsonString()
        };
    }

    private static int SeverityRank(string severity) => severity switch
    {
        "critical" => 3,
        "warning" => 2,
        "info" => 1,
        _ => 0
    };

    private static string StableIdentity(Crumb crumb) => Join(
        crumb.OccurredAt.UtcTicks.ToString(),
        crumb.EndedAt?.UtcTicks.ToString() ?? "",
        crumb.Id,
        crumb.Category,
        crumb.Severity,
        crumb.Summary,
        crumb.Excerpt ?? "",
        crumb.Url ?? "",
        crumb.Confidence.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        crumb.Actor ?? "",
        crumb.ObjectType ?? "",
        crumb.ObjectId ?? "",
        crumb.Provenance.ToJsonString());

    private static string Join(params string[] parts) => string.Join(Separator, parts);

    private static string Truncate(string value, int maximumCharacters) =>
        value.Length <= maximumCharacters ? value : value[..(maximumCharacters - 1)] + "…";

    private sealed record ClassifiedCrumb(Crumb Crumb, CompressionIdentity Identity);
    private sealed record CompressionIdentity(
        string Source,
        string Category,
        string Strategy,
        string Key);
}
