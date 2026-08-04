using System.Net;
using System.Text;
using Panko.Api.Crumbs;
using Panko.Api.Domain;
using Panko.Api.Options;

namespace Panko.Api.Tests;

public sealed class ConsulCrumbSourceTests
{
    private static readonly DateTimeOffset ObservedAt = DateTimeOffset.Parse("2026-08-03T10:05:00Z");

    [Fact]
    public async Task ReportsRegisteredHealthAndMissingExpectedServices()
    {
        var requests = new List<(string PathAndQuery, string? Token, string? Authorization)>();
        var handler = new DelegateHandler(request =>
        {
            requests.Add((
                request.RequestUri!.PathAndQuery,
                request.Headers.TryGetValues("X-Consul-Token", out var values) ? values.Single() : null,
                request.Headers.Authorization?.ToString()));
            return request.RequestUri.AbsolutePath.EndsWith("/payments-api", StringComparison.Ordinal)
                ? Json("""
                    [
                      {
                        "Node": { "Node": "node-a" },
                        "Service": { "ID": "payments-api-a", "Service": "payments-api" },
                        "Checks": [
                          { "CheckID": "serfHealth", "Status": "passing" },
                          { "CheckID": "service:payments-api-a", "Status": "passing" }
                        ]
                      },
                      {
                        "Node": { "Node": "node-b" },
                        "Service": { "ID": "payments-api-b", "Service": "payments-api" },
                        "Checks": [
                          { "CheckID": "serfHealth", "Status": "passing" },
                          { "CheckID": "service:payments-api-b", "Status": "critical" }
                        ]
                      }
                    ]
                    """)
                : Json("[]");
        });
        var connector = Connector(handler);

        var result = await connector.CollectAsync(
            Context(BuildRecipe()),
            Scope(),
            CancellationToken.None);

        Assert.Equal(CrumbSourceHealth.Complete, result.Health);
        var payments = Assert.Single(result.Crumbs, crumb =>
            crumb.ObjectType == "consul-service" && crumb.ObjectId == "payments/payments-api");
        Assert.Equal("critical", payments.Severity);
        Assert.Contains("2 instance(s): 1 passing, 0 warning, 1 critical, 0 unknown", payments.Summary,
            StringComparison.Ordinal);
        Assert.Equal("critical", payments.Provenance["scope"]?["status"]?.GetValue<string>());

        var unhealthy = Assert.Single(result.Crumbs, crumb =>
            crumb.ObjectType == "consul-service-instance");
        Assert.Equal("critical", unhealthy.Severity);
        Assert.Contains("payments-api-b", unhealthy.Summary, StringComparison.Ordinal);
        Assert.Contains("node-b", unhealthy.Summary, StringComparison.Ordinal);

        var missing = Assert.Single(result.Crumbs, crumb =>
            crumb.ObjectType == "consul-service" && crumb.ObjectId == "payments/payments-worker");
        Assert.Equal("critical", missing.Severity);
        Assert.Contains("is not registered", missing.Summary, StringComparison.Ordinal);
        Assert.Equal("unregistered", missing.Provenance["scope"]?["status"]?.GetValue<string>());

        Assert.Equal(2, requests.Count);
        Assert.All(requests, request =>
        {
            Assert.Contains("dc=primary", request.PathAndQuery, StringComparison.Ordinal);
            Assert.Contains("ns=payments", request.PathAndQuery, StringComparison.Ordinal);
            Assert.Contains("partition=default", request.PathAndQuery, StringComparison.Ordinal);
            Assert.Equal("consul-secret", request.Token);
            Assert.Null(request.Authorization);
        });
    }

    [Fact]
    public async Task RegistrationStateChangesKeepAStableServiceIdentity()
    {
        var call = 0;
        var handler = new DelegateHandler(_ => Interlocked.Increment(ref call) == 1
            ? Json("""
                [{
                  "Node": { "Node": "node-a" },
                  "Service": { "ID": "payments-api-a", "Service": "payments-api" },
                  "Checks": [{ "CheckID": "serfHealth", "Status": "passing" }]
                }]
                """)
            : Json("[]"));
        var connector = Connector(handler);
        var recipe = new Recipe
        {
            Id = "payments",
            Consul = new ConsulScope
            {
                Services = [new ConsulService { Name = "payments-api" }]
            }
        };

        var registered = await connector.CollectAsync(Context(recipe), Scope(), CancellationToken.None);
        var unregistered = await connector.CollectAsync(Context(recipe), Scope(), CancellationToken.None);

        var first = Assert.Single(registered.Crumbs);
        var second = Assert.Single(unregistered.Crumbs);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.ObjectId, second.ObjectId);
        Assert.Contains("is registered", first.Summary, StringComparison.Ordinal);
        Assert.Contains("is not registered", second.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnregisteredServiceWinsAConstrainedCrumbBudget()
    {
        var handler = new DelegateHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/payments-api", StringComparison.Ordinal)
                ? Json("""
                    [{
                      "Node": { "Node": "node-a" },
                      "Service": { "ID": "payments-api-a", "Service": "payments-api" },
                      "Checks": [{ "CheckID": "service:payments-api-a", "Status": "critical" }]
                    }]
                    """)
                : Json("[]"));
        var connector = Connector(handler, maxItems: 1);

        var result = await connector.CollectAsync(Context(BuildRecipe()), Scope(), CancellationToken.None);

        var crumb = Assert.Single(result.Crumbs);
        Assert.Equal("payments/payments-worker", crumb.ObjectId);
        Assert.Contains("is not registered", crumb.Summary, StringComparison.Ordinal);
        Assert.Equal(CrumbSourceHealth.Partial, result.Health);
    }

    private static ConsulCrumbSource Connector(HttpMessageHandler handler, int maxItems = 100)
    {
        var transport = new ConnectorTransport
        {
            Mode = "api",
            BaseUrl = "https://consul.test",
            CredentialEnv = "CONSUL_HTTP_TOKEN",
            TimeoutSeconds = 5,
            MaxItems = maxItems,
            MaxBytes = 262144
        };
        return new ConsulCrumbSource(
            new StubHttpClientFactory(handler),
            new ThrowingMcpAdapter(),
            TestConfiguration.CrumbSources(consul: transport),
            TestConfiguration.Credentials(("CONSUL_HTTP_TOKEN", "consul-secret")));
    }

    private static Recipe BuildRecipe() => new()
    {
        Id = "payments",
        Consul = new ConsulScope
        {
            Datacenter = "primary",
            Partition = "default",
            Services =
            [
                new ConsulService { Name = "payments-api", Namespace = "payments" },
                new ConsulService { Name = "payments-worker", Namespace = "payments" }
            ]
        }
    };

    private static CaseContext Context(Recipe recipe) => new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        "PD-1",
        "payments",
        "Payments failing",
        "high",
        PagerDutyIncidentState.Triggered,
        DateTimeOffset.Parse("2026-08-03T10:00:00Z"),
        new Dictionary<string, string>(),
        recipe);

    private static CrumbScope Scope() => new(
        ObservedAt.AddMinutes(-30),
        ObservedAt,
        "v1",
        100,
        262144);

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };

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

    private sealed class ThrowingMcpAdapter : IMcpCrumbSourceAdapter
    {
        public Task<CrumbSourceResult> CollectAsync(
            string source,
            McpToolConfiguration configuration,
            CaseContext context,
            CrumbScope scope,
            object allowedResources,
            string? allowedBaseUrl,
            CancellationToken cancellationToken) => throw new InvalidOperationException("MCP was not expected.");
    }
}
