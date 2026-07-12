using Npgsql;

namespace IncidentBot.Api.Infrastructure;

public sealed class DatabaseInitializer(NpgsqlDataSource dataSource, ILogger<DatabaseInitializer> logger) : IHostedService
{
    private const string MigrationTableSql = """
        create table if not exists schema_migrations (
            version integer primary key,
            applied_at timestamptz not null default now()
        );
        """;

    private const string SchemaSql = """
        create table if not exists incidents (
            id uuid primary key,
            pagerduty_incident_id text not null unique,
            service_id text not null,
            profile_id text not null,
            title text not null,
            urgency text not null,
            state text not null,
            status text not null,
            triggered_at timestamptz not null,
            updated_at timestamptz not null,
            version integer not null default 0,
            is_frozen boolean not null default false,
            report_json jsonb,
            slack_channel text not null,
            slack_timestamp text,
            labels_json jsonb not null default '{}'::jsonb,
            created_at timestamptz not null default now()
        );

        create table if not exists webhook_receipts (
            event_id text primary key,
            pagerduty_incident_id text not null,
            event_type text not null,
            payload_hash text not null,
            received_at timestamptz not null default now()
        );

        create table if not exists work_items (
            id bigserial primary key,
            incident_id uuid not null references incidents(id) on delete cascade,
            kind text not null,
            idempotency_key text not null unique,
            due_at timestamptz not null,
            attempts integer not null default 0,
            locked_until timestamptz,
            completed_at timestamptz,
            last_error text
        );
        create index if not exists ix_work_items_due on work_items(due_at) where completed_at is null;

        create table if not exists evidence (
            incident_id uuid not null references incidents(id) on delete cascade,
            report_version integer not null,
            finding_id text not null,
            source text not null,
            occurred_at timestamptz not null,
            payload jsonb not null,
            primary key (incident_id, report_version, finding_id)
        );

        create table if not exists timeline_events (
            incident_id uuid not null references incidents(id) on delete cascade,
            report_version integer not null,
            ordinal integer not null,
            occurred_at timestamptz not null,
            payload jsonb not null,
            primary key (incident_id, report_version, ordinal)
        );

        create table if not exists outbox (
            id bigserial primary key,
            kind text not null,
            payload jsonb not null,
            due_at timestamptz not null default now(),
            attempts integer not null default 0,
            locked_until timestamptz,
            processed_at timestamptz,
            last_error text
        );
        create index if not exists ix_outbox_due on outbox(due_at) where processed_at is null;

        create table if not exists problem_groups (
            id uuid primary key,
            algorithm_version text not null,
            problem_key text not null unique,
            service_id text not null,
            profile_id text not null,
            family_hash text not null,
            representative_exact_hash text not null,
            representative_features jsonb not null,
            lifecycle_state text not null,
            first_seen timestamptz not null,
            last_seen timestamptz not null,
            resolved_at timestamptz,
            occurrence_count integer not null default 0,
            created_at timestamptz not null default now(),
            updated_at timestamptz not null default now(),
            unique (algorithm_version, service_id, profile_id, representative_exact_hash)
        );
        create index if not exists ix_problem_groups_exact on problem_groups(algorithm_version, service_id, profile_id, representative_exact_hash);
        create index if not exists ix_problem_groups_family on problem_groups(algorithm_version, service_id, profile_id, family_hash);
        create index if not exists ix_problem_groups_recent on problem_groups(algorithm_version, service_id, profile_id, last_seen desc);

        create table if not exists incident_fingerprints (
            incident_id uuid not null,
            algorithm_version text not null,
            fingerprint_stage text not null,
            family_hash text not null,
            exact_hash text not null,
            features_json jsonb not null,
            completeness double precision not null,
            created_at timestamptz not null default now(),
            primary key (incident_id, algorithm_version, fingerprint_stage)
        );
        create index if not exists ix_incident_fingerprints_exact on incident_fingerprints(algorithm_version, exact_hash);
        create index if not exists ix_incident_fingerprints_family on incident_fingerprints(algorithm_version, family_hash);

        create table if not exists problem_occurrences (
            problem_group_id uuid not null references problem_groups(id) on delete cascade,
            incident_id uuid not null,
            algorithm_version text not null,
            pagerduty_incident_id text not null,
            incident_state text not null,
            match_type text not null,
            similarity_score integer not null,
            matched_features jsonb not null,
            occurred_at timestamptz not null,
            active boolean not null,
            created_at timestamptz not null default now(),
            updated_at timestamptz not null default now(),
            primary key (problem_group_id, incident_id),
            unique (incident_id, algorithm_version)
        );
        create index if not exists ix_problem_occurrences_history on problem_occurrences(problem_group_id, occurred_at desc);
        create index if not exists ix_problem_occurrences_updated on problem_occurrences(updated_at);
        """;

    private static readonly IReadOnlyList<(int Version, string Sql)> Migrations =
    [
        (1, SchemaSql)
    ];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        logger.LogInformation("Incident Bot PostgreSQL connection established");
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var migrationTable = new NpgsqlCommand(MigrationTableSql, connection, transaction))
        {
            await migrationTable.ExecuteNonQueryAsync(cancellationToken);
        }

        var applied = new HashSet<int>();
        await using (var appliedCommand = new NpgsqlCommand("select version from schema_migrations", connection, transaction))
        await using (var reader = await appliedCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                applied.Add(reader.GetInt32(0));
            }
        }

        foreach (var migration in Migrations.Where(migration => !applied.Contains(migration.Version)))
        {
            await using var command = new NpgsqlCommand(migration.Sql, connection, transaction);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await using var record = new NpgsqlCommand(
                "insert into schema_migrations(version) values ($1)", connection, transaction);
            record.Parameters.AddWithValue(migration.Version);
            await record.ExecuteNonQueryAsync(cancellationToken);
            logger.LogInformation("Applied Incident Bot database migration {MigrationVersion}", migration.Version);
        }

        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(
            "Incident Bot PostgreSQL schema is ready at version {SchemaVersion}",
            Migrations.Max(migration => migration.Version));
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
