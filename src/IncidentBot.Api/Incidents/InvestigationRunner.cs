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
        IncidentRecord incident,
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
    ReportComposer composer,
    IInvestigationSynthesizer synthesizer,
    IIncidentUpdatePublisher updates,
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
            var initialVersion = await repository.SaveReportAsync(incident, initial, cancellationToken);
            await updates.PublishReportAsync(
                incident.Id, initialVersion, initial.Status, ["status", "timeline", "problem"], cancellationToken);
            incident = await repository.GetIncidentAsync(incidentId, cancellationToken)
                ?? throw new InvalidOperationException($"Incident '{incidentId}' disappeared after initial report.");
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
                var disabledVersion = await repository.SaveReportAsync(incident, updated, cancellationToken);
                await updates.PublishReportAsync(
                    incident.Id, disabledVersion, updated.Status, ["status", "problem"], cancellationToken);
            }
            logger.LogInformation(
                "Investigation completed without evidence collection in {DurationMilliseconds} ms",
                investigationStopwatch.ElapsedMilliseconds);
            return;
        }

        await repository.SetStatusAsync(incident.Id, IncidentProgression.Collecting, cancellationToken);
        await updates.PublishStatusAsync(
            incident.Id, incident.Version, IncidentProgression.Collecting, cancellationToken);

        var context = new InvestigationContext(
            incident.Id, incident.PagerDutyIncidentId, incident.ServiceId, incident.Title, incident.Urgency,
            incident.State, incident.TriggeredAt, incident.Labels, profile);
        var scope = new EvidenceScope(
            incident.TriggeredAt - TimeSpan.FromMinutes(options.Value.EvidenceWindowMinutes),
            timeProvider.GetUtcNow(), profiles.Revision,
            options.Value.EvidenceMaximumItems,
            options.Value.EvidenceMaximumBytes);
        var enabledSources = evidenceSources.EnabledSources(profile);
        var selectedConnectors = evidenceSources.Select(profile);
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
        var collectionStopwatch = Stopwatch.StartNew();
        var tasks = selectedConnectors
            .Select(connector => CollectSafelyAsync(connector, context, scope, cancellationToken));
        var results = await Task.WhenAll(tasks);
        logger.LogInformation(
            "Evidence collection completed in {DurationMilliseconds} ms: {CompleteCount} complete, {PartialCount} partial, {UnavailableCount} unavailable, {FindingCount} findings",
            collectionStopwatch.ElapsedMilliseconds,
            results.Count(result => result.Health == SourceHealth.Complete),
            results.Count(result => result.Health == SourceHealth.Partial),
            results.Count(result => result.Health == SourceHealth.Unavailable),
            results.Sum(result => result.Findings.Count));
        var previous = await repository.GetReportAsync(incident.Id, cancellationToken);
        logger.LogDebug("Synthesis started with {ConnectorResultCount} connector results", results.Length);
        var ai = await synthesizer.SynthesizeAsync(incident, results, previous?.Ai, cancellationToken);
        var report = composer.Compose(incident, profile, profiles.Revision, results, previous, ai);
        report = report with
        {
            Problem = await recurrence.ResolveFinalAsync(incident, report.Evidence, cancellationToken)
        };
        var version = await repository.SaveReportAsync(incident, report, cancellationToken);
        await updates.PublishReportAsync(
            incident.Id, version, report.Status,
            ["summary", "ai", "timeline", "evidence", "sources", "links", "problem"], cancellationToken);
        logger.LogInformation(
            "Investigation completed in {DurationMilliseconds} ms with report version {ReportVersion}, status {ReportStatus}, and synthesis status {SynthesisStatus}",
            investigationStopwatch.ElapsedMilliseconds, version, report.Status, ai.Status);
    }

    private async Task<ConnectorResult> CollectSafelyAsync(
        IIncidentEvidenceConnector connector,
        InvestigationContext context,
        EvidenceScope scope,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await connector.CollectAsync(context, scope, cancellationToken);
            if (result.Health == SourceHealth.Unavailable)
            {
                logger.LogWarning(
                    "Connector {Source} unavailable after {DurationMilliseconds} ms: {Diagnostic}",
                    connector.Source, result.DurationMilliseconds,
                    string.IsNullOrWhiteSpace(result.Diagnostic) ? "No diagnostic supplied" : result.Diagnostic);
            }
            else if (result.Health == SourceHealth.Partial)
            {
                logger.LogWarning(
                    "Connector {Source} returned partial evidence after {DurationMilliseconds} ms with {FindingCount} findings: {Diagnostic}",
                    connector.Source, result.DurationMilliseconds, result.Findings.Count,
                    string.IsNullOrWhiteSpace(result.Diagnostic) ? "No diagnostic supplied" : result.Diagnostic);
            }
            else
            {
                logger.LogDebug(
                    "Connector {Source} completed with health {SourceHealth} in {DurationMilliseconds} ms and returned {FindingCount} findings",
                    connector.Source, result.Health, result.DurationMilliseconds, result.Findings.Count);
            }
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Connector {Source} failed outside its normal failure boundary", connector.Source);
            var diagnostic = exception.Message.Length <= 500 ? exception.Message : exception.Message[..500] + "…";
            return ConnectorResult.Unavailable(connector.Source, stopwatch.ElapsedMilliseconds, diagnostic);
        }
    }
}
