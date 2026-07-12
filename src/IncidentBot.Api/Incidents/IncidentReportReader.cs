using IncidentBot.Api.Domain;

namespace IncidentBot.Api.Incidents;

public sealed record IncidentReportState(
    Guid Id,
    string PagerDutyIncidentId,
    string ServiceId,
    string ProfileId,
    string Title,
    string Urgency,
    IncidentState State,
    string Status,
    DateTimeOffset TriggeredAt,
    DateTimeOffset UpdatedAt,
    int Version,
    bool IsFrozen,
    InvestigationReport? Report)
{
    public static IncidentReportState From(IncidentRecord incident, InvestigationReport? report = null) => new(
        incident.Id, incident.PagerDutyIncidentId, incident.ServiceId, incident.ProfileId, incident.Title,
        incident.Urgency, incident.State, incident.Status, incident.TriggeredAt, incident.UpdatedAt,
        incident.Version, incident.IsFrozen, report);

    public static IncidentReportState From(InvestigationReport report, bool isFrozen = false) => new(
        report.Id, report.PagerDutyIncidentId, report.ServiceId, report.ProfileId, report.Title,
        report.Urgency, report.State, report.Status, report.TriggeredAt, report.UpdatedAt,
        report.Version, isFrozen, report);
}

public interface IIncidentReportReader
{
    Task<IncidentReportState?> GetAsync(Guid incidentId, CancellationToken cancellationToken);
}

public sealed class RepositoryIncidentReportReader(IIncidentStore repository) : IIncidentReportReader
{
    public async Task<IncidentReportState?> GetAsync(Guid incidentId, CancellationToken cancellationToken)
    {
        var incident = await repository.GetIncidentAsync(incidentId, cancellationToken);
        if (incident is null) return null;
        var report = await repository.GetReportAsync(incidentId, cancellationToken);
        return IncidentReportState.From(incident, report);
    }
}
