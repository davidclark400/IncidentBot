using Panko.Api.Security;
using Microsoft.AspNetCore.SignalR;

namespace Panko.Api.Hubs;

public sealed class CaseHub(ICaseAccessAuthorizer authorization) : Hub
{
    public async Task JoinCase(string caseId)
    {
        if (!Guid.TryParse(caseId, out var parsed))
        {
            throw new HubException("A valid Case id is required.");
        }

        var grant = await authorization.AuthorizeAsync(
            Context.User ?? new System.Security.Claims.ClaimsPrincipal(),
            parsed,
            CaseAccessKind.LiveUpdates,
            Context.ConnectionAborted);
        if (grant is null)
        {
            throw new HubException("Case not found.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(parsed));
    }

    public Task LeaveCase(string caseId)
    {
        if (!Guid.TryParse(caseId, out var parsed))
        {
            return Task.CompletedTask;
        }

        return Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(parsed));
    }

    public static string GroupName(Guid caseId) => $"case:{caseId:N}";
}
