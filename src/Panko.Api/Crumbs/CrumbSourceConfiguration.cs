using Panko.Api.Options;
using Microsoft.Extensions.Options;

namespace Panko.Api.Crumbs;

public sealed class CrumbSourceConfiguration(IOptions<CrumbSourceOptions> options)
{
    private readonly CrumbSourceOptions configured = options.Value;

    public ConnectorTransport For(string source) => source switch
    {
        CrumbSourceRegistry.PagerDuty => configured.PagerDuty,
        CrumbSourceRegistry.Nomad => configured.Nomad,
        CrumbSourceRegistry.Consul => configured.Consul,
        CrumbSourceRegistry.GitLab => configured.GitLab,
        CrumbSourceRegistry.Grafana => configured.Grafana,
        CrumbSourceRegistry.Kafka => configured.Kafka,
        CrumbSourceRegistry.VictoriaLogs => configured.VictoriaLogs,
        _ => throw new InvalidOperationException($"Unknown Crumb source '{source}'.")
    };
}
