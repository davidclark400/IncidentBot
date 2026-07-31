using System.Text.Json;
using System.Net.Http.Headers;
using IncidentBot.Api.Domain;
using IncidentBot.Api.Infrastructure;

namespace IncidentBot.Api.Connectors;

public sealed class PagerDutyEvidenceConnector(
    IHttpClientFactory httpClientFactory,
    IMcpEvidenceAdapter mcp,
    EvidenceSourceConfiguration evidenceSources,
    ICredentialProvider credentials) : IIncidentEvidenceConnector
{
    public string Source => EvidenceSourceRegistry.PagerDuty;
    public bool SupportsWindowExpansion => false;

    public Task<ConnectorResult> CollectAsync(InvestigationContext context, EvidenceScope scope, CancellationToken cancellationToken)
    {
        var configuration = context.Profile.PagerDuty;
        if (configuration is null) return Task.FromResult(ConnectorResult.Excluded(Source));
        var transport = evidenceSources.For(Source);
        return ConnectorUtilities.CollectAsync(
            Source, transport, mcp, context, scope,
            new { incidentId = context.PagerDutyIncidentId }, async ct =>
        {
            var budget = new ConnectorResponseBudget(scope.MaxBytes, transport.MaxBytes, 1);
            const string operation = "GET /incidents/{id}";
            var json = await budget.TryReadJsonAsync(
                operation,
                async operationCancellationToken =>
                {
                    using var request = ConnectorUtilities.CreateRequest(
                        HttpMethod.Get,
                        ConnectorUtilities.Url(
                            transport,
                            $"incidents/{Uri.EscapeDataString(context.PagerDutyIncidentId)}"),
                        transport,
                        credentials);
                    var token = credentials.Get(transport.CredentialEnv);
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        request.Headers.Authorization = new AuthenticationHeaderValue("Token", $"token={token}");
                    }
                    request.Headers.TryAddWithoutValidation(
                        "Accept", "application/vnd.pagerduty+json;version=2");
                    return await httpClientFactory.CreateClient().SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        operationCancellationToken);
                },
                ct);
            if (json is null)
            {
                return new ConnectorResult(
                    Source, SourceHealth.Partial, [], [], [], 0, budget.Diagnostic);
            }

            using (json)
            {
                var incident = json.RootElement.TryGetProperty("incident", out var wrapped) ? wrapped : json.RootElement;
                var triggeredAt = ConnectorUtilities.Timestamp(incident, "created_at", context.TriggeredAt);
                var statusChangedAt = ConnectorUtilities.Timestamp(incident, "last_status_change_at", triggeredAt);
                var status = ConnectorUtilities.Text(incident, "status").ToLowerInvariant();
                var incidentSeverity = ConnectorUtilities.Text(incident, "urgency") == "high"
                    ? "critical"
                    : "warning";
                var url = ConnectorUtilities.Text(incident, "html_url", "");
                var finding = new EvidenceFinding(
                    ConnectorUtilities.Id(Source, "incident", context.PagerDutyIncidentId), Source, statusChangedAt, null,
                    "incident", status == "triggered" ? "critical" : "info",
                    $"PagerDuty incident is {status}", null, string.IsNullOrWhiteSpace(url) ? null : url, 1,
                    ConnectorUtilities.Provenance("GET /incidents/{id}", new { incidentId = context.PagerDutyIncidentId }));
                var timeline = new List<TimelineCandidate>
                {
                    new(triggeredAt, Source, "incident-triggered", "PagerDuty incident triggered",
                        incidentSeverity, finding.Url)
                };
                if (status != "triggered")
                {
                    timeline.Add(new TimelineCandidate(
                        statusChangedAt,
                        Source,
                        "incident-state",
                        $"PagerDuty incident {status}",
                        "info",
                        finding.Url));
                }
                var itemLimit = Math.Min(
                    Math.Max(0, scope.MaxItems),
                    Math.Max(0, transport.MaxItems));
                var selectedTimeline = timeline
                    .Skip(Math.Max(0, timeline.Count - itemLimit))
                    .ToList();
                var itemsTruncated = itemLimit < timeline.Count;
                return new ConnectorResult(
                    Source,
                    itemsTruncated ? SourceHealth.Partial : SourceHealth.Complete,
                    itemLimit > 0 ? [finding] : [],
                    selectedTimeline,
                    itemLimit > 0 && finding.Url is not null
                        ? [new SourceLink("PagerDuty incident", finding.Url)]
                        : [],
                    0,
                    itemsTruncated
                        ? ConnectorUtilities.CombineDiagnostics(
                            $"Source item limit {itemLimit} truncated findings, timeline entries, or links.")
                        : null);
            }
        }, cancellationToken);
    }
}
