using IncidentBot.Api.Domain;
using IncidentBot.Api.Profiles;

namespace IncidentBot.Api.Incidents;

/// <summary>
/// Accepts a transport-validated PagerDuty event into the durable investigation workflow.
/// </summary>
public interface IIncidentIntake
{
    Task<(Guid IncidentId, bool IsDuplicate)> AcceptAsync(
        PagerDutyWebhookEvent incident,
        ReadOnlyMemory<byte> rawPayload,
        CancellationToken cancellationToken);
}

public sealed class IncidentIntake(
    InvestigationProfileStore profiles,
    IIncidentStore incidents) : IIncidentIntake
{
    public async Task<(Guid IncidentId, bool IsDuplicate)> AcceptAsync(
        PagerDutyWebhookEvent incident,
        ReadOnlyMemory<byte> rawPayload,
        CancellationToken cancellationToken)
    {
        InvestigationProfile profile;
        try
        {
            profile = profiles.Resolve(incident.ServiceId, incident.Labels);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvestigationProfileSelectionException(exception.Message, exception);
        }

        var acceptedIncident = incident with
        {
            Labels = profiles.FilterPersistedLabels(profile, incident.Labels)
        };
        return await incidents.AcceptWebhookAsync(
            acceptedIncident,
            profile,
            rawPayload,
            cancellationToken);
    }
}

public sealed class InvestigationProfileSelectionException(string message, Exception innerException)
    : InvalidOperationException(message, innerException);
