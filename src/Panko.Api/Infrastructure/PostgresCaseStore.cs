using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using Panko.Api.Domain;
using Panko.Api.Cases;
using Npgsql;
using NpgsqlTypes;

namespace Panko.Api.Infrastructure;

public sealed class PostgresCaseStore(
    NpgsqlDataSource dataSource,
    TimeProvider timeProvider,
    CaseTelemetry? telemetry = null) :
    ICaseStore
{
    private const string EmptySnapshotSource = "__panko_empty_snapshot__";
    private const int RetainedCrumbSourceSnapshotGenerations = 3;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async Task<(Guid CaseId, bool IsDuplicate)> AcceptOriginEventAsync(
        AcceptCaseOriginEvent originEvent,
        Recipe recipe,
        CaseOriginEventReceipt originReceipt,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var lifecycle = originEvent.LifecycleCrumb;
        var externalId = originEvent.Origin.ExternalId
            ?? throw new InvalidOperationException("Origin event external ID was not supplied.");
        var originKind = originEvent.Origin.Kind.ToString().ToLowerInvariant();
        var pagerDutyIncidentId = originEvent.Origin.Kind == CaseOriginKind.PagerDuty
            ? externalId
            : null;
        var createdBy = originEvent.Origin.Kind == CaseOriginKind.PagerDuty
            ? null
            : originReceipt.ProducerPrincipal;
        var publishToSlack = originEvent.Origin.Kind == CaseOriginKind.PagerDuty;
        var hash = Convert.ToHexStringLower(SHA256.HashData(originReceipt.RawPayload.Span));
        await using var receipt = new NpgsqlCommand("""
            insert into case_origin_receipts(
                idempotency_key, origin_external_id, source_event_type, payload_hash)
            values ($1, $2, $3, $4)
            on conflict (idempotency_key) do nothing
            returning idempotency_key
            """, connection, transaction);
        receipt.Parameters.AddWithValue(originReceipt.IdempotencyKey);
        receipt.Parameters.AddWithValue(externalId);
        receipt.Parameters.AddWithValue(originReceipt.SourceEventType);
        receipt.Parameters.AddWithValue(hash);
        var inserted = await receipt.ExecuteScalarAsync(cancellationToken) is not null;

        if (!inserted)
        {
            await using var existing = new NpgsqlCommand("""
                select case_record.id, case_record.team, receipt.source_event_type, receipt.payload_hash
                from case_origin_receipts receipt
                inner join cases case_record
                    on case_record.origin_kind = $3
                   and case_record.origin_external_id = receipt.origin_external_id
                where receipt.idempotency_key = $1 and receipt.origin_external_id = $2
            """, connection, transaction);
            existing.Parameters.AddWithValue(originReceipt.IdempotencyKey);
            existing.Parameters.AddWithValue(externalId);
            existing.Parameters.AddWithValue(originKind);
            Guid existingId;
            string existingTeam;
            string existingSourceEventType;
            string existingPayloadHash;
            await using (var reader = await existing.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken))
                {
                    throw new InvalidOperationException(
                        $"Origin event '{originReceipt.IdempotencyKey}' was already used for a different Case.");
                }

                existingId = reader.GetGuid(0);
                existingTeam = reader.GetString(1);
                existingSourceEventType = reader.GetString(2);
                existingPayloadHash = reader.GetString(3);
            }

            if (!string.Equals(existingTeam, recipe.Team, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Origin Case '{externalId}' is already owned by a different team.");
            }
            if (!string.Equals(
                    existingSourceEventType,
                    originReceipt.SourceEventType,
                    StringComparison.Ordinal)
                || !string.Equals(existingPayloadHash, hash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Origin event '{originReceipt.IdempotencyKey}' was retried with different content.");
            }

            await ReconcileDuplicateLifecycleProjectionAsync(
                connection,
                transaction,
                existingId,
                originEvent,
                originReceipt,
                hash,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            telemetry?.CrumbsDeduplicated(1);
            return (existingId, true);
        }

        var caseId = Guid.NewGuid();
        bool createdCase;
        var state = originEvent.PagerDutyState;
        var activeStatus = publishToSlack
            ? CaseProgression.Queued
            : CaseProgression.Rebuilding;
        var frozenStatus = publishToSlack
            ? CaseProgression.Finalizing
            : CaseProgression.Resolved;
        await using var upsert = new NpgsqlCommand("""
            insert into cases(
                id, pagerduty_incident_id, service_id, recipe_id, team, title, urgency, pagerduty_state, status,
                opened_at, updated_at, slack_channel, labels_json, is_frozen,
                origin_kind, origin_external_id, created_by, publish_to_slack, acknowledged_at, resolved_at,
                pagerduty_lifecycle_updated_at)
            values ($1, $2, $3, $4, $16, $5, $6, $7, $13, $15, $8, $9, $10::jsonb, $11,
                $17, $18, $19, $20,
                case when $12 = 'pagerduty-incident-acknowledged' then $8 else null end,
                case when $12 = 'pagerduty-incident-resolved' then $8 else null end,
                case when $12 in (
                    'pagerduty-incident-triggered', 'pagerduty-incident-acknowledged', 'pagerduty-incident-escalated',
                    'pagerduty-incident-reassigned', 'pagerduty-incident-resolved', 'pagerduty-incident-reopened')
                    then $8 else null end)
            on conflict (origin_kind, origin_external_id)
                where origin_external_id is not null
            do update set
                pagerduty_incident_id = excluded.pagerduty_incident_id,
                service_id = excluded.service_id,
                recipe_id = excluded.recipe_id,
                title = excluded.title,
                urgency = excluded.urgency,
                opened_at = least(cases.opened_at, excluded.opened_at),
                pagerduty_state = case
                    when excluded.pagerduty_state = 'Unknown' then cases.pagerduty_state
                    when cases.pagerduty_lifecycle_updated_at is null
                        or excluded.pagerduty_lifecycle_updated_at
                            > cases.pagerduty_lifecycle_updated_at
                        then excluded.pagerduty_state
                    else cases.pagerduty_state
                end,
                status = case
                    when excluded.pagerduty_lifecycle_updated_at is null
                        or cases.pagerduty_lifecycle_updated_at is null
                        or excluded.pagerduty_lifecycle_updated_at
                            > cases.pagerduty_lifecycle_updated_at
                        then case when excluded.is_frozen then $14 else $13 end
                    else cases.status
                end,
                updated_at = greatest(cases.updated_at, excluded.updated_at),
                slack_channel = excluded.slack_channel,
                labels_json = excluded.labels_json,
                created_by = coalesce(cases.created_by, excluded.created_by),
                publish_to_slack = excluded.publish_to_slack,
                is_frozen = case
                    when (cases.pagerduty_lifecycle_updated_at is null
                            or excluded.pagerduty_lifecycle_updated_at
                                > cases.pagerduty_lifecycle_updated_at)
                        and $12 = 'pagerduty-incident-resolved' then true
                    when (cases.pagerduty_lifecycle_updated_at is null
                            or excluded.pagerduty_lifecycle_updated_at
                                > cases.pagerduty_lifecycle_updated_at)
                        and $12 in ('pagerduty-incident-triggered', 'pagerduty-incident-reopened') then false
                    else cases.is_frozen
                end,
                acknowledged_at = case
                    when $21
                        and $12 = 'pagerduty-incident-acknowledged'
                        and cases.pagerduty_state = 'Acknowledged'
                        and cases.acknowledged_at is null
                        then excluded.acknowledged_at
                    when (cases.pagerduty_lifecycle_updated_at is null
                            or excluded.pagerduty_lifecycle_updated_at
                                > cases.pagerduty_lifecycle_updated_at)
                        and $12 = 'pagerduty-incident-acknowledged' then excluded.acknowledged_at
                    when (cases.pagerduty_lifecycle_updated_at is null
                            or excluded.pagerduty_lifecycle_updated_at
                                > cases.pagerduty_lifecycle_updated_at)
                        and $12 in ('pagerduty-incident-triggered', 'pagerduty-incident-reopened') then null
                    else cases.acknowledged_at
                end,
                resolved_at = case
                    when $21
                        and $12 = 'pagerduty-incident-resolved'
                        and cases.pagerduty_state = 'Resolved'
                        and cases.resolved_at is null
                        then excluded.resolved_at
                    when (cases.pagerduty_lifecycle_updated_at is null
                            or excluded.pagerduty_lifecycle_updated_at
                                > cases.pagerduty_lifecycle_updated_at)
                        and $12 = 'pagerduty-incident-resolved' then excluded.resolved_at
                    when (cases.pagerduty_lifecycle_updated_at is null
                            or excluded.pagerduty_lifecycle_updated_at
                                > cases.pagerduty_lifecycle_updated_at)
                        and $12 in ('pagerduty-incident-triggered', 'pagerduty-incident-reopened', 'pagerduty-incident-acknowledged') then null
                    else cases.resolved_at
                end,
                pagerduty_lifecycle_updated_at = case
                    when excluded.pagerduty_lifecycle_updated_at is not null
                        and (cases.pagerduty_lifecycle_updated_at is null
                            or excluded.pagerduty_lifecycle_updated_at
                                > cases.pagerduty_lifecycle_updated_at)
                        then excluded.pagerduty_lifecycle_updated_at
                    else cases.pagerduty_lifecycle_updated_at
                end
            where cases.team = excluded.team
            returning id, (xmax = 0) as created_case
            """, connection, transaction);
        upsert.Parameters.AddWithValue(caseId);
        upsert.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)pagerDutyIncidentId ?? DBNull.Value);
        upsert.Parameters.AddWithValue(originEvent.ServiceId);
        upsert.Parameters.AddWithValue(recipe.Id);
        upsert.Parameters.AddWithValue(originEvent.Title);
        upsert.Parameters.AddWithValue(originEvent.Urgency);
        upsert.Parameters.AddWithValue(state.ToString());
        upsert.Parameters.AddWithValue(originEvent.OccurredAt);
        upsert.Parameters.AddWithValue(publishToSlack ? recipe.SlackChannel : string.Empty);
        upsert.Parameters.AddWithValue(JsonSerializer.Serialize(originEvent.Labels, JsonOptions));
        upsert.Parameters.AddWithValue(state == PagerDutyIncidentState.Resolved);
        upsert.Parameters.AddWithValue(lifecycle.Category);
        upsert.Parameters.AddWithValue(activeStatus);
        upsert.Parameters.AddWithValue(frozenStatus);
        upsert.Parameters.AddWithValue(originEvent.ReferenceTime);
        upsert.Parameters.AddWithValue(recipe.Team);
        upsert.Parameters.AddWithValue(originKind);
        upsert.Parameters.AddWithValue(externalId);
        upsert.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)createdBy ?? DBNull.Value);
        upsert.Parameters.AddWithValue(publishToSlack);
        upsert.Parameters.AddWithValue(originReceipt.IsAuthoritativeSnapshot);
        await using (var reader = await upsert.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException(
                    $"Origin Case '{externalId}' is already owned by a different team.");
            }
            caseId = reader.GetGuid(0);
            createdCase = reader.GetBoolean(1);
        }

        long inputVersion;
        await using (var sequenceCommand = new NpgsqlCommand("""
            select coalesce(max(sequence), -1) + 1
            from case_inputs
            where case_id = $1
            """, connection, transaction))
        {
            sequenceCommand.Parameters.AddWithValue(caseId);
            var sequence = (long)(await sequenceCommand.ExecuteScalarAsync(cancellationToken) ?? 0L);
            inputVersion = sequence;
            var crumbId = CaseInputBoundary.DeterministicCrumbId(
                caseId,
                originReceipt.ProducerPrincipal,
                lifecycle.ClientCrumbId);
            await using var canonicalCrumb = new NpgsqlCommand("""
                insert into case_inputs(
                    id, case_id, sequence, input_version, producer_principal, client_crumb_id,
                    crumb_kind, occurred_at, category, severity, summary, excerpt, declared_source,
                    source_reference, url, actor, object_type, object_id, attributes_json,
                    trust_level, payload_hash)
                values ($1, $2, $3, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12,
                    $13, $14, $15, $16, $17, $18::jsonb, 'collected', $19)
                """, connection, transaction);
            canonicalCrumb.Parameters.AddWithValue(crumbId);
            canonicalCrumb.Parameters.AddWithValue(caseId);
            canonicalCrumb.Parameters.AddWithValue(sequence);
            canonicalCrumb.Parameters.AddWithValue(originReceipt.ProducerPrincipal);
            canonicalCrumb.Parameters.AddWithValue(lifecycle.ClientCrumbId);
            canonicalCrumb.Parameters.AddWithValue(lifecycle.Kind.ToString().ToLowerInvariant());
            canonicalCrumb.Parameters.AddWithValue(originEvent.OccurredAt);
            canonicalCrumb.Parameters.AddWithValue(lifecycle.Category);
            canonicalCrumb.Parameters.AddWithValue(lifecycle.Severity);
            canonicalCrumb.Parameters.AddWithValue(lifecycle.Summary);
            canonicalCrumb.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)lifecycle.Excerpt ?? DBNull.Value);
            canonicalCrumb.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)lifecycle.DeclaredSource ?? DBNull.Value);
            canonicalCrumb.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)lifecycle.SourceReference ?? DBNull.Value);
            canonicalCrumb.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)lifecycle.Url ?? DBNull.Value);
            canonicalCrumb.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)lifecycle.Actor ?? DBNull.Value);
            canonicalCrumb.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)lifecycle.ObjectType ?? DBNull.Value);
            canonicalCrumb.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)lifecycle.ObjectId ?? DBNull.Value);
            canonicalCrumb.Parameters.AddWithValue(JsonSerializer.Serialize(
                lifecycle.Attributes ?? new Dictionary<string, JsonElement>(),
                JsonOptions));
            canonicalCrumb.Parameters.AddWithValue(hash);
            await canonicalCrumb.ExecuteNonQueryAsync(cancellationToken);

            await using var versionUpdate = new NpgsqlCommand(
                "update cases set input_version = $2 where id = $1",
                connection,
                transaction);
            versionUpdate.Parameters.AddWithValue(caseId);
            versionUpdate.Parameters.AddWithValue(sequence);
            await versionUpdate.ExecuteNonQueryAsync(cancellationToken);
        }

        var now = timeProvider.GetUtcNow();
        if (originEvent.Origin.Kind == CaseOriginKind.PagerDuty)
        {
            var delays = lifecycle.Category is "pagerduty-incident-triggered" or "pagerduty-incident-reopened"
                ? new[] { TimeSpan.Zero, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(90) }
                : new[] { TimeSpan.Zero };
            foreach (var delay in delays)
            {
                await using var work = new NpgsqlCommand("""
                    insert into work_items(case_id, kind, idempotency_key, due_at)
                    values ($1, $2, $3, $4)
                    on conflict (idempotency_key) do nothing
                    """, connection, transaction);
                work.Parameters.AddWithValue(caseId);
                work.Parameters.AddWithValue(CaseWorkKinds.Build);
                work.Parameters.AddWithValue($"{originReceipt.IdempotencyKey}:{(int)delay.TotalSeconds}");
                work.Parameters.AddWithValue(now + delay);
                await work.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        else
        {
            await using var work = new NpgsqlCommand("""
                insert into work_items(
                    case_id, kind, idempotency_key, due_at, target_input_version)
                values ($1, $2, $3, $4, $5)
                on conflict (idempotency_key) do nothing
                """, connection, transaction);
            work.Parameters.AddWithValue(caseId);
            work.Parameters.AddWithValue(CaseWorkKinds.Project);
            work.Parameters.AddWithValue(
                $"{CaseWorkKinds.Project}:{caseId:D}:{inputVersion}");
            work.Parameters.AddWithValue(now);
            work.Parameters.AddWithValue(inputVersion);
            await work.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        if (createdCase)
        {
            telemetry?.CaseCreated(originKind);
        }
        telemetry?.CrumbsAccepted(1);
        return (caseId, false);
    }

    public async Task<CaseRecord?> GetCaseAsync(Guid caseId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            select id, pagerduty_incident_id, service_id, recipe_id, title, urgency, pagerduty_state, opened_at,
                   updated_at, case_file_version, status, is_frozen, case_file_json::text, slack_channel, slack_timestamp,
                   labels_json::text, origin_kind, origin_external_id, created_by, input_version,
                   projected_input_version, publish_to_slack, acknowledged_at, resolved_at, team
            from cases where id = $1
            """);
        command.Parameters.AddWithValue(caseId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadCase(reader);
    }

    public async Task<CaseFile?> GetCaseFileAsync(Guid caseId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("select case_file_json::text from cases where id = $1");
        command.Parameters.AddWithValue(caseId);
        var json = await command.ExecuteScalarAsync(cancellationToken) as string;
        return string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<CaseFile>(json, JsonOptions);
    }

    public async Task<CaseProjectionInputs> GetProjectionInputsAsync(
        Guid caseId,
        long targetInputVersion,
        CancellationToken cancellationToken)
    {
        if (targetInputVersion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetInputVersion));
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            System.Data.IsolationLevel.RepeatableRead,
            cancellationToken);
        var inputs = new List<CaseInput>();
        await using (var inputsCommand = new NpgsqlCommand("""
            select id, case_id, sequence, input_version, producer_principal, client_crumb_id,
                   crumb_kind, occurred_at, received_at, category, severity, summary, excerpt,
                   declared_source, source_reference, url, actor, object_type, object_id,
                   attributes_json::text, trust_level, payload_hash, supersedes_crumb_id,
                   retracted_at, retracted_input_version
            from case_inputs
            where case_id = $1
              and input_version <= $2
              and (retracted_input_version is null or retracted_input_version > $2)
            order by sequence
            """, connection, transaction))
        {
            inputsCommand.Parameters.AddWithValue(caseId);
            inputsCommand.Parameters.AddWithValue(targetInputVersion);
            await using var reader = await inputsCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                inputs.Add(ReadProjectionInput(reader));
            }
        }

        var crumbSourceResults = new List<CrumbSourceResult>();
        await using (var snapshots = new NpgsqlCommand("""
            select source, result_json::text
            from crumb_source_snapshots
            where case_id = $1
              and snapshot_version = (
                  select max(snapshot_version)
                  from crumb_source_snapshots
                  where case_id = $1)
            order by source
            """, connection, transaction))
        {
            snapshots.Parameters.AddWithValue(caseId);
            await using var reader = await snapshots.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(0), EmptySnapshotSource, StringComparison.Ordinal))
                {
                    continue;
                }
                crumbSourceResults.Add(JsonSerializer.Deserialize<CrumbSourceResult>(reader.GetString(1), JsonOptions)
                    ?? throw new InvalidOperationException("A persisted Crumb-source snapshot is invalid."));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new CaseProjectionInputs(inputs, crumbSourceResults);
    }

    public async Task<CaseProgress?> GetProgressAsync(
        Guid caseId,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            select attempt_id, revision, base_case_file_version, projection_json::text
            from case_progress
            where case_id = $1
            """);
        command.Parameters.AddWithValue(caseId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var progress = JsonSerializer.Deserialize<CaseProgress>(reader.GetString(3), JsonOptions)
            ?? throw new JsonException($"Case progress for Case '{caseId}' was empty.");
        return progress with
        {
            AttemptId = reader.GetGuid(0),
            Revision = reader.GetInt64(1),
            BaseCaseFileVersion = reader.GetInt32(2)
        };
    }

    public async Task<long?> BeginProgressAsync(
        CaseProgress progress,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await ProgressBaseMatchesForUpdateAsync(
                connection,
                transaction,
                progress.CaseId,
                progress.BaseCaseFileVersion,
                requireCollecting: true,
                cancellationToken)
            || await ExistingProgressStartedLaterAsync(
                connection, transaction, progress, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var json = JsonSerializer.Serialize(progress, JsonOptions);
        await using var command = new NpgsqlCommand("""
            insert into case_progress(
                case_id, attempt_id, revision, base_case_file_version, updated_at, projection_json)
            values ($1, $2, 1, $3, $4, $5::jsonb)
            on conflict (case_id) do update set
                attempt_id = excluded.attempt_id,
                revision = 1,
                base_case_file_version = excluded.base_case_file_version,
                updated_at = excluded.updated_at,
                projection_json = excluded.projection_json
            returning revision
            """, connection, transaction);
        command.Parameters.AddWithValue(progress.CaseId);
        command.Parameters.AddWithValue(progress.AttemptId);
        command.Parameters.AddWithValue(progress.BaseCaseFileVersion);
        command.Parameters.AddWithValue(progress.UpdatedAt);
        command.Parameters.AddWithValue(json);
        var revision = (long?)await command.ExecuteScalarAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return revision;
    }

    public async Task<long?> UpdateProgressAsync(
        CaseProgress progress,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await ProgressBaseMatchesForUpdateAsync(
                connection,
                transaction,
                progress.CaseId,
                progress.BaseCaseFileVersion,
                requireCollecting: false,
                cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var json = JsonSerializer.Serialize(progress, JsonOptions);
        await using var command = new NpgsqlCommand("""
            update case_progress set
                revision = revision + 1,
                updated_at = $5,
                projection_json = $6::jsonb
            where case_id = $1
              and attempt_id = $2
              and base_case_file_version = $3
              and revision = $4
            returning revision
            """, connection, transaction);
        command.Parameters.AddWithValue(progress.CaseId);
        command.Parameters.AddWithValue(progress.AttemptId);
        command.Parameters.AddWithValue(progress.BaseCaseFileVersion);
        command.Parameters.AddWithValue(progress.Revision);
        command.Parameters.AddWithValue(progress.UpdatedAt);
        command.Parameters.AddWithValue(json);
        var revision = (long?)await command.ExecuteScalarAsync(cancellationToken);
        if (revision is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        await transaction.CommitAsync(cancellationToken);
        return revision;
    }

    public Task<int> SaveCaseFileAsync(
        CaseRecord caseRecord,
        CaseFile caseFile,
        CancellationToken cancellationToken) =>
        SaveCaseFileAsync(caseRecord, caseFile, null, null, cancellationToken);

    public Task<int> SaveCaseFileAsync(
        CaseRecord caseRecord,
        CaseFile caseFile,
        Guid? progressAttemptId,
        CancellationToken cancellationToken) =>
        SaveCaseFileAsync(caseRecord, caseFile, progressAttemptId, null, cancellationToken);

    public async Task<int> SaveCaseFileAsync(
        CaseRecord caseRecord,
        CaseFile caseFile,
        Guid? progressAttemptId,
        IReadOnlyList<CrumbSourceResult>? crumbSourceSnapshot,
        CancellationToken cancellationToken)
    {
        ValidateCaseFileProjection(caseRecord, caseFile);
        ValidateCrumbSourceSnapshot(crumbSourceSnapshot);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var nextVersion = caseRecord.Version + 1;
        var versionedCaseFile = caseFile with { CaseFileVersion = nextVersion };
        var json = JsonSerializer.Serialize(versionedCaseFile, JsonOptions);

        if (progressAttemptId is not null
            && !await ConsumeProgressAttemptAsync(
                connection,
                transaction,
                caseRecord.Id,
                caseRecord.Version,
                progressAttemptId.Value,
                cancellationToken))
        {
            throw new InvalidOperationException(
                "The Case progress attempt was superseded before its canonical Case File could be committed.");
        }

        await using var update = new NpgsqlCommand("""
            update cases set case_file_json = $2::jsonb, case_file_version = $3, status = $4, updated_at = $5,
                projected_input_version = $7
            where id = $1 and case_file_version = $6 and input_version = $8
            """, connection, transaction);
        update.Parameters.AddWithValue(caseRecord.Id);
        update.Parameters.AddWithValue(json);
        update.Parameters.AddWithValue(nextVersion);
        update.Parameters.AddWithValue(caseFile.Status);
        update.Parameters.AddWithValue(caseFile.UpdatedAt);
        update.Parameters.AddWithValue(caseRecord.Version);
        update.Parameters.AddWithValue(caseFile.ProjectedInputVersion);
        update.Parameters.AddWithValue(caseRecord.InputVersion);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("Case File was updated concurrently; the work item will be retried.");
        }

        if (crumbSourceSnapshot is not null)
        {
            await PersistCrumbSourceSnapshotGenerationAsync(
                connection,
                transaction,
                caseRecord.Id,
                crumbSourceSnapshot,
                cancellationToken);
        }

        await using (var clearProgress = new NpgsqlCommand("""
            delete from case_progress
            where case_id = $1 and base_case_file_version < $2
            """, connection, transaction))
        {
            clearProgress.Parameters.AddWithValue(caseRecord.Id);
            clearProgress.Parameters.AddWithValue(nextVersion);
            await clearProgress.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var crumb in caseFile.Crumbs)
        {
            await using var insert = new NpgsqlCommand("""
                insert into crumbs(case_id, case_file_version, crumb_id, source, occurred_at, payload)
                values ($1, $2, $3, $4, $5, $6::jsonb)
                """, connection, transaction);
            insert.Parameters.AddWithValue(caseRecord.Id);
            insert.Parameters.AddWithValue(nextVersion);
            insert.Parameters.AddWithValue(crumb.Id);
            insert.Parameters.AddWithValue(crumb.Source);
            insert.Parameters.AddWithValue(crumb.OccurredAt);
            insert.Parameters.AddWithValue(JsonSerializer.Serialize(crumb, JsonOptions));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        for (var index = 0; index < caseFile.Trail.Count; index++)
        {
            var trailEntry = caseFile.Trail[index];
            await using var insert = new NpgsqlCommand("""
                insert into trail_entries(case_id, case_file_version, ordinal, occurred_at, payload)
                values ($1, $2, $3, $4, $5::jsonb)
                """, connection, transaction);
            insert.Parameters.AddWithValue(caseRecord.Id);
            insert.Parameters.AddWithValue(nextVersion);
            insert.Parameters.AddWithValue(index);
            insert.Parameters.AddWithValue(trailEntry.OccurredAt);
            insert.Parameters.AddWithValue(JsonSerializer.Serialize(trailEntry, JsonOptions));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        var outboxPayload = JsonSerializer.Serialize(new { caseId = caseRecord.Id, caseFileVersion = nextVersion });
        if (caseRecord.PublishToSlack && !string.IsNullOrWhiteSpace(caseRecord.SlackChannel))
        {
            await using var outbox = new NpgsqlCommand("""
                insert into outbox(kind, payload) values ($1, $2::jsonb)
                """, connection, transaction);
            outbox.Parameters.AddWithValue(CaseOutboxKinds.SlackCaseFile);
            outbox.Parameters.AddWithValue(outboxPayload);
            await outbox.ExecuteNonQueryAsync(cancellationToken);

            if (CaseProgression.NeedsStuckNotification(caseFile.Status))
            {
                await using var stuckCheck = new NpgsqlCommand("""
                    insert into outbox(kind, payload, due_at)
                    values ($1, $2::jsonb, now() + interval '1 minute')
                    """, connection, transaction);
                stuckCheck.Parameters.AddWithValue(CaseOutboxKinds.SlackCaseFile);
                stuckCheck.Parameters.AddWithValue(outboxPayload);
                await stuckCheck.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        await transaction.CommitAsync(cancellationToken);
        return nextVersion;
    }

    public async Task SetStatusAsync(Guid caseId, string status, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "update cases set status = $2, updated_at = now() where id = $1");
        command.Parameters.AddWithValue(caseId);
        command.Parameters.AddWithValue(status);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> RebuildCaseAsync(
        Guid caseId,
        string slackChannel,
        string slackTimestamp,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slackChannel);
        ArgumentException.ThrowIfNullOrWhiteSpace(slackTimestamp);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using var caseCommand = new NpgsqlCommand("""
            select status
            from cases
            where id = $1 and slack_channel = $2 and slack_timestamp = $3
            for update
            """, connection, transaction);
        caseCommand.Parameters.AddWithValue(caseId);
        caseCommand.Parameters.AddWithValue(slackChannel);
        caseCommand.Parameters.AddWithValue(slackTimestamp);
        var currentStatus = await caseCommand.ExecuteScalarAsync(cancellationToken) as string;
        if (currentStatus is null || !CaseProgression.CanRequestRebuild(currentStatus))
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await using var retire = new NpgsqlCommand("""
            update work_items
            set completed_at = now(), locked_until = null
            where case_id = $1 and completed_at is null
            """, connection, transaction);
        retire.Parameters.AddWithValue(caseId);
        await retire.ExecuteNonQueryAsync(cancellationToken);

        await using var update = new NpgsqlCommand(
            "update cases set status = $2, updated_at = now() where id = $1",
            connection, transaction);
        update.Parameters.AddWithValue(caseId);
        update.Parameters.AddWithValue(CaseProgression.Queued);
        await update.ExecuteNonQueryAsync(cancellationToken);

        await using (var clearProgress = new NpgsqlCommand(
            "delete from case_progress where case_id = $1",
            connection,
            transaction))
        {
            clearProgress.Parameters.AddWithValue(caseId);
            await clearProgress.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var work = new NpgsqlCommand("""
            insert into work_items(case_id, kind, idempotency_key, due_at)
            values ($1, $2, $3, now())
            """, connection, transaction);
        work.Parameters.AddWithValue(caseId);
        work.Parameters.AddWithValue(CaseWorkKinds.Build);
        work.Parameters.AddWithValue($"manual-rebuild:{caseId:N}:{Guid.NewGuid():N}");
        await work.ExecuteNonQueryAsync(cancellationToken);

        await using var stuckCheck = new NpgsqlCommand("""
            insert into outbox(kind, payload, due_at)
            values ($1, jsonb_build_object('caseId', $2), now() + interval '1 minute')
            """, connection, transaction);
        stuckCheck.Parameters.AddWithValue(CaseOutboxKinds.SlackCaseFile);
        stuckCheck.Parameters.AddWithValue(caseId);
        await stuckCheck.ExecuteNonQueryAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task SetSlackTimestampAsync(Guid caseId, string timestamp, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "update cases set slack_timestamp = $2 where id = $1 and slack_timestamp is null");
        command.Parameters.AddWithValue(caseId);
        command.Parameters.AddWithValue(timestamp);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("delete from cases where updated_at < $1");
        command.Parameters.AddWithValue(cutoff);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void ValidateCaseFileProjection(
        CaseRecord caseRecord,
        CaseFile caseFile)
    {
        if (caseFile.CaseId != caseRecord.Id
            || caseFile.InputVersion != caseRecord.InputVersion
            || caseFile.ProjectedInputVersion != caseRecord.InputVersion)
        {
            throw new InvalidOperationException(
                "The Case File does not project the exact Case input version captured by this commit attempt.");
        }
    }

    private static void ValidateCrumbSourceSnapshot(IReadOnlyList<CrumbSourceResult>? crumbSourceSnapshot)
    {
        if (crumbSourceSnapshot is null)
        {
            return;
        }

        var duplicate = crumbSourceSnapshot
            .GroupBy(result => result.Source, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"Crumb-source snapshot contains source '{duplicate}' more than once.",
                nameof(crumbSourceSnapshot));
        }
        if (crumbSourceSnapshot.Any(result =>
                string.IsNullOrWhiteSpace(result.Source)
                || string.Equals(result.Source, EmptySnapshotSource, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "Crumb-source snapshot sources must be non-empty and may not use Panko's reserved empty-snapshot source.",
                nameof(crumbSourceSnapshot));
        }
    }

    private async Task PersistCrumbSourceSnapshotGenerationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid caseId,
        IReadOnlyList<CrumbSourceResult> crumbSourceSnapshot,
        CancellationToken cancellationToken)
    {
        long currentVersion;
        await using (var current = new NpgsqlCommand("""
            select coalesce(max(snapshot_version), 0)
            from crumb_source_snapshots
            where case_id = $1
            """, connection, transaction))
        {
            current.Parameters.AddWithValue(caseId);
            currentVersion = (long)(await current.ExecuteScalarAsync(cancellationToken) ?? 0L);
        }
        var snapshotVersion = checked(currentVersion + 1);
        var collectedAt = timeProvider.GetUtcNow();
        var results = crumbSourceSnapshot.Count == 0
            ? new[] { CrumbSourceResult.Excluded(EmptySnapshotSource) }
            : crumbSourceSnapshot;
        foreach (var result in results)
        {
            await using var insert = new NpgsqlCommand("""
                insert into crumb_source_snapshots(
                    case_id, snapshot_version, source, collected_at, result_json)
                values ($1, $2, $3, $4, $5::jsonb)
                """, connection, transaction);
            insert.Parameters.AddWithValue(caseId);
            insert.Parameters.AddWithValue(snapshotVersion);
            insert.Parameters.AddWithValue(result.Source);
            insert.Parameters.AddWithValue(collectedAt);
            insert.Parameters.AddWithValue(JsonSerializer.Serialize(result, JsonOptions));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var expire = new NpgsqlCommand("""
            delete from crumb_source_snapshots
            where case_id = $1 and snapshot_version <= $2 - $3
            """, connection, transaction);
        expire.Parameters.AddWithValue(caseId);
        expire.Parameters.AddWithValue(snapshotVersion);
        expire.Parameters.AddWithValue(RetainedCrumbSourceSnapshotGenerations);
        await expire.ExecuteNonQueryAsync(cancellationToken);
    }

    private static CaseInput ReadProjectionInput(NpgsqlDataReader reader)
    {
        var attributes = JsonNode.Parse(reader.GetString(19)) as JsonObject ?? [];
        return new CaseInput(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetString(4),
            reader.GetString(5),
            Enum.TryParse<Panko.Contracts.SubmittedCrumbKind>(
                reader.GetString(6),
                ignoreCase: true,
                out var type)
                ? type
                : throw new InvalidOperationException("A persisted Case input has an invalid Crumb type."),
            reader.GetFieldValue<DateTimeOffset>(7),
            reader.GetFieldValue<DateTimeOffset>(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetString(14),
            reader.IsDBNull(15) ? null : reader.GetString(15),
            reader.IsDBNull(16) ? null : reader.GetString(16),
            reader.IsDBNull(17) ? null : reader.GetString(17),
            reader.IsDBNull(18) ? null : reader.GetString(18),
            attributes,
            reader.GetString(20),
            reader.GetString(21),
            reader.IsDBNull(22) ? null : reader.GetGuid(22),
            reader.IsDBNull(23) ? null : reader.GetFieldValue<DateTimeOffset>(23),
            reader.IsDBNull(24) ? null : reader.GetInt64(24));
    }

    private static CaseRecord ReadCase(NpgsqlDataReader reader)
    {
        var labels = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(15), JsonOptions)
            ?? new Dictionary<string, string>();
        var pagerDutyIncidentId = reader.IsDBNull(1) ? null : reader.GetString(1);
        var originKind = Enum.TryParse<CaseOriginKind>(reader.GetString(16), true, out var parsedOrigin)
            ? parsedOrigin
            : CaseOriginKind.PagerDuty;
        return new CaseRecord(
            reader.GetGuid(0), pagerDutyIncidentId, reader.GetString(2), reader.GetString(3), reader.GetString(4),
            reader.GetString(5), Enum.TryParse<PagerDutyIncidentState>(reader.GetString(6), out var state) ? state : PagerDutyIncidentState.Unknown,
            reader.GetFieldValue<DateTimeOffset>(7), reader.GetFieldValue<DateTimeOffset>(8), reader.GetInt32(9),
            reader.GetString(10), reader.GetBoolean(11), reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.GetString(13), reader.IsDBNull(14) ? null : reader.GetString(14), labels)
        {
            Origin = new CaseOrigin(
                originKind,
                reader.IsDBNull(17) ? pagerDutyIncidentId : reader.GetString(17)),
            CreatedBy = reader.IsDBNull(18) ? null : reader.GetString(18),
            InputVersion = reader.GetInt64(19),
            ProjectedInputVersion = reader.GetInt64(20),
            PublishToSlack = reader.GetBoolean(21),
            AcknowledgedAt = reader.IsDBNull(22) ? null : reader.GetFieldValue<DateTimeOffset>(22),
            ResolvedAt = reader.IsDBNull(23) ? null : reader.GetFieldValue<DateTimeOffset>(23),
            Team = reader.GetString(24)
        };
    }

    private static async Task ReconcileDuplicateLifecycleProjectionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid caseId,
        AcceptCaseOriginEvent originEvent,
        CaseOriginEventReceipt originReceipt,
        string payloadHash,
        CancellationToken cancellationToken)
    {
        var lifecycle = originEvent.LifecycleCrumb;
        await using var command = new NpgsqlCommand("""
            update cases as case_record
            set acknowledged_at = case
                    when $4 = 'pagerduty-incident-acknowledged'
                        and case_record.pagerduty_state = 'Acknowledged'
                        and case_record.acknowledged_at is null
                        then $5
                    else case_record.acknowledged_at
                end,
                resolved_at = case
                    when $4 = 'pagerduty-incident-resolved'
                        and case_record.pagerduty_state = 'Resolved'
                        and case_record.resolved_at is null
                        then $5
                    else case_record.resolved_at
                end,
                pagerduty_lifecycle_updated_at = case
                    when case_record.pagerduty_lifecycle_updated_at is null
                        and (($4 = 'pagerduty-incident-acknowledged' and case_record.pagerduty_state = 'Acknowledged')
                            or ($4 = 'pagerduty-incident-resolved' and case_record.pagerduty_state = 'Resolved'))
                        then $5
                    else case_record.pagerduty_lifecycle_updated_at
                end
            where case_record.id = $1
              and exists (
                  select 1
                  from case_origin_receipts as receipt
                  where receipt.idempotency_key = $2
                    and receipt.origin_external_id = $3
                    and receipt.source_event_type = $6
                    and receipt.payload_hash = $7
                    and not exists (
                        select 1
                        from case_inputs as current_event
                        inner join case_inputs as newer
                            on newer.case_id = current_event.case_id
                           and newer.sequence > current_event.sequence
                        where current_event.case_id = case_record.id
                          and current_event.producer_principal = $8
                          and current_event.client_crumb_id = $2
                          and newer.category in (
                              'pagerduty-incident-triggered', 'pagerduty-incident-acknowledged', 'pagerduty-incident-escalated',
                              'pagerduty-incident-reassigned', 'pagerduty-incident-resolved', 'pagerduty-incident-reopened')))
            """, connection, transaction);
        command.Parameters.AddWithValue(caseId);
        command.Parameters.AddWithValue(originReceipt.IdempotencyKey);
        command.Parameters.AddWithValue(originEvent.Origin.ExternalId!);
        command.Parameters.AddWithValue(lifecycle.Category);
        command.Parameters.AddWithValue(originEvent.OccurredAt);
        command.Parameters.AddWithValue(originReceipt.SourceEventType);
        command.Parameters.AddWithValue(payloadHash);
        command.Parameters.AddWithValue(originReceipt.ProducerPrincipal);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> ProgressBaseMatchesForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid caseId,
        int expectedVersion,
        bool requireCollecting,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            select case_file_version, status
            from cases
            where id = $1
            for update
            """, connection, transaction);
        command.Parameters.AddWithValue(caseId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            && reader.GetInt32(0) == expectedVersion
            && (!requireCollecting
                || string.Equals(reader.GetString(1), CaseProgression.Collecting, StringComparison.Ordinal));
    }

    private static async Task<bool> ExistingProgressStartedLaterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CaseProgress candidate,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            select base_case_file_version, projection_json::text
            from case_progress
            where case_id = $1
            """, connection, transaction);
        command.Parameters.AddWithValue(candidate.CaseId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.GetInt32(0) < candidate.BaseCaseFileVersion)
        {
            return false;
        }
        var existing = JsonSerializer.Deserialize<CaseProgress>(reader.GetString(1), JsonOptions)
            ?? throw new JsonException(
                $"Case progress for Case '{candidate.CaseId}' was empty.");
        return existing.StartedAt > candidate.StartedAt;
    }

    private static async Task<bool> ConsumeProgressAttemptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid caseId,
        int expectedCaseFileVersion,
        Guid attemptId,
        CancellationToken cancellationToken)
    {
        await using (var caseCommand = new NpgsqlCommand("""
            select case_file_version
            from cases
            where id = $1
            for update
            """, connection, transaction))
        {
            caseCommand.Parameters.AddWithValue(caseId);
            if (await caseCommand.ExecuteScalarAsync(cancellationToken) is not int storedCaseFileVersion
                || storedCaseFileVersion != expectedCaseFileVersion)
            {
                return false;
            }
        }

        await using var progress = new NpgsqlCommand("""
            delete from case_progress
            where case_id = $1
              and attempt_id = $2
              and base_case_file_version = $3
            returning revision
            """, connection, transaction);
        progress.Parameters.AddWithValue(caseId);
        progress.Parameters.AddWithValue(attemptId);
        progress.Parameters.AddWithValue(expectedCaseFileVersion);
        return await progress.ExecuteScalarAsync(cancellationToken) is long;
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}
