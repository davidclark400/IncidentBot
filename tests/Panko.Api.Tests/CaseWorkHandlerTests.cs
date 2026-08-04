using System.Text.Json.Nodes;
using Panko.Api.Crumbs;
using Panko.Api.Domain;
using Panko.Api.Cases;
using Panko.Api.Options;
using Panko.Api.Recipes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SubmittedCrumbKind = Panko.Contracts.SubmittedCrumbKind;

namespace Panko.Api.Tests;

public sealed class CaseWorkHandlerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-03T10:00:00Z");

    [Fact]
    public async Task SameTargetProjectionResetsPreviouslyCompletedSynthesisToPending()
    {
        using var recipes = new RecipeFixture();
        var caseRecord = BuildCase(inputVersion: 1, projectedInputVersion: 1) with
        {
            WorkflowGeneration = 1,
            ProjectedWorkflowGeneration = 0
        };
        var previous = BuildCaseFile(caseRecord) with
        {
            Ai = new AiSynthesis(
                "complete",
                "Stale synthesis",
                ["stale contributor"],
                ["stale unknown"],
                ["stale check"],
                "stale-hash")
        };
        var repository = new StubRepository(caseRecord)
        {
            Inputs = [Input(caseRecord.Id)],
            StoredCaseFile = previous
        };
        var handler = Handler(repository, recipes.Store, new CountingSynthesizer());

        await handler.ProcessAsync(
            new WorkItem(1, caseRecord.Id, CaseWorkKinds.Project, 1, 1, 1),
            CancellationToken.None);

        var committed = Assert.IsType<CaseFile>(repository.CommittedProjection);
        Assert.Equal("pending", committed.Ai.Status);
        Assert.Null(committed.Ai.Summary);
        Assert.Empty(committed.Ai.PossibleContributors);
        Assert.Empty(committed.Ai.Unknowns);
        Assert.Empty(committed.Ai.RecommendedChecks);
        Assert.Null(committed.Ai.CrumbHash);
    }

    [Fact]
    public async Task AnalysisForSupersededInputTargetReturnsBeforeCallingSynthesizer()
    {
        using var recipes = new RecipeFixture();
        var caseRecord = BuildCase(inputVersion: 2, projectedInputVersion: 1);
        var repository = new StubRepository(caseRecord) { StoredCaseFile = BuildCaseFile(caseRecord) };
        var synthesizer = new CountingSynthesizer();
        var handler = Handler(repository, recipes.Store, synthesizer);

        await handler.ProcessAsync(
            new WorkItem(1, caseRecord.Id, CaseWorkKinds.Analyse, 1, 1),
            CancellationToken.None);

        Assert.Equal(0, synthesizer.Calls);
        Assert.Null(repository.CommittedAnalysis);
    }

    [Fact]
    public async Task AnalysisForSupersededWorkflowGenerationReturnsBeforeCallingSynthesizer()
    {
        using var recipes = new RecipeFixture();
        var caseRecord = BuildCase(inputVersion: 1, projectedInputVersion: 1) with
        {
            WorkflowGeneration = 2,
            ProjectedWorkflowGeneration = 1
        };
        var repository = new StubRepository(caseRecord) { StoredCaseFile = BuildCaseFile(caseRecord) };
        var synthesizer = new CountingSynthesizer();
        var handler = Handler(repository, recipes.Store, synthesizer);

        await handler.ProcessAsync(
            new WorkItem(1, caseRecord.Id, CaseWorkKinds.Analyse, 1, 1, 1),
            CancellationToken.None);

        Assert.Equal(0, synthesizer.Calls);
        Assert.Null(repository.CommittedAnalysis);
    }

    private static CaseWorkHandler Handler(
        ICaseInputStore repository,
        RecipeStore recipes,
        ICaseFileSynthesizer synthesizer) => new(
        repository,
        recipes,
        null!,
        null!,
        new CaseFileProjectionBuilder(new FixedTimeProvider(Now)),
        synthesizer,
        new NoOpUpdates(),
        Microsoft.Extensions.Options.Options.Create(new PankoOptions()),
        new CaseTelemetry(),
        new FixedTimeProvider(Now),
        NullLogger<CaseWorkHandler>.Instance);

    private static CaseRecord BuildCase(long inputVersion, long projectedInputVersion) => new(
        Guid.NewGuid(),
        null,
        "payments-api",
        "payments-production",
        "Payment timeouts",
        "high",
        PagerDutyIncidentState.Triggered,
        Now,
        Now,
        2,
        CaseProgression.Ready,
        false,
        null,
        string.Empty,
        null,
        new Dictionary<string, string>())
    {
        Team = "payments",
        Origin = new CaseOrigin(CaseOriginKind.Agent, null),
        CreatedBy = "agent@example.internal",
        InputVersion = inputVersion,
        ProjectedInputVersion = projectedInputVersion,
        PublishToSlack = false
    };

    private static CaseFile BuildCaseFile(CaseRecord caseRecord) => new(
        caseRecord.Id,
        null,
        caseRecord.ServiceId,
        caseRecord.RecipeId,
        "test-revision",
        caseRecord.Title,
        caseRecord.Urgency,
        caseRecord.PagerDutyState,
        caseRecord.Status,
        caseRecord.OpenedAt,
        Now,
        caseRecord.Version,
        "Existing deterministic Case File",
        new AiSynthesis("pending", null, [], [], [], null),
        [],
        [],
        [],
        [])
    {
        Origin = caseRecord.Origin,
        InputVersion = caseRecord.InputVersion,
        ProjectedInputVersion = caseRecord.ProjectedInputVersion,
        CreatedBy = caseRecord.CreatedBy
    };

    private static CaseInput Input(Guid caseId) => new(
        Guid.NewGuid(),
        caseId,
        1,
        1,
        "agent@example.internal",
        "deployment-1",
        SubmittedCrumbKind.Crumb,
        Now,
        Now,
        "deployment",
        "warning",
        "Deployment completed",
        null,
        "gitlab",
        null,
        null,
        "agent@example.internal",
        "deployment",
        "deployment-1",
        new JsonObject(),
        "submitted",
        "payload-hash",
        null,
        null,
        null);

    private sealed class StubRepository(CaseRecord caseRecord) : ICaseInputStore
    {
        public IReadOnlyList<CaseInput> Inputs { get; init; } = [];
        public CaseFile? StoredCaseFile { get; init; }
        public CaseFile? CommittedProjection { get; private set; }
        public CaseFile? CommittedAnalysis { get; private set; }

        public Task<CaseRecord?> GetCaseAsync(Guid caseId, CancellationToken cancellationToken) =>
            Task.FromResult<CaseRecord?>(caseRecord);

        public Task<CaseFile?> GetCaseFileAsync(Guid caseId, CancellationToken cancellationToken) =>
            Task.FromResult(StoredCaseFile);

        public Task<IReadOnlyList<CaseInput>> ListInputsAsync(
            Guid caseId,
            long? throughInputVersion,
            bool includeInactive,
            CancellationToken cancellationToken) => Task.FromResult(Inputs);

        public Task<IReadOnlyList<CrumbSourceResult>> GetLatestCrumbSourceResultsAsync(
            Guid caseId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CrumbSourceResult>>([]);

        public Task<int?> CommitProjectionAsync(
            CaseRecord expected,
            CaseFile caseFile,
            long targetInputVersion,
            CancellationToken cancellationToken,
            long? targetWorkflowGeneration = null)
        {
            CommittedProjection = caseFile;
            return Task.FromResult<int?>(expected.Version + 1);
        }

        public Task<int?> CommitAnalysisAsync(
            CaseRecord expected,
            CaseFile caseFile,
            long projectedInputVersion,
            CancellationToken cancellationToken,
            long? targetWorkflowGeneration = null)
        {
            CommittedAnalysis = caseFile;
            return Task.FromResult<int?>(expected.Version + 1);
        }

        public Task<CreateCaseResult> CreateAsync(
            CaseRecord proposed,
            CaseFile initialCaseFile,
            CaseInput createdInput,
            string producerPrincipal,
            string idempotencyKey,
            string requestHash,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AppendCrumbsResult> AppendAsync(
            Guid caseId,
            string producerPrincipal,
            string batchId,
            string requestHash,
            IReadOnlyList<NormalizedCrumb> normalizedCrumbs,
            int maximumCrumbsPerCase,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> QueueProjectionAsync(
            Guid caseId,
            long targetInputVersion,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> QueueRefreshAsync(
            Guid caseId,
            long targetInputVersion,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task CloseAsync(
            Guid caseId,
            string producerPrincipal,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<CaseRecord>> ListRecentAsync(
            int limit,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<long> SaveCrumbSourceSnapshotsAsync(
            Guid caseId,
            IReadOnlyList<CrumbSourceResult> results,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class CountingSynthesizer : ICaseFileSynthesizer
    {
        public int Calls { get; private set; }

        public Task<AiSynthesis> SynthesizeAsync(
            CaseSubject subject,
            IReadOnlyList<CrumbSourceResult> results,
            AiSynthesis? previous,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new AiSynthesis("complete", "Complete", [], [], [], "hash"));
        }
    }

    private sealed class NoOpUpdates : ICaseUpdatePublisher
    {
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
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecipeFixture : IDisposable
    {
        private readonly string path = Path.Combine(
            Path.GetTempPath(),
            $"panko-case-work-recipe-{Guid.NewGuid():N}.yaml");

        public RecipeFixture()
        {
            File.WriteAllText(path, """
                version: 3
                revision: test-revision
                fallbackSlackChannel: C0000000000
                recipes:
                  - id: payments-production
                    pagerDutyServiceId: P123PAYMENTS
                    team: payments
                    slackChannel: C0123456789
                """);
            Store = new RecipeStore(
                Microsoft.Extensions.Options.Options.Create(
                    new PankoOptions { RecipesPath = path }),
                new TestEnvironment(),
                new CrumbSourceRegistry(
                    Array.Empty<ICrumbSourceAdapter>(),
                    TestConfiguration.CrumbSources()));
        }

        public RecipeStore Store { get; }

        public void Dispose() => File.Delete(path);
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Panko.Api.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
