using IncidentBot.Api.Options;
using Microsoft.Extensions.Options;

namespace IncidentBot.Api.Connectors;

public sealed class EvidenceSourceConfiguration(IOptions<EvidenceSourceOptions> options)
{
    private readonly EvidenceSourceOptions configured = options.Value;

    public ConnectorTransport For(string source) => source switch
    {
        EvidenceSourceRegistry.PagerDuty => configured.PagerDuty,
        EvidenceSourceRegistry.Nomad => configured.Nomad,
        EvidenceSourceRegistry.GitLab => configured.GitLab,
        EvidenceSourceRegistry.Grafana => configured.Grafana,
        EvidenceSourceRegistry.VictoriaLogs => configured.VictoriaLogs,
        _ => throw new InvalidOperationException($"Unknown evidence source '{source}'.")
    };
}
