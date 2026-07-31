using IncidentBot.Api.Demo;
using IncidentBot.Api.Incidents;

namespace IncidentBot.Api.Tests;

public sealed class DemoTests
{
    [Fact]
    public void ReplayBuildsProductionShapedVersionsAndCitedDiagnosis()
    {
        var store = new DemoIncidentStore(TimeProvider.System);
        var reset = store.Reset();

        Assert.Equal(1, reset.Report.Version);
        Assert.Equal("collecting", reset.Report.Status);
        Assert.Empty(reset.Report.CausalEvents!);

        var final = reset.Report;
        for (var phase = 1; phase <= 6; phase++)
        {
            final = store.Advance(reset.Generation, phase)!;
        }

        Assert.Equal(7, final.Version);
        Assert.Equal("ready", final.Status);
        Assert.Equal(new[]
        {
            "merge-request-created", "merge-request-merged", "deployment", "workload-failure", "first-error", "pipeline"
        }, final.CausalEvents!.Select(item => item.Category));
        var diagnosis = Assert.Single(final.Ai.Diagnoses!);
        var reference = Assert.Single(diagnosis.CodeReferences);
        Assert.Equal(43, reference.StartLine);
        Assert.Equal(44, reference.EndLine);
    }

    [Fact]
    public void StaleReplayCannotOverwriteANewerReset()
    {
        var store = new DemoIncidentStore(TimeProvider.System);
        var oldReplay = store.Reset();
        var newReplay = store.Reset();

        Assert.Null(store.Advance(oldReplay.Generation, 4));
        Assert.NotNull(store.Advance(newReplay.Generation, 1));
    }

    [Fact]
    public async Task BurstResetsCoalesceToTheNewestQueuedGeneration()
    {
        var store = new DemoIncidentStore(TimeProvider.System);
        var oldest = store.Reset();
        _ = store.Reset();
        var newest = store.Reset();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await using var starts = store.ReadStartsAsync(timeout.Token).GetAsyncEnumerator();

        Assert.True(await starts.MoveNextAsync());
        Assert.Equal(newest.Generation, starts.Current);
        Assert.False(store.IsCurrentGeneration(oldest.Generation));
        Assert.True(store.IsCurrentGeneration(newest.Generation));
    }

    [Fact]
    public async Task ReplayTransitionsPublishResetAndPhaseMetadataThroughOneInterface()
    {
        var store = new DemoIncidentStore(TimeProvider.System);
        var updates = new RecordingUpdates();
        var replay = new DemoReplay(
            store,
            updates,
            Microsoft.Extensions.Options.Options.Create(new IncidentBot.Api.Options.DemoOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DemoReplay>.Instance);

        var start = await replay.ResetAsync(CancellationToken.None);
        for (var phase = 1; phase <= 6; phase++)
        {
            Assert.True(await replay.AdvanceAsync(start.Generation, phase, CancellationToken.None));
        }

        var resetSections = new[]
        {
            "status", "summary", "timeline", "evidence", "sources", "causalEvents", "ai", "problem"
        };
        var evidencePhaseSections = new[]
        {
            "summary", "timeline", "evidence", "causalEvents", "sources", "problem"
        };
        var expected = new (int Version, string Status, string[] ChangedSections)[]
        {
            (1, "collecting", resetSections),
            (2, "collecting", evidencePhaseSections),
            (3, "collecting", evidencePhaseSections),
            (4, "collecting", evidencePhaseSections),
            (5, "collecting", evidencePhaseSections),
            (6, "collecting", evidencePhaseSections),
            (7, "ready", ["summary", "ai", "status", "problem"])
        };
        Assert.Equal(expected.Length, updates.Reports.Count);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index].Version, updates.Reports[index].Version);
            Assert.Equal(expected[index].Status, updates.Reports[index].Status);
            Assert.Equal(expected[index].ChangedSections, updates.Reports[index].ChangedSections);
        }

        var newer = await replay.ResetAsync(CancellationToken.None);
        var publicationCount = updates.Reports.Count;
        Assert.False(await replay.AdvanceAsync(start.Generation, 2, CancellationToken.None));
        Assert.Equal(publicationCount, updates.Reports.Count);
        Assert.True(newer.Generation > start.Generation);
        Assert.Equal(Enumerable.Range(1, 8), updates.Reports.Select(item => item.Version));
    }

    [Fact]
    public async Task ResetWaitsForAnInFlightPhasePublicationAndPublishesInVersionOrder()
    {
        var store = new DemoIncidentStore(TimeProvider.System);
        var updates = new BlockingUpdates(blockedVersion: 2);
        var replay = new DemoReplay(
            store,
            updates,
            Microsoft.Extensions.Options.Options.Create(new IncidentBot.Api.Options.DemoOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DemoReplay>.Instance);

        var start = await replay.ResetAsync(CancellationToken.None);
        var advance = replay.AdvanceAsync(start.Generation, 1, CancellationToken.None);
        await updates.WaitUntilBlockedAsync();

        var reset = replay.ResetAsync(CancellationToken.None);
        Assert.False(reset.IsCompleted);

        updates.Release();
        Assert.True(await advance);
        var newer = await reset;

        Assert.True(newer.Generation > start.Generation);
        Assert.Equal([1, 2, 3], updates.Versions);
    }

    private sealed class RecordingUpdates : IIncidentUpdatePublisher
    {
        public List<PublishedReport> Reports { get; } = [];

        public Task PublishStatusAsync(
            Guid incidentId,
            int version,
            string status,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task PublishReportAsync(
            Guid incidentId,
            int version,
            string status,
            IReadOnlyList<string> changedSections,
            CancellationToken cancellationToken)
        {
            Assert.Equal(DemoIncidentStore.IncidentId, incidentId);
            Reports.Add(new PublishedReport(version, status, changedSections.ToArray()));
            return Task.CompletedTask;
        }
    }

    private sealed record PublishedReport(
        int Version,
        string Status,
        IReadOnlyList<string> ChangedSections);

    private sealed class BlockingUpdates(int blockedVersion) : IIncidentUpdatePublisher
    {
        private readonly TaskCompletionSource blocked =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<int> Versions { get; } = [];

        public Task WaitUntilBlockedAsync() =>
            blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public void Release() => release.TrySetResult();

        public Task PublishStatusAsync(
            Guid incidentId,
            int version,
            string status,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task PublishReportAsync(
            Guid incidentId,
            int version,
            string status,
            IReadOnlyList<string> changedSections,
            CancellationToken cancellationToken)
        {
            Versions.Add(version);
            if (version != blockedVersion) return;

            blocked.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        }
    }
}
