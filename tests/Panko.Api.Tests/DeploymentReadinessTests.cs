using Panko.Api.Infrastructure;
using Panko.Api.Crumbs;
using Panko.Api.Domain;
using Panko.Api.Options;
using Panko.Api.Recipes;
using Panko.Observability;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace Panko.Api.Tests;

public sealed class DeploymentReadinessTests
{
    [Fact]
    public void CompleteProductionConfigurationIsReady()
    {
        using var recipe = TestRecipe.Create();
        var checker = CreateChecker(
            recipe.Store,
            new Dictionary<string, string>
            {
                ["PAGERDUTY_WEBHOOK_SECRET"] = "configured",
                ["PAGERDUTY_API_TOKEN"] = "configured",
                ["SLACK_BOT_TOKEN"] = "configured",
                ["SLACK_APP_TOKEN"] = "configured",
                ["LITELLM_API_KEY"] = "configured"
            });

        var assessment = checker.CheckProduction();

        Assert.True(assessment.Ready);
        Assert.Empty(assessment.MissingEnvironmentVariables);
        Assert.Empty(assessment.ConfigurationIssues);
    }

    [Fact]
    public void MissingSecretsAndUnsafeProductionSettingsAreReportedByName()
    {
        using var recipe = TestRecipe.Create();
        var checker = CreateChecker(
            recipe.Store,
            new Dictionary<string, string> { ["SLACK_BOT_TOKEN"] = "replace-me" },
            requireSignature: false,
            requireIdentity: false,
            publicBaseUrl: "http://localhost:5173");

        var assessment = checker.CheckProduction();

        Assert.False(assessment.Ready);
        Assert.Equal(
            ["LITELLM_API_KEY", "PAGERDUTY_API_TOKEN", "SLACK_APP_TOKEN", "SLACK_BOT_TOKEN"],
            assessment.MissingEnvironmentVariables);
        Assert.Contains("PagerDuty webhook signature validation must be enabled.", assessment.ConfigurationIssues);
        Assert.Contains("JWT identity enforcement must be enabled.", assessment.ConfigurationIssues);
        Assert.Contains("Panko:PublicBaseUrl must be a non-loopback ingress URL.", assessment.ConfigurationIssues);
    }

    [Fact]
    public void PlaceholderConnectorHostsBlockProductionReadiness()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "config", "recipes.yaml");
        var store = new RecipeStore(
            Microsoft.Extensions.Options.Options.Create(new PankoOptions { RecipesPath = path }),
            new TestEnvironment(),
            EmptySources(),
            serviceMetricPlans: new ServiceMetricPlanStore(ServiceMetricCatalog.Load(
                Path.Combine(AppContext.BaseDirectory, "config", "service-metric-packs.yaml"))));
        var checker = CreateChecker(
            store,
            new Dictionary<string, string>
            {
                ["PAGERDUTY_WEBHOOK_SECRET"] = "configured",
                ["PAGERDUTY_API_TOKEN"] = "configured",
                ["NOMAD_TOKEN"] = "configured",
                ["GITLAB_READ_TOKEN"] = "configured",
                ["GRAFANA_SERVICE_TOKEN"] = "configured",
                ["VICTORIALOGS_TOKEN"] = "configured",
                ["SLACK_BOT_TOKEN"] = "configured",
                ["SLACK_APP_TOKEN"] = "configured",
                ["LITELLM_API_KEY"] = "configured"
            });

        var assessment = checker.CheckProduction();

        Assert.False(assessment.Ready);
        Assert.Contains(
            assessment.ConfigurationIssues,
            issue => issue.Contains("placeholder host", StringComparison.Ordinal));
    }

    [Fact]
    public void PilotCanRequireSlackAsACoreDeliveryPath()
    {
        using var recipe = TestRecipe.Create();
        var checker = CreateChecker(
            recipe.Store,
            new Dictionary<string, string>
            {
                ["PAGERDUTY_WEBHOOK_SECRET"] = "configured",
                ["PAGERDUTY_API_TOKEN"] = "configured",
                ["LITELLM_API_KEY"] = "configured"
            },
            slackEnabled: false);

        var assessment = checker.CheckProduction();

        Assert.False(assessment.Ready);
        Assert.Contains("Slack must be enabled for this deployment.", assessment.ConfigurationIssues);
    }

    [Fact]
    public void LiteLlmPlaceholderEndpointBlocksEnabledCollection()
    {
        using var recipe = TestRecipe.Create();
        var checker = CreateChecker(
            recipe.Store,
            CompleteEnvironment(),
            liteLlmBaseUrl: "https://litellm.example");

        var assessment = checker.CheckProduction();

        Assert.False(assessment.Ready);
        Assert.Contains(
            "LiteLlm:BaseUrl still uses placeholder host 'litellm.example'.",
            assessment.ConfigurationIssues);
    }

    [Fact]
    public void DisabledCollectionDoesNotRequireLiteLlmConfiguration()
    {
        using var recipe = TestRecipe.Create();
        var environment = CompleteEnvironment()
            .Where(pair => pair.Key != "LITELLM_API_KEY")
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var checker = CreateChecker(
            recipe.Store,
            environment,
            collectionEnabled: false,
            liteLlmBaseUrl: "https://litellm.example");

        var assessment = checker.CheckProduction();

        Assert.True(assessment.Ready);
        Assert.DoesNotContain("LITELLM_API_KEY", assessment.MissingEnvironmentVariables);
        Assert.DoesNotContain(
            assessment.ConfigurationIssues,
            issue => issue.StartsWith("LiteLlm:", StringComparison.Ordinal));
    }

    [Fact]
    public void DisabledMcpReportsOnlyEnabledCrumbSources()
    {
        using var recipe = TestRecipe.Create(EmptySources(McpCrumbSources()));
        var checker = CreateChecker(
            recipe.Store,
            CompleteEnvironment(),
            mcpEnabled: false);

        var assessment = checker.CheckProduction();

        Assert.False(assessment.Ready);
        Assert.Contains(
            "At least one enabled Crumb source uses MCP transport while Panko:McpEnabled is false.",
            assessment.ConfigurationIssues);
    }

    [Fact]
    public void UnsafeJwtTeamAndMissingProxySettingsBlockProductionReadiness()
    {
        using var recipe = TestRecipe.Create();
        var checker = CreateChecker(
            recipe.Store,
            CompleteEnvironment(),
            identityOptions: new JwtIdentityOptions
            {
                Required = true,
                Authority = "http://identity.internal",
                Issuer = "not-an-issuer-uri",
                Audience = " ",
                RequireHttpsMetadata = false
            },
            teamAuthorizationOptions: new TeamAuthorizationOptions
            {
                TeamClaimTypes = [],
                GroupClaimTypes = [],
                GroupTeamMappings = new Dictionary<string, string>
                {
                    ["payments-responders"] = "payments"
                }
            },
            trustedProxyOptions: new TrustedProxyOptions());

        var assessment = checker.CheckProduction();

        Assert.False(assessment.Ready);
        Assert.Contains("OIDC discovery metadata must require HTTPS.", assessment.ConfigurationIssues);
        Assert.Contains("JwtIdentity:Authority must be an absolute HTTPS URL.", assessment.ConfigurationIssues);
        Assert.Contains("JwtIdentity:Issuer must be an absolute HTTPS URL.", assessment.ConfigurationIssues);
        Assert.Contains(
            "JwtIdentity:Audience must identify the Panko resource.",
            assessment.ConfigurationIssues);
        Assert.Contains(
            "Team authorization needs a signed team claim or directory-group mapping.",
            assessment.ConfigurationIssues);
        Assert.Contains(
            "At least one trusted proxy IP address or CIDR network must be configured.",
            assessment.ConfigurationIssues);
    }

    [Fact]
    public void GroupClaimWithDirectoryMappingSatisfiesTeamReadiness()
    {
        using var recipe = TestRecipe.Create();
        var checker = CreateChecker(
            recipe.Store,
            CompleteEnvironment(),
            teamAuthorizationOptions: new TeamAuthorizationOptions
            {
                TeamClaimTypes = [],
                GroupClaimTypes = ["groups"],
                GroupTeamMappings = new Dictionary<string, string>
                {
                    ["payments-responders"] = "payments"
                }
            });

        var assessment = checker.CheckProduction();

        Assert.True(assessment.Ready);
        Assert.DoesNotContain(
            "Team authorization needs a signed team claim or directory-group mapping.",
            assessment.ConfigurationIssues);
    }

    [Fact]
    public void DirectoryGroupMappingToAnUnknownTeamBlocksProductionReadiness()
    {
        using var recipe = TestRecipe.Create();
        var checker = CreateChecker(
            recipe.Store,
            CompleteEnvironment(),
            teamAuthorizationOptions: new TeamAuthorizationOptions
            {
                TeamClaimTypes = [],
                GroupClaimTypes = ["groups"],
                GroupTeamMappings = new Dictionary<string, string>
                {
                    ["orders-responders"] = "orders"
                }
            });

        var assessment = checker.CheckProduction();

        Assert.False(assessment.Ready);
        Assert.Contains(
            "TeamAuthorization group 'orders-responders' maps to unknown team 'orders'.",
            assessment.ConfigurationIssues);
    }

    [Fact]
    public void CatchAllTrustedProxyNetworkBlocksProductionReadiness()
    {
        using var recipe = TestRecipe.Create();
        var checker = CreateChecker(
            recipe.Store,
            CompleteEnvironment(),
            trustedProxyOptions: new TrustedProxyOptions
            {
                KnownNetworks = ["0.0.0.0/0"]
            });

        var assessment = checker.CheckProduction();

        Assert.False(assessment.Ready);
        Assert.Contains(
            "Trusted proxy addresses must be explicit and must not use catch-all networks.",
            assessment.ConfigurationIssues);
    }

    private static DeploymentReadinessChecker CreateChecker(
        RecipeStore recipes,
        IReadOnlyDictionary<string, string> environment,
        bool requireSignature = true,
        bool requireIdentity = true,
        string publicBaseUrl = "https://panko.internal",
        bool slackEnabled = true,
        bool collectionEnabled = true,
        bool mcpEnabled = true,
        string liteLlmBaseUrl = "http://litellm.internal:4000",
        JwtIdentityOptions? identityOptions = null,
        TeamAuthorizationOptions? teamAuthorizationOptions = null,
        TrustedProxyOptions? trustedProxyOptions = null) => new(
        recipes,
        Microsoft.Extensions.Options.Options.Create(new PankoOptions
        {
            PublicBaseUrl = publicBaseUrl,
            CrumbCollectionEnabled = collectionEnabled,
            McpEnabled = mcpEnabled,
            RequireSlackForReadiness = true
        }),
        Microsoft.Extensions.Options.Options.Create(new PagerDutyOptions { RequireSignature = requireSignature }),
        Microsoft.Extensions.Options.Options.Create(CrumbSources()),
        Microsoft.Extensions.Options.Options.Create(new SlackOptions
        {
            Enabled = slackEnabled,
            ChannelTeams = new Dictionary<string, string>
            {
                ["C0123456789"] = "payments"
            }
        }),
        Microsoft.Extensions.Options.Options.Create(new LiteLlmOptions { BaseUrl = liteLlmBaseUrl }),
        Microsoft.Extensions.Options.Options.Create(identityOptions ?? new JwtIdentityOptions
        {
            Required = requireIdentity,
            Authority = "https://identity.internal",
            Issuer = "https://identity.internal"
        }),
        Microsoft.Extensions.Options.Options.Create(teamAuthorizationOptions ?? new TeamAuthorizationOptions()),
        Microsoft.Extensions.Options.Options.Create(trustedProxyOptions ?? new TrustedProxyOptions
        {
            KnownProxies = ["10.42.1.10"]
        }),
        new DictionaryEnvironment(environment));

    private sealed class DictionaryEnvironment(IReadOnlyDictionary<string, string> values) : ICredentialProvider
    {
        public string? Get(string name) => values.TryGetValue(name, out var value) ? value : null;
    }

    private sealed class TestRecipe : IDisposable
    {
        private readonly string path;

        private TestRecipe(string path, RecipeStore store)
        {
            this.path = path;
            Store = store;
        }

        public RecipeStore Store { get; }

        public static TestRecipe Create(CrumbSourceRegistry? crumbSources = null)
        {
            var path = Path.Combine(Path.GetTempPath(), $"panko-recipe-{Guid.NewGuid():N}.yaml");
            File.WriteAllText(path, """
                version: 3
                revision: test-v1
                fallbackSlackChannel: "#cases"
                recipes:
                  - id: payments-production
                    pagerDutyServiceId: P123
                    team: payments
                    slackChannel: C0123456789
                    pagerDuty: {}
                """);
            var store = new RecipeStore(
                Microsoft.Extensions.Options.Options.Create(new PankoOptions { RecipesPath = path }),
                new TestEnvironment(),
                crumbSources ?? EmptySources());
            return new TestRecipe(path, store);
        }

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

    private static CrumbSourceRegistry EmptySources(CrumbSourceOptions? options = null) => new(
        Array.Empty<ICrumbSourceAdapter>(),
        new CrumbSourceConfiguration(Microsoft.Extensions.Options.Options.Create(options ?? CrumbSources())));

    private static IReadOnlyDictionary<string, string> CompleteEnvironment() =>
        new Dictionary<string, string>
        {
            ["PAGERDUTY_WEBHOOK_SECRET"] = "configured",
            ["PAGERDUTY_API_TOKEN"] = "configured",
            ["SLACK_BOT_TOKEN"] = "configured",
            ["SLACK_APP_TOKEN"] = "configured",
            ["LITELLM_API_KEY"] = "configured"
        };

    private static CrumbSourceOptions McpCrumbSources()
    {
        var options = CrumbSources();
        return new CrumbSourceOptions
        {
            PagerDuty = new ConnectorTransport
            {
                Mode = "mcp",
                BaseUrl = options.PagerDuty.BaseUrl,
                CredentialEnv = options.PagerDuty.CredentialEnv,
                Mcp = new McpToolConfiguration
                {
                    ServerUrl = "https://mcp.internal",
                    ToolName = "collect_pagerduty",
                    CredentialEnv = "PAGERDUTY_MCP_TOKEN"
                }
            },
            Nomad = options.Nomad,
            Consul = options.Consul,
            GitLab = options.GitLab,
            Grafana = options.Grafana,
            Kafka = options.Kafka,
            VictoriaLogs = options.VictoriaLogs
        };
    }

    private static CrumbSourceOptions CrumbSources() => new()
    {
        PagerDuty = Transport("https://api.pagerduty.com", "PAGERDUTY_API_TOKEN"),
        Nomad = Transport("https://nomad.internal.example", "NOMAD_TOKEN"),
        Consul = Transport("https://consul.internal.example", "CONSUL_HTTP_TOKEN"),
        GitLab = Transport("https://gitlab.internal.example", "GITLAB_READ_TOKEN"),
        Grafana = Transport("https://grafana.internal.example", "GRAFANA_SERVICE_TOKEN"),
        Kafka = Transport("https://grafana.internal.example", "GRAFANA_KAFKA_READ_TOKEN"),
        VictoriaLogs = Transport("https://victorialogs.internal.example", "VICTORIALOGS_TOKEN")
    };

    private static ConnectorTransport Transport(string baseUrl, string credentialEnv) => new()
    {
        Mode = "api",
        BaseUrl = baseUrl,
        CredentialEnv = credentialEnv
    };
}
