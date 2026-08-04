using System.Net;
using System.Text;
using Panko.Api.Crumbs;
using Panko.Api.Domain;
using Panko.Api.Cases;
using Panko.Api.Cases;
using Panko.Api.Options;
using Panko.Api.Recipes;
using Panko.Api.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace Panko.Api.Tests;

public sealed class PagerDutyPullTests
{
    [Fact]
    public async Task RecentQueryScopesPagerDutyRequestAndMapsResponderFields()
    {
        var requests = new List<HttpRequestMessage>();
        var tokenEnvironmentVariable = $"PANKO_PD_TEST_{Guid.NewGuid():N}";
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
                ["PSERVICE", "PSECOND"],
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
            Assert.Contains("service_ids%5B%5D=PSERVICE", request.RequestUri.Query, StringComparison.Ordinal);
            Assert.Contains("service_ids%5B%5D=PSECOND", request.RequestUri.Query, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(tokenEnvironmentVariable, null);
            foreach (var request in requests) request.Dispose();
        }
    }

    [Fact]
    public async Task RecentQueryPostFiltersUnexpectedIncidentsOutsideTheAuthorizedServices()
    {
        var client = CreateClient("PAGERDUTY_TEST_TOKEN", new DelegateHandler(_ => Json("""
            {
              "more": false,
              "incidents": [
                {
                  "id": "PAUTHORIZED",
                  "incident_number": 42,
                  "title": "Checkout latency",
                  "status": "triggered",
                  "urgency": "high",
                  "created_at": "2026-07-13T08:00:00Z",
                  "last_status_change_at": "2026-07-13T08:05:00Z",
                  "service": { "id": "PSERVICE", "summary": "Checkout API" }
                },
                {
                  "id": "POTHERTEAM",
                  "incident_number": 99,
                  "title": "Search latency",
                  "status": "triggered",
                  "urgency": "high",
                  "created_at": "2026-07-13T08:00:00Z",
                  "last_status_change_at": "2026-07-13T08:05:00Z",
                  "service": { "id": "PSEARCH", "summary": "Search API" }
                }
              ]
            }
            """)));

        var result = await client.GetRecentAsync(
            DateTimeOffset.Parse("2026-07-13T07:00:00Z"),
            DateTimeOffset.Parse("2026-07-13T09:00:00Z"),
            ["PSERVICE"],
            CancellationToken.None);

        var incident = Assert.Single(result.Incidents);
        Assert.Equal("PAUTHORIZED", incident.Id);
        Assert.Equal("PSERVICE", incident.ServiceId);
    }

    [Fact]
    public async Task EmptyAuthorizedServiceScopeFailsClosedWithoutCallingPagerDuty()
    {
        var requests = 0;
        using var recipes = new RecipeFixture();
        var admission = new RecordingCaseAdmission();
        var service = new PagerDutyPullService(
            CreateClient("PAGERDUTY_TEST_TOKEN", new DelegateHandler(_ =>
            {
                requests++;
                return Json("""{ "more": false, "incidents": [] }""");
            })),
            recipes.Store,
            new PagerDutyCaseAdapter(recipes.Store, admission));

        var result = await service.GetRecentAsync(
            DateTimeOffset.Parse("2026-07-13T07:00:00Z"),
            DateTimeOffset.Parse("2026-07-13T09:00:00Z"),
            [],
            CancellationToken.None);

        Assert.False(result.HasMore);
        Assert.Empty(result.Incidents);
        Assert.Equal(0, requests);
        Assert.Equal(0, admission.Calls);
    }

    [Fact]
    public async Task TriggerLookupMergesBoundedAlertDetailsIntoRecipeLabels()
    {
        var tokenEnvironmentVariable = $"PANKO_PD_TEST_{Guid.NewGuid():N}";
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

    [Fact]
    public async Task TriggerMapsRecipeSelectionFailureToConflict()
    {
        using var recipes = new RecipeFixture();
        var selectionFailure = new RecipeSelectionException(
            "No Recipe selector matched PagerDuty service 'PSERVICE'.",
            new InvalidOperationException("Recipe selection failed"));
        var service = new PagerDutyPullService(
            CreateClient("PAGERDUTY_TEST_TOKEN", new DelegateHandler(TriggerLookupResponse)),
            recipes.Store,
            new PagerDutyCaseAdapter(
                recipes.Store,
                new ThrowingCaseAdmission(selectionFailure)));

        var exception = await Assert.ThrowsAsync<PagerDutyPullException>(() =>
            service.TriggerAsync(
                "PINCIDENT",
                TeamAccessScope.Restricted(["payments"]),
                CancellationToken.None));

        Assert.Equal(StatusCodes.Status409Conflict, exception.StatusCode);
    }

    [Fact]
    public async Task TriggerDoesNotMapOtherAdmissionInvalidOperationToConflict()
    {
        using var recipes = new RecipeFixture();
        var persistenceFailure = new InvalidOperationException("Case upsert failed");
        var service = new PagerDutyPullService(
            CreateClient("PAGERDUTY_TEST_TOKEN", new DelegateHandler(TriggerLookupResponse)),
            recipes.Store,
            new PagerDutyCaseAdapter(
                recipes.Store,
                new ThrowingCaseAdmission(persistenceFailure)));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.TriggerAsync(
                "PINCIDENT",
                TeamAccessScope.Restricted(["payments"]),
                CancellationToken.None));

        Assert.Same(persistenceFailure, exception);
    }

    [Fact]
    public async Task FullTriggerLookupDeniesAnIncidentOwnedByAnotherTeamBeforeAdmission()
    {
        var requests = new List<string>();
        using var recipes = new RecipeFixture();
        var admission = new RecordingCaseAdmission();
        var service = new PagerDutyPullService(
            CreateClient("PAGERDUTY_TEST_TOKEN", new DelegateHandler(request =>
            {
                requests.Add(request.RequestUri!.AbsolutePath);
                return TriggerLookupResponse(request, "PSEARCH");
            })),
            recipes.Store,
            new PagerDutyCaseAdapter(recipes.Store, admission));

        var result = await service.TriggerAsync(
            "PINCIDENT",
            TeamAccessScope.Restricted(["payments"]),
            CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, admission.Calls);
        Assert.Equal(
            ["/incidents/PINCIDENT", "/incidents/PINCIDENT/alerts"],
            requests);
    }

    private static PagerDutyIncidentClient CreateClient(string credentialEnvironmentVariable, HttpMessageHandler handler) =>
        new(
            new StubHttpClientFactory(handler),
            Microsoft.Extensions.Options.Options.Create(new PagerDutyOptions()),
            TestConfiguration.CrumbSources(pagerDuty: new ConnectorTransport
            {
                BaseUrl = "https://api.pagerduty.test",
                CredentialEnv = credentialEnvironmentVariable
            }),
            TestConfiguration.Credentials((credentialEnvironmentVariable, "test-token")));

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage TriggerLookupResponse(HttpRequestMessage request) =>
        TriggerLookupResponse(request, "PSERVICE");

    private static HttpResponseMessage TriggerLookupResponse(
        HttpRequestMessage request,
        string serviceId) =>
        request.RequestUri!.AbsolutePath.EndsWith("/alerts", StringComparison.Ordinal)
            ? Json("""{ "alerts": [] }""")
            : Json($$"""
                { "incident": {
                  "id": "PINCIDENT",
                  "incident_number": 42,
                  "title": "Checkout latency",
                  "status": "triggered",
                  "urgency": "high",
                  "created_at": "2026-07-13T08:00:00Z",
                  "last_status_change_at": "2026-07-13T08:05:00Z",
                  "service": { "id": "{{serviceId}}", "summary": "Checkout API" }
                } }
                """);

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

    private sealed class ThrowingCaseAdmission(Exception exception) : ICaseAdmission
    {
        public Task<(Guid CaseId, bool IsDuplicate)> AcceptAsync(
            AcceptCaseOriginEvent originEvent,
            CaseOriginEventReceipt receipt,
            CancellationToken cancellationToken) =>
            Task.FromException<(Guid CaseId, bool IsDuplicate)>(exception);
    }

    private sealed class RecordingCaseAdmission : ICaseAdmission
    {
        public int Calls { get; private set; }

        public Task<(Guid CaseId, bool IsDuplicate)> AcceptAsync(
            AcceptCaseOriginEvent originEvent,
            CaseOriginEventReceipt receipt,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult((Guid.NewGuid(), false));
        }
    }

    private sealed class RecipeFixture : IDisposable
    {
        private readonly string path = Path.Combine(
            Path.GetTempPath(),
            $"panko-pagerduty-recipe-{Guid.NewGuid():N}.yaml");

        public RecipeFixture()
        {
            File.WriteAllText(path, """
                version: 3
                revision: pagerduty-auth-test-v1
                fallbackSlackChannel: "#cases"
                recipes:
                  - id: payments-production
                    pagerDutyServiceId: PSERVICE
                    team: payments
                    slackChannel: "#payments-cases"
                    pagerDuty: {}
                  - id: search-production
                    pagerDutyServiceId: PSEARCH
                    team: search
                    slackChannel: "#search-cases"
                    pagerDuty: {}
                """);
            Store = new RecipeStore(
                Microsoft.Extensions.Options.Options.Create(new PankoOptions { RecipesPath = path }),
                new TestEnvironment(),
                new CrumbSourceRegistry(
                    Array.Empty<ICrumbSourceAdapter>(),
                    TestConfiguration.CrumbSources()));
        }

        public RecipeStore Store { get; }

        public void Dispose() => File.Delete(path);
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Panko.Api.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
