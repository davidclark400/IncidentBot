using IncidentBot.Api.Domain;
using IncidentBot.Api.Options;
using Microsoft.Extensions.Options;

namespace IncidentBot.Api.Fingerprinting;

/// <summary>
/// Owns deterministic recurrence decisions. Persistence supplies locked candidates and statistics;
/// this policy decides association, matching presentation, cutoffs, and lifecycle.
/// </summary>
public sealed class RecurrencePolicy(
    FingerprintMatcher matcher,
    IOptions<IncidentBotOptions> options)
{
    public int MaximumCandidates => options.Value.FingerprintMaximumCandidates;

    public int EscalationCount => options.Value.FingerprintEscalationCount;

    public DateTimeOffset CandidateCutoff(DateTimeOffset now) =>
        now - TimeSpan.FromDays(options.Value.FingerprintCandidateLookbackDays);

    public DateTimeOffset EscalationCutoff(DateTimeOffset now) =>
        now - TimeSpan.FromDays(options.Value.FingerprintEscalationWindowDays);

    public IReadOnlyList<CandidateScore> RankPossible(
        IncidentFingerprint fingerprint,
        IEnumerable<ProblemCandidate> candidates) =>
        fingerprint.Stage == FingerprintStage.Provisional
            ? matcher.Preview(fingerprint, candidates)
            : matcher.Possible(fingerprint, candidates);

    public CandidateScore? SelectAssociation(
        IncidentFingerprint fingerprint,
        IReadOnlyList<ProblemCandidate> candidates,
        Guid? existingGroupId)
    {
        var ranked = matcher.Rank(fingerprint, candidates);
        return existingGroupId is { } assignedId
            ? ranked.FirstOrDefault(value => value.Candidate.GroupId == assignedId)
                ?? candidates.Where(candidate => candidate.GroupId == assignedId)
                    .Select(candidate => new CandidateScore(
                        candidate,
                        "existing",
                        0,
                        ["existing incident assignment"]))
                    .FirstOrDefault()
            : ranked.FirstOrDefault(value => value.Score >= options.Value.FingerprintAutomaticThreshold);
    }

    public static string ProblemKey(IncidentFingerprint fingerprint) =>
        FingerprintGenerator.ProblemKey(fingerprint.Features, fingerprint.FamilyHash);

    public ProblemLifecycleState ClassifyLifecycle(
        ProblemLifecycleState? previous,
        IncidentState incidentState,
        bool active,
        int occurrenceCount,
        int recentCount)
    {
        if (!active)
        {
            return ProblemLifecycleState.Resolved;
        }
        if (previous == ProblemLifecycleState.Resolved && incidentState != IncidentState.Resolved)
        {
            return ProblemLifecycleState.Regressed;
        }
        if (recentCount >= EscalationCount)
        {
            return ProblemLifecycleState.Escalating;
        }
        return occurrenceCount == 1 ? ProblemLifecycleState.New : ProblemLifecycleState.Ongoing;
    }
}
