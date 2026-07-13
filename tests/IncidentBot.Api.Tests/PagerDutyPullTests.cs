using System.Net;
using System.Text;
using IncidentBot.Api.Connectors;
using IncidentBot.Api.Domain;
using IncidentBot.Api.Incidents;
using IncidentBot.Api.Options;
using Microsoft.Extensions.Options;

namespace IncidentBot.Api.Tests;

public sealed class PagerDutyPullTests
{
    [Fact]
    public async Task RecentQueryRequestsEveryLifecycleStateAndMapsResponderFields()
    {
        var requests = new List<HttpRequestMessage>();
        var tokenEnvironmentVariable = $"INCIDENTBOT_PD_TEST_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(tokenEnvironmentVariable, "test-token");
        try
        {
            var client = CreateClient(tokenEnvironmentVariable, new DelegateHandler(request =>
            {
                requests.Add(Clone(request));
                return Json("""
                    {
                      "more": true,
                      "incidents": [{
                        "id": "PINCIDENT",
                        "incident_number": 42,
                        "title": "Checkout latency",
                        "status": "acknowledged",
                        "urgency": "high",
                        "created_at": "2026-07-13T08:00:00Z",
                        "last_status_change_at": "2026-07-13T08:05:00Z",
                        "html_url": "https://example.pagerduty.com/incidents/PINCIDENT",
                        "service": { "id": "PSERVICE", "summary": "Checkout API" },
                        "assignments": [
                          { "assignee": { "summary": "Alex Chen" } },
                          { "assignee": { "summary": "Alex Chen" } }
                        ]
                      }]
                    }
                    """);
            }));

            var result = await client.GetRecentAsync(
                DateTimeOffset.Parse("2026-07-13T07:00:00Z"),
                DateTimeOffset.Parse("2026-07-13T09:00:00Z"),
                CancellationToken.None);

            Assert.True(result.HasMore);
            var incident = Assert.Single(result.Incidents);
            Assert.Equal("PINCIDENT", incident.Id);
            Assert.Equal(42, incident.IncidentNumber);
            Assert.Equal("Checkout API", incident.ServiceName);
            Assert.Equal(["Alex Chen"], incident.Assignees);
            var request = Assert.Single(requests);
            Assert.Equal("Token token=test-token", request.Headers.Authorization?.ToString());
            Assert.Contains("statuses%5B%5D=triggered", request.RequestUri!.Query, StringComparison.Ordinal);
            Assert.Contains("statuses%5B%5D=acknowledged", request.RequestUri.Query, StringComparison.Ordinal);
            Assert.Contains("statuses%5B%5D=resolved", request.RequestUri.Query, StringComparison.Ordinal);
            Assert.Contains("sort_by=created_at%3Adesc", request.RequestUri.Query, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(tokenEnvironmentVariable, null);
            foreach (var request in requests) request.Dispose();
        }
    }

    [Fact]
    public async Task TriggerLookupMergesBoundedAlertDetailsIntoProfileLabels()
    {
        var tokenEnvironmentVariable = $"INCIDENTBOT_PD_TEST_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(tokenEnvironmentVariable, "test-token");
        try
        {
            var client = CreateClient(tokenEnvironmentVariable, new DelegateHandler(request =>
                request.RequestUri!.AbsolutePath.EndsWith("/alerts", StringComparison.Ordinal)
                    ? Json("""
                        { "alerts": [{
                          "alert_rule": { "id": "rule-5" },
                          "body": { "details": { "environment": "production", "cluster": "eu-west" } }
                        }] }
                        """)
                    : Json("""
                        { "incident": {
                          "id": "PINCIDENT",
                          "incident_number": 42,
                          "title": "Checkout latency",
                          "status": "triggered",
                          "urgency": "high",
                          "created_at": "2026-07-13T08:00:00Z",
                          "last_status_change_at": "2026-07-13T08:05:00Z",
                          "service": { "id": "PSERVICE", "summary": "Checkout API" }
                        } }
                        """)));

            var incident = await client.GetAsync("PINCIDENT", CancellationToken.None);

            Assert.NotNull(incident);
            Assert.Equal("PSERVICE", incident.Labels["service"]);
            Assert.Equal("production", incident.Labels["environment"]);
            Assert.Equal("eu-west", incident.Labels["cluster"]);
            Assert.Equal("rule-5", incident.Labels["alert_rule_id"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(tokenEnvironmentVariable, null);
        }
    }

    [Theory]
    [InlineData("triggered", "incident.triggered")]
    [InlineData("acknowledged", "incident.acknowledged")]
    [InlineData("resolved", "incident.resolved")]
    public void PulledStatusUsesTheExistingWebhookLifecycle(string status, string eventType)
    {
        Assert.Equal(eventType, PagerDutyPullService.EventType(status));
    }

    [Fact]
    public void ResolvedPullPreservesIncidentStartAndResolutionTimes()
    {
        var createdAt = DateTimeOffset.Parse("2026-07-13T08:00:00Z");
        var resolvedAt = DateTimeOffset.Parse("2026-07-13T08:25:00Z");
        var incident = new PagerDutyIncidentSnapshot(
            "PINCIDENT",
            42,
            "Checkout latency",
            "resolved",
            "high",
            createdAt,
            resolvedAt,
            "PSERVICE",
            "Checkout API",
            [],
            "https://example.pagerduty.com/incidents/PINCIDENT",
            new Dictionary<string, string> { ["service"] = "PSERVICE" });

        var webhook = PagerDutyPullService.CreateWebhook(incident, incident.Labels);

        Assert.StartsWith("pagerduty-pull:v2:", webhook.EventId, StringComparison.Ordinal);
        Assert.Equal("incident.resolved", webhook.EventType);
        Assert.Equal(createdAt, webhook.TriggeredAt);
        Assert.Equal(resolvedAt, webhook.OccurredAt);
    }

    private static PagerDutyIncidentClient CreateClient(string credentialEnvironmentVariable, HttpMessageHandler handler) =>
        new(
            new StubHttpClientFactory(handler),
            Microsoft.Extensions.Options.Options.Create(new PagerDutyOptions()),
            TestConfiguration.EvidenceSources(pagerDuty: new ConnectorTransport
            {
                BaseUrl = "https://api.pagerduty.test",
                CredentialEnv = credentialEnvironmentVariable
            }),
            TestConfiguration.Credentials((credentialEnvironmentVariable, "test-token")));

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private static HttpRequestMessage Clone(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        return clone;
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
}
