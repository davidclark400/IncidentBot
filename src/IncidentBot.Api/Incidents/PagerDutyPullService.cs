using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IncidentBot.Api.Connectors;
using IncidentBot.Api.Domain;
using IncidentBot.Api.Profiles;

namespace IncidentBot.Api.Incidents;

public sealed class PagerDutyPullService(
    PagerDutyIncidentClient pagerDuty,
    InvestigationProfileStore profiles,
    IIncidentStore incidents) : IPagerDutyPullService
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

        InvestigationProfile profile;
        try
        {
            profile = profiles.Resolve(incident.ServiceId, incident.Labels);
        }
        catch (InvalidOperationException)
        {
            throw new PagerDutyPullException(
                "No unambiguous investigation profile matches this PagerDuty incident.",
                StatusCodes.Status409Conflict);
        }

        var labels = profiles.FilterPersistedLabels(profile, incident.Labels);
        var webhook = CreateWebhook(incident, labels);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            source = "pagerduty-pull",
            incidentId = incident.Id,
            status = incident.Status,
            createdAt = incident.CreatedAt,
            lastStatusChangeAt = incident.LastStatusChangeAt
        });
        var accepted = await incidents.AcceptWebhookAsync(webhook, profile, payload, cancellationToken);
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
