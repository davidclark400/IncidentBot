using IncidentBot.Api.Domain;

namespace IncidentBot.Api.Connectors;

public interface IIncidentEvidenceConnector
{
    string Source { get; }
    Task<ConnectorResult> CollectAsync(
        InvestigationContext context,
        EvidenceScope scope,
        CancellationToken cancellationToken);
}

public interface IMcpEvidenceAdapter
{
    Task<ConnectorResult> CollectAsync(
        string source,
        McpToolConfiguration configuration,
        InvestigationContext context,
        EvidenceScope scope,
        object allowedResources,
        string? allowedBaseUrl,
        CancellationToken cancellationToken);
}
