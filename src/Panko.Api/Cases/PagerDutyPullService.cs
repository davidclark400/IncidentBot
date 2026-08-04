using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Panko.Api.Crumbs;
using Panko.Api.Domain;
using Panko.Api.Recipes;
using Panko.Api.Security;

namespace Panko.Api.Cases;

public sealed class PagerDutyPullService(
    PagerDutyIncidentClient pagerDuty,
    RecipeStore recipes,
    PagerDutyCaseAdapter caseAdapter) : IPagerDutyPullService
{
    internal const string EventIdPrefix = "pagerduty-pull:v2:";

    public async Task<PagerDutyIncidentPage> GetRecentAsync(
        DateTimeOffset since,
        DateTimeOffset until,
        IReadOnlyList<string> authorizedServiceIds,
        CancellationToken cancellationToken)
    {
        if (authorizedServiceIds.Count == 0)
        {
            return new PagerDutyIncidentPage(false, []);
        }

        return await pagerDuty.GetRecentAsync(
            since, until, authorizedServiceIds, cancellationToken);
    }

    public async Task<PulledCaseTrigger?> TriggerAsync(
        string pagerDutyIncidentId,
        TeamAccessScope accessScope,
        CancellationToken cancellationToken)
    {
        var incident = await pagerDuty.GetAsync(pagerDutyIncidentId, cancellationToken);
        if (incident is null) return null;

        var serviceOwnership = recipes.All
            .Where(candidate => string.Equals(
                candidate.PagerDutyServiceId, incident.ServiceId, StringComparison.Ordinal))
            .ToArray();
        if (serviceOwnership.Length == 0
            || serviceOwnership.All(candidate => !accessScope.Allows(candidate.Team)))
        {
            return null;
        }

        Recipe recipe;
        try
        {
            recipe = recipes.Resolve(incident.ServiceId, incident.Labels);
        }
        catch (InvalidOperationException)
        {
            throw new PagerDutyPullException(
                "No unambiguous Recipe matches this PagerDuty incident.",
                StatusCodes.Status409Conflict);
        }
        if (!accessScope.Allows(recipe.Team))
        {
            return null;
        }

        var webhook = CreateWebhook(incident, incident.Labels);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            source = "pagerduty-pull",
            pagerDutyIncidentId = incident.Id,
            status = incident.Status,
            createdAt = incident.CreatedAt,
            lastStatusChangeAt = incident.LastStatusChangeAt
        });
        (Guid CaseId, bool IsDuplicate) accepted;
        try
        {
            accepted = await caseAdapter.AcceptAsync(
                webhook,
                payload,
                cancellationToken,
                isAuthoritativeSnapshot: true);
        }
        catch (RecipeSelectionException)
        {
            throw new PagerDutyPullException(
                "No unambiguous Recipe matches this PagerDuty incident.",
                StatusCodes.Status409Conflict);
        }
        return new PulledCaseTrigger(
            accepted.CaseId,
            $"/cases/{accepted.CaseId}",
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
        return $"{EventIdPrefix}{hash[..32]}";
    }
}
