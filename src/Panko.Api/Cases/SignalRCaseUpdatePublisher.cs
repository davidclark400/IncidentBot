using Panko.Api.Hubs;
using Panko.Api.Contracts;
using Panko.Contracts;
using Microsoft.AspNetCore.SignalR;
using Domain = Panko.Api.Domain;

namespace Panko.Api.Cases;

public sealed class SignalRCaseUpdatePublisher(IHubContext<CaseHub> hub) : ICaseUpdatePublisher
{
    public async Task PublishProgressAsync(
        Domain.CaseProgress progress,
        CancellationToken cancellationToken)
    {
        var clients = hub.Clients.Group(CaseHub.GroupName(progress.CaseId));
        var contract = progress.ToContract();
        await clients.SendAsync(
            "CaseProgressUpdated",
            contract,
            cancellationToken);
    }

    public Task PublishStatusAsync(
        Guid caseId,
        int caseFileVersion,
        string status,
        CancellationToken cancellationToken) =>
        PublishStatusAsync(caseId, caseFileVersion, 0, 0, status, cancellationToken);

    public async Task PublishStatusAsync(
        Guid caseId,
        int caseFileVersion,
        long inputVersion,
        long projectedInputVersion,
        string status,
        CancellationToken cancellationToken)
    {
        var clients = hub.Clients.Group(CaseHub.GroupName(caseId));
        var update = new CaseStatusChanged(
            caseId, status, caseFileVersion, inputVersion, projectedInputVersion);
        await clients.SendAsync(
            "CaseStatusChanged",
            update,
            cancellationToken);
    }

    public async Task PublishCaseFileAsync(
        Guid caseId,
        int caseFileVersion,
        string status,
        IReadOnlyList<string> changedSections,
        CancellationToken cancellationToken)
        => await PublishCaseFileAsync(
            caseId, caseFileVersion, 0, 0, status, changedSections, cancellationToken);

    public async Task PublishCaseFileAsync(
        Guid caseId,
        int caseFileVersion,
        long inputVersion,
        long projectedInputVersion,
        string status,
        IReadOnlyList<string> changedSections,
        CancellationToken cancellationToken)
    {
        var clients = hub.Clients.Group(CaseHub.GroupName(caseId));
        var caseUpdate = new CaseUpdated(
            caseId,
            caseFileVersion,
            changedSections,
            inputVersion,
            projectedInputVersion,
            status);
        await clients.SendAsync(
            "CaseUpdated",
            caseUpdate,
            cancellationToken);
        var statusUpdate = new CaseStatusChanged(
            caseId, status, caseFileVersion, inputVersion, projectedInputVersion);
        await clients.SendAsync(
            "CaseStatusChanged",
            statusUpdate,
            cancellationToken);
    }
}
