using System.Text.Json;
using System.Text.RegularExpressions;
using Panko.Api.Domain;
using Panko.Api.Cases;
using Panko.Api.Infrastructure;
using Panko.Api.Security;

namespace Panko.Api.Crumbs;

public sealed class VictoriaLogsCrumbSource(
    IHttpClientFactory httpClientFactory,
    IMcpCrumbSourceAdapter mcp,
    SafeTemplateRenderer templates,
    CrumbSourceConfiguration crumbSources,
    ICredentialProvider credentials) : ICrumbSourceAdapter
{
    public string Source => CrumbSourceRegistry.VictoriaLogs;
    public bool SupportsWindowExpansion => true;

    public Task<CrumbSourceResult> CollectAsync(CaseContext context, CrumbScope scope, CancellationToken cancellationToken)
    {
        var configuration = context.Recipe.VictoriaLogs;
        if (configuration is null) return Task.FromResult(CrumbSourceResult.Excluded(Source));
        var transport = crumbSources.For(Source);
        return CrumbSourceUtilities.CollectAsync(
            Source, transport, mcp, context, scope,
            new
            {
                configuration.AccountId,
                configuration.ProjectId,
                configuration.StreamFilters,
                configuration.Fields,
                configuration.Queries
            }, async ct =>
        {
            var crumbs = new List<Crumb>();
            var trail = new List<TrailCandidate>();
            var links = new List<SourceLink>();
            var client = httpClientFactory.CreateClient();
            var streamFilters = JsonSerializer.Serialize(configuration.StreamFilters);
            var streamFilterIdentity = JsonSerializer.Serialize(configuration.StreamFilters
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ThenBy(pair => pair.Value, StringComparer.Ordinal));
            var budget = new CrumbSourceResponseBudget(
                scope.MaxBytes,
                transport.MaxBytes,
                configuration.Queries.Count * 2);
            var queriesWithSamples = new List<(
                VictoriaLogsQuery Configuration,
                string Query,
                Dictionary<string, string> Common,
                string Link)>();

            // Count every configured query before downloading any samples. Counts are
            // smaller and establish which streams are relevant; this prevents the first
            // noisy stream from starving all later queries.
            foreach (var configuredQuery in configuration.Queries)
            {
                var query = templates.Render(configuredQuery.Expression, context.Labels);
                var common = new Dictionary<string, string>
                {
                    ["query"] = query,
                    ["start"] = CrumbSourceUtilities.Iso(scope.Start),
                    ["end"] = CrumbSourceUtilities.Iso(scope.End),
                    ["extra_stream_filters"] = streamFilters
                };
                var hitsUrl = CrumbSourceUtilities.Url(transport, "select/logsql/hits");
                var hitsOperation = $"POST /select/logsql/hits ({configuredQuery.Name})";
                var hitsJson = await budget.TryReadJsonAsync(
                    hitsOperation,
                    async operationCancellationToken =>
                    {
                        using var hitsRequest = CrumbSourceUtilities.CreateRequest(
                            HttpMethod.Post, hitsUrl, transport, credentials);
                        hitsRequest.Headers.TryAddWithoutValidation("AccountID", configuration.AccountId);
                        hitsRequest.Headers.TryAddWithoutValidation("ProjectID", configuration.ProjectId);
                        hitsRequest.Content = new FormUrlEncodedContent(common.Concat(new[]
                        {
                            new KeyValuePair<string, string>("step", "60s")
                        }));
                        return await client.SendAsync(
                            hitsRequest,
                            HttpCompletionOption.ResponseHeadersRead,
                            operationCancellationToken);
                    },
                    ct);
                if (hitsJson is null)
                {
                    budget.SkipPlannedOperation();
                    continue;
                }

                long total;
                using (hitsJson)
                {
                    total = TotalHits(hitsJson.RootElement);
                }

                var summary = $"{configuredQuery.Name}: {total} matching log events";
                var severity = total > 0 ? "warning" : "info";
                var link = $"{hitsUrl}?query={Uri.EscapeDataString(query)}&start={Uri.EscapeDataString(CrumbSourceUtilities.Iso(scope.Start))}&end={Uri.EscapeDataString(CrumbSourceUtilities.Iso(scope.End))}";
                links.Add(new SourceLink($"VictoriaLogs: {configuredQuery.Name}", link));
                crumbs.Add(new Crumb(
                    CrumbSourceUtilities.Id(
                        Source,
                        "log-count-snapshot",
                        configuration.AccountId,
                        configuration.ProjectId,
                        configuredQuery.Name,
                        query,
                        streamFilterIdentity), Source,
                    scope.End, null, "log-count", severity, summary, null, link, total > 0 ? 0.85 : 0.7,
                    CrumbSourceUtilities.Provenance("POST /select/logsql/hits", new
                    {
                        configuredQuery.Name,
                        configuration.AccountId,
                        configuration.ProjectId,
                        configuration.StreamFilters,
                        matchCount = total,
                        exactWindowStart = scope.Start,
                        exactWindowEnd = scope.End
                    }), ObjectType: "log-query", ObjectId: configuredQuery.Name));

                if (total <= 0)
                {
                    budget.SkipPlannedOperation();
                    continue;
                }

                queriesWithSamples.Add((configuredQuery, query, common, link));
            }

            foreach (var prepared in queriesWithSamples)
            {
                var configuredAnchors = prepared.Configuration.AnchorPatterns
                    .Select(anchor => new ConfiguredAnchorPattern(
                        anchor.Name,
                        new Regex(
                            anchor.Pattern,
                            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)))
                    .ToList();
                var observedAnchors = new HashSet<string>(StringComparer.Ordinal);
                var sampleLimit = Math.Min(transport.MaxItems, 20);
                var sampleExpression = $"{prepared.Query} | fields {string.Join(", ", configuration.Fields)} | sort by (_time) | limit {sampleLimit}";
                var sampleForm = new Dictionary<string, string>(prepared.Common)
                {
                    ["query"] = sampleExpression
                };
                var queryUrl = CrumbSourceUtilities.Url(transport, "select/logsql/query");
                var sampleOperation = $"POST /select/logsql/query ({prepared.Configuration.Name})";
                var sampleText = await budget.TryReadTextAsync(
                    sampleOperation,
                    async operationCancellationToken =>
                    {
                        using var sampleRequest = CrumbSourceUtilities.CreateRequest(
                            HttpMethod.Post, queryUrl, transport, credentials);
                        sampleRequest.Headers.TryAddWithoutValidation("AccountID", configuration.AccountId);
                        sampleRequest.Headers.TryAddWithoutValidation("ProjectID", configuration.ProjectId);
                        sampleRequest.Content = new FormUrlEncodedContent(sampleForm);
                        return await client.SendAsync(
                            sampleRequest,
                            HttpCompletionOption.ResponseHeadersRead,
                            operationCancellationToken);
                    },
                    ct);
                if (sampleText is null) continue;

                var lines = sampleText.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Distinct(StringComparer.Ordinal)
                    .Take(sampleLimit)
                    .ToList();
                for (var index = 0; index < lines.Count; index++)
                {
                    var line = lines[index];
                    using var sample = JsonDocument.Parse(line);
                    var at = CrumbSourceUtilities.Timestamp(sample.RootElement, "_time", scope.End);
                    var sourceTimestamp = CrumbSourceUtilities.Text(sample.RootElement, "_time", "");
                    var message = Redact(CrumbSourceUtilities.Text(sample.RootElement, "_msg", line), configuration.RedactPatterns);
                    var matchedAnchorNames = configuredAnchors
                        .Where(anchor => !observedAnchors.Contains(anchor.Name) && anchor.Regex.IsMatch(line))
                        .Select(anchor => anchor.Name)
                        .ToList();
                    observedAnchors.UnionWith(matchedAnchorNames);
                    var isAnchor = index == 0 || matchedAnchorNames.Count > 0;
                    var crumbSummary = matchedAnchorNames.Count > 0
                        ? ConfiguredAnchorSummary(prepared.Configuration.Name, matchedAnchorNames, message)
                        : index == 0
                            ? $"First observed {prepared.Configuration.Name} in the case window: {CrumbSourceUtilities.Truncate(message, 200)}"
                            : CrumbSourceUtilities.Truncate(message, 240);
                    crumbs.Add(new Crumb(
                        CrumbSourceUtilities.Id(
                            Source,
                            "log-event",
                            configuration.AccountId,
                            configuration.ProjectId,
                            prepared.Configuration.Name,
                            sourceTimestamp,
                            message),
                        Source, at, null, isAnchor ? "first-error" : "log-sample", "warning", crumbSummary,
                        CrumbSourceUtilities.Truncate(Redact(line, configuration.RedactPatterns), 1200), prepared.Link,
                        matchedAnchorNames.Count > 0 ? 0.9 : 0.8,
                        CrumbSourceUtilities.Provenance("POST /select/logsql/query", new
                        {
                            prepared.Configuration.Name,
                            anchorPatterns = matchedAnchorNames,
                            order = "_time ascending",
                            exactWindowStart = scope.Start,
                            exactWindowEnd = scope.End
                        }), ObjectType: "log-query", ObjectId: prepared.Configuration.Name));
                    if (isAnchor)
                    {
                        trail.Add(new TrailCandidate(at, Source, "first-error", crumbSummary, "warning", prepared.Link,
                            ObjectType: "log-query", ObjectId: prepared.Configuration.Name));
                    }
                }
            }

            var itemLimit = Math.Min(
                Math.Max(0, scope.MaxItems),
                Math.Max(0, transport.MaxItems));
            var rankedCrumbs = CrumbRankingPolicy.Rank(crumbs, context.OpenedAt);
            var orderedTrail = trail.OrderBy(item => item.OccurredAt).ToList();
            var distinctLinks = links.Distinct().ToList();
            var itemsTruncated = rankedCrumbs.Count > itemLimit
                                 || orderedTrail.Count > itemLimit
                                 || distinctLinks.Count > itemLimit;
            var diagnostic = CrumbSourceUtilities.CombineDiagnostics(
                budget.Diagnostic,
                itemsTruncated ? $"Source item limit {itemLimit} truncated Crumbs, Trail entries, or links." : null);
            return new CrumbSourceResult(
                Source,
                budget.IsPartial || itemsTruncated ? CrumbSourceHealth.Partial : CrumbSourceHealth.Complete,
                rankedCrumbs.Take(itemLimit).ToList(),
                orderedTrail.Take(itemLimit).ToList(),
                distinctLinks.Take(itemLimit).ToList(),
                0,
                diagnostic);
        }, cancellationToken);
    }

    private static long TotalHits(JsonElement root)
    {
        if (!root.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array) return 0;
        long total = 0;
        foreach (var group in hits.EnumerateArray())
        {
            if (group.TryGetProperty("total", out var value) && value.TryGetInt64(out var count)) total += count;
        }
        return total;
    }

    private static string ConfiguredAnchorSummary(
        string queryName,
        IReadOnlyList<string> anchorNames,
        string message)
    {
        var label = anchorNames.Count == 1
            ? $"anchor '{anchorNames[0]}'"
            : $"anchors {string.Join(", ", anchorNames.Select(name => $"'{name}'"))}";
        return $"First observed configured {label} in {queryName}: {CrumbSourceUtilities.Truncate(message, 200)}";
    }

    private static string Redact(string value, IEnumerable<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            value = Regex.Replace(value, pattern, "[REDACTED]", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        }
        return value;
    }

    private sealed record ConfiguredAnchorPattern(string Name, Regex Regex);
}
