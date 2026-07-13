using IncidentBot.Api.Connectors;
using IncidentBot.Api.Infrastructure;
using IncidentBot.Api.Options;
using Microsoft.Extensions.Options;

namespace IncidentBot.Api.Tests;

internal static class TestConfiguration
{
    public static EvidenceSourceConfiguration EvidenceSources(
        ConnectorTransport? pagerDuty = null,
        ConnectorTransport? nomad = null,
        ConnectorTransport? gitLab = null,
        ConnectorTransport? grafana = null,
        ConnectorTransport? victoriaLogs = null) => new(Microsoft.Extensions.Options.Options.Create(new EvidenceSourceOptions
    {
        PagerDuty = pagerDuty ?? Transport("https://api.pagerduty.test", "PAGERDUTY_API_TOKEN"),
        Nomad = nomad ?? Transport("https://nomad.test", "NOMAD_TOKEN"),
        GitLab = gitLab ?? Transport("https://gitlab.test", "GITLAB_READ_TOKEN"),
        Grafana = grafana ?? Transport("https://grafana.test", "GRAFANA_SERVICE_TOKEN"),
        VictoriaLogs = victoriaLogs ?? Transport("https://victorialogs.test", "VICTORIALOGS_TOKEN")
    }));

    public static ICredentialProvider Credentials(params (string Name, string Value)[] values) =>
        new DictionaryCredentialProvider(values.ToDictionary(value => value.Name, value => value.Value, StringComparer.Ordinal));

    private static ConnectorTransport Transport(string baseUrl, string credentialEnv) => new()
    {
        Mode = "api",
        BaseUrl = baseUrl,
        CredentialEnv = credentialEnv
    };

    private sealed class DictionaryCredentialProvider(IReadOnlyDictionary<string, string> values) : ICredentialProvider
    {
        public string? Get(string environmentVariableName) =>
            values.TryGetValue(environmentVariableName, out var value) ? value : null;
    }
}
