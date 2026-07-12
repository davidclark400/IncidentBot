using System.Text.Json;
using System.Net.Http.Headers;
using IncidentBot.Api.Domain;

namespace IncidentBot.Api.Connectors;

public sealed class PagerDutyEvidenceConnector(
    IHttpClientFactory httpClientFactory,
    IMcpEvidenceAdapter mcp) : IIncidentEvidenceConnector
{
    public string Source => EvidenceSourceRegistry.PagerDuty;

    public Task<ConnectorResult> CollectAsync(InvestigationContext context, EvidenceScope scope, CancellationToken cancellationToken)
    {
        var configuration = context.Profile.PagerDuty;
        if (configuration is null) return Task.FromResult(ConnectorResult.Excluded(Source));
        return ConnectorUtilities.CollectAsync(
            Source, configuration.Connector, mcp, context, scope,
            new { incidentId = context.PagerDutyIncidentId }, async ct =>
        {
            var budget = new ConnectorByteBudget(scope.MaxBytes, configuration.Connector.MaxBytes, 1);
            const string operation = "GET /incidents/{id}";
            var allowance = budget.BeginOperation(operation);
            if (allowance <= 0)
            {
                return new ConnectorResult(
                    Source, SourceHealth.Partial, [], [], [], 0, budget.Diagnostic);
            }

            using var request = ConnectorUtilities.CreateRequest(
                HttpMethod.Get,
                ConnectorUtilities.Url(configuration.Connector, $"incidents/{Uri.EscapeDataString(context.PagerDutyIncidentId)}"),
                configuration.Connector);
            var token = Environment.GetEnvironmentVariable(configuration.Connector.CredentialEnv);
            if (!string.IsNullOrWhiteSpace(token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Token", $"token={token}");
            }
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.pagerduty+json;version=2");
            using var response = await httpClientFactory.CreateClient().SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);
            JsonDocument json;
            try
            {
                json = await ConnectorUtilities.ReadBoundedJsonAsync(
                    response,
                    budget.SafeReadLimit(allowance, response.Content),
                    ct,
                    budget.ObserveBytesRead);
            }
            catch (InvalidOperationException exception) when (ConnectorUtilities.IsByteLimitException(exception))
            {
                budget.RecordLimited(operation);
                return new ConnectorResult(
                    Source, SourceHealth.Partial, [], [], [], 0, budget.Diagnostic);
            }

            using (json)
            {
                var incident = json.RootElement.TryGetProperty("incident", out var wrapped) ? wrapped : json.RootElement;
                var occurred = ConnectorUtilities.Timestamp(incident, "created_at", context.TriggeredAt);
                var status = ConnectorUtilities.Text(incident, "status");
                var url = ConnectorUtilities.Text(incident, "html_url", "");
                var finding = new EvidenceFinding(
                    ConnectorUtilities.Id(Source, "incident", context.PagerDutyIncidentId), Source, occurred, null,
                    "incident", status == "triggered" ? "critical" : "info",
                    $"PagerDuty incident is {status}", null, string.IsNullOrWhiteSpace(url) ? null : url, 1,
                    ConnectorUtilities.Provenance("GET /incidents/{id}", new { incidentId = context.PagerDutyIncidentId }));
                var itemLimit = Math.Min(
                    Math.Max(0, scope.MaxItems),
                    Math.Max(0, configuration.Connector.MaxItems));
                var itemsTruncated = itemLimit < 1;
                return new ConnectorResult(
                    Source,
                    itemsTruncated ? SourceHealth.Partial : SourceHealth.Complete,
                    itemLimit > 0 ? [finding] : [],
                    itemLimit > 0
                        ? [new TimelineCandidate(occurred, Source, "pagerduty", finding.Summary, finding.Severity, finding.Url)]
                        : [],
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
