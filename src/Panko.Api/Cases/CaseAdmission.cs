using Panko.Api.Domain;
using Panko.Api.Recipes;

namespace Panko.Api.Cases;

/// <summary>
/// Accepts a transport-validated origin event into the durable Case workflow.
/// </summary>
public interface ICaseAdmission
{
    Task<(Guid CaseId, bool IsDuplicate)> AcceptAsync(
        AcceptCaseOriginEvent originEvent,
        CaseOriginEventReceipt receipt,
        CancellationToken cancellationToken);
}

public sealed class CaseAdmission(
    RecipeStore recipes,
    ICaseStore cases) : ICaseAdmission
{
    public async Task<(Guid CaseId, bool IsDuplicate)> AcceptAsync(
        AcceptCaseOriginEvent originEvent,
        CaseOriginEventReceipt receipt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(originEvent);
        ArgumentNullException.ThrowIfNull(receipt);
        Validate(originEvent, receipt);

        Recipe recipe;
        try
        {
            recipe = recipes.ResolveById(originEvent.RecipeId);
        }
        catch (InvalidOperationException exception)
        {
            throw new RecipeSelectionException(exception.Message, exception);
        }
        if (string.Equals(recipe.Team, "unmapped", StringComparison.Ordinal))
        {
            var exception = new InvalidOperationException(
                $"Service '{originEvent.ServiceId}' has no team-owned Recipe.");
            throw new RecipeSelectionException(exception.Message, exception);
        }

        if (originEvent.Origin.Kind == CaseOriginKind.PagerDuty
            && !string.Equals(recipe.PagerDutyServiceId, originEvent.ServiceId, StringComparison.Ordinal))
        {
            throw new RecipeSelectionException(
                $"Recipe '{recipe.Id}' does not own PagerDuty service '{originEvent.ServiceId}'.",
                new InvalidOperationException("PagerDuty service ownership did not match the selected Recipe."));
        }

        var acceptedEvent = originEvent with
        {
            Labels = recipes.FilterPersistedLabels(recipe, originEvent.Labels)
        };
        return await cases.AcceptOriginEventAsync(
            acceptedEvent,
            recipe,
            receipt,
            cancellationToken);
    }

    private static void Validate(
        AcceptCaseOriginEvent originEvent,
        CaseOriginEventReceipt receipt)
    {
        if (string.IsNullOrWhiteSpace(originEvent.Origin.ExternalId))
        {
            throw new ArgumentException("Origin events require a durable external ID.", nameof(originEvent));
        }
        if (string.IsNullOrWhiteSpace(originEvent.RecipeId)
            || string.IsNullOrWhiteSpace(originEvent.ServiceId))
        {
            throw new ArgumentException("Origin events require Recipe and service IDs.", nameof(originEvent));
        }
        if (originEvent.LifecycleCrumb.Kind != Panko.Contracts.SubmittedCrumbKind.Event)
        {
            throw new ArgumentException("Origin lifecycle inputs must be events.", nameof(originEvent));
        }
        if (originEvent.LifecycleCrumb.OccurredAt.ToUniversalTime()
            != originEvent.OccurredAt.ToUniversalTime())
        {
            throw new ArgumentException(
                "The lifecycle event occurrence time must match the origin event occurrence time.",
                nameof(originEvent));
        }
        if (string.IsNullOrWhiteSpace(receipt.ProducerPrincipal)
            || string.IsNullOrWhiteSpace(receipt.IdempotencyKey)
            || string.IsNullOrWhiteSpace(receipt.SourceEventType))
        {
            throw new ArgumentException("Origin event receipt fields are required.", nameof(receipt));
        }
        if (!string.Equals(
                receipt.IdempotencyKey,
                originEvent.LifecycleCrumb.ClientCrumbId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The receipt idempotency key must match the lifecycle event client ID.",
                nameof(receipt));
        }
    }
}

public sealed class RecipeSelectionException(string message, Exception innerException)
    : InvalidOperationException(message, innerException);
