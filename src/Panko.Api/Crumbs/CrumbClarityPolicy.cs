using System.Text.Json.Nodes;
using Panko.Api.Crumbs;
using Panko.Api.Domain;

namespace Panko.Api.Crumbs;

/// <summary>
/// Decides whether bounded Crumb collection has enough structured support to stop widening.
/// The policy deliberately requires more than one generic high-signal crumb.
/// </summary>
internal static class CrumbClarityPolicy
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
        "service-health",
        "service-registration",
        "workload-failure"
    };

    public static CrumbClarityAssessment Evaluate(
        CaseContext context,
        IEnumerable<CrumbSourceResult> results,
        DateTimeOffset collectionEnd,
        int initialWindowMinutes)
    {
        var crumbs = results
            .SelectMany(result => result.Crumbs)
            .Where(crumb => !string.Equals(crumb.Source, "submitted", StringComparison.Ordinal))
            .GroupBy(crumb => $"{crumb.Source}\u001f{crumb.Id}", StringComparer.Ordinal)
            .Select(group => CrumbRankingPolicy.Rank(group, context.OpenedAt)[0])
            .ToList();
        if (crumbs.Count == 0)
        {
            return CrumbClarityAssessment.Inconclusive;
        }

        var recentStart = context.OpenedAt - TimeSpan.FromMinutes(initialWindowMinutes);
        var recentSignals = crumbs
            .Where(CrumbRankingPolicy.IsHighSignal)
            .Where(HasReliableTiming)
            .Where(crumb => MetricCrumb.Start(crumb) <= collectionEnd
                && MetricCrumb.End(crumb, collectionEnd) >= recentStart)
            .ToList();

        var explicitFailure = recentSignals
            .Where(IsExplicitFailure)
            .OrderBy(crumb => Math.Abs((MetricCrumb.Start(crumb) - context.OpenedAt).Ticks))
            .ThenBy(crumb => crumb.Source, StringComparer.Ordinal)
            .ThenBy(crumb => crumb.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        if (explicitFailure is not null)
        {
            return new CrumbClarityAssessment(
                true,
                CrumbClarityReason.ExplicitFailure,
                [explicitFailure.Id]);
        }

        var corroborated = CorroboratedPair(recentSignals, collectionEnd);
        if (corroborated is not null)
        {
            return new CrumbClarityAssessment(
                true,
                CrumbClarityReason.CorroboratedSignals,
                [corroborated.Value.First.Id, corroborated.Value.Second.Id]);
        }

        var recentFailures = recentSignals.Where(IsFailureSignal).ToList();
        var correlated = crumbs
            .Where(crumb => ChangeCategories.Contains(crumb.Category))
            .SelectMany(change => recentFailures
                .Where(failure => !string.Equals(change.Source, failure.Source, StringComparison.Ordinal))
                .Where(failure => change.OccurredAt <= MetricCrumb.Start(failure))
                .Select(failure => new
                {
                    Change = change,
                    Failure = failure,
                    Distance = MetricCrumb.Start(failure) - change.OccurredAt
                }))
            .OrderBy(pair => pair.Distance)
            .ThenBy(pair => pair.Change.Source, StringComparer.Ordinal)
            .ThenBy(pair => pair.Change.Id, StringComparer.Ordinal)
            .ThenBy(pair => pair.Failure.Source, StringComparer.Ordinal)
            .ThenBy(pair => pair.Failure.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        return correlated is null
            ? CrumbClarityAssessment.Inconclusive
            : new CrumbClarityAssessment(
                true,
                CrumbClarityReason.ChangePrecedesFailure,
                [correlated.Change.Id, correlated.Failure.Id]);
    }

    private static (Crumb First, Crumb Second)? CorroboratedPair(
        IReadOnlyList<Crumb> signals,
        DateTimeOffset collectionEnd)
    {
        var ordered = signals
            .OrderBy(MetricCrumb.Start)
            .ThenBy(crumb => crumb.Source, StringComparer.Ordinal)
            .ThenBy(crumb => crumb.Id, StringComparer.Ordinal)
            .ToList();
        for (var firstIndex = 0; firstIndex < ordered.Count; firstIndex++)
        {
            for (var secondIndex = firstIndex + 1; secondIndex < ordered.Count; secondIndex++)
            {
                var first = ordered[firstIndex];
                var second = ordered[secondIndex];
                if (string.Equals(first.Source, second.Source, StringComparison.Ordinal)) continue;
                var distance = IntervalDistance(first, second, collectionEnd);
                if (distance > CorroborationTolerance) continue;
                return (first, second);
            }
        }
        return null;
    }

    private static TimeSpan IntervalDistance(
        Crumb first,
        Crumb second,
        DateTimeOffset collectionEnd)
    {
        var firstStart = MetricCrumb.Start(first);
        var firstEnd = MetricCrumb.End(first, collectionEnd);
        var secondStart = MetricCrumb.Start(second);
        var secondEnd = MetricCrumb.End(second, collectionEnd);
        if (firstEnd < secondStart) return secondStart - firstEnd;
        if (secondEnd < firstStart) return firstStart - secondEnd;
        return TimeSpan.Zero;
    }

    private static bool HasReliableTiming(Crumb crumb)
    {
        if (crumb.Category != "metric" && !CrumbRankingPolicy.IsKafkaCrumb(crumb))
        {
            return true;
        }
        return MetricCrumb.HasReliableTimestamp(crumb);
    }

    private static bool IsFailureSignal(Crumb crumb) =>
        FailureCategories.Contains(crumb.Category)
        || CrumbRankingPolicy.IsKafkaAnomaly(crumb);

    private static bool IsExplicitFailure(Crumb crumb)
    {
        if (CrumbRankingPolicy.IsKafkaAnomaly(crumb)
            && ScopeValue(crumb, "thresholdState") == "critical")
        {
            return true;
        }
        if (crumb.Category is "error" or "exception" && crumb.Severity == "critical")
        {
            return true;
        }
        if (crumb.Category == "pipeline-job-output")
        {
            return ScopeBoolean(crumb, "firstHardFailure")
                && !ScopeBoolean(crumb, "allowFailure");
        }
        if (crumb.Category is "pipeline" or "deployment")
        {
            return ScopeValue(crumb, "status") is "failed" or "failure";
        }
        if (crumb.Source == CrumbSourceRegistry.Consul
            && crumb.Category is "service-registration" or "service-health")
        {
            return ScopeValue(crumb, "status") is "unregistered" or "critical";
        }
        return false;
    }

    private static string? ScopeValue(Crumb crumb, string name)
    {
        if (crumb.Provenance["scope"] is not JsonObject scope) return null;
        var property = scope.FirstOrDefault(item =>
            string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase));
        return property.Value is JsonValue value && value.TryGetValue<string>(out var text)
            ? text.ToLowerInvariant()
            : null;
    }

    private static bool ScopeBoolean(Crumb crumb, string name)
    {
        if (crumb.Provenance["scope"] is not JsonObject scope) return false;
        var property = scope.FirstOrDefault(item =>
            string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase));
        return property.Value is JsonValue value
            && (value.TryGetValue<bool>(out var boolean) && boolean
                || value.TryGetValue<string>(out var text)
                && bool.TryParse(text, out var parsed)
                && parsed);
    }
}
