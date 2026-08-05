using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Panko.Api.Domain;
using Panko.Api.Cases;
using Panko.Api.Security;
using Npgsql;
using NpgsqlTypes;
using SubmittedCrumbKind = Panko.Contracts.SubmittedCrumbKind;

namespace Panko.Api.Cases;

/// <summary>
/// Durable persistence seam for first-class Case work. Commands remain responsible for
/// authorisation and input normalisation; this adapter owns the transactional and concurrency invariants.
/// </summary>
public interface ICaseInputStore
{
    Task<CreateCaseResult> CreateAsync(
        CaseRecord proposed,
        CaseFile initialCaseFile,
        CaseInput createdInput,
        string producerPrincipal,
        string idempotencyKey,
        string requestHash,
        CancellationToken cancellationToken);

    Task<CaseRecord?> GetCaseAsync(Guid caseId, CancellationToken cancellationToken);

    Task<CaseFile?> GetCaseFileAsync(Guid caseId, CancellationToken cancellationToken);

    Task<AppendCrumbsResult> AppendAsync(
        Guid caseId,
        string producerPrincipal,
        string batchId,
        string requestHash,
        IReadOnlyList<NormalizedCrumb> crumbs,
        int maximumCrumbsPerCase,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CaseInput>> ListInputsAsync(
        Guid caseId,
        long? throughInputVersion,
        bool includeInactive,
        CancellationToken cancellationToken);

    Task<bool> QueueProjectionAsync(
        Guid caseId,
        long targetInputVersion,
        CancellationToken cancellationToken);

    Task<bool> QueueRefreshAsync(
        Guid caseId,
        long targetInputVersion,
        CancellationToken cancellationToken);

    Task CloseAsync(
        Guid caseId,
        string producerPrincipal,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CaseRecord>> ListRecentAsync(int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<CaseRecord>> ListRecentAsync(
        int limit,
        TeamAccessScope scope,
        CancellationToken cancellationToken) => ListRecentAsync(limit, cancellationToken);

    /// <summary>
    /// Persists one complete Crumb-source refresh generation (including an empty generation) and
    /// atomically schedules projection work that cannot be swallowed by same-input coalescing.
    /// </summary>
    Task<long> SaveCrumbSourceSnapshotsAsync(
        Guid caseId,
        IReadOnlyList<CrumbSourceResult> results,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CrumbSourceResult>> GetLatestCrumbSourceResultsAsync(
        Guid caseId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Commits a projection only when the Case File version is unchanged and no newer input target has
    /// already been projected. The returned value is null when the projection lost that race.
    /// </summary>
    Task<int?> CommitProjectionAsync(
        CaseRecord expected,
        CaseFile caseFile,
        long targetInputVersion,
        CancellationToken cancellationToken,
        long? targetWorkflowGeneration = null);

    /// <summary>
    /// Commits analysis only while the exact projected input on which it was based is still current.
    /// Analysis never advances projected_input_version.
    /// </summary>
    Task<int?> CommitAnalysisAsync(
        CaseRecord expected,
        CaseFile caseFile,
        long projectedInputVersion,
        CancellationToken cancellationToken,
        long? targetWorkflowGeneration = null);
}

public sealed class PostgresCaseInputStore(
    NpgsqlDataSource dataSource,
    TimeProvider timeProvider,
    CaseTelemetry? telemetry = null) : ICaseInputStore
{
    private const string EmptySnapshotSource = "__panko_empty_snapshot__";
    private const int RetainedCrumbSourceSnapshotGenerations = 3;
    private const string CaseColumns = """
        id, pagerduty_incident_id, service_id, recipe_id, title, urgency, pagerduty_state, opened_at,
        updated_at, case_file_version, status, is_frozen, case_file_json::text, slack_channel, slack_timestamp,
        labels_json::text, origin_kind, origin_external_id, created_by, input_version,
        projected_input_version, publish_to_slack, acknowledged_at, resolved_at, team,
        workflow_generation, projected_workflow_generation
        """;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters =
        {
            new TrailCandidateJsonConverter(),
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
        }
    };

    public async Task<CreateCaseResult> CreateAsync(
        CaseRecord proposed,
        CaseFile initialCaseFile,
        CaseInput createdInput,
        string producerPrincipal,
        string idempotencyKey,
        string requestHash,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposed);
        ArgumentNullException.ThrowIfNull(initialCaseFile);
        ArgumentNullException.ThrowIfNull(createdInput);
        RequireValue(producerPrincipal, nameof(producerPrincipal));
        RequireValue(idempotencyKey, nameof(idempotencyKey));
        RequireValue(requestHash, nameof(requestHash));

        if (proposed.Origin.Kind != CaseOriginKind.Agent
            || proposed.Origin.ExternalId is not null
            || proposed.PagerDutyIncidentId is not null)
        {
            throw new CaseValidationException(
                "Agent-created Cases must use the agent origin without an external or PagerDuty identity.");
        }
        if (createdInput.SupersedesCrumbId is not null
            || createdInput.RetractedAt is not null
            || createdInput.RetractedInputVersion is not null)
        {
            throw new CaseValidationException(
                "The Case-created Crumb cannot supersede or retract another Crumb.");
        }
        RequireValue(createdInput.ClientCrumbId, nameof(createdInput.ClientCrumbId));
        RequireValue(createdInput.PayloadHash, nameof(createdInput.PayloadHash));
        RequireValue(createdInput.TrustLevel, nameof(createdInput.TrustLevel));

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // The receipt cannot be inserted before its Case foreign key exists. A transaction-scoped advisory
        // lock gives competing first attempts a deterministic point at which to observe the winner.
        await using (var idempotencyLock = new NpgsqlCommand(
            "select pg_advisory_xact_lock(hashtextextended($1, 0))", connection, transaction))
        {
            idempotencyLock.Parameters.AddWithValue($"{producerPrincipal}\u001f{idempotencyKey}");
            await idempotencyLock.ExecuteScalarAsync(cancellationToken);
        }

        var receipt = await ReadCreateReceiptAsync(
            connection, transaction, producerPrincipal, idempotencyKey, cancellationToken);
        if (receipt is not null)
        {
            EnsureMatchingHash(requestHash, receipt.Value.RequestHash, "create", idempotencyKey);
            var existing = await ReadCaseAsync(
                    connection, transaction, receipt.Value.CaseId, forUpdate: false, cancellationToken)
                ?? throw new InvalidOperationException(
                    "A Case creation receipt refers to a missing Case.");
            await transaction.CommitAsync(cancellationToken);
            return new CreateCaseResult(existing, Duplicate: true);
        }

        var now = timeProvider.GetUtcNow();
        var origin = new CaseOrigin(CaseOriginKind.Agent, null);
        var caseFileUpdatedAt = initialCaseFile.UpdatedAt == default ? now : initialCaseFile.UpdatedAt;
        var storedCaseFile = initialCaseFile with
        {
            CaseId = proposed.Id,
            PagerDutyIncidentId = null,
            ServiceId = proposed.ServiceId,
            RecipeId = proposed.RecipeId,
            Title = proposed.Title,
            Urgency = proposed.Urgency,
            PagerDutyState = proposed.PagerDutyState,
            OpenedAt = proposed.OpenedAt,
            UpdatedAt = caseFileUpdatedAt,
            CaseFileVersion = 1,
            Origin = origin,
            InputVersion = 0,
            ProjectedInputVersion = 0,
            CreatedBy = producerPrincipal
        };
        var caseFileJson = JsonSerializer.Serialize(storedCaseFile, JsonOptions);
        var storedCase = proposed with
        {
            PagerDutyIncidentId = null,
            UpdatedAt = caseFileUpdatedAt,
            Version = 1,
            Status = storedCaseFile.Status,
            IsFrozen = false,
            CaseFileJson = caseFileJson,
            Origin = origin,
            InputVersion = 0,
            ProjectedInputVersion = 0,
            CreatedBy = producerPrincipal
        };

        await InsertCaseAsync(connection, transaction, storedCase, cancellationToken);
        await InsertCreatedInputAsync(
            connection, transaction, storedCase.Id, producerPrincipal, createdInput, now, cancellationToken);
        await PersistCaseFileArtifactsAsync(
            connection, transaction, storedCase.Id, storedCaseFile, cancellationToken);

        if (storedCase.PublishToSlack)
        {
            await EnqueueSlackCaseFileAsync(
                connection, transaction, storedCase.Id, storedCaseFile.CaseFileVersion, storedCaseFile.Status,
                cancellationToken);
        }

        await using (var recordReceipt = new NpgsqlCommand("""
            insert into case_create_receipts(
                producer_principal, idempotency_key, request_hash, case_id, response_json)
            values ($1, $2, $3, $4, $5::jsonb)
            """, connection, transaction))
        {
            recordReceipt.Parameters.AddWithValue(producerPrincipal);
            recordReceipt.Parameters.AddWithValue(idempotencyKey);
            recordReceipt.Parameters.AddWithValue(requestHash);
            recordReceipt.Parameters.AddWithValue(storedCase.Id);
            recordReceipt.Parameters.AddWithValue(JsonSerializer.Serialize(
                new { caseId = storedCase.Id }, JsonOptions));
            await recordReceipt.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new CreateCaseResult(storedCase, Duplicate: false);
    }

    public async Task<CaseRecord?> GetCaseAsync(
        Guid caseId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadCaseAsync(
            connection, transaction: null, caseId, forUpdate: false, cancellationToken);
    }

    public async Task<CaseFile?> GetCaseFileAsync(
        Guid caseId,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "select case_file_json::text from cases where id = $1");
        command.Parameters.AddWithValue(caseId);
        var json = await command.ExecuteScalarAsync(cancellationToken) as string;
        return string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<CaseFile>(json, JsonOptions)
                ?? throw new InvalidOperationException("The persisted Case File is invalid.");
    }

    public async Task<AppendCrumbsResult> AppendAsync(
        Guid caseId,
        string producerPrincipal,
        string batchId,
        string requestHash,
        IReadOnlyList<NormalizedCrumb> crumbs,
        int maximumCrumbsPerCase,
        CancellationToken cancellationToken)
    {
        RequireValue(producerPrincipal, nameof(producerPrincipal));
        RequireValue(batchId, nameof(batchId));
        RequireValue(requestHash, nameof(requestHash));
        ArgumentNullException.ThrowIfNull(crumbs);
        if (maximumCrumbsPerCase <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumCrumbsPerCase), "The per-Case Crumb limit must be positive.");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var caseRecord = await ReadCaseAsync(
                connection, transaction, caseId, forUpdate: true, cancellationToken)
            ?? throw new CaseNotFoundException(caseId);

        var priorResponse = await ReadAppendReceiptAsync(
            connection, transaction, caseId, producerPrincipal, batchId, cancellationToken);
        if (priorResponse is not null)
        {
            EnsureMatchingHash(
                requestHash,
                priorResponse.Value.RequestHash,
                CaseCommandKinds.AppendCrumbs,
                batchId);
            var duplicateResult = JsonSerializer.Deserialize<AppendCrumbsResult>(
                    priorResponse.Value.ResponseJson, JsonOptions)
                ?? throw new InvalidOperationException("The persisted append receipt is invalid.");
            await transaction.CommitAsync(cancellationToken);
            return duplicateResult with { DuplicateBatch = true };
        }

        // A committed command keeps its acknowledgement even if the Case is closed later. Lifecycle
        // validation therefore deliberately follows receipt replay.
        if (caseRecord.IsFrozen
            || caseRecord.PagerDutyState == PagerDutyIncidentState.Resolved
            || string.Equals(caseRecord.Status, "closed", StringComparison.OrdinalIgnoreCase))
        {
            throw new CaseConflictException(
                $"Case '{caseId}' is closed and cannot accept new Crumbs.");
        }

        var lookupClientIds = crumbs
            .SelectMany(item => item.SupersedesClientCrumbId is null
                ? [item.ClientCrumbId]
                : new[] { item.ClientCrumbId, item.SupersedesClientCrumbId })
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var existingByClientId = await ReadCrumbReferencesAsync(
            connection, transaction, caseId, producerPrincipal, lookupClientIds, cancellationToken);

        var observed = new Dictionary<string, NormalizedCrumb>(StringComparer.Ordinal);
        var newCrumbs = new List<NormalizedCrumb>(crumbs.Count);
        var duplicates = 0;
        foreach (var item in crumbs)
        {
            RequireValue(item.ClientCrumbId, nameof(item.ClientCrumbId));
            RequireValue(item.PayloadHash, nameof(item.PayloadHash));
            if (observed.TryGetValue(item.ClientCrumbId, out var repeated))
            {
                EnsureMatchingCrumbHash(item.ClientCrumbId, item.PayloadHash, repeated.PayloadHash);
                duplicates++;
                continue;
            }
            observed.Add(item.ClientCrumbId, item);

            if (existingByClientId.TryGetValue(item.ClientCrumbId, out var existing))
            {
                EnsureMatchingCrumbHash(item.ClientCrumbId, item.PayloadHash, existing.PayloadHash);
                duplicates++;
                continue;
            }
            newCrumbs.Add(item);
        }

        await EnsureSequenceInvariantAsync(
            connection, transaction, caseId, caseRecord.InputVersion, cancellationToken);
        var currentSubmittedCount = await CountSubmittedCrumbsAsync(
            connection, transaction, caseId, cancellationToken);
        if (currentSubmittedCount + newCrumbs.Count > maximumCrumbsPerCase)
        {
            throw new CaseValidationException(
                $"Case '{caseId}' may contain at most {maximumCrumbsPerCase} submitted Crumbs.");
        }

        var availableReferences = new Dictionary<string, CrumbReference>(
            existingByClientId, StringComparer.Ordinal);
        var newCrumbIndexes = newCrumbs
            .Select((item, index) => (item.ClientCrumbId, index))
            .ToDictionary(pair => pair.ClientCrumbId, pair => pair.index, StringComparer.Ordinal);
        var supersededIds = new HashSet<Guid>();
        var nextSequence = caseRecord.InputVersion;
        var receivedAt = timeProvider.GetUtcNow();

        for (var index = 0; index < newCrumbs.Count; index++)
        {
            var item = newCrumbs[index];
            CrumbReference? superseded = null;
            if (item.SupersedesClientCrumbId is not null)
            {
                if (string.Equals(
                    item.ClientCrumbId, item.SupersedesClientCrumbId, StringComparison.Ordinal))
                {
                    throw new CaseValidationException(
                        $"Crumb '{item.ClientCrumbId}' cannot supersede itself.");
                }
                if (!availableReferences.TryGetValue(item.SupersedesClientCrumbId, out superseded))
                {
                    if (newCrumbIndexes.TryGetValue(item.SupersedesClientCrumbId, out var targetIndex)
                        && targetIndex > index)
                    {
                        throw new CaseValidationException(
                            $"Crumb '{item.ClientCrumbId}' may only supersede an earlier Crumb in its batch.");
                    }
                    throw new CaseValidationException(
                        $"Superseded Crumb '{item.SupersedesClientCrumbId}' does not exist for this producer.");
                }
                if (superseded.RetractedInputVersion is not null
                    || !supersededIds.Add(superseded.Id))
                {
                    throw new CaseConflictException(
                        $"Crumb '{item.SupersedesClientCrumbId}' has already been superseded.");
                }
            }

            nextSequence++;
            await InsertNormalizedCrumbAsync(
                connection,
                transaction,
                caseId,
                producerPrincipal,
                item,
                nextSequence,
                receivedAt,
                superseded?.Id,
                cancellationToken);
            var insertedReference = new CrumbReference(
                item.Id, item.ClientCrumbId, item.PayloadHash, RetractedInputVersion: null);
            availableReferences[item.ClientCrumbId] = insertedReference;

            if (superseded is not null)
            {
                await RetractCrumbAsync(
                    connection, transaction, superseded.Id, receivedAt, nextSequence, cancellationToken);
                availableReferences[item.SupersedesClientCrumbId!] = superseded with
                {
                    RetractedInputVersion = nextSequence
                };
            }
        }

        var rebuildQueued = false;
        if (newCrumbs.Count > 0)
        {
            long workflowGeneration;
            await using (var updateVersion = new NpgsqlCommand("""
                update cases
                set input_version = $2, status = $3, updated_at = $4,
                    workflow_generation = workflow_generation + 1
                where id = $1
                returning workflow_generation
                """, connection, transaction))
            {
                updateVersion.Parameters.AddWithValue(caseId);
                updateVersion.Parameters.AddWithValue(nextSequence);
                updateVersion.Parameters.AddWithValue(CaseProgression.Rebuilding);
                updateVersion.Parameters.AddWithValue(receivedAt);
                workflowGeneration = (long)(await updateVersion.ExecuteScalarAsync(cancellationToken)
                    ?? throw new InvalidOperationException(
                        "The Case workflow generation was not advanced."));
            }
            rebuildQueued = await EnqueueCoalescedAsync(
                connection,
                transaction,
                caseId,
                CaseWorkKinds.Project,
                nextSequence,
                workflowGeneration,
                cancellationToken);
        }

        var response = new AppendCrumbsResult(
            Accepted: newCrumbs.Count,
            Duplicates: duplicates,
            InputVersion: nextSequence,
            ProjectedInputVersion: caseRecord.ProjectedInputVersion,
            RebuildQueued: rebuildQueued,
            DuplicateBatch: false);

        await using (var recordReceipt = new NpgsqlCommand("""
            insert into case_command_receipts(
                case_id, producer_principal, command_kind, idempotency_key, request_hash, response_json)
            values ($1, $2, $3, $4, $5, $6::jsonb)
            """, connection, transaction))
        {
            recordReceipt.Parameters.AddWithValue(caseId);
            recordReceipt.Parameters.AddWithValue(producerPrincipal);
            recordReceipt.Parameters.AddWithValue(CaseCommandKinds.AppendCrumbs);
            recordReceipt.Parameters.AddWithValue(batchId);
            recordReceipt.Parameters.AddWithValue(requestHash);
            recordReceipt.Parameters.AddWithValue(JsonSerializer.Serialize(response, JsonOptions));
            await recordReceipt.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return response;
    }

    public async Task<IReadOnlyList<CaseInput>> ListInputsAsync(
        Guid caseId,
        long? throughInputVersion,
        bool includeInactive,
        CancellationToken cancellationToken)
    {
        if (throughInputVersion is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(throughInputVersion));
        }

        await using var command = dataSource.CreateCommand("""
            select id, case_id, sequence, input_version, producer_principal, client_crumb_id,
                   crumb_kind, occurred_at, received_at, category, severity, summary, excerpt,
                   declared_source, source_reference, url, actor, object_type, object_id,
                   attributes_json::text, trust_level, payload_hash, supersedes_crumb_id,
                   retracted_at, retracted_input_version
            from case_inputs
            where case_id = $1
              and ($2::bigint is null or input_version <= $2)
              and ($3 or retracted_input_version is null
                   or retracted_input_version > coalesce($2, 9223372036854775807::bigint))
            order by sequence
            """);
        command.Parameters.AddWithValue(caseId);
        AddNullable(command.Parameters, NpgsqlDbType.Bigint, throughInputVersion);
        command.Parameters.AddWithValue(includeInactive);
        var output = new List<CaseInput>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            output.Add(ReadCaseInput(reader));
        }
        return output;
    }

    public Task<bool> QueueProjectionAsync(
        Guid caseId,
        long targetInputVersion,
        CancellationToken cancellationToken) =>
        QueueWorkAsync(
            caseId, CaseWorkKinds.Project, targetInputVersion, cancellationToken);

    public Task<bool> QueueRefreshAsync(
        Guid caseId,
        long targetInputVersion,
        CancellationToken cancellationToken) =>
        QueueWorkAsync(
            caseId, CaseWorkKinds.RefreshSources, targetInputVersion, cancellationToken);

    public async Task CloseAsync(
        Guid caseId,
        string producerPrincipal,
        CancellationToken cancellationToken)
    {
        RequireValue(producerPrincipal, nameof(producerPrincipal));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await ReadCaseAsync(
                connection, transaction, caseId, forUpdate: true, cancellationToken)
            ?? throw new CaseNotFoundException(caseId);
        if (current.IsFrozen && current.PagerDutyState == PagerDutyIncidentState.Resolved)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        var now = timeProvider.GetUtcNow();
        await EnsureSequenceInvariantAsync(
            connection, transaction, caseId, current.InputVersion, cancellationToken);
        var closeInputVersion = checked(current.InputVersion + 1);
        await InsertCloseInputAsync(
            connection,
            transaction,
            current,
            producerPrincipal,
            closeInputVersion,
            now,
            cancellationToken);

        long workflowGeneration;
        await using (var close = new NpgsqlCommand("""
            update cases
            set pagerduty_state = $2, status = $3, is_frozen = true, updated_at = $4,
                resolved_at = $4, input_version = $5,
                workflow_generation = workflow_generation + 1
            where id = $1
            returning workflow_generation
            """, connection, transaction))
        {
            close.Parameters.AddWithValue(caseId);
            close.Parameters.AddWithValue(PagerDutyIncidentState.Resolved.ToString());
            close.Parameters.AddWithValue(CaseProgression.Resolved);
            close.Parameters.AddWithValue(now);
            close.Parameters.AddWithValue(closeInputVersion);
            workflowGeneration = (long)(await close.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException(
                    "The Case close workflow generation was not advanced."));
        }

        await DeleteProgressAsync(connection, transaction, caseId, cancellationToken);

        await EnqueueCoalescedAsync(
            connection,
            transaction,
            caseId,
            CaseWorkKinds.Project,
            closeInputVersion,
            workflowGeneration,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CaseRecord>> ListRecentAsync(
        int limit,
        CancellationToken cancellationToken)
        => await ListRecentAsync(limit, TeamAccessScope.Unrestricted, cancellationToken);

    public async Task<IReadOnlyList<CaseRecord>> ListRecentAsync(
        int limit,
        TeamAccessScope scope,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "The recent-Case limit must be 1 through 500.");
        }

        await using var command = dataSource.CreateCommand($"""
            select {CaseColumns}
            from cases
            where $2 or team = any($3)
            order by updated_at desc, id
            limit $1
            """);
        command.Parameters.AddWithValue(limit);
        command.Parameters.AddWithValue(scope.IsUnrestricted);
        command.Parameters.AddWithValue(scope.Teams.Order(StringComparer.Ordinal).ToArray());
        var output = new List<CaseRecord>(limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            output.Add(ReadCase(reader));
        }
        return output;
    }

    public async Task<long> SaveCrumbSourceSnapshotsAsync(
        Guid caseId,
        IReadOnlyList<CrumbSourceResult> results,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(results);
        var duplicateSource = results
            .GroupBy(result => result.Source, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateSource is not null)
        {
            throw new CaseValidationException(
                $"Crumb-source snapshot contains source '{duplicateSource}' more than once.");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var caseRecord = await ReadCaseAsync(
                connection, transaction, caseId, forUpdate: true, cancellationToken)
            ?? throw new CaseNotFoundException(caseId);
        if (caseRecord.IsFrozen)
        {
            throw new CaseConflictException(
                $"Case '{caseId}' was closed while Crumb sources were refreshing.");
        }

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
        if (results.Count == 0)
        {
            await InsertCrumbSourceSnapshotAsync(
                connection,
                transaction,
                caseId,
                snapshotVersion,
                EmptySnapshotSource,
                collectedAt,
                CrumbSourceResult.Excluded(EmptySnapshotSource),
                cancellationToken);
        }
        else
        {
            foreach (var result in results)
            {
                RequireValue(result.Source, nameof(result.Source));
                if (string.Equals(result.Source, EmptySnapshotSource, StringComparison.Ordinal))
                {
                    throw new CaseValidationException(
                        $"Crumb source '{EmptySnapshotSource}' is reserved by Panko.");
                }
                await InsertCrumbSourceSnapshotAsync(
                    connection,
                    transaction,
                    caseId,
                    snapshotVersion,
                    result.Source,
                    collectedAt,
                    result,
                    cancellationToken);
            }
        }

        // Snapshot persistence and its projection trigger are one atomic operation. The snapshot
        // generation in the key intentionally prevents an already-leased same-input projection from
        // swallowing newly collected Crumb-source state.
        var workflowGeneration = await AdvanceWorkflowAsync(
            connection,
            transaction,
            caseId,
            CaseProgression.Rebuilding,
            collectedAt,
            cancellationToken);
        await EnqueueSnapshotProjectionAsync(
            connection,
            transaction,
            caseId,
            caseRecord.InputVersion,
            snapshotVersion,
            workflowGeneration,
            cancellationToken);
        await DeleteExpiredCrumbSourceSnapshotGenerationsAsync(
            connection,
            transaction,
            caseId,
            snapshotVersion,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return snapshotVersion;
    }

    public async Task<IReadOnlyList<CrumbSourceResult>> GetLatestCrumbSourceResultsAsync(
        Guid caseId,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            select source, result_json::text
            from crumb_source_snapshots
            where case_id = $1
              and snapshot_version = (
                  select max(snapshot_version)
                  from crumb_source_snapshots
                  where case_id = $1)
            order by source
            """);
        command.Parameters.AddWithValue(caseId);
        var output = new List<CrumbSourceResult>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(0), EmptySnapshotSource, StringComparison.Ordinal))
            {
                continue;
            }
            output.Add(JsonSerializer.Deserialize<CrumbSourceResult>(reader.GetString(1), JsonOptions)
                ?? throw new InvalidOperationException("A persisted Crumb-source snapshot is invalid."));
        }
        return output;
    }

    public Task<int?> CommitProjectionAsync(
        CaseRecord expected,
        CaseFile caseFile,
        long targetInputVersion,
        CancellationToken cancellationToken,
        long? targetWorkflowGeneration = null) =>
        CommitCaseFileAsync(
            expected,
            caseFile,
            targetInputVersion,
            targetWorkflowGeneration,
            advancesProjection: true,
            cancellationToken);

    public Task<int?> CommitAnalysisAsync(
        CaseRecord expected,
        CaseFile caseFile,
        long projectedInputVersion,
        CancellationToken cancellationToken,
        long? targetWorkflowGeneration = null) =>
        CommitCaseFileAsync(
            expected,
            caseFile,
            projectedInputVersion,
            targetWorkflowGeneration,
            advancesProjection: false,
            cancellationToken);

    private async Task<int?> CommitCaseFileAsync(
        CaseRecord expected,
        CaseFile caseFile,
        long targetInputVersion,
        long? targetWorkflowGeneration,
        bool advancesProjection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(caseFile);
        if (targetInputVersion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetInputVersion));
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await ReadCaseAsync(
                connection, transaction, expected.Id, forUpdate: true, cancellationToken)
            ?? throw new CaseNotFoundException(expected.Id);
        var workflowGeneration = targetWorkflowGeneration ?? current.WorkflowGeneration;

        if (targetInputVersion > current.InputVersion)
        {
            throw new CaseConflictException(
                $"Cannot commit input version {targetInputVersion} for Case '{current.Id}' at version {current.InputVersion}.");
        }
        if (workflowGeneration > current.WorkflowGeneration)
        {
            throw new CaseConflictException(
                $"Cannot commit workflow generation {workflowGeneration} for Case '{current.Id}' at generation {current.WorkflowGeneration}.");
        }
        if (targetWorkflowGeneration is not null
            && (workflowGeneration < current.WorkflowGeneration
                || advancesProjection
                && current.ProjectedWorkflowGeneration >= workflowGeneration))
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        if (advancesProjection && current.ProjectedInputVersion > targetInputVersion)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        if (!advancesProjection && current.ProjectedInputVersion != targetInputVersion)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        if (!advancesProjection && current.InputVersion != targetInputVersion)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        if (current.Version != expected.Version)
        {
            if (advancesProjection)
            {
                // An equal projected-input version does not prove that this rebuild's Crumb-source
                // snapshot or Recipe revision is represented. Keep the durable item retryable.
                throw new CaseConflictException(
                    $"Case '{current.Id}' Case File changed while projecting input version {targetInputVersion}; retry the projection.");
            }
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var projectedInputVersion = advancesProjection
            ? targetInputVersion
            : current.ProjectedInputVersion;
        var nextVersion = checked(current.Version + 1);
        var queueAnalysis = advancesProjection
            && !current.IsFrozen
            && current.InputVersion == projectedInputVersion
            && current.WorkflowGeneration == workflowGeneration
            && caseFile.Crumbs.Count > 0;
        var effectiveState = current.IsFrozen ? current.PagerDutyState : caseFile.PagerDutyState;
        var effectiveStatus = current.IsFrozen
            ? current.Status
            : current.InputVersion > projectedInputVersion
                ? CaseProgression.Rebuilding
                : queueAnalysis
                    ? CaseProgression.Analysing
                : caseFile.Status;
        var versioned = caseFile with
        {
            CaseId = current.Id,
            PagerDutyIncidentId = current.PagerDutyIncidentId,
            ServiceId = current.ServiceId,
            RecipeId = current.RecipeId,
            Title = current.Title,
            Urgency = current.Urgency,
            PagerDutyState = effectiveState,
            Status = effectiveStatus,
            OpenedAt = current.OpenedAt,
            CaseFileVersion = nextVersion,
            Origin = current.Origin,
            InputVersion = current.InputVersion,
            ProjectedInputVersion = projectedInputVersion,
            CreatedBy = current.CreatedBy
        };
        var json = JsonSerializer.Serialize(versioned, JsonOptions);

        await using (var update = new NpgsqlCommand("""
            update cases
            set case_file_json = $2::jsonb, case_file_version = $3, status = $4, updated_at = $5,
                projected_input_version = $6,
                projected_workflow_generation = case
                    when $8 then greatest(projected_workflow_generation, $9)
                    else projected_workflow_generation
                end
            where id = $1 and case_file_version = $7
              and input_version >= $6
              and projected_input_version <= $6
            """, connection, transaction))
        {
            update.Parameters.AddWithValue(current.Id);
            update.Parameters.AddWithValue(json);
            update.Parameters.AddWithValue(nextVersion);
            update.Parameters.AddWithValue(effectiveStatus);
            update.Parameters.AddWithValue(versioned.UpdatedAt);
            update.Parameters.AddWithValue(projectedInputVersion);
            update.Parameters.AddWithValue(expected.Version);
            update.Parameters.AddWithValue(advancesProjection);
            update.Parameters.AddWithValue(workflowGeneration);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }
        }


        await DeleteProgressAsync(connection, transaction, current.Id, cancellationToken);

        await PersistCaseFileArtifactsAsync(
            connection, transaction, current.Id, versioned, cancellationToken);
        var coalescedAnalysisItems = 0;
        if (queueAnalysis)
        {
            coalescedAnalysisItems = await EnqueueCaseFileAnalysisAsync(
                connection,
                transaction,
                current.Id,
                projectedInputVersion,
                workflowGeneration,
                nextVersion,
                cancellationToken);
        }
        if (current.PublishToSlack)
        {
            await EnqueueSlackCaseFileAsync(
                connection, transaction, current.Id, nextVersion, effectiveStatus, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        telemetry?.LlmCallsAvoided(coalescedAnalysisItems, "queued-analysis-coalesced");
        return nextVersion;
    }

    private static async Task DeleteProgressAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid caseId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "delete from case_progress where case_id = $1",
            connection,
            transaction);
        command.Parameters.AddWithValue(caseId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<bool> QueueWorkAsync(
        Guid caseId,
        string kind,
        long targetInputVersion,
        CancellationToken cancellationToken)
    {
        if (targetInputVersion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetInputVersion));
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await ReadCaseAsync(
                connection, transaction, caseId, forUpdate: true, cancellationToken)
            ?? throw new CaseNotFoundException(caseId);
        if (targetInputVersion > current.InputVersion)
        {
            throw new CaseConflictException(
                $"Target input version {targetInputVersion} is newer than Case '{caseId}' version {current.InputVersion}.");
        }
        var status = kind switch
        {
            CaseWorkKinds.Project => CaseProgression.Rebuilding,
            CaseWorkKinds.RefreshSources => CaseProgression.RefreshingSources,
            _ => throw new InvalidOperationException(
                $"Unknown Case work kind '{kind}'.")
        };
        var workflowGeneration = await AdvanceWorkflowAsync(
            connection,
            transaction,
            caseId,
            current.IsFrozen ? null : status,
            timeProvider.GetUtcNow(),
            cancellationToken);
        var queued = await EnqueueCoalescedAsync(
            connection,
            transaction,
            caseId,
            kind,
            targetInputVersion,
            workflowGeneration,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return queued;
    }

    private static async Task<int> EnqueueCaseFileAnalysisAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid caseId,
        long targetInputVersion,
        long targetWorkflowGeneration,
        int caseFileVersion,
        CancellationToken cancellationToken)
    {
        var idempotencyKey =
            $"{CaseWorkKinds.Analyse}:{caseId:D}:{targetInputVersion}:workflow:{targetWorkflowGeneration}:case-file:{caseFileVersion}";
        await using (var enqueue = new NpgsqlCommand("""
            insert into work_items(
                case_id, kind, idempotency_key, due_at, target_input_version,
                target_workflow_generation)
            values ($1, $2, $3, now(), $4, $5)
            on conflict (idempotency_key) do update set
                due_at = now(),
                completed_at = null,
                locked_until = null,
                last_error = null,
                attempts = 0,
                target_input_version = excluded.target_input_version,
                target_workflow_generation = excluded.target_workflow_generation
            where work_items.completed_at is not null
            """, connection, transaction))
        {
            enqueue.Parameters.AddWithValue(caseId);
            enqueue.Parameters.AddWithValue(CaseWorkKinds.Analyse);
            enqueue.Parameters.AddWithValue(idempotencyKey);
            enqueue.Parameters.AddWithValue(targetInputVersion);
            enqueue.Parameters.AddWithValue(targetWorkflowGeneration);
            await enqueue.ExecuteNonQueryAsync(cancellationToken);
        }

        // A leased older analysis is allowed to finish against the Case File it read. This distinct
        // Case File-generation key guarantees that it cannot consume the follow-up analysis request.
        await using var coalesce = new NpgsqlCommand("""
            update work_items
            set completed_at = now(), locked_until = null
            where case_id = $1 and kind = $2 and completed_at is null
              and target_workflow_generation <= $3 and idempotency_key <> $4
              and (locked_until is null or locked_until < now())
            """, connection, transaction);
        coalesce.Parameters.AddWithValue(caseId);
        coalesce.Parameters.AddWithValue(CaseWorkKinds.Analyse);
        coalesce.Parameters.AddWithValue(targetWorkflowGeneration);
        coalesce.Parameters.AddWithValue(idempotencyKey);
        return await coalesce.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> EnqueueCoalescedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid caseId,
        string kind,
        long targetInputVersion,
        long targetWorkflowGeneration,
        CancellationToken cancellationToken)
    {
        var idempotencyKey =
            $"{kind}:{caseId:D}:{targetInputVersion}:workflow:{targetWorkflowGeneration}";
        await using (var enqueue = new NpgsqlCommand("""
            insert into work_items(
                case_id, kind, idempotency_key, due_at, target_input_version,
                target_workflow_generation)
            values ($1, $2, $3, now(), $4, $5)
            on conflict (idempotency_key) do update set
                due_at = now(),
                completed_at = null,
                locked_until = null,
                last_error = null,
                attempts = 0,
                target_input_version = excluded.target_input_version,
                target_workflow_generation = excluded.target_workflow_generation
            where work_items.completed_at is not null
            """, connection, transaction))
        {
            enqueue.Parameters.AddWithValue(caseId);
            enqueue.Parameters.AddWithValue(kind);
            enqueue.Parameters.AddWithValue(idempotencyKey);
            enqueue.Parameters.AddWithValue(targetInputVersion);
            enqueue.Parameters.AddWithValue(targetWorkflowGeneration);
            await enqueue.ExecuteNonQueryAsync(cancellationToken);
        }

        // Do not retire a currently leased older item: it may finish honestly at its own target. Any
        // unlocked older request is safely coalesced into the target just inserted above.
        await using (var coalesce = new NpgsqlCommand("""
            update work_items
            set completed_at = now(), locked_until = null
            where case_id = $1 and kind = $2 and completed_at is null
              and target_workflow_generation < $3
              and (locked_until is null or locked_until < now())
            """, connection, transaction))
        {
            coalesce.Parameters.AddWithValue(caseId);
            coalesce.Parameters.AddWithValue(kind);
            coalesce.Parameters.AddWithValue(targetWorkflowGeneration);
            await coalesce.ExecuteNonQueryAsync(cancellationToken);
        }
        return true;
    }

    private static async Task EnqueueSnapshotProjectionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid caseId,
        long targetInputVersion,
        long snapshotVersion,
        long targetWorkflowGeneration,
        CancellationToken cancellationToken)
    {
        var idempotencyKey =
            $"{CaseWorkKinds.Project}:{caseId:D}:{targetInputVersion}:workflow:{targetWorkflowGeneration}:snapshot:{snapshotVersion}";
        await using (var enqueue = new NpgsqlCommand("""
            insert into work_items(
                case_id, kind, idempotency_key, due_at, target_input_version,
                target_workflow_generation)
            values ($1, $2, $3, now(), $4, $5)
            """, connection, transaction))
        {
            enqueue.Parameters.AddWithValue(caseId);
            enqueue.Parameters.AddWithValue(CaseWorkKinds.Project);
            enqueue.Parameters.AddWithValue(idempotencyKey);
            enqueue.Parameters.AddWithValue(targetInputVersion);
            enqueue.Parameters.AddWithValue(targetWorkflowGeneration);
            await enqueue.ExecuteNonQueryAsync(cancellationToken);
        }

        // A not-yet-leased projection has read no state and is safely subsumed by this generation.
        // Leased work remains honest at its own snapshot, while this row guarantees a follow-up pass.
        await using var coalesce = new NpgsqlCommand("""
            update work_items
            set completed_at = now(), locked_until = null
            where case_id = $1 and kind = $2 and completed_at is null
              and target_workflow_generation < $3 and idempotency_key <> $4
              and (locked_until is null or locked_until < now())
            """, connection, transaction);
        coalesce.Parameters.AddWithValue(caseId);
        coalesce.Parameters.AddWithValue(CaseWorkKinds.Project);
        coalesce.Parameters.AddWithValue(targetWorkflowGeneration);
        coalesce.Parameters.AddWithValue(idempotencyKey);
        await coalesce.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteExpiredCrumbSourceSnapshotGenerationsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid caseId,
        long latestSnapshotVersion,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            delete from crumb_source_snapshots
            where case_id = $1 and snapshot_version <= $2 - $3
            """, connection, transaction);
        command.Parameters.AddWithValue(caseId);
        command.Parameters.AddWithValue(latestSnapshotVersion);
        command.Parameters.AddWithValue(RetainedCrumbSourceSnapshotGenerations);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> AdvanceWorkflowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid caseId,
        string? status,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            update cases
            set workflow_generation = workflow_generation + 1,
                status = coalesce($2, status),
                updated_at = $3
            where id = $1
            returning workflow_generation
            """, connection, transaction);
        command.Parameters.AddWithValue(caseId);
        AddNullable(command.Parameters, NpgsqlDbType.Text, status);
        command.Parameters.AddWithValue(updatedAt);
        return (long)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException(
                "The Case workflow generation was not advanced."));
    }

    private static async Task InsertCrumbSourceSnapshotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid caseId,
        long snapshotVersion,
        string source,
        DateTimeOffset collectedAt,
        CrumbSourceResult result,
        CancellationToken cancellationToken)
    {
        await using var insert = new NpgsqlCommand("""
            insert into crumb_source_snapshots(
                case_id, snapshot_version, source, collected_at, result_json)
            values ($1, $2, $3, $4, $5::jsonb)
            """, connection, transaction);
        insert.Parameters.AddWithValue(caseId);
        insert.Parameters.AddWithValue(snapshotVersion);
        insert.Parameters.AddWithValue(source);
        insert.Parameters.AddWithValue(collectedAt);
        insert.Parameters.AddWithValue(JsonSerializer.Serialize(result, JsonOptions));
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<CaseRecord?> ReadCaseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid caseId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            select {CaseColumns}
            from cases
            where id = $1
            {(forUpdate ? "for update" : string.Empty)}
            """, connection, transaction);
        command.Parameters.AddWithValue(caseId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadCase(reader) : null;
    }

    private static CaseRecord ReadCase(NpgsqlDataReader reader)
    {
        var labels = JsonSerializer.Deserialize<Dictionary<string, string>>(
                reader.GetString(15), JsonOptions)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var pagerDutyId = reader.IsDBNull(1) ? null : reader.GetString(1);
        var originKind = Enum.TryParse<CaseOriginKind>(
            reader.GetString(16), ignoreCase: true, out var parsedOrigin)
            ? parsedOrigin
            : CaseOriginKind.PagerDuty;
        return new CaseRecord(
            reader.GetGuid(0),
            pagerDutyId,
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            Enum.TryParse<PagerDutyIncidentState>(reader.GetString(6), ignoreCase: true, out var state)
                ? state
                : PagerDutyIncidentState.Unknown,
            reader.GetFieldValue<DateTimeOffset>(7),
            reader.GetFieldValue<DateTimeOffset>(8),
            reader.GetInt32(9),
            reader.GetString(10),
            reader.GetBoolean(11),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(13) ? string.Empty : reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetString(14),
            labels)
        {
            Origin = new CaseOrigin(
                originKind, reader.IsDBNull(17) ? null : reader.GetString(17)),
            CreatedBy = reader.IsDBNull(18) ? null : reader.GetString(18),
            InputVersion = reader.GetInt64(19),
            ProjectedInputVersion = reader.GetInt64(20),
            PublishToSlack = reader.GetBoolean(21),
            AcknowledgedAt = reader.IsDBNull(22)
                ? null
                : reader.GetFieldValue<DateTimeOffset>(22),
            ResolvedAt = reader.IsDBNull(23)
                ? null
                : reader.GetFieldValue<DateTimeOffset>(23),
            Team = reader.GetString(24),
            WorkflowGeneration = reader.GetInt64(25),
            ProjectedWorkflowGeneration = reader.GetInt64(26)
        };
    }

    private static CaseInput ReadCaseInput(NpgsqlDataReader reader)
    {
        var attributes = JsonNode.Parse(reader.GetString(19)) as JsonObject ?? [];
        return new CaseInput(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetString(4),
            reader.GetString(5),
            Enum.TryParse<SubmittedCrumbKind>(reader.GetString(6), ignoreCase: true, out var type)
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

    private static async Task InsertCaseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CaseRecord caseRecord,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            insert into cases(
                id, pagerduty_incident_id, service_id, recipe_id, title, urgency, pagerduty_state, status,
                opened_at, updated_at, case_file_version, is_frozen, case_file_json, slack_channel,
                slack_timestamp, labels_json, origin_kind, origin_external_id, created_by,
                input_version, projected_input_version, publish_to_slack,
                acknowledged_at, resolved_at, team, workflow_generation,
                projected_workflow_generation)
            values (
                $1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13::jsonb, $14,
                $15, $16::jsonb, $17, $18, $19, $20, $21, $22, $23, $24, $25, $26, $27)
            """, connection, transaction);
        command.Parameters.AddWithValue(caseRecord.Id);
        AddNullable(command.Parameters, NpgsqlDbType.Text, caseRecord.PagerDutyIncidentId);
        command.Parameters.AddWithValue(caseRecord.ServiceId);
        command.Parameters.AddWithValue(caseRecord.RecipeId);
        command.Parameters.AddWithValue(caseRecord.Title);
        command.Parameters.AddWithValue(caseRecord.Urgency);
        command.Parameters.AddWithValue(caseRecord.PagerDutyState.ToString());
        command.Parameters.AddWithValue(caseRecord.Status);
        command.Parameters.AddWithValue(caseRecord.OpenedAt);
        command.Parameters.AddWithValue(caseRecord.UpdatedAt);
        command.Parameters.AddWithValue(caseRecord.Version);
        command.Parameters.AddWithValue(caseRecord.IsFrozen);
        AddNullable(command.Parameters, NpgsqlDbType.Jsonb, caseRecord.CaseFileJson);
        command.Parameters.AddWithValue(caseRecord.SlackChannel ?? string.Empty);
        AddNullable(command.Parameters, NpgsqlDbType.Text, caseRecord.SlackTimestamp);
        command.Parameters.AddWithValue(JsonSerializer.Serialize(caseRecord.Labels, JsonOptions));
        command.Parameters.AddWithValue(caseRecord.Origin.Kind.ToString().ToLowerInvariant());
        AddNullable(command.Parameters, NpgsqlDbType.Text, caseRecord.Origin.ExternalId);
        AddNullable(command.Parameters, NpgsqlDbType.Text, caseRecord.CreatedBy);
        command.Parameters.AddWithValue(caseRecord.InputVersion);
        command.Parameters.AddWithValue(caseRecord.ProjectedInputVersion);
        command.Parameters.AddWithValue(caseRecord.PublishToSlack);
        AddNullable(command.Parameters, NpgsqlDbType.TimestampTz, caseRecord.AcknowledgedAt);
        AddNullable(command.Parameters, NpgsqlDbType.TimestampTz, caseRecord.ResolvedAt);
        command.Parameters.AddWithValue(caseRecord.Team);
        command.Parameters.AddWithValue(caseRecord.WorkflowGeneration);
        command.Parameters.AddWithValue(caseRecord.ProjectedWorkflowGeneration);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertCreatedInputAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid caseId,
        string producerPrincipal,
        CaseInput createdInput,
        DateTimeOffset fallbackReceivedAt,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            insert into case_inputs(
                id, case_id, sequence, input_version, producer_principal, client_crumb_id,
                crumb_kind, occurred_at, received_at, category, severity, summary, excerpt,
                declared_source, source_reference, url, actor, object_type, object_id,
                attributes_json, trust_level, payload_hash, supersedes_crumb_id,
                retracted_at, retracted_input_version)
            values (
                $1, $2, 0, 0, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13,
                $14, $15, $16, $17, $18::jsonb, $19, $20, null, null, null)
            """, connection, transaction);
        command.Parameters.AddWithValue(createdInput.Id);
        command.Parameters.AddWithValue(caseId);
        command.Parameters.AddWithValue(producerPrincipal);
        command.Parameters.AddWithValue(createdInput.ClientCrumbId);
        command.Parameters.AddWithValue(createdInput.Kind.ToString().ToLowerInvariant());
        command.Parameters.AddWithValue(createdInput.OccurredAt);
        command.Parameters.AddWithValue(
            createdInput.ReceivedAt == default ? fallbackReceivedAt : createdInput.ReceivedAt);
        command.Parameters.AddWithValue(createdInput.Category);
        command.Parameters.AddWithValue(createdInput.Severity);
        command.Parameters.AddWithValue(createdInput.Summary);
        AddNullable(command.Parameters, NpgsqlDbType.Text, createdInput.Excerpt);
        AddNullable(command.Parameters, NpgsqlDbType.Text, createdInput.DeclaredSource);
        AddNullable(command.Parameters, NpgsqlDbType.Text, createdInput.SourceReference);
        AddNullable(command.Parameters, NpgsqlDbType.Text, createdInput.Url);
        AddNullable(command.Parameters, NpgsqlDbType.Text, createdInput.Actor);
        AddNullable(command.Parameters, NpgsqlDbType.Text, createdInput.ObjectType);
        AddNullable(command.Parameters, NpgsqlDbType.Text, createdInput.ObjectId);
        command.Parameters.AddWithValue(JsonSerializer.Serialize(createdInput.Attributes, JsonOptions));
        command.Parameters.AddWithValue(createdInput.TrustLevel);
        command.Parameters.AddWithValue(createdInput.PayloadHash);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertCloseInputAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CaseRecord caseRecord,
        string producerPrincipal,
        long inputVersion,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        const string clientCrumbId = "case-closed";
        var crumbId = CaseInputBoundary.DeterministicCrumbId(
            caseRecord.Id, producerPrincipal, clientCrumbId);
        var payloadHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{caseRecord.Id:N}\u001f{producerPrincipal}\u001f{inputVersion}\u001f{occurredAt:O}\u001fcase-closed")));
        await using var command = new NpgsqlCommand("""
            insert into case_inputs(
                id, case_id, sequence, input_version, producer_principal, client_crumb_id,
                crumb_kind, occurred_at, received_at, category, severity, summary, excerpt,
                declared_source, source_reference, url, actor, object_type, object_id,
                attributes_json, trust_level, payload_hash, supersedes_crumb_id,
                retracted_at, retracted_input_version)
            values (
                $1, $2, $3, $3, $4, $5, 'event', $6, $6, 'case-closed', 'info',
                'Case closed', null, null, null, null, $4,
                'case', $7, '{}'::jsonb, 'system', $8, null, null, null)
            """, connection, transaction);
        command.Parameters.AddWithValue(crumbId);
        command.Parameters.AddWithValue(caseRecord.Id);
        command.Parameters.AddWithValue(inputVersion);
        command.Parameters.AddWithValue(producerPrincipal);
        command.Parameters.AddWithValue(clientCrumbId);
        command.Parameters.AddWithValue(occurredAt);
        command.Parameters.AddWithValue(caseRecord.Id.ToString("D"));
        command.Parameters.AddWithValue(payloadHash);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertNormalizedCrumbAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid caseId,
        string producerPrincipal,
        NormalizedCrumb item,
        long sequence,
        DateTimeOffset receivedAt,
        Guid? supersedesCrumbId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            insert into case_inputs(
                id, case_id, sequence, input_version, producer_principal, client_crumb_id,
                crumb_kind, occurred_at, received_at, category, severity, summary, excerpt,
                declared_source, source_reference, url, actor, object_type, object_id,
                attributes_json, trust_level, payload_hash, supersedes_crumb_id)
            values (
                $1, $2, $3, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14,
                $15, $16, $17, $18, $19::jsonb, 'submitted', $20, $21)
            """, connection, transaction);
        command.Parameters.AddWithValue(item.Id);
        command.Parameters.AddWithValue(caseId);
        command.Parameters.AddWithValue(sequence);
        command.Parameters.AddWithValue(producerPrincipal);
        command.Parameters.AddWithValue(item.ClientCrumbId);
        command.Parameters.AddWithValue(item.Kind.ToString().ToLowerInvariant());
        command.Parameters.AddWithValue(item.OccurredAt);
        command.Parameters.AddWithValue(receivedAt);
        command.Parameters.AddWithValue(item.Category);
        command.Parameters.AddWithValue(item.Severity);
        command.Parameters.AddWithValue(item.Summary);
        AddNullable(command.Parameters, NpgsqlDbType.Text, item.Excerpt);
        AddNullable(command.Parameters, NpgsqlDbType.Text, item.DeclaredSource);
        AddNullable(command.Parameters, NpgsqlDbType.Text, item.SourceReference);
        AddNullable(command.Parameters, NpgsqlDbType.Text, item.Url);
        AddNullable(command.Parameters, NpgsqlDbType.Text, item.Actor);
        AddNullable(command.Parameters, NpgsqlDbType.Text, item.ObjectType);
        AddNullable(command.Parameters, NpgsqlDbType.Text, item.ObjectId);
        command.Parameters.AddWithValue(JsonSerializer.Serialize(item.Attributes, JsonOptions));
        command.Parameters.AddWithValue(item.PayloadHash);
        AddNullable(command.Parameters, NpgsqlDbType.Uuid, supersedesCrumbId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task RetractCrumbAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid crumbId,
        DateTimeOffset retractedAt,
        long retractedInputVersion,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            update case_inputs
            set retracted_at = $2, retracted_input_version = $3
            where id = $1 and retracted_input_version is null
            """, connection, transaction);
        command.Parameters.AddWithValue(crumbId);
        command.Parameters.AddWithValue(retractedAt);
        command.Parameters.AddWithValue(retractedInputVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new CaseConflictException(
                $"Crumb '{crumbId}' was superseded concurrently.");
        }
    }

    private static async Task<Dictionary<string, CrumbReference>> ReadCrumbReferencesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid caseId,
        string producerPrincipal,
        string[] clientCrumbIds,
        CancellationToken cancellationToken)
    {
        var output = new Dictionary<string, CrumbReference>(StringComparer.Ordinal);
        if (clientCrumbIds.Length == 0)
        {
            return output;
        }

        await using var command = new NpgsqlCommand("""
            select id, client_crumb_id, payload_hash, retracted_input_version
            from case_inputs
            where case_id = $1 and producer_principal = $2
              and client_crumb_id = any($3)
            """, connection, transaction);
        command.Parameters.AddWithValue(caseId);
        command.Parameters.AddWithValue(producerPrincipal);
        command.Parameters.AddWithValue(clientCrumbIds);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var item = new CrumbReference(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3));
            output.Add(item.ClientCrumbId, item);
        }
        return output;
    }

    private static async Task EnsureSequenceInvariantAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid caseId,
        long inputVersion,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            select coalesce(max(sequence), 0)
            from case_inputs
            where case_id = $1
            """, connection, transaction);
        command.Parameters.AddWithValue(caseId);
        var maximumSequence = (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
        if (maximumSequence != inputVersion)
        {
            throw new InvalidOperationException(
                $"Case '{caseId}' input version {inputVersion} does not match its maximum Crumb sequence {maximumSequence}.");
        }
    }

    private static async Task<long> CountSubmittedCrumbsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid caseId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            select count(*)
            from case_inputs
            where case_id = $1 and sequence > 0
            """, connection, transaction);
        command.Parameters.AddWithValue(caseId);
        return (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
    }

    private static async Task<(string RequestHash, Guid CaseId)?> ReadCreateReceiptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string producerPrincipal,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            select request_hash, case_id
            from case_create_receipts
            where producer_principal = $1 and idempotency_key = $2
            """, connection, transaction);
        command.Parameters.AddWithValue(producerPrincipal);
        command.Parameters.AddWithValue(idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetString(0), reader.GetGuid(1))
            : null;
    }

    private static async Task<(string RequestHash, string ResponseJson)?> ReadAppendReceiptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid caseId,
        string producerPrincipal,
        string batchId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            select request_hash, response_json::text
            from case_command_receipts
            where case_id = $1 and producer_principal = $2
              and command_kind = $3 and idempotency_key = $4
            limit 1
            """, connection, transaction);
        command.Parameters.AddWithValue(caseId);
        command.Parameters.AddWithValue(producerPrincipal);
        command.Parameters.AddWithValue(CaseCommandKinds.AppendCrumbs);
        command.Parameters.AddWithValue(batchId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetString(0), reader.GetString(1))
            : null;
    }

    private static async Task PersistCaseFileArtifactsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid caseId,
        CaseFile caseFile,
        CancellationToken cancellationToken)
    {
        foreach (var crumb in caseFile.Crumbs)
        {
            await using var insert = new NpgsqlCommand("""
                insert into crumbs(case_id, case_file_version, crumb_id, source, occurred_at, payload)
                values ($1, $2, $3, $4, $5, $6::jsonb)
                """, connection, transaction);
            insert.Parameters.AddWithValue(caseId);
            insert.Parameters.AddWithValue(caseFile.CaseFileVersion);
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
            insert.Parameters.AddWithValue(caseId);
            insert.Parameters.AddWithValue(caseFile.CaseFileVersion);
            insert.Parameters.AddWithValue(index);
            insert.Parameters.AddWithValue(trailEntry.OccurredAt);
            insert.Parameters.AddWithValue(JsonSerializer.Serialize(trailEntry, JsonOptions));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task EnqueueSlackCaseFileAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid caseId,
        int caseFileVersion,
        string status,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(
            new { caseId, caseFileVersion }, JsonOptions);
        await using (var outbox = new NpgsqlCommand("""
            insert into outbox(kind, payload) values ($1, $2::jsonb)
            """, connection, transaction))
        {
            outbox.Parameters.AddWithValue(CaseOutboxKinds.SlackCaseFile);
            outbox.Parameters.AddWithValue(payload);
            await outbox.ExecuteNonQueryAsync(cancellationToken);
        }

        if (CaseProgression.NeedsStuckNotification(status))
        {
            await using var stuckCheck = new NpgsqlCommand("""
                insert into outbox(kind, payload, due_at)
                values ($1, $2::jsonb, now() + interval '1 minute')
                """, connection, transaction);
            stuckCheck.Parameters.AddWithValue(CaseOutboxKinds.SlackCaseFile);
            stuckCheck.Parameters.AddWithValue(payload);
            await stuckCheck.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static void EnsureMatchingHash(
        string supplied,
        string persisted,
        string commandKind,
        string idempotencyKey)
    {
        if (!string.Equals(supplied, persisted, StringComparison.Ordinal))
        {
            throw new CaseConflictException(
                $"Idempotency key '{idempotencyKey}' was already used for a different {commandKind} request.");
        }
    }

    private static void EnsureMatchingCrumbHash(
        string clientCrumbId,
        string supplied,
        string persisted)
    {
        if (!string.Equals(supplied, persisted, StringComparison.Ordinal))
        {
            throw new CaseConflictException(
                $"Client Crumb ID '{clientCrumbId}' was already used with a different payload.");
        }
    }

    private static void RequireValue(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty value is required.", parameterName);
        }
    }

    private static void AddNullable(
        NpgsqlParameterCollection parameters,
        NpgsqlDbType type,
        object? value) =>
        parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = type,
            Value = value ?? DBNull.Value
        });

    private sealed record CrumbReference(
        Guid Id,
        string ClientCrumbId,
        string PayloadHash,
        long? RetractedInputVersion);

    /// <summary>
    /// TrailCandidate has a convenience constructor in addition to its primary constructor.
    /// System.Text.Json cannot select between them without an explicit converter.
    /// </summary>
    private sealed class TrailCandidateJsonConverter : JsonConverter<TrailCandidate>
    {
        public override TrailCandidate Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            var occurredAt = RequiredDateTimeOffset(root, "occurredAt");
            var source = RequiredString(root, "source");
            var kind = RequiredString(root, "kind");
            var summary = RequiredString(root, "summary");
            var severity = RequiredString(root, "severity");
            var url = OptionalString(root, "url");
            var actor = OptionalString(root, "actor");
            var objectType = OptionalString(root, "objectType");
            var objectId = OptionalString(root, "objectId");
            var id = OptionalString(root, "id")
                ?? TrailCandidateIdentity.Create(
                    occurredAt, source, kind, summary, url, actor, objectType, objectId);
            return new TrailCandidate(
                id, occurredAt, source, kind, summary, severity, url, actor, objectType, objectId);
        }

        public override void Write(
            Utf8JsonWriter writer,
            TrailCandidate value,
            JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("id", value.StableId);
            writer.WriteString("occurredAt", value.OccurredAt);
            writer.WriteString("source", value.Source);
            writer.WriteString("kind", value.Kind);
            writer.WriteString("summary", value.Summary);
            writer.WriteString("severity", value.Severity);
            WriteNullableString(writer, "url", value.Url);
            WriteNullableString(writer, "actor", value.Actor);
            WriteNullableString(writer, "objectType", value.ObjectType);
            WriteNullableString(writer, "objectId", value.ObjectId);
            writer.WriteEndObject();
        }

        private static DateTimeOffset RequiredDateTimeOffset(JsonElement element, string name)
        {
            if (TryGetProperty(element, name, out var property)
                && property.ValueKind == JsonValueKind.String
                && property.TryGetDateTimeOffset(out var value))
            {
                return value;
            }
            throw new JsonException($"Trail property '{name}' is missing or invalid.");
        }

        private static string RequiredString(JsonElement element, string name) =>
            OptionalString(element, name)
            ?? throw new JsonException($"Trail property '{name}' is missing or invalid.");

        private static string? OptionalString(JsonElement element, string name) =>
            TryGetProperty(element, name, out var property)
                && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;

        private static bool TryGetProperty(
            JsonElement element,
            string camelCaseName,
            out JsonElement property)
        {
            if (element.TryGetProperty(camelCaseName, out property))
            {
                return true;
            }
            var pascalCaseName = char.ToUpperInvariant(camelCaseName[0]) + camelCaseName[1..];
            return element.TryGetProperty(pascalCaseName, out property);
        }

        private static void WriteNullableString(
            Utf8JsonWriter writer,
            string name,
            string? value)
        {
            if (value is null)
            {
                writer.WriteNull(name);
            }
            else
            {
                writer.WriteString(name, value);
            }
        }
    }
}
