using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using IncidentBot.Api.Domain;
using IncidentBot.Api.Incidents;
using IncidentBot.Api.Infrastructure;
using IncidentBot.Api.Security;

namespace IncidentBot.Api.Connectors;

public sealed class GrafanaEvidenceConnector(
    IHttpClientFactory httpClientFactory,
    IMcpEvidenceAdapter mcp,
    SafeTemplateRenderer templates,
    EvidenceSourceConfiguration evidenceSources,
    ICredentialProvider credentials) : IIncidentEvidenceConnector
{
    public string Source => EvidenceSourceRegistry.Grafana;
    public bool SupportsWindowExpansion => true;

    public Task<ConnectorResult> CollectAsync(InvestigationContext context, EvidenceScope scope, CancellationToken cancellationToken)
    {
        var configuration = context.Profile.Grafana;
        if (configuration is null) return Task.FromResult(ConnectorResult.Excluded(Source));
        var transport = evidenceSources.For(Source);
        return ConnectorUtilities.CollectAsync(
            Source, transport, mcp, context, scope,
            new
            {
                configuration.OrganizationId,
                configuration.Dashboards,
                configuration.Queries,
                configuration.AnnotationTags
            }, async ct =>
        {
            var findings = new List<EvidenceFinding>();
            var timeline = new List<TimelineCandidate>();
            var links = new List<SourceLink>();
            var client = httpClientFactory.CreateClient();
            var fromMilliseconds = scope.Start.ToUnixTimeMilliseconds();
            var toMilliseconds = scope.End.ToUnixTimeMilliseconds();
            var budget = new ConnectorByteBudget(
                scope.MaxBytes,
                transport.MaxBytes,
                1 + configuration.Queries.Count);

            foreach (var dashboard in configuration.Dashboards)
            {
                var baseLink = $"{transport.BaseUrl.TrimEnd('/')}/d/{Uri.EscapeDataString(dashboard.Uid)}?from={fromMilliseconds}&to={toMilliseconds}";
                links.Add(new SourceLink($"Grafana dashboard {dashboard.Uid}", baseLink));
                foreach (var panelId in dashboard.PanelIds)
                {
                    links.Add(new SourceLink($"Grafana {dashboard.Uid} panel {panelId}", $"{baseLink}&viewPanel={panelId}"));
                }
            }

            var annotationParameters = new List<string>
            {
                $"from={fromMilliseconds}", $"to={toMilliseconds}", $"limit={Math.Min(scope.MaxItems, transport.MaxItems)}"
            };
            annotationParameters.AddRange(configuration.AnnotationTags.Select(tag => $"tags={Uri.EscapeDataString(tag)}"));
            var annotationsUrl = ConnectorUtilities.Url(transport, $"api/annotations?{string.Join('&', annotationParameters)}");
            const string annotationOperation = "GET /api/annotations";
            var annotationAllowance = budget.BeginOperation(annotationOperation);
            if (annotationAllowance > 0)
            {
                try
                {
                    using var annotationRequest = ConnectorUtilities.CreateRequest(
                        HttpMethod.Get, annotationsUrl, transport, credentials);
                    annotationRequest.Headers.TryAddWithoutValidation(
                        "X-Grafana-Org-Id", configuration.OrganizationId.ToString());
                    using var response = await client.SendAsync(
                        annotationRequest, HttpCompletionOption.ResponseHeadersRead, ct);
                    using var json = await ConnectorUtilities.ReadBoundedJsonAsync(
                        response,
                        budget.SafeReadLimit(annotationAllowance, response.Content),
                        ct,
                        budget.ObserveBytesRead);
                    foreach (var annotation in json.RootElement.EnumerateArray())
                    {
                        var at = ConnectorUtilities.Timestamp(annotation, "time", scope.End);
                        var text = ConnectorUtilities.Text(annotation, "text", "Grafana annotation");
                        var url = ConnectorUtilities.Text(annotation, "url", annotationsUrl);
                        findings.Add(new EvidenceFinding(
                            ConnectorUtilities.Id(Source, "annotation", at.ToUnixTimeMilliseconds().ToString(), text), Source, at, null,
                            "annotation", "info", text, null, url, 0.85,
                            ConnectorUtilities.Provenance("GET /api/annotations", new { configuration.AnnotationTags })));
                        timeline.Add(new TimelineCandidate(at, Source, "annotation", text, "info", url));
                    }
                }
                catch (InvalidOperationException exception) when (ConnectorUtilities.IsByteLimitException(exception))
                {
                    budget.RecordLimited(annotationOperation);
                }
            }

            var refIndex = 0;
            foreach (var query in configuration.Queries)
            {
                var expression = templates.Render(query.Expression, context.Labels);
                var queryBody = new
                {
                    from = fromMilliseconds.ToString(),
                    to = toMilliseconds.ToString(),
                    queries = new[]
                    {
                        new
                        {
                            refId = ((char)('A' + refIndex++ % 26)).ToString(),
                            datasource = new { uid = query.DatasourceUid },
                            expr = expression,
                            format = "time_series",
                            intervalMs = 15000,
                            maxDataPoints = 240
                        }
                    }
                };
                var queryUrl = ConnectorUtilities.Url(transport, "api/ds/query");
                var operation = $"POST /api/ds/query ({query.Name})";
                var allowance = budget.BeginOperation(operation);
                if (allowance <= 0) continue;
                try
                {
                    using var request = ConnectorUtilities.CreateRequest(HttpMethod.Post, queryUrl, transport, credentials);
                    request.Headers.TryAddWithoutValidation("X-Grafana-Org-Id", configuration.OrganizationId.ToString());
                    request.Content = JsonContent.Create(queryBody);
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                    using var json = await ConnectorUtilities.ReadBoundedJsonAsync(
                        response,
                        budget.SafeReadLimit(allowance, response.Content),
                        ct,
                        budget.ObserveBytesRead);
                    var values = MetricValues(json.RootElement).Take(10000).ToList();
                    var max = values.Count == 0 ? (double?)null : values.Max();
                    var warning = max.HasValue && query.WarningAbove.HasValue && max.Value > query.WarningAbove.Value;
                    var summary = max.HasValue
                        ? $"{query.Name}: maximum observed value {max.Value:0.###}"
                        : $"{query.Name}: query returned no numeric samples";
                    findings.Add(new EvidenceFinding(
                        ConnectorUtilities.Id(
                            Source,
                            "metric-snapshot",
                            configuration.OrganizationId.ToString(CultureInfo.InvariantCulture),
                            query.DatasourceUid,
                            query.Name,
                            expression),
                        Source, scope.End, null, "metric", warning ? "warning" : "info", summary,
                        ConnectorUtilities.Truncate(json.RootElement.ToString(), 1200), queryUrl, warning ? 0.9 : 0.7,
                        ConnectorUtilities.Provenance("POST /api/ds/query", new
                        {
                            query.Name,
                            query.DatasourceUid,
                            maximumObservedValue = max,
                            query.WarningAbove,
                            exactWindowStart = scope.Start,
                            exactWindowEnd = scope.End
                        }),
                        ObjectType: "metric-query",
                        ObjectId: $"{query.DatasourceUid}:{query.Name}"));
                }
                catch (InvalidOperationException exception) when (ConnectorUtilities.IsByteLimitException(exception))
                {
                    budget.RecordLimited(operation);
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

    private static IEnumerable<double> MetricValues(JsonElement root)
    {
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var result in results.EnumerateObject())
        {
            if (!result.Value.TryGetProperty("frames", out var frames) || frames.ValueKind != JsonValueKind.Array) continue;
            foreach (var frame in frames.EnumerateArray())
            {
                if (!frame.TryGetProperty("schema", out var schema)
                    || !schema.TryGetProperty("fields", out var fields)
                    || !frame.TryGetProperty("data", out var data)
                    || !data.TryGetProperty("values", out var values)) continue;
                var fieldArray = fields.EnumerateArray().ToArray();
                var valueArray = values.EnumerateArray().ToArray();
                for (var index = 0; index < Math.Min(fieldArray.Length, valueArray.Length); index++)
                {
                    if (ConnectorUtilities.Text(fieldArray[index], "type") != "number") continue;
                    foreach (var value in valueArray[index].EnumerateArray())
                    {
                        if (value.TryGetDouble(out var number)) yield return number;
                    }
                }
            }
        }
    }
}
