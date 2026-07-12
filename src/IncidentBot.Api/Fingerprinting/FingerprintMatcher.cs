using IncidentBot.Api.Domain;
using IncidentBot.Api.Options;
using Microsoft.Extensions.Options;

namespace IncidentBot.Api.Fingerprinting;

public sealed record ProblemCandidate(
    Guid GroupId,
    string ProblemKey,
    IncidentFingerprint Fingerprint,
    ProblemLifecycleState LifecycleState,
    int OccurrenceCount,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen);

public sealed record CandidateScore(ProblemCandidate Candidate, string MatchType, int Score, IReadOnlyList<string> MatchedFeatures);

public sealed class FingerprintMatcher(IOptions<IncidentBotOptions> options)
{
    public IReadOnlyList<CandidateScore> Rank(IncidentFingerprint fingerprint, IEnumerable<ProblemCandidate> candidates) =>
        candidates
            .Where(candidate => string.Equals(candidate.Fingerprint.AlgorithmVersion, fingerprint.AlgorithmVersion, StringComparison.Ordinal))
            .Where(candidate => string.Equals(candidate.Fingerprint.Features.ServiceId, fingerprint.Features.ServiceId, StringComparison.Ordinal))
            .Where(candidate => string.Equals(candidate.Fingerprint.Features.ProfileId, fingerprint.Features.ProfileId, StringComparison.Ordinal))
            .Where(candidate => ScopeCompatible(fingerprint.Features.Scopes, candidate.Fingerprint.Features.Scopes))
            .Select(candidate => Score(fingerprint, candidate))
            .OrderByDescending(score => score.MatchType == "exact")
            .ThenByDescending(score => score.Score)
            .ThenByDescending(score => score.Candidate.LastSeen)
            .ThenBy(score => score.Candidate.GroupId)
            .Take(options.Value.FingerprintMaximumCandidates)
            .ToArray();

    public CandidateScore? Automatic(IncidentFingerprint fingerprint, IEnumerable<ProblemCandidate> candidates) =>
        Rank(fingerprint, candidates).FirstOrDefault(score => score.Score >= options.Value.FingerprintAutomaticThreshold);

    public IReadOnlyList<CandidateScore> Possible(IncidentFingerprint fingerprint, IEnumerable<ProblemCandidate> candidates) =>
        Rank(fingerprint, candidates)
            .Where(score => score.Score >= options.Value.FingerprintPossibleThreshold
                && score.Score < options.Value.FingerprintAutomaticThreshold)
            .Take(5)
            .ToArray();

    public IReadOnlyList<CandidateScore> Preview(IncidentFingerprint fingerprint, IEnumerable<ProblemCandidate> candidates) =>
        Rank(fingerprint, candidates)
            .Where(score => score.Score >= options.Value.FingerprintPossibleThreshold)
            .Take(5)
            .ToArray();

    private CandidateScore Score(IncidentFingerprint fingerprint, ProblemCandidate candidate)
    {
        if (string.Equals(candidate.Fingerprint.ExactHash, fingerprint.ExactHash, StringComparison.Ordinal))
        {
            return new CandidateScore(candidate, "exact", 100, Explain(fingerprint.Features, candidate.Fingerprint.Features));
        }

        var left = fingerprint.Features;
        var right = candidate.Fingerprint.Features;
        var adequateExactEvidence = (left.ErrorTemplates.Count + left.CodeLocations.Count) > 0
            && (right.ErrorTemplates.Count + right.CodeLocations.Count) > 0;
        var exactConflict = adequateExactEvidence
            && Overlap(left.ErrorTemplates, right.ErrorTemplates) == 0
            && Overlap(left.CodeLocations, right.CodeLocations) == 0;
        var score = Weighted(left, right);
        if (exactConflict && string.Equals(fingerprint.FamilyHash, candidate.Fingerprint.FamilyHash, StringComparison.Ordinal))
        {
            score = Math.Min(score, options.Value.FingerprintPossibleThreshold - 1);
        }
        var matchType = string.Equals(fingerprint.FamilyHash, candidate.Fingerprint.FamilyHash, StringComparison.Ordinal)
            ? "family"
            : "similarity";
        return new CandidateScore(candidate, matchType, score, Explain(left, right));
    }

    private int Weighted(FingerprintFeatures left, FingerprintFeatures right)
    {
        var configuredTotal = options.Value.FingerprintErrorTemplateWeight
            + options.Value.FingerprintCodeLocationWeight
            + options.Value.FingerprintComponentWeight
            + options.Value.FingerprintSymptomWeight
            + options.Value.FingerprintTitleWeight;
        if (configuredTotal == 0) return 0;
        var score = Similarity(left.ErrorTemplates, right.ErrorTemplates) * options.Value.FingerprintErrorTemplateWeight
            + Similarity(left.CodeLocations, right.CodeLocations) * options.Value.FingerprintCodeLocationWeight
            + Similarity(left.Components, right.Components) * options.Value.FingerprintComponentWeight
            + Similarity(left.SymptomCategories, right.SymptomCategories) * options.Value.FingerprintSymptomWeight
            + Similarity(left.TitleTokens, right.TitleTokens) * options.Value.FingerprintTitleWeight;
        return (int)Math.Round(score * 100 / configuredTotal, MidpointRounding.AwayFromZero);
    }

    internal static bool ScopeCompatible(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count == 0 || right.Count == 0) return true;
        var leftTyped = TypedScopes(left);
        var rightTyped = TypedScopes(right);
        if (leftTyped.Count == 0 || rightTyped.Count == 0)
        {
            return left.Intersect(right, StringComparer.Ordinal).Any();
        }
        foreach (var dimension in leftTyped.Keys.Intersect(rightTyped.Keys, StringComparer.Ordinal))
        {
            if (!leftTyped[dimension].Intersect(rightTyped[dimension], StringComparer.Ordinal).Any()) return false;
        }
        return true;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> TypedScopes(IEnumerable<string> scopes) => scopes
        .Select(value => (Value: value, Separator: value.IndexOf(':', StringComparison.Ordinal)))
        .Where(item => item.Separator > 0 && item.Separator < item.Value.Length - 1)
        .GroupBy(item => item.Value[..item.Separator], StringComparer.Ordinal)
        .ToDictionary(
            group => group.Key,
            group => (IReadOnlyList<string>)group.Select(item => item.Value[(item.Separator + 1)..]).Distinct(StringComparer.Ordinal).ToArray(),
            StringComparer.Ordinal);

    private static double Similarity(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count == 0 && right.Count == 0) return 0;
        var union = left.Union(right, StringComparer.Ordinal).Count();
        return union == 0 ? 0 : (double)Overlap(left, right) / union;
    }

    private static int Overlap(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
        left.Intersect(right, StringComparer.Ordinal).Count();

    private static IReadOnlyList<string> Explain(FingerprintFeatures left, FingerprintFeatures right)
    {
        var explanations = new List<string>();
        Add("error", left.ErrorTemplates, right.ErrorTemplates, explanations);
        Add("code location", left.CodeLocations, right.CodeLocations, explanations);
        Add("component", left.Components, right.Components, explanations);
        Add("symptom", left.SymptomCategories, right.SymptomCategories, explanations);
        Add("title", left.TitleTokens, right.TitleTokens, explanations);
        return explanations.Take(5).ToArray();
    }

    private static void Add(string label, IEnumerable<string> left, IEnumerable<string> right, ICollection<string> explanations)
    {
        foreach (var value in left.Intersect(right, StringComparer.Ordinal).Take(2))
        {
            explanations.Add($"{label}: {value}");
        }
    }
}
