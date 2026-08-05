using Npgsql;

namespace Panko.Api.Infrastructure;

public sealed class DatabaseInitializer(
    NpgsqlDataSource dataSource,
    ILogger<DatabaseInitializer> logger) : IHostedService
{
    private const string MigrationTableSql = """
        create table if not exists schema_migrations (
            version integer primary key,
            applied_at timestamptz not null default now()
        );
        """;

    private const string SchemaSql = """
        create table cases (
            id uuid primary key,
            pagerduty_incident_id text unique,
            service_id text not null,
            recipe_id text not null,
            team text not null,
            title text not null,
            urgency text not null,
            pagerduty_state text not null,
            status text not null,
            opened_at timestamptz not null,
            updated_at timestamptz not null,
            case_file_version integer not null default 0,
            is_frozen boolean not null default false,
            case_file_json jsonb,
            slack_channel text not null,
            slack_timestamp text,
            labels_json jsonb not null default '{}'::jsonb,
            origin_kind text not null,
            origin_external_id text,
            created_by text,
            input_version bigint not null default 0,
            projected_input_version bigint not null default 0,
            publish_to_slack boolean not null default true,
            acknowledged_at timestamptz,
            resolved_at timestamptz,
            pagerduty_lifecycle_updated_at timestamptz,
            workflow_generation bigint not null default 0,
            projected_workflow_generation bigint not null default 0,
            created_at timestamptz not null default now()
        );

        create unique index ux_cases_origin_external
            on cases(origin_kind, origin_external_id)
            where origin_external_id is not null;

        create table case_origin_receipts (
            idempotency_key text primary key,
            origin_external_id text not null,
            source_event_type text not null,
            payload_hash text not null,
            received_at timestamptz not null default now()
        );

        create table work_items (
            id bigserial primary key,
            case_id uuid not null references cases(id) on delete cascade,
            kind text not null,
            idempotency_key text not null unique,
            due_at timestamptz not null,
            attempts integer not null default 0,
            locked_until timestamptz,
            completed_at timestamptz,
            last_error text,
            target_input_version bigint,
            target_workflow_generation bigint
        );

        create index ix_work_items_due
            on work_items(due_at)
            where completed_at is null;
        create index ix_work_items_case_kind
            on work_items(case_id, kind, target_input_version)
            where completed_at is null;

        create table crumbs (
            case_id uuid not null references cases(id) on delete cascade,
            case_file_version integer not null,
            crumb_id text not null,
            source text not null,
            occurred_at timestamptz not null,
            payload jsonb not null,
            primary key (case_id, case_file_version, crumb_id)
        );

        create table trail_entries (
            case_id uuid not null references cases(id) on delete cascade,
            case_file_version integer not null,
            ordinal integer not null,
            occurred_at timestamptz not null,
            payload jsonb not null,
            primary key (case_id, case_file_version, ordinal)
        );

        create table outbox (
            id bigserial primary key,
            kind text not null,
            payload jsonb not null,
            due_at timestamptz not null default now(),
            attempts integer not null default 0,
            locked_until timestamptz,
            processed_at timestamptz,
            last_error text
        );

        create index ix_outbox_due
            on outbox(due_at)
            where processed_at is null;

        create table patterns (
            id uuid primary key,
            algorithm_version text not null,
            pattern_key text not null,
            service_id text not null,
            recipe_id text not null,
            family_hash text not null,
            representative_exact_hash text not null,
            representative_features jsonb not null,
            lifecycle_state text not null,
            first_seen timestamptz not null,
            last_seen timestamptz not null,
            resolved_at timestamptz,
            occurrence_count integer not null default 0,
            team text not null,
            created_at timestamptz not null default now(),
            updated_at timestamptz not null default now()
        );

        create unique index ux_patterns_team_pattern_key
            on patterns(team, pattern_key);
        create unique index ux_patterns_exact
            on patterns(
                team, algorithm_version, service_id, recipe_id, representative_exact_hash);
        create index ix_patterns_family
            on patterns(team, algorithm_version, service_id, recipe_id, family_hash);
        create index ix_patterns_recent
            on patterns(team, algorithm_version, service_id, recipe_id, last_seen desc);

        create table case_signatures (
            case_id uuid not null,
            algorithm_version text not null,
            signature_stage text not null,
            family_hash text not null,
            exact_hash text not null,
            features_json jsonb not null,
            completeness double precision not null,
            created_at timestamptz not null default now(),
            primary key (case_id, algorithm_version, signature_stage)
        );

        create index ix_case_signatures_exact
            on case_signatures(algorithm_version, exact_hash);
        create index ix_case_signatures_family
            on case_signatures(algorithm_version, family_hash);

        create table pattern_occurrences (
            pattern_id uuid not null references patterns(id) on delete cascade,
            case_id uuid not null,
            algorithm_version text not null,
            pagerduty_incident_id text,
            pagerduty_state text not null,
            match_type text not null,
            similarity_score integer not null,
            matched_features jsonb not null,
            occurred_at timestamptz not null,
            active boolean not null,
            created_at timestamptz not null default now(),
            updated_at timestamptz not null default now(),
            primary key (pattern_id, case_id),
            unique (case_id, algorithm_version)
        );

        create index ix_pattern_occurrences_history
            on pattern_occurrences(pattern_id, occurred_at desc);
        create index ix_pattern_occurrences_updated
            on pattern_occurrences(updated_at);

        create table case_inputs (
            id uuid primary key,
            case_id uuid not null references cases(id) on delete cascade,
            sequence bigint not null,
            input_version bigint not null,
            producer_principal text not null,
            client_crumb_id text not null,
            crumb_kind text not null,
            occurred_at timestamptz not null,
            received_at timestamptz not null default now(),
            category text not null,
            severity text not null,
            summary text not null,
            excerpt text,
            declared_source text,
            source_reference text,
            url text,
            actor text,
            object_type text,
            object_id text,
            attributes_json jsonb not null default '{}'::jsonb,
            trust_level text not null,
            payload_hash text not null,
            supersedes_crumb_id uuid references case_inputs(id),
            retracted_at timestamptz,
            retracted_input_version bigint,
            unique(case_id, producer_principal, client_crumb_id),
            unique(case_id, sequence)
        );

        create index ix_case_inputs_active
            on case_inputs(case_id, sequence)
            where retracted_at is null;
        create index ix_case_inputs_projection
            on case_inputs(case_id, input_version, retracted_input_version, sequence);

        create table crumb_source_snapshots (
            case_id uuid not null references cases(id) on delete cascade,
            snapshot_version bigint not null,
            source text not null,
            collected_at timestamptz not null,
            result_json jsonb not null,
            primary key(case_id, snapshot_version, source)
        );

        create index ix_crumb_source_snapshots_latest
            on crumb_source_snapshots(case_id, snapshot_version desc);

        create table case_command_receipts (
            case_id uuid not null references cases(id) on delete cascade,
            producer_principal text not null,
            command_kind text not null,
            idempotency_key text not null,
            request_hash text not null,
            response_json jsonb not null,
            created_at timestamptz not null default now(),
            primary key(case_id, producer_principal, command_kind, idempotency_key)
        );

        create table case_create_receipts (
            producer_principal text not null,
            idempotency_key text not null,
            request_hash text not null,
            case_id uuid not null references cases(id) on delete cascade,
            response_json jsonb not null,
            created_at timestamptz not null default now(),
            primary key(producer_principal, idempotency_key)
        );

        create table case_progress (
            case_id uuid primary key references cases(id) on delete cascade,
            attempt_id uuid not null,
            revision bigint not null,
            base_case_file_version integer not null,
            updated_at timestamptz not null,
            projection_json jsonb not null
        );

        create table security_audit_events (
            id uuid primary key,
            occurred_at timestamptz not null,
            action text not null,
            outcome text not null,
            actor_id text not null,
            authentication_source text not null,
            actor_teams text[] not null default '{}'::text[],
            target_team text,
            recipe_id text,
            case_id uuid,
            metadata jsonb not null default '{}'::jsonb,
            constraint ck_security_audit_metadata_object
                check (jsonb_typeof(metadata) = 'object')
        );

        create index ix_security_audit_occurred
            on security_audit_events(occurred_at desc, id);
        create index ix_security_audit_case
            on security_audit_events(case_id, occurred_at desc)
            where case_id is not null;

        create function panko_reject_security_audit_mutation()
        returns trigger
        language plpgsql
        as $audit$
        begin
            raise exception 'security audit events are append-only';
        end
        $audit$;

        create trigger security_audit_events_append_only
            before update or delete on security_audit_events
            for each statement
            execute function panko_reject_security_audit_mutation();
        """;

    private static readonly IReadOnlyList<(int Version, string Sql)> Migrations =
    [
        (1, SchemaSql)
    ];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        logger.LogInformation("Panko PostgreSQL connection established");
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var migrationTable = new NpgsqlCommand(
            MigrationTableSql,
            connection,
            transaction))
        {
            await migrationTable.ExecuteNonQueryAsync(cancellationToken);
        }

        var applied = new HashSet<int>();
        await using (var appliedCommand = new NpgsqlCommand(
            "select version from schema_migrations",
            connection,
            transaction))
        await using (var reader = await appliedCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                applied.Add(reader.GetInt32(0));
            }
        }

        foreach (var migration in Migrations.Where(
                     migration => !applied.Contains(migration.Version)))
        {
            await using var command = new NpgsqlCommand(
                migration.Sql,
                connection,
                transaction);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await using var record = new NpgsqlCommand(
                "insert into schema_migrations(version) values ($1)",
                connection,
                transaction);
            record.Parameters.AddWithValue(migration.Version);
            await record.ExecuteNonQueryAsync(cancellationToken);
            logger.LogInformation(
                "Applied Panko database migration {MigrationVersion}",
                migration.Version);
        }

        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(
            "Panko PostgreSQL schema is ready at version {SchemaVersion}",
            Migrations.Max(migration => migration.Version));
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
