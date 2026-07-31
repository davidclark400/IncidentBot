using IncidentBot.Api.Domain;

namespace IncidentBot.Api.Incidents;

public enum InvestigationReportTransition
{
    Initial,
    CollectionDisabled,
    CollectionStarted,
    Completed
}

/// <summary>
/// Owns versioned report persistence and the matching live-update publication.
/// </summary>
public sealed class InvestigationReportTransitions(
    IIncidentStore incidents,
    IIncidentUpdatePublisher updates)
{
    public async Task<IncidentRecord> CommitAsync(
        IncidentRecord incident,
        InvestigationReport report,
        InvestigationReportTransition transition,
        CancellationToken cancellationToken)
    {
        var version = await incidents.SaveReportAsync(incident, report, cancellationToken);
        var current = await incidents.GetIncidentAsync(incident.Id, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Incident '{incident.Id}' disappeared after the {transition} report transition.");
        await updates.PublishReportAsync(
            current.Id,
            version,
            current.Status,
            ChangedSections(transition),
            cancellationToken);
        return current;
    }

    public async Task<IncidentRecord> SetStatusAsync(
        IncidentRecord incident,
        string status,
        CancellationToken cancellationToken)
    {
        await incidents.SetStatusAsync(incident.Id, status, cancellationToken);
        var current = await incidents.GetIncidentAsync(incident.Id, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Incident '{incident.Id}' disappeared after the '{status}' status transition.");
        await updates.PublishStatusAsync(
            current.Id,
            current.Version,
            current.Status,
            cancellationToken);
        return current;
    }

    internal static IReadOnlyList<string> ChangedSections(InvestigationReportTransition transition) =>
        transition switch
        {
            InvestigationReportTransition.Initial => ["status", "timeline", "sources", "problem"],
            InvestigationReportTransition.CollectionDisabled => ["status", "problem"],
            InvestigationReportTransition.CollectionStarted => ["status", "sources", "problem"],
            InvestigationReportTransition.Completed =>
                ["status", "summary", "ai", "timeline", "evidence", "sources", "links", "causalEvents", "problem"],
            _ => throw new ArgumentOutOfRangeException(nameof(transition), transition, null)
        };
}
