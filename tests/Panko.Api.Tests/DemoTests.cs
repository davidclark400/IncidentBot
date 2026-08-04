using Panko.Api.Demo;
using Panko.Api.Domain;
using Panko.Api.Cases;

namespace Panko.Api.Tests;

public sealed class DemoTests
{
    [Fact]
    public void RecentCasesReturnsTheCanonicalDemoCase()
    {
        var store = new DemoCaseStore(TimeProvider.System);

        var result = DemoCaseApi.ListCases(store);
        var response = Assert.IsType<Panko.Contracts.RecentCases>(result.Value);

        Assert.Equal(1, response.Total);
        var recentCase = Assert.Single(response.Cases);
        Assert.Equal(DemoCaseStore.CaseId, recentCase.CaseId);
        Assert.Equal(Panko.Contracts.CaseOriginKind.PagerDuty, recentCase.Origin);
        Assert.Equal($"/cases/{DemoCaseStore.CaseId}", recentCase.CaseUrl);
    }

    [Fact]
    public void InputAuditReturnsAnEmptyCanonicalPageForTheDemoCase()
    {
        var result = DemoCaseApi.ListInputs(DemoCaseStore.CaseId);

        var ok = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok<
            Panko.Contracts.Page<Panko.Contracts.CaseInput>>>(result);
        var page = Assert.IsType<Panko.Contracts.Page<Panko.Contracts.CaseInput>>(ok.Value);
        Assert.Equal(0, page.Total);
        Assert.Empty(page.Items);
        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.NotFound>(
            DemoCaseApi.ListInputs(Guid.NewGuid()));
    }

    [Fact]
    public async Task ReplayKeepsCanonicalCrumbsStableUntilTheFinalTransition()
    {
        var store = new DemoCaseStore(TimeProvider.System);
        var reset = store.Reset();

        Assert.Equal(1, reset.CaseFile.CaseFileVersion);
        Assert.Equal("collecting", reset.CaseFile.Status);
        Assert.Empty(reset.CaseFile.CausalMarkers!);
        Assert.Empty(reset.CaseFile.Crumbs);
        Assert.Equal(1, reset.Progress.Revision);
        Assert.Equal(reset.CaseFile.CaseFileVersion, reset.Progress.BaseCaseFileVersion);

        CaseProgress? synthesizing = null;
        for (var phase = 1; phase <= 5; phase++)
        {
            var transition = store.Advance(reset.Generation, phase);
            Assert.NotNull(transition);
            Assert.Null(transition!.CaseFile);
            var progress = Assert.IsType<CaseProgress>(transition.Progress);
            Assert.Equal(phase + 1, progress.Revision);
            Assert.Equal(reset.CaseFile.CaseFileVersion, progress.BaseCaseFileVersion);
            Assert.Equal(reset.Progress.AttemptId, progress.AttemptId);

            var canonical = store.Get();
            Assert.Same(reset.CaseFile, canonical);
            Assert.Equal(1, canonical.CaseFileVersion);
            Assert.Empty(canonical.Crumbs);
            Assert.Equal(progress, await store.GetProgressAsync(
                DemoCaseStore.CaseId,
                CancellationToken.None));
            synthesizing = progress;
        }

        var finalProgress = Assert.IsType<CaseProgress>(synthesizing);
        Assert.True(finalProgress.DeterministicCaseFileUsable);
        Assert.True(finalProgress.OnlyAiSynthesisRemaining);
        Assert.Equal(CaseProgressPhase.Synthesizing, finalProgress.Phase);
        Assert.Equal(AiSynthesisProgressState.Running, finalProgress.AiSynthesisState);
        Assert.Equal(2, finalProgress.CurrentPass);
        Assert.Equal(120, finalProgress.CurrentLookbackMinutes);
        Assert.InRange(finalProgress.EarlyCrumbs.Count, 1, 5);

        var completed = store.Advance(reset.Generation, 6);
        Assert.NotNull(completed);
        Assert.Null(completed!.Progress);
        var final = Assert.IsType<CaseFile>(completed.CaseFile);
        Assert.Equal(2, final.CaseFileVersion);
        Assert.Equal("ready", final.Status);
        Assert.Equal(new[]
        {
            "merge-request-created", "merge-request-merged", "deployment", "workload-failure", "first-error", "pipeline"
        }, final.CausalMarkers!.Select(item => item.Category));
        var diagnosis = Assert.Single(final.Ai.Diagnoses!);
        var reference = Assert.Single(diagnosis.CodeReferences);
        Assert.Equal(43, reference.StartLine);
        Assert.Equal(44, reference.EndLine);
        Assert.Null(await store.GetProgressAsync(DemoCaseStore.CaseId, CancellationToken.None));
    }

    [Fact]
    public void StaleReplayCannotOverwriteANewerReset()
    {
        var store = new DemoCaseStore(TimeProvider.System);
        var oldReplay = store.Reset();
        var newReplay = store.Reset();

        Assert.Null(store.Advance(oldReplay.Generation, 4));
        var transition = store.Advance(newReplay.Generation, 1);
        Assert.NotNull(transition);
        Assert.NotNull(transition!.Progress);
        Assert.Null(transition.CaseFile);
    }

    [Fact]
    public async Task BurstResetsCoalesceToTheNewestQueuedGeneration()
    {
        var store = new DemoCaseStore(TimeProvider.System);
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
    public async Task ReplayPublishesInitialAndFinalCaseFilesWithProgressOnlyBetweenThem()
    {
        var store = new DemoCaseStore(TimeProvider.System);
        var updates = new RecordingUpdates();
        var replay = new DemoReplay(
            store,
            updates,
            Microsoft.Extensions.Options.Options.Create(new Panko.Api.Options.DemoOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DemoReplay>.Instance);

        var start = await replay.ResetAsync(CancellationToken.None);
        for (var phase = 1; phase <= 6; phase++)
        {
            Assert.True(await replay.AdvanceAsync(start.Generation, phase, CancellationToken.None));
        }

        var initialSections = new[]
        {
            "status", "summary", "trail", "crumbs", "crumbSources", "causalMarkers", "ai", "pattern"
        };
        var completedSections = new[]
        {
            "status", "summary", "ai", "trail", "crumbs", "crumbSources", "links", "causalMarkers", "pattern"
        };
        var expected = new (int CaseFileVersion, string Status, string[] ChangedSections)[]
        {
            (1, "collecting", initialSections),
            (2, "ready", completedSections)
        };
        Assert.Equal(expected.Length, updates.PublishedCaseFiles.Count);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index].CaseFileVersion, updates.PublishedCaseFiles[index].CaseFileVersion);
            Assert.Equal(expected[index].Status, updates.PublishedCaseFiles[index].Status);
            Assert.Equal(expected[index].ChangedSections, updates.PublishedCaseFiles[index].ChangedSections);
        }
        Assert.Equal(6, updates.Progress.Count);
        Assert.Equal(Enumerable.Range(1, 6).Select(value => (long)value),
            updates.Progress.Select(item => item.Revision));
        Assert.All(updates.Progress, progress =>
        {
            Assert.Equal(1, progress.BaseCaseFileVersion);
            Assert.Equal(updates.Progress[0].AttemptId, progress.AttemptId);
        });
        Assert.All(updates.Progress.Take(5), progress =>
            Assert.Equal(CaseProgressPhase.Collecting, progress.Phase));
        Assert.Equal(CaseProgressPhase.Synthesizing, updates.Progress[^1].Phase);
        Assert.Equal(new[]
        {
            "case-file:1", "progress:1", "progress:2", "progress:3",
            "progress:4", "progress:5", "progress:6", "case-file:2"
        }, updates.Events);
        Assert.Null(await store.GetProgressAsync(DemoCaseStore.CaseId, CancellationToken.None));

        var newer = await replay.ResetAsync(CancellationToken.None);
        var publicationCount = updates.PublishedCaseFiles.Count;
        var progressPublicationCount = updates.Progress.Count;
        Assert.False(await replay.AdvanceAsync(start.Generation, 2, CancellationToken.None));
        Assert.Equal(publicationCount, updates.PublishedCaseFiles.Count);
        Assert.Equal(progressPublicationCount, updates.Progress.Count);
        Assert.True(newer.Generation > start.Generation);
        Assert.Equal([1, 2, 3], updates.PublishedCaseFiles.Select(item => item.CaseFileVersion));
        Assert.Equal(1, updates.Progress[^1].Revision);
        Assert.Equal(3, updates.Progress[^1].BaseCaseFileVersion);
        Assert.NotEqual(updates.Progress[0].AttemptId, updates.Progress[^1].AttemptId);
    }

    [Fact]
    public async Task ResetWaitsForAnInFlightProgressPublicationBeforeStartingANewAttempt()
    {
        var store = new DemoCaseStore(TimeProvider.System);
        var updates = new BlockingUpdates(blockedProgressRevision: 2);
        var replay = new DemoReplay(
            store,
            updates,
            Microsoft.Extensions.Options.Options.Create(new Panko.Api.Options.DemoOptions()),
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
        Assert.Equal([1, 2], updates.CaseFileVersions);
        Assert.Equal([1L, 2L, 1L], updates.ProgressRevisions);
    }

    private sealed class RecordingUpdates : ICaseUpdatePublisher
    {
        public List<PublishedCaseFile> PublishedCaseFiles { get; } = [];
        public List<CaseProgress> Progress { get; } = [];
        public List<string> Events { get; } = [];

        public Task PublishStatusAsync(
            Guid caseId,
            int version,
            string status,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task PublishCaseFileAsync(
            Guid caseId,
            int version,
            string status,
            IReadOnlyList<string> changedSections,
            CancellationToken cancellationToken)
        {
            Assert.Equal(DemoCaseStore.CaseId, caseId);
            PublishedCaseFiles.Add(new PublishedCaseFile(version, status, changedSections.ToArray()));
            Events.Add($"case-file:{version}");
            return Task.CompletedTask;
        }

        public Task PublishProgressAsync(
            CaseProgress progress,
            CancellationToken cancellationToken)
        {
            Assert.Equal(DemoCaseStore.CaseId, progress.CaseId);
            Progress.Add(progress);
            Events.Add($"progress:{progress.Revision}");
            return Task.CompletedTask;
        }
    }

    private sealed record PublishedCaseFile(
        int CaseFileVersion,
        string Status,
        IReadOnlyList<string> ChangedSections);

    private sealed class BlockingUpdates(long blockedProgressRevision) : ICaseUpdatePublisher
    {
        private readonly TaskCompletionSource blocked =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<int> CaseFileVersions { get; } = [];
        public List<long> ProgressRevisions { get; } = [];

        public Task WaitUntilBlockedAsync() =>
            blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public void Release() => release.TrySetResult();

        public Task PublishStatusAsync(
            Guid caseId,
            int version,
            string status,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task PublishCaseFileAsync(
            Guid caseId,
            int version,
            string status,
            IReadOnlyList<string> changedSections,
            CancellationToken cancellationToken)
        {
            CaseFileVersions.Add(version);
            await Task.CompletedTask;
        }

        public async Task PublishProgressAsync(
            CaseProgress progress,
            CancellationToken cancellationToken)
        {
            ProgressRevisions.Add(progress.Revision);
            if (progress.Revision != blockedProgressRevision) return;

            blocked.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        }
    }
}
