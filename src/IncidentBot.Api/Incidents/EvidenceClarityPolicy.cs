using System.Text.Json.Nodes;
using IncidentBot.Api.Domain;

namespace IncidentBot.Api.Incidents;

/// <summary>
/// Decides whether bounded evidence collection has enough structured support to stop widening.
/// The policy deliberately requires more than one generic high-signal finding.
/// </summary>
internal static class EvidenceClarityPolicy
{
    private static readonly TimeSpan CorroborationTolerance = TimeSpan.FromMinutes(10);

    private static readonly HashSet<string> ChangeCategories = new(StringComparer.Ordinal)
    {
        "code-change",
        "code-diff",
        "merge-request-merged",
        "deployment"
    };

    private static readonly HashSet<string> FailureCategories = new(StringComparer.Ordinal)
    {
        "error",
        "exception",
        "first-error",
        "log-count",
        "log-sample",
        "metric",
        "pipeline",
        "pipeline-job-output",
        "workload-failure"
    };

    public static EvidenceClarityAssessment Evaluate(
        InvestigationContext context,
        IEnumerable<ConnectorResult> results,
        DateTimeOffset collectionEnd,
        int initialWindowMinutes)
    {
        var findings = results
            .SelectMany(result => result.Findings)
            .GroupBy(finding => $"{finding.Source}\u001f{finding.Id}", StringComparer.Ordinal)
            .Select(group => EvidenceRankingPolicy.Rank(group, context.TriggeredAt)[0])
            .ToList();
        if (findings.Count == 0)
        {
            return EvidenceClarityAssessment.Inconclusive;
        }

        var recentStart = context.TriggeredAt - TimeSpan.FromMinutes(initialWindowMinutes);
        var recentSignals = findings
            .Where(EvidenceRankingPolicy.IsHighSignal)
            .Where(finding => finding.OccurredAt >= recentStart && finding.OccurredAt <= collectionEnd)
            .ToList();

        var explicitFailure = recentSignals
            .Where(IsExplicitFailure)
            .OrderBy(finding => Math.Abs((finding.OccurredAt - context.TriggeredAt).Ticks))
            .ThenBy(finding => finding.Source, StringComparer.Ordinal)
            .ThenBy(finding => finding.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        if (explicitFailure is not null)
        {
            return new EvidenceClarityAssessment(
                true,
                EvidenceClarityReason.ExplicitFailure,
                [explicitFailure.Id]);
        }

        var corroborated = CorroboratedPair(recentSignals);
        if (corroborated is not null)
        {
            return new EvidenceClarityAssessment(
                true,
                EvidenceClarityReason.CorroboratedSignals,
                [corroborated.Value.First.Id, corroborated.Value.Second.Id]);
        }

        var recentFailures = recentSignals.Where(IsFailureSignal).ToList();
        var correlated = findings
            .Where(finding => ChangeCategories.Contains(finding.Category))
            .SelectMany(change => recentFailures
                .Where(failure => !string.Equals(change.Source, failure.Source, StringComparison.Ordinal))
                .Where(failure => change.OccurredAt <= failure.OccurredAt)
                .Select(failure => new
                {
                    Change = change,
                    Failure = failure,
                    Distance = failure.OccurredAt - change.OccurredAt
                }))
            .OrderBy(pair => pair.Distance)
            .ThenBy(pair => pair.Change.Source, StringComparer.Ordinal)
            .ThenBy(pair => pair.Change.Id, StringComparer.Ordinal)
            .ThenBy(pair => pair.Failure.Source, StringComparer.Ordinal)
            .ThenBy(pair => pair.Failure.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        return correlated is null
            ? EvidenceClarityAssessment.Inconclusive
            : new EvidenceClarityAssessment(
                true,
                EvidenceClarityReason.ChangePrecedesFailure,
                [correlated.Change.Id, correlated.Failure.Id]);
    }

    private static (EvidenceFinding First, EvidenceFinding Second)? CorroboratedPair(
        IReadOnlyList<EvidenceFinding> signals)
    {
        var ordered = signals
            .OrderBy(finding => finding.OccurredAt)
            .ThenBy(finding => finding.Source, StringComparer.Ordinal)
            .ThenBy(finding => finding.Id, StringComparer.Ordinal)
            .ToList();
        for (var firstIndex = 0; firstIndex < ordered.Count; firstIndex++)
        {
            for (var secondIndex = firstIndex + 1; secondIndex < ordered.Count; secondIndex++)
            {
                var first = ordered[firstIndex];
                var second = ordered[secondIndex];
                if (second.OccurredAt - first.OccurredAt > CorroborationTolerance) break;
                if (string.Equals(first.Source, second.Source, StringComparison.Ordinal)) continue;
                return (first, second);
            }
        }
        return null;
    }

    private static bool IsFailureSignal(EvidenceFinding finding) =>
        FailureCategories.Contains(finding.Category);

    private static bool IsExplicitFailure(EvidenceFinding finding)
    {
        if (finding.Category is "error" or "exception" && finding.Severity == "critical")
        {
            return true;
        }
        if (finding.Category == "pipeline-job-output")
        {
            return ScopeBoolean(finding, "firstHardFailure")
                && !ScopeBoolean(finding, "allowFailure");
        }
        if (finding.Category is "pipeline" or "deployment")
        {
            return ScopeValue(finding, "status") is "failed" or "failure";
        }
        return false;
    }

    private static string? ScopeValue(EvidenceFinding finding, string name)
    {
        if (finding.Provenance["scope"] is not JsonObject scope) return null;
        var property = scope.FirstOrDefault(item =>
            string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase));
        return property.Value is JsonValue value && value.TryGetValue<string>(out var text)
            ? text.ToLowerInvariant()
            : null;
    }

    private static bool ScopeBoolean(EvidenceFinding finding, string name)
    {
        if (finding.Provenance["scope"] is not JsonObject scope) return false;
        var property = scope.FirstOrDefault(item =>
            string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase));
        return property.Value is JsonValue value
            && (value.TryGetValue<bool>(out var boolean) && boolean
                || value.TryGetValue<string>(out var text)
                && bool.TryParse(text, out var parsed)
                && parsed);
    }
}
