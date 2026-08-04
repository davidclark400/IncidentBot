using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Panko.Api.Domain;
using Panko.Api.Cases;

namespace Panko.Api.Crumbs;

/// <summary>
/// Treats an MCP tool result as untrusted input before it crosses the Crumb source boundary.
/// </summary>
internal static class McpCrumbSourceBoundary
{
    private const int MaximumSummaryCharacters = 600;
    private const int MaximumDiagnosticCharacters = 600;
    private const int MaximumProvenanceBytes = 2048;
    private const int MaximumCodeReferencesPerCrumb = 8;
    private const int RetainedPercentage = 90;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static CrumbSourceResult Normalize(
        string requestedSource,
        CrumbSourceResult result,
        CrumbScope scope,
        DateTimeOffset caseOpenedAt,
        string? allowedBaseUrl,
        JsonNode? allowedResources,
        string? credential)
    {
        if (string.IsNullOrWhiteSpace(requestedSource)
            || !string.Equals(result.Source, requestedSource, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("MCP Crumb source result did not match the requested source.");
        }

        var maxItems = Math.Max(0, scope.MaxItems);
        var retainedByteLimit = RetainedByteLimit(scope.MaxBytes);
        var secrets = new McpSecretSanitizer(credential);
        var urls = McpAllowedUrlPolicy.Create(allowedBaseUrl, allowedResources, secrets);
        var resources = McpAllowedResourcePolicy.Create(requestedSource, allowedResources);
        var rawCrumbs = result.Crumbs ?? [];
        var boundaryMutated = rawCrumbs.Any(item => item is not null
                && CrumbRequiresTruncation(item, excerptCharacters: null, secrets, urls, resources))
            || (result.Trail ?? []).Any(item => item is not null
                && (secrets.Sanitize(item.Summary, MaximumSummaryCharacters) != item.Summary
                    || item.Url is not null
                    && (!resources.AllowsUrl(item.Url)
                        || !string.Equals(urls.Sanitize(item.Url), item.Url, StringComparison.Ordinal))))
            || (result.Links ?? []).Any(item => item is not null
                && (secrets.Sanitize(item.Label, 240) != item.Label
                    || !resources.AllowsUrl(item.Url)
                    || !string.Equals(urls.Sanitize(item.Url), item.Url, StringComparison.Ordinal)))
            || secrets.Sanitize(result.Diagnostic, MaximumDiagnosticCharacters) != result.Diagnostic;
        var eligibleCrumbCount = Math.Min(
            maxItems,
            rawCrumbs
                .Where(item => item is not null
                    && SourceMatches(item.Source, requestedSource)
                    && IsWithinScope(item.OccurredAt, scope)
                    && resources.AllowsCrumb(item))
                .Select(item => CanonicalCrumbId(item, requestedSource))
                .Distinct(StringComparer.Ordinal)
                .Count());
        var excerptCharacters = CrumbExcerptLimit(retainedByteLimit, eligibleCrumbCount);
        boundaryMutated |= rawCrumbs.Any(item => item is not null
            && CrumbRequiresTruncation(item, excerptCharacters, secrets, urls, resources));

        var deduplicatedCrumbs = rawCrumbs
            .Where(item => item is not null
                && SourceMatches(item.Source, requestedSource)
                && IsWithinScope(item.OccurredAt, scope)
                && resources.AllowsCrumb(item))
            .Select(item => NormalizeCrumb(
                item, requestedSource, excerptCharacters, secrets, urls, resources))
            .Where(item => !string.IsNullOrWhiteSpace(item.Summary))
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => CrumbRankingPolicy.Rank(group, caseOpenedAt)[0])
            .ToList();
        CanonicalizeGitLabFailureOrder(requestedSource, deduplicatedCrumbs);
        var missingRequiredGrafanaMetrics = resources.MissingRequiredGrafanaMetrics(deduplicatedCrumbs);
        var crumbs = CrumbRankingPolicy.Rank(deduplicatedCrumbs, caseOpenedAt)
            .Take(maxItems)
            .ToList();

        var selectedTrail = (result.Trail ?? [])
            .Where(item => item is not null
                && SourceMatches(item.Source, requestedSource)
                && IsWithinScope(item.OccurredAt, scope)
                && resources.AllowsTrailEntry(item)
                && !resources.IsContextGrafanaTrailEntry(item))
            .Select(item => NormalizeTrailEntry(item, requestedSource, secrets, urls, resources))
            .Where(item => !string.IsNullOrWhiteSpace(item.Summary))
            .GroupBy(TrailIdentity, StringComparer.Ordinal)
            .Select(group => group.OrderBy(TrailStableIdentity, StringComparer.Ordinal).First())
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
        if (missingRequiredGrafanaMetrics.Count > 0)
        {
            diagnostic = AppendDiagnostic(
                diagnostic,
                "Required Grafana metrics returned no Crumbs: "
                + string.Join(", ", missingRequiredGrafanaMetrics)
                + ".");
        }
        var prototype = new CrumbSourceResult(
            requestedSource,
            missingRequiredGrafanaMetrics.Count > 0 && result.Health == CrumbSourceHealth.Complete
                ? CrumbSourceHealth.Partial
                : result.Health,
            [],
            [],
            [],
            Math.Max(0, result.DurationMilliseconds),
            diagnostic);

        var normalized = FitToRetainedBudget(
            prototype, crumbs, selectedTrail, links, retainedByteLimit, caseOpenedAt);
        var reduced = boundaryMutated
            || normalized.Crumbs.Count < rawCrumbs.Count
            || normalized.Trail.Count < (result.Trail?.Count ?? 0)
            || normalized.Links.Count < (result.Links?.Count ?? 0);
        if (!reduced || normalized.Health != CrumbSourceHealth.Complete) return normalized;
        var partial = normalized with
        {
            Health = CrumbSourceHealth.Partial,
            Diagnostic = AppendDiagnostic(normalized.Diagnostic, "MCP boundary rejected, deduplicated, or truncated returned Crumbs.")
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

    internal static int EstimateRetainedBytes(CrumbSourceResult result) =>
        JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions).Length;

    private static Crumb NormalizeCrumb(
        Crumb crumb,
        string source,
        int excerptCharacters,
        McpSecretSanitizer secrets,
        McpAllowedUrlPolicy urls,
        McpAllowedResourcePolicy resources)
    {
        var category = (secrets.Sanitize(crumb.Category, 80) ?? "crumb").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(category)) category = "crumb";
        var summary = secrets.Sanitize(crumb.Summary, MaximumSummaryCharacters) ?? "";
        var id = CanonicalCrumbId(crumb, source);
        var codeReferences = (crumb.CodeReferences ?? [])
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
            .Take(MaximumCodeReferencesPerCrumb)
            .ToList();

        return new Crumb(
            id,
            source,
            crumb.OccurredAt,
            crumb.EndedAt >= crumb.OccurredAt ? crumb.EndedAt : null,
            category,
            resources.IsContextGrafanaCrumb(crumb)
                ? "info"
                : NormalizeSeverity(crumb.Severity),
            summary,
            secrets.Sanitize(crumb.Excerpt, excerptCharacters),
            resources.AllowsUrl(crumb.Url) ? urls.Sanitize(crumb.Url) : null,
            double.IsFinite(crumb.Confidence) ? Math.Clamp(crumb.Confidence, 0, 1) : 0,
            SanitizeProvenance(resources.CanonicalizeProvenance(crumb), secrets, urls),
            secrets.Sanitize(crumb.Actor, 240),
            secrets.Sanitize(crumb.ObjectType, 120),
            secrets.Sanitize(crumb.ObjectId, 240),
            codeReferences);
    }

    private static TrailCandidate NormalizeTrailEntry(
        TrailCandidate item,
        string source,
        McpSecretSanitizer secrets,
        McpAllowedUrlPolicy urls,
        McpAllowedResourcePolicy resources) =>
        new(
            item.OccurredAt,
            source,
            secrets.Sanitize(item.Kind, 80) ?? "crumb",
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
        string crumbId,
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
            CrumbSourceUtilities.Id(source, "mcp-code", crumbId, identity),
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

    private static CrumbSourceResult FitToRetainedBudget(
        CrumbSourceResult prototype,
        IReadOnlyList<Crumb> candidates,
        IReadOnlyList<TrailCandidate> trailCandidates,
        IReadOnlyList<SourceLink> linkCandidates,
        int retainedByteLimit,
        DateTimeOffset caseOpenedAt)
    {
        var crumbs = new List<Crumb>();
        var trail = new List<TrailCandidate>();
        var links = new List<SourceLink>();
        var result = BuildResult(prototype, crumbs, trail, links);

        if (retainedByteLimit <= 0) return result with { Diagnostic = null };
        if (EstimateRetainedBytes(result) > retainedByteLimit)
        {
            prototype = prototype with { Diagnostic = null };
            result = BuildResult(prototype, crumbs, trail, links);
        }

        var primaryCrumbLimit = Math.Max(EstimateRetainedBytes(result), retainedByteLimit * 75 / 100);
        var deferredCrumbs = new List<Crumb>();
        foreach (var crumb in candidates)
        {
            crumbs.Add(crumb);
            if (EstimateRetainedBytes(BuildResult(prototype, crumbs, trail, links)) <= primaryCrumbLimit)
            {
                continue;
            }

            crumbs.RemoveAt(crumbs.Count - 1);
            deferredCrumbs.Add(crumb);
        }

        var trailLimit = retainedByteLimit * 92 / 100;
        foreach (var item in trailCandidates)
        {
            trail.Add(item);
            if (EstimateRetainedBytes(BuildResult(prototype, crumbs, trail, links)) > trailLimit)
            {
                trail.RemoveAt(trail.Count - 1);
            }
        }

        foreach (var link in linkCandidates)
        {
            links.Add(link);
            if (EstimateRetainedBytes(BuildResult(prototype, crumbs, trail, links)) > retainedByteLimit)
            {
                links.RemoveAt(links.Count - 1);
            }
        }

        foreach (var crumb in deferredCrumbs)
        {
            crumbs.Add(crumb);
            if (EstimateRetainedBytes(BuildResult(prototype, crumbs, trail, links)) > retainedByteLimit)
            {
                crumbs.RemoveAt(crumbs.Count - 1);
            }
        }

        var rankedCrumbs = CrumbRankingPolicy.Rank(crumbs, caseOpenedAt);
        return BuildResult(prototype, rankedCrumbs, trail, links);
    }

    private static CrumbSourceResult BuildResult(
        CrumbSourceResult prototype,
        IReadOnlyList<Crumb> crumbs,
        IReadOnlyList<TrailCandidate> trail,
        IReadOnlyList<SourceLink> links) =>
        prototype with
        {
            Crumbs = crumbs.ToArray(),
            Trail = trail.ToArray(),
            Links = links.ToArray()
        };

    private static int CrumbExcerptLimit(int retainedByteLimit, int crumbCount)
    {
        if (retainedByteLimit <= 0 || crumbCount <= 0) return 0;
        var fairShare = retainedByteLimit / crumbCount;
        return Math.Clamp((fairShare - 900) / 2, 200, 6000);
    }

    private static string CanonicalCrumbId(Crumb crumb, string source)
    {
        var identity = string.IsNullOrWhiteSpace(crumb.Id)
            ? $"{crumb.OccurredAt.UtcTicks}|{crumb.Category}|{crumb.Summary}"
            : crumb.Id;
        return CrumbSourceUtilities.Id(source, "mcp", identity);
    }

    private static bool SourceMatches(string? actual, string requested) =>
        string.Equals(actual, requested, StringComparison.OrdinalIgnoreCase);

    private static bool IsWithinScope(DateTimeOffset occurredAt, CrumbScope scope) =>
        occurredAt >= scope.Start && occurredAt <= scope.End;

    private static void CanonicalizeGitLabFailureOrder(
        string source,
        IReadOnlyList<Crumb> crumbs)
    {
        if (!string.Equals(source, CrumbSourceRegistry.GitLab, StringComparison.OrdinalIgnoreCase)) return;
        foreach (var group in crumbs
                     .Where(item => item.Category == "pipeline-job-output"
                         && item.ObjectType != "pipeline-job-cancellations")
                     .GroupBy(CrumbRankingPolicy.GroupKey, StringComparer.Ordinal))
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

    private static string TrailIdentity(TrailCandidate item) => item.StableId;

    private static string TrailStableIdentity(TrailCandidate item) =>
        $"{TrailIdentity(item)}|{item.Severity}|{item.Url}|{item.Actor}";

    private static string AppendDiagnostic(string? current, string message) =>
        string.IsNullOrWhiteSpace(current)
            ? message
            : CrumbSourceUtilities.Truncate($"{current}; {message}", MaximumDiagnosticCharacters);

    private static bool CrumbRequiresTruncation(
        Crumb crumb,
        int? excerptCharacters,
        McpSecretSanitizer secrets,
        McpAllowedUrlPolicy urls,
        McpAllowedResourcePolicy resources) =>
        !string.Equals(
            (secrets.Sanitize(crumb.Category, 80) ?? "crumb").Trim().ToLowerInvariant(),
            crumb.Category,
            StringComparison.Ordinal)
        || secrets.Sanitize(crumb.Summary, MaximumSummaryCharacters) != crumb.Summary
        || excerptCharacters is not null
        && secrets.Sanitize(crumb.Excerpt, excerptCharacters.Value) != crumb.Excerpt
        || crumb.Url is not null
        && (!resources.AllowsUrl(crumb.Url)
            || !string.Equals(urls.Sanitize(crumb.Url), crumb.Url, StringComparison.Ordinal))
        || (crumb.CodeReferences?.Count ?? 0) > MaximumCodeReferencesPerCrumb
        || (crumb.CodeReferences ?? []).Any(reference =>
            !resources.AllowsCodeReference(reference)
            || !string.Equals(urls.Sanitize(reference.Url), reference.Url, StringComparison.Ordinal)
            || secrets.Sanitize(reference.Excerpt, 1600) != reference.Excerpt)
        || JsonSerializer.SerializeToUtf8Bytes(crumb.Provenance, JsonOptions).Length > MaximumProvenanceBytes;

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
/// PagerDuty incident from the same service.
/// </summary>
internal sealed class McpAllowedResourcePolicy
{
    private readonly string source;
    private readonly HashSet<string> gitLabProjects = new(StringComparer.Ordinal);
    private readonly HashSet<string> nomadJobs = new(StringComparer.Ordinal);
    private readonly HashSet<string> consulServices = new(StringComparer.Ordinal);
    private readonly HashSet<string> grafanaDashboards = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GrafanaQueryPolicy> grafanaQueries =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> grafanaAnnotationTags = new(StringComparer.Ordinal);
    private readonly HashSet<string> victoriaQueries = new(StringComparer.Ordinal);
    private string? pagerDutyIncidentId;
    private string? consulDatacenter;
    private string? consulPartition;
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

        foreach (var service in Array(root, "services").OfType<JsonObject>())
        {
            var name = Text(service, "name");
            var serviceNamespace = Text(service, "namespace") ?? "";
            if (!string.IsNullOrWhiteSpace(name))
            {
                policy.consulServices.Add($"{serviceNamespace}\u001f{name}");
            }
        }

        policy.pagerDutyIncidentId = Text(root, "pagerDutyIncidentId");
        policy.consulDatacenter = Text(root, "datacenter");
        policy.consulPartition = Text(root, "partition");
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
                var key = $"{datasource}\u001f{name}";
                policy.grafanaQueries.TryAdd(key, new GrafanaQueryPolicy(
                    name,
                    datasource,
                    Text(query, "crumbMode") ?? "anomaly",
                    Text(query, "requirement") ?? "optional"));
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

    public bool AllowsCrumb(Crumb crumb)
    {
        var scope = crumb.Provenance?["scope"] as JsonObject;
        return source switch
        {
            "gitlab" => AllowsGitLabCrumb(crumb, scope),
            "nomad" => AllowsNomadScope(scope),
            "consul" => AllowsConsulScope(scope),
            "pagerduty" => MatchesPagerDuty(scope, crumb.ObjectId),
            "grafana" => AllowsGrafanaCrumb(crumb, scope),
            "victorialogs" => AllowsVictoriaCrumb(crumb, scope),
            _ => false
        };
    }

    public bool AllowsTrailEntry(TrailCandidate item)
    {
        if (AllowsUrl(item.Url)) return true;
        return source switch
        {
            "pagerduty" => MatchesPagerDuty(null, item.ObjectId),
            "consul" => MatchesConsulObject(item.ObjectId),
            "grafana" => MatchesGrafanaObject(item.ObjectId),
            "victorialogs" => !string.IsNullOrWhiteSpace(item.ObjectId)
                && victoriaQueries.Contains(item.ObjectId),
            _ => false
        };
    }

    public bool IsContextGrafanaCrumb(Crumb crumb)
    {
        if (source != "grafana") return false;
        var key = GrafanaQueryKey(crumb.Provenance?["scope"] as JsonObject);
        return key is not null
            && grafanaQueries.TryGetValue(key, out var query)
            && string.Equals(query.CrumbMode, "context", StringComparison.Ordinal);
    }

    public bool IsContextGrafanaTrailEntry(TrailCandidate item)
    {
        if (source != "grafana" || string.IsNullOrWhiteSpace(item.ObjectId)) return false;
        return grafanaQueries.Values.Any(query =>
            string.Equals(item.ObjectId, $"{query.DatasourceUid}:{query.Name}", StringComparison.Ordinal)
            && string.Equals(query.CrumbMode, "context", StringComparison.Ordinal));
    }

    public IReadOnlyList<string> MissingRequiredGrafanaMetrics(
        IReadOnlyList<Crumb> crumbs)
    {
        if (source != "grafana") return [];
        var returned = crumbs
            .Select(crumb => GrafanaQueryKey(crumb.Provenance?["scope"] as JsonObject))
            .Where(key => key is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);
        return grafanaQueries
            .Where(item => string.Equals(item.Value.Requirement, "required", StringComparison.Ordinal)
                           && !returned.Contains(item.Key))
            .Select(item => item.Value.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    public bool AllowsCodeReference(CodeReference item) =>
        source == "gitlab" && gitLabProjects.Contains(item.ProjectId) && AllowsUrl(item.Url);

    public JsonObject CanonicalizeProvenance(Crumb crumb)
    {
        var provenance = crumb.Provenance?.DeepClone() as JsonObject ?? new JsonObject();
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
        else if (source == "consul")
        {
            scope["datacenter"] = Text(scope, "datacenter");
            scope["partition"] = Text(scope, "partition");
            scope["namespace"] = Text(scope, "namespace");
            scope["service"] = Text(scope, "service");
            scope["status"] = Text(scope, "status")?.ToLowerInvariant();
        }
        else if (source is "grafana" or "victorialogs")
        {
            scope["name"] = Text(scope, "name");
        }
        else if (source == "pagerduty")
        {
            scope["pagerDutyIncidentId"] = Text(scope, "pagerDutyIncidentId");
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
            "consul" => consulServices.Any(resource =>
            {
                var parts = resource.Split('\u001f');
                return ContainsResource(decoded, parts[1])
                    && QueryParameterMatches(uri, "ns", parts[0])
                    && QueryParameterMatches(uri, "dc", consulDatacenter)
                    && QueryParameterMatches(uri, "partition", consulPartition);
            }),
            "pagerduty" => !string.IsNullOrWhiteSpace(pagerDutyIncidentId)
                && ContainsResource(decoded, pagerDutyIncidentId),
            "grafana" => grafanaDashboards.Any(uid => ContainsResource(decoded, uid)),
            "victorialogs" => victoriaQueries.Any(query => decoded.Contains(query, StringComparison.Ordinal)),
            _ => false
        };
    }

    private bool AllowsGitLabCrumb(Crumb crumb, JsonObject? scope)
    {
        var project = Text(scope, "project") ?? Text(scope, "projectId");
        if (string.IsNullOrWhiteSpace(project) || !gitLabProjects.Contains(project)) return false;
        if (string.Equals(crumb.Category, "pipeline", StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrWhiteSpace(Text(scope, "pipelineId"))
                && IsPipelineStatus(Text(scope, "status"));
        }
        if (!string.Equals(crumb.Category, "pipeline-job-output", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.IsNullOrWhiteSpace(Text(scope, "pipelineId"))) return false;
        var status = Text(scope, "status") ?? Text(scope, "pipelineStatus");
        if (string.Equals(crumb.ObjectType, "pipeline-job-cancellations", StringComparison.OrdinalIgnoreCase))
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

    private bool AllowsConsulScope(JsonObject? scope)
    {
        var service = Text(scope, "service");
        var serviceNamespace = Text(scope, "namespace") ?? "";
        if (string.IsNullOrWhiteSpace(service)
            || !consulServices.Contains($"{serviceNamespace}\u001f{service}"))
        {
            return false;
        }

        var datacenter = Text(scope, "datacenter") ?? "";
        var partition = Text(scope, "partition") ?? "";
        var status = Text(scope, "status")?.ToLowerInvariant();
        return status is "passing" or "warning" or "critical" or "unknown" or "unregistered"
            && (string.IsNullOrWhiteSpace(consulDatacenter)
                || string.Equals(datacenter, consulDatacenter, StringComparison.Ordinal))
            && (string.IsNullOrWhiteSpace(consulPartition)
                || string.Equals(partition, consulPartition, StringComparison.Ordinal));
    }

    private bool MatchesPagerDuty(JsonObject? scope, string? objectId)
    {
        if (string.IsNullOrWhiteSpace(pagerDutyIncidentId)) return false;
        return string.Equals(Text(scope, "pagerDutyIncidentId"), pagerDutyIncidentId, StringComparison.Ordinal)
            || string.Equals(objectId, pagerDutyIncidentId, StringComparison.Ordinal);
    }

    private bool AllowsGrafanaCrumb(Crumb crumb, JsonObject? scope)
    {
        if (string.Equals(crumb.Category, "annotation", StringComparison.OrdinalIgnoreCase))
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
            && grafanaQueries.ContainsKey($"{datasource}\u001f{name}");
    }

    private bool AllowsVictoriaCrumb(Crumb crumb, JsonObject? scope)
    {
        var name = Text(scope, "name") ?? crumb.ObjectId;
        if (string.IsNullOrWhiteSpace(name) || !victoriaQueries.Contains(name)) return false;
        var account = Text(scope, "accountId");
        var project = Text(scope, "projectId");
        return (account is null || string.Equals(account, victoriaAccountId, StringComparison.Ordinal))
            && (project is null || string.Equals(project, victoriaProjectId, StringComparison.Ordinal));
    }

    private bool MatchesGrafanaObject(string? objectId)
    {
        if (string.IsNullOrWhiteSpace(objectId)) return false;
        return grafanaQueries.Keys.Any(query =>
        {
            var parts = query.Split('\u001f');
            return string.Equals(objectId, $"{parts[0]}:{parts[1]}", StringComparison.Ordinal);
        });
    }

    private static string? GrafanaQueryKey(JsonObject? scope)
    {
        var name = Text(scope, "name");
        var datasource = Text(scope, "datasourceUid");
        return string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(datasource)
            ? null
            : $"{datasource}\u001f{name}";
    }

    private sealed record GrafanaQueryPolicy(
        string Name,
        string DatasourceUid,
        string CrumbMode,
        string Requirement);

    private bool MatchesConsulObject(string? objectId)
    {
        if (string.IsNullOrWhiteSpace(objectId)) return false;
        return consulServices.Any(resource =>
        {
            var parts = resource.Split('\u001f');
            var expected = string.IsNullOrWhiteSpace(parts[0])
                ? parts[1]
                : $"{parts[0]}/{parts[1]}";
            return string.Equals(objectId, expected, StringComparison.Ordinal)
                || objectId.StartsWith(expected + "/", StringComparison.Ordinal);
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

    private static bool QueryParameterMatches(Uri uri, string name, string? expected)
    {
        if (string.IsNullOrWhiteSpace(expected)) return true;
        foreach (var parameter in uri.Query.TrimStart('?')
                     .Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = parameter.IndexOf('=');
            var rawName = separator < 0 ? parameter : parameter[..separator];
            var rawValue = separator < 0 ? "" : parameter[(separator + 1)..];
            try
            {
                if (string.Equals(Uri.UnescapeDataString(rawName.Replace('+', ' ')), name, StringComparison.Ordinal)
                    && string.Equals(Uri.UnescapeDataString(rawValue.Replace('+', ' ')), expected, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            catch (UriFormatException)
            {
                return false;
            }
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
