using System.Text;
using System.Text.Json.Nodes;
using System.Diagnostics.Metrics;
using Panko.Api.Crumbs;
using Panko.Api.Domain;
using Panko.Api.Patterns;
using Panko.Api.Signatures;
using Panko.Api.Infrastructure;
using Panko.Api.Cases;
using Panko.Api.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Panko.Api.Tests;

[Collection(PostgresPatternCollection.Name)]
public sealed class PagerDutyProjectionPostgresTests(PostgresFixture database) : IAsyncLifetime
{
    private static readonly DateTimeOffset TriggeredAt =
        DateTimeOffset.Parse("2026-08-03T09:45:00Z");

    public Task InitializeAsync() => database.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task RunnerProjectsCanonicalLifecycleAndAtomicallyRetainsConnectorSnapshot()
    {
        var repository = new PostgresCaseStore(database.DataSource, TimeProvider.System);
        var accepted = await AcceptAsync(
            repository,
            "evt-projection-triggered",
            "incident.triggered",
            TriggeredAt);
        await AcceptAsync(
            repository,
            "evt-projection-acknowledged",
            "incident.acknowledged",
            TriggeredAt.AddMinutes(5));
        var scheduleBefore = await ReadWorkScheduleAsync();

        await Runner(repository, new SnapshotConnector(), new ImmediateSynthesizer())
            .RunAsync(accepted.CaseId, CancellationToken.None);

        var stored = await repository.GetCaseAsync(accepted.CaseId, CancellationToken.None);
        var caseFile = await repository.GetCaseFileAsync(accepted.CaseId, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.NotNull(caseFile);
        Assert.Equal(1, stored.InputVersion);
        Assert.Equal(1, stored.ProjectedInputVersion);
        Assert.Equal(1, caseFile.InputVersion);
        Assert.Equal(1, caseFile.ProjectedInputVersion);
        Assert.Single(caseFile.Trail, item => item.Kind == "pagerduty-incident-triggered");
        Assert.Contains(caseFile.Trail, item => item.Kind == "pagerduty-incident-acknowledged");
        Assert.Equal(scheduleBefore, await ReadWorkScheduleAsync());
        Assert.Equal(1, await ScalarAsync<int>("""
            select count(distinct snapshot_version)
            from crumb_source_snapshots
            """));
        Assert.Equal("pagerduty", await ScalarAsync<string>("""
            select source from crumb_source_snapshots
            """));
    }

    [Fact]
    public async Task LifecycleAcceptedDuringSynthesisMakesTheOldCommitRetryWithoutOverclaiming()
    {
        var repository = new PostgresCaseStore(database.DataSource, TimeProvider.System);
        var accepted = await AcceptAsync(
            repository,
            "evt-race-triggered",
            "incident.triggered",
            TriggeredAt);
        var synthesizer = new BlockingSynthesizer();
        var firstRun = Runner(repository, new SnapshotConnector(), synthesizer)
            .RunAsync(accepted.CaseId, CancellationToken.None);
        await synthesizer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await AcceptAsync(
            repository,
            "evt-race-acknowledged",
            "incident.acknowledged",
            TriggeredAt.AddMinutes(5));
        synthesizer.Release();
        await Assert.ThrowsAsync<InvalidOperationException>(() => firstRun);

        var staleCase = await repository.GetCaseAsync(accepted.CaseId, CancellationToken.None);
        var staleCaseFile = await repository.GetCaseFileAsync(accepted.CaseId, CancellationToken.None);
        Assert.NotNull(staleCase);
        Assert.NotNull(staleCaseFile);
        Assert.Equal(1, staleCase.InputVersion);
        Assert.Equal(0, staleCase.ProjectedInputVersion);
        Assert.Equal(0, staleCaseFile.InputVersion);
        Assert.Equal(0, staleCaseFile.ProjectedInputVersion);
        Assert.DoesNotContain(staleCaseFile.Trail, item => item.Kind == "pagerduty-incident-acknowledged");
        Assert.Equal(0, await ScalarAsync<int>("select count(*) from crumb_source_snapshots"));

        await Runner(repository, new SnapshotConnector(), new ImmediateSynthesizer())
            .RunAsync(accepted.CaseId, CancellationToken.None);

        var current = await repository.GetCaseFileAsync(accepted.CaseId, CancellationToken.None);
        Assert.NotNull(current);
        Assert.Equal(1, current.InputVersion);
        Assert.Equal(1, current.ProjectedInputVersion);
        Assert.Contains(current.Trail, item => item.Kind == "pagerduty-incident-acknowledged");
        Assert.Equal(1, await ScalarAsync<int>("""
            select count(distinct snapshot_version)
            from crumb_source_snapshots
            """));
    }

    [Fact]
    public async Task PagerDutyAdmissionRecordsCreatedAcceptedAndDeduplicatedCaseMetrics()
    {
        var measurements = new List<MetricMeasurement>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, current) =>
            {
                if (instrument.Meter.Name == CaseTelemetry.MeterName)
                {
                    current.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Add(new MetricMeasurement(
                instrument.Name,
                value,
                tags.ToArray())));
        listener.Start();
        var repository = new PostgresCaseStore(
            database.DataSource,
            TimeProvider.System,
            new CaseTelemetry());

        await AcceptAsync(repository, "evt-metric-triggered", "incident.triggered", TriggeredAt);
        await AcceptAsync(
            repository,
            "evt-metric-acknowledged",
            "incident.acknowledged",
            TriggeredAt.AddMinutes(5));
        await AcceptAsync(
            repository,
            "evt-metric-acknowledged",
            "incident.acknowledged",
            TriggeredAt.AddMinutes(5));

        var created = Assert.Single(measurements, item =>
            item.Name == "panko.cases.created");
        Assert.Equal(1, created.Value);
        Assert.Contains(created.Tags, tag =>
            tag.Key == "origin" && Equals(tag.Value, "pagerduty"));
        Assert.Equal(2, measurements
            .Where(item => item.Name == "panko.case_crumbs.accepted")
            .Sum(item => item.Value));
        Assert.Equal(1, measurements
            .Where(item => item.Name == "panko.case_crumbs.deduplicated")
            .Sum(item => item.Value));
    }

    private static CaseFileBuilder Runner(
        PostgresCaseStore repository,
        ICrumbSourceAdapter connector,
        ICaseFileSynthesizer synthesizer)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new PankoOptions
        {
            CrumbCollectionEnabled = true,
            CrumbWindowMinutes = 30,
            CrumbMaximumWindowMinutes = 30
        });
        var sources = new CrumbSourceRegistry(
            [connector],
            TestConfiguration.CrumbSources());
        return new CaseFileBuilder(
            repository,
            new RecipeProvider(),
            sources,
            new AdaptiveCrumbCollector(
                options,
                TimeProvider.System,
                NullLogger<AdaptiveCrumbCollector>.Instance),
            new CaseFileProjectionBuilder(TimeProvider.System),
            synthesizer,
            new CaseFileTransitions(repository, new NoOpUpdates()),
            options,
            TimeProvider.System,
            new NoOpRecurrence(),
            NullLogger<CaseFileBuilder>.Instance);
    }

    private static Task<(Guid CaseId, bool IsDuplicate)> AcceptAsync(
        PostgresCaseStore repository,
        string eventId,
        string eventType,
        DateTimeOffset occurredAt)
    {
        var webhook = new PagerDutyWebhookEvent(
            eventId,
            eventType,
            "PD-PROJECTION-1",
            "P123",
            "Payments failing",
            "high",
            "https://pagerduty.example/incidents/PD-PROJECTION-1",
            TriggeredAt,
            occurredAt,
            new Dictionary<string, string> { ["environment"] = "production" });
        return repository.AcceptWebhookAsync(
            webhook,
            RecipeProvider.Recipe,
            Encoding.UTF8.GetBytes($"{{\"event\":\"{eventId}\"}}"),
            CancellationToken.None);
    }

    private async Task<IReadOnlyList<WorkSchedule>> ReadWorkScheduleAsync()
    {
        await using var command = database.DataSource.CreateCommand("""
            select id, kind, idempotency_key, due_at, target_input_version
            from work_items
            order by id
            """);
        var output = new List<WorkSchedule>();
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        while (await reader.ReadAsync(CancellationToken.None))
        {
            output.Add(new WorkSchedule(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetFieldValue<DateTimeOffset>(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4)));
        }
        return output;
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        var value = await command.ExecuteScalarAsync(CancellationToken.None);
        return (T)Convert.ChangeType(value!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed record WorkSchedule(
        long Id,
        string Kind,
        string IdempotencyKey,
        DateTimeOffset DueAt,
        long? TargetInputVersion);

    private sealed record MetricMeasurement(
        string Name,
        long Value,
        KeyValuePair<string, object?>[] Tags);

    private sealed class RecipeProvider : IRecipeProvider
    {
        public static Recipe Recipe { get; } = new()
        {
            Id = "payments-production",
            PagerDutyServiceId = "P123",
            Team = "payments",
            SlackChannel = "#cases",
            PagerDuty = new PagerDutyScope()
        };

        public string Revision => "test-v1";

        public Recipe Resolve(
            string serviceId,
            IReadOnlyDictionary<string, string> labels) => Recipe;
    }

    private sealed class SnapshotConnector : ICrumbSourceAdapter
    {
        public string Source => "pagerduty";

        public Task<CrumbSourceResult> CollectAsync(
            CaseContext context,
            CrumbScope scope,
            CancellationToken cancellationToken)
        {
            var crumb = new Crumb(
                "snapshot-crumb",
                Source,
                scope.End,
                null,
                "pagerduty-incident",
                "warning",
                "PagerDuty connector snapshot",
                null,
                null,
                0.9,
                new JsonObject());
            return Task.FromResult(new CrumbSourceResult(
                Source,
                CrumbSourceHealth.Complete,
                [crumb],
                [],
                [],
                1,
                null));
        }
    }

    private sealed class ImmediateSynthesizer : ICaseFileSynthesizer
    {
        public Task<AiSynthesis> SynthesizeAsync(
            CaseSubject subject,
            IReadOnlyList<CrumbSourceResult> results,
            AiSynthesis? previous,
            CancellationToken cancellationToken) =>
            Task.FromResult(new AiSynthesis("complete", "done", [], [], [], "hash"));
    }

    private sealed class BlockingSynthesizer : ICaseFileSynthesizer
    {
        private readonly TaskCompletionSource release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

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

    private sealed class NoOpRecurrence : IPatternCoordinator
    {
        public Task<PatternContext> ResolveProvisionalAsync(
            CaseRecord caseRecord,
            bool collectionEnabled,
            CancellationToken cancellationToken) =>
            Task.FromResult(Context(caseRecord, SignatureStage.Provisional));

        public Task<PatternContext> ResolveFinalAsync(
            CaseRecord caseRecord,
            IReadOnlyList<Crumb> crumbs,
            CancellationToken cancellationToken) =>
            Task.FromResult(Context(caseRecord, SignatureStage.Final));

        private static PatternContext Context(CaseRecord caseRecord, SignatureStage stage) =>
            new("available", "test-v1", stage, "PAYMENTS-1", Guid.NewGuid(),
                PatternLifecycleState.New, "new", null, [], 1,
                caseRecord.OpenedAt, caseRecord.OpenedAt, [], [], 1);
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
}
