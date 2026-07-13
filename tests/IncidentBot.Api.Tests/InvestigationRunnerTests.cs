using System.Text.Json.Nodes;
using IncidentBot.Api.Connectors;
using IncidentBot.Api.Domain;
using IncidentBot.Api.Incidents;
using IncidentBot.Api.Fingerprinting;
using IncidentBot.Api.Options;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;

namespace IncidentBot.Api.Tests;

public sealed class InvestigationRunnerTests
{
    [Fact]
    public async Task RunOwnsTheCompleteInvestigationAttempt()
    {
        var incident = new IncidentRecord(
            Guid.NewGuid(), "PD-1", "payments", "profile", "Payments failing", "high",
            IncidentState.Triggered, DateTimeOffset.Parse("2026-07-11T10:00:00Z"),
            DateTimeOffset.Parse("2026-07-11T10:00:01Z"), 1, "queued", false, null,
            "#incidents", null, new Dictionary<string, string>());
        var repository = new RecordingRepository(incident);
        var updates = new RecordingUpdates();
        var connector = new RecordingConnector();
        var evidenceSources = new EvidenceSourceRegistry([connector], TestConfiguration.EvidenceSources());
        var botOptions = BotOptions();
        var runner = new InvestigationRunner(
            repository,
            new ProfileProvider([connector.Source]),
            evidenceSources,
            Collector(botOptions),
            new ReportComposer(TimeProvider.System, evidenceSources),
            new Synthesizer(),
            updates,
            botOptions,
            TimeProvider.System,
            new Recurrence(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<InvestigationRunner>.Instance);

        await runner.RunAsync(incident.Id, CancellationToken.None);

        Assert.Equal("collecting", Assert.Single(repository.Statuses));
        Assert.True(connector.WasCalled);
        Assert.NotNull(repository.SavedReport);
        Assert.Equal("ready", repository.SavedReport.Status);
        Assert.Equal(SourceRequestState.Requested, Assert.Single(repository.SavedReports[0].Sources).RequestState);
        Assert.Equal(SourceRequestState.Received, Assert.Single(repository.SavedReport.Sources).RequestState);
        Assert.Equal(new[] { "status:collecting", "report:collecting", "report:ready" }, updates.Events);
    }

    [Fact]
    public async Task UnexpectedConnectorFailureDoesNotDiscardOtherEvidence()
    {
        var incident = Incident();
        var repository = new RecordingRepository(incident);
        var synthesizer = new RecordingSynthesizer();
        var runner = Runner(repository, [new ThrowingConnector(), new RecordingConnector("nomad")], synthesizer);

        await runner.RunAsync(incident.Id, CancellationToken.None);

        Assert.Equal(2, synthesizer.Results.Count);
        var failed = Assert.Single(synthesizer.Results, result => result.Source == "pagerduty");
        Assert.Equal(SourceHealth.Unavailable, failed.Health);
        Assert.Contains("unexpected collector failure", failed.Diagnostic);
        Assert.Contains(synthesizer.Results, result => result.Source == "nomad" && result.Findings.Count == 1);
        Assert.Equal("ready", repository.SavedReport?.Status);
        Assert.Equal(
            SourceRequestState.Errored,
            repository.SavedReport?.Sources.Single(source => source.Source == "pagerduty").RequestState);
        Assert.Equal(
            SourceRequestState.Received,
            repository.SavedReport?.Sources.Single(source => source.Source == "nomad").RequestState);
    }

    [Fact]
    public async Task FingerprintingFailureDoesNotPreventTheReport()
    {
        var incident = Incident();
        var repository = new RecordingRepository(incident);
        var runner = Runner(repository, [new RecordingConnector()], new Synthesizer(), new Recurrence(fail: true));

        await runner.RunAsync(incident.Id, CancellationToken.None);

        Assert.Equal("ready", repository.SavedReport?.Status);
        Assert.Equal("unavailable", repository.SavedReport?.Problem?.Availability);
        Assert.Equal("InvalidOperationException", repository.SavedReport?.Problem?.Diagnostic);
    }

    [Fact]
    public async Task CollectionDisabledStillPublishesResolutionAndProblemLifecycleUpdates()
    {
        var incident = Incident() with { State = IncidentState.Resolved, IsFrozen = true, Version = 1 };
        var profileProvider = new ProfileProvider([]);
        var profile = profileProvider.Resolve(incident.ServiceId, incident.Labels);
        var evidenceSources = new EvidenceSourceRegistry([], TestConfiguration.EvidenceSources());
        var previous = new ReportComposer(TimeProvider.System, evidenceSources).ComposeInitial(incident, profile, profileProvider.Revision);
        var repository = new RecordingRepository(incident, previous);
        var updates = new RecordingUpdates();
        var botOptions = BotOptions(collectionEnabled: false);
        var runner = new InvestigationRunner(
            repository, profileProvider, evidenceSources, Collector(botOptions),
            new ReportComposer(TimeProvider.System, evidenceSources), new Synthesizer(), updates, botOptions,
            TimeProvider.System, new Recurrence(), NullLogger<InvestigationRunner>.Instance);

        await runner.RunAsync(incident.Id, CancellationToken.None);

        Assert.Equal(IncidentState.Resolved, repository.SavedReport?.State);
        Assert.Equal("resolved", repository.SavedReport?.Status);
        Assert.Equal("available", repository.SavedReport?.Problem?.Availability);
        Assert.Equal(new[] { "report:resolved" }, updates.Events);
    }

    [Fact]
    public async Task ResolvedIncidentStillCollectsAndKeepsItsLifecycleTimeline()
    {
        var incident = Incident() with
        {
            State = IncidentState.Resolved,
            IsFrozen = true,
            Version = 0,
            TriggeredAt = DateTimeOffset.Parse("2026-07-11T10:00:00Z"),
            UpdatedAt = DateTimeOffset.Parse("2026-07-11T10:20:00Z")
        };
        var repository = new RecordingRepository(incident);
        var connector = new RecordingConnector();
        var runner = Runner(repository, [connector], new Synthesizer());

        await runner.RunAsync(incident.Id, CancellationToken.None);

        Assert.True(connector.WasCalled);
        Assert.Equal(
            incident.TriggeredAt - TimeSpan.FromMinutes(30),
            connector.LastScope?.Start);
        Assert.Equal(IncidentProgression.Resolved, repository.SavedReport?.Status);
        Assert.Contains(repository.SavedReport!.Timeline, item =>
            item.Kind == "incident-triggered" && item.OccurredAt == incident.TriggeredAt);
        Assert.Contains(repository.SavedReport.Timeline, item =>
            item.Kind == "incident-state"
            && item.Summary == "PagerDuty incident resolved"
            && item.OccurredAt == incident.UpdatedAt);
    }

    private static IncidentRecord Incident() => new(
        Guid.NewGuid(), "PD-1", "payments", "profile", "Payments failing", "high",
        IncidentState.Triggered, DateTimeOffset.Parse("2026-07-11T10:00:00Z"),
        DateTimeOffset.Parse("2026-07-11T10:00:01Z"), 1, "queued", false, null,
        "#incidents", null, new Dictionary<string, string>());

    private static InvestigationRunner Runner(
        RecordingRepository repository,
        IEnumerable<IIncidentEvidenceConnector> connectors,
        IInvestigationSynthesizer synthesizer,
        IRecurrenceCoordinator? recurrence = null)
    {
        var connectorList = connectors.ToList();
        var evidenceSources = new EvidenceSourceRegistry(connectorList, TestConfiguration.EvidenceSources());
        var botOptions = BotOptions();
        return new InvestigationRunner(
            repository, new ProfileProvider(connectorList.Select(item => item.Source)), evidenceSources,
            Collector(botOptions), new ReportComposer(TimeProvider.System, evidenceSources),
            synthesizer, new RecordingUpdates(), botOptions,
            TimeProvider.System, recurrence ?? new Recurrence(), NullLogger<InvestigationRunner>.Instance);
    }

    private static IOptions<IncidentBotOptions> BotOptions(bool collectionEnabled = true) =>
        Microsoft.Extensions.Options.Options.Create(new IncidentBotOptions
        {
            CollectionEnabled = collectionEnabled,
            EvidenceWindowMinutes = 30,
            EvidenceMaximumWindowMinutes = 30
        });

    private static AdaptiveEvidenceCollector Collector(IOptions<IncidentBotOptions> options) => new(
        options,
        TimeProvider.System,
        NullLogger<AdaptiveEvidenceCollector>.Instance);

    private sealed class RecordingRepository(IncidentRecord incident, InvestigationReport? previousReport = null) : IIncidentStore
    {
        public List<string> Statuses { get; } = [];
        public List<InvestigationReport> SavedReports { get; } = [];
        public InvestigationReport? SavedReport => SavedReports.LastOrDefault();

        public Task<IncidentRecord?> GetIncidentAsync(Guid incidentId, CancellationToken cancellationToken) =>
            Task.FromResult<IncidentRecord?>(incident);

        public Task<InvestigationReport?> GetReportAsync(Guid incidentId, CancellationToken cancellationToken) =>
            Task.FromResult(SavedReport ?? previousReport);

        public Task<int> SaveReportAsync(
            IncidentRecord current,
            InvestigationReport report,
            CancellationToken cancellationToken)
        {
            SavedReports.Add(report);
            return Task.FromResult(current.Version + 1);
        }

        public Task SetStatusAsync(Guid incidentId, string status, CancellationToken cancellationToken)
        {
            Statuses.Add(status);
            return Task.CompletedTask;
        }

        public Task<bool> RestartInvestigationAsync(
            Guid incidentId,
            string? slackChannel,
            string? slackTimestamp,
            CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<(Guid IncidentId, bool IsDuplicate)> AcceptWebhookAsync(
            PagerDutyWebhookEvent webhook,
            InvestigationProfile profile,
            ReadOnlyMemory<byte> rawPayload,
            CancellationToken cancellationToken) => Task.FromResult((incident.Id, false));

        public Task SetSlackTimestampAsync(Guid incidentId, string timestamp, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken) =>
            Task.FromResult(0);
    }

    private sealed class ProfileProvider(IEnumerable<string> enabledSources) : IInvestigationProfileProvider
    {
        private readonly HashSet<string> sources = enabledSources.ToHashSet(StringComparer.Ordinal);
        public string Revision => "test-v1";

        public InvestigationProfile Resolve(string serviceId, IReadOnlyDictionary<string, string> labels) =>
            new()
            {
                Id = "profile",
                PagerDutyServiceId = serviceId,
                SlackChannel = "#incidents",
                PagerDuty = sources.Contains(EvidenceSourceRegistry.PagerDuty) ? new PagerDutyScope() : null,
                Nomad = sources.Contains(EvidenceSourceRegistry.Nomad) ? new NomadScope() : null
            };
    }

    private sealed class RecordingConnector(string source = "pagerduty") : IIncidentEvidenceConnector
    {
        public string Source => source;
        public bool WasCalled { get; private set; }
        public EvidenceScope? LastScope { get; private set; }

        public Task<ConnectorResult> CollectAsync(
            InvestigationContext context,
            EvidenceScope scope,
            CancellationToken cancellationToken)
        {
            WasCalled = true;
            LastScope = scope;
            var finding = new EvidenceFinding(
                "finding-1", Source, scope.End, null, "incident", "warning", "Signal found",
                null, null, 0.9, new JsonObject());
            return Task.FromResult(new ConnectorResult(
                Source, SourceHealth.Complete, [finding], [], [], 10, null));
        }
    }

    private sealed class ThrowingConnector : IIncidentEvidenceConnector
    {
        public string Source => "pagerduty";

        public Task<ConnectorResult> CollectAsync(
            InvestigationContext context,
            EvidenceScope scope,
            CancellationToken cancellationToken) =>
            throw new NullReferenceException("unexpected collector failure");
    }

    private sealed class Synthesizer : IInvestigationSynthesizer
    {
        public Task<AiSynthesis> SynthesizeAsync(
            IncidentRecord incident,
            IReadOnlyList<ConnectorResult> results,
            AiSynthesis? previous,
            CancellationToken cancellationToken) =>
            Task.FromResult(new AiSynthesis("complete", "Signal found", [], [], [], "hash"));
    }

    private sealed class RecordingSynthesizer : IInvestigationSynthesizer
    {
        public IReadOnlyList<ConnectorResult> Results { get; private set; } = [];

        public Task<AiSynthesis> SynthesizeAsync(
            IncidentRecord incident,
            IReadOnlyList<ConnectorResult> results,
            AiSynthesis? previous,
            CancellationToken cancellationToken)
        {
            Results = results;
            return Task.FromResult(new AiSynthesis("complete", "Signal found", [], [], [], "hash"));
        }
    }

    private sealed class RecordingUpdates : IIncidentUpdatePublisher
    {
        public List<string> Events { get; } = [];

        public Task PublishStatusAsync(
            Guid incidentId,
            int version,
            string status,
            CancellationToken cancellationToken)
        {
            Events.Add($"status:{status}");
            return Task.CompletedTask;
        }

        public Task PublishReportAsync(
            Guid incidentId,
            int version,
            string status,
            IReadOnlyList<string> changedSections,
            CancellationToken cancellationToken)
        {
            Events.Add($"report:{status}");
            return Task.CompletedTask;
        }
    }

    private sealed class Recurrence(bool fail = false) : IRecurrenceCoordinator
    {
        public Task<ProblemContext> ResolveProvisionalAsync(
            IncidentRecord incident,
            bool collectionEnabled,
            CancellationToken cancellationToken) => Task.FromResult(Context(incident, FingerprintStage.Provisional));

        public Task<ProblemContext> ResolveFinalAsync(
            IncidentRecord incident,
            IReadOnlyList<EvidenceFinding> evidence,
            CancellationToken cancellationToken) => Task.FromResult(fail
                ? new ProblemContext("unavailable", "v1", FingerprintStage.Final, null, null, null, null, null,
                    [], 0, null, null, [], [], 0, "InvalidOperationException")
                : Context(incident, FingerprintStage.Final));

        private static ProblemContext Context(IncidentRecord incident, FingerprintStage stage) =>
            new("available", "v1", stage, "PAYMENTS-FAILING-1234", Guid.NewGuid(),
                ProblemLifecycleState.New, "new", null, ["new deterministic fingerprint"], 1,
                incident.TriggeredAt, incident.TriggeredAt, [], [], 1);
    }
}
