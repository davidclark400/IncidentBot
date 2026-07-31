using IncidentBot.Api.Options;
using IncidentBot.Api.Profiles;
using IncidentBot.Api.Domain;
using IncidentBot.Api.Connectors;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using YamlDotNet.Core;

namespace IncidentBot.Api.Tests;

public sealed class ProfileTests
{
    [Fact]
    public void ExampleProfile_LoadsAndNarrowsAllSources()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "config", "investigation-profiles.yaml");
        var options = Microsoft.Extensions.Options.Options.Create(new IncidentBotOptions { ProfilesPath = path });
        var store = new InvestigationProfileStore(options, new TestEnvironment(), EmptySources());

        var profile = store.Resolve("P123PAYMENTS", new Dictionary<string, string>
        {
            ["environment"] = "production",
            ["service"] = "P123PAYMENTS"
        });

        Assert.Equal("payments-production", profile.Id);
        Assert.NotEmpty(profile.Nomad!.Namespaces.Single().Jobs);
        Assert.NotEmpty(profile.Grafana!.Dashboards);
        Assert.NotEmpty(profile.Grafana.Queries);
        Assert.NotEmpty(profile.VictoriaLogs!.StreamFilters);
        Assert.NotEmpty(profile.GitLab!.Projects);
        Assert.Equal("production", profile.SlackPromptLabels["environment"]);
    }

    [Fact]
    public void ExampleProfile_ListsDistinctConfiguredEvidenceSources()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "config", "investigation-profiles.yaml");
        var store = new InvestigationProfileStore(
            Microsoft.Extensions.Options.Options.Create(new IncidentBotOptions { ProfilesPath = path }),
            new TestEnvironment(),
            EmptySources());

        var sources = store.ConfiguredEvidenceSources();

        Assert.Equal(
            ["gitlab", "grafana", "nomad", "pagerduty", "victorialogs"],
            sources.Select(source => source.Source));
        Assert.All(sources, source => Assert.Equal(["payments-production"], source.ProfileIds));
    }

    [Fact]
    public void ExampleProfile_UsesApplicationLevelTransportConfiguration()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "config", "investigation-profiles.yaml");
        var store = new InvestigationProfileStore(
            Microsoft.Extensions.Options.Options.Create(new IncidentBotOptions { ProfilesPath = path }),
            new TestEnvironment(),
            EmptySources());

        var source = Assert.Single(
            store.ConfiguredEvidenceSources(),
            configured => configured.Source == EvidenceSourceRegistry.Nomad);

        Assert.Equal("https://nomad.internal.example", source.Transport.BaseUrl);
        Assert.Null(typeof(NomadScope).GetProperty("Connector"));
    }

    [Fact]
    public void ProfileConnectorOverride_IsRejected()
    {
        var path = Path.Combine(Path.GetTempPath(), $"incidentbot-profile-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, """
            version: 2
            revision: test-v2
            fallbackSlackChannel: "#incidents"
            profiles:
              - id: payments
                pagerDutyServiceId: P123
                team: payments
                slackChannel: "#payments-incidents"
                nomad:
                  connector:
                    baseUrl: https://wrong.example
                  namespaces:
                    - name: production
                      jobs: [payments]
            """);
        try
        {
            Assert.Throws<YamlException>(() => new InvestigationProfileStore(
                Microsoft.Extensions.Options.Options.Create(new IncidentBotOptions { ProfilesPath = path }),
                new TestEnvironment(),
                EmptySources()));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [MemberData(nameof(DocumentsMissingRequiredMetadata))]
    public void RequiredProfileMetadata_CannotBeOmitted(string yaml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"incidentbot-profile-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, yaml);
        try
        {
            Assert.Throws<InvalidOperationException>(() => new InvestigationProfileStore(
                Microsoft.Extensions.Options.Options.Create(new IncidentBotOptions { ProfilesPath = path }),
                new TestEnvironment(),
                EmptySources()));
        }
        finally
        {
            File.Delete(path);
        }
    }

    public static IEnumerable<object[]> DocumentsMissingRequiredMetadata()
    {
        yield return
        [
            """
            revision: test-v2
            fallbackSlackChannel: "#incidents"
            profiles: []
            """
        ];
        yield return
        [
            """
            version: 2
            fallbackSlackChannel: "#incidents"
            profiles: []
            """
        ];
        yield return
        [
            """
            version: 2
            revision: test-v2
            profiles: []
            """
        ];
    }

    [Fact]
    public void UnmappedService_UsesFallbackWithoutGlobalDiscovery()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "config", "investigation-profiles.yaml");
        var store = new InvestigationProfileStore(
            Microsoft.Extensions.Options.Options.Create(new IncidentBotOptions { ProfilesPath = path }),
            new TestEnvironment(),
            EmptySources());

        var profile = store.Resolve("UNKNOWN", new Dictionary<string, string>());

        Assert.Equal("unmapped", profile.Id);
        Assert.Null(profile.Nomad);
        Assert.Null(profile.Grafana);
        Assert.Null(profile.VictoriaLogs);
    }

    [Theory]
    [MemberData(nameof(InvalidNamedQueryDocuments))]
    public void QueryTemplateAuthorityKeysMustBeNamedAndUnique(string yaml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"incidentbot-profile-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, yaml);
        try
        {
            Assert.Throws<InvalidOperationException>(() => new InvestigationProfileStore(
                Microsoft.Extensions.Options.Options.Create(new IncidentBotOptions { ProfilesPath = path }),
                new TestEnvironment(),
                EmptySources()));
        }
        finally
        {
            File.Delete(path);
        }
    }

    public static IEnumerable<object[]> InvalidNamedQueryDocuments()
    {
        yield return
        [
            """
            version: 2
            revision: test-v2
            fallbackSlackChannel: "#incidents"
            profiles:
              - id: payments
                pagerDutyServiceId: P123
                grafana:
                  queries:
                    - name: Errors
                      datasourceUid: prometheus
                      expression: up
                    - name: Errors
                      datasourceUid: prometheus
                      expression: rate(errors[5m])
            """
        ];
        yield return
        [
            """
            version: 2
            revision: test-v2
            fallbackSlackChannel: "#incidents"
            profiles:
              - id: payments
                pagerDutyServiceId: P123
                victoriaLogs:
                  streamFilters:
                    service: payments
                  queries:
                    - name: ""
                      expression: level:error
            """
        ];
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

    private static EvidenceSourceRegistry EmptySources() => new(
        Array.Empty<IIncidentEvidenceConnector>(),
        new EvidenceSourceConfiguration(Microsoft.Extensions.Options.Options.Create(EvidenceSources())));

    private static EvidenceSourceOptions EvidenceSources() => new()
    {
        PagerDuty = Transport("https://api.pagerduty.com", "PAGERDUTY_API_TOKEN"),
        Nomad = Transport("https://nomad.internal.example", "NOMAD_TOKEN"),
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
