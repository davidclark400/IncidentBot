using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using IncidentBot.Api.Domain;
using IncidentBot.Api.Incidents;

namespace IncidentBot.Api.Connectors;

/// <summary>
/// Treats an MCP tool result as untrusted input before it crosses the connector boundary.
/// </summary>
internal static class McpConnectorResultBoundary
{
    private const int MaximumSummaryCharacters = 600;
    private const int MaximumDiagnosticCharacters = 600;
    private const int MaximumProvenanceBytes = 2048;
    private const int MaximumCodeReferencesPerFinding = 8;
    private const int RetainedPercentage = 90;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static ConnectorResult Normalize(
        string requestedSource,
        ConnectorResult result,
        EvidenceScope scope,
        DateTimeOffset incidentTriggeredAt,
        string? allowedBaseUrl,
        JsonNode? allowedResources,
        string? credential)
    {
        if (string.IsNullOrWhiteSpace(requestedSource)
            || !string.Equals(result.Source, requestedSource, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("MCP connector result source did not match the requested source.");
        }

        var maxItems = Math.Max(0, scope.MaxItems);
        var retainedByteLimit = RetainedByteLimit(scope.MaxBytes);
        var secrets = new McpSecretSanitizer(credential);
        var urls = McpAllowedUrlPolicy.Create(allowedBaseUrl, allowedResources, secrets);
        var resources = McpAllowedResourcePolicy.Create(requestedSource, allowedResources);
        var rawFindings = result.Findings ?? [];
        var boundaryMutated = rawFindings.Any(item => item is not null
                && FindingRequiresTruncation(item, excerptCharacters: null, secrets, urls, resources))
            || (result.Timeline ?? []).Any(item => item is not null
                && (secrets.Sanitize(item.Summary, MaximumSummaryCharacters) != item.Summary
                    || item.Url is not null
                    && (!resources.AllowsUrl(item.Url)
                        || !string.Equals(urls.Sanitize(item.Url), item.Url, StringComparison.Ordinal))))
            || (result.Links ?? []).Any(item => item is not null
                && (secrets.Sanitize(item.Label, 240) != item.Label
                    || !resources.AllowsUrl(item.Url)
                    || !string.Equals(urls.Sanitize(item.Url), item.Url, StringComparison.Ordinal)))
            || secrets.Sanitize(result.Diagnostic, MaximumDiagnosticCharacters) != result.Diagnostic;
        var eligibleFindingCount = Math.Min(
            maxItems,
            rawFindings
                .Where(item => item is not null
                    && SourceMatches(item.Source, requestedSource)
                    && IsWithinScope(item.OccurredAt, scope)
                    && resources.AllowsFinding(item))
                .Select(item => CanonicalFindingId(item, requestedSource))
                .Distinct(StringComparer.Ordinal)
                .Count());
        var excerptCharacters = FindingExcerptLimit(retainedByteLimit, eligibleFindingCount);
        boundaryMutated |= rawFindings.Any(item => item is not null
            && FindingRequiresTruncation(item, excerptCharacters, secrets, urls, resources));

        var deduplicatedFindings = rawFindings
            .Where(item => item is not null
                && SourceMatches(item.Source, requestedSource)
                && IsWithinScope(item.OccurredAt, scope)
                && resources.AllowsFinding(item))
            .Select(item => NormalizeFinding(
                item, requestedSource, excerptCharacters, secrets, urls, resources))
            .Where(item => !string.IsNullOrWhiteSpace(item.Summary))
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => EvidenceRankingPolicy.Rank(group, incidentTriggeredAt)[0])
            .ToList();
        CanonicalizeGitLabFailureOrder(requestedSource, deduplicatedFindings);
        var findings = EvidenceRankingPolicy.Rank(deduplicatedFindings, incidentTriggeredAt)
            .Take(maxItems)
            .ToList();

        var selectedTimeline = (result.Timeline ?? [])
            .Where(item => item is not null
                && SourceMatches(item.Source, requestedSource)
                && IsWithinScope(item.OccurredAt, scope)
                && resources.AllowsTimeline(item))
            .Select(item => NormalizeTimeline(item, requestedSource, secrets, urls, resources))
            .Where(item => !string.IsNullOrWhiteSpace(item.Summary))
            .GroupBy(TimelineIdentity, StringComparer.Ordinal)
            .Select(group => group.OrderBy(TimelineStableIdentity, StringComparer.Ordinal).First())
            .OrderByDescending(item => SeverityRank(item.Severity))
            .ThenByDescending(item => item.OccurredAt)
            .ThenBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Summary, StringComparer.Ordinal)
            .Take(maxItems)
            .OrderBy(item => item.OccurredAt)
            .ThenByDescending(item => SeverityRank(item.Severity))
            .ThenBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Summary, StringComparer.Ordinal)
            .ToList();

        var links = (result.Links ?? [])
            .Where(item => item is not null && resources.AllowsUrl(item.Url))
            .Select(item => NormalizeLink(item, secrets, urls))
            .Where(item => item is not null)
            .Select(item => item!)
            .GroupBy(item => item.Url, StringComparer.Ordinal)
            .Select(group => group.OrderBy(item => item.Label, StringComparer.Ordinal).First())
            .OrderBy(item => item.Label, StringComparer.Ordinal)
            .ThenBy(item => item.Url, StringComparer.Ordinal)
            .Take(maxItems)
            .ToList();

        var diagnostic = secrets.Sanitize(result.Diagnostic, MaximumDiagnosticCharacters);
        var prototype = new ConnectorResult(
            requestedSource,
            result.Health,
            [],
            [],
            [],
            Math.Max(0, result.DurationMilliseconds),
            diagnostic);

        var normalized = FitToRetainedBudget(
            prototype, findings, selectedTimeline, links, retainedByteLimit, incidentTriggeredAt);
        var reduced = boundaryMutated
            || normalized.Findings.Count < rawFindings.Count
            || normalized.Timeline.Count < (result.Timeline?.Count ?? 0)
            || normalized.Links.Count < (result.Links?.Count ?? 0);
        if (!reduced || normalized.Health != SourceHealth.Complete) return normalized;
        var partial = normalized with
        {
            Health = SourceHealth.Partial,
            Diagnostic = AppendDiagnostic(normalized.Diagnostic, "MCP boundary rejected, deduplicated, or truncated returned evidence.")
        };
        return EstimateRetainedBytes(partial) <= retainedByteLimit
            ? partial
            : partial with { Diagnostic = null };
    }

    internal static int RetainedByteLimit(int maxBytes)
    {
        if (maxBytes <= 0) return 0;
        return (int)Math.Min(int.MaxValue, (long)maxBytes * RetainedPercentage / 100);
    }

    internal static int EstimateRetainedBytes(ConnectorResult result) =>
        JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions).Length;

    private static EvidenceFinding NormalizeFinding(
        EvidenceFinding finding,
        string source,
        int excerptCharacters,
        McpSecretSanitizer secrets,
        McpAllowedUrlPolicy urls,
        McpAllowedResourcePolicy resources)
    {
        var category = (secrets.Sanitize(finding.Category, 80) ?? "evidence").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(category)) category = "evidence";
        var summary = secrets.Sanitize(finding.Summary, MaximumSummaryCharacters) ?? "";
        var id = CanonicalFindingId(finding, source);
        var codeReferences = (finding.CodeReferences ?? [])
            .Where(item => item is not null)
            .Where(resources.AllowsCodeReference)
            .Select(item => NormalizeCodeReference(item, source, id, secrets, urls))
            .Where(item => item is not null)
            .Select(item => item!)
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group
                .OrderBy(item => item.Path, StringComparer.Ordinal)
                .ThenBy(item => item.StartLine)
                .First())
            .OrderBy(item => item.Path, StringComparer.Ordinal)
            .ThenBy(item => item.StartLine)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .Take(MaximumCodeReferencesPerFinding)
            .ToList();

        return new EvidenceFinding(
            id,
            source,
            finding.OccurredAt,
            finding.EndedAt >= finding.OccurredAt ? finding.EndedAt : null,
            category,
            NormalizeSeverity(finding.Severity),
            summary,
            secrets.Sanitize(finding.Excerpt, excerptCharacters),
            resources.AllowsUrl(finding.Url) ? urls.Sanitize(finding.Url) : null,
            double.IsFinite(finding.Confidence) ? Math.Clamp(finding.Confidence, 0, 1) : 0,
            SanitizeProvenance(resources.CanonicalizeProvenance(finding), secrets, urls),
            secrets.Sanitize(finding.Actor, 240),
            secrets.Sanitize(finding.ObjectType, 120),
            secrets.Sanitize(finding.ObjectId, 240),
            codeReferences);
    }

    private static TimelineCandidate NormalizeTimeline(
        TimelineCandidate item,
        string source,
        McpSecretSanitizer secrets,
        McpAllowedUrlPolicy urls,
        McpAllowedResourcePolicy resources) =>
        new(
            item.OccurredAt,
            source,
            secrets.Sanitize(item.Kind, 80) ?? "evidence",
            secrets.Sanitize(item.Summary, MaximumSummaryCharacters) ?? "",
            NormalizeSeverity(item.Severity),
            resources.AllowsUrl(item.Url) ? urls.Sanitize(item.Url) : null,
            secrets.Sanitize(item.Actor, 240),
            secrets.Sanitize(item.ObjectType, 120),
            secrets.Sanitize(item.ObjectId, 240));

    private static SourceLink? NormalizeLink(
        SourceLink item,
        McpSecretSanitizer secrets,
        McpAllowedUrlPolicy urls)
    {
        var url = urls.Sanitize(item.Url);
        if (url is null) return null;
        var label = secrets.Sanitize(item.Label, 240);
        return string.IsNullOrWhiteSpace(label) ? null : new SourceLink(label, url);
    }

    private static CodeReference? NormalizeCodeReference(
        CodeReference item,
        string source,
        string findingId,
        McpSecretSanitizer secrets,
        McpAllowedUrlPolicy urls)
    {
        var url = urls.Sanitize(item.Url);
        if (url is null) return null;
        var path = secrets.Sanitize(item.Path, 500);
        if (string.IsNullOrWhiteSpace(path)) return null;
        var startLine = Math.Max(1, item.StartLine);
        var endLine = Math.Max(startLine, item.EndLine);
        var identity = string.IsNullOrWhiteSpace(item.Id)
            ? $"{path}|{startLine}|{endLine}"
            : item.Id;
        return new CodeReference(
            ConnectorUtilities.Id(source, "mcp-code", findingId, identity),
            secrets.Sanitize(item.ProjectId, 240) ?? "",
            secrets.Sanitize(item.CommitSha, 160) ?? "",
            path,
            startLine,
            endLine,
            url,
            secrets.Sanitize(item.Excerpt, 1600) ?? "");
    }

    private static JsonObject SanitizeProvenance(
        JsonObject? provenance,
        McpSecretSanitizer secrets,
        McpAllowedUrlPolicy urls)
    {
        var sanitized = secrets.SanitizeNode(provenance, urls) as JsonObject ?? new JsonObject();
        if (JsonSerializer.SerializeToUtf8Bytes(sanitized, JsonOptions).Length <= MaximumProvenanceBytes)
        {
            return sanitized;
        }

        var bounded = new JsonObject();
        var omitted = false;
        foreach (var property in sanitized.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            bounded[property.Key] = property.Value?.DeepClone();
            if (JsonSerializer.SerializeToUtf8Bytes(bounded, JsonOptions).Length <= MaximumProvenanceBytes)
            {
                continue;
            }

            bounded.Remove(property.Key);
            omitted = true;
        }

        if (omitted) bounded["_truncated"] = true;
        return bounded;
    }

    private static ConnectorResult FitToRetainedBudget(
        ConnectorResult prototype,
        IReadOnlyList<EvidenceFinding> candidates,
        IReadOnlyList<TimelineCandidate> timelineCandidates,
        IReadOnlyList<SourceLink> linkCandidates,
        int retainedByteLimit,
        DateTimeOffset incidentTriggeredAt)
    {
        var findings = new List<EvidenceFinding>();
        var timeline = new List<TimelineCandidate>();
        var links = new List<SourceLink>();
        var result = BuildResult(prototype, findings, timeline, links);

        if (retainedByteLimit <= 0) return result with { Diagnostic = null };
        if (EstimateRetainedBytes(result) > retainedByteLimit)
        {
            prototype = prototype with { Diagnostic = null };
            result = BuildResult(prototype, findings, timeline, links);
        }

        var primaryFindingLimit = Math.Max(EstimateRetainedBytes(result), retainedByteLimit * 75 / 100);
        var deferredFindings = new List<EvidenceFinding>();
        foreach (var finding in candidates)
        {
            findings.Add(finding);
            if (EstimateRetainedBytes(BuildResult(prototype, findings, timeline, links)) <= primaryFindingLimit)
            {
                continue;
            }

            findings.RemoveAt(findings.Count - 1);
            deferredFindings.Add(finding);
        }

        var timelineLimit = retainedByteLimit * 92 / 100;
        foreach (var item in timelineCandidates)
        {
            timeline.Add(item);
            if (EstimateRetainedBytes(BuildResult(prototype, findings, timeline, links)) > timelineLimit)
            {
                timeline.RemoveAt(timeline.Count - 1);
            }
        }

        foreach (var link in linkCandidates)
        {
            links.Add(link);
            if (EstimateRetainedBytes(BuildResult(prototype, findings, timeline, links)) > retainedByteLimit)
            {
                links.RemoveAt(links.Count - 1);
            }
        }

        foreach (var finding in deferredFindings)
        {
            findings.Add(finding);
            if (EstimateRetainedBytes(BuildResult(prototype, findings, timeline, links)) > retainedByteLimit)
            {
                findings.RemoveAt(findings.Count - 1);
            }
        }

        var rankedFindings = EvidenceRankingPolicy.Rank(findings, incidentTriggeredAt);
        return BuildResult(prototype, rankedFindings, timeline, links);
    }

    private static ConnectorResult BuildResult(
        ConnectorResult prototype,
        IReadOnlyList<EvidenceFinding> findings,
        IReadOnlyList<TimelineCandidate> timeline,
        IReadOnlyList<SourceLink> links) =>
        prototype with
        {
            Findings = findings.ToArray(),
            Timeline = timeline.ToArray(),
            Links = links.ToArray()
        };

    private static int FindingExcerptLimit(int retainedByteLimit, int findingCount)
    {
        if (retainedByteLimit <= 0 || findingCount <= 0) return 0;
        var fairShare = retainedByteLimit / findingCount;
        return Math.Clamp((fairShare - 900) / 2, 200, 6000);
    }

    private static string CanonicalFindingId(EvidenceFinding finding, string source)
    {
        var identity = string.IsNullOrWhiteSpace(finding.Id)
            ? $"{finding.OccurredAt.UtcTicks}|{finding.Category}|{finding.Summary}"
            : finding.Id;
        return ConnectorUtilities.Id(source, "mcp", identity);
    }

    private static bool SourceMatches(string? actual, string requested) =>
        string.Equals(actual, requested, StringComparison.OrdinalIgnoreCase);

    private static bool IsWithinScope(DateTimeOffset occurredAt, EvidenceScope scope) =>
        occurredAt >= scope.Start && occurredAt <= scope.End;

    private static void CanonicalizeGitLabFailureOrder(
        string source,
        IReadOnlyList<EvidenceFinding> findings)
    {
        if (!string.Equals(source, EvidenceSourceRegistry.GitLab, StringComparison.OrdinalIgnoreCase)) return;
        foreach (var group in findings
                     .Where(item => item.Category == "pipeline-job-output"
                         && item.ObjectType != "pipeline-job-cancellations")
                     .GroupBy(EvidenceRankingPolicy.GroupKey, StringComparer.Ordinal))
        {
            foreach (var item in group)
            {
                if (item.Provenance["scope"] is not JsonObject scope) continue;
                scope["failureOrdinal"] = 0;
                scope["firstHardFailure"] = false;
            }
            var hardFailures = group
                .Where(item => item.Provenance["scope"] is JsonObject scope
                    && string.Equals(ScopeText(scope, "status"), "failed", StringComparison.OrdinalIgnoreCase)
                    && ScopeBoolean(scope, "allowFailure") == false)
                .OrderBy(item => item.OccurredAt)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .ToList();
            for (var index = 0; index < hardFailures.Count; index++)
            {
                if (hardFailures[index].Provenance["scope"] is not JsonObject scope) continue;
                scope["failureOrdinal"] = index + 1;
                scope["firstHardFailure"] = index == 0;
            }
        }
    }

    private static string? ScopeText(JsonObject scope, string name)
    {
        var node = scope.FirstOrDefault(item => string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
        return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }

    private static bool? ScopeBoolean(JsonObject scope, string name)
    {
        var node = scope.FirstOrDefault(item => string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
        if (node is JsonValue value && value.TryGetValue<bool>(out var boolean)) return boolean;
        if (node is JsonValue textValue
            && textValue.TryGetValue<string>(out var text)
            && bool.TryParse(text, out var parsed)) return parsed;
        return null;
    }

    private static string NormalizeSeverity(string? severity) =>
        severity?.Trim().ToLowerInvariant() switch
        {
            "fatal" or "critical" or "error" => "critical",
            "warning" or "warn" => "warning",
            _ => "info"
        };

    private static int SeverityRank(string? severity) => severity switch
    {
        "critical" => 4,
        "warning" => 2,
        "info" => 1,
        _ => 0
    };

    private static string TimelineIdentity(TimelineCandidate item) =>
        $"{item.OccurredAt.UtcTicks}|{item.Kind}|{item.Summary}|{item.ObjectType}|{item.ObjectId}";

    private static string TimelineStableIdentity(TimelineCandidate item) =>
        $"{TimelineIdentity(item)}|{item.Severity}|{item.Url}|{item.Actor}";

    private static string AppendDiagnostic(string? current, string message) =>
        string.IsNullOrWhiteSpace(current)
            ? message
            : ConnectorUtilities.Truncate($"{current}; {message}", MaximumDiagnosticCharacters);

    private static bool FindingRequiresTruncation(
        EvidenceFinding finding,
        int? excerptCharacters,
        McpSecretSanitizer secrets,
        McpAllowedUrlPolicy urls,
        McpAllowedResourcePolicy resources) =>
        !string.Equals(
            (secrets.Sanitize(finding.Category, 80) ?? "evidence").Trim().ToLowerInvariant(),
            finding.Category,
            StringComparison.Ordinal)
        || secrets.Sanitize(finding.Summary, MaximumSummaryCharacters) != finding.Summary
        || excerptCharacters is not null
        && secrets.Sanitize(finding.Excerpt, excerptCharacters.Value) != finding.Excerpt
        || finding.Url is not null
        && (!resources.AllowsUrl(finding.Url)
            || !string.Equals(urls.Sanitize(finding.Url), finding.Url, StringComparison.Ordinal))
        || (finding.CodeReferences?.Count ?? 0) > MaximumCodeReferencesPerFinding
        || (finding.CodeReferences ?? []).Any(reference =>
            !resources.AllowsCodeReference(reference)
            || !string.Equals(urls.Sanitize(reference.Url), reference.Url, StringComparison.Ordinal)
            || secrets.Sanitize(reference.Excerpt, 1600) != reference.Excerpt)
        || JsonSerializer.SerializeToUtf8Bytes(finding.Provenance, JsonOptions).Length > MaximumProvenanceBytes;

}

internal sealed class McpSecretSanitizer
{
    private const string Redacted = "[REDACTED]";
    private const int MaximumNodeDepth = 8;
    private const int MaximumNodeMembers = 48;
    private const int MaximumNodeStringCharacters = 1000;

    private static readonly Regex AssignedSecret = new(
        @"\b(?<key>authorization|proxy[-_ ]?authorization|api[-_ ]?key|access[-_ ]?token|refresh[-_ ]?token|private[-_ ]?token|client[-_ ]?secret|password|passwd|token|secret|credential|cookie)\b\s*[:=]\s*(?:(?:bearer|basic|token)\s+)?(?:""[^""]*""|'[^']*'|[^\s,;&}\]]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));
    private static readonly Regex Jwt = new(
        @"\beyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private static readonly Regex KnownToken = new(
        @"\b(?:glpat|gldt|glrt)-[A-Za-z0-9_-]{8,}\b|\b(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9_]{8,}\b|\b(?:xoxb|xoxp|xoxa|xoxr)-[A-Za-z0-9_-]{8,}\b|\bAKIA[A-Z0-9]{16}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));
    private static readonly Regex AuthorizationValue = new(
        @"\b(?<scheme>bearer|basic)\s+[A-Za-z0-9._~+/=-]{8,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));
    private static readonly Regex PrivateKey = new(
        @"-----BEGIN(?: [A-Z0-9]+)? PRIVATE KEY-----.*?(?:-----END(?: [A-Z0-9]+)? PRIVATE KEY-----|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline,
        TimeSpan.FromMilliseconds(100));
    private static readonly Regex UriUserInfo = new(
        @"(?<scheme>https?://)[^\s/@:]+:[^\s/@]+@",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    private readonly string? credential;
    private readonly string? encodedCredential;

    public McpSecretSanitizer(string? credential)
    {
        this.credential = string.IsNullOrWhiteSpace(credential) || credential.Length < 4 ? null : credential;
        encodedCredential = this.credential is null ? null : Uri.EscapeDataString(this.credential);
    }

    public string? Sanitize(string? value, int maximumCharacters)
    {
        if (value is null) return null;
        if (maximumCharacters <= 0) return "";
        var scanLimit = (int)Math.Min(int.MaxValue, (long)maximumCharacters + 1024);
        if (value.Length > scanLimit) value = value[..scanLimit];
        value = RemoveControls(value);
        value = PrivateKey.Replace(value, Redacted);
        value = AssignedSecret.Replace(value, "${key}=[REDACTED]");
        value = AuthorizationValue.Replace(value, "${scheme} [REDACTED]");
        value = Jwt.Replace(value, Redacted);
        value = KnownToken.Replace(value, Redacted);
        value = UriUserInfo.Replace(value, "${scheme}[REDACTED]@");
        if (credential is not null)
        {
            value = value.Replace(credential, Redacted, StringComparison.Ordinal);
        }
        if (!string.IsNullOrEmpty(encodedCredential))
        {
            value = value.Replace(encodedCredential, Redacted, StringComparison.OrdinalIgnoreCase);
        }
        return value.Length <= maximumCharacters ? value : value[..maximumCharacters] + "…";
    }

    public JsonNode? SanitizeNode(JsonNode? node, McpAllowedUrlPolicy urls, string? propertyName = null, int depth = 0)
    {
        if (node is null) return null;
        if (depth >= MaximumNodeDepth) return JsonValue.Create("[TRUNCATED]");
        if (propertyName is not null && IsSensitiveName(propertyName)) return JsonValue.Create(Redacted);

        if (node is JsonObject sourceObject)
        {
            var target = new JsonObject();
            foreach (var property in sourceObject
                         .OrderBy(item => item.Key, StringComparer.Ordinal)
                         .Take(MaximumNodeMembers))
            {
                target[property.Key] = SanitizeNode(property.Value, urls, property.Key, depth + 1);
            }
            if (sourceObject.Count > MaximumNodeMembers) target["_truncated"] = true;
            return target;
        }

        if (node is JsonArray sourceArray)
        {
            var target = new JsonArray();
            foreach (var item in sourceArray.Take(MaximumNodeMembers))
            {
                target.Add(SanitizeNode(item, urls, propertyName, depth + 1));
            }
            if (sourceArray.Count > MaximumNodeMembers) target.Add("[TRUNCATED]");
            return target;
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            if (propertyName is not null && IsUrlName(propertyName))
            {
                return JsonValue.Create(urls.Sanitize(text) ?? "[REDACTED URL]");
            }
            return JsonValue.Create(Sanitize(text, MaximumNodeStringCharacters));
        }

        return node.DeepClone();
    }

    public bool ContainsSecret(string value)
    {
        if (credential is not null && value.Contains(credential, StringComparison.Ordinal)) return true;
        if (!string.IsNullOrEmpty(encodedCredential)
            && value.Contains(encodedCredential, StringComparison.OrdinalIgnoreCase)) return true;
        return AssignedSecret.IsMatch(value) || Jwt.IsMatch(value) || KnownToken.IsMatch(value) || PrivateKey.IsMatch(value);
    }

    internal static bool IsSensitiveName(string name)
    {
        var normalized = NormalizeName(name);
        return normalized is "authorization" or "proxyauthorization" or "apikey" or "accesstoken"
            or "refreshtoken" or "privatetoken" or "clientsecret" or "password" or "passwd"
            or "token" or "secret" or "credential" or "credentials" or "cookie" or "setcookie"
            or "signature" or "privatekey" or "signingkey" or "sessiontoken"
            || normalized.EndsWith("password", StringComparison.Ordinal)
            || normalized.EndsWith("token", StringComparison.Ordinal)
            || normalized.EndsWith("secret", StringComparison.Ordinal)
            || normalized.EndsWith("credential", StringComparison.Ordinal);
    }

    internal static bool IsUrlName(string name)
    {
        var normalized = NormalizeName(name);
        return normalized is "url" or "urls" or "uri" or "uris" or "href" or "link"
            || normalized.EndsWith("url", StringComparison.Ordinal)
            || normalized.EndsWith("uri", StringComparison.Ordinal);
    }

    private static string NormalizeName(string name) =>
        new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string RemoveControls(string value) =>
        new(value.Where(character => character is '\t' or '\n' or '\r' || !char.IsControl(character)).ToArray());
}

/// <summary>
/// Enforces the resource portion of the MCP request as an output allowlist. Host-level
/// URL checks alone cannot prevent a tool from returning a different project, job, or
/// incident from the same service.
/// </summary>
internal sealed class McpAllowedResourcePolicy
{
    private readonly string source;
    private readonly HashSet<string> gitLabProjects = new(StringComparer.Ordinal);
    private readonly HashSet<string> nomadJobs = new(StringComparer.Ordinal);
    private readonly HashSet<string> grafanaDashboards = new(StringComparer.Ordinal);
    private readonly HashSet<string> grafanaQueries = new(StringComparer.Ordinal);
    private readonly HashSet<string> grafanaAnnotationTags = new(StringComparer.Ordinal);
    private readonly HashSet<string> victoriaQueries = new(StringComparer.Ordinal);
    private string? pagerDutyIncidentId;
    private string? victoriaAccountId;
    private string? victoriaProjectId;
    private bool grafanaAllowsUntaggedAnnotations;

    private McpAllowedResourcePolicy(string source)
    {
        this.source = source.ToLowerInvariant();
    }

    public static McpAllowedResourcePolicy Create(string source, JsonNode? allowedResources)
    {
        var policy = new McpAllowedResourcePolicy(source);
        if (allowedResources is not JsonObject root) return policy;

        foreach (var project in Array(root, "projects").OfType<JsonObject>())
        {
            policy.gitLabProjects.Add(Text(project, "id") ?? "");
        }

        foreach (var item in Array(root, "namespaces").OfType<JsonObject>())
        {
            var jobNamespace = Text(item, "name");
            foreach (var job in Array(item, "jobs").Select(StringValue).Where(value => value is not null))
            {
                policy.nomadJobs.Add($"{jobNamespace}\u001f{job}");
            }
        }

        policy.pagerDutyIncidentId = Text(root, "incidentId");
        foreach (var dashboard in Array(root, "dashboards").OfType<JsonObject>())
        {
            policy.grafanaDashboards.Add(Text(dashboard, "uid") ?? "");
        }
        foreach (var query in Array(root, "queries").OfType<JsonObject>())
        {
            var name = Text(query, "name");
            var datasource = Text(query, "datasourceUid");
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(datasource))
            {
                policy.grafanaQueries.Add($"{datasource}\u001f{name}");
            }
            if (!string.IsNullOrWhiteSpace(name)) policy.victoriaQueries.Add(name);
        }
        var annotationTags = Array(root, "annotationTags")
            .Select(StringValue)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToList();
        policy.grafanaAnnotationTags.UnionWith(annotationTags);
        policy.grafanaAllowsUntaggedAnnotations = Property(root, "annotationTags") is JsonArray && annotationTags.Count == 0;
        policy.victoriaAccountId = Text(root, "accountId");
        policy.victoriaProjectId = Text(root, "projectId");
        return policy;
    }

    public bool AllowsFinding(EvidenceFinding finding)
    {
        var scope = finding.Provenance?["scope"] as JsonObject;
        return source switch
        {
            "gitlab" => AllowsGitLabFinding(finding, scope),
            "nomad" => AllowsNomadScope(scope),
            "pagerduty" => MatchesPagerDuty(scope, finding.ObjectId),
            "grafana" => AllowsGrafanaFinding(finding, scope),
            "victorialogs" => AllowsVictoriaFinding(finding, scope),
            _ => false
        };
    }

    public bool AllowsTimeline(TimelineCandidate item)
    {
        if (AllowsUrl(item.Url)) return true;
        return source switch
        {
            "pagerduty" => MatchesPagerDuty(null, item.ObjectId),
            "grafana" => MatchesGrafanaObject(item.ObjectId),
            "victorialogs" => !string.IsNullOrWhiteSpace(item.ObjectId)
                && victoriaQueries.Contains(item.ObjectId),
            _ => false
        };
    }

    public bool AllowsCodeReference(CodeReference item) =>
        source == "gitlab" && gitLabProjects.Contains(item.ProjectId) && AllowsUrl(item.Url);

    public JsonObject CanonicalizeProvenance(EvidenceFinding finding)
    {
        var provenance = finding.Provenance?.DeepClone() as JsonObject ?? new JsonObject();
        if (provenance["scope"] is not JsonObject scope) return provenance;
        if (source == "gitlab")
        {
            scope["project"] = Text(scope, "project") ?? Text(scope, "projectId");
            scope["pipelineId"] = Text(scope, "pipelineId") ?? Text(scope, "pipeline");
            scope["status"] = (Text(scope, "status") ?? Text(scope, "pipelineStatus"))?.ToLowerInvariant();
            if (TryBoolean(scope, "allowFailure", out var allowFailure)) scope["allowFailure"] = allowFailure;
        }
        else if (source == "nomad")
        {
            scope["namespace"] = Text(scope, "namespace");
            scope["job"] = Text(scope, "job");
        }
        else if (source is "grafana" or "victorialogs")
        {
            scope["name"] = Text(scope, "name");
        }
        else if (source == "pagerduty")
        {
            scope["incidentId"] = Text(scope, "incidentId");
        }
        return provenance;
    }

    public bool AllowsUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(uri.PathAndQuery);
        }
        catch (UriFormatException)
        {
            return false;
        }

        return source switch
        {
            "gitlab" => gitLabProjects.Any(project => ContainsResource(decoded, project)),
            "nomad" => nomadJobs.Any(resource =>
            {
                var parts = resource.Split('\u001f');
                return ContainsResource(decoded, parts[1]) && decoded.Contains(parts[0], StringComparison.Ordinal);
            }),
            "pagerduty" => !string.IsNullOrWhiteSpace(pagerDutyIncidentId)
                && ContainsResource(decoded, pagerDutyIncidentId),
            "grafana" => grafanaDashboards.Any(uid => ContainsResource(decoded, uid)),
            "victorialogs" => victoriaQueries.Any(query => decoded.Contains(query, StringComparison.Ordinal)),
            _ => false
        };
    }

    private bool AllowsGitLabFinding(EvidenceFinding finding, JsonObject? scope)
    {
        var project = Text(scope, "project") ?? Text(scope, "projectId");
        if (string.IsNullOrWhiteSpace(project) || !gitLabProjects.Contains(project)) return false;
        if (string.Equals(finding.Category, "pipeline", StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrWhiteSpace(Text(scope, "pipelineId"))
                && IsPipelineStatus(Text(scope, "status"));
        }
        if (!string.Equals(finding.Category, "pipeline-job-output", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.IsNullOrWhiteSpace(Text(scope, "pipelineId"))) return false;
        var status = Text(scope, "status") ?? Text(scope, "pipelineStatus");
        if (string.Equals(finding.ObjectType, "pipeline-job-cancellations", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(status, "canceled", StringComparison.OrdinalIgnoreCase);
        }
        return status is not null
            && (status.Equals("failed", StringComparison.OrdinalIgnoreCase)
                || status.Equals("canceled", StringComparison.OrdinalIgnoreCase))
            && TryBoolean(scope, "allowFailure", out _);
    }

    private bool AllowsNomadScope(JsonObject? scope)
    {
        var jobNamespace = Text(scope, "namespace");
        var job = Text(scope, "job");
        return !string.IsNullOrWhiteSpace(jobNamespace)
            && !string.IsNullOrWhiteSpace(job)
            && nomadJobs.Contains($"{jobNamespace}\u001f{job}");
    }

    private bool MatchesPagerDuty(JsonObject? scope, string? objectId)
    {
        if (string.IsNullOrWhiteSpace(pagerDutyIncidentId)) return false;
        return string.Equals(Text(scope, "incidentId"), pagerDutyIncidentId, StringComparison.Ordinal)
            || string.Equals(objectId, pagerDutyIncidentId, StringComparison.Ordinal);
    }

    private bool AllowsGrafanaFinding(EvidenceFinding finding, JsonObject? scope)
    {
        if (string.Equals(finding.Category, "annotation", StringComparison.OrdinalIgnoreCase))
        {
            var tagsNode = Property(scope, "annotationTags");
            if (tagsNode is not JsonArray tags) return false;
            var returnedTags = tags.Select(StringValue).Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().ToList();
            return returnedTags.Count == 0
                ? grafanaAllowsUntaggedAnnotations
                : returnedTags.All(grafanaAnnotationTags.Contains);
        }
        var name = Text(scope, "name");
        var datasource = Text(scope, "datasourceUid");
        return !string.IsNullOrWhiteSpace(name)
            && !string.IsNullOrWhiteSpace(datasource)
            && grafanaQueries.Contains($"{datasource}\u001f{name}");
    }

    private bool AllowsVictoriaFinding(EvidenceFinding finding, JsonObject? scope)
    {
        var name = Text(scope, "name") ?? finding.ObjectId;
        if (string.IsNullOrWhiteSpace(name) || !victoriaQueries.Contains(name)) return false;
        var account = Text(scope, "accountId");
        var project = Text(scope, "projectId");
        return (account is null || string.Equals(account, victoriaAccountId, StringComparison.Ordinal))
            && (project is null || string.Equals(project, victoriaProjectId, StringComparison.Ordinal));
    }

    private bool MatchesGrafanaObject(string? objectId)
    {
        if (string.IsNullOrWhiteSpace(objectId)) return false;
        return grafanaQueries.Any(query =>
        {
            var parts = query.Split('\u001f');
            return string.Equals(objectId, $"{parts[0]}:{parts[1]}", StringComparison.Ordinal);
        });
    }

    private static bool IsPipelineStatus(string? status) => status?.ToLowerInvariant() is
        "created" or "waiting_for_resource" or "preparing" or "pending" or "running"
        or "success" or "failed" or "canceled" or "skipped" or "manual" or "scheduled";

    private static bool ContainsResource(string value, string resource)
    {
        if (string.IsNullOrWhiteSpace(resource)) return false;
        var index = value.IndexOf(resource, StringComparison.Ordinal);
        while (index >= 0)
        {
            var before = index == 0 || !char.IsLetterOrDigit(value[index - 1]);
            var end = index + resource.Length;
            var after = end == value.Length || !char.IsLetterOrDigit(value[end]);
            if (before && after) return true;
            index = value.IndexOf(resource, index + 1, StringComparison.Ordinal);
        }
        return false;
    }

    private static JsonNode? Property(JsonObject? value, string name) => value?
        .FirstOrDefault(item => string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static JsonArray Array(JsonObject? value, string name) => Property(value, name) as JsonArray ?? [];

    private static string? Text(JsonObject? value, string name) => StringValue(Property(value, name));

    private static string? StringValue(JsonNode? value)
    {
        if (value is not JsonValue jsonValue) return null;
        if (jsonValue.TryGetValue<string>(out var text)) return text;
        if (jsonValue.TryGetValue<long>(out var number)) return number.ToString();
        return null;
    }

    private static bool TryBoolean(JsonObject? value, string name, out bool result)
    {
        var node = Property(value, name);
        if (node is JsonValue jsonValue && jsonValue.TryGetValue<bool>(out result)) return true;
        if (node is JsonValue textValue
            && textValue.TryGetValue<string>(out var text)
            && bool.TryParse(text, out result)) return true;
        result = false;
        return false;
    }
}

internal sealed class McpAllowedUrlPolicy
{
    private readonly IReadOnlyList<Uri> roots;
    private readonly McpSecretSanitizer secrets;

    private McpAllowedUrlPolicy(IReadOnlyList<Uri> roots, McpSecretSanitizer secrets)
    {
        this.roots = roots;
        this.secrets = secrets;
    }

    public static McpAllowedUrlPolicy Create(
        string? allowedBaseUrl,
        JsonNode? allowedResources,
        McpSecretSanitizer secrets)
    {
        var roots = new List<Uri>();
        AddRoot(roots, allowedBaseUrl);
        CollectExplicitResourceUrls(allowedResources, null, roots, 0);
        return new McpAllowedUrlPolicy(
            roots
                .DistinctBy(uri => uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.SafeUnescaped), StringComparer.OrdinalIgnoreCase)
                .OrderBy(uri => uri.AbsoluteUri, StringComparer.Ordinal)
                .ToList(),
            secrets);
    }

    public string? Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo)
            || roots.Count == 0
            || !roots.Any(root => IsWithin(root, uri))
            || secrets.ContainsSecret(uri.AbsolutePath))
        {
            return null;
        }

        var query = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(parameter => KeepQueryParameter(parameter, secrets));
        var builder = new UriBuilder(uri)
        {
            Fragment = "",
            Query = string.Join('&', query),
            UserName = "",
            Password = ""
        };
        return builder.Uri.AbsoluteUri;
    }

    private static bool KeepQueryParameter(string parameter, McpSecretSanitizer secrets)
    {
        var separator = parameter.IndexOf('=');
        var rawName = separator < 0 ? parameter : parameter[..separator];
        string name;
        try
        {
            name = Uri.UnescapeDataString(rawName.Replace('+', ' '));
        }
        catch (UriFormatException)
        {
            return false;
        }
        return !McpSecretSanitizer.IsSensitiveName(name) && !secrets.ContainsSecret(parameter);
    }

    private static bool IsWithin(Uri root, Uri candidate)
    {
        if (!string.Equals(root.Scheme, candidate.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(root.IdnHost, candidate.IdnHost, StringComparison.OrdinalIgnoreCase)
            || root.Port != candidate.Port)
        {
            return false;
        }

        var rootPath = root.AbsolutePath.TrimEnd('/');
        var candidatePath = candidate.AbsolutePath.TrimEnd('/');
        if (rootPath.Length == 0) return true;
        return string.Equals(rootPath, candidatePath, StringComparison.Ordinal)
            || candidatePath.StartsWith(rootPath + "/", StringComparison.Ordinal);
    }

    private static void CollectExplicitResourceUrls(JsonNode? node, string? propertyName, List<Uri> roots, int depth)
    {
        if (node is null || depth >= 8) return;
        if (node is JsonObject jsonObject)
        {
            foreach (var property in jsonObject)
            {
                CollectExplicitResourceUrls(property.Value, property.Key, roots, depth + 1);
            }
            return;
        }
        if (node is JsonArray jsonArray)
        {
            foreach (var item in jsonArray.Take(100))
            {
                CollectExplicitResourceUrls(item, propertyName, roots, depth + 1);
            }
            return;
        }
        if (propertyName is not null
            && McpSecretSanitizer.IsUrlName(propertyName)
            && node is JsonValue value
            && value.TryGetValue<string>(out var text))
        {
            AddRoot(roots, text);
        }
    }

    private static void AddRoot(List<Uri> roots, string? value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https"
            && string.IsNullOrEmpty(uri.UserInfo))
        {
            roots.Add(uri);
        }
    }
}
