using Panko.Api.Crumbs;
using Panko.Api.Infrastructure;
using Panko.Api.Options;
using Microsoft.Extensions.Options;

namespace Panko.Api.Tests;

internal static class TestConfiguration
{
    public static CrumbSourceConfiguration CrumbSources(
        ConnectorTransport? pagerDuty = null,
        ConnectorTransport? nomad = null,
        ConnectorTransport? consul = null,
        ConnectorTransport? gitLab = null,
        ConnectorTransport? grafana = null,
        ConnectorTransport? kafka = null,
        ConnectorTransport? victoriaLogs = null) => new(Microsoft.Extensions.Options.Options.Create(new CrumbSourceOptions
        {
            PagerDuty = pagerDuty ?? Transport("https://api.pagerduty.test", "PAGERDUTY_API_TOKEN"),
            Nomad = nomad ?? Transport("https://nomad.test", "NOMAD_TOKEN"),
            Consul = consul ?? Transport("https://consul.test", "CONSUL_HTTP_TOKEN"),
            GitLab = gitLab ?? Transport("https://gitlab.test", "GITLAB_READ_TOKEN"),
            Grafana = grafana ?? Transport("https://grafana.test", "GRAFANA_SERVICE_TOKEN"),
            Kafka = kafka ?? Transport("https://kafka-grafana.test", "GRAFANA_KAFKA_READ_TOKEN"),
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
