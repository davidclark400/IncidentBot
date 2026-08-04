using Panko.Api.Domain;
using Panko.Api.Signatures;
using Panko.Api.Options;
using Microsoft.Extensions.Options;

namespace Panko.Api.Patterns;

public sealed record PatternCandidate(
    Guid PatternId,
    string PatternKey,
    CaseSignature Signature,
    PatternLifecycleState LifecycleState,
    int OccurrenceCount,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen);

public sealed record PatternCandidateScore(
    PatternCandidate Candidate,
    string MatchType,
    int Score,
    IReadOnlyList<string> MatchedFeatures);

/// <summary>
/// Owns deterministic Pattern decisions. Persistence supplies locked candidates and statistics;
/// this policy decides association, matching presentation, cutoffs, and lifecycle.
/// </summary>
public sealed class PatternPolicy(IOptions<PankoOptions> options)
{
    private const int PatternKeyHashLength = 20;

    public int MaximumCandidates => options.Value.SignatureMaximumCandidates;

    public int EscalationCount => options.Value.SignatureEscalationCount;

    public DateTimeOffset CandidateCutoff(DateTimeOffset now) =>
        now - TimeSpan.FromDays(options.Value.SignatureCandidateLookbackDays);

    public DateTimeOffset EscalationCutoff(DateTimeOffset now) =>
        now - TimeSpan.FromDays(options.Value.SignatureEscalationWindowDays);

    public IReadOnlyList<PatternCandidateScore> RankPossible(
        CaseSignature signature,
        IEnumerable<PatternCandidate> candidates)
    {
        var ranked = Rank(signature, candidates)
            .Where(score => score.Score >= options.Value.SignaturePossibleThreshold);
        if (signature.Stage != SignatureStage.Provisional)
        {
            ranked = ranked.Where(score => score.Score < options.Value.SignatureAutomaticThreshold);
        }
        return ranked.Take(5).ToArray();
    }

    public PatternCandidateScore? SelectAssociation(
        CaseSignature signature,
        IReadOnlyList<PatternCandidate> candidates,
        Guid? existingPatternId)
    {
        var ranked = Rank(signature, candidates);
        return existingPatternId is { } assignedId
            ? ranked.FirstOrDefault(value => value.Candidate.PatternId == assignedId)
                ?? candidates.Where(candidate => candidate.PatternId == assignedId)
                    .Select(candidate => new PatternCandidateScore(
                        candidate,
                        "existing",
                        0,
                        ["existing Case assignment"]))
                    .FirstOrDefault()
            : ranked.FirstOrDefault(value => value.Score >= options.Value.SignatureAutomaticThreshold);
    }

    public static string PatternKey(CaseSignature signature)
    {
        var features = signature.Features;
        var service = KeyPart(features.ServiceId);
        var component = features.Components
            .FirstOrDefault(value => !string.Equals(value, features.ServiceId, StringComparison.Ordinal))
            ?? features.TitleTokens.FirstOrDefault()
            ?? "PATTERN";
        return $"{service}-{KeyPart(component)}-{signature.ExactHash[..PatternKeyHashLength].ToUpperInvariant()}";
    }

    public PatternLifecycleState ClassifyLifecycle(
        PatternLifecycleState? previous,
        PagerDutyIncidentState pagerDutyState,
        bool active,
        int occurrenceCount,
        int recentCount)
    {
        if (!active)
        {
            return PatternLifecycleState.Resolved;
        }
        if (previous == PatternLifecycleState.Resolved && pagerDutyState != PagerDutyIncidentState.Resolved)
        {
            return PatternLifecycleState.Regressed;
        }
        return ClassifyActive(occurrenceCount, recentCount);
    }

    public PatternLifecycleState ClassifyAfterRetention(
        bool active,
        int occurrenceCount,
        int recentCount)
    {
        if (!active)
        {
            return PatternLifecycleState.Resolved;
        }
        return ClassifyActive(occurrenceCount, recentCount);
    }

    private PatternLifecycleState ClassifyActive(int occurrenceCount, int recentCount)
    {
        if (recentCount >= EscalationCount)
        {
            return PatternLifecycleState.Escalating;
        }
        return occurrenceCount == 1 ? PatternLifecycleState.New : PatternLifecycleState.Ongoing;
    }

    private IReadOnlyList<PatternCandidateScore> Rank(
        CaseSignature signature,
        IEnumerable<PatternCandidate> candidates) =>
        candidates
            .Where(candidate => string.Equals(candidate.Signature.AlgorithmVersion, signature.AlgorithmVersion, StringComparison.Ordinal))
            .Where(candidate => string.Equals(candidate.Signature.Features.ServiceId, signature.Features.ServiceId, StringComparison.Ordinal))
            .Where(candidate => string.Equals(candidate.Signature.Features.RecipeId, signature.Features.RecipeId, StringComparison.Ordinal))
            .Where(candidate => ScopeCompatible(signature.Features.Scopes, candidate.Signature.Features.Scopes))
            .Select(candidate => Score(signature, candidate))
            .OrderByDescending(score => score.MatchType == "exact")
            .ThenByDescending(score => score.Score)
            .ThenByDescending(score => score.Candidate.LastSeen)
            .ThenBy(score => score.Candidate.PatternId)
            .Take(options.Value.SignatureMaximumCandidates)
            .ToArray();

    private PatternCandidateScore Score(CaseSignature signature, PatternCandidate candidate)
    {
        if (string.Equals(candidate.Signature.ExactHash, signature.ExactHash, StringComparison.Ordinal))
        {
            return new PatternCandidateScore(candidate, "exact", 100, Explain(signature.Features, candidate.Signature.Features));
        }

        var left = signature.Features;
        var right = candidate.Signature.Features;
        var adequateExactCrumbs = (left.ErrorTemplates.Count + left.CodeLocations.Count) > 0
            && (right.ErrorTemplates.Count + right.CodeLocations.Count) > 0;
        var exactConflict = adequateExactCrumbs
            && Overlap(left.ErrorTemplates, right.ErrorTemplates) == 0
            && Overlap(left.CodeLocations, right.CodeLocations) == 0;
        var score = Weighted(left, right);
        if (exactConflict && string.Equals(signature.FamilyHash, candidate.Signature.FamilyHash, StringComparison.Ordinal))
        {
            score = Math.Min(score, options.Value.SignaturePossibleThreshold - 1);
        }
        var matchType = string.Equals(signature.FamilyHash, candidate.Signature.FamilyHash, StringComparison.Ordinal)
            ? "family"
            : "similarity";
        return new PatternCandidateScore(candidate, matchType, score, Explain(left, right));
    }

    private int Weighted(SignatureFeatures left, SignatureFeatures right)
    {
        var configuredTotal = options.Value.SignatureErrorTemplateWeight
            + options.Value.SignatureCodeLocationWeight
            + options.Value.SignatureComponentWeight
            + options.Value.SignatureSymptomWeight
            + options.Value.SignatureTitleWeight;
        if (configuredTotal == 0) return 0;
        var score = Similarity(left.ErrorTemplates, right.ErrorTemplates) * options.Value.SignatureErrorTemplateWeight
            + Similarity(left.CodeLocations, right.CodeLocations) * options.Value.SignatureCodeLocationWeight
            + Similarity(left.Components, right.Components) * options.Value.SignatureComponentWeight
            + Similarity(left.SymptomCategories, right.SymptomCategories) * options.Value.SignatureSymptomWeight
            + Similarity(left.TitleTokens, right.TitleTokens) * options.Value.SignatureTitleWeight;
        return (int)Math.Round(score * 100 / configuredTotal, MidpointRounding.AwayFromZero);
    }

    private static bool ScopeCompatible(IReadOnlyList<string> left, IReadOnlyList<string> right)
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

    private static IReadOnlyList<string> Explain(SignatureFeatures left, SignatureFeatures right)
    {
        var explanations = new List<string>();
        Add("error", left.ErrorTemplates, right.ErrorTemplates, explanations);
        Add("code location", left.CodeLocations, right.CodeLocations, explanations);
        Add("component", left.Components, right.Components, explanations);
        Add("symptom", left.SymptomCategories, right.SymptomCategories, explanations);
        Add("title", left.TitleTokens, right.TitleTokens, explanations);
        return explanations.Take(5).ToArray();
    }

    private static void Add(
        string label,
        IEnumerable<string> left,
        IEnumerable<string> right,
        ICollection<string> explanations)
    {
        foreach (var value in left.Intersect(right, StringComparer.Ordinal).Take(2))
        {
            explanations.Add($"{label}: {value}");
        }
    }

    private static string KeyPart(string value)
    {
        var cleaned = new string(value.Where(char.IsLetterOrDigit).Take(12).ToArray()).ToUpperInvariant();
        return string.IsNullOrEmpty(cleaned) ? "PATTERN" : cleaned;
    }
}
