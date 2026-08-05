using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Panko.Api.Domain;
using Panko.Api.Patterns;
using Panko.Api.Signatures;
using Panko.Api.Infrastructure;
using Panko.Api.Cases;
using Panko.Api.Options;
using Panko.Api.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Panko.Api.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PostgresPatternCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres-signatures";
}

[Collection(PostgresPatternCollection.Name)]
public sealed class PostgresPatternTests(PostgresFixture database) : IAsyncLifetime
{
    private readonly IOptions<PankoOptions> _options = Microsoft.Extensions.Options.Options.Create(
        new PankoOptions
        {
            SignatureAutomaticThreshold = 80,
            SignaturePossibleThreshold = 60,
            SignatureMaximumCandidates = 100,
            SignatureCandidateLookbackDays = 365,
            SignatureEscalationCount = 3,
            SignatureEscalationWindowDays = 7
        });

    public Task InitializeAsync() => database.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task DatabaseInitializerRecordsTheCurrentSchemaVersion()
    {
        await using var command = database.DataSource.CreateCommand(
            "select max(version) from schema_migrations");

        var version = await command.ExecuteScalarAsync(CancellationToken.None);

        Assert.Equal(1, Convert.ToInt32(version, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task PagerDutyWebhookIsIdempotentAndSchedulesBoundedRefreshes()
    {
        var repository = new PostgresCaseStore(database.DataSource, TimeProvider.System);
        var webhook = Webhook("evt-triggered", "incident.triggered");
        var recipe = BuildRecipe();
        var payload = Encoding.UTF8.GetBytes("{\"event\":\"triggered\"}");

        var first = await repository.AcceptWebhookAsync(webhook, recipe, payload, CancellationToken.None);
        var duplicate = await repository.AcceptWebhookAsync(webhook, recipe, payload, CancellationToken.None);

        Assert.False(first.IsDuplicate);
        Assert.True(duplicate.IsDuplicate);
        Assert.Equal(first.CaseId, duplicate.CaseId);
        Assert.NotEqual(Guid.Empty, first.CaseId);
        Assert.Equal(1, await ScalarAsync<int>("select count(*) from case_origin_receipts"));
        Assert.Equal(1, await ScalarAsync<int>("select count(*) from cases"));
        Assert.Equal(3, await ScalarAsync<int>("select count(*) from work_items"));
    }

    [Fact]
    public async Task ProgressIsRevisionGuardedAndNeverRewritesCanonicalCrumbs()
    {
        var repository = new PostgresCaseStore(database.DataSource, TimeProvider.System);
        var accepted = await repository.AcceptWebhookAsync(
            Webhook("evt-progress", "incident.triggered"),
            BuildRecipe(),
            Encoding.UTF8.GetBytes("{\"event\":\"progress\"}"),
            CancellationToken.None);
        await repository.SetStatusAsync(
            accepted.CaseId,
            CaseProgression.Collecting,
            CancellationToken.None);
        var caseRecord = await repository.GetCaseAsync(accepted.CaseId, CancellationToken.None);
        Assert.NotNull(caseRecord);

        var caseFile = new CaseFile(
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
            DateTimeOffset.UtcNow,
            caseRecord.Version,
            "Deterministic Case File ready.",
            new AiSynthesis("complete", "done", [], [], [], "hash"),
            [],
            [],
            [],
            []);

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveCaseFileAsync(
            caseRecord,
            caseFile,
            Guid.NewGuid(),
            CancellationToken.None));
        Assert.Null(await repository.GetProgressAsync(caseRecord.Id, CancellationToken.None));
        Assert.Equal(0, await ScalarAsync<int>("select count(*) from cases where case_file_json is not null"));

        var attemptId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;
        var initial = new CaseProgress(
            caseRecord.Id,
            attemptId,
            0,
            caseRecord.Version,
            startedAt,
            startedAt,
            0,
            CaseProgressPhase.Collecting,
            1,
            30,
            false,
            false,
            AiSynthesisProgressState.Pending,
            [],
            []);

        Assert.Equal(1L, await repository.BeginProgressAsync(initial, CancellationToken.None));
        var current = initial with { Revision = 1, CurrentLookbackMinutes = 60 };
        Assert.Equal(2L, await repository.UpdateProgressAsync(current, CancellationToken.None));
        Assert.Null(await repository.UpdateProgressAsync(current, CancellationToken.None));

        var stored = await repository.GetProgressAsync(caseRecord.Id, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(2, stored.Revision);
        Assert.Equal(60, stored.CurrentLookbackMinutes);
        Assert.Equal(0, await ScalarAsync<int>("select count(*) from crumbs"));
        Assert.Equal(0, await ScalarAsync<int>("select count(*) from trail_entries"));
        Assert.Equal(0, await ScalarAsync<int>("select count(*) from outbox"));
        Assert.Equal(0, await ScalarAsync<int>("select count(*) from cases where case_file_json is not null"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveCaseFileAsync(
            caseRecord,
            caseFile,
            Guid.NewGuid(),
            CancellationToken.None));
        Assert.NotNull(await repository.GetProgressAsync(caseRecord.Id, CancellationToken.None));
        Assert.Equal(0, await ScalarAsync<int>("select count(*) from cases where case_file_json is not null"));

        var replacementAttemptId = Guid.NewGuid();
        var replacement = current with
        {
            AttemptId = replacementAttemptId,
            Revision = 0,
            StartedAt = startedAt.AddSeconds(1),
            UpdatedAt = startedAt.AddSeconds(1)
        };
        Assert.Equal(1L, await repository.BeginProgressAsync(replacement, CancellationToken.None));
        stored = await repository.GetProgressAsync(caseRecord.Id, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(replacementAttemptId, stored.AttemptId);
        Assert.Equal(1, stored.Revision);

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveCaseFileAsync(
            caseRecord,
            caseFile,
            attemptId,
            CancellationToken.None));
        stored = await repository.GetProgressAsync(caseRecord.Id, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(replacementAttemptId, stored.AttemptId);

        var duplicateCrumb = new Crumb(
            "duplicate",
            "test",
            caseRecord.OpenedAt,
            null,
            "signal",
            "warning",
            "Duplicate Crumb used to force the canonical save to roll back.",
            null,
            null,
            0.8,
            new JsonObject());
        var invalidCaseFile = caseFile with { Crumbs = [duplicateCrumb, duplicateCrumb] };
        var persistenceFailure = await Assert.ThrowsAsync<PostgresException>(() => repository.SaveCaseFileAsync(
            caseRecord,
            invalidCaseFile,
            replacementAttemptId,
            CancellationToken.None));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, persistenceFailure.SqlState);
        stored = await repository.GetProgressAsync(caseRecord.Id, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(replacementAttemptId, stored.AttemptId);
        Assert.Equal(caseRecord.Version, (await repository.GetCaseAsync(
            caseRecord.Id,
            CancellationToken.None))!.Version);
        Assert.Equal(0, await ScalarAsync<int>("select count(*) from crumbs"));
        Assert.Equal(0, await ScalarAsync<int>("select count(*) from cases where case_file_json is not null"));

        await repository.SaveCaseFileAsync(caseRecord, caseFile, replacementAttemptId, CancellationToken.None);

        Assert.Null(await repository.GetProgressAsync(caseRecord.Id, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveCaseFileAsync(
            caseRecord,
            caseFile,
            replacementAttemptId,
            CancellationToken.None));
        Assert.Null(await repository.UpdateProgressAsync(
            current with { Revision = 2 },
            CancellationToken.None));
        Assert.Null(await repository.UpdateProgressAsync(
            replacement with { Revision = 1 },
            CancellationToken.None));
    }

    [Fact]
    public async Task PagerDutyLifecycleTimestampsFollowTheCurrentIncidentCycle()
    {
        var repository = new PostgresCaseStore(database.DataSource, TimeProvider.System);
        var triggeredAt = DateTimeOffset.Parse("2026-07-13T08:00:00Z");
        var acknowledgedAt = triggeredAt.AddMinutes(5);
        var resolvedAt = triggeredAt.AddMinutes(25);
        var reopenedAt = triggeredAt.AddMinutes(40);

        var accepted = await repository.AcceptWebhookAsync(
            Webhook("evt-lifecycle-triggered", "incident.triggered", triggeredAt, triggeredAt),
            BuildRecipe(),
            Encoding.UTF8.GetBytes("{\"event\":\"triggered\"}"),
            CancellationToken.None);
        var triggered = await repository.GetCaseAsync(accepted.CaseId, CancellationToken.None);
        Assert.NotNull(triggered);
        Assert.Equal(triggeredAt, triggered.OpenedAt);
        Assert.Null(triggered.AcknowledgedAt);
        Assert.Null(triggered.ResolvedAt);

        await repository.AcceptWebhookAsync(
            Webhook("evt-lifecycle-acknowledged", "incident.acknowledged", triggeredAt, acknowledgedAt),
            BuildRecipe(),
            Encoding.UTF8.GetBytes("{\"event\":\"acknowledged\"}"),
            CancellationToken.None);
        var acknowledged = await repository.GetCaseAsync(accepted.CaseId, CancellationToken.None);
        Assert.NotNull(acknowledged);
        Assert.Equal(PagerDutyIncidentState.Acknowledged, acknowledged.PagerDutyState);
        Assert.Equal(acknowledgedAt, acknowledged.AcknowledgedAt);
        Assert.Null(acknowledged.ResolvedAt);

        await repository.AcceptWebhookAsync(
            Webhook("evt-lifecycle-resolved", "incident.resolved", triggeredAt, resolvedAt),
            BuildRecipe(),
            Encoding.UTF8.GetBytes("{\"event\":\"resolved\"}"),
            CancellationToken.None);
        var resolved = await repository.GetCaseAsync(accepted.CaseId, CancellationToken.None);
        Assert.NotNull(resolved);
        Assert.Equal(PagerDutyIncidentState.Resolved, resolved.PagerDutyState);
        Assert.Equal(acknowledgedAt, resolved.AcknowledgedAt);
        Assert.Equal(resolvedAt, resolved.ResolvedAt);

        await repository.AcceptWebhookAsync(
            Webhook("evt-lifecycle-reopened", "incident.reopened", triggeredAt, reopenedAt),
            BuildRecipe(),
            Encoding.UTF8.GetBytes("{\"event\":\"reopened\"}"),
            CancellationToken.None);
        var reopened = await repository.GetCaseAsync(accepted.CaseId, CancellationToken.None);
        Assert.NotNull(reopened);
        Assert.Equal(PagerDutyIncidentState.Triggered, reopened.PagerDutyState);
        Assert.Null(reopened.AcknowledgedAt);
        Assert.Null(reopened.ResolvedAt);
    }

    [Fact]
    public async Task DuplicatePagerDutyPullRepairsOnlyItsMissingLifecycleTimestamp()
    {
        var repository = new PostgresCaseStore(database.DataSource, TimeProvider.System);
        var triggeredAt = DateTimeOffset.Parse("2026-07-13T08:00:00Z");
        var resolvedAt = triggeredAt.AddMinutes(25);
        var conservativeWatermark = resolvedAt.AddDays(2);
        var webhook = Webhook(
            "pagerduty-pull:v2:stable-resolved",
            "incident.resolved",
            triggeredAt,
            resolvedAt);
        var payload = Encoding.UTF8.GetBytes("{\"source\":\"pagerduty-pull\",\"status\":\"resolved\"}");

        var accepted = await repository.AcceptWebhookAsync(
            webhook,
            BuildRecipe(),
            payload,
            CancellationToken.None);
        await using (var clearProjection = database.DataSource.CreateCommand("""
            update cases
            set resolved_at = null, pagerduty_lifecycle_updated_at = $2
            where id = $1
            """))
        {
            clearProjection.Parameters.AddWithValue(accepted.CaseId);
            clearProjection.Parameters.AddWithValue(conservativeWatermark);
            await clearProjection.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.AcceptWebhookAsync(
            webhook,
            BuildRecipe(),
            Encoding.UTF8.GetBytes("{\"source\":\"pagerduty-pull\",\"status\":\"resolved\",\"changed\":true}"),
            CancellationToken.None));
        var unrepaired = await repository.GetCaseAsync(accepted.CaseId, CancellationToken.None);
        Assert.NotNull(unrepaired);
        Assert.Null(unrepaired.ResolvedAt);

        var duplicate = await repository.AcceptWebhookAsync(
            webhook,
            BuildRecipe(),
            payload,
            CancellationToken.None);
        var repaired = await repository.GetCaseAsync(accepted.CaseId, CancellationToken.None);

        Assert.True(duplicate.IsDuplicate);
        Assert.NotNull(repaired);
        Assert.Equal(PagerDutyIncidentState.Resolved, repaired.PagerDutyState);
        Assert.Null(repaired.AcknowledgedAt);
        Assert.Equal(resolvedAt, repaired.ResolvedAt);
        await using var watermark = database.DataSource.CreateCommand("""
            select pagerduty_lifecycle_updated_at
            from cases
            where id = $1
            """);
        watermark.Parameters.AddWithValue(accepted.CaseId);
        await using var reader = await watermark.ExecuteReaderAsync(CancellationToken.None);
        Assert.True(await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(conservativeWatermark, reader.GetFieldValue<DateTimeOffset>(0));
    }

    [Fact]
    public async Task DelayedPagerDutyLifecycleEventsDoNotRegressTheCurrentCycle()
    {
        var repository = new PostgresCaseStore(database.DataSource, TimeProvider.System);
        var triggeredAt = DateTimeOffset.Parse("2026-07-13T08:00:00Z");
        var acknowledgedAt = triggeredAt.AddMinutes(5);
        var resolvedAt = triggeredAt.AddMinutes(25);
        var reopenedAt = triggeredAt.AddMinutes(40);

        var accepted = await repository.AcceptWebhookAsync(
            Webhook("evt-ordered-triggered", "incident.triggered", triggeredAt, triggeredAt),
            BuildRecipe(),
            Encoding.UTF8.GetBytes("{\"event\":\"triggered\"}"),
            CancellationToken.None);
        await repository.AcceptWebhookAsync(
            Webhook("evt-ordered-acknowledged", "incident.acknowledged", triggeredAt, acknowledgedAt),
            BuildRecipe(),
            Encoding.UTF8.GetBytes("{\"event\":\"acknowledged\"}"),
            CancellationToken.None);
        await repository.AcceptWebhookAsync(
            Webhook("evt-ordered-resolved", "incident.resolved", triggeredAt, resolvedAt),
            BuildRecipe(),
            Encoding.UTF8.GetBytes("{\"event\":\"resolved\"}"),
            CancellationToken.None);

        await repository.AcceptWebhookAsync(
            Webhook(
                "evt-delayed-reopened-before-resolution",
                "incident.reopened",
                triggeredAt,
                resolvedAt.AddMinutes(-1)),
            BuildRecipe(),
            Encoding.UTF8.GetBytes("{\"event\":\"delayed-reopened\"}"),
            CancellationToken.None);
        var stillResolved = await repository.GetCaseAsync(accepted.CaseId, CancellationToken.None);
        Assert.NotNull(stillResolved);
        Assert.Equal(PagerDutyIncidentState.Resolved, stillResolved.PagerDutyState);
        Assert.True(stillResolved.IsFrozen);
        Assert.Equal(acknowledgedAt, stillResolved.AcknowledgedAt);
        Assert.Equal(resolvedAt, stillResolved.ResolvedAt);

        await repository.AcceptWebhookAsync(
            Webhook("evt-ordered-reopened", "incident.reopened", triggeredAt, reopenedAt),
            BuildRecipe(),
            Encoding.UTF8.GetBytes("{\"event\":\"reopened\"}"),
            CancellationToken.None);
        await repository.AcceptWebhookAsync(
            Webhook(
                "evt-delayed-resolved-before-reopen",
                "incident.resolved",
                triggeredAt,
                reopenedAt.AddMinutes(-1)),
            BuildRecipe(),
            Encoding.UTF8.GetBytes("{\"event\":\"delayed-resolved\"}"),
            CancellationToken.None);

        var stillReopened = await repository.GetCaseAsync(accepted.CaseId, CancellationToken.None);
        Assert.NotNull(stillReopened);
        Assert.Equal(PagerDutyIncidentState.Triggered, stillReopened.PagerDutyState);
        Assert.False(stillReopened.IsFrozen);
        Assert.Null(stillReopened.AcknowledgedAt);
        Assert.Null(stillReopened.ResolvedAt);
    }

    [Fact]
    public async Task SlackRebuildRetiresScheduledWorkAndQueuesOneFreshRun()
    {
        var repository = new PostgresCaseStore(database.DataSource, TimeProvider.System);
        var accepted = await repository.AcceptWebhookAsync(
            Webhook("evt-rebuild", "incident.triggered"),
            BuildRecipe(),
            Encoding.UTF8.GetBytes("{\"event\":\"rebuild\"}"),
            CancellationToken.None);
        await repository.SetStatusAsync(accepted.CaseId, "ready", CancellationToken.None);
        await repository.SetSlackTimestampAsync(accepted.CaseId, "171234.5678", CancellationToken.None);

        var rebuilt = await repository.RebuildCaseAsync(
            accepted.CaseId, "#cases", "171234.5678", CancellationToken.None);

        Assert.True(rebuilt);
        Assert.Equal("queued", await ScalarAsync<string>(
            $"select status from cases where id = '{accepted.CaseId}'"));
        Assert.Equal(1, await ScalarAsync<int>(
            "select count(*) from work_items where completed_at is null"));
        Assert.Equal(1, await ScalarAsync<int>(
            "select count(*) from outbox where processed_at is null"));
    }

    [Fact]
    public async Task RepeatedRunsUpdateOneOccurrence()
    {
        var repository = Repository();
        var signature = Signature("checkout timeout");
        var caseRecord = BuildCase("PD-IDEMPOTENT", PagerDutyIncidentState.Triggered);

        var first = await repository.MatchOrCreateAsync(caseRecord, signature, CancellationToken.None);
        var repeated = await repository.MatchOrCreateAsync(caseRecord, signature, CancellationToken.None);

        Assert.Equal(first.PatternId, repeated.PatternId);
        Assert.Equal(1, repeated.OccurrenceCount);
        Assert.Equal(1, await ScalarAsync<int>("select count(*) from pattern_occurrences"));
    }

    [Fact]
    public async Task ConcurrentExactMatchesCreateOnePattern()
    {
        var repository = Repository();
        var signature = Signature("provider timeout");
        var first = BuildCase("PD-CONCURRENT-1", PagerDutyIncidentState.Triggered);
        var second = BuildCase("PD-CONCURRENT-2", PagerDutyIncidentState.Triggered);

        var matches = await Task.WhenAll(
            repository.MatchOrCreateAsync(first, signature, CancellationToken.None),
            repository.MatchOrCreateAsync(second, signature, CancellationToken.None));

        Assert.Equal(matches[0].PatternId, matches[1].PatternId);
        Assert.Equal(1, await ScalarAsync<int>("select count(*) from patterns"));
        Assert.Equal(2, await ScalarAsync<int>("select count(*) from pattern_occurrences"));
    }

    [Fact]
    public async Task ConcurrentAutomaticSimilarityMatchesCreateOnePattern()
    {
        var repository = Repository();
        var first = BuildCase("PD-CONCURRENT-SIMILAR-1", PagerDutyIncidentState.Triggered);
        var second = BuildCase("PD-CONCURRENT-SIMILAR-2", PagerDutyIncidentState.Triggered);
        var firstSignature = SimilarSignature(includeAdditionalError: false);
        var secondSignature = SimilarSignature(includeAdditionalError: true);
        Assert.Equal(firstSignature.FamilyHash, secondSignature.FamilyHash);
        Assert.NotEqual(firstSignature.ExactHash, secondSignature.ExactHash);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var blockerConnection = await database.DataSource.OpenConnectionAsync(cancellation.Token);
        await using var blockerTransaction = await blockerConnection.BeginTransactionAsync(cancellation.Token);
        var blockerPid = await BackendPidAsync(blockerConnection, blockerTransaction, cancellation.Token);
        await using (var blocker = new NpgsqlCommand(
                         "select pg_advisory_xact_lock(hashtextextended($1, 0))",
                         blockerConnection,
                         blockerTransaction))
        {
            blocker.Parameters.AddWithValue(
                $"association|{first.Team}|{firstSignature.AlgorithmVersion}|{firstSignature.Features.ServiceId}|{firstSignature.Features.RecipeId}");
            await blocker.ExecuteNonQueryAsync(cancellation.Token);
        }

        var matchTasks = new[]
        {
            repository.MatchOrCreateAsync(first, firstSignature, cancellation.Token),
            repository.MatchOrCreateAsync(second, secondSignature, cancellation.Token)
        };
        try
        {
            await WaitForAdvisoryLockWaitersAsync(blockerPid, matchTasks.Length, cancellation.Token);
        }
        catch
        {
            cancellation.Cancel();
            await blockerTransaction.RollbackAsync(CancellationToken.None);
            try
            {
                await Task.WhenAll(matchTasks);
            }
            catch
            {
                // Preserve the lock-synchronization failure that made the regression test invalid.
            }
            throw;
        }

        await blockerTransaction.CommitAsync(cancellation.Token);
        var matches = await Task.WhenAll(matchTasks);

        Assert.Contains(matches, match => match.MatchType == "new");
        Assert.Contains(matches, match => match.MatchType == "family" && match.Score >= 80);
        Assert.Equal(matches[0].PatternId, matches[1].PatternId);
        Assert.Equal(1, await ScalarAsync<int>("select count(*) from patterns"));
        Assert.Equal(2, await ScalarAsync<int>("select count(*) from pattern_occurrences"));
    }

    [Fact]
    public async Task ConcurrentRerunsOfOneCaseKeepOneStableAssignment()
    {
        var repository = Repository();
        var caseRecord = BuildCase("PD-CONCURRENT-RERUN", PagerDutyIncidentState.Triggered);

        var matches = await Task.WhenAll(
            repository.MatchOrCreateAsync(caseRecord, Signature("provider timeout"), CancellationToken.None),
            repository.MatchOrCreateAsync(caseRecord, Signature("database corruption"), CancellationToken.None));

        Assert.Equal(matches[0].PatternId, matches[1].PatternId);
        Assert.Equal(1, await ScalarAsync<int>("select count(*) from patterns"));
        Assert.Equal(1, await ScalarAsync<int>("select count(*) from pattern_occurrences"));
    }

    [Fact]
    public async Task MateriallyDifferentSymptomsInOneServiceCreateSeparatePatterns()
    {
        var repository = Repository();

        var timeout = await repository.MatchOrCreateAsync(
            BuildCase("PD-DIFFERENT-1", PagerDutyIncidentState.Triggered), Signature("provider timeout"), CancellationToken.None);
        var corruption = await repository.MatchOrCreateAsync(
            BuildCase("PD-DIFFERENT-2", PagerDutyIncidentState.Triggered, 1), Signature("database corruption"), CancellationToken.None);

        Assert.NotEqual(timeout.PatternId, corruption.PatternId);
        Assert.Equal(2, await ScalarAsync<int>("select count(*) from patterns"));
    }

    [Fact]
    public async Task ExistingCasePatternAssignmentIsStableWhenLaterCrumbsChange()
    {
        var repository = Repository();
        var caseRecord = BuildCase("PD-STABLE-1", PagerDutyIncidentState.Triggered);
        var original = await repository.MatchOrCreateAsync(caseRecord, Signature("provider timeout"), CancellationToken.None);
        var other = await repository.MatchOrCreateAsync(
            BuildCase("PD-STABLE-2", PagerDutyIncidentState.Triggered, 1), Signature("database corruption"), CancellationToken.None);

        var rerun = await repository.MatchOrCreateAsync(caseRecord, Signature("database corruption"), CancellationToken.None);

        Assert.Equal(original.PatternId, rerun.PatternId);
        Assert.NotEqual(other.PatternId, rerun.PatternId);
        Assert.Equal(2, await ScalarAsync<int>("select count(*) from patterns"));
        Assert.Equal(2, await ScalarAsync<int>("select count(*) from pattern_occurrences"));
    }

    [Fact]
    public async Task AggregateLifecycleAndCountAreNotLimitedByRecentHistoryPayload()
    {
        var repository = Repository();
        var signature = Signature("high volume failure");
        var baseCase = BuildCase("PD-BULK-BASE", PagerDutyIncidentState.Triggered);
        var first = await repository.MatchOrCreateAsync(baseCase, signature, CancellationToken.None);
        await using (var command = database.DataSource.CreateCommand("""
            with inserted_cases as (
                insert into cases(
                    id, pagerduty_incident_id, service_id, recipe_id, team, title, urgency, pagerduty_state,
                    status, opened_at, updated_at, slack_channel, origin_kind)
                select gen_random_uuid(), 'PD-BULK-' || value, 'payments', 'payments-production',
                       'payments', 'Bulk Case ' || value, 'high', 'Triggered', 'ready',
                       now() - make_interval(mins => value), now() - make_interval(mins => value),
                       '#cases', 'pagerduty'
                from generate_series(1, 55) value
                returning id, pagerduty_incident_id, pagerduty_state, opened_at
            )
            insert into pattern_occurrences(pattern_id, case_id, algorithm_version, pagerduty_incident_id,
                pagerduty_state, match_type, similarity_score, matched_features, occurred_at, active)
            select $1, id, 'v1', pagerduty_incident_id, pagerduty_state, 'exact', 100, '[]'::jsonb,
                   opened_at, true
            from inserted_cases
            """))
        {
            command.Parameters.AddWithValue(first.PatternId);
            await command.ExecuteNonQueryAsync();
        }

        var updated = await repository.MatchOrCreateAsync(
            baseCase with { PagerDutyState = PagerDutyIncidentState.Resolved }, signature, CancellationToken.None);

        Assert.Equal(56, updated.OccurrenceCount);
        Assert.NotEqual(PatternLifecycleState.Resolved, updated.LifecycleState);
        Assert.True(updated.RecentOccurrences.Count <= 10);
    }

    [Fact]
    public async Task LifecycleResolvesRegressesAndRemainsActiveWhileAnyOccurrenceIsActive()
    {
        var repository = Repository();
        var signature = Signature("gateway unavailable");
        var first = BuildCase("PD-LIFE-1", PagerDutyIncidentState.Triggered);
        var second = BuildCase("PD-LIFE-2", PagerDutyIncidentState.Triggered, minutesLater: 1);

        await repository.MatchOrCreateAsync(first, signature, CancellationToken.None);
        var withTwoActive = await repository.MatchOrCreateAsync(second, signature, CancellationToken.None);
        var oneResolved = await repository.MatchOrCreateAsync(first with { PagerDutyState = PagerDutyIncidentState.Resolved }, signature, CancellationToken.None);

        Assert.Equal(PatternLifecycleState.Ongoing, withTwoActive.LifecycleState);
        Assert.Equal(PatternLifecycleState.Ongoing, oneResolved.LifecycleState);

        var fullyResolved = await repository.MatchOrCreateAsync(second with { PagerDutyState = PagerDutyIncidentState.Resolved }, signature, CancellationToken.None);
        Assert.Equal(PatternLifecycleState.Resolved, fullyResolved.LifecycleState);

        var recurrence = await repository.MatchOrCreateAsync(
            BuildCase("PD-LIFE-3", PagerDutyIncidentState.Triggered, minutesLater: 2), signature, CancellationToken.None);
        Assert.Equal(PatternLifecycleState.Regressed, recurrence.LifecycleState);
        Assert.Equal(3, recurrence.OccurrenceCount);
    }

    [Fact]
    public async Task ThreeDistinctRecentOccurrencesEscalate()
    {
        var repository = Repository();
        var signature = Signature("ledger connection refused");

        await repository.MatchOrCreateAsync(BuildCase("PD-ESC-1", PagerDutyIncidentState.Triggered), signature, CancellationToken.None);
        await repository.MatchOrCreateAsync(BuildCase("PD-ESC-2", PagerDutyIncidentState.Triggered, 1), signature, CancellationToken.None);
        var third = await repository.MatchOrCreateAsync(
            BuildCase("PD-ESC-3", PagerDutyIncidentState.Triggered, 2), signature, CancellationToken.None);

        Assert.Equal(PatternLifecycleState.Escalating, third.LifecycleState);
        Assert.Equal(3, third.OccurrenceCount);
    }

    [Fact]
    public async Task PatternsAndHistoryAreIsolatedByPersistedTeam()
    {
        var repository = Repository();
        var signature = Signature("shared provider timeout");
        var payments = BuildCase("PD-TEAM-PAYMENTS", PagerDutyIncidentState.Triggered) with
        {
            Team = "payments"
        };
        var search = BuildCase("PD-TEAM-SEARCH", PagerDutyIncidentState.Triggered, minutesLater: 1) with
        {
            Team = "search"
        };

        var paymentsMatch = await repository.MatchOrCreateAsync(
            payments, signature, CancellationToken.None);
        var searchMatch = await repository.MatchOrCreateAsync(
            search, signature, CancellationToken.None);

        Assert.NotEqual(paymentsMatch.PatternId, searchMatch.PatternId);
        Assert.Equal(2, await ScalarAsync<int>("select count(*) from patterns"));
        Assert.Single(await repository.FindCandidatesAsync(
            "payments", signature, CancellationToken.None));
        Assert.Single(await repository.FindCandidatesAsync(
            "search", signature, CancellationToken.None));
    }

    [Fact]
    public async Task SecurityAuditSurvivesCaseRetentionAndRejectsMutation()
    {
        var caseRepository = new PostgresCaseStore(database.DataSource, TimeProvider.System);
        var accepted = await caseRepository.AcceptWebhookAsync(
            Webhook("evt-audit-retention", "incident.triggered"),
            BuildRecipe(),
            Encoding.UTF8.GetBytes("{\"event\":\"audit-retention\"}"),
            CancellationToken.None);
        var audit = new PostgresSecurityAuditTrail(database.DataSource, TimeProvider.System);
        await audit.RecordAsync(
            new SecurityAuditEvent(
                SecurityAuditActions.CaseFileAccess,
                "allowed",
                new SecurityAuditActor("subject-1", "Bearer", ["payments"]),
                "payments",
                "payments-production",
                accepted.CaseId),
            CancellationToken.None);

        await caseRepository.PurgeOlderThanAsync(
            DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);

        Assert.Equal(0, await ScalarAsync<int>("select count(*) from cases"));
        Assert.Equal(1, await ScalarAsync<int>("select count(*) from security_audit_events"));
        await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var update = database.DataSource.CreateCommand(
                "update security_audit_events set outcome = 'changed'");
            await update.ExecuteNonQueryAsync(CancellationToken.None);
        });
        await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var delete = database.DataSource.CreateCommand(
                "delete from security_audit_events");
            await delete.ExecuteNonQueryAsync(CancellationToken.None);
        });
        Assert.Equal(1, await ScalarAsync<int>("select count(*) from security_audit_events"));
    }

    [Fact]
    public async Task CompactHistorySurvivesFullCaseRetention()
    {
        var patternRepository = Repository();
        var caseRepository = new PostgresCaseStore(database.DataSource, TimeProvider.System);
        var caseRecord = BuildCase("PD-RETENTION", PagerDutyIncidentState.Resolved, triggeredAt: DateTimeOffset.UtcNow - TimeSpan.FromDays(60));
        var signature = Signature("retained failure");
        await InsertCaseAndCrumbsAsync(caseRecord);
        await patternRepository.SaveSignatureAsync(caseRecord.Id, signature, CancellationToken.None);
        await patternRepository.MatchOrCreateAsync(caseRecord, signature, CancellationToken.None);

        var deleted = await caseRepository.PurgeOlderThanAsync(
            DateTimeOffset.UtcNow - TimeSpan.FromDays(30), CancellationToken.None);

        Assert.Equal(1, deleted);
        Assert.Equal(0, await ScalarAsync<int>("select count(*) from crumbs"));
        Assert.Equal(1, await ScalarAsync<int>("select count(*) from pattern_occurrences"));
        Assert.Equal(1, await ScalarAsync<int>("select count(*) from case_signatures"));
        Assert.Equal(1, await ScalarAsync<int>("select count(*) from patterns"));
    }

    [Fact]
    public async Task CompactRetentionRecomputesPatternsAndRemovesOrphans()
    {
        var repository = Repository();
        var signature = Signature("retention pruning");
        var old = BuildCase("PD-PRUNE-OLD", PagerDutyIncidentState.Resolved, triggeredAt: DateTimeOffset.UtcNow - TimeSpan.FromDays(400));
        var recent = BuildCase("PD-PRUNE-RECENT", PagerDutyIncidentState.Triggered);
        var group = await repository.MatchOrCreateAsync(old, signature, CancellationToken.None);
        await repository.MatchOrCreateAsync(recent, signature, CancellationToken.None);
        await using (var age = database.DataSource.CreateCommand("""
            update pattern_occurrences set updated_at = now() - interval '400 days' where case_id = $1
            """))
        {
            age.Parameters.AddWithValue(old.Id);
            await age.ExecuteNonQueryAsync();
        }
        await using (var regress = database.DataSource.CreateCommand(
                         "update patterns set lifecycle_state = 'regressed' where id = $1"))
        {
            regress.Parameters.AddWithValue(group.PatternId);
            await regress.ExecuteNonQueryAsync();
        }

        await repository.PurgeAsync(DateTimeOffset.UtcNow - TimeSpan.FromDays(365), CancellationToken.None);

        Assert.Equal(1, await ScalarAsync<int>("select occurrence_count from patterns where id = '" + group.PatternId + "'"));
        Assert.Equal("new", await ScalarAsync<string>("select lifecycle_state from patterns where id = '" + group.PatternId + "'"));
        Assert.Equal(1, await ScalarAsync<int>("select count(*) from pattern_occurrences"));

        await using (var ageRemaining = database.DataSource.CreateCommand(
            "update pattern_occurrences set updated_at = now() - interval '400 days'"))
        {
            await ageRemaining.ExecuteNonQueryAsync();
        }
        await repository.PurgeAsync(DateTimeOffset.UtcNow - TimeSpan.FromDays(365), CancellationToken.None);

        Assert.Equal(0, await ScalarAsync<int>("select count(*) from patterns"));
        Assert.Equal(0, await ScalarAsync<int>("select count(*) from pattern_occurrences"));
    }

    private PatternRepository Repository()
    {
        var policy = new PatternPolicy(_options);
        return new PatternRepository(database.DataSource, policy, TimeProvider.System, NullLogger<PatternRepository>.Instance);
    }

    private static async Task<int> BackendPidAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("select pg_backend_pid()", connection, transaction);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task WaitForAdvisoryLockWaitersAsync(
        int blockerPid,
        int expectedWaiters,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10))
        {
            await using var command = database.DataSource.CreateCommand("""
                select count(*)::integer
                from pg_locks blocker
                join pg_locks waiter
                  on waiter.locktype = blocker.locktype
                 and waiter.database = blocker.database
                 and waiter.classid = blocker.classid
                 and waiter.objid = blocker.objid
                 and waiter.objsubid = blocker.objsubid
                where blocker.pid = $1
                  and blocker.locktype = 'advisory'
                  and blocker.granted
                  and not waiter.granted
                """);
            command.Parameters.AddWithValue(blockerPid);
            if (Convert.ToInt32(
                    await command.ExecuteScalarAsync(cancellationToken),
                    System.Globalization.CultureInfo.InvariantCulture) >= expectedWaiters)
            {
                return;
            }

            await Task.Delay(25, cancellationToken);
        }

        throw new TimeoutException(
            $"Expected {expectedWaiters} recurrence transactions to wait on the association advisory lock.");
    }

    private static CaseSignature Signature(string title)
    {
        var features = new SignatureFeatures(
            "payments", "payments-production", ["production"], title,
            title.Split(' ', StringSplitOptions.RemoveEmptyEntries), ["error"], [title], ["checkout"], ["payments:src/provider.cs"]);
        return new SignatureGenerator().Generate(features, SignatureStage.Final);
    }

    private static CaseSignature SimilarSignature(bool includeAdditionalError)
    {
        var errors = includeAdditionalError
            ? new[] { "shared timeout", "downstream retry exhausted" }
            : ["shared timeout"];
        var features = new SignatureFeatures(
            "payments",
            "payments-production",
            ["production"],
            "checkout provider timeout",
            ["checkout", "provider", "timeout"],
            ["error"],
            errors,
            ["checkout"],
            ["payments:src/provider.cs"]);
        return new SignatureGenerator().Generate(features, SignatureStage.Final);
    }

    private static CaseRecord BuildCase(
        string pagerDutyId,
        PagerDutyIncidentState state,
        int minutesLater = 0,
        DateTimeOffset? triggeredAt = null)
    {
        var occurredAt = (triggeredAt ?? DateTimeOffset.UtcNow).AddMinutes(minutesLater);
        return new CaseRecord(
            Guid.NewGuid(), pagerDutyId, "payments", "payments-production", "failure", "high", state,
            occurredAt, occurredAt, 0, "queued", state == PagerDutyIncidentState.Resolved, null, "#cases", null,
            new Dictionary<string, string> { ["environment"] = "production", ["component"] = "checkout" })
        {
            Team = "payments"
        };
    }

    private static PagerDutyWebhookEvent Webhook(
        string eventId,
        string eventType,
        DateTimeOffset? triggeredAt = null,
        DateTimeOffset? occurredAt = null)
    {
        var incidentTriggeredAt = triggeredAt ?? DateTimeOffset.UtcNow;
        return new PagerDutyWebhookEvent(
            eventId,
            eventType,
            "PD-WEBHOOK-1",
            "P123",
            "Payments failing",
            "high",
            "https://pagerduty.example/incidents/PD-WEBHOOK-1",
            incidentTriggeredAt,
            occurredAt ?? incidentTriggeredAt,
            new Dictionary<string, string> { ["environment"] = "production" });
    }

    private static Recipe BuildRecipe() => new()
    {
        Id = "payments-production",
        PagerDutyServiceId = "P123",
        Team = "payments",
        SlackChannel = "#cases"
    };

    private async Task InsertCaseAndCrumbsAsync(CaseRecord caseRecord)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(CancellationToken.None);
        await using var command = new NpgsqlCommand("""
            insert into cases(id, pagerduty_incident_id, service_id, recipe_id, team, title, urgency, pagerduty_state, status,
                opened_at, updated_at, slack_channel, labels_json, is_frozen, origin_kind)
            values ($1, $2, $3, $4, $5, $6, $7, $8, 'resolved', $9, $10, '#cases', '{}'::jsonb, true,
                'pagerduty')
            """, connection);
        command.Parameters.AddWithValue(caseRecord.Id);
        command.Parameters.AddWithValue(
            NpgsqlDbType.Text,
            (object?)caseRecord.PagerDutyIncidentId ?? DBNull.Value);
        command.Parameters.AddWithValue(caseRecord.ServiceId);
        command.Parameters.AddWithValue(caseRecord.RecipeId);
        command.Parameters.AddWithValue(caseRecord.Team);
        command.Parameters.AddWithValue(caseRecord.Title);
        command.Parameters.AddWithValue(caseRecord.Urgency);
        command.Parameters.AddWithValue(caseRecord.PagerDutyState.ToString());
        command.Parameters.AddWithValue(caseRecord.OpenedAt);
        command.Parameters.AddWithValue(caseRecord.UpdatedAt);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
        await using var crumbCommand = new NpgsqlCommand("""
            insert into crumbs(case_id, case_file_version, crumb_id, source, occurred_at, payload)
            values ($1, 1, 'retained-crumb', 'logs', $2, '{}'::jsonb)
            """, connection);
        crumbCommand.Parameters.AddWithValue(caseRecord.Id);
        crumbCommand.Parameters.AddWithValue(caseRecord.OpenedAt);
        await crumbCommand.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        var value = await command.ExecuteScalarAsync(CancellationToken.None);
        return (T)Convert.ChangeType(value!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }
}

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly string _containerName = $"panko-signature-tests-{Guid.NewGuid():N}";
    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await DockerAsync(
            "run", "--detach", "--rm", "--name", _containerName,
            "--env", "POSTGRES_DB=panko_tests",
            "--env", "POSTGRES_USER=panko",
            "--env", "POSTGRES_PASSWORD=panko",
            "--publish", "127.0.0.1::5432", "postgres:17-alpine");
        var portOutput = await DockerAsync("port", _containerName, "5432/tcp");
        var port = int.Parse(portOutput.Trim().Split(':').Last());
        DataSource = NpgsqlDataSource.Create(
            $"Host=127.0.0.1;Port={port};Database=panko_tests;Username=panko;Password=panko;Timeout=2");

        Exception? lastError = null;
        for (var attempt = 0; attempt < 120; attempt++)
        {
            try
            {
                await using var connection = await DataSource.OpenConnectionAsync();
                lastError = null;
                break;
            }
            catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
            {
                lastError = exception;
                await Task.Delay(250);
            }
        }
        if (lastError is not null) throw new InvalidOperationException("PostgreSQL test container did not become ready.", lastError);

        var initializer = new DatabaseInitializer(DataSource, NullLogger<DatabaseInitializer>.Instance);
        await initializer.StartAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        if (DataSource is not null) await DataSource.DisposeAsync();
        try
        {
            await DockerAsync("stop", "--time", "1", _containerName);
        }
        catch
        {
            // Best-effort cleanup; --rm removes the container after a successful stop.
        }
    }

    public async Task ResetAsync()
    {
        await using var command = DataSource.CreateCommand("""
            truncate table security_audit_events, case_progress, case_command_receipts, case_create_receipts,
                crumb_source_snapshots, case_inputs, pattern_occurrences, case_signatures, patterns,
                outbox, trail_entries, crumbs, work_items, case_origin_receipts, cases restart identity cascade
            """);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> DockerAsync(params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "docker",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException($"Docker command failed: {error.Trim()}");
        return output;
    }
}
