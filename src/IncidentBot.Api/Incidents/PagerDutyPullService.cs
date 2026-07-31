using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IncidentBot.Api.Connectors;
using IncidentBot.Api.Domain;

namespace IncidentBot.Api.Incidents;

public sealed class PagerDutyPullService(
    PagerDutyIncidentClient pagerDuty,
    IIncidentIntake intake) : IPagerDutyPullService
{
    public Task<PagerDutyIncidentPage> GetRecentAsync(
        DateTimeOffset since,
        DateTimeOffset until,
        CancellationToken cancellationToken) =>
        pagerDuty.GetRecentAsync(since, until, cancellationToken);

    public async Task<PulledIncidentTrigger?> TriggerAsync(
        string pagerDutyIncidentId,
        CancellationToken cancellationToken)
    {
        var incident = await pagerDuty.GetAsync(pagerDutyIncidentId, cancellationToken);
        if (incident is null) return null;

        var webhook = CreateWebhook(incident, incident.Labels);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            source = "pagerduty-pull",
            incidentId = incident.Id,
            status = incident.Status,
            createdAt = incident.CreatedAt,
            lastStatusChangeAt = incident.LastStatusChangeAt
        });
        (Guid IncidentId, bool IsDuplicate) accepted;
        try
        {
            accepted = await intake.AcceptAsync(webhook, payload, cancellationToken);
        }
        catch (InvestigationProfileSelectionException)
        {
            throw new PagerDutyPullException(
                "No unambiguous investigation profile matches this PagerDuty incident.",
                StatusCodes.Status409Conflict);
        }
        return new PulledIncidentTrigger(
            accepted.IncidentId,
            $"/incidents/{accepted.IncidentId}",
            accepted.IsDuplicate);
    }

    internal static PagerDutyWebhookEvent CreateWebhook(
        PagerDutyIncidentSnapshot incident,
        IReadOnlyDictionary<string, string> labels) => new(
            EventId(incident),
            EventType(incident.Status),
            incident.Id,
            incident.ServiceId,
            incident.Title,
            incident.Urgency,
            incident.HtmlUrl,
            incident.CreatedAt,
            incident.LastStatusChangeAt,
            labels);

    internal static string EventType(string status) => status.ToLowerInvariant() switch
    {
        "triggered" => "incident.triggered",
        "acknowledged" => "incident.acknowledged",
        "resolved" => "incident.resolved",
        _ => "incident.triggered"
    };

    private static string EventId(PagerDutyIncidentSnapshot incident)
    {
        var value = $"{incident.Id}|{incident.Status}|{incident.CreatedAt.UtcTicks}|{incident.LastStatusChangeAt.UtcTicks}";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        return $"pagerduty-pull:v2:{hash[..32]}";
    }
}
