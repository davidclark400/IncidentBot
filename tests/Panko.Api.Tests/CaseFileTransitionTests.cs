using Panko.Api.Domain;
using Panko.Api.Cases;
using Panko.Api.Recipes;

namespace Panko.Api.Tests;

public sealed class CaseFileTransitionTests
{
    [Fact]
    public async Task CommitOwnsVersionProgressionAndChangedSectionPolicy()
    {
        var caseRecord = BuildCase(version: 4);
        var caseFile = BuildCaseFile(caseRecord);
        var store = new RecordingStore();
        var updates = new RecordingUpdates();
        var transitions = new CaseFileTransitions(store, updates);
        var completedAttemptId = Guid.NewGuid();
        var expected = new Dictionary<CaseFileTransition, string[]>
        {
            [CaseFileTransition.Initial] = ["status", "trail", "crumbSources", "pattern"],
            [CaseFileTransition.CollectionDisabled] = ["status", "pattern"],
            [CaseFileTransition.CollectionStarted] = ["status", "crumbSources", "pattern"],
            [CaseFileTransition.Completed] =
                ["status", "summary", "ai", "trail", "crumbs", "crumbSources", "links", "causalMarkers", "pattern"]
        };

        foreach (var item in expected)
        {
            caseRecord = await transitions.CommitAsync(
                caseRecord,
                caseFile,
                item.Key,
                CancellationToken.None,
                item.Key == CaseFileTransition.Completed
                    ? completedAttemptId
                    : null);

            var published = updates.PublishedCaseFiles[^1];
            Assert.Equal(caseRecord.Version, published.Version);
            Assert.Equal(item.Value, published.ChangedSections);
        }

        Assert.Equal([4, 5, 6, 7], store.SavedAtVersions);
        Assert.Equal([null, null, null, completedAttemptId], store.SavedProgressAttempts);
        Assert.Equal(8, caseRecord.Version);
        Assert.Equal(caseFile.Status, caseRecord.Status);
        Assert.Equal(caseFile.UpdatedAt, caseRecord.UpdatedAt);
    }

    [Fact]
    public async Task CommitReturnsLifecycleChangesThatArriveDuringPersistence()
    {
        var caseRecord = BuildCase(version: 2);
        var store = new RecordingStore
        {
            AfterSave = saved => saved with
            {
                Title = "Payments recovered",
                PagerDutyState = PagerDutyIncidentState.Resolved,
                IsFrozen = true,
                Labels = new Dictionary<string, string> { ["state"] = "resolved" }
            }
        };
        var transitions = new CaseFileTransitions(store, new RecordingUpdates());

        var current = await transitions.CommitAsync(
            caseRecord,
            BuildCaseFile(caseRecord),
            CaseFileTransition.Initial,
            CancellationToken.None);

        Assert.Equal(3, current.Version);
        Assert.Equal("Payments recovered", current.Title);
        Assert.Equal(PagerDutyIncidentState.Resolved, current.PagerDutyState);
        Assert.True(current.IsFrozen);
        Assert.Equal("resolved", current.Labels["state"]);
    }

    [Fact]
    public async Task CommitPublishesTheCanonicalStatusThatChangedDuringPersistence()
    {
        var caseRecord = BuildCase(version: 2);
        var store = new RecordingStore
        {
            AfterSave = saved => saved with
            {
                Version = saved.Version + 1,
                Status = CaseProgression.Finalizing
            }
        };
        var updates = new RecordingUpdates();
        var transitions = new CaseFileTransitions(store, updates);

        var current = await transitions.CommitAsync(
            caseRecord,
            BuildCaseFile(caseRecord),
            CaseFileTransition.Completed,
            CancellationToken.None,
            Guid.NewGuid());

        var published = Assert.Single(updates.PublishedCaseFiles);
        Assert.Equal(3, published.Version);
        Assert.Equal(CaseProgression.Finalizing, published.Status);
        Assert.Equal(4, current.Version);
        Assert.Equal(CaseProgression.Finalizing, current.Status);
    }

    [Fact]
    public async Task CompletedTransitionRequiresANonEmptyProgressAttemptBeforePersistence()
    {
        var caseRecord = BuildCase(version: 2);
        var store = new RecordingStore();
        var updates = new RecordingUpdates();
        var transitions = new CaseFileTransitions(store, updates);

        foreach (var attemptId in new Guid?[] { null, Guid.Empty })
        {
            var exception = await Assert.ThrowsAsync<ArgumentException>(() => transitions.CommitAsync(
                caseRecord,
                BuildCaseFile(caseRecord),
                CaseFileTransition.Completed,
                CancellationToken.None,
                attemptId));

            Assert.Equal("progressAttemptId", exception.ParamName);
        }
        Assert.Empty(store.SavedAtVersions);
        Assert.Empty(updates.PublishedCaseFiles);
    }

    [Fact]
    public async Task NonCompletedTransitionsRejectAProgressAttemptBeforePersistence()
    {
        var caseRecord = BuildCase(version: 2);
        var store = new RecordingStore();
        var updates = new RecordingUpdates();
        var transitions = new CaseFileTransitions(store, updates);

        foreach (var transition in Enum.GetValues<CaseFileTransition>()
                     .Where(value => value != CaseFileTransition.Completed))
        {
            var exception = await Assert.ThrowsAsync<ArgumentException>(() => transitions.CommitAsync(
                caseRecord,
                BuildCaseFile(caseRecord),
                transition,
                CancellationToken.None,
                Guid.NewGuid()));

            Assert.Equal("progressAttemptId", exception.ParamName);
        }

        Assert.Empty(store.SavedAtVersions);
        Assert.Empty(updates.PublishedCaseFiles);
    }

    [Fact]
    public async Task StatusTransitionPersistsBeforePublishingAtTheCurrentVersion()
    {
        var caseRecord = BuildCase(version: 7);
        var events = new List<string>();
        var store = new RecordingStore(events, caseRecord);
        var updates = new RecordingUpdates(events);
        var transitions = new CaseFileTransitions(store, updates);

        var changed = await transitions.SetStatusAsync(
            caseRecord,
            CaseProgression.Collecting,
            CancellationToken.None);

        Assert.Equal(CaseProgression.Collecting, changed.Status);
        Assert.Equal(["store:collecting", "publish:collecting:7"], events);
    }

    [Fact]
    public async Task StatusTransitionReturnsLifecycleChangesThatArriveDuringPersistence()
    {
        var caseRecord = BuildCase(version: 7);
        var store = new RecordingStore(initialCase: caseRecord)
        {
            AfterStatus = current => current with
            {
                Title = "Payments recovered",
                PagerDutyState = PagerDutyIncidentState.Resolved,
                IsFrozen = true,
                Labels = new Dictionary<string, string> { ["state"] = "resolved" }
            }
        };
        var updates = new RecordingUpdates();
        var transitions = new CaseFileTransitions(store, updates);

        var changed = await transitions.SetStatusAsync(
            caseRecord,
            CaseProgression.Collecting,
            CancellationToken.None);

        Assert.Equal("Payments recovered", changed.Title);
        Assert.Equal(PagerDutyIncidentState.Resolved, changed.PagerDutyState);
        Assert.True(changed.IsFrozen);
        Assert.Equal("resolved", changed.Labels["state"]);
        Assert.Equal(CaseProgression.Collecting, changed.Status);
    }

    private static CaseRecord BuildCase(int version) => new(
        Guid.NewGuid(),
        "PD-1",
        "payments",
        "recipe",
        "Payments failing",
        "high",
        PagerDutyIncidentState.Triggered,
        DateTimeOffset.Parse("2026-07-11T10:00:00Z"),
        DateTimeOffset.Parse("2026-07-11T10:01:00Z"),
        version,
        CaseProgression.Queued,
        false,
        null,
        "#cases",
        null,
        new Dictionary<string, string>());

    private static CaseFile BuildCaseFile(CaseRecord caseRecord) => new(
        caseRecord.Id,
        caseRecord.PagerDutyIncidentId,
        caseRecord.ServiceId,
        caseRecord.RecipeId,
        "test-v1",
        caseRecord.Title,
        caseRecord.Urgency,
        caseRecord.PagerDutyState,
        CaseProgression.Ready,
        caseRecord.OpenedAt,
        DateTimeOffset.Parse("2026-07-11T10:05:00Z"),
        caseRecord.Version,
        "Crumbs collected.",
        new AiSynthesis("complete", "Crumbs collected.", [], [], [], "hash"),
        [],
        [],
        [],
        [],
        []);

    private sealed class RecordingStore(
        ICollection<string>? events = null,
        CaseRecord? initialCase = null) : ICaseStore
    {
        private CaseRecord? savedCase = initialCase;

        public List<int> SavedAtVersions { get; } = [];
        public List<Guid?> SavedProgressAttempts { get; } = [];
        public Func<CaseRecord, CaseRecord>? AfterSave { get; init; }
        public Func<CaseRecord, CaseRecord>? AfterStatus { get; init; }

        public Task<int> SaveCaseFileAsync(
            CaseRecord caseRecord,
            CaseFile caseFile,
            CancellationToken cancellationToken) =>
            SaveCaseFileAsync(caseRecord, caseFile, null, cancellationToken);

        public Task<int> SaveCaseFileAsync(
            CaseRecord caseRecord,
            CaseFile caseFile,
            Guid? progressAttemptId,
            CancellationToken cancellationToken)
        {
            SavedAtVersions.Add(caseRecord.Version);
            SavedProgressAttempts.Add(progressAttemptId);
            var version = caseRecord.Version + 1;
            savedCase = caseRecord with
            {
                Version = version,
                Status = caseFile.Status,
                UpdatedAt = caseFile.UpdatedAt
            };
            savedCase = AfterSave?.Invoke(savedCase) ?? savedCase;
            return Task.FromResult(version);
        }

        public Task SetStatusAsync(Guid caseId, string status, CancellationToken cancellationToken)
        {
            events?.Add($"store:{status}");
            savedCase = (savedCase ?? throw new InvalidOperationException("Test Case was not seeded.")) with
            {
                Status = status
            };
            savedCase = AfterStatus?.Invoke(savedCase) ?? savedCase;
            return Task.CompletedTask;
        }

        public Task<CaseRecord?> GetCaseAsync(Guid caseId, CancellationToken cancellationToken) =>
            Task.FromResult(savedCase);

        public Task<CaseFile?> GetCaseFileAsync(Guid caseId, CancellationToken cancellationToken) =>
            Task.FromResult<CaseFile?>(null);

        public Task<CaseProgress?> GetProgressAsync(
            Guid caseId,
            CancellationToken cancellationToken) =>
            Task.FromResult<CaseProgress?>(null);

        public Task<long?> BeginProgressAsync(
            CaseProgress progress,
            CancellationToken cancellationToken) => Task.FromResult<long?>(1);

        public Task<long?> UpdateProgressAsync(
            CaseProgress progress,
            CancellationToken cancellationToken) => Task.FromResult<long?>(progress.Revision + 1);

        public Task<bool> RebuildCaseAsync(
            Guid caseId,
            string? slackChannel,
            string? slackTimestamp,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<(Guid CaseId, bool IsDuplicate)> AcceptOriginEventAsync(
            AcceptCaseOriginEvent originEvent,
            Recipe recipe,
            CaseOriginEventReceipt receipt,
            CancellationToken cancellationToken) => Task.FromResult((Guid.Empty, false));

        public Task SetSlackTimestampAsync(
            Guid caseId,
            string timestamp,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken) =>
            Task.FromResult(0);
    }

    private sealed class RecordingUpdates(ICollection<string>? events = null) : ICaseUpdatePublisher
    {
        public List<PublishedCaseFile> PublishedCaseFiles { get; } = [];

        public Task PublishStatusAsync(
            Guid caseId,
            int version,
            string status,
            CancellationToken cancellationToken)
        {
            events?.Add($"publish:{status}:{version}");
            return Task.CompletedTask;
        }

        public Task PublishCaseFileAsync(
            Guid caseId,
            int version,
            string status,
            IReadOnlyList<string> changedSections,
            CancellationToken cancellationToken)
        {
            PublishedCaseFiles.Add(new PublishedCaseFile(version, status, changedSections.ToArray()));
            return Task.CompletedTask;
        }
    }

    private sealed record PublishedCaseFile(
        int Version,
        string Status,
        IReadOnlyList<string> ChangedSections);
}
