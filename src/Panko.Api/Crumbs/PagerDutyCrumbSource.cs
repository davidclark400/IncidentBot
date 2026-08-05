using System.Text.Json;
using System.Net.Http.Headers;
using Panko.Api.Domain;
using Panko.Api.Infrastructure;

namespace Panko.Api.Crumbs;

public sealed class PagerDutyCrumbSource(
    IHttpClientFactory httpClientFactory,
    IMcpCrumbSourceAdapter mcp,
    CrumbSourceConfiguration crumbSources,
    ICredentialProvider credentials) : ICrumbSourceAdapter
{
    public string Source => CrumbSourceRegistry.PagerDuty;
    public bool SupportsWindowExpansion => false;

    public Task<CrumbSourceResult> CollectAsync(CaseContext context, CrumbScope scope, CancellationToken cancellationToken)
    {
        var configuration = context.Recipe.PagerDuty;
        if (configuration is null || string.IsNullOrWhiteSpace(context.PagerDutyIncidentId))
        {
            return Task.FromResult(CrumbSourceResult.Excluded(Source));
        }
        var pagerDutyIncidentId = context.PagerDutyIncidentId;
        var transport = crumbSources.For(Source);
        return CrumbSourceUtilities.CollectAsync(
            Source, transport, mcp, context, scope,
            new { pagerDutyIncidentId }, async ct =>
        {
            var budget = new CrumbSourceResponseBudget(scope.MaxBytes, transport.MaxBytes, 1);
            const string operation = "GET /incidents/{id}";
            var json = await budget.TryReadJsonAsync(
                operation,
                async operationCancellationToken =>
                {
                    using var request = CrumbSourceUtilities.CreateRequest(
                        HttpMethod.Get,
                        CrumbSourceUtilities.Url(
                            transport,
                            $"incidents/{Uri.EscapeDataString(pagerDutyIncidentId)}"),
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
                return new CrumbSourceResult(
                    Source, CrumbSourceHealth.Partial, [], [], [], 0, budget.Diagnostic);
            }

            using (json)
            {
                var incident = json.RootElement.TryGetProperty("incident", out var wrapped) ? wrapped : json.RootElement;
                var triggeredAt = CrumbSourceUtilities.Timestamp(incident, "created_at", context.OpenedAt);
                var statusChangedAt = CrumbSourceUtilities.Timestamp(incident, "last_status_change_at", triggeredAt);
                var status = CrumbSourceUtilities.Text(incident, "status").ToLowerInvariant();
                var incidentSeverity = CrumbSourceUtilities.Text(incident, "urgency") == "high"
                    ? "critical"
                    : "warning";
                var url = CrumbSourceUtilities.Text(incident, "html_url", "");
                var crumb = new Crumb(
                    CrumbSourceUtilities.Id(Source, "pagerduty-incident", pagerDutyIncidentId), Source, statusChangedAt, null,
                    "pagerduty-incident", status == "triggered" ? "critical" : "info",
                    $"PagerDuty incident is {status}", null, string.IsNullOrWhiteSpace(url) ? null : url, 1,
                    CrumbSourceUtilities.Provenance("GET /incidents/{id}", new { pagerDutyIncidentId }));
                var trail = new List<TrailCandidate>
                {
                    new(triggeredAt, Source, "pagerduty-incident-triggered", "PagerDuty incident triggered",
                        incidentSeverity, crumb.Url)
                };
                if (status != "triggered")
                {
                    trail.Add(new TrailCandidate(
                        statusChangedAt,
                        Source,
                        "pagerduty-incident-state",
                        $"PagerDuty incident {status}",
                        "info",
                        crumb.Url));
                }
                var itemLimit = Math.Min(
                    Math.Max(0, scope.MaxItems),
                    Math.Max(0, transport.MaxItems));
                var selectedTrail = trail
                    .Skip(Math.Max(0, trail.Count - itemLimit))
                    .ToList();
                var itemsTruncated = itemLimit < trail.Count;
                return new CrumbSourceResult(
                    Source,
                    itemsTruncated ? CrumbSourceHealth.Partial : CrumbSourceHealth.Complete,
                    itemLimit > 0 ? [crumb] : [],
                    selectedTrail,
                    itemLimit > 0 && crumb.Url is not null
                        ? [new SourceLink("PagerDuty incident", crumb.Url)]
                        : [],
                    0,
                    itemsTruncated
                        ? CrumbSourceUtilities.CombineDiagnostics(
                            $"Source item limit {itemLimit} truncated Crumbs, Trail entries, or links.")
                        : null);
            }
        }, cancellationToken);
    }
}
