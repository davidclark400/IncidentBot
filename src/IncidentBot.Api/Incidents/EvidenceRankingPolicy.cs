using System.Globalization;
using System.Text.Json.Nodes;
using IncidentBot.Api.Domain;

namespace IncidentBot.Api.Incidents;

/// <summary>
/// Owns responder-facing evidence priority. Severity describes the observed event;
/// this policy separately accounts for relevance, confidence, time, grouping, and source diversity.
/// </summary>
public static class EvidenceRankingPolicy
{
    private const int PresentationDiversityWindow = 25;
    private const string KafkaSource = "kafka";
    private const string KafkaCategoryPrefix = "kafka-";

    public static IReadOnlyList<EvidenceFinding> OrderForReport(
        IEnumerable<EvidenceFinding> findings,
        DateTimeOffset incidentTriggeredAt,
        int maximumItems = 250)
    {
        var ranked = Rank(findings, incidentTriggeredAt).ToList();
        var head = SelectDiverse(
            ranked,
            incidentTriggeredAt,
            Math.Min(PresentationDiversityWindow, ranked.Count),
            maximumPerGroup: 3,
            maximumPerSource: 5);
        var selected = head.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        return head.Concat(ranked.Where(item => !selected.Contains(item.Id))).Take(maximumItems).ToList();
    }

    public static IReadOnlyList<EvidenceFinding> OrderForSynthesis(
        IEnumerable<EvidenceFinding> findings,
        DateTimeOffset incidentTriggeredAt)
    {
        var ranked = Rank(findings, incidentTriggeredAt);
        var sources = ranked
            .GroupBy(item => item.Source, StringComparer.Ordinal)
            .Select(group => group.ToList())
            .OrderByDescending(group => Score(group[0], incidentTriggeredAt))
            .ThenBy(group => group[0].Source, StringComparer.Ordinal)
            .ToList();
        var output = new List<EvidenceFinding>(ranked.Count);
        var selected = new HashSet<string>(StringComparer.Ordinal);

        // Two fair rounds give every operational source useful budget before a noisy source expands.
        for (var round = 0; round < 2; round++)
        {
            foreach (var source in sources)
            {
                if (round >= source.Count) continue;
                var finding = source[round];
                if (selected.Add(finding.Id)) output.Add(finding);
            }
        }

        output.AddRange(ranked.Where(item => selected.Add(item.Id)));
        return output;
    }

    public static IReadOnlyList<EvidenceFinding> SelectTopSignals(
        IEnumerable<EvidenceFinding> findings,
        DateTimeOffset incidentTriggeredAt,
        int maximumItems) =>
        SelectDiverse(
            findings.Where(IsHighSignal),
            incidentTriggeredAt,
            maximumItems,
            maximumPerGroup: 1,
            maximumPerSource: 2);

    public static IReadOnlyList<EvidenceFinding> SelectDiverse(
        IEnumerable<EvidenceFinding> findings,
        DateTimeOffset incidentTriggeredAt,
        int maximumItems,
        int maximumPerGroup,
        int maximumPerSource)
    {
        if (maximumItems <= 0) return [];
        var ranked = Rank(findings, incidentTriggeredAt);
        var output = new List<EvidenceFinding>(Math.Min(maximumItems, ranked.Count));
        var selected = new HashSet<string>(StringComparer.Ordinal);
        var groupCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var sourceCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        AddPass(enforceGroupLimit: true, enforceSourceLimit: true);
        AddPass(enforceGroupLimit: true, enforceSourceLimit: false);
        AddPass(enforceGroupLimit: false, enforceSourceLimit: false);
        return output;

        void AddPass(bool enforceGroupLimit, bool enforceSourceLimit)
        {
            foreach (var finding in ranked)
            {
                if (output.Count >= maximumItems) return;
                if (selected.Contains(finding.Id)) continue;
                var group = GroupKey(finding);
                var groupCount = groupCounts.GetValueOrDefault(group);
                var sourceCount = sourceCounts.GetValueOrDefault(finding.Source);
                if (enforceGroupLimit && groupCount >= maximumPerGroup) continue;
                if (enforceSourceLimit && sourceCount >= maximumPerSource) continue;
                output.Add(finding);
                selected.Add(finding.Id);
                groupCounts[group] = groupCount + 1;
                sourceCounts[finding.Source] = sourceCount + 1;
            }
        }
    }

    public static IReadOnlyList<EvidenceFinding> Rank(
        IEnumerable<EvidenceFinding> findings,
        DateTimeOffset incidentTriggeredAt) => findings
        .OrderByDescending(item => Score(item, incidentTriggeredAt))
        .ThenBy(item => Math.Abs((item.OccurredAt - incidentTriggeredAt).TotalSeconds))
        .ThenBy(item => item.Source, StringComparer.Ordinal)
        .ThenBy(item => item.Id, StringComparer.Ordinal)
        .ThenBy(StableIdentity, StringComparer.Ordinal)
        .ToList();

    public static int Score(EvidenceFinding finding, DateTimeOffset incidentTriggeredAt)
    {
        var distanceMinutes = Math.Abs((finding.OccurredAt - incidentTriggeredAt).TotalMinutes);
        var proximity = Math.Max(0, 999 - (int)Math.Min(999, distanceMinutes * 10));
        return SignalTier(finding) * 1_000_000
            + SeverityRank(finding.Severity) * 10_000
            + (int)Math.Round(Math.Clamp(finding.Confidence, 0, 1) * 1_000)
            + (ScopeBoolean(finding, "firstHardFailure") ? 2_000 : 0)
            + proximity;
    }

    public static bool IsHighSignal(EvidenceFinding finding) =>
        finding.Category != "incident" && SignalTier(finding) >= 6;

    public static string GroupKey(EvidenceFinding finding)
    {
        if (IsKafkaFinding(finding)
            && !string.IsNullOrWhiteSpace(finding.ObjectType)
            && !string.IsNullOrWhiteSpace(finding.ObjectId))
        {
            return $"{finding.Source}|{finding.ObjectType}|{finding.ObjectId}";
        }
        if (finding.Category.StartsWith("pipeline", StringComparison.Ordinal))
        {
            var pipelineId = ScopeValue(finding, "pipelineId")
                ?? (finding.ObjectType == "pipeline" ? finding.ObjectId : null)
                ?? ScopeValue(finding, "pipeline")
                ?? finding.ObjectId
                ?? finding.Id;
            var project = ScopeValue(finding, "project") ?? "project";
            return $"{finding.Source}|pipeline|{project}|{pipelineId}";
        }
        if (finding.Category is "first-error" or "log-sample" or "log-count")
        {
            return $"{finding.Source}|log|{finding.ObjectId ?? ScopeValue(finding, "Name") ?? finding.Category}";
        }
        if (finding.Category == "workload-failure")
        {
            var job = ScopeValue(finding, "job");
            var jobNamespace = ScopeValue(finding, "namespace");
            if (!string.IsNullOrWhiteSpace(job))
            {
                return $"{finding.Source}|workload|{jobNamespace}|{job}";
            }
        }
        if (!string.IsNullOrWhiteSpace(finding.ObjectType) && !string.IsNullOrWhiteSpace(finding.ObjectId))
        {
            return $"{finding.Source}|{finding.ObjectType}|{finding.ObjectId}";
        }
        return $"{finding.Source}|{finding.Category}";
    }

    private static int SignalTier(EvidenceFinding finding)
    {
        var category = finding.Category;
        if (category == "incident") return 1;
        if (IsKafkaFinding(finding)) return KafkaSignalTier(finding);
        if (category == "pipeline-job-output")
        {
            if (finding.ObjectType == "pipeline-job-cancellations"
                || string.Equals(ScopeValue(finding, "status"), "canceled", StringComparison.OrdinalIgnoreCase)) return 4;
            if (ScopeBoolean(finding, "allowFailure")) return 5;
            return finding.Severity == "critical" ? 10 : 6;
        }
        if (category.Contains("pipeline-job", StringComparison.Ordinal)
            && category.Contains("cancel", StringComparison.Ordinal)) return 5;
        if (category is "first-error" or "exception" or "error") return 10;
        if (category == "workload-failure") return 9;
        if (category == "pipeline"
            && string.Equals(ScopeValue(finding, "status"), "failed", StringComparison.OrdinalIgnoreCase)) return 9;
        if (category == "pipeline"
            && string.Equals(ScopeValue(finding, "status"), "canceled", StringComparison.OrdinalIgnoreCase)) return 4;
        if (category == "metric" && SeverityRank(finding.Severity) >= 2) return 8;
        if (category is "log-sample" or "log-count") return SeverityRank(finding.Severity) >= 2 ? 7 : 3;
        if (category == "deployment" && SeverityRank(finding.Severity) >= 2) return 8;
        if (category is "merge-request-created" or "merge-request-merged" or "deployment" or "code-diff" or "code-change") return 5;
        if (SeverityRank(finding.Severity) == 3) return 9;
        if (SeverityRank(finding.Severity) == 2) return 7;
        return 2;
    }

    internal static bool IsKafkaFinding(EvidenceFinding finding) =>
        string.Equals(finding.Source, KafkaSource, StringComparison.Ordinal)
        && finding.Category.StartsWith(KafkaCategoryPrefix, StringComparison.Ordinal);

    internal static bool IsKafkaAnomaly(EvidenceFinding finding)
    {
        if (!IsKafkaFinding(finding)
            || !string.Equals(ScopeValue(finding, "evidenceMode"), "anomaly", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return ScopeValue(finding, "thresholdState") is "warning" or "critical";
    }

    internal static bool IsKafkaContext(EvidenceFinding finding) =>
        IsKafkaFinding(finding)
        && string.Equals(ScopeValue(finding, "evidenceMode"), "context", StringComparison.OrdinalIgnoreCase);

    private static int KafkaSignalTier(EvidenceFinding finding)
    {
        if (!IsKafkaAnomaly(finding)) return 2;

        var thresholdState = ScopeValue(finding, "thresholdState");
        var hardFailure = IsHardKafkaFailureCategory(finding.Category);
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

    private static string? ScopeValue(EvidenceFinding finding, string name)
    {
        if (finding.Provenance["scope"] is not JsonObject scope) return null;
        var property = scope.FirstOrDefault(item => string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase));
        return property.Value switch
        {
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            JsonValue value when value.TryGetValue<long>(out var number) => number.ToString(),
            _ => property.Value?.ToJsonString()
        };
    }

    private static bool ScopeBoolean(EvidenceFinding finding, string name)
    {
        if (finding.Provenance["scope"] is not JsonObject scope) return false;
        var property = scope.FirstOrDefault(item => string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase));
        return property.Value is JsonValue value
            && (value.TryGetValue<bool>(out var boolean) && boolean
                || value.TryGetValue<string>(out var text) && bool.TryParse(text, out var parsed) && parsed);
    }

    private static string StableIdentity(EvidenceFinding finding) => string.Join("\u001f",
        finding.OccurredAt.UtcTicks.ToString(CultureInfo.InvariantCulture),
        finding.EndedAt?.UtcTicks.ToString(CultureInfo.InvariantCulture),
        finding.Category,
        finding.Severity,
        finding.Summary,
        finding.Excerpt,
        finding.Url,
        finding.Confidence.ToString("R", CultureInfo.InvariantCulture),
        finding.Actor,
        finding.ObjectType,
        finding.ObjectId,
        finding.Provenance.ToJsonString());
}
