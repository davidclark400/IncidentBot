using IncidentBot.Api.Infrastructure;
using IncidentBot.Api.Connectors;
using IncidentBot.Api.Domain;
using IncidentBot.Api.Options;
using IncidentBot.Api.Profiles;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace IncidentBot.Api.Tests;

public sealed class DeploymentReadinessTests
{
    [Fact]
    public void CompleteProductionConfigurationIsReady()
    {
        using var profile = TestProfile.Create();
        var checker = CreateChecker(
            profile.Store,
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
        using var profile = TestProfile.Create();
        var checker = CreateChecker(
            profile.Store,
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
        Assert.Contains("Trusted ingress identity enforcement must be enabled.", assessment.ConfigurationIssues);
        Assert.Contains("IncidentBot:PublicBaseUrl must be a non-loopback ingress URL.", assessment.ConfigurationIssues);
    }

    [Fact]
    public void PlaceholderConnectorHostsBlockProductionReadiness()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "config", "investigation-profiles.yaml");
        var store = new InvestigationProfileStore(
            Microsoft.Extensions.Options.Options.Create(new IncidentBotOptions { ProfilesPath = path }),
            new TestEnvironment(),
            EmptySources());
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
        using var profile = TestProfile.Create();
        var checker = CreateChecker(
            profile.Store,
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
        using var profile = TestProfile.Create();
        var checker = CreateChecker(
            profile.Store,
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
        using var profile = TestProfile.Create();
        var environment = CompleteEnvironment()
            .Where(pair => pair.Key != "LITELLM_API_KEY")
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var checker = CreateChecker(
            profile.Store,
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
    public void DisabledMcpReportsOnlyEnabledEvidenceSources()
    {
        using var profile = TestProfile.Create(EmptySources(McpEvidenceSources()));
        var checker = CreateChecker(
            profile.Store,
            CompleteEnvironment(),
            mcpEnabled: false);

        var assessment = checker.CheckProduction();

        Assert.False(assessment.Ready);
        Assert.Contains(
            "At least one enabled evidence source uses MCP transport while IncidentBot:McpEnabled is false.",
            assessment.ConfigurationIssues);
    }

    private static DeploymentReadinessChecker CreateChecker(
        InvestigationProfileStore profiles,
        IReadOnlyDictionary<string, string> environment,
        bool requireSignature = true,
        bool requireIdentity = true,
        string publicBaseUrl = "https://incidentbot.internal",
        bool slackEnabled = true,
        bool collectionEnabled = true,
        bool mcpEnabled = true,
        string liteLlmBaseUrl = "http://litellm.internal:4000") => new(
        profiles,
        Microsoft.Extensions.Options.Options.Create(new IncidentBotOptions
        {
            PublicBaseUrl = publicBaseUrl,
            CollectionEnabled = collectionEnabled,
            McpEnabled = mcpEnabled,
            RequireSlackForReadiness = true
        }),
        Microsoft.Extensions.Options.Options.Create(new PagerDutyOptions { RequireSignature = requireSignature }),
        Microsoft.Extensions.Options.Options.Create(EvidenceSources()),
        Microsoft.Extensions.Options.Options.Create(new SlackOptions { Enabled = slackEnabled }),
        Microsoft.Extensions.Options.Options.Create(new LiteLlmOptions { BaseUrl = liteLlmBaseUrl }),
        Microsoft.Extensions.Options.Options.Create(new IngressIdentityOptions { Required = requireIdentity }),
        new DictionaryEnvironment(environment));

    private sealed class DictionaryEnvironment(IReadOnlyDictionary<string, string> values) : ICredentialProvider
    {
        public string? Get(string name) => values.TryGetValue(name, out var value) ? value : null;
    }

    private sealed class TestProfile : IDisposable
    {
        private readonly string path;

        private TestProfile(string path, InvestigationProfileStore store)
        {
            this.path = path;
            Store = store;
        }

        public InvestigationProfileStore Store { get; }

        public static TestProfile Create(EvidenceSourceRegistry? evidenceSources = null)
        {
            var path = Path.Combine(Path.GetTempPath(), $"incidentbot-profile-{Guid.NewGuid():N}.yaml");
            File.WriteAllText(path, """
                version: 2
                revision: test-v1
                fallbackSlackChannel: "#incidents"
                profiles:
                  - id: payments-production
                    pagerDutyServiceId: P123
                    team: payments
                    slackChannel: "#payments-incidents"
                    pagerDuty: {}
                """);
            var store = new InvestigationProfileStore(
                Microsoft.Extensions.Options.Options.Create(new IncidentBotOptions { ProfilesPath = path }),
                new TestEnvironment(),
                evidenceSources ?? EmptySources());
            return new TestProfile(path, store);
        }

        public void Dispose() => File.Delete(path);
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "IncidentBot.Api.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static EvidenceSourceRegistry EmptySources(EvidenceSourceOptions? options = null) => new(
        Array.Empty<IIncidentEvidenceConnector>(),
        new EvidenceSourceConfiguration(Microsoft.Extensions.Options.Options.Create(options ?? EvidenceSources())));

    private static IReadOnlyDictionary<string, string> CompleteEnvironment() =>
        new Dictionary<string, string>
        {
            ["PAGERDUTY_WEBHOOK_SECRET"] = "configured",
            ["PAGERDUTY_API_TOKEN"] = "configured",
            ["SLACK_BOT_TOKEN"] = "configured",
            ["SLACK_APP_TOKEN"] = "configured",
            ["LITELLM_API_KEY"] = "configured"
        };

    private static EvidenceSourceOptions McpEvidenceSources()
    {
        var options = EvidenceSources();
        return new EvidenceSourceOptions
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
            GitLab = options.GitLab,
            Grafana = options.Grafana,
            VictoriaLogs = options.VictoriaLogs
        };
    }

    private static EvidenceSourceOptions EvidenceSources() => new()
    {
        PagerDuty = Transport("https://api.pagerduty.com", "PAGERDUTY_API_TOKEN"),
        Nomad = Transport("https://nomad.internal.example", "NOMAD_TOKEN"),
        GitLab = Transport("https://gitlab.internal.example", "GITLAB_READ_TOKEN"),
        Grafana = Transport("https://grafana.internal.example", "GRAFANA_SERVICE_TOKEN"),
        VictoriaLogs = Transport("https://victorialogs.internal.example", "VICTORIALOGS_TOKEN")
    };

    private static ConnectorTransport Transport(string baseUrl, string credentialEnv) => new()
    {
        Mode = "api",
        BaseUrl = baseUrl,
        CredentialEnv = credentialEnv
    };
}
