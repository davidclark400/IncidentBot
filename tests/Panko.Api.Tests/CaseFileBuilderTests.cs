using System.Text.Json.Nodes;
using Panko.Api.Crumbs;
using Panko.Api.Domain;
using Panko.Api.Cases;
using Panko.Api.Patterns;
using Panko.Api.Signatures;
using Panko.Api.Options;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;

namespace Panko.Api.Tests;

public sealed class CaseFileBuilderTests
{
    [Fact]
    public async Task RunOwnsTheCompleteCaseFileBuildAttempt()
    {
        var caseRecord = new CaseRecord(
            Guid.NewGuid(), "PD-1", "payments", "recipe", "Payments failing", "high",
            PagerDutyIncidentState.Triggered, DateTimeOffset.Parse("2026-07-11T10:00:00Z"),
            DateTimeOffset.Parse("2026-07-11T10:00:01Z"), 1, "queued", false, null,
            "#cases", null, new Dictionary<string, string>())
        {
            Team = "payments"
        };
        var repository = new RecordingRepository(caseRecord);
        var updates = new RecordingUpdates();
        var connector = new RecordingConnector();
        var crumbSources = new CrumbSourceRegistry([connector], TestConfiguration.CrumbSources());
        var pankoOptions = CreatePankoOptions();
        var runner = new CaseFileBuilder(
            repository,
            new RecipeProvider([connector.Source]),
            crumbSources,
            Collector(pankoOptions),
            new CaseFileProjectionBuilder(TimeProvider.System),
            new Synthesizer(),
            new CaseFileTransitions(repository, updates),
            pankoOptions,
            TimeProvider.System,
            new Recurrence(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CaseFileBuilder>.Instance);

        await runner.RunAsync(caseRecord.Id, CancellationToken.None);

        Assert.Equal("collecting", Assert.Single(repository.Statuses));
        Assert.True(connector.WasCalled);
        Assert.NotNull(repository.SavedCaseFile);
        Assert.Equal("ready", repository.SavedCaseFile.Status);
        Assert.Single(repository.SavedCaseFiles);
        Assert.Equal(CrumbSourceRequestState.Received, Assert.Single(repository.SavedCaseFile.CrumbSources).RequestState);
        Assert.Equal(new[] { "status:collecting", "caseFile:ready" }, updates.Events);
    }

    [Fact]
    public async Task UnexpectedCrumbSourceFailureDoesNotDiscardOtherCrumbs()
    {
        var caseRecord = BuildCase();
        var repository = new RecordingRepository(caseRecord);
        var synthesizer = new RecordingSynthesizer();
        var runner = Runner(repository, [new ThrowingConnector(), new RecordingConnector("nomad")], synthesizer);

        await runner.RunAsync(caseRecord.Id, CancellationToken.None);

        Assert.Equal(2, synthesizer.Results.Count);
        var failed = Assert.Single(synthesizer.Results, result => result.Source == "pagerduty");
        Assert.Equal(CrumbSourceHealth.Unavailable, failed.Health);
        Assert.Contains("unexpected collector failure", failed.Diagnostic);
        Assert.Contains(synthesizer.Results, result => result.Source == "nomad" && result.Crumbs.Count == 1);
        Assert.Equal("ready", repository.SavedCaseFile?.Status);
        Assert.Equal(
            CrumbSourceRequestState.Errored,
            repository.SavedCaseFile?.CrumbSources.Single(source => source.Source == "pagerduty").RequestState);
        Assert.Equal(
            CrumbSourceRequestState.Received,
            repository.SavedCaseFile?.CrumbSources.Single(source => source.Source == "nomad").RequestState);
    }

    [Fact]
    public async Task SignatureFailureDoesNotPreventTheCaseFile()
    {
        var caseRecord = BuildCase();
        var repository = new RecordingRepository(caseRecord);
        var runner = Runner(repository, [new RecordingConnector()], new Synthesizer(), new Recurrence(fail: true));

        await runner.RunAsync(caseRecord.Id, CancellationToken.None);

        Assert.Equal("ready", repository.SavedCaseFile?.Status);
        Assert.Equal("unavailable", repository.SavedCaseFile?.Pattern?.Availability);
        Assert.Equal("InvalidOperationException", repository.SavedCaseFile?.Pattern?.Diagnostic);
    }

    [Fact]
    public async Task ExposesDeterministicProgressWhileOnlySynthesisRemainsThenClearsItOnFinalCommit()
    {
        var caseRecord = BuildCase();
        var repository = new RecordingRepository(caseRecord);
        var synthesizer = new BlockingSynthesizer();
        var runner = Runner(repository, [new RecordingConnector()], synthesizer);

        var run = runner.RunAsync(caseRecord.Id, CancellationToken.None);
        await synthesizer.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Empty(repository.SavedCaseFiles);
        Assert.NotNull(repository.Progress);
        Assert.Equal(CaseProgressPhase.Synthesizing, repository.Progress.Phase);
        Assert.True(repository.Progress.DeterministicCaseFileUsable);
        Assert.True(repository.Progress.OnlyAiSynthesisRemaining);
        Assert.Equal(AiSynthesisProgressState.Running, repository.Progress.AiSynthesisState);

        synthesizer.Release();
        await run;

        Assert.Single(repository.SavedCaseFiles);
        Assert.Null(repository.Progress);
        Assert.Equal("complete", repository.SavedCaseFile?.Ai.Status);
    }

    [Fact]
    public async Task CollectionDisabledStillPublishesResolutionAndPatternLifecycleUpdates()
    {
        var caseRecord = BuildCase() with { PagerDutyState = PagerDutyIncidentState.Resolved, IsFrozen = true, Version = 1 };
        var recipeProvider = new RecipeProvider([]);
        var recipe = recipeProvider.Resolve(caseRecord.ServiceId, caseRecord.Labels);
        var crumbSources = new CrumbSourceRegistry([], TestConfiguration.CrumbSources());
        var previous = new CaseFileComposer(TimeProvider.System, crumbSources).ComposeInitial(caseRecord, recipe, recipeProvider.Revision);
        var repository = new RecordingRepository(caseRecord, previous);
        var updates = new RecordingUpdates();
        var pankoOptions = CreatePankoOptions(collectionEnabled: false);
        var runner = new CaseFileBuilder(
            repository, recipeProvider, crumbSources, Collector(pankoOptions),
            new CaseFileProjectionBuilder(TimeProvider.System), new Synthesizer(),
            new CaseFileTransitions(repository, updates), pankoOptions,
            TimeProvider.System, new Recurrence(), NullLogger<CaseFileBuilder>.Instance);

        await runner.RunAsync(caseRecord.Id, CancellationToken.None);

        Assert.Equal(PagerDutyIncidentState.Resolved, repository.SavedCaseFile?.PagerDutyState);
        Assert.Equal("resolved", repository.SavedCaseFile?.Status);
        Assert.Equal("available", repository.SavedCaseFile?.Pattern?.Availability);
        Assert.Equal(new[] { "caseFile:resolved" }, updates.Events);
    }

    [Fact]
    public async Task ResolvedPagerDutyIncidentStillCollectsAndKeepsItsLifecycleTrail()
    {
        var caseRecord = BuildCase() with
        {
            PagerDutyState = PagerDutyIncidentState.Resolved,
            IsFrozen = true,
            Version = 0,
            InputVersion = 1,
            OpenedAt = DateTimeOffset.Parse("2026-07-11T10:00:00Z"),
            UpdatedAt = DateTimeOffset.Parse("2026-07-11T10:20:00Z")
        };
        var repository = new RecordingRepository(caseRecord)
        {
            ProjectionInputs = new CaseProjectionInputs(
                [
                    LifecycleInput(caseRecord, 0, "pagerduty-incident-triggered", "PagerDuty incident triggered", caseRecord.OpenedAt),
                    LifecycleInput(caseRecord, 1, "pagerduty-incident-resolved", "PagerDuty incident resolved", caseRecord.UpdatedAt)
                ],
                [])
        };
        var connector = new RecordingConnector();
        var runner = Runner(repository, [connector], new Synthesizer());

        await runner.RunAsync(caseRecord.Id, CancellationToken.None);

        Assert.True(connector.WasCalled);
        Assert.Equal(
            caseRecord.OpenedAt - TimeSpan.FromMinutes(30),
            connector.LastScope?.Start);
        Assert.Equal(CaseProgression.Resolved, repository.SavedCaseFile?.Status);
        Assert.Contains(repository.SavedCaseFile!.Trail, item =>
            item.Kind == "pagerduty-incident-triggered" && item.OccurredAt == caseRecord.OpenedAt);
        Assert.Contains(repository.SavedCaseFile.Trail, item =>
            item.Kind == "pagerduty-incident-resolved"
            && item.Summary == "PagerDuty incident resolved"
            && item.OccurredAt == caseRecord.UpdatedAt);
    }

    [Fact]
    public async Task ProjectsCanonicalPagerDutyLifecycleAtTheExactCapturedInputVersion()
    {
        var caseRecord = BuildCase() with
        {
            Version = 1,
            InputVersion = 2,
            PagerDutyState = PagerDutyIncidentState.Resolved,
            IsFrozen = true,
            UpdatedAt = DateTimeOffset.Parse("2026-07-11T10:20:00Z")
        };
        var repository = new RecordingRepository(caseRecord)
        {
            ProjectionInputs = new CaseProjectionInputs(
                [
                    LifecycleInput(caseRecord, 0, "pagerduty-incident-triggered", "PagerDuty incident triggered", caseRecord.OpenedAt),
                    LifecycleInput(caseRecord, 1, "pagerduty-incident-acknowledged", "PagerDuty incident acknowledged", caseRecord.OpenedAt.AddMinutes(5)),
                    LifecycleInput(caseRecord, 2, "pagerduty-incident-resolved", "PagerDuty incident resolved", caseRecord.UpdatedAt)
                ],
                [])
        };

        await Runner(repository, [new RecordingConnector()], new Synthesizer())
            .RunAsync(caseRecord.Id, CancellationToken.None);

        var caseFile = Assert.Single(repository.SavedCaseFiles);
        Assert.Equal(2, caseFile.InputVersion);
        Assert.Equal(2, caseFile.ProjectedInputVersion);
        Assert.Single(caseFile.Trail, item => item.Kind == "pagerduty-incident-triggered");
        Assert.Contains(caseFile.Trail, item => item.Kind == "pagerduty-incident-acknowledged");
        Assert.Contains(caseFile.Trail, item => item.Kind == "pagerduty-incident-resolved");
        Assert.DoesNotContain(caseFile.Crumbs, item => item.Category.StartsWith("pagerduty-incident-", StringComparison.Ordinal));
        Assert.NotNull(repository.SavedConnectorSnapshot);
        Assert.Single(repository.SavedConnectorSnapshot!);
    }

    [Fact]
    public async Task RunFailsClosedWhenThePersistedTeamNoLongerOwnsTheRecipe()
    {
        var caseRecord = BuildCase();
        var repository = new RecordingRepository(caseRecord);
        var connector = new RecordingConnector();
        var crumbSources = new CrumbSourceRegistry([connector], TestConfiguration.CrumbSources());
        var pankoOptions = CreatePankoOptions();
        var runner = new CaseFileBuilder(
            repository,
            new RecipeProvider([connector.Source], team: "search"),
            crumbSources,
            Collector(pankoOptions),
            new CaseFileProjectionBuilder(TimeProvider.System),
            new Synthesizer(),
            new CaseFileTransitions(repository, new RecordingUpdates()),
            pankoOptions,
            TimeProvider.System,
            new Recurrence(),
            NullLogger<CaseFileBuilder>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync(caseRecord.Id, CancellationToken.None));

        Assert.False(connector.WasCalled);
        Assert.Empty(repository.SavedCaseFiles);
    }

    private static CaseRecord BuildCase() => new CaseRecord(
        Guid.NewGuid(), "PD-1", "payments", "recipe", "Payments failing", "high",
        PagerDutyIncidentState.Triggered, DateTimeOffset.Parse("2026-07-11T10:00:00Z"),
        DateTimeOffset.Parse("2026-07-11T10:00:01Z"), 1, "queued", false, null,
        "#cases", null, new Dictionary<string, string>())
    {
        Team = "payments"
    };

    private static CaseInput LifecycleInput(
        CaseRecord caseRecord,
        long sequence,
        string category,
        string summary,
        DateTimeOffset occurredAt) => new(
        Guid.NewGuid(),
        caseRecord.Id,
        sequence,
        sequence,
        PagerDutyCaseAdapter.ProducerPrincipal,
        $"crumb-{sequence}",
        Panko.Contracts.SubmittedCrumbKind.Event,
        occurredAt,
        occurredAt,
        category,
        category == "pagerduty-incident-triggered" ? "critical" : "info",
        summary,
        null,
        "pagerduty",
        caseRecord.PagerDutyIncidentId,
        null,
        null,
        "pagerduty-incident",
        caseRecord.PagerDutyIncidentId,
        new JsonObject(),
        "collected",
        $"hash-{sequence}",
        null,
        null,
        null);

    private static CaseFileBuilder Runner(
        RecordingRepository repository,
        IEnumerable<ICrumbSourceAdapter> connectors,
        ICaseFileSynthesizer synthesizer,
        IPatternCoordinator? recurrence = null)
    {
        var connectorList = connectors.ToList();
        var crumbSources = new CrumbSourceRegistry(connectorList, TestConfiguration.CrumbSources());
        var pankoOptions = CreatePankoOptions();
        return new CaseFileBuilder(
            repository, new RecipeProvider(connectorList.Select(item => item.Source)), crumbSources,
            Collector(pankoOptions), new CaseFileProjectionBuilder(TimeProvider.System),
            synthesizer, new CaseFileTransitions(repository, new RecordingUpdates()), pankoOptions,
            TimeProvider.System, recurrence ?? new Recurrence(), NullLogger<CaseFileBuilder>.Instance);
    }

    private static IOptions<PankoOptions> CreatePankoOptions(bool collectionEnabled = true) =>
        Microsoft.Extensions.Options.Options.Create(new PankoOptions
        {
            CrumbCollectionEnabled = collectionEnabled,
            CrumbWindowMinutes = 30,
            CrumbMaximumWindowMinutes = 30
        });

    private static AdaptiveCrumbCollector Collector(IOptions<PankoOptions> options) => new(
        options,
        TimeProvider.System,
        NullLogger<AdaptiveCrumbCollector>.Instance);

    private sealed class RecordingRepository(CaseRecord caseRecord, CaseFile? previousCaseFile = null) : ICaseStore
    {
        private CaseRecord currentCase = caseRecord;

        public List<string> Statuses { get; } = [];
        public List<CaseFile> SavedCaseFiles { get; } = [];
        public CaseFile? SavedCaseFile => SavedCaseFiles.LastOrDefault();
        public CaseProgress? Progress { get; private set; }
        public CaseProjectionInputs ProjectionInputs { get; set; } = new([], []);
        public IReadOnlyList<CrumbSourceResult>? SavedConnectorSnapshot { get; private set; }

        public Task<CaseRecord?> GetCaseAsync(Guid caseId, CancellationToken cancellationToken) =>
            Task.FromResult<CaseRecord?>(currentCase);

        public Task<CaseFile?> GetCaseFileAsync(Guid caseId, CancellationToken cancellationToken) =>
            Task.FromResult(SavedCaseFile ?? previousCaseFile);

        public Task<CaseProjectionInputs> GetProjectionInputsAsync(
            Guid caseId,
            long targetInputVersion,
            CancellationToken cancellationToken) => Task.FromResult(ProjectionInputs);

        public Task<CaseProgress?> GetProgressAsync(
            Guid caseId,
            CancellationToken cancellationToken) => Task.FromResult(Progress);

        public Task<long?> BeginProgressAsync(
            CaseProgress progress,
            CancellationToken cancellationToken)
        {
            Progress = progress with { Revision = 1 };
            return Task.FromResult<long?>(1);
        }

        public Task<long?> UpdateProgressAsync(
            CaseProgress progress,
            CancellationToken cancellationToken)
        {
            if (Progress is null
                || Progress.AttemptId != progress.AttemptId
                || Progress.Revision != progress.Revision)
            {
                return Task.FromResult<long?>(null);
            }
            var revision = progress.Revision + 1;
            Progress = progress with { Revision = revision };
            return Task.FromResult<long?>(revision);
        }

        public Task<int> SaveCaseFileAsync(
            CaseRecord current,
            CaseFile caseFile,
            CancellationToken cancellationToken) =>
            SaveCaseFileAsync(current, caseFile, null, null, cancellationToken);

        public Task<int> SaveCaseFileAsync(
            CaseRecord current,
            CaseFile caseFile,
            Guid? progressAttemptId,
            CancellationToken cancellationToken) =>
            SaveCaseFileAsync(current, caseFile, progressAttemptId, null, cancellationToken);

        public Task<int> SaveCaseFileAsync(
            CaseRecord current,
            CaseFile caseFile,
            Guid? progressAttemptId,
            IReadOnlyList<CrumbSourceResult>? connectorSnapshot,
            CancellationToken cancellationToken)
        {
            Assert.Equal(currentCase.Version, current.Version);
            Assert.Equal(currentCase.InputVersion, current.InputVersion);
            Assert.Equal(current.InputVersion, caseFile.InputVersion);
            Assert.Equal(current.InputVersion, caseFile.ProjectedInputVersion);
            if (progressAttemptId is not null)
            {
                Assert.NotNull(Progress);
                Assert.Equal(Progress.AttemptId, progressAttemptId);
            }
            SavedConnectorSnapshot = connectorSnapshot;
            SavedCaseFiles.Add(caseFile);
            var version = current.Version + 1;
            currentCase = currentCase with
            {
                Version = version,
                Status = caseFile.Status,
                UpdatedAt = caseFile.UpdatedAt,
                ProjectedInputVersion = caseFile.ProjectedInputVersion
            };
            Progress = null;
            return Task.FromResult(version);
        }

        public Task SetStatusAsync(Guid caseId, string status, CancellationToken cancellationToken)
        {
            Statuses.Add(status);
            currentCase = currentCase with { Status = status };
            return Task.CompletedTask;
        }

        public Task<bool> RebuildCaseAsync(
            Guid caseId,
            string? slackChannel,
            string? slackTimestamp,
            CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<(Guid CaseId, bool IsDuplicate)> AcceptOriginEventAsync(
            AcceptCaseOriginEvent originEvent,
            Recipe recipe,
            CaseOriginEventReceipt receipt,
            CancellationToken cancellationToken) => Task.FromResult((currentCase.Id, false));

        public Task SetSlackTimestampAsync(Guid caseId, string timestamp, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken) =>
            Task.FromResult(0);
    }

    private sealed class RecipeProvider(
        IEnumerable<string> enabledSources,
        string team = "payments") : IRecipeProvider
    {
        private readonly HashSet<string> sources = enabledSources.ToHashSet(StringComparer.Ordinal);
        public string Revision => "test-v1";

        public Recipe Resolve(string serviceId, IReadOnlyDictionary<string, string> labels) =>
            new()
            {
                Id = "recipe",
                PagerDutyServiceId = serviceId,
                Team = team,
                SlackChannel = "#cases",
                PagerDuty = sources.Contains(CrumbSourceRegistry.PagerDuty) ? new PagerDutyScope() : null,
                Nomad = sources.Contains(CrumbSourceRegistry.Nomad) ? new NomadScope() : null
            };
    }

    private sealed class RecordingConnector(string source = "pagerduty") : ICrumbSourceAdapter
    {
        public string Source => source;
        public bool WasCalled { get; private set; }
        public CrumbScope? LastScope { get; private set; }

        public Task<CrumbSourceResult> CollectAsync(
            CaseContext context,
            CrumbScope scope,
            CancellationToken cancellationToken)
        {
            WasCalled = true;
            LastScope = scope;
            var crumb = new Crumb(
                "crumb-1", Source, scope.End, null, "pagerduty-incident", "warning", "Signal found",
                null, null, 0.9, new JsonObject());
            return Task.FromResult(new CrumbSourceResult(
                Source, CrumbSourceHealth.Complete, [crumb], [], [], 10, null));
        }
    }

    private sealed class ThrowingConnector : ICrumbSourceAdapter
    {
        public string Source => "pagerduty";

        public Task<CrumbSourceResult> CollectAsync(
            CaseContext context,
            CrumbScope scope,
            CancellationToken cancellationToken) =>
            throw new NullReferenceException("unexpected collector failure");
    }

    private sealed class Synthesizer : ICaseFileSynthesizer
    {
        public Task<AiSynthesis> SynthesizeAsync(
            CaseSubject subject,
            IReadOnlyList<CrumbSourceResult> results,
            AiSynthesis? previous,
            CancellationToken cancellationToken) =>
            Task.FromResult(new AiSynthesis("complete", "Signal found", [], [], [], "hash"));
    }

    private sealed class BlockingSynthesizer : ICaseFileSynthesizer
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<AiSynthesis> SynthesizeAsync(
            CaseSubject subject,
            IReadOnlyList<CrumbSourceResult> results,
            AiSynthesis? previous,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return new AiSynthesis("complete", "done", [], [], [], "hash");
        }

        public void Release() => release.TrySetResult();
    }

    private sealed class RecordingSynthesizer : ICaseFileSynthesizer
    {
        public IReadOnlyList<CrumbSourceResult> Results { get; private set; } = [];

        public Task<AiSynthesis> SynthesizeAsync(
            CaseSubject subject,
            IReadOnlyList<CrumbSourceResult> results,
            AiSynthesis? previous,
            CancellationToken cancellationToken)
        {
            Results = results;
            return Task.FromResult(new AiSynthesis("complete", "Signal found", [], [], [], "hash"));
        }
    }

    private sealed class RecordingUpdates : ICaseUpdatePublisher
    {
        public List<string> Events { get; } = [];

        public Task PublishStatusAsync(
            Guid caseId,
            int version,
            string status,
            CancellationToken cancellationToken)
        {
            Events.Add($"status:{status}");
            return Task.CompletedTask;
        }

        public Task PublishCaseFileAsync(
            Guid caseId,
            int version,
            string status,
            IReadOnlyList<string> changedSections,
            CancellationToken cancellationToken)
        {
            Events.Add($"caseFile:{status}");
            return Task.CompletedTask;
        }
    }

    private sealed class Recurrence(bool fail = false) : IPatternCoordinator
    {
        public Task<PatternContext> ResolveProvisionalAsync(
            CaseRecord caseRecord,
            bool collectionEnabled,
            CancellationToken cancellationToken) => Task.FromResult(Context(caseRecord, SignatureStage.Provisional));

        public Task<PatternContext> ResolveFinalAsync(
            CaseRecord caseRecord,
            IReadOnlyList<Crumb> crumbs,
            CancellationToken cancellationToken) => Task.FromResult(fail
                ? new PatternContext("unavailable", "v1", SignatureStage.Final, null, null, null, null, null,
                    [], 0, null, null, [], [], 0, "InvalidOperationException")
                : Context(caseRecord, SignatureStage.Final));

        private static PatternContext Context(CaseRecord caseRecord, SignatureStage stage) =>
            new("available", "v1", stage, "PAYMENTS-FAILING-1234", Guid.NewGuid(),
                PatternLifecycleState.New, "new", null, ["new deterministic Signature"], 1,
                caseRecord.OpenedAt, caseRecord.OpenedAt, [], [], 1);
    }
}
