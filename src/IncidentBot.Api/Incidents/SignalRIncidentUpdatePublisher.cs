using IncidentBot.Api.Hubs;
using IncidentBot.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace IncidentBot.Api.Incidents;

public sealed class SignalRIncidentUpdatePublisher(IHubContext<IncidentHub> hub) : IIncidentUpdatePublisher
{
    public Task PublishStatusAsync(
        Guid incidentId,
        int version,
        string status,
        CancellationToken cancellationToken) =>
        hub.Clients.Group(IncidentHub.GroupName(incidentId)).SendAsync(
            "IncidentStatusChanged", new IncidentStatusChanged(incidentId, status, version), cancellationToken);

    public async Task PublishReportAsync(
        Guid incidentId,
        int version,
        string status,
        IReadOnlyList<string> changedSections,
        CancellationToken cancellationToken)
    {
        var clients = hub.Clients.Group(IncidentHub.GroupName(incidentId));
        await clients.SendAsync(
            "IncidentUpdated", new IncidentUpdated(incidentId, version, changedSections), cancellationToken);
        await clients.SendAsync(
            "IncidentStatusChanged", new IncidentStatusChanged(incidentId, status, version), cancellationToken);
    }
}
