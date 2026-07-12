using IncidentBot.Api.Domain;

namespace IncidentBot.Api.Incidents;

/// <summary>
/// Domain-facing persistence surface for an incident and its durable investigation state.
/// PostgreSQL details remain inside the infrastructure adapter.
/// </summary>
public interface IIncidentStore
{
    Task<(Guid IncidentId, bool IsDuplicate)> AcceptWebhookAsync(
        PagerDutyWebhookEvent webhook,
        InvestigationProfile profile,
        ReadOnlyMemory<byte> rawPayload,
        CancellationToken cancellationToken);

    Task<IncidentRecord?> GetIncidentAsync(Guid incidentId, CancellationToken cancellationToken);

    Task<InvestigationReport?> GetReportAsync(Guid incidentId, CancellationToken cancellationToken);

    Task<int> SaveReportAsync(
        IncidentRecord incident,
        InvestigationReport report,
        CancellationToken cancellationToken);

    Task SetStatusAsync(Guid incidentId, string status, CancellationToken cancellationToken);

    Task<bool> RestartInvestigationAsync(
        Guid incidentId,
        string? slackChannel,
        string? slackTimestamp,
        CancellationToken cancellationToken);

    Task SetSlackTimestampAsync(Guid incidentId, string timestamp, CancellationToken cancellationToken);

    Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);
}
