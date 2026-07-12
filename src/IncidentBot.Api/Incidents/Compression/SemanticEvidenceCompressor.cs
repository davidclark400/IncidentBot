using System.Text.Json.Nodes;
using IncidentBot.Api.Connectors;
using IncidentBot.Api.Domain;
using IncidentBot.Api.Fingerprinting;

namespace IncidentBot.Api.Incidents.Compression;

/// <summary>
/// Controls the bounded representatives retained when adaptive compression is required.
/// </summary>
internal sealed record SemanticCompressionOptions
{
    public int MaximumRepresentativesPerGroup { get; init; } = 3;
    public int MaximumCodeReferencesPerGroup { get; init; } = 8;
}

internal sealed record OwnedCodeReference(string EvidenceId, CodeReference Reference);

/// <summary>
/// A loss-auditable semantic group. MemberEvidenceIds records every source finding represented by
/// the group; Representatives are the bounded findings serialized for synthesis.
/// </summary>
internal sealed record SemanticEvidenceGroup(
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
    IReadOnlyList<EvidenceFinding> Representatives,
    IReadOnlyList<string> MemberEvidenceIds,
    IReadOnlyList<OwnedCodeReference> CodeReferences)
{
    public bool IsCompressed => OccurrenceCount > 1;
}

internal sealed record SemanticCompressionResult(
    IReadOnlyList<SemanticEvidenceGroup> Groups,
    int InputFindingCount,
    int DuplicateFindingCount,
    int SemanticallyCollapsedFindingCount)
{
    public int OutputGroupCount => Groups.Count;
    public int SuppressedFindingCount => DuplicateFindingCount + SemanticallyCollapsedFindingCount;
}

/// <summary>
/// Deterministically removes duplicate IDs and then collapses only source-specific semantic
/// equivalence classes. It never mutates the original ConnectorResult or EvidenceFinding objects.
/// </summary>
internal sealed class SemanticEvidenceCompressor
{
    private const char Separator = '\u001f';
    private readonly SemanticCompressionOptions options;
    private readonly FingerprintNormalizer normalizer = new();

    public SemanticEvidenceCompressor(SemanticCompressionOptions? options = null)
    {
        this.options = options ?? new SemanticCompressionOptions();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(this.options.MaximumRepresentativesPerGroup);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(this.options.MaximumCodeReferencesPerGroup);
    }

    public SemanticCompressionResult Compress(
        IEnumerable<ConnectorResult> results,
        DateTimeOffset incidentTriggeredAt,
        bool collapseSemantically = true)
    {
        ArgumentNullException.ThrowIfNull(results);

        var input = results.SelectMany(result => result.Findings).ToList();
        var canonical = input
            .GroupBy(finding => Join(finding.Source, finding.Id), StringComparer.Ordinal)
            .Select(CanonicalizeDuplicate)
            .ToList();

        var groups = canonical
            .Select(finding => new ClassifiedFinding(
                finding,
                collapseSemantically ? Classify(finding) : Preserve(finding)))
            .GroupBy(item => item.Identity)
            .Select(group => BuildGroup(group.Key, group.Select(item => item.Finding).ToList(), incidentTriggeredAt))
            .OrderByDescending(group => EvidenceRankingPolicy.Score(group.Representatives[0], incidentTriggeredAt))
            .ThenBy(group => group.Source, StringComparer.Ordinal)
            .ThenBy(group => group.FirstOccurredAt)
            .ThenBy(group => group.SemanticKey, StringComparer.Ordinal)
            .ToList();

        return new SemanticCompressionResult(
            groups,
            input.Count,
            input.Count - canonical.Count,
            canonical.Count - groups.Count);
    }

    private SemanticEvidenceGroup BuildGroup(
        CompressionIdentity identity,
        IReadOnlyList<EvidenceFinding> members,
        DateTimeOffset incidentTriggeredAt)
    {
        var chronological = members
            .OrderBy(finding => finding.OccurredAt)
            .ThenBy(finding => finding.Id, StringComparer.Ordinal)
            .ThenBy(StableIdentity, StringComparer.Ordinal)
            .ToList();
        var ranked = EvidenceRankingPolicy.Rank(members, incidentTriggeredAt);
        var representatives = SelectRepresentatives(ranked, chronological);
        var strongest = ranked[0];
        var first = chronological[0].OccurredAt;
        var last = chronological[^1].EndedAt ?? chronological[^1].OccurredAt;
        var memberIds = chronological
            .Select(finding => finding.Id)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var codeReferences = ranked
            .SelectMany(finding => (finding.CodeReferences ?? [])
                .Select(reference => new OwnedCodeReference(finding.Id, reference)))
            .GroupBy(item => item.Reference.Id, StringComparer.Ordinal)
            .Select(group => group
                .OrderBy(item => item.EvidenceId, StringComparer.Ordinal)
                .ThenBy(item => item.Reference.Path, StringComparer.Ordinal)
                .ThenBy(item => item.Reference.StartLine)
                .First())
            .OrderBy(item => item.Reference.Path, StringComparer.Ordinal)
            .ThenBy(item => item.Reference.StartLine)
            .ThenBy(item => item.Reference.Id, StringComparer.Ordinal)
            .Take(options.MaximumCodeReferencesPerGroup)
            .ToList();

        return new SemanticEvidenceGroup(
            identity.Source,
            identity.Category,
            identity.Strategy,
            identity.Key,
            chronological.Count,
            first,
            last,
            members.OrderByDescending(finding => SeverityRank(finding.Severity)).First().Severity,
            members.Max(finding => Math.Clamp(finding.Confidence, 0, 1)),
            Describe(identity, chronological, strongest),
            representatives,
            memberIds,
            codeReferences);
    }

    private IReadOnlyList<EvidenceFinding> SelectRepresentatives(
        IReadOnlyList<EvidenceFinding> ranked,
        IReadOnlyList<EvidenceFinding> chronological)
    {
        var output = new List<EvidenceFinding>(options.MaximumRepresentativesPerGroup);
        var selected = new HashSet<string>(StringComparer.Ordinal);

        Add(ranked[0]);
        Add(chronological[0]);
        Add(chronological[^1]);
        foreach (var finding in ranked) Add(finding);
        return output;

        void Add(EvidenceFinding finding)
        {
            if (output.Count >= options.MaximumRepresentativesPerGroup || !selected.Add(finding.Id)) return;
            output.Add(finding);
        }
    }

    private CompressionIdentity Classify(EvidenceFinding finding)
    {
        if ((finding.CodeReferences?.Count ?? 0) > 0) return Preserve(finding);
        var identity = finding.Source switch
        {
            EvidenceSourceRegistry.VictoriaLogs => ClassifyVictoriaLogs(finding),
            EvidenceSourceRegistry.Nomad => ClassifyNomad(finding),
            _ => Preserve(finding)
        };
        return identity with { Source = finding.Source, Category = finding.Category };
    }

    private CompressionIdentity ClassifyVictoriaLogs(EvidenceFinding finding)
    {
        var query = ScopeValue(finding, "name") ?? finding.ObjectId ?? "query";
        return finding.Category switch
        {
            "log-sample" => Aggregate(
                "victorialogs.log-template",
                query,
                Normalized(finding.Summary)),
            "log-count" => Aggregate(
                "victorialogs.query-count",
                query,
                Normalized(finding.Summary)),
            // The first observed error is a causal/time anchor and must remain independently citable.
            _ => Preserve(finding)
        };
    }

    private CompressionIdentity ClassifyNomad(EvidenceFinding finding)
    {
        if (finding.Category != "workload-failure" || finding.ObjectType != "nomad-allocation")
        {
            return Preserve(finding);
        }
        return Aggregate(
            "nomad.failure-template",
            ScopeValue(finding, "namespace") ?? "namespace",
            ScopeValue(finding, "job") ?? "job",
            finding.ObjectType ?? "object",
            Normalized(finding.Summary));
    }

    private string Describe(
        CompressionIdentity identity,
        IReadOnlyList<EvidenceFinding> chronological,
        EvidenceFinding strongest)
    {
        if (chronological.Count == 1) return strongest.Summary;

        var range = $"{chronological[0].OccurredAt:O} to {(chronological[^1].EndedAt ?? chronological[^1].OccurredAt):O}";
        var example = Truncate(strongest.Summary, 420);
        var noun = identity.Strategy switch
        {
            "victorialogs.log-template" => "similar log events",
            "victorialogs.query-count" => "equivalent log-count snapshots",
            "nomad.failure-template" => "similar Nomad failures",
            _ => "equivalent evidence findings"
        };
        return $"{chronological.Count} {noun} from {range}. Representative: {example}";
    }

    private CompressionIdentity Preserve(EvidenceFinding finding) =>
        new(finding.Source, finding.Category, "preserve", Join("id", finding.Id));

    private static CompressionIdentity Aggregate(string strategy, params string[] parts) =>
        new("", "", strategy, Join(parts));

    private string Normalized(string? value)
    {
        var normalized = normalizer.Normalize(value);
        return string.IsNullOrEmpty(normalized) ? "empty" : normalized;
    }

    private static EvidenceFinding CanonicalizeDuplicate(IGrouping<string, EvidenceFinding> group)
    {
        var ordered = group
            .OrderByDescending(finding => SeverityRank(finding.Severity))
            .ThenByDescending(finding => Math.Clamp(finding.Confidence, 0, 1))
            .ThenByDescending(finding => finding.CodeReferences?.Count ?? 0)
            .ThenByDescending(finding => finding.Excerpt?.Length ?? 0)
            .ThenBy(StableIdentity, StringComparer.Ordinal)
            .ToList();
        var selected = ordered[0];
        var excerpt = ordered
            .Where(finding => !string.IsNullOrWhiteSpace(finding.Excerpt))
            .OrderByDescending(finding => finding.Excerpt!.Length)
            .ThenBy(finding => finding.Excerpt, StringComparer.Ordinal)
            .Select(finding => finding.Excerpt)
            .FirstOrDefault();
        var references = ordered
            .SelectMany(finding => finding.CodeReferences ?? [])
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
            OccurredAt = ordered.Min(finding => finding.OccurredAt),
            EndedAt = ordered.Select(finding => finding.EndedAt).Where(value => value.HasValue).Max(),
            Excerpt = excerpt,
            Confidence = ordered.Max(finding => Math.Clamp(finding.Confidence, 0, 1)),
            CodeReferences = references
        };
    }

    private static string? ScopeValue(EvidenceFinding finding, string name)
    {
        if (finding.Provenance["scope"] is not JsonObject scope) return null;
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

    private static string StableIdentity(EvidenceFinding finding) => Join(
        finding.OccurredAt.UtcTicks.ToString(),
        finding.EndedAt?.UtcTicks.ToString() ?? "",
        finding.Id,
        finding.Category,
        finding.Severity,
        finding.Summary,
        finding.Excerpt ?? "",
        finding.Url ?? "",
        finding.Confidence.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        finding.Actor ?? "",
        finding.ObjectType ?? "",
        finding.ObjectId ?? "",
        finding.Provenance.ToJsonString());

    private static string Join(params string[] parts) => string.Join(Separator, parts);

    private static string Truncate(string value, int maximumCharacters) =>
        value.Length <= maximumCharacters ? value : value[..(maximumCharacters - 1)] + "…";

    private sealed record ClassifiedFinding(EvidenceFinding Finding, CompressionIdentity Identity);
    private sealed record CompressionIdentity(
        string Source,
        string Category,
        string Strategy,
        string Key);
}
