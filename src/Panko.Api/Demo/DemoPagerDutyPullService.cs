using Panko.Api.Cases;
using Panko.Api.Security;

namespace Panko.Api.Demo;

public sealed class DemoPagerDutyPullService(
    DemoCaseStore store,
    DemoReplay replay) : IPagerDutyPullService
{
    public Task<PagerDutyIncidentPage> GetRecentAsync(
        DateTimeOffset since,
        DateTimeOffset until,
        IReadOnlyList<string> authorizedServiceIds,
        CancellationToken cancellationToken)
    {
        var caseFile = store.Get();
        IReadOnlyList<PagerDutyIncidentSnapshot> incidents =
            caseFile.OpenedAt >= since && caseFile.OpenedAt <= until
            && authorizedServiceIds.Contains(caseFile.ServiceId, StringComparer.Ordinal)
                ?
                [
                    new PagerDutyIncidentSnapshot(
                        caseFile.PagerDutyIncidentId ?? "PDEMO",
                        2481,
                        caseFile.Title,
                        "triggered",
                        caseFile.Urgency,
                        caseFile.OpenedAt,
                        caseFile.UpdatedAt,
                        caseFile.ServiceId,
                        "Payments API",
                        ["Alex Chen"],
                        "https://pagerduty.example/incidents/PDEMO",
                        new Dictionary<string, string> { ["service"] = caseFile.ServiceId })
                ]
                : [];
        return Task.FromResult(new PagerDutyIncidentPage(false, incidents));
    }

    public async Task<PulledCaseTrigger?> TriggerAsync(
        string pagerDutyIncidentId,
        TeamAccessScope accessScope,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(pagerDutyIncidentId, "PDEMO", StringComparison.Ordinal)
            || !accessScope.Allows("payments")) return null;
        await replay.ResetAsync(cancellationToken);
        return new PulledCaseTrigger(
            DemoCaseStore.CaseId,
            $"/cases/{DemoCaseStore.CaseId}",
            false);
    }
}
