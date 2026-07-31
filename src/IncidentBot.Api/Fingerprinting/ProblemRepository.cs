using System.Text.Json;
using System.Text.Json.Serialization;
using IncidentBot.Api.Domain;
using Npgsql;

namespace IncidentBot.Api.Fingerprinting;

public interface IProblemRepository
{
    Task SaveFingerprintAsync(Guid incidentId, IncidentFingerprint fingerprint, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProblemCandidate>> FindCandidatesAsync(IncidentFingerprint fingerprint, CancellationToken cancellationToken);
    Task<ProblemMatch> MatchOrCreateAsync(IncidentRecord incident, IncidentFingerprint fingerprint, CancellationToken cancellationToken);
    Task<int> PurgeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);
}

public sealed class ProblemRepository(
    NpgsqlDataSource dataSource,
    RecurrencePolicy policy,
    TimeProvider timeProvider,
    ILogger<ProblemRepository> logger) : IProblemRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async Task SaveFingerprintAsync(Guid incidentId, IncidentFingerprint fingerprint, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            insert into incident_fingerprints(incident_id, algorithm_version, fingerprint_stage, family_hash, exact_hash, features_json, completeness)
            values ($1, $2, $3, $4, $5, $6::jsonb, $7)
            on conflict (incident_id, algorithm_version, fingerprint_stage) do update set
                family_hash = excluded.family_hash, exact_hash = excluded.exact_hash,
                features_json = excluded.features_json, completeness = excluded.completeness
            """);
        command.Parameters.AddWithValue(incidentId);
        command.Parameters.AddWithValue(fingerprint.AlgorithmVersion);
        command.Parameters.AddWithValue(fingerprint.Stage.ToString().ToLowerInvariant());
        command.Parameters.AddWithValue(fingerprint.FamilyHash);
        command.Parameters.AddWithValue(fingerprint.ExactHash);
        command.Parameters.AddWithValue(JsonSerializer.Serialize(fingerprint.Features, JsonOptions));
        command.Parameters.AddWithValue(fingerprint.Completeness);
        await command.ExecuteNonQueryAsync(cancellationToken);
        logger.LogInformation("Persisted {FingerprintStage} incident fingerprint using {AlgorithmVersion}", fingerprint.Stage, fingerprint.AlgorithmVersion);
    }

    public async Task<IReadOnlyList<ProblemCandidate>> FindCandidatesAsync(IncidentFingerprint fingerprint, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            select id, problem_key, representative_exact_hash, family_hash, representative_features::text,
                   lifecycle_state, occurrence_count, first_seen, last_seen
            from problem_groups
            where algorithm_version = $1 and service_id = $2 and profile_id = $3
              and (representative_exact_hash = $5 or last_seen >= $4)
            order by (representative_exact_hash = $5) desc, (family_hash = $6) desc, last_seen desc, id
            limit $7
            """);
        command.Parameters.AddWithValue(fingerprint.AlgorithmVersion);
        command.Parameters.AddWithValue(fingerprint.Features.ServiceId);
        command.Parameters.AddWithValue(fingerprint.Features.ProfileId);
        command.Parameters.AddWithValue(policy.CandidateCutoff(timeProvider.GetUtcNow()));
        command.Parameters.AddWithValue(fingerprint.ExactHash);
        command.Parameters.AddWithValue(fingerprint.FamilyHash);
        command.Parameters.AddWithValue(policy.MaximumCandidates);
        var candidates = new List<ProblemCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var features = JsonSerializer.Deserialize<FingerprintFeatures>(reader.GetString(4), JsonOptions)!;
            var representative = new IncidentFingerprint(
                fingerprint.AlgorithmVersion, FingerprintStage.Final, reader.GetString(3), reader.GetString(2), features, 1);
            candidates.Add(new ProblemCandidate(
                reader.GetGuid(0), reader.GetString(1), representative,
                Enum.Parse<ProblemLifecycleState>(reader.GetString(5), true), reader.GetInt32(6),
                reader.GetFieldValue<DateTimeOffset>(7), reader.GetFieldValue<DateTimeOffset>(8)));
        }
        return candidates;
    }

    public async Task<ProblemMatch> MatchOrCreateAsync(IncidentRecord incident, IncidentFingerprint fingerprint, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var incidentLock = new NpgsqlCommand("select pg_advisory_xact_lock(hashtextextended($1, 0))", connection, transaction))
        {
            incidentLock.Parameters.AddWithValue($"incident|{fingerprint.AlgorithmVersion}|{incident.Id}");
            await incidentLock.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var advisory = new NpgsqlCommand("select pg_advisory_xact_lock(hashtextextended($1, 0))", connection, transaction))
        {
            advisory.Parameters.AddWithValue(
                $"association|{fingerprint.AlgorithmVersion}|{fingerprint.Features.ServiceId}|{fingerprint.Features.ProfileId}");
            await advisory.ExecuteNonQueryAsync(cancellationToken);
        }

        var candidates = (await ReadCandidatesAsync(connection, transaction, fingerprint, cancellationToken)).ToList();
        var existingGroupId = await ReadExistingGroupIdAsync(connection, transaction, incident.Id, fingerprint.AlgorithmVersion, cancellationToken);
        if (existingGroupId is { } existingId && candidates.All(candidate => candidate.GroupId != existingId))
        {
            var existingCandidate = await ReadCandidateAsync(connection, transaction, existingId, fingerprint.AlgorithmVersion, cancellationToken);
            if (existingCandidate is not null) candidates.Add(existingCandidate);
        }
        var selected = policy.SelectAssociation(fingerprint, candidates, existingGroupId);
        var groupId = selected?.Candidate.GroupId ?? Guid.NewGuid();
        var problemKey = selected?.Candidate.ProblemKey ?? RecurrencePolicy.ProblemKey(fingerprint);
        var previousLifecycle = selected?.Candidate.LifecycleState;
        var matchType = selected?.MatchType ?? "new";
        var score = selected?.Score ?? 0;
        var explanation = selected?.MatchedFeatures ?? ["new deterministic fingerprint"];

        if (selected is null)
        {
            await using var insert = new NpgsqlCommand("""
                insert into problem_groups(id, algorithm_version, problem_key, service_id, profile_id, family_hash,
                    representative_exact_hash, representative_features, lifecycle_state, first_seen, last_seen)
                values ($1, $2, $3, $4, $5, $6, $7, $8::jsonb, 'new', $9, $9)
                on conflict (algorithm_version, service_id, profile_id, representative_exact_hash) do update set updated_at = now()
                returning id, problem_key, lifecycle_state
                """, connection, transaction);
            insert.Parameters.AddWithValue(groupId);
            insert.Parameters.AddWithValue(fingerprint.AlgorithmVersion);
            insert.Parameters.AddWithValue(problemKey);
            insert.Parameters.AddWithValue(fingerprint.Features.ServiceId);
            insert.Parameters.AddWithValue(fingerprint.Features.ProfileId);
            insert.Parameters.AddWithValue(fingerprint.FamilyHash);
            insert.Parameters.AddWithValue(fingerprint.ExactHash);
            insert.Parameters.AddWithValue(JsonSerializer.Serialize(fingerprint.Features, JsonOptions));
            insert.Parameters.AddWithValue(incident.TriggeredAt);
            await using var inserted = await insert.ExecuteReaderAsync(cancellationToken);
            await inserted.ReadAsync(cancellationToken);
            groupId = inserted.GetGuid(0);
            problemKey = inserted.GetString(1);
            previousLifecycle = Enum.Parse<ProblemLifecycleState>(inserted.GetString(2), true);
        }

        await using (var occurrence = new NpgsqlCommand("""
            insert into problem_occurrences(problem_group_id, incident_id, algorithm_version, pagerduty_incident_id,
                incident_state, match_type, similarity_score, matched_features, occurred_at, active)
            values ($1, $2, $3, $4, $5, $6, $7, $8::jsonb, $9, $10)
            on conflict (incident_id, algorithm_version) do update set
                incident_state = excluded.incident_state, match_type = excluded.match_type,
                similarity_score = excluded.similarity_score, matched_features = excluded.matched_features,
                active = excluded.active, updated_at = now()
            """, connection, transaction))
        {
            occurrence.Parameters.AddWithValue(groupId);
            occurrence.Parameters.AddWithValue(incident.Id);
            occurrence.Parameters.AddWithValue(fingerprint.AlgorithmVersion);
            occurrence.Parameters.AddWithValue(incident.PagerDutyIncidentId);
            occurrence.Parameters.AddWithValue(incident.State.ToString());
            occurrence.Parameters.AddWithValue(matchType);
            occurrence.Parameters.AddWithValue(score);
            occurrence.Parameters.AddWithValue(JsonSerializer.Serialize(explanation, JsonOptions));
            occurrence.Parameters.AddWithValue(incident.TriggeredAt);
            occurrence.Parameters.AddWithValue(incident.State != IncidentState.Resolved);
            await occurrence.ExecuteNonQueryAsync(cancellationToken);
        }

        var history = await ReadHistoryAsync(connection, transaction, groupId, cancellationToken);
        var stats = await ReadStatsAsync(connection, transaction, groupId,
            policy.EscalationCutoff(timeProvider.GetUtcNow()), cancellationToken);
        var lifecycle = policy.ClassifyLifecycle(
            previousLifecycle, incident.State, stats.Active, stats.Count, stats.RecentCount);

        await using (var update = new NpgsqlCommand("""
            update problem_groups set lifecycle_state = $2, occurrence_count = $3, first_seen = $4, last_seen = $5,
                resolved_at = case when $2 = 'resolved' then now() else null end, updated_at = now()
            where id = $1
            """, connection, transaction))
        {
            update.Parameters.AddWithValue(groupId);
            update.Parameters.AddWithValue(lifecycle.ToString().ToLowerInvariant());
            update.Parameters.AddWithValue(stats.Count);
            update.Parameters.AddWithValue(stats.FirstSeen);
            update.Parameters.AddWithValue(stats.LastSeen);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(
            "Fingerprint decision {MatchType} selected problem {ProblemGroupId} at score {SimilarityScore} from {CandidateCount} candidates; lifecycle {PreviousLifecycle} -> {Lifecycle}",
            matchType, groupId, score, candidates.Count, previousLifecycle, lifecycle);
        return new ProblemMatch(groupId, problemKey, matchType, score, explanation, lifecycle, stats.Count,
            stats.FirstSeen, stats.LastSeen, history.Take(10).ToArray());
    }

    public async Task<int> PurgeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var fingerprints = new NpgsqlCommand("delete from incident_fingerprints where created_at < $1", connection, transaction);
        fingerprints.Parameters.AddWithValue(cutoff);
        var deleted = await fingerprints.ExecuteNonQueryAsync(cancellationToken);

        // MatchOrCreate locks problem groups before mutating their occurrences. Lock every group
        // affected by retention in the same deterministic order so the two transactions cannot
        // form a group -> occurrence / occurrence -> group deadlock.
        var affectedGroups = new List<Guid>();
        await using (var groupLocks = new NpgsqlCommand("""
            select g.id
            from problem_groups g
            where exists (
                select 1 from problem_occurrences o
                where o.problem_group_id = g.id and o.updated_at < $1)
            order by g.id
            for update of g
            """, connection, transaction))
        {
            groupLocks.Parameters.AddWithValue(cutoff);
            await using var reader = await groupLocks.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                affectedGroups.Add(reader.GetGuid(0));
            }
        }

        await using var occurrences = new NpgsqlCommand("delete from problem_occurrences where updated_at < $1", connection, transaction);
        occurrences.Parameters.AddWithValue(cutoff);
        deleted += await occurrences.ExecuteNonQueryAsync(cancellationToken);

        if (affectedGroups.Count > 0)
        {
            var statsByGroup = new Dictionary<Guid, GroupStats>();
            await using (var stats = new NpgsqlCommand("""
                select problem_group_id, count(*)::integer, min(occurred_at), max(occurred_at),
                       coalesce(bool_or(active), false),
                       count(*) filter (where occurred_at >= $2)::integer
                from problem_occurrences
                where problem_group_id = any($1)
                group by problem_group_id
                order by problem_group_id
                """, connection, transaction))
            {
                stats.Parameters.AddWithValue(affectedGroups.ToArray());
                stats.Parameters.AddWithValue(policy.EscalationCutoff(timeProvider.GetUtcNow()));
                await using var reader = await stats.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    statsByGroup[reader.GetGuid(0)] = new GroupStats(
                        reader.GetInt32(1),
                        reader.GetFieldValue<DateTimeOffset>(2),
                        reader.GetFieldValue<DateTimeOffset>(3),
                        reader.GetBoolean(4),
                        reader.GetInt32(5));
                }
            }

            await using var updates = new NpgsqlBatch(connection, transaction);
            foreach (var groupId in affectedGroups)
            {
                if (!statsByGroup.TryGetValue(groupId, out var stats)) continue;
                var lifecycle = policy.ClassifyAfterRetention(
                    stats.Active,
                    stats.Count,
                    stats.RecentCount);
                var update = new NpgsqlBatchCommand("""
                    update problem_groups set
                        lifecycle_state = $2, occurrence_count = $3, first_seen = $4, last_seen = $5,
                        resolved_at = case when $2 = 'resolved' then coalesce(resolved_at, now()) else null end,
                        updated_at = now()
                    where id = $1
                    """);
                update.Parameters.AddWithValue(groupId);
                update.Parameters.AddWithValue(lifecycle.ToString().ToLowerInvariant());
                update.Parameters.AddWithValue(stats.Count);
                update.Parameters.AddWithValue(stats.FirstSeen);
                update.Parameters.AddWithValue(stats.LastSeen);
                updates.BatchCommands.Add(update);
            }
            if (updates.BatchCommands.Count > 0)
            {
                await updates.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await using var groups = new NpgsqlCommand("delete from problem_groups g where not exists (select 1 from problem_occurrences o where o.problem_group_id = g.id)", connection, transaction);
        deleted += await groups.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return deleted;
    }

    private async Task<IReadOnlyList<ProblemCandidate>> ReadCandidatesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, IncidentFingerprint fingerprint, CancellationToken ct)
    {
        // The incident advisory lock serializes assignment lookups. Avoid locking the occurrence
        // here so every mutation path acquires its problem-group row before an occurrence row.
        await using var command = new NpgsqlCommand("""
            select id, problem_key, representative_exact_hash, family_hash, representative_features::text,
                   lifecycle_state, occurrence_count, first_seen, last_seen
            from problem_groups
            where algorithm_version = $1 and service_id = $2 and profile_id = $3
              and (representative_exact_hash = $5 or last_seen >= $4)
            order by (representative_exact_hash = $5) desc, (family_hash = $6) desc, last_seen desc, id limit $7
            for update
            """, connection, transaction);
        command.Parameters.AddWithValue(fingerprint.AlgorithmVersion);
        command.Parameters.AddWithValue(fingerprint.Features.ServiceId);
        command.Parameters.AddWithValue(fingerprint.Features.ProfileId);
        command.Parameters.AddWithValue(policy.CandidateCutoff(timeProvider.GetUtcNow()));
        command.Parameters.AddWithValue(fingerprint.ExactHash);
        command.Parameters.AddWithValue(fingerprint.FamilyHash);
        command.Parameters.AddWithValue(policy.MaximumCandidates);
        var values = new List<ProblemCandidate>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var features = JsonSerializer.Deserialize<FingerprintFeatures>(reader.GetString(4), JsonOptions)!;
            values.Add(new ProblemCandidate(reader.GetGuid(0), reader.GetString(1),
                new IncidentFingerprint(fingerprint.AlgorithmVersion, FingerprintStage.Final, reader.GetString(3), reader.GetString(2), features, 1),
                Enum.Parse<ProblemLifecycleState>(reader.GetString(5), true), reader.GetInt32(6),
                reader.GetFieldValue<DateTimeOffset>(7), reader.GetFieldValue<DateTimeOffset>(8)));
        }
        return values;
    }

    private static async Task<IReadOnlyList<ProblemOccurrenceSummary>> ReadHistoryAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid groupId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            select incident_id, pagerduty_incident_id, incident_state, occurred_at, updated_at
            from problem_occurrences where problem_group_id = $1 order by occurred_at desc, incident_id limit 50
            """, connection, transaction);
        command.Parameters.AddWithValue(groupId);
        var values = new List<ProblemOccurrenceSummary>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetGuid(0);
            values.Add(new ProblemOccurrenceSummary(id, reader.GetString(1), Enum.Parse<IncidentState>(reader.GetString(2), true),
                reader.GetFieldValue<DateTimeOffset>(3), reader.GetFieldValue<DateTimeOffset>(4), $"/incidents/{id}"));
        }
        return values;
    }

    private static async Task<Guid?> ReadExistingGroupIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid incidentId,
        string algorithmVersion,
        CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            select problem_group_id from problem_occurrences
            where incident_id = $1 and algorithm_version = $2
            """, connection, transaction);
        command.Parameters.AddWithValue(incidentId);
        command.Parameters.AddWithValue(algorithmVersion);
        return await command.ExecuteScalarAsync(ct) is Guid groupId ? groupId : null;
    }

    private static async Task<ProblemCandidate?> ReadCandidateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid groupId,
        string algorithmVersion,
        CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            select id, problem_key, representative_exact_hash, family_hash, representative_features::text,
                   lifecycle_state, occurrence_count, first_seen, last_seen
            from problem_groups where id = $1 for update
            """, connection, transaction);
        command.Parameters.AddWithValue(groupId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var features = JsonSerializer.Deserialize<FingerprintFeatures>(reader.GetString(4), JsonOptions)!;
        return new ProblemCandidate(reader.GetGuid(0), reader.GetString(1),
            new IncidentFingerprint(algorithmVersion, FingerprintStage.Final, reader.GetString(3), reader.GetString(2), features, 1),
            Enum.Parse<ProblemLifecycleState>(reader.GetString(5), true), reader.GetInt32(6),
            reader.GetFieldValue<DateTimeOffset>(7), reader.GetFieldValue<DateTimeOffset>(8));
    }

    private static async Task<GroupStats> ReadStatsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid groupId,
        DateTimeOffset recentCutoff,
        CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            select count(*)::integer, min(occurred_at), max(occurred_at), coalesce(bool_or(active), false),
                   count(*) filter (where occurred_at >= $2)::integer
            from problem_occurrences where problem_group_id = $1
            """, connection, transaction);
        command.Parameters.AddWithValue(groupId);
        command.Parameters.AddWithValue(recentCutoff);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || reader.GetInt32(0) == 0)
            throw new InvalidOperationException("Problem group has no occurrence after association.");
        return new GroupStats(reader.GetInt32(0), reader.GetFieldValue<DateTimeOffset>(1),
            reader.GetFieldValue<DateTimeOffset>(2), reader.GetBoolean(3), reader.GetInt32(4));
    }
    private sealed record GroupStats(int Count, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, bool Active, int RecentCount);
}
