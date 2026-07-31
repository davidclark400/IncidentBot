using System.Diagnostics;
using System.Text;
using System.Text.Json;
using IncidentBot.Api.Domain;
using IncidentBot.Api.Fingerprinting;
using IncidentBot.Api.Infrastructure;
using IncidentBot.Api.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace IncidentBot.Api.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PostgresFingerprintingCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres-fingerprinting";
}

[Collection(PostgresFingerprintingCollection.Name)]
public sealed class PostgresFingerprintingTests(PostgresFixture database) : IAsyncLifetime
{
    private readonly IOptions<IncidentBotOptions> _options = Microsoft.Extensions.Options.Options.Create(
        new IncidentBotOptions
        {
            FingerprintAutomaticThreshold = 80,
            FingerprintPossibleThreshold = 60,
            FingerprintMaximumCandidates = 100,
            FingerprintCandidateLookbackDays = 365,
            FingerprintEscalationCount = 3,
            FingerprintEscalationWindowDays = 7
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
        var repository = new IncidentRepository(database.DataSource, TimeProvider.System);
        var webhook = Webhook("evt-triggered", "incident.triggered");
        var profile = Profile();
        var payload = Encoding.UTF8.GetBytes("{\"event\":\"triggered\"}");

        var first = await repository.AcceptWebhookAsync(webhook, profile, payload, CancellationToken.None);
        var duplicate = await repository.AcceptWebhookAsync(webhook, profile, payload, CancellationToken.None);

        Assert.False(first.IsDuplicate);
        Assert.True(duplicate.IsDuplicate);
        Assert.Equal(first.IncidentId, duplicate.IncidentId);
        Assert.NotEqual(Guid.Empty, first.IncidentId);
        Assert.Equal(1, await ScalarAsync<int>("select count(*) from webhook_receipts"));
        Assert.Equal(1, await ScalarAsync<int>("select count(*) from incidents"));
        Assert.Equal(3, await ScalarAsync<int>("select count(*) from work_items"));
    }

    [Fact]
    public async Task SlackRestartRetiresScheduledWorkAndQueuesOneFreshRun()
    {
        var repository = new IncidentRepository(database.DataSource, TimeProvider.System);
        var accepted = await repository.AcceptWebhookAsync(
            Webhook("evt-restart", "incident.triggered"),
            Profile(),
            Encoding.UTF8.GetBytes("{\"event\":\"restart\"}"),
            CancellationToken.None);
        await repository.SetStatusAsync(accepted.IncidentId, "ready", CancellationToken.None);
        await repository.SetSlackTimestampAsync(accepted.IncidentId, "171234.5678", CancellationToken.None);

        var restarted = await repository.RestartInvestigationAsync(
            accepted.IncidentId, "#incidents", "171234.5678", CancellationToken.None);

        Assert.True(restarted);
        Assert.Equal("queued", await ScalarAsync<string>(
            $"select status from incidents where id = '{accepted.IncidentId}'"));
        Assert.Equal(1, await ScalarAsync<int>(
            "select count(*) from work_items where completed_at is null"));
        Assert.Equal(1, await ScalarAsync<int>(
            "select count(*) from outbox where processed_at is null"));
    }

    [Fact]
    public async Task RepeatedRunsUpdateOneOccurrence()
    {
        var repository = Repository();
        var fingerprint = Fingerprint("checkout timeout");
        var incident = Incident("PD-IDEMPOTENT", IncidentState.Triggered);

        var first = await repository.MatchOrCreateAsync(incident, fingerprint, CancellationToken.None);
        var repeated = await repository.MatchOrCreateAsync(incident, fingerprint, CancellationToken.None);

        Assert.Equal(first.ProblemGroupId, repeated.ProblemGroupId);
        Assert.Equal(1, repeated.OccurrenceCount);
        Assert.Equal(1, await ScalarAsync<int>("select count(*) from problem_occurrences"));
    }

    [Fact]
    public async Task ConcurrentExactMatchesCreateOneProblemGroup()
    {
        var repository = Repository();
        var fingerprint = Fingerprint("provider timeout");
        var first = Incident("PD-CONCURRENT-1", IncidentState.Triggered);
        var second = Incident("PD-CONCURRENT-2", IncidentState.Triggered);

        var matches = await Task.WhenAll(
            repository.MatchOrCreateAsync(first, fingerprint, CancellationToken.None),
            repository.MatchOrCreateAsync(second, fingerprint, CancellationToken.None));

        Assert.Equal(matches[0].ProblemGroupId, matches[1].ProblemGroupId);
        Assert.Equal(1, await ScalarAsync<int>("select count(*) from problem_groups"));
        Assert.Equal(2, await ScalarAsync<int>("select count(*) from problem_occurrences"));
    }

    [Fact]
    public async Task ConcurrentAutomaticSimilarityMatchesCreateOneProblemGroup()
    {
        var repository = Repository();
        var first = Incident("PD-CONCURRENT-SIMILAR-1", IncidentState.Triggered);
        var second = Incident("PD-CONCURRENT-SIMILAR-2", IncidentState.Triggered);
        var firstFingerprint = SimilarFingerprint(includeAdditionalError: false);
        var secondFingerprint = SimilarFingerprint(includeAdditionalError: true);
        Assert.Equal(firstFingerprint.FamilyHash, secondFingerprint.FamilyHash);
        Assert.NotEqual(firstFingerprint.ExactHash, secondFingerprint.ExactHash);

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
                $"association|{firstFingerprint.AlgorithmVersion}|{firstFingerprint.Features.ServiceId}|{firstFingerprint.Features.ProfileId}");
            await blocker.ExecuteNonQueryAsync(cancellation.Token);
        }

        var matchTasks = new[]
        {
            repository.MatchOrCreateAsync(first, firstFingerprint, cancellation.Token),
            repository.MatchOrCreateAsync(second, secondFingerprint, cancellation.Token)
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
        Assert.Equal(matches[0].ProblemGroupId, matches[1].ProblemGroupId);
        Assert.Equal(1, await ScalarAsync<int>("select count(*) from problem_groups"));
        Assert.Equal(2, await ScalarAsync<int>("select count(*) from problem_occurrences"));
    }

    [Fact]
    public async Task ConcurrentRerunsOfOneIncidentKeepOneStableAssignment()
    {
        var repository = Repository();
        var incident = Incident("PD-CONCURRENT-RERUN", IncidentState.Triggered);

        var matches = await Task.WhenAll(
            repository.MatchOrCreateAsync(incident, Fingerprint("provider timeout"), CancellationToken.None),
            repository.MatchOrCreateAsync(incident, Fingerprint("database corruption"), CancellationToken.None));

        Assert.Equal(matches[0].ProblemGroupId, matches[1].ProblemGroupId);
        Assert.Equal(1, await ScalarAsync<int>("select count(*) from problem_groups"));
        Assert.Equal(1, await ScalarAsync<int>("select count(*) from problem_occurrences"));
    }

    [Fact]
    public async Task MateriallyDifferentSymptomsInOneServiceCreateSeparateGroups()
    {
        var repository = Repository();

        var timeout = await repository.MatchOrCreateAsync(
            Incident("PD-DIFFERENT-1", IncidentState.Triggered), Fingerprint("provider timeout"), CancellationToken.None);
        var corruption = await repository.MatchOrCreateAsync(
            Incident("PD-DIFFERENT-2", IncidentState.Triggered, 1), Fingerprint("database corruption"), CancellationToken.None);

        Assert.NotEqual(timeout.ProblemGroupId, corruption.ProblemGroupId);
        Assert.Equal(2, await ScalarAsync<int>("select count(*) from problem_groups"));
    }

    [Fact]
    public async Task ExistingIncidentAssignmentIsStableWhenLaterEvidenceChanges()
    {
        var repository = Repository();
        var incident = Incident("PD-STABLE-1", IncidentState.Triggered);
        var original = await repository.MatchOrCreateAsync(incident, Fingerprint("provider timeout"), CancellationToken.None);
        var other = await repository.MatchOrCreateAsync(
            Incident("PD-STABLE-2", IncidentState.Triggered, 1), Fingerprint("database corruption"), CancellationToken.None);

        var rerun = await repository.MatchOrCreateAsync(incident, Fingerprint("database corruption"), CancellationToken.None);

        Assert.Equal(original.ProblemGroupId, rerun.ProblemGroupId);
        Assert.NotEqual(other.ProblemGroupId, rerun.ProblemGroupId);
        Assert.Equal(2, await ScalarAsync<int>("select count(*) from problem_groups"));
        Assert.Equal(2, await ScalarAsync<int>("select count(*) from problem_occurrences"));
    }

    [Fact]
    public async Task AggregateLifecycleAndCountAreNotLimitedByRecentHistoryPayload()
    {
        var repository = Repository();
        var fingerprint = Fingerprint("high volume failure");
        var baseIncident = Incident("PD-BULK-BASE", IncidentState.Triggered);
        var first = await repository.MatchOrCreateAsync(baseIncident, fingerprint, CancellationToken.None);
        await using (var command = database.DataSource.CreateCommand("""
            insert into problem_occurrences(problem_group_id, incident_id, algorithm_version, pagerduty_incident_id,
                incident_state, match_type, similarity_score, matched_features, occurred_at, active)
            select $1, gen_random_uuid(), 'v1', 'PD-BULK-' || value, 'Triggered', 'exact', 100, '[]'::jsonb,
                   now() - make_interval(mins => value), true
            from generate_series(1, 55) value
            """))
        {
            command.Parameters.AddWithValue(first.ProblemGroupId);
            await command.ExecuteNonQueryAsync();
        }

        var updated = await repository.MatchOrCreateAsync(
            baseIncident with { State = IncidentState.Resolved }, fingerprint, CancellationToken.None);

        Assert.Equal(56, updated.OccurrenceCount);
        Assert.NotEqual(ProblemLifecycleState.Resolved, updated.LifecycleState);
        Assert.True(updated.RecentOccurrences.Count <= 10);
    }

    [Fact]
    public async Task LifecycleResolvesRegressesAndRemainsActiveWhileAnyOccurrenceIsActive()
    {
        var repository = Repository();
        var fingerprint = Fingerprint("gateway unavailable");
        var first = Incident("PD-LIFE-1", IncidentState.Triggered);
        var second = Incident("PD-LIFE-2", IncidentState.Triggered, minutesLater: 1);

        await repository.MatchOrCreateAsync(first, fingerprint, CancellationToken.None);
        var withTwoActive = await repository.MatchOrCreateAsync(second, fingerprint, CancellationToken.None);
        var oneResolved = await repository.MatchOrCreateAsync(first with { State = IncidentState.Resolved }, fingerprint, CancellationToken.None);

        Assert.Equal(ProblemLifecycleState.Ongoing, withTwoActive.LifecycleState);
        Assert.Equal(ProblemLifecycleState.Ongoing, oneResolved.LifecycleState);

        var fullyResolved = await repository.MatchOrCreateAsync(second with { State = IncidentState.Resolved }, fingerprint, CancellationToken.None);
        Assert.Equal(ProblemLifecycleState.Resolved, fullyResolved.LifecycleState);

        var recurrence = await repository.MatchOrCreateAsync(
            Incident("PD-LIFE-3", IncidentState.Triggered, minutesLater: 2), fingerprint, CancellationToken.None);
        Assert.Equal(ProblemLifecycleState.Regressed, recurrence.LifecycleState);
        Assert.Equal(3, recurrence.OccurrenceCount);
    }

    [Fact]
    public async Task ThreeDistinctRecentOccurrencesEscalate()
    {
        var repository = Repository();
        var fingerprint = Fingerprint("ledger connection refused");

        await repository.MatchOrCreateAsync(Incident("PD-ESC-1", IncidentState.Triggered), fingerprint, CancellationToken.None);
        await repository.MatchOrCreateAsync(Incident("PD-ESC-2", IncidentState.Triggered, 1), fingerprint, CancellationToken.None);
        var third = await repository.MatchOrCreateAsync(
            Incident("PD-ESC-3", IncidentState.Triggered, 2), fingerprint, CancellationToken.None);

        Assert.Equal(ProblemLifecycleState.Escalating, third.LifecycleState);
        Assert.Equal(3, third.OccurrenceCount);
    }

    [Fact]
    public async Task CompactHistorySurvivesFullIncidentRetention()
    {
        var problemRepository = Repository();
        var incidentRepository = new IncidentRepository(database.DataSource, TimeProvider.System);
        var incident = Incident("PD-RETENTION", IncidentState.Resolved, triggeredAt: DateTimeOffset.UtcNow - TimeSpan.FromDays(60));
        var fingerprint = Fingerprint("retained failure");
        await InsertIncidentAndEvidenceAsync(incident);
        await problemRepository.SaveFingerprintAsync(incident.Id, fingerprint, CancellationToken.None);
        await problemRepository.MatchOrCreateAsync(incident, fingerprint, CancellationToken.None);

        var deleted = await incidentRepository.PurgeOlderThanAsync(
            DateTimeOffset.UtcNow - TimeSpan.FromDays(30), CancellationToken.None);

        Assert.Equal(1, deleted);
        Assert.Equal(0, await ScalarAsync<int>("select count(*) from evidence"));
        Assert.Equal(1, await ScalarAsync<int>("select count(*) from problem_occurrences"));
        Assert.Equal(1, await ScalarAsync<int>("select count(*) from incident_fingerprints"));
        Assert.Equal(1, await ScalarAsync<int>("select count(*) from problem_groups"));
    }

    [Fact]
    public async Task CompactRetentionRecomputesGroupsAndRemovesOrphans()
    {
        var repository = Repository();
        var fingerprint = Fingerprint("retention pruning");
        var old = Incident("PD-PRUNE-OLD", IncidentState.Resolved, triggeredAt: DateTimeOffset.UtcNow - TimeSpan.FromDays(400));
        var recent = Incident("PD-PRUNE-RECENT", IncidentState.Triggered);
        var group = await repository.MatchOrCreateAsync(old, fingerprint, CancellationToken.None);
        await repository.MatchOrCreateAsync(recent, fingerprint, CancellationToken.None);
        await using (var age = database.DataSource.CreateCommand("""
            update problem_occurrences set updated_at = now() - interval '400 days' where incident_id = $1
            """))
        {
            age.Parameters.AddWithValue(old.Id);
            await age.ExecuteNonQueryAsync();
        }
        await using (var regress = database.DataSource.CreateCommand(
                         "update problem_groups set lifecycle_state = 'regressed' where id = $1"))
        {
            regress.Parameters.AddWithValue(group.ProblemGroupId);
            await regress.ExecuteNonQueryAsync();
        }

        await repository.PurgeAsync(DateTimeOffset.UtcNow - TimeSpan.FromDays(365), CancellationToken.None);

        Assert.Equal(1, await ScalarAsync<int>("select occurrence_count from problem_groups where id = '" + group.ProblemGroupId + "'"));
        Assert.Equal("new", await ScalarAsync<string>("select lifecycle_state from problem_groups where id = '" + group.ProblemGroupId + "'"));
        Assert.Equal(1, await ScalarAsync<int>("select count(*) from problem_occurrences"));

        await using (var ageRemaining = database.DataSource.CreateCommand(
            "update problem_occurrences set updated_at = now() - interval '400 days'"))
        {
            await ageRemaining.ExecuteNonQueryAsync();
        }
        await repository.PurgeAsync(DateTimeOffset.UtcNow - TimeSpan.FromDays(365), CancellationToken.None);

        Assert.Equal(0, await ScalarAsync<int>("select count(*) from problem_groups"));
        Assert.Equal(0, await ScalarAsync<int>("select count(*) from problem_occurrences"));
    }

    private ProblemRepository Repository()
    {
        var policy = new RecurrencePolicy(_options);
        return new ProblemRepository(database.DataSource, policy, TimeProvider.System, NullLogger<ProblemRepository>.Instance);
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

    private static IncidentFingerprint Fingerprint(string title)
    {
        var features = new FingerprintFeatures(
            "payments", "payments-production", ["production"], title,
            title.Split(' ', StringSplitOptions.RemoveEmptyEntries), ["error"], [title], ["checkout"], ["payments:src/provider.cs"]);
        return new FingerprintGenerator().Generate(features, FingerprintStage.Final);
    }

    private static IncidentFingerprint SimilarFingerprint(bool includeAdditionalError)
    {
        var errors = includeAdditionalError
            ? new[] { "shared timeout", "downstream retry exhausted" }
            : ["shared timeout"];
        var features = new FingerprintFeatures(
            "payments",
            "payments-production",
            ["production"],
            "checkout provider timeout",
            ["checkout", "provider", "timeout"],
            ["error"],
            errors,
            ["checkout"],
            ["payments:src/provider.cs"]);
        return new FingerprintGenerator().Generate(features, FingerprintStage.Final);
    }

    private static IncidentRecord Incident(
        string pagerDutyId,
        IncidentState state,
        int minutesLater = 0,
        DateTimeOffset? triggeredAt = null)
    {
        var occurredAt = (triggeredAt ?? DateTimeOffset.UtcNow).AddMinutes(minutesLater);
        return new IncidentRecord(
            Guid.NewGuid(), pagerDutyId, "payments", "payments-production", "failure", "high", state,
            occurredAt, occurredAt, 0, "queued", state == IncidentState.Resolved, null, "#incidents", null,
            new Dictionary<string, string> { ["environment"] = "production", ["component"] = "checkout" });
    }

    private static PagerDutyWebhookEvent Webhook(string eventId, string eventType) => new(
        eventId,
        eventType,
        "PD-WEBHOOK-1",
        "P123",
        "Payments failing",
        "high",
        "https://pagerduty.example/incidents/PD-WEBHOOK-1",
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        new Dictionary<string, string> { ["environment"] = "production" });

    private static InvestigationProfile Profile() => new()
    {
        Id = "payments-production",
        PagerDutyServiceId = "P123",
        Team = "payments",
        SlackChannel = "#incidents"
    };

    private async Task InsertIncidentAndEvidenceAsync(IncidentRecord incident)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(CancellationToken.None);
        await using var command = new NpgsqlCommand("""
            insert into incidents(id, pagerduty_incident_id, service_id, profile_id, title, urgency, state, status,
                triggered_at, updated_at, slack_channel, labels_json, is_frozen)
            values ($1, $2, $3, $4, $5, $6, $7, 'resolved', $8, $9, '#incidents', '{}'::jsonb, true)
            """, connection);
        command.Parameters.AddWithValue(incident.Id);
        command.Parameters.AddWithValue(incident.PagerDutyIncidentId);
        command.Parameters.AddWithValue(incident.ServiceId);
        command.Parameters.AddWithValue(incident.ProfileId);
        command.Parameters.AddWithValue(incident.Title);
        command.Parameters.AddWithValue(incident.Urgency);
        command.Parameters.AddWithValue(incident.State.ToString());
        command.Parameters.AddWithValue(incident.TriggeredAt);
        command.Parameters.AddWithValue(incident.UpdatedAt);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
        await using var evidence = new NpgsqlCommand("""
            insert into evidence(incident_id, report_version, finding_id, source, occurred_at, payload)
            values ($1, 1, 'old-evidence', 'logs', $2, '{}'::jsonb)
            """, connection);
        evidence.Parameters.AddWithValue(incident.Id);
        evidence.Parameters.AddWithValue(incident.TriggeredAt);
        await evidence.ExecuteNonQueryAsync(CancellationToken.None);
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
    private readonly string _containerName = $"incidentbot-fingerprint-tests-{Guid.NewGuid():N}";
    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await DockerAsync(
            "run", "--detach", "--rm", "--name", _containerName,
            "--env", "POSTGRES_DB=incidentbot_tests",
            "--env", "POSTGRES_USER=incidentbot",
            "--env", "POSTGRES_PASSWORD=incidentbot",
            "--publish", "127.0.0.1::5432", "postgres:17-alpine");
        var portOutput = await DockerAsync("port", _containerName, "5432/tcp");
        var port = int.Parse(portOutput.Trim().Split(':').Last());
        DataSource = NpgsqlDataSource.Create(
            $"Host=127.0.0.1;Port={port};Database=incidentbot_tests;Username=incidentbot;Password=incidentbot;Timeout=2");

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
            truncate table problem_occurrences, incident_fingerprints, problem_groups,
                outbox, timeline_events, evidence, work_items, webhook_receipts, incidents restart identity cascade
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
