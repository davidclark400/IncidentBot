using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IncidentBot.Api.Domain;
using IncidentBot.Api.Incidents;
using Npgsql;

namespace IncidentBot.Api.Infrastructure;

public sealed class IncidentRepository(NpgsqlDataSource dataSource, TimeProvider timeProvider) :
    IIncidentStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async Task<(Guid IncidentId, bool IsDuplicate)> AcceptWebhookAsync(
        PagerDutyWebhookEvent webhook,
        InvestigationProfile profile,
        ReadOnlyMemory<byte> rawPayload,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var hash = Convert.ToHexStringLower(SHA256.HashData(rawPayload.Span));
        await using var receipt = new NpgsqlCommand("""
            insert into webhook_receipts(event_id, pagerduty_incident_id, event_type, payload_hash)
            values ($1, $2, $3, $4)
            on conflict (event_id) do nothing
            returning event_id
            """, connection, transaction);
        receipt.Parameters.AddWithValue(webhook.EventId);
        receipt.Parameters.AddWithValue(webhook.PagerDutyIncidentId);
        receipt.Parameters.AddWithValue(webhook.EventType);
        receipt.Parameters.AddWithValue(hash);
        var inserted = await receipt.ExecuteScalarAsync(cancellationToken) is not null;

        if (!inserted)
        {
            await using var existing = new NpgsqlCommand(
                "select id from incidents where pagerduty_incident_id = $1", connection, transaction);
            existing.Parameters.AddWithValue(webhook.PagerDutyIncidentId);
            var existingId = (Guid?)await existing.ExecuteScalarAsync(cancellationToken) ?? Guid.Empty;
            await transaction.CommitAsync(cancellationToken);
            return (existingId, true);
        }

        var incidentId = Guid.NewGuid();
        var state = MapState(webhook.EventType);
        await using var upsert = new NpgsqlCommand("""
            insert into incidents(
                id, pagerduty_incident_id, service_id, profile_id, title, urgency, state, status,
                triggered_at, updated_at, slack_channel, labels_json, is_frozen)
            values ($1, $2, $3, $4, $5, $6, $7, $13, $15, $8, $9, $10::jsonb, $11)
            on conflict (pagerduty_incident_id) do update set
                service_id = excluded.service_id,
                profile_id = excluded.profile_id,
                title = excluded.title,
                urgency = excluded.urgency,
                triggered_at = least(incidents.triggered_at, excluded.triggered_at),
                state = case when excluded.state = 'Unknown' then incidents.state else excluded.state end,
                status = case when excluded.is_frozen then $14 else $13 end,
                updated_at = excluded.updated_at,
                slack_channel = excluded.slack_channel,
                labels_json = excluded.labels_json,
                is_frozen = case
                    when $12 = 'incident.resolved' then true
                    when $12 in ('incident.triggered', 'incident.reopened') then false
                    else incidents.is_frozen
                end
            returning id
            """, connection, transaction);
        upsert.Parameters.AddWithValue(incidentId);
        upsert.Parameters.AddWithValue(webhook.PagerDutyIncidentId);
        upsert.Parameters.AddWithValue(webhook.ServiceId);
        upsert.Parameters.AddWithValue(profile.Id);
        upsert.Parameters.AddWithValue(webhook.Title);
        upsert.Parameters.AddWithValue(webhook.Urgency);
        upsert.Parameters.AddWithValue(state.ToString());
        upsert.Parameters.AddWithValue(webhook.OccurredAt);
        upsert.Parameters.AddWithValue(profile.SlackChannel);
        upsert.Parameters.AddWithValue(JsonSerializer.Serialize(webhook.Labels, JsonOptions));
        upsert.Parameters.AddWithValue(state == IncidentState.Resolved);
        upsert.Parameters.AddWithValue(webhook.EventType);
        upsert.Parameters.AddWithValue(IncidentProgression.Queued);
        upsert.Parameters.AddWithValue(IncidentProgression.Finalizing);
        upsert.Parameters.AddWithValue(webhook.TriggeredAt);
        incidentId = (Guid)(await upsert.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("Incident upsert did not return an id."));

        var now = timeProvider.GetUtcNow();
        var delays = webhook.EventType is "incident.triggered" or "incident.reopened"
            ? new[] { TimeSpan.Zero, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(90) }
            : new[] { TimeSpan.Zero };
        foreach (var delay in delays)
        {
            await using var work = new NpgsqlCommand("""
                insert into work_items(incident_id, kind, idempotency_key, due_at)
                values ($1, 'investigate', $2, $3)
                on conflict (idempotency_key) do nothing
                """, connection, transaction);
            work.Parameters.AddWithValue(incidentId);
            work.Parameters.AddWithValue($"{webhook.EventId}:{(int)delay.TotalSeconds}");
            work.Parameters.AddWithValue(now + delay);
            await work.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return (incidentId, false);
    }

    public async Task<IncidentRecord?> GetIncidentAsync(Guid incidentId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            select id, pagerduty_incident_id, service_id, profile_id, title, urgency, state, triggered_at,
                   updated_at, version, status, is_frozen, report_json::text, slack_channel, slack_timestamp,
                   labels_json::text
            from incidents where id = $1
            """);
        command.Parameters.AddWithValue(incidentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadIncident(reader);
    }

    public async Task<InvestigationReport?> GetReportAsync(Guid incidentId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("select report_json::text from incidents where id = $1");
        command.Parameters.AddWithValue(incidentId);
        var json = await command.ExecuteScalarAsync(cancellationToken) as string;
        return string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<InvestigationReport>(json, JsonOptions);
    }

    public async Task<int> SaveReportAsync(
        IncidentRecord incident,
        InvestigationReport report,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var nextVersion = incident.Version + 1;
        var versioned = report with { Version = nextVersion };
        var json = JsonSerializer.Serialize(versioned, JsonOptions);

        await using var update = new NpgsqlCommand("""
            update incidents set report_json = $2::jsonb, version = $3, status = $4, updated_at = $5
            where id = $1 and version = $6
            """, connection, transaction);
        update.Parameters.AddWithValue(incident.Id);
        update.Parameters.AddWithValue(json);
        update.Parameters.AddWithValue(nextVersion);
        update.Parameters.AddWithValue(report.Status);
        update.Parameters.AddWithValue(report.UpdatedAt);
        update.Parameters.AddWithValue(incident.Version);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("Incident report was updated concurrently; the work item will be retried.");
        }

        foreach (var finding in report.Evidence)
        {
            await using var insert = new NpgsqlCommand("""
                insert into evidence(incident_id, report_version, finding_id, source, occurred_at, payload)
                values ($1, $2, $3, $4, $5, $6::jsonb)
                """, connection, transaction);
            insert.Parameters.AddWithValue(incident.Id);
            insert.Parameters.AddWithValue(nextVersion);
            insert.Parameters.AddWithValue(finding.Id);
            insert.Parameters.AddWithValue(finding.Source);
            insert.Parameters.AddWithValue(finding.OccurredAt);
            insert.Parameters.AddWithValue(JsonSerializer.Serialize(finding, JsonOptions));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        for (var index = 0; index < report.Timeline.Count; index++)
        {
            var timeline = report.Timeline[index];
            await using var insert = new NpgsqlCommand("""
                insert into timeline_events(incident_id, report_version, ordinal, occurred_at, payload)
                values ($1, $2, $3, $4, $5::jsonb)
                """, connection, transaction);
            insert.Parameters.AddWithValue(incident.Id);
            insert.Parameters.AddWithValue(nextVersion);
            insert.Parameters.AddWithValue(index);
            insert.Parameters.AddWithValue(timeline.OccurredAt);
            insert.Parameters.AddWithValue(JsonSerializer.Serialize(timeline, JsonOptions));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var outbox = new NpgsqlCommand("""
            insert into outbox(kind, payload) values ('slack.report', $1::jsonb)
            """, connection, transaction);
        var outboxPayload = JsonSerializer.Serialize(new { incidentId = incident.Id, version = nextVersion });
        outbox.Parameters.AddWithValue(outboxPayload);
        await outbox.ExecuteNonQueryAsync(cancellationToken);

        if (IncidentProgression.NeedsStuckNotification(report.Status))
        {
            await using var stuckCheck = new NpgsqlCommand("""
                insert into outbox(kind, payload, due_at)
                values ('slack.report', $1::jsonb, now() + interval '1 minute')
                """, connection, transaction);
            stuckCheck.Parameters.AddWithValue(outboxPayload);
            await stuckCheck.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return nextVersion;
    }

    public async Task SetStatusAsync(Guid incidentId, string status, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "update incidents set status = $2, updated_at = now() where id = $1");
        command.Parameters.AddWithValue(incidentId);
        command.Parameters.AddWithValue(status);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> RestartInvestigationAsync(
        Guid incidentId,
        string? slackChannel,
        string? slackTimestamp,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var incidentPredicate = "id = $1";
        if (slackChannel is not null)
        {
            incidentPredicate += " and slack_channel = $2";
        }
        if (slackTimestamp is not null)
        {
            var timestampParameter = slackChannel is null ? 2 : 3;
            incidentPredicate += $" and slack_timestamp = ${timestampParameter}";
        }

        await using var incident = new NpgsqlCommand($"""
            select status
            from incidents
            where {incidentPredicate}
            for update
            """, connection, transaction);
        incident.Parameters.AddWithValue(incidentId);
        if (slackChannel is not null)
        {
            incident.Parameters.AddWithValue(slackChannel);
        }
        if (slackTimestamp is not null)
        {
            incident.Parameters.AddWithValue(slackTimestamp);
        }
        var currentStatus = await incident.ExecuteScalarAsync(cancellationToken) as string;
        if (currentStatus is null || !IncidentProgression.CanRequestRestart(currentStatus))
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await using var retire = new NpgsqlCommand("""
            update work_items
            set completed_at = now(), locked_until = null
            where incident_id = $1 and completed_at is null
            """, connection, transaction);
        retire.Parameters.AddWithValue(incidentId);
        await retire.ExecuteNonQueryAsync(cancellationToken);

        await using var update = new NpgsqlCommand(
            "update incidents set status = $2, updated_at = now() where id = $1",
            connection, transaction);
        update.Parameters.AddWithValue(incidentId);
        update.Parameters.AddWithValue(IncidentProgression.Queued);
        await update.ExecuteNonQueryAsync(cancellationToken);

        await using var work = new NpgsqlCommand("""
            insert into work_items(incident_id, kind, idempotency_key, due_at)
            values ($1, 'investigate', $2, now())
            """, connection, transaction);
        work.Parameters.AddWithValue(incidentId);
        work.Parameters.AddWithValue($"manual-restart:{incidentId:N}:{Guid.NewGuid():N}");
        await work.ExecuteNonQueryAsync(cancellationToken);

        await using var stuckCheck = new NpgsqlCommand("""
            insert into outbox(kind, payload, due_at)
            values ('slack.report', jsonb_build_object('incidentId', $1), now() + interval '1 minute')
            """, connection, transaction);
        stuckCheck.Parameters.AddWithValue(incidentId);
        await stuckCheck.ExecuteNonQueryAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task SetSlackTimestampAsync(Guid incidentId, string timestamp, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "update incidents set slack_timestamp = $2 where id = $1 and slack_timestamp is null");
        command.Parameters.AddWithValue(incidentId);
        command.Parameters.AddWithValue(timestamp);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("delete from incidents where updated_at < $1");
        command.Parameters.AddWithValue(cutoff);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static IncidentRecord ReadIncident(NpgsqlDataReader reader)
    {
        var labels = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(15), JsonOptions)
            ?? new Dictionary<string, string>();
        return new IncidentRecord(
            reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            reader.GetString(5), Enum.TryParse<IncidentState>(reader.GetString(6), out var state) ? state : IncidentState.Unknown,
            reader.GetFieldValue<DateTimeOffset>(7), reader.GetFieldValue<DateTimeOffset>(8), reader.GetInt32(9),
            reader.GetString(10), reader.GetBoolean(11), reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.GetString(13), reader.IsDBNull(14) ? null : reader.GetString(14), labels);
    }

    private static IncidentState MapState(string eventType) => eventType switch
    {
        "incident.triggered" or "incident.reopened" => IncidentState.Triggered,
        "incident.acknowledged" => IncidentState.Acknowledged,
        "incident.escalated" => IncidentState.Escalated,
        "incident.reassigned" => IncidentState.Reassigned,
        "incident.resolved" => IncidentState.Resolved,
        _ => IncidentState.Unknown
    };

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}
