using System.Globalization;
using System.Text.Json.Nodes;
using Panko.Api.Domain;

namespace Panko.Api.Crumbs;

/// <summary>
/// Owns responder-facing Crumb priority. Severity describes the observed event;
/// this policy separately accounts for relevance, confidence, time, grouping, and source diversity.
/// </summary>
public static class CrumbRankingPolicy
{
    private const int PresentationDiversityWindow = 25;
    private const string KafkaSource = "kafka";
    private const string KafkaCategoryPrefix = "kafka-";

    public static IReadOnlyList<Crumb> OrderForCaseFile(
        IEnumerable<Crumb> crumbs,
        DateTimeOffset caseOpenedAt,
        int maximumItems = 250)
    {
        var ranked = Rank(crumbs, caseOpenedAt).ToList();
        var head = SelectDiverse(
            ranked,
            caseOpenedAt,
            Math.Min(PresentationDiversityWindow, ranked.Count),
            maximumPerGroup: 3,
            maximumPerSource: 5);
        var selected = head.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        return head.Concat(ranked.Where(item => !selected.Contains(item.Id))).Take(maximumItems).ToList();
    }

    public static IReadOnlyList<Crumb> OrderForSynthesis(
        IEnumerable<Crumb> crumbs,
        DateTimeOffset caseOpenedAt)
    {
        var ranked = Rank(crumbs, caseOpenedAt);
        var sources = ranked
            .GroupBy(item => item.Source, StringComparer.Ordinal)
            .Select(group => group.ToList())
            .OrderByDescending(group => Score(group[0], caseOpenedAt))
            .ThenBy(group => group[0].Source, StringComparer.Ordinal)
            .ToList();
        var output = new List<Crumb>(ranked.Count);
        var selected = new HashSet<string>(StringComparer.Ordinal);

        // Two fair rounds give every operational source useful budget before a noisy source expands.
        for (var round = 0; round < 2; round++)
        {
            foreach (var source in sources)
            {
                if (round >= source.Count) continue;
                var crumb = source[round];
                if (selected.Add(crumb.Id)) output.Add(crumb);
            }
        }

        output.AddRange(ranked.Where(item => selected.Add(item.Id)));
        return output;
    }

    public static IReadOnlyList<Crumb> SelectTopCrumbs(
        IEnumerable<Crumb> crumbs,
        DateTimeOffset caseOpenedAt,
        int maximumItems) =>
        SelectDiverse(
            crumbs.Where(IsHighSignal),
            caseOpenedAt,
            maximumItems,
            maximumPerGroup: 1,
            maximumPerSource: 2);

    public static IReadOnlyList<Crumb> SelectDiverse(
        IEnumerable<Crumb> crumbs,
        DateTimeOffset caseOpenedAt,
        int maximumItems,
        int maximumPerGroup,
        int maximumPerSource)
    {
        if (maximumItems <= 0) return [];
        var ranked = Rank(crumbs, caseOpenedAt);
        var output = new List<Crumb>(Math.Min(maximumItems, ranked.Count));
        var selected = new HashSet<string>(StringComparer.Ordinal);
        var groupCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var sourceCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        AddPass(enforceGroupLimit: true, enforceSourceLimit: true);
        AddPass(enforceGroupLimit: true, enforceSourceLimit: false);
        AddPass(enforceGroupLimit: false, enforceSourceLimit: false);
        return output;

        void AddPass(bool enforceGroupLimit, bool enforceSourceLimit)
        {
            foreach (var crumb in ranked)
            {
                if (output.Count >= maximumItems) return;
                if (selected.Contains(crumb.Id)) continue;
                var group = GroupKey(crumb);
                var groupCount = groupCounts.GetValueOrDefault(group);
                var sourceCount = sourceCounts.GetValueOrDefault(crumb.Source);
                if (enforceGroupLimit && groupCount >= maximumPerGroup) continue;
                if (enforceSourceLimit && sourceCount >= maximumPerSource) continue;
                output.Add(crumb);
                selected.Add(crumb.Id);
                groupCounts[group] = groupCount + 1;
                sourceCounts[crumb.Source] = sourceCount + 1;
            }
        }
    }

    public static IReadOnlyList<Crumb> Rank(
        IEnumerable<Crumb> crumbs,
        DateTimeOffset caseOpenedAt) => crumbs
        .OrderByDescending(item => Score(item, caseOpenedAt))
        .ThenBy(item => Math.Abs((item.OccurredAt - caseOpenedAt).TotalSeconds))
        .ThenBy(item => item.Source, StringComparer.Ordinal)
        .ThenBy(item => item.Id, StringComparer.Ordinal)
        .ThenBy(StableIdentity, StringComparer.Ordinal)
        .ToList();

    public static int Score(Crumb crumb, DateTimeOffset caseOpenedAt)
    {
        var distanceMinutes = Math.Abs((crumb.OccurredAt - caseOpenedAt).TotalMinutes);
        var proximity = Math.Max(0, 999 - (int)Math.Min(999, distanceMinutes * 10));
        return SignalTier(crumb) * 1_000_000
            - (string.Equals(crumb.Source, "submitted", StringComparison.Ordinal) ? 3_000_000 : 0)
            + SeverityRank(crumb.Severity) * 10_000
            + (int)Math.Round(Math.Clamp(crumb.Confidence, 0, 1) * 1_000)
            + (ScopeBoolean(crumb, "firstHardFailure") ? 2_000 : 0)
            + proximity;
    }

    public static bool IsHighSignal(Crumb crumb) =>
        crumb.Category != "pagerduty-incident" && SignalTier(crumb) >= 6;

    public static string GroupKey(Crumb crumb)
    {
        if (IsKafkaCrumb(crumb)
            && !string.IsNullOrWhiteSpace(crumb.ObjectType)
            && !string.IsNullOrWhiteSpace(crumb.ObjectId))
        {
            return $"{crumb.Source}|{crumb.ObjectType}|{crumb.ObjectId}";
        }
        if (crumb.Category.StartsWith("pipeline", StringComparison.Ordinal))
        {
            var pipelineId = ScopeValue(crumb, "pipelineId")
                ?? (crumb.ObjectType == "pipeline" ? crumb.ObjectId : null)
                ?? ScopeValue(crumb, "pipeline")
                ?? crumb.ObjectId
                ?? crumb.Id;
            var project = ScopeValue(crumb, "project") ?? "project";
            return $"{crumb.Source}|pipeline|{project}|{pipelineId}";
        }
        if (crumb.Category is "first-error" or "log-sample" or "log-count")
        {
            return $"{crumb.Source}|log|{crumb.ObjectId ?? ScopeValue(crumb, "Name") ?? crumb.Category}";
        }
        if (crumb.Category == "workload-failure")
        {
            var job = ScopeValue(crumb, "job");
            var jobNamespace = ScopeValue(crumb, "namespace");
            if (!string.IsNullOrWhiteSpace(job))
            {
                return $"{crumb.Source}|workload|{jobNamespace}|{job}";
            }
        }
        if (!string.IsNullOrWhiteSpace(crumb.ObjectType) && !string.IsNullOrWhiteSpace(crumb.ObjectId))
        {
            return $"{crumb.Source}|{crumb.ObjectType}|{crumb.ObjectId}";
        }
        return $"{crumb.Source}|{crumb.Category}";
    }

    private static int SignalTier(Crumb crumb)
    {
        var category = crumb.Category;
        if (category == "pagerduty-incident") return 1;
        if (IsKafkaCrumb(crumb)) return KafkaSignalTier(crumb);
        if (category == "pipeline-job-output")
        {
            if (crumb.ObjectType == "pipeline-job-cancellations"
                || string.Equals(ScopeValue(crumb, "status"), "canceled", StringComparison.OrdinalIgnoreCase)) return 4;
            if (ScopeBoolean(crumb, "allowFailure")) return 5;
            return crumb.Severity == "critical" ? 10 : 6;
        }
        if (category.Contains("pipeline-job", StringComparison.Ordinal)
            && category.Contains("cancel", StringComparison.Ordinal)) return 5;
        if (category is "first-error" or "exception" or "error") return 10;
        if (category == "workload-failure") return 9;
        if (category == "service-registration")
        {
            if (string.Equals(ScopeValue(crumb, "status"), "unregistered", StringComparison.OrdinalIgnoreCase)) return 11;
            if (SeverityRank(crumb.Severity) == 3) return 10;
            if (SeverityRank(crumb.Severity) == 2) return 8;
            return 2;
        }
        if (category == "service-health") return SeverityRank(crumb.Severity) == 3 ? 9 : 7;
        if (category == "pipeline"
            && string.Equals(ScopeValue(crumb, "status"), "failed", StringComparison.OrdinalIgnoreCase)) return 9;
        if (category == "pipeline"
            && string.Equals(ScopeValue(crumb, "status"), "canceled", StringComparison.OrdinalIgnoreCase)) return 4;
        if (category == "metric" && SeverityRank(crumb.Severity) >= 2) return 8;
        if (category is "log-sample" or "log-count") return SeverityRank(crumb.Severity) >= 2 ? 7 : 3;
        if (category == "deployment" && SeverityRank(crumb.Severity) >= 2) return 8;
        if (category is "merge-request-created" or "merge-request-merged" or "deployment" or "code-diff" or "code-change") return 5;
        if (SeverityRank(crumb.Severity) == 3) return 9;
        if (SeverityRank(crumb.Severity) == 2) return 7;
        return 2;
    }

    internal static bool IsKafkaCrumb(Crumb crumb) =>
        string.Equals(crumb.Source, KafkaSource, StringComparison.Ordinal)
        && crumb.Category.StartsWith(KafkaCategoryPrefix, StringComparison.Ordinal);

    internal static bool IsKafkaAnomaly(Crumb crumb)
    {
        if (!IsKafkaCrumb(crumb)
            || !string.Equals(ScopeValue(crumb, "crumbMode"), "anomaly", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return ScopeValue(crumb, "thresholdState") is "warning" or "critical";
    }

    internal static bool IsKafkaContext(Crumb crumb) =>
        IsKafkaCrumb(crumb)
        && string.Equals(ScopeValue(crumb, "crumbMode"), "context", StringComparison.OrdinalIgnoreCase);

    private static int KafkaSignalTier(Crumb crumb)
    {
        if (!IsKafkaAnomaly(crumb)) return 2;

        var thresholdState = ScopeValue(crumb, "thresholdState");
        var hardFailure = IsHardKafkaFailureCategory(crumb.Category);
        if (hardFailure && thresholdState == "critical") return 11;
        if (thresholdState == "critical") return 10;
        return hardFailure ? 9 : 8;
    }

    private static bool IsHardKafkaFailureCategory(string category) =>
        category.Contains("availability", StringComparison.Ordinal)
        || category.Contains("offline", StringComparison.Ordinal)
        || category.Contains("under-replic", StringComparison.Ordinal)
        || category.Contains("replication", StringComparison.Ordinal)
        || category.Contains("leader", StringComparison.Ordinal)
        || category.Contains("election", StringComparison.Ordinal)
        || category.Contains("isr", StringComparison.Ordinal);

    private static int SeverityRank(string severity) => severity switch
    {
        "critical" => 3,
        "warning" => 2,
        "info" => 1,
        _ => 0
    };

    private static string? ScopeValue(Crumb crumb, string name)
    {
        if (crumb.Provenance["scope"] is not JsonObject scope) return null;
        var property = scope.FirstOrDefault(item => string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase));
        return property.Value switch
        {
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            JsonValue value when value.TryGetValue<long>(out var number) => number.ToString(),
            _ => property.Value?.ToJsonString()
        };
    }

    private static bool ScopeBoolean(Crumb crumb, string name)
    {
        if (crumb.Provenance["scope"] is not JsonObject scope) return false;
        var property = scope.FirstOrDefault(item => string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase));
        return property.Value is JsonValue value
            && (value.TryGetValue<bool>(out var boolean) && boolean
                || value.TryGetValue<string>(out var text) && bool.TryParse(text, out var parsed) && parsed);
    }

    private static string StableIdentity(Crumb crumb) => string.Join("\u001f",
        crumb.OccurredAt.UtcTicks.ToString(CultureInfo.InvariantCulture),
        crumb.EndedAt?.UtcTicks.ToString(CultureInfo.InvariantCulture),
        crumb.Category,
        crumb.Severity,
        crumb.Summary,
        crumb.Excerpt,
        crumb.Url,
        crumb.Confidence.ToString("R", CultureInfo.InvariantCulture),
        crumb.Actor,
        crumb.ObjectType,
        crumb.ObjectId,
        crumb.Provenance.ToJsonString());
}
