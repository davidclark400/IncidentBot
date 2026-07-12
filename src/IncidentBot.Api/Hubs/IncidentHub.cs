using Microsoft.AspNetCore.SignalR;

namespace IncidentBot.Api.Hubs;

public sealed class IncidentHub : Hub
{
    public Task JoinIncident(string incidentId)
    {
        if (!Guid.TryParse(incidentId, out var parsed))
        {
            throw new HubException("A valid incident id is required.");
        }

        return Groups.AddToGroupAsync(Context.ConnectionId, GroupName(parsed));
    }

    public Task LeaveIncident(string incidentId)
    {
        if (!Guid.TryParse(incidentId, out var parsed))
        {
            return Task.CompletedTask;
        }

        return Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(parsed));
    }

    public static string GroupName(Guid incidentId) => $"incident:{incidentId:N}";
}
