using IncidentBot.Api.Incidents;

namespace IncidentBot.Api.Demo;

public sealed class DemoPagerDutyPullService(
    DemoIncidentStore store,
    DemoReplay replay) : IPagerDutyPullService
{
    public Task<PagerDutyIncidentPage> GetRecentAsync(
        DateTimeOffset since,
        DateTimeOffset until,
        CancellationToken cancellationToken)
    {
        var report = store.Get();
        IReadOnlyList<PagerDutyIncidentSnapshot> incidents =
            report.TriggeredAt >= since && report.TriggeredAt <= until
                ?
                [
                    new PagerDutyIncidentSnapshot(
                        report.PagerDutyIncidentId,
                        2481,
                        report.Title,
                        "triggered",
                        report.Urgency,
                        report.TriggeredAt,
                        report.UpdatedAt,
                        report.ServiceId,
                        "Payments API",
                        ["Alex Chen"],
                        "https://pagerduty.example/incidents/PDEMO",
                        new Dictionary<string, string> { ["service"] = report.ServiceId })
                ]
                : [];
        return Task.FromResult(new PagerDutyIncidentPage(false, incidents));
    }

    public async Task<PulledIncidentTrigger?> TriggerAsync(
        string pagerDutyIncidentId,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(pagerDutyIncidentId, "PDEMO", StringComparison.Ordinal)) return null;
        await replay.ResetAsync(cancellationToken);
        return new PulledIncidentTrigger(
            DemoIncidentStore.IncidentId,
            $"/incidents/{DemoIncidentStore.IncidentId}",
            false);
    }
}
