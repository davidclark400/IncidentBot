using Panko.Api.Domain;
using Panko.Api.Cases;
using Panko.Api.Recipes;
using DomainIncidentState = Panko.Api.Domain.PagerDutyIncidentState;
using SubmittedCrumbKind = Panko.Contracts.SubmittedCrumbKind;
using SubmittedCrumb = Panko.Contracts.SubmittedCrumb;

namespace Panko.Api.Cases;

/// <summary>
/// Translates authenticated PagerDuty inputs into source-neutral Case admission.
/// PagerDuty vocabulary and recipe selection stop at this adapter.
/// </summary>
public sealed class PagerDutyCaseAdapter(
    RecipeStore recipes,
    ICaseAdmission admission)
{
    internal const string ProducerPrincipal = "pagerduty-adapter";

    public async Task<(Guid CaseId, bool IsDuplicate)> AcceptAsync(
        PagerDutyWebhookEvent webhook,
        ReadOnlyMemory<byte> rawPayload,
        CancellationToken cancellationToken,
        bool isAuthoritativeSnapshot = false)
    {
        Recipe recipe;
        try
        {
            recipe = recipes.Resolve(webhook.ServiceId, webhook.Labels);
        }
        catch (InvalidOperationException exception)
        {
            throw new RecipeSelectionException(exception.Message, exception);
        }
        if (string.Equals(recipe.Team, "unmapped", StringComparison.Ordinal))
        {
            var exception = new InvalidOperationException(
                $"PagerDuty service '{webhook.ServiceId}' has no team-owned Recipe.");
            throw new RecipeSelectionException(exception.Message, exception);
        }

        return await admission.AcceptAsync(
            Map(webhook, recipe.Id),
            new CaseOriginEventReceipt(
                ProducerPrincipal,
                webhook.EventId,
                webhook.EventType,
                rawPayload,
                isAuthoritativeSnapshot),
            cancellationToken);
    }

    internal static AcceptCaseOriginEvent Map(
        PagerDutyWebhookEvent webhook,
        string recipeId)
    {
        var category = LifecycleCategory(webhook.EventType);
        var occurredAt = webhook.OccurredAt.ToUniversalTime();
        var lifecycleEvent = new SubmittedCrumb(
            webhook.EventId,
            SubmittedCrumbKind.Event,
            occurredAt,
            category,
            string.Equals(webhook.Urgency, "high", StringComparison.OrdinalIgnoreCase)
                ? "critical"
                : "info",
            LifecycleSummary(webhook.EventType),
            DeclaredSource: "pagerduty",
            SourceReference: webhook.PagerDutyIncidentId,
            Url: webhook.HtmlUrl,
            ObjectType: "pagerduty-incident",
            ObjectId: webhook.PagerDutyIncidentId);

        return new AcceptCaseOriginEvent(
            new CaseOrigin(
                CaseOriginKind.PagerDuty,
                webhook.PagerDutyIncidentId),
            recipeId,
            webhook.ServiceId,
            webhook.Title,
            webhook.Urgency,
            LifecycleState(webhook.EventType),
            webhook.TriggeredAt.ToUniversalTime(),
            occurredAt,
            webhook.Labels,
            lifecycleEvent);
    }

    internal static DomainIncidentState LifecycleState(string eventType) => eventType switch
    {
        "incident.triggered" or "incident.reopened" => DomainIncidentState.Triggered,
        "incident.acknowledged" => DomainIncidentState.Acknowledged,
        "incident.escalated" => DomainIncidentState.Escalated,
        "incident.reassigned" => DomainIncidentState.Reassigned,
        "incident.resolved" => DomainIncidentState.Resolved,
        _ => DomainIncidentState.Unknown
    };

    internal static string LifecycleCategory(string eventType) => eventType switch
    {
        "incident.triggered" => "pagerduty-incident-triggered",
        "incident.acknowledged" => "pagerduty-incident-acknowledged",
        "incident.escalated" => "pagerduty-incident-escalated",
        "incident.reassigned" => "pagerduty-incident-reassigned",
        "incident.resolved" => "pagerduty-incident-resolved",
        "incident.reopened" => "pagerduty-incident-reopened",
        _ => "pagerduty-incident-updated"
    };

    internal static string LifecycleSummary(string eventType) => eventType switch
    {
        "incident.triggered" => "PagerDuty incident triggered",
        "incident.acknowledged" => "PagerDuty incident acknowledged",
        "incident.escalated" => "PagerDuty incident escalated",
        "incident.reassigned" => "PagerDuty incident reassigned",
        "incident.resolved" => "PagerDuty incident resolved",
        "incident.reopened" => "PagerDuty incident reopened",
        _ => "PagerDuty incident updated"
    };
}
