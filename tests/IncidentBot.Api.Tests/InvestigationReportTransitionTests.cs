using IncidentBot.Api.Domain;
using IncidentBot.Api.Incidents;
using IncidentBot.Api.Profiles;

namespace IncidentBot.Api.Tests;

public sealed class InvestigationReportTransitionTests
{
    [Fact]
    public async Task CommitOwnsVersionProgressionAndChangedSectionPolicy()
    {
        var incident = Incident(version: 4);
        var report = Report(incident);
        var store = new RecordingStore();
        var updates = new RecordingUpdates();
        var transitions = new InvestigationReportTransitions(store, updates);
        var expected = new Dictionary<InvestigationReportTransition, string[]>
        {
            [InvestigationReportTransition.Initial] = ["status", "timeline", "sources", "problem"],
            [InvestigationReportTransition.CollectionDisabled] = ["status", "problem"],
            [InvestigationReportTransition.CollectionStarted] = ["status", "sources", "problem"],
            [InvestigationReportTransition.Completed] =
                ["status", "summary", "ai", "timeline", "evidence", "sources", "links", "causalEvents", "problem"]
        };

        foreach (var item in expected)
        {
            incident = await transitions.CommitAsync(
                incident,
                report,
                item.Key,
                CancellationToken.None);

            var published = updates.Reports[^1];
            Assert.Equal(incident.Version, published.Version);
            Assert.Equal(item.Value, published.ChangedSections);
        }

        Assert.Equal([4, 5, 6, 7], store.SavedAtVersions);
        Assert.Equal(8, incident.Version);
        Assert.Equal(report.Status, incident.Status);
        Assert.Equal(report.UpdatedAt, incident.UpdatedAt);
    }

    [Fact]
    public async Task CommitReturnsLifecycleChangesThatArriveDuringPersistence()
    {
        var incident = Incident(version: 2);
        var store = new RecordingStore
        {
            AfterSave = saved => saved with
            {
                Title = "Payments recovered",
                State = IncidentState.Resolved,
                IsFrozen = true,
                Labels = new Dictionary<string, string> { ["state"] = "resolved" }
            }
        };
        var transitions = new InvestigationReportTransitions(store, new RecordingUpdates());

        var current = await transitions.CommitAsync(
            incident,
            Report(incident),
            InvestigationReportTransition.Initial,
            CancellationToken.None);

        Assert.Equal(3, current.Version);
        Assert.Equal("Payments recovered", current.Title);
        Assert.Equal(IncidentState.Resolved, current.State);
        Assert.True(current.IsFrozen);
        Assert.Equal("resolved", current.Labels["state"]);
    }

    [Fact]
    public async Task CommitPublishesTheCanonicalStatusThatChangedDuringPersistence()
    {
        var incident = Incident(version: 2);
        var store = new RecordingStore
        {
            AfterSave = saved => saved with
            {
                Version = saved.Version + 1,
                Status = IncidentProgression.Finalizing
            }
        };
        var updates = new RecordingUpdates();
        var transitions = new InvestigationReportTransitions(store, updates);

        var current = await transitions.CommitAsync(
            incident,
            Report(incident),
            InvestigationReportTransition.Completed,
            CancellationToken.None);

        var published = Assert.Single(updates.Reports);
        Assert.Equal(3, published.Version);
        Assert.Equal(IncidentProgression.Finalizing, published.Status);
        Assert.Equal(4, current.Version);
        Assert.Equal(IncidentProgression.Finalizing, current.Status);
    }

    [Fact]
    public async Task StatusTransitionPersistsBeforePublishingAtTheCurrentVersion()
    {
        var incident = Incident(version: 7);
        var events = new List<string>();
        var store = new RecordingStore(events, incident);
        var updates = new RecordingUpdates(events);
        var transitions = new InvestigationReportTransitions(store, updates);

        var changed = await transitions.SetStatusAsync(
            incident,
            IncidentProgression.Collecting,
            CancellationToken.None);

        Assert.Equal(IncidentProgression.Collecting, changed.Status);
        Assert.Equal(["store:collecting", "publish:collecting:7"], events);
    }

    [Fact]
    public async Task StatusTransitionReturnsLifecycleChangesThatArriveDuringPersistence()
    {
        var incident = Incident(version: 7);
        var store = new RecordingStore(initialIncident: incident)
        {
            AfterStatus = current => current with
            {
                Title = "Payments recovered",
                State = IncidentState.Resolved,
                IsFrozen = true,
                Labels = new Dictionary<string, string> { ["state"] = "resolved" }
            }
        };
        var updates = new RecordingUpdates();
        var transitions = new InvestigationReportTransitions(store, updates);

        var changed = await transitions.SetStatusAsync(
            incident,
            IncidentProgression.Collecting,
            CancellationToken.None);

        Assert.Equal("Payments recovered", changed.Title);
        Assert.Equal(IncidentState.Resolved, changed.State);
        Assert.True(changed.IsFrozen);
        Assert.Equal("resolved", changed.Labels["state"]);
        Assert.Equal(IncidentProgression.Collecting, changed.Status);
    }

    private static IncidentRecord Incident(int version) => new(
        Guid.NewGuid(),
        "PD-1",
        "payments",
        "profile",
        "Payments failing",
        "high",
        IncidentState.Triggered,
        DateTimeOffset.Parse("2026-07-11T10:00:00Z"),
        DateTimeOffset.Parse("2026-07-11T10:01:00Z"),
        version,
        IncidentProgression.Queued,
        false,
        null,
        "#incidents",
        null,
        new Dictionary<string, string>());

    private static InvestigationReport Report(IncidentRecord incident) => new(
        incident.Id,
        incident.PagerDutyIncidentId,
        incident.ServiceId,
        incident.ProfileId,
        "test-v1",
        incident.Title,
        incident.Urgency,
        incident.State,
        IncidentProgression.Ready,
        incident.TriggeredAt,
        DateTimeOffset.Parse("2026-07-11T10:05:00Z"),
        incident.Version,
        "Evidence collected.",
        new AiSynthesis("complete", "Evidence collected.", [], [], [], "hash"),
        [],
        [],
        [],
        [],
        []);

    private sealed class RecordingStore(
        ICollection<string>? events = null,
        IncidentRecord? initialIncident = null) : IIncidentStore
    {
        private IncidentRecord? savedIncident = initialIncident;

        public List<int> SavedAtVersions { get; } = [];
        public Func<IncidentRecord, IncidentRecord>? AfterSave { get; init; }
        public Func<IncidentRecord, IncidentRecord>? AfterStatus { get; init; }

        public Task<int> SaveReportAsync(
            IncidentRecord incident,
            InvestigationReport report,
            CancellationToken cancellationToken)
        {
            SavedAtVersions.Add(incident.Version);
            var version = incident.Version + 1;
            savedIncident = incident with
            {
                Version = version,
                Status = report.Status,
                UpdatedAt = report.UpdatedAt
            };
            savedIncident = AfterSave?.Invoke(savedIncident) ?? savedIncident;
            return Task.FromResult(version);
        }

        public Task SetStatusAsync(Guid incidentId, string status, CancellationToken cancellationToken)
        {
            events?.Add($"store:{status}");
            savedIncident = (savedIncident ?? throw new InvalidOperationException("Test incident was not seeded.")) with
            {
                Status = status
            };
            savedIncident = AfterStatus?.Invoke(savedIncident) ?? savedIncident;
            return Task.CompletedTask;
        }

        public Task<IncidentRecord?> GetIncidentAsync(Guid incidentId, CancellationToken cancellationToken) =>
            Task.FromResult(savedIncident);

        public Task<InvestigationReport?> GetReportAsync(Guid incidentId, CancellationToken cancellationToken) =>
            Task.FromResult<InvestigationReport?>(null);

        public Task<bool> RestartInvestigationAsync(
            Guid incidentId,
            string? slackChannel,
            string? slackTimestamp,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<(Guid IncidentId, bool IsDuplicate)> AcceptWebhookAsync(
            PagerDutyWebhookEvent webhook,
            InvestigationProfile profile,
            ReadOnlyMemory<byte> rawPayload,
            CancellationToken cancellationToken) => Task.FromResult((Guid.Empty, false));

        public Task SetSlackTimestampAsync(
            Guid incidentId,
            string timestamp,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken) =>
            Task.FromResult(0);
    }

    private sealed class RecordingUpdates(ICollection<string>? events = null) : IIncidentUpdatePublisher
    {
        public List<PublishedReport> Reports { get; } = [];

        public Task PublishStatusAsync(
            Guid incidentId,
            int version,
            string status,
            CancellationToken cancellationToken)
        {
            events?.Add($"publish:{status}:{version}");
            return Task.CompletedTask;
        }

        public Task PublishReportAsync(
            Guid incidentId,
            int version,
            string status,
            IReadOnlyList<string> changedSections,
            CancellationToken cancellationToken)
        {
            Reports.Add(new PublishedReport(version, status, changedSections.ToArray()));
            return Task.CompletedTask;
        }
    }

    private sealed record PublishedReport(
        int Version,
        string Status,
        IReadOnlyList<string> ChangedSections);
}
