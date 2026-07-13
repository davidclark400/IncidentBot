using IncidentBot.Api.Incidents;

namespace IncidentBot.Api.Demo;

public sealed class DemoPagerDutyPullService(
    DemoIncidentStore store,
    IIncidentUpdatePublisher updates) : IPagerDutyPullService
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
        var reset = store.Reset();
        await updates.PublishReportAsync(
            DemoIncidentStore.IncidentId,
            reset.Report.Version,
            reset.Report.Status,
            ["status", "summary", "timeline", "evidence", "sources", "causalEvents", "ai", "problem"],
            cancellationToken);
        return new PulledIncidentTrigger(
            DemoIncidentStore.IncidentId,
            $"/incidents/{DemoIncidentStore.IncidentId}",
            false);
    }
}
