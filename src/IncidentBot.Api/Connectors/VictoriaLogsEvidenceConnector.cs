using System.Text.Json;
using System.Text.RegularExpressions;
using IncidentBot.Api.Domain;
using IncidentBot.Api.Incidents;
using IncidentBot.Api.Infrastructure;
using IncidentBot.Api.Security;

namespace IncidentBot.Api.Connectors;

public sealed class VictoriaLogsEvidenceConnector(
    IHttpClientFactory httpClientFactory,
    IMcpEvidenceAdapter mcp,
    SafeTemplateRenderer templates,
    EvidenceSourceConfiguration evidenceSources,
    ICredentialProvider credentials) : IIncidentEvidenceConnector
{
    public string Source => EvidenceSourceRegistry.VictoriaLogs;
    public bool SupportsWindowExpansion => true;

    public Task<ConnectorResult> CollectAsync(InvestigationContext context, EvidenceScope scope, CancellationToken cancellationToken)
    {
        var configuration = context.Profile.VictoriaLogs;
        if (configuration is null) return Task.FromResult(ConnectorResult.Excluded(Source));
        var transport = evidenceSources.For(Source);
        return ConnectorUtilities.CollectAsync(
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
            var findings = new List<EvidenceFinding>();
            var timeline = new List<TimelineCandidate>();
            var links = new List<SourceLink>();
            var client = httpClientFactory.CreateClient();
            var streamFilters = JsonSerializer.Serialize(configuration.StreamFilters);
            var streamFilterIdentity = JsonSerializer.Serialize(configuration.StreamFilters
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ThenBy(pair => pair.Value, StringComparer.Ordinal));
            var budget = new ConnectorByteBudget(
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
                    ["start"] = ConnectorUtilities.Iso(scope.Start),
                    ["end"] = ConnectorUtilities.Iso(scope.End),
                    ["extra_stream_filters"] = streamFilters
                };
                var hitsUrl = ConnectorUtilities.Url(transport, "select/logsql/hits");
                var hitsOperation = $"POST /select/logsql/hits ({configuredQuery.Name})";
                var hitsAllowance = budget.BeginOperation(hitsOperation);
                if (hitsAllowance <= 0)
                {
                    budget.RemovePlannedOperation();
                    continue;
                }

                long total;
                try
                {
                    using var hitsRequest = ConnectorUtilities.CreateRequest(
                        HttpMethod.Post, hitsUrl, transport, credentials);
                    hitsRequest.Headers.TryAddWithoutValidation("AccountID", configuration.AccountId);
                    hitsRequest.Headers.TryAddWithoutValidation("ProjectID", configuration.ProjectId);
                    hitsRequest.Content = new FormUrlEncodedContent(common.Concat(new[]
                    {
                        new KeyValuePair<string, string>("step", "60s")
                    }));
                    using var hitsResponse = await client.SendAsync(
                        hitsRequest, HttpCompletionOption.ResponseHeadersRead, ct);
                    using var hitsJson = await ConnectorUtilities.ReadBoundedJsonAsync(
                        hitsResponse,
                        budget.SafeReadLimit(hitsAllowance, hitsResponse.Content),
                        ct,
                        budget.ObserveBytesRead);
                    total = TotalHits(hitsJson.RootElement);
                }
                catch (InvalidOperationException exception) when (ConnectorUtilities.IsByteLimitException(exception))
                {
                    budget.RecordLimited(hitsOperation);
                    budget.RemovePlannedOperation();
                    continue;
                }

                var summary = $"{configuredQuery.Name}: {total} matching log events";
                var severity = total > 0 ? "warning" : "info";
                var link = $"{hitsUrl}?query={Uri.EscapeDataString(query)}&start={Uri.EscapeDataString(ConnectorUtilities.Iso(scope.Start))}&end={Uri.EscapeDataString(ConnectorUtilities.Iso(scope.End))}";
                links.Add(new SourceLink($"VictoriaLogs: {configuredQuery.Name}", link));
                findings.Add(new EvidenceFinding(
                    ConnectorUtilities.Id(
                        Source,
                        "log-count-snapshot",
                        configuration.AccountId,
                        configuration.ProjectId,
                        configuredQuery.Name,
                        query,
                        streamFilterIdentity), Source,
                    scope.End, null, "log-count", severity, summary, null, link, total > 0 ? 0.85 : 0.7,
                    ConnectorUtilities.Provenance("POST /select/logsql/hits", new
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
                    budget.RemovePlannedOperation();
                    continue;
                }

                queriesWithSamples.Add((configuredQuery, query, common, link));
            }

            foreach (var prepared in queriesWithSamples)
            {
                var sampleLimit = Math.Min(transport.MaxItems, 20);
                var sampleExpression = $"{prepared.Query} | fields {string.Join(", ", configuration.Fields)} | sort by (_time) | limit {sampleLimit}";
                var sampleForm = new Dictionary<string, string>(prepared.Common)
                {
                    ["query"] = sampleExpression
                };
                var queryUrl = ConnectorUtilities.Url(transport, "select/logsql/query");
                var sampleOperation = $"POST /select/logsql/query ({prepared.Configuration.Name})";
                var sampleAllowance = budget.BeginOperation(sampleOperation);
                if (sampleAllowance <= 0) continue;
                string sampleText;
                try
                {
                    using var sampleRequest = ConnectorUtilities.CreateRequest(
                        HttpMethod.Post, queryUrl, transport, credentials);
                    sampleRequest.Headers.TryAddWithoutValidation("AccountID", configuration.AccountId);
                    sampleRequest.Headers.TryAddWithoutValidation("ProjectID", configuration.ProjectId);
                    sampleRequest.Content = new FormUrlEncodedContent(sampleForm);
                    using var sampleResponse = await client.SendAsync(
                        sampleRequest, HttpCompletionOption.ResponseHeadersRead, ct);
                    sampleText = await ConnectorUtilities.ReadBoundedTextAsync(
                        sampleResponse,
                        budget.SafeReadLimit(sampleAllowance, sampleResponse.Content),
                        ct,
                        budget.ObserveBytesRead);
                }
                catch (InvalidOperationException exception) when (ConnectorUtilities.IsByteLimitException(exception))
                {
                    budget.RecordLimited(sampleOperation);
                    continue;
                }

                var lines = sampleText.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Distinct(StringComparer.Ordinal)
                    .Take(sampleLimit)
                    .ToList();
                for (var index = 0; index < lines.Count; index++)
                {
                    var line = lines[index];
                    using var sample = JsonDocument.Parse(line);
                    var at = ConnectorUtilities.Timestamp(sample.RootElement, "_time", scope.End);
                    var sourceTimestamp = ConnectorUtilities.Text(sample.RootElement, "_time", "");
                    var message = Redact(ConnectorUtilities.Text(sample.RootElement, "_msg", line), configuration.RedactPatterns);
                    var isFirst = index == 0;
                    var findingSummary = isFirst
                        ? $"First observed {prepared.Configuration.Name} in the investigation window: {ConnectorUtilities.Truncate(message, 200)}"
                        : ConnectorUtilities.Truncate(message, 240);
                    findings.Add(new EvidenceFinding(
                        ConnectorUtilities.Id(
                            Source,
                            "log-event",
                            configuration.AccountId,
                            configuration.ProjectId,
                            prepared.Configuration.Name,
                            sourceTimestamp,
                            message),
                        Source, at, null, isFirst ? "first-error" : "log-sample", "warning", findingSummary,
                        ConnectorUtilities.Truncate(Redact(line, configuration.RedactPatterns), 1200), prepared.Link, 0.8,
                        ConnectorUtilities.Provenance("POST /select/logsql/query", new
                        {
                            prepared.Configuration.Name,
                            order = "_time ascending",
                            exactWindowStart = scope.Start,
                            exactWindowEnd = scope.End
                        }), ObjectType: "log-query", ObjectId: prepared.Configuration.Name));
                    if (isFirst)
                    {
                        timeline.Add(new TimelineCandidate(at, Source, "first-error", findingSummary, "warning", prepared.Link,
                            ObjectType: "log-query", ObjectId: prepared.Configuration.Name));
                    }
                }
            }

            var itemLimit = Math.Min(
                Math.Max(0, scope.MaxItems),
                Math.Max(0, transport.MaxItems));
            var rankedFindings = EvidenceRankingPolicy.Rank(findings, context.TriggeredAt);
            var orderedTimeline = timeline.OrderBy(item => item.OccurredAt).ToList();
            var distinctLinks = links.Distinct().ToList();
            var itemsTruncated = rankedFindings.Count > itemLimit
                                 || orderedTimeline.Count > itemLimit
                                 || distinctLinks.Count > itemLimit;
            var diagnostic = ConnectorUtilities.CombineDiagnostics(
                budget.Diagnostic,
                itemsTruncated ? $"Source item limit {itemLimit} truncated findings, timeline entries, or links." : null);
            return new ConnectorResult(
                Source,
                budget.IsPartial || itemsTruncated ? SourceHealth.Partial : SourceHealth.Complete,
                rankedFindings.Take(itemLimit).ToList(),
                orderedTimeline.Take(itemLimit).ToList(),
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

    private static string Redact(string value, IEnumerable<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            value = Regex.Replace(value, pattern, "[REDACTED]", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        }
        return value;
    }
}
