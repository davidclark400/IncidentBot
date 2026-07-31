using IncidentBot.Api.Connectors;
using IncidentBot.Api.Domain;
using IncidentBot.Api.Fingerprinting;
using IncidentBot.Api.Options;
using IncidentBot.Api.Profiles;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace IncidentBot.Api.Incidents;

public interface IInvestigationProfileProvider
{
    string Revision { get; }
    InvestigationProfile Resolve(string serviceId, IReadOnlyDictionary<string, string> labels);
}

public interface IInvestigationSynthesizer
{
    Task<AiSynthesis> SynthesizeAsync(
        InvestigationSubject subject,
        IReadOnlyList<ConnectorResult> results,
        AiSynthesis? previous,
        CancellationToken cancellationToken);
}

public interface IIncidentUpdatePublisher
{
    Task PublishStatusAsync(Guid incidentId, int version, string status, CancellationToken cancellationToken);
    Task PublishReportAsync(
        Guid incidentId,
        int version,
        string status,
        IReadOnlyList<string> changedSections,
        CancellationToken cancellationToken);
}

public sealed class InvestigationRunner(
    IIncidentStore repository,
    IInvestigationProfileProvider profiles,
    EvidenceSourceRegistry evidenceSources,
    AdaptiveEvidenceCollector evidenceCollector,
    ReportComposer composer,
    IInvestigationSynthesizer synthesizer,
    InvestigationReportTransitions transitions,
    IOptions<IncidentBotOptions> options,
    TimeProvider timeProvider,
    IRecurrenceCoordinator recurrence,
    ILogger<InvestigationRunner> logger)
{
    public async Task RunAsync(Guid incidentId, CancellationToken cancellationToken)
    {
        var investigationStopwatch = Stopwatch.StartNew();
        var incident = await repository.GetIncidentAsync(incidentId, cancellationToken)
            ?? throw new InvalidOperationException($"Incident '{incidentId}' no longer exists.");
        var profile = profiles.Resolve(incident.ServiceId, incident.Labels);
        using var logScope = logger.BeginScope(new Dictionary<string, object>
        {
            ["IncidentId"] = incident.Id,
            ["PagerDutyIncidentId"] = incident.PagerDutyIncidentId,
            ["ServiceId"] = incident.ServiceId,
            ["ProfileId"] = profile.Id,
            ["IncidentVersion"] = incident.Version
        });
        logger.LogInformation(
            "Investigation started with profile revision {ProfileRevision}; collection enabled: {CollectionEnabled}",
            profiles.Revision, options.Value.CollectionEnabled);
        var provisionalContext = await recurrence.ResolveProvisionalAsync(
            incident, options.Value.CollectionEnabled, cancellationToken);

        var createdInitialReport = false;
        if (incident.Version == 0)
        {
            var initial = composer.ComposeInitial(incident, profile, profiles.Revision, provisionalContext);
            incident = await transitions.CommitAsync(
                incident,
                initial,
                InvestigationReportTransition.Initial,
                cancellationToken);
            createdInitialReport = true;
        }

        if (!options.Value.CollectionEnabled)
        {
            if (!createdInitialReport && await repository.GetReportAsync(incident.Id, cancellationToken) is { } disabledPrevious)
            {
                var updated = disabledPrevious with
                {
                    Title = incident.Title,
                    State = incident.State,
                    Status = IncidentProgression.ForDisabledCollection(incident.IsFrozen),
                    UpdatedAt = timeProvider.GetUtcNow(),
                    Problem = provisionalContext
                };
                await transitions.CommitAsync(
                    incident,
                    updated,
                    InvestigationReportTransition.CollectionDisabled,
                    cancellationToken);
            }
            logger.LogInformation(
                "Investigation completed without evidence collection in {DurationMilliseconds} ms",
                investigationStopwatch.ElapsedMilliseconds);
            return;
        }

        incident = await transitions.SetStatusAsync(
            incident,
            IncidentProgression.Collecting,
            cancellationToken);

        var context = new InvestigationContext(
            incident.Id, incident.PagerDutyIncidentId, incident.ServiceId, incident.Title, incident.Urgency,
            incident.State, incident.TriggeredAt, incident.Labels, profile);
        var enabledSources = evidenceSources.EnabledSources(profile);
        var selectedConnectors = evidenceSources.Select(profile);
        if (!createdInitialReport)
        {
            var previousReport = await repository.GetReportAsync(incident.Id, cancellationToken);
            var requested = composer.ComposeCollectionStarted(
                incident, profile, profiles.Revision, previousReport, provisionalContext);
            incident = await transitions.CommitAsync(
                incident,
                requested,
                InvestigationReportTransition.CollectionStarted,
                cancellationToken);
        }
        if (selectedConnectors.Count == 0)
        {
            logger.LogWarning(
                "Evidence collection has no selected connectors; enabled profile sources were {EnabledSources}",
                string.Join(',', enabledSources));
        }
        else
        {
            logger.LogInformation(
                "Evidence collection started for {ConnectorCount} connectors: {ConnectorSources}",
                selectedConnectors.Count, string.Join(',', selectedConnectors.Select(connector => connector.Source)));
        }
        var collection = await evidenceCollector.CollectAsync(
            context,
            profiles.Revision,
            selectedConnectors,
            cancellationToken);
        var results = collection.ConnectorResults;
        var previous = await repository.GetReportAsync(incident.Id, cancellationToken);
        logger.LogDebug("Synthesis started with {ConnectorResultCount} connector results", results.Count);
        var ai = await synthesizer.SynthesizeAsync(
            InvestigationSubject.FromIncident(incident),
            results,
            previous?.Ai,
            cancellationToken);
        var report = composer.Compose(
            incident,
            profile,
            profiles.Revision,
            results,
            previous,
            ai,
            collectionOutcome: collection.Outcome);
        report = report with
        {
            Problem = await recurrence.ResolveFinalAsync(incident, report.Evidence, cancellationToken)
        };
        incident = await transitions.CommitAsync(
            incident,
            report,
            InvestigationReportTransition.Completed,
            cancellationToken);
        logger.LogInformation(
            "Investigation completed in {DurationMilliseconds} ms with report version {ReportVersion}, status {ReportStatus}, synthesis status {SynthesisStatus}, and evidence completion reason {EvidenceCompletionReason}",
            investigationStopwatch.ElapsedMilliseconds,
            incident.Version,
            report.Status,
            ai.Status,
            collection.Outcome.CompletionReason);
    }

}
