using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json.Nodes;
using Panko.Api.Domain;
using Panko.Api.Cases;
using Panko.Api.Security;
using Npgsql;
using SubmittedCrumbKind = Panko.Contracts.SubmittedCrumbKind;

namespace Panko.Api.Tests;

[Collection(PostgresPatternCollection.Name)]
public sealed class CasePostgresTests(PostgresFixture database) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-03T10:00:00Z");
    private const string Producer = "agent@example.internal";

    public Task InitializeAsync() => database.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ConcurrentCreateRetriesPersistOneCaseAndOneCanonicalCreatedInput()
    {
        var repository = Repository();
        var attempts = Enumerable.Range(0, 8)
            .Select(index => CreateAsync(
                repository,
                Guid.NewGuid(),
                "agent-run-concurrent",
                "same-create-payload"))
            .ToArray();

        var results = await Task.WhenAll(attempts);

        Assert.Single(results.Select(result => result.Case.Id).Distinct());
        Assert.Single(results, result => !result.Duplicate);
        Assert.Equal(7, results.Count(result => result.Duplicate));
        Assert.All(results, result =>
        {
            Assert.Null(result.Case.PagerDutyIncidentId);
            Assert.Equal(CaseOriginKind.Agent, result.Case.Origin.Kind);
            Assert.Null(result.Case.Origin.ExternalId);
            Assert.Equal(0, result.Case.InputVersion);
            Assert.Equal(0, result.Case.ProjectedInputVersion);
        });
        Assert.Equal(1, await ScalarAsync<int>("select count(*) from cases"));
        Assert.Equal(1, await ScalarAsync<int>("select count(*) from case_create_receipts"));
        Assert.Equal(1, await ScalarAsync<int>("select count(*) from case_inputs"));
    }

    [Fact]
    public async Task ConcurrentAppendsAllocateOneDurableOrderAndCoalesceProjectionWork()
    {
        var repository = Repository();
        var created = await CreateAsync(repository, Guid.NewGuid(), "agent-run-appends", "create-hash");
        var caseId = created.Case.Id;
        var firstBatch = new[] { NormalizedInput(caseId, "a-1"), NormalizedInput(caseId, "a-2") };
        var secondBatch = new[] { NormalizedInput(caseId, "b-1"), NormalizedInput(caseId, "b-2") };

        var results = await Task.WhenAll(
            repository.AppendAsync(
                caseId, Producer, "batch-a", "batch-a-hash", firstBatch, 100, CancellationToken.None),
            repository.AppendAsync(
                caseId, Producer, "batch-b", "batch-b-hash", secondBatch, 100, CancellationToken.None));

        Assert.All(results, result => Assert.Equal(2, result.Accepted));
        Assert.Equal(new long[] { 2, 4 }, results.Select(result => result.InputVersion).Order().ToArray());
        var stored = await repository.GetCaseAsync(caseId, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(4, stored.InputVersion);
        Assert.Equal(CaseProgression.Rebuilding, stored.Status);
        Assert.Equal(2, stored.WorkflowGeneration);
        var inputs = await repository.ListInputsAsync(
            caseId, throughInputVersion: null, includeInactive: true, CancellationToken.None);
        Assert.Equal(new long[] { 0, 1, 2, 3, 4 }, inputs.Select(item => item.Sequence));
        Assert.Equal(5, inputs.Select(item => item.Id).Distinct().Count());
        Assert.Equal(1, await ScalarAsync<int>("""
            select count(*) from work_items
            where kind = 'project-case' and completed_at is null
            """));
        Assert.Equal(4L, await ScalarAsync<long>("""
            select target_input_version from work_items
            where kind = 'project-case' and completed_at is null
            """));
        Assert.Equal(2L, await ScalarAsync<long>("""
            select target_workflow_generation from work_items
            where kind = 'project-case' and completed_at is null
            """));

        var replay = await repository.AppendAsync(
            caseId, Producer, "batch-a", "batch-a-hash", firstBatch, 100, CancellationToken.None);
        Assert.True(replay.DuplicateBatch);
        Assert.Equal(2, replay.Accepted);
        Assert.Equal(5, await ScalarAsync<int>("select count(*) from case_inputs"));

        var duplicateInput = await repository.AppendAsync(
            caseId,
            Producer,
            "batch-c",
            "batch-c-hash",
            [NormalizedInput(caseId, "a-1")],
            100,
            CancellationToken.None);
        Assert.Equal(0, duplicateInput.Accepted);
        Assert.Equal(1, duplicateInput.Duplicates);
        Assert.Equal(4, duplicateInput.InputVersion);
        Assert.False(duplicateInput.RebuildQueued);
    }

    [Fact]
    public async Task InvalidSupersessionRollsBackTheWholeBatch()
    {
        var repository = Repository();
        var created = await CreateAsync(repository, Guid.NewGuid(), "agent-run-rollback", "create-hash");
        var caseId = created.Case.Id;

        await Assert.ThrowsAsync<CaseValidationException>(() => repository.AppendAsync(
            caseId,
            Producer,
            "invalid-batch",
            "invalid-batch-hash",
            [
                NormalizedInput(caseId, "inserted-before-error"),
                NormalizedInput(caseId, "invalid-replacement", supersedes: "missing-event")
            ],
            100,
            CancellationToken.None));

        var stored = await repository.GetCaseAsync(caseId, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(0, stored.InputVersion);
        Assert.Equal(1, await ScalarAsync<int>("select count(*) from case_inputs"));
        Assert.Equal(0, await ScalarAsync<int>("select count(*) from case_command_receipts"));
        Assert.Equal(0, await ScalarAsync<int>("select count(*) from work_items"));
    }

    [Fact]
    public async Task SupersessionProducesCorrectHistoricalAndCurrentActiveSets()
    {
        var repository = Repository();
        var created = await CreateAsync(repository, Guid.NewGuid(), "agent-run-supersession", "create-hash");
        var caseId = created.Case.Id;
        await repository.AppendAsync(
            caseId,
            Producer,
            "original-batch",
            "original-hash",
            [NormalizedInput(caseId, "original")],
            100,
            CancellationToken.None);
        await repository.AppendAsync(
            caseId,
            Producer,
            "replacement-batch",
            "replacement-hash",
            [NormalizedInput(caseId, "replacement", supersedes: "original")],
            100,
            CancellationToken.None);

        var atVersionOne = await repository.ListInputsAsync(
            caseId, 1, includeInactive: false, CancellationToken.None);
        var atVersionTwo = await repository.ListInputsAsync(
            caseId, 2, includeInactive: false, CancellationToken.None);
        var audit = await repository.ListInputsAsync(
            caseId, throughInputVersion: null, includeInactive: true, CancellationToken.None);

        Assert.Equal(new[] { "case-created", "original" }, atVersionOne.Select(item => item.ClientCrumbId));
        Assert.Equal(new[] { "case-created", "replacement" }, atVersionTwo.Select(item => item.ClientCrumbId));
        var original = Assert.Single(audit, item => item.ClientCrumbId == "original");
        var replacement = Assert.Single(audit, item => item.ClientCrumbId == "replacement");
        Assert.Equal(2, original.RetractedInputVersion);
        Assert.Equal(original.Id, replacement.SupersedesCrumbId);
    }

    [Fact]
    public async Task StaleProjectionCommitsOnlyItsExactTargetAndCannotRegressTheCaseFile()
    {
        var repository = Repository();
        var created = await CreateAsync(repository, Guid.NewGuid(), "agent-run-projection", "create-hash");
        var caseId = created.Case.Id;
        await repository.AppendAsync(
            caseId, Producer, "batch-1", "hash-1", [NormalizedInput(caseId, "event-1")], 100,
            CancellationToken.None);
        var targetOneBase = await repository.GetCaseAsync(caseId, CancellationToken.None);
        Assert.NotNull(targetOneBase);
        await repository.AppendAsync(
            caseId, Producer, "batch-2", "hash-2", [NormalizedInput(caseId, "event-2")], 100,
            CancellationToken.None);

        var targetOneVersion = await repository.CommitProjectionAsync(
            targetOneBase,
            BuildCaseFile(targetOneBase, targetInputVersion: 1),
            1,
            CancellationToken.None);
        Assert.Equal(2, targetOneVersion);
        var afterStaleProjection = await repository.GetCaseAsync(caseId, CancellationToken.None);
        Assert.NotNull(afterStaleProjection);
        Assert.Equal(2, afterStaleProjection.InputVersion);
        Assert.Equal(1, afterStaleProjection.ProjectedInputVersion);
        Assert.Equal(CaseProgression.Rebuilding, afterStaleProjection.Status);

        var targetTwoVersion = await repository.CommitProjectionAsync(
            afterStaleProjection,
            BuildCaseFile(afterStaleProjection, targetInputVersion: 2),
            2,
            CancellationToken.None);
        Assert.Equal(3, targetTwoVersion);
        Assert.Null(await repository.CommitProjectionAsync(
            targetOneBase,
            BuildCaseFile(targetOneBase, targetInputVersion: 1),
            1,
            CancellationToken.None));
        var current = await repository.GetCaseAsync(caseId, CancellationToken.None);
        Assert.NotNull(current);
        Assert.Equal(2, current.ProjectedInputVersion);
        Assert.Equal(3, current.Version);
    }

    [Fact]
    public async Task ExplicitWorkRequestsPersistTheirTransitionalStatusWithTheQueueItem()
    {
        var repository = Repository();
        var rebuild = await CreateAsync(
            repository, Guid.NewGuid(), "agent-run-explicit-rebuild", "create-hash");
        var refresh = await CreateAsync(
            repository, Guid.NewGuid(), "agent-run-explicit-refresh", "create-hash");

        Assert.True(await repository.QueueProjectionAsync(
            rebuild.Case.Id, 0, CancellationToken.None));
        Assert.True(await repository.QueueRefreshAsync(
            refresh.Case.Id, 0, CancellationToken.None));

        var rebuilding = await repository.GetCaseAsync(rebuild.Case.Id, CancellationToken.None);
        var refreshing = await repository.GetCaseAsync(refresh.Case.Id, CancellationToken.None);
        Assert.NotNull(rebuilding);
        Assert.NotNull(refreshing);
        Assert.Equal(CaseProgression.Rebuilding, rebuilding.Status);
        Assert.Equal(CaseProgression.RefreshingSources, refreshing.Status);
        Assert.Equal(1, await ScalarAsync<int>($"""
            select count(*) from work_items
            where case_id = '{rebuild.Case.Id:D}'
              and kind = 'project-case' and completed_at is null
            """));
        Assert.Equal(1, await ScalarAsync<int>($"""
            select count(*) from work_items
            where case_id = '{refresh.Case.Id:D}'
              and kind = 'refresh-case-sources' and completed_at is null
            """));
    }

    [Fact]
    public async Task RecentCaseLimitIsAppliedAfterTeamScopeFiltering()
    {
        var repository = Repository();
        var accessible = await CreateAsync(
            repository, Guid.NewGuid(), "agent-run-recent-accessible", "create-hash");
        var hidden = await CreateAsync(
            repository, Guid.NewGuid(), "agent-run-recent-hidden", "create-hash");
        await ExecuteAsync($"""
            update cases
            set team = 'payments', updated_at = '2026-08-03T09:00:00Z'
            where id = '{accessible.Case.Id:D}';
            update cases
            set team = 'platform', updated_at = '2026-08-03T11:00:00Z'
            where id = '{hidden.Case.Id:D}';
            """);

        var recent = await repository.ListRecentAsync(
            1,
            TeamAccessScope.Restricted(["payments"]),
            CancellationToken.None);

        Assert.Collection(recent, caseRecord => Assert.Equal(accessible.Case.Id, caseRecord.Id));
    }

    [Fact]
    public async Task SameTargetProjectionRetriesOnCaseFileChangeAndKeepsSnapshotAnalysisScheduled()
    {
        var repository = Repository();
        var created = await CreateAsync(
            repository, Guid.NewGuid(), "agent-run-same-target", "create-hash");
        var caseId = created.Case.Id;
        await repository.AppendAsync(
            caseId,
            Producer,
            "crumb-batch",
            "crumb-hash",
            [NormalizedInput(caseId, "crumb")],
            100,
            CancellationToken.None);
        var firstExpected = await repository.GetCaseAsync(caseId, CancellationToken.None);
        Assert.NotNull(firstExpected);

        Assert.Equal(2, await repository.CommitProjectionAsync(
            firstExpected,
            CaseFileWithCrumbs(firstExpected, 1),
            1,
            CancellationToken.None));
        var afterFirstProjection = await repository.GetCaseAsync(caseId, CancellationToken.None);
        Assert.NotNull(afterFirstProjection);
        Assert.Equal(CaseProgression.Analysing, afterFirstProjection.Status);
        Assert.EndsWith(":case-file:2", await ScalarAsync<string>($"""
            select idempotency_key from work_items
            where case_id = '{caseId:D}'
              and kind = 'analyse-case' and completed_at is null
            """), StringComparison.Ordinal);

        await ExecuteAsync($"""
            update work_items set locked_until = now() + interval '1 minute'
            where case_id = '{caseId:D}'
              and kind = 'analyse-case' and completed_at is null
            """);
        Assert.Equal(3, await repository.CommitProjectionAsync(
            afterFirstProjection,
            CaseFileWithCrumbs(afterFirstProjection, 1),
            1,
            CancellationToken.None));

        Assert.Equal(2, await ScalarAsync<int>($"""
            select count(*) from work_items
            where case_id = '{caseId:D}'
              and kind = 'analyse-case' and completed_at is null
            """));
        Assert.Equal(1, await ScalarAsync<int>($"""
            select count(*) from work_items
            where case_id = '{caseId:D}'
              and kind = 'analyse-case' and completed_at is null
              and idempotency_key like '%:case-file:3'
            """));
        await Assert.ThrowsAsync<CaseConflictException>(() =>
            repository.CommitProjectionAsync(
                afterFirstProjection,
                CaseFileWithCrumbs(afterFirstProjection, 1),
                1,
                CancellationToken.None));
    }

    [Fact]
    public async Task RetiringUnleasedAnalysisWorkRecordsAnAvoidedLlmCall()
    {
        var measurements = new ConcurrentQueue<(long Count, string? Reason)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, currentListener) =>
        {
            if (instrument.Meter.Name == CaseTelemetry.MeterName
                && instrument.Name == "panko.case_analysis.llm_calls_avoided")
            {
                currentListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            string? reason = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "reason") reason = tag.Value as string;
            }
            measurements.Enqueue((measurement, reason));
        });
        listener.Start();

        var repository = Repository(new CaseTelemetry());
        var created = await CreateAsync(
            repository, Guid.NewGuid(), "agent-run-analysis-coalescing", "create-hash");
        var caseId = created.Case.Id;
        await repository.AppendAsync(
            caseId,
            Producer,
            "crumb-batch",
            "crumb-hash",
            [NormalizedInput(caseId, "crumb")],
            100,
            CancellationToken.None);
        var firstExpected = await repository.GetCaseAsync(caseId, CancellationToken.None);
        Assert.NotNull(firstExpected);
        await repository.CommitProjectionAsync(
            firstExpected,
            CaseFileWithCrumbs(firstExpected, 1),
            1,
            CancellationToken.None);
        var secondExpected = await repository.GetCaseAsync(caseId, CancellationToken.None);
        Assert.NotNull(secondExpected);

        await repository.CommitProjectionAsync(
            secondExpected,
            CaseFileWithCrumbs(secondExpected, 1),
            1,
            CancellationToken.None);

        Assert.Contains(
            measurements,
            measurement => measurement is (1, "queued-analysis-coalesced"));
        Assert.Equal(1, await ScalarAsync<int>($"""
            select count(*) from work_items
            where case_id = '{caseId:D}'
              and kind = 'analyse-case' and completed_at is null
            """));
    }

    [Fact]
    public async Task NewerWorkflowGenerationPreventsStaleCommitsFromOverwritingStatus()
    {
        var repository = Repository();
        var projectionCreated = await CreateAsync(
            repository, Guid.NewGuid(), "agent-run-status-projection-race", "create-hash");
        await repository.AppendAsync(
            projectionCreated.Case.Id,
            Producer,
            "projection-race-batch",
            "projection-race-hash",
            [NormalizedInput(projectionCreated.Case.Id, "projection-race")],
            100,
            CancellationToken.None);
        var staleProjection = await repository.GetCaseAsync(
            projectionCreated.Case.Id, CancellationToken.None);
        Assert.NotNull(staleProjection);
        Assert.Equal(1, staleProjection.WorkflowGeneration);

        await repository.QueueRefreshAsync(
            staleProjection.Id, staleProjection.InputVersion, CancellationToken.None);
        Assert.Null(await repository.CommitProjectionAsync(
            staleProjection,
            CaseFileWithCrumbs(staleProjection, staleProjection.InputVersion),
            staleProjection.InputVersion,
            CancellationToken.None,
            staleProjection.WorkflowGeneration));
        var afterStaleProjection = await repository.GetCaseAsync(
            staleProjection.Id, CancellationToken.None);
        Assert.NotNull(afterStaleProjection);
        Assert.Equal(CaseProgression.RefreshingSources, afterStaleProjection.Status);
        Assert.Equal(2, afterStaleProjection.WorkflowGeneration);
        Assert.Equal(0, afterStaleProjection.ProjectedWorkflowGeneration);

        var analysisCreated = await CreateAsync(
            repository, Guid.NewGuid(), "agent-run-status-analysis-race", "create-hash");
        await repository.AppendAsync(
            analysisCreated.Case.Id,
            Producer,
            "analysis-race-batch",
            "analysis-race-hash",
            [NormalizedInput(analysisCreated.Case.Id, "analysis-race")],
            100,
            CancellationToken.None);
        var projected = await repository.GetCaseAsync(
            analysisCreated.Case.Id, CancellationToken.None);
        Assert.NotNull(projected);
        Assert.Equal(2, await repository.CommitProjectionAsync(
            projected,
            CaseFileWithCrumbs(projected, projected.InputVersion),
            projected.InputVersion,
            CancellationToken.None,
            projected.WorkflowGeneration));
        var staleAnalysis = await repository.GetCaseAsync(projected.Id, CancellationToken.None);
        Assert.NotNull(staleAnalysis);

        await repository.QueueProjectionAsync(
            staleAnalysis.Id, staleAnalysis.InputVersion, CancellationToken.None);
        Assert.Null(await repository.CommitAnalysisAsync(
            staleAnalysis,
            CaseFileWithCrumbs(staleAnalysis, staleAnalysis.InputVersion) with
            {
                Ai = new AiSynthesis("complete", "Stale", [], [], [], "stale-hash"),
                Status = CaseProgression.Ready
            },
            staleAnalysis.InputVersion,
            CancellationToken.None,
            staleAnalysis.WorkflowGeneration));
        var afterStaleAnalysis = await repository.GetCaseAsync(
            staleAnalysis.Id, CancellationToken.None);
        Assert.NotNull(afterStaleAnalysis);
        Assert.Equal(CaseProgression.Rebuilding, afterStaleAnalysis.Status);
        Assert.Equal(2, afterStaleAnalysis.Version);
        Assert.Equal(2, afterStaleAnalysis.WorkflowGeneration);
        Assert.Equal(1, afterStaleAnalysis.ProjectedWorkflowGeneration);
    }

    [Fact]
    public async Task CloseIsIdempotentAuditableAndPreservesCommittedAppendReceipts()
    {
        var repository = Repository();
        var created = await CreateAsync(repository, Guid.NewGuid(), "agent-run-close", "create-hash");
        var caseId = created.Case.Id;
        var batch = new[] { NormalizedInput(caseId, "before-close") };
        await repository.AppendAsync(
            caseId, Producer, "before-close-batch", "before-close-hash", batch, 100,
            CancellationToken.None);

        await repository.CloseAsync(caseId, "closer@example.internal", CancellationToken.None);
        await repository.CloseAsync(caseId, "closer@example.internal", CancellationToken.None);

        var stored = await repository.GetCaseAsync(caseId, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.True(stored.IsFrozen);
        Assert.Equal(PagerDutyIncidentState.Resolved, stored.PagerDutyState);
        Assert.Equal(2, stored.InputVersion);
        var inputs = await repository.ListInputsAsync(
            caseId, throughInputVersion: null, includeInactive: true, CancellationToken.None);
        var close = Assert.Single(inputs, item => item.Category == "case-closed");
        Assert.Equal("closer@example.internal", close.ProducerPrincipal);
        Assert.Equal("closer@example.internal", close.Actor);
        Assert.Equal("system", close.TrustLevel);

        var replay = await repository.AppendAsync(
            caseId, Producer, "before-close-batch", "before-close-hash", batch, 100,
            CancellationToken.None);
        Assert.True(replay.DuplicateBatch);
        await Assert.ThrowsAsync<CaseConflictException>(() => repository.AppendAsync(
            caseId,
            Producer,
            "after-close-batch",
            "after-close-hash",
            [NormalizedInput(caseId, "after-close")],
            100,
            CancellationToken.None));
    }

    [Fact]
    public async Task EmptyConnectorRefreshReplacesThePriorGenerationAndSchedulesProjectionAtomically()
    {
        var repository = Repository();
        var created = await CreateAsync(repository, Guid.NewGuid(), "agent-run-snapshots", "create-hash");
        var caseId = created.Case.Id;
        var crumb = new Crumb(
            "gitlab-crumb",
            "gitlab",
            Now,
            null,
            "deployment",
            "warning",
            "Deployment completed",
            null,
            null,
            0.9,
            new JsonObject());
        var result = new CrumbSourceResult(
            "gitlab",
            CrumbSourceHealth.Complete,
            [crumb],
            [new TrailCandidate("gitlab-trail", Now, "gitlab", "deployment", "Deployment completed", "warning", null)],
            [],
            10,
            null);

        Assert.Equal(1, await repository.SaveCrumbSourceSnapshotsAsync(
            caseId, [result], CancellationToken.None));
        Assert.Single(await repository.GetLatestCrumbSourceResultsAsync(caseId, CancellationToken.None));
        Assert.Equal(2, await repository.SaveCrumbSourceSnapshotsAsync(
            caseId, [], CancellationToken.None));
        Assert.Empty(await repository.GetLatestCrumbSourceResultsAsync(caseId, CancellationToken.None));
        Assert.Equal(3, await repository.SaveCrumbSourceSnapshotsAsync(
            caseId, [], CancellationToken.None));
        Assert.Equal(4, await repository.SaveCrumbSourceSnapshotsAsync(
            caseId, [], CancellationToken.None));
        Assert.Equal(5, await repository.SaveCrumbSourceSnapshotsAsync(
            caseId, [], CancellationToken.None));
        Assert.Equal(1, await ScalarAsync<int>("""
            select count(*) from work_items
            where kind = 'project-case' and completed_at is null
            """));
        var key = await ScalarAsync<string>("""
            select idempotency_key from work_items
            where kind = 'project-case' and completed_at is null
            """);
        Assert.EndsWith(":snapshot:5", key, StringComparison.Ordinal);
        Assert.Equal(3, await ScalarAsync<int>($"""
            select count(distinct snapshot_version)
            from crumb_source_snapshots
            where case_id = '{caseId:D}'
            """));
        Assert.Equal(3L, await ScalarAsync<long>($"""
            select min(snapshot_version)
            from crumb_source_snapshots
            where case_id = '{caseId:D}'
            """));
        var stored = await repository.GetCaseAsync(caseId, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(CaseProgression.Rebuilding, stored.Status);
        Assert.Equal(5, stored.WorkflowGeneration);
    }

    private PostgresCaseInputStore Repository(
        CaseTelemetry? telemetry = null) =>
        new(database.DataSource, new FixedTimeProvider(Now), telemetry);

    private static Task<CreateCaseResult> CreateAsync(
        PostgresCaseInputStore repository,
        Guid caseId,
        string idempotencyKey,
        string requestHash)
    {
        var origin = new CaseOrigin(CaseOriginKind.Agent, null);
        var proposed = new CaseRecord(
            caseId,
            null,
            "payments-api",
            "payments-production",
            "Payment timeouts",
            "high",
            PagerDutyIncidentState.Triggered,
            Now,
            Now,
            0,
            CaseProgression.Open,
            false,
            null,
            string.Empty,
            null,
            new Dictionary<string, string> { ["environment"] = "production" })
        {
            Team = "payments",
            Origin = origin,
            CreatedBy = Producer,
            PublishToSlack = false
        };
        var createdInputId = CaseInputBoundary.DeterministicCrumbId(
            caseId, Producer, "case-created");
        var createdInput = new CaseInput(
            createdInputId,
            caseId,
            0,
            0,
            Producer,
            "case-created",
            SubmittedCrumbKind.Event,
            Now,
            Now,
            "case-created",
            "critical",
            "Case created by agent",
            null,
            "agent",
            null,
            null,
            Producer,
            "case",
            caseId.ToString("D"),
            new JsonObject(),
            "collected",
            "created-hash",
            null,
            null,
            null);
        var initial = BuildCaseFile(proposed, 0) with
        {
            CaseFileVersion = 0,
            Status = CaseProgression.Open,
            Trail =
            [
                new TrailCandidate(
                    $"case-input:{createdInputId:N}:trail",
                    Now,
                    "agent",
                    "case-created",
                    "Case created by agent",
                    "critical",
                    null)
            ]
        };
        return repository.CreateAsync(
            proposed,
            initial,
            createdInput,
            Producer,
            idempotencyKey,
            requestHash,
            CancellationToken.None);
    }

    private static NormalizedCrumb NormalizedInput(
        Guid caseId,
        string clientCrumbId,
        string? supersedes = null) => new(
        CaseInputBoundary.DeterministicCrumbId(caseId, Producer, clientCrumbId),
        clientCrumbId,
        SubmittedCrumbKind.Event,
        Now,
        "deployment",
        "warning",
        $"Event {clientCrumbId}",
        null,
        "gitlab",
        null,
        null,
        Producer,
        "deployment",
        clientCrumbId,
        new JsonObject(),
        supersedes,
        $"payload-{clientCrumbId}");

    private static CaseFile BuildCaseFile(CaseRecord caseRecord, long targetInputVersion) => new(
        caseRecord.Id,
        caseRecord.PagerDutyIncidentId,
        caseRecord.ServiceId,
        caseRecord.RecipeId,
        "test-revision",
        caseRecord.Title,
        caseRecord.Urgency,
        caseRecord.PagerDutyState,
        targetInputVersion < caseRecord.InputVersion ? CaseProgression.Rebuilding : CaseProgression.Ready,
        caseRecord.OpenedAt,
        Now,
        caseRecord.Version,
        $"Projected input version {targetInputVersion}.",
        new AiSynthesis("pending", null, [], [], [], null),
        [],
        [],
        [],
        [])
    {
        Origin = caseRecord.Origin,
        InputVersion = caseRecord.InputVersion,
        ProjectedInputVersion = targetInputVersion,
        CreatedBy = caseRecord.CreatedBy
    };

    private static CaseFile CaseFileWithCrumbs(
        CaseRecord caseRecord,
        long targetInputVersion) => BuildCaseFile(caseRecord, targetInputVersion) with
        {
            Crumbs =
        [
            new Crumb(
                "submitted-crumb",
                "submitted",
                Now,
                null,
                "deployment",
                "warning",
                "Deployment crumb",
                null,
                null,
                0.55,
                new JsonObject())
        ]
        };

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        var value = await command.ExecuteScalarAsync(CancellationToken.None);
        return (T)Convert.ChangeType(value!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
