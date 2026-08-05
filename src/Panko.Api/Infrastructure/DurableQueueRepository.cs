using Panko.Api.Domain;
using Panko.Api.Cases;
using Npgsql;

namespace Panko.Api.Infrastructure;

public sealed class DurableQueueRepository(NpgsqlDataSource dataSource) :
    IDurableQueue<WorkItem>,
    IDurableQueue<OutboxItem>
{
    async Task<WorkItem?> IDurableQueue<WorkItem>.LeaseAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            select id, case_id, kind, attempts, target_input_version,
                   target_workflow_generation
            from work_items
            where completed_at is null and due_at <= now()
              and (locked_until is null or locked_until < now())
            order by due_at, id
            for update skip locked
            limit 1
            """, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            await reader.DisposeAsync();
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var item = new WorkItem(
            reader.GetInt64(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.GetInt32(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4),
            reader.IsDBNull(5) ? null : reader.GetInt64(5));
        await reader.DisposeAsync();
        await using var lease = new NpgsqlCommand(
            "update work_items set locked_until = now() + interval '2 minutes', attempts = attempts + 1 where id = $1",
            connection, transaction);
        lease.Parameters.AddWithValue(item.Id);
        await lease.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return item with { Attempts = item.Attempts + 1 };
    }

    Task IDurableQueue<WorkItem>.CompleteAsync(WorkItem item, CancellationToken cancellationToken) =>
        ExecuteAsync(
            "update work_items set completed_at = now(), locked_until = null where id = $1",
            item.Id,
            cancellationToken);

    async Task IDurableQueue<WorkItem>.FailAsync(
        WorkItem item,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var delaySeconds = Math.Min(60, (int)Math.Pow(2, Math.Min(item.Attempts, 5)));
        await FailAsync("work_items", item.Id, delaySeconds, exception, cancellationToken);
    }

    async Task<OutboxItem?> IDurableQueue<OutboxItem>.LeaseAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            select id, kind, payload::text, attempts from outbox
            where processed_at is null and due_at <= now()
              and (locked_until is null or locked_until < now())
            order by due_at, id for update skip locked limit 1
            """, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            await reader.DisposeAsync();
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var item = new OutboxItem(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3));
        await reader.DisposeAsync();
        await using var lease = new NpgsqlCommand(
            "update outbox set locked_until = now() + interval '1 minute', attempts = attempts + 1 where id = $1",
            connection, transaction);
        lease.Parameters.AddWithValue(item.Id);
        await lease.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return item with { Attempts = item.Attempts + 1 };
    }

    Task IDurableQueue<OutboxItem>.CompleteAsync(OutboxItem item, CancellationToken cancellationToken) =>
        ExecuteAsync(
            "update outbox set processed_at = now(), locked_until = null where id = $1",
            item.Id,
            cancellationToken);

    async Task IDurableQueue<OutboxItem>.FailAsync(
        OutboxItem item,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var delaySeconds = Math.Min(300, (int)Math.Pow(2, Math.Min(item.Attempts, 8)));
        await FailAsync("outbox", item.Id, delaySeconds, exception, cancellationToken);
    }

    private async Task FailAsync(
        string table,
        long id,
        int delaySeconds,
        Exception exception,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand($"""
            update {table} set locked_until = null, due_at = now() + make_interval(secs => $2), last_error = $3
            where id = $1
            """);
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(delaySeconds);
        command.Parameters.AddWithValue(Truncate(exception.Message, 1000));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task ExecuteAsync(string sql, long id, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}
