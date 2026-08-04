using System.Text.Json;
using System.Text.Json.Serialization;
using Panko.Api.Domain;
using Panko.Api.Signatures;
using Npgsql;
using NpgsqlTypes;

namespace Panko.Api.Patterns;

public interface IPatternRepository
{
    Task SaveSignatureAsync(Guid caseId, CaseSignature signature, CancellationToken cancellationToken);
    Task<IReadOnlyList<PatternCandidate>> FindCandidatesAsync(
        string team,
        CaseSignature signature,
        CancellationToken cancellationToken);
    Task<PatternMatch> MatchOrCreateAsync(CaseRecord caseRecord, CaseSignature signature, CancellationToken cancellationToken);
    Task<int> PurgeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);
}

public sealed class PatternRepository(
    NpgsqlDataSource dataSource,
    PatternPolicy policy,
    TimeProvider timeProvider,
    ILogger<PatternRepository> logger) : IPatternRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async Task SaveSignatureAsync(Guid caseId, CaseSignature signature, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            insert into case_signatures(case_id, algorithm_version, signature_stage, family_hash, exact_hash, features_json, completeness)
            values ($1, $2, $3, $4, $5, $6::jsonb, $7)
            on conflict (case_id, algorithm_version, signature_stage) do update set
                family_hash = excluded.family_hash, exact_hash = excluded.exact_hash,
                features_json = excluded.features_json, completeness = excluded.completeness
            """);
        command.Parameters.AddWithValue(caseId);
        command.Parameters.AddWithValue(signature.AlgorithmVersion);
        command.Parameters.AddWithValue(signature.Stage.ToString().ToLowerInvariant());
        command.Parameters.AddWithValue(signature.FamilyHash);
        command.Parameters.AddWithValue(signature.ExactHash);
        command.Parameters.AddWithValue(JsonSerializer.Serialize(signature.Features, JsonOptions));
        command.Parameters.AddWithValue(signature.Completeness);
        await command.ExecuteNonQueryAsync(cancellationToken);
        logger.LogInformation("Persisted {SignatureStage} Case Signature using {AlgorithmVersion}", signature.Stage, signature.AlgorithmVersion);
    }

    public async Task<IReadOnlyList<PatternCandidate>> FindCandidatesAsync(
        string team,
        CaseSignature signature,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            select id, pattern_key, representative_exact_hash, family_hash, representative_features::text,
                   lifecycle_state, occurrence_count, first_seen, last_seen
            from patterns
            where team = $8 and algorithm_version = $1 and service_id = $2 and recipe_id = $3
              and (representative_exact_hash = $5 or last_seen >= $4)
            order by (representative_exact_hash = $5) desc, (family_hash = $6) desc, last_seen desc, id
            limit $7
            """);
        command.Parameters.AddWithValue(signature.AlgorithmVersion);
        command.Parameters.AddWithValue(signature.Features.ServiceId);
        command.Parameters.AddWithValue(signature.Features.RecipeId);
        command.Parameters.AddWithValue(policy.CandidateCutoff(timeProvider.GetUtcNow()));
        command.Parameters.AddWithValue(signature.ExactHash);
        command.Parameters.AddWithValue(signature.FamilyHash);
        command.Parameters.AddWithValue(policy.MaximumCandidates);
        command.Parameters.AddWithValue(team);
        var candidates = new List<PatternCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var features = JsonSerializer.Deserialize<SignatureFeatures>(reader.GetString(4), JsonOptions)!;
            var representative = new CaseSignature(
                signature.AlgorithmVersion, SignatureStage.Final, reader.GetString(3), reader.GetString(2), features, 1);
            candidates.Add(new PatternCandidate(
                reader.GetGuid(0), reader.GetString(1), representative,
                Enum.Parse<PatternLifecycleState>(reader.GetString(5), true), reader.GetInt32(6),
                reader.GetFieldValue<DateTimeOffset>(7), reader.GetFieldValue<DateTimeOffset>(8)));
        }
        return candidates;
    }

    public async Task<PatternMatch> MatchOrCreateAsync(CaseRecord caseRecord, CaseSignature signature, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var caseLock = new NpgsqlCommand("select pg_advisory_xact_lock(hashtextextended($1, 0))", connection, transaction))
        {
            caseLock.Parameters.AddWithValue($"case|{signature.AlgorithmVersion}|{caseRecord.Id}");
            await caseLock.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var patternAssignmentLock = new NpgsqlCommand("select pg_advisory_xact_lock(hashtextextended($1, 0))", connection, transaction))
        {
            patternAssignmentLock.Parameters.AddWithValue(
                $"association|{caseRecord.Team}|{signature.AlgorithmVersion}|{signature.Features.ServiceId}|{signature.Features.RecipeId}");
            await patternAssignmentLock.ExecuteNonQueryAsync(cancellationToken);
        }

        var candidates = (await ReadCandidatesAsync(
            connection, transaction, caseRecord.Team, signature, cancellationToken)).ToList();
        var existingPatternId = await ReadExistingPatternIdAsync(
            connection, transaction, caseRecord.Id, signature.AlgorithmVersion, caseRecord.Team, cancellationToken);
        if (existingPatternId is { } existingId && candidates.All(candidate => candidate.PatternId != existingId))
        {
            var existingCandidate = await ReadCandidateAsync(
                connection, transaction, existingId, signature.AlgorithmVersion, caseRecord.Team, cancellationToken);
            if (existingCandidate is not null) candidates.Add(existingCandidate);
        }
        var selected = policy.SelectAssociation(signature, candidates, existingPatternId);
        var patternId = selected?.Candidate.PatternId ?? Guid.NewGuid();
        var patternKey = selected?.Candidate.PatternKey ?? PatternPolicy.PatternKey(signature);
        var previousLifecycle = selected?.Candidate.LifecycleState;
        var matchType = selected?.MatchType ?? "new";
        var score = selected?.Score ?? 0;
        var explanation = selected?.MatchedFeatures ?? ["new deterministic signature"];

        if (selected is null)
        {
            await using var insert = new NpgsqlCommand("""
                insert into patterns(id, algorithm_version, pattern_key, service_id, recipe_id, family_hash, team,
                    representative_exact_hash, representative_features, lifecycle_state, first_seen, last_seen)
                values ($1, $2, $3, $4, $5, $6, $10, $7, $8::jsonb, 'new', $9, $9)
                on conflict (team, algorithm_version, service_id, recipe_id, representative_exact_hash)
                    do update set updated_at = now()
                returning id, pattern_key, lifecycle_state
                """, connection, transaction);
            insert.Parameters.AddWithValue(patternId);
            insert.Parameters.AddWithValue(signature.AlgorithmVersion);
            insert.Parameters.AddWithValue(patternKey);
            insert.Parameters.AddWithValue(signature.Features.ServiceId);
            insert.Parameters.AddWithValue(signature.Features.RecipeId);
            insert.Parameters.AddWithValue(signature.FamilyHash);
            insert.Parameters.AddWithValue(signature.ExactHash);
            insert.Parameters.AddWithValue(JsonSerializer.Serialize(signature.Features, JsonOptions));
            insert.Parameters.AddWithValue(caseRecord.OpenedAt);
            insert.Parameters.AddWithValue(caseRecord.Team);
            await using var inserted = await insert.ExecuteReaderAsync(cancellationToken);
            await inserted.ReadAsync(cancellationToken);
            patternId = inserted.GetGuid(0);
            patternKey = inserted.GetString(1);
            previousLifecycle = Enum.Parse<PatternLifecycleState>(inserted.GetString(2), true);
        }

        await using (var occurrence = new NpgsqlCommand("""
            insert into pattern_occurrences(pattern_id, case_id, algorithm_version, pagerduty_incident_id,
                pagerduty_state, match_type, similarity_score, matched_features, occurred_at, active)
            values ($1, $2, $3, $4, $5, $6, $7, $8::jsonb, $9, $10)
            on conflict (case_id, algorithm_version) do update set
                pagerduty_state = excluded.pagerduty_state, match_type = excluded.match_type,
                similarity_score = excluded.similarity_score, matched_features = excluded.matched_features,
                active = excluded.active, updated_at = now()
            """, connection, transaction))
        {
            occurrence.Parameters.AddWithValue(patternId);
            occurrence.Parameters.AddWithValue(caseRecord.Id);
            occurrence.Parameters.AddWithValue(signature.AlgorithmVersion);
            occurrence.Parameters.AddWithValue(
                NpgsqlDbType.Text,
                (object?)caseRecord.PagerDutyIncidentId ?? DBNull.Value);
            occurrence.Parameters.AddWithValue(caseRecord.PagerDutyState.ToString());
            occurrence.Parameters.AddWithValue(matchType);
            occurrence.Parameters.AddWithValue(score);
            occurrence.Parameters.AddWithValue(JsonSerializer.Serialize(explanation, JsonOptions));
            occurrence.Parameters.AddWithValue(caseRecord.OpenedAt);
            occurrence.Parameters.AddWithValue(caseRecord.PagerDutyState != PagerDutyIncidentState.Resolved);
            await occurrence.ExecuteNonQueryAsync(cancellationToken);
        }

        var history = await ReadHistoryAsync(
            connection, transaction, patternId, caseRecord.Team, cancellationToken);
        var stats = await ReadStatsAsync(connection, transaction, patternId,
            policy.EscalationCutoff(timeProvider.GetUtcNow()), cancellationToken);
        var lifecycle = policy.ClassifyLifecycle(
            previousLifecycle, caseRecord.PagerDutyState, stats.Active, stats.Count, stats.RecentCount);

        await using (var update = new NpgsqlCommand("""
            update patterns set lifecycle_state = $2, occurrence_count = $3, first_seen = $4, last_seen = $5,
                resolved_at = case when $2 = 'resolved' then now() else null end, updated_at = now()
            where id = $1 and team = $6
            """, connection, transaction))
        {
            update.Parameters.AddWithValue(patternId);
            update.Parameters.AddWithValue(lifecycle.ToString().ToLowerInvariant());
            update.Parameters.AddWithValue(stats.Count);
            update.Parameters.AddWithValue(stats.FirstSeen);
            update.Parameters.AddWithValue(stats.LastSeen);
            update.Parameters.AddWithValue(caseRecord.Team);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException("Pattern ownership changed during association.");
            }
        }
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(
            "Signature decision {MatchType} selected Pattern {PatternId} at score {SimilarityScore} from {CandidateCount} candidates; lifecycle {PreviousLifecycle} -> {Lifecycle}",
            matchType, patternId, score, candidates.Count, previousLifecycle, lifecycle);
        return new PatternMatch(patternId, patternKey, matchType, score, explanation, lifecycle, stats.Count,
            stats.FirstSeen, stats.LastSeen, history.Take(10).ToArray());
    }

    public async Task<int> PurgeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var signatures = new NpgsqlCommand("delete from case_signatures where created_at < $1", connection, transaction);
        signatures.Parameters.AddWithValue(cutoff);
        var deleted = await signatures.ExecuteNonQueryAsync(cancellationToken);

        // MatchOrCreate locks Patterns before mutating their occurrences. Lock every Pattern
        // affected by retention in the same deterministic order so the two transactions cannot
        // form a Pattern -> occurrence / occurrence -> Pattern deadlock.
        var affectedPatterns = new List<Guid>();
        await using (var patternLocks = new NpgsqlCommand("""
            select g.id
            from patterns g
            where exists (
                select 1 from pattern_occurrences o
                where o.pattern_id = g.id and o.updated_at < $1)
            order by g.id
            for update of g
            """, connection, transaction))
        {
            patternLocks.Parameters.AddWithValue(cutoff);
            await using var reader = await patternLocks.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                affectedPatterns.Add(reader.GetGuid(0));
            }
        }

        await using var occurrences = new NpgsqlCommand("delete from pattern_occurrences where updated_at < $1", connection, transaction);
        occurrences.Parameters.AddWithValue(cutoff);
        deleted += await occurrences.ExecuteNonQueryAsync(cancellationToken);

        if (affectedPatterns.Count > 0)
        {
            var statsByPattern = new Dictionary<Guid, PatternStats>();
            await using (var stats = new NpgsqlCommand("""
                select pattern_id, count(*)::integer, min(occurred_at), max(occurred_at),
                       coalesce(bool_or(active), false),
                       count(*) filter (where occurred_at >= $2)::integer
                from pattern_occurrences
                where pattern_id = any($1)
                group by pattern_id
                order by pattern_id
                """, connection, transaction))
            {
                stats.Parameters.AddWithValue(affectedPatterns.ToArray());
                stats.Parameters.AddWithValue(policy.EscalationCutoff(timeProvider.GetUtcNow()));
                await using var reader = await stats.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    statsByPattern[reader.GetGuid(0)] = new PatternStats(
                        reader.GetInt32(1),
                        reader.GetFieldValue<DateTimeOffset>(2),
                        reader.GetFieldValue<DateTimeOffset>(3),
                        reader.GetBoolean(4),
                        reader.GetInt32(5));
                }
            }

            await using var updates = new NpgsqlBatch(connection, transaction);
            foreach (var patternId in affectedPatterns)
            {
                if (!statsByPattern.TryGetValue(patternId, out var stats)) continue;
                var lifecycle = policy.ClassifyAfterRetention(
                    stats.Active,
                    stats.Count,
                    stats.RecentCount);
                var update = new NpgsqlBatchCommand("""
                    update patterns set
                        lifecycle_state = $2, occurrence_count = $3, first_seen = $4, last_seen = $5,
                        resolved_at = case when $2 = 'resolved' then coalesce(resolved_at, now()) else null end,
                        updated_at = now()
                    where id = $1
                    """);
                update.Parameters.AddWithValue(patternId);
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

        await using var emptyPatterns = new NpgsqlCommand("delete from patterns g where not exists (select 1 from pattern_occurrences o where o.pattern_id = g.id)", connection, transaction);
        deleted += await emptyPatterns.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return deleted;
    }

    private async Task<IReadOnlyList<PatternCandidate>> ReadCandidatesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string team,
        CaseSignature signature,
        CancellationToken ct)
    {
        // The advisory-lock key serializes Case assignment lookups. Avoid locking the occurrence
        // here so every mutation path acquires its physical Pattern row first.
        await using var command = new NpgsqlCommand("""
            select id, pattern_key, representative_exact_hash, family_hash, representative_features::text,
                   lifecycle_state, occurrence_count, first_seen, last_seen
            from patterns
            where team = $8 and algorithm_version = $1 and service_id = $2 and recipe_id = $3
              and (representative_exact_hash = $5 or last_seen >= $4)
            order by (representative_exact_hash = $5) desc, (family_hash = $6) desc, last_seen desc, id limit $7
            for update
            """, connection, transaction);
        command.Parameters.AddWithValue(signature.AlgorithmVersion);
        command.Parameters.AddWithValue(signature.Features.ServiceId);
        command.Parameters.AddWithValue(signature.Features.RecipeId);
        command.Parameters.AddWithValue(policy.CandidateCutoff(timeProvider.GetUtcNow()));
        command.Parameters.AddWithValue(signature.ExactHash);
        command.Parameters.AddWithValue(signature.FamilyHash);
        command.Parameters.AddWithValue(policy.MaximumCandidates);
        command.Parameters.AddWithValue(team);
        var values = new List<PatternCandidate>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var features = JsonSerializer.Deserialize<SignatureFeatures>(reader.GetString(4), JsonOptions)!;
            values.Add(new PatternCandidate(reader.GetGuid(0), reader.GetString(1),
                new CaseSignature(signature.AlgorithmVersion, SignatureStage.Final, reader.GetString(3), reader.GetString(2), features, 1),
                Enum.Parse<PatternLifecycleState>(reader.GetString(5), true), reader.GetInt32(6),
                reader.GetFieldValue<DateTimeOffset>(7), reader.GetFieldValue<DateTimeOffset>(8)));
        }
        return values;
    }

    private static async Task<IReadOnlyList<PatternOccurrenceSummary>> ReadHistoryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid patternId,
        string team,
        CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            select occurrence.case_id, occurrence.pagerduty_incident_id, occurrence.pagerduty_state,
                   occurrence.occurred_at, occurrence.updated_at
            from pattern_occurrences occurrence
            inner join patterns pattern on pattern.id = occurrence.pattern_id
            where occurrence.pattern_id = $1 and pattern.team = $2
            order by occurrence.occurred_at desc, occurrence.case_id
            limit 50
            """, connection, transaction);
        command.Parameters.AddWithValue(patternId);
        command.Parameters.AddWithValue(team);
        var values = new List<PatternOccurrenceSummary>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetGuid(0);
            values.Add(new PatternOccurrenceSummary(id, reader.IsDBNull(1) ? null : reader.GetString(1), Enum.Parse<PagerDutyIncidentState>(reader.GetString(2), true),
                reader.GetFieldValue<DateTimeOffset>(3), reader.GetFieldValue<DateTimeOffset>(4), $"/cases/{id}"));
        }
        return values;
    }

    private static async Task<Guid?> ReadExistingPatternIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid caseId,
        string algorithmVersion,
        string team,
        CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            select occurrence.pattern_id
            from pattern_occurrences occurrence
            inner join patterns pattern on pattern.id = occurrence.pattern_id
            where occurrence.case_id = $1
              and occurrence.algorithm_version = $2
              and pattern.team = $3
            """, connection, transaction);
        command.Parameters.AddWithValue(caseId);
        command.Parameters.AddWithValue(algorithmVersion);
        command.Parameters.AddWithValue(team);
        return await command.ExecuteScalarAsync(ct) is Guid patternId ? patternId : null;
    }

    private static async Task<PatternCandidate?> ReadCandidateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid patternId,
        string algorithmVersion,
        string team,
        CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            select id, pattern_key, representative_exact_hash, family_hash, representative_features::text,
                   lifecycle_state, occurrence_count, first_seen, last_seen
            from patterns where id = $1 and team = $2 for update
            """, connection, transaction);
        command.Parameters.AddWithValue(patternId);
        command.Parameters.AddWithValue(team);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var features = JsonSerializer.Deserialize<SignatureFeatures>(reader.GetString(4), JsonOptions)!;
        return new PatternCandidate(reader.GetGuid(0), reader.GetString(1),
            new CaseSignature(algorithmVersion, SignatureStage.Final, reader.GetString(3), reader.GetString(2), features, 1),
            Enum.Parse<PatternLifecycleState>(reader.GetString(5), true), reader.GetInt32(6),
            reader.GetFieldValue<DateTimeOffset>(7), reader.GetFieldValue<DateTimeOffset>(8));
    }

    private static async Task<PatternStats> ReadStatsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid patternId,
        DateTimeOffset recentCutoff,
        CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            select count(*)::integer, min(occurred_at), max(occurred_at), coalesce(bool_or(active), false),
                   count(*) filter (where occurred_at >= $2)::integer
            from pattern_occurrences where pattern_id = $1
            """, connection, transaction);
        command.Parameters.AddWithValue(patternId);
        command.Parameters.AddWithValue(recentCutoff);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || reader.GetInt32(0) == 0)
            throw new InvalidOperationException("Pattern has no occurrence after association.");
        return new PatternStats(reader.GetInt32(0), reader.GetFieldValue<DateTimeOffset>(1),
            reader.GetFieldValue<DateTimeOffset>(2), reader.GetBoolean(3), reader.GetInt32(4));
    }
    private sealed record PatternStats(int Count, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, bool Active, int RecentCount);
}
