using IncidentBot.Api.Options;

namespace IncidentBot.Api.Tests;

public sealed class EvidenceSourceOptionsTests
{
    [Theory]
    [InlineData("")]
    [InlineData("TOKEN-NAME")]
    [InlineData("9TOKEN")]
    public void ApiCredentialMustNameAnEnvironmentVariable(string credentialEnv)
    {
        var result = Validate(ValidOptions(nomad: Transport(credentialEnv: credentialEnv)));

        Assert.True(result.Failed);
        Assert.Contains("CredentialEnv must be a valid environment-variable name", result.FailureMessage);
    }

    [Fact]
    public void ApiEndpointMustUseHttpOrHttps()
    {
        var result = Validate(ValidOptions(nomad: Transport(baseUrl: "ftp://nomad.test")));

        Assert.True(result.Failed);
        Assert.Contains("BaseUrl must be an absolute HTTP(S) URL", result.FailureMessage);
    }

    [Fact]
    public void McpSettingsRequireHttpEndpointToolAndCredentialName()
    {
        var result = Validate(ValidOptions(nomad: new ConnectorTransport
        {
            Mode = "mcp",
            BaseUrl = "https://nomad.test",
            Mcp = new McpToolConfiguration
            {
                ServerUrl = "ftp://mcp.test",
                ToolName = "",
                CredentialEnv = "TOKEN-NAME"
            }
        }));

        Assert.True(result.Failed);
        Assert.Contains("Mcp requires an HTTP(S) ServerUrl, ToolName, and CredentialEnv", result.FailureMessage);
    }

    [Fact]
    public void PagerDutyMcpModeStillRequiresNativePullCredentialName()
    {
        var options = ValidOptions();
        var result = Validate(new EvidenceSourceOptions
        {
            PagerDuty = new ConnectorTransport
            {
                Mode = "mcp",
                BaseUrl = "https://api.pagerduty.test",
                Mcp = new McpToolConfiguration
                {
                    ServerUrl = "https://mcp.test",
                    ToolName = "collect_pagerduty",
                    CredentialEnv = "PAGERDUTY_MCP_TOKEN"
                }
            },
            Nomad = options.Nomad,
            GitLab = options.GitLab,
            Grafana = options.Grafana,
            VictoriaLogs = options.VictoriaLogs
        });

        Assert.True(result.Failed);
        Assert.Contains("incident pull always uses the native API", result.FailureMessage);
    }

    private static Microsoft.Extensions.Options.ValidateOptionsResult Validate(EvidenceSourceOptions options) =>
        new EvidenceSourceOptionsValidator().Validate(null, options);

    private static EvidenceSourceOptions ValidOptions(ConnectorTransport? nomad = null) => new()
    {
        PagerDuty = Transport("https://api.pagerduty.test", "PAGERDUTY_API_TOKEN"),
        Nomad = nomad ?? Transport(),
        GitLab = Transport("https://gitlab.test", "GITLAB_READ_TOKEN"),
        Grafana = Transport("https://grafana.test", "GRAFANA_SERVICE_TOKEN"),
        VictoriaLogs = Transport("https://victorialogs.test", "VICTORIALOGS_TOKEN")
    };

    private static ConnectorTransport Transport(
        string baseUrl = "https://nomad.test",
        string credentialEnv = "NOMAD_TOKEN") => new()
    {
        Mode = "api",
        BaseUrl = baseUrl,
        CredentialEnv = credentialEnv
    };
}
