using IncidentBot.Api.Options;
using IncidentBot.Api.Profiles;
using IncidentBot.Api.Domain;
using IncidentBot.Api.Connectors;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;

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

    [Fact]
    public void PersistedLabelsKeepOnlyRuntimeAndSelectedProfileKeys()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "config", "investigation-profiles.yaml");
        var store = new InvestigationProfileStore(
            Microsoft.Extensions.Options.Options.Create(new IncidentBotOptions { ProfilesPath = path }),
            new TestEnvironment(),
            EmptySources());
        var selectedProfile = new InvestigationProfile
        {
            Selectors =
            [
                new ProfileSelector
                {
                    Labels = new Dictionary<string, string> { ["tenant"] = "blue" }
                }
            ]
        };

        var filtered = store.FilterPersistedLabels(selectedProfile, new Dictionary<string, string>
        {
            ["service"] = "P123PAYMENTS",
            ["environment"] = "production",
            ["tenant"] = "blue",
            ["diagnostic_noise"] = "not needed after routing",
            ["auth_token"] = "must-not-persist"
        });

        Assert.Equal("production", filtered["environment"]);
        Assert.Equal("blue", filtered["tenant"]);
        Assert.DoesNotContain("diagnostic_noise", filtered.Keys);
        Assert.DoesNotContain("auth_token", filtered.Keys);
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

    private static EvidenceSourceRegistry EmptySources() => new(Array.Empty<IIncidentEvidenceConnector>());
}
