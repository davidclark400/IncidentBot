using System.Text.Json;
using Panko.Api.Security;
using Npgsql;
using NpgsqlTypes;

namespace Panko.Api.Infrastructure;

public sealed class PostgresSecurityAuditTrail(
    NpgsqlDataSource dataSource,
    TimeProvider timeProvider) : ISecurityAuditTrail
{
    public async Task RecordAsync(SecurityAuditEvent auditEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        ArgumentNullException.ThrowIfNull(auditEvent.Actor);
        await using var command = dataSource.CreateCommand("""
            insert into security_audit_events(
                id, occurred_at, action, outcome, actor_id, authentication_source,
                actor_teams, target_team, recipe_id, case_id, metadata)
            values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11::jsonb)
            """);
        Add(command.Parameters, NpgsqlDbType.Uuid, Guid.NewGuid());
        Add(command.Parameters, NpgsqlDbType.TimestampTz, timeProvider.GetUtcNow());
        Add(command.Parameters, NpgsqlDbType.Text, Bound(auditEvent.Action, 128));
        Add(command.Parameters, NpgsqlDbType.Text, Bound(auditEvent.Outcome, 64));
        Add(command.Parameters, NpgsqlDbType.Text, Bound(auditEvent.Actor.Id, 512));
        Add(
            command.Parameters,
            NpgsqlDbType.Text,
            Bound(auditEvent.Actor.AuthenticationSource, 128));
        Add(
            command.Parameters,
            NpgsqlDbType.Array | NpgsqlDbType.Text,
            auditEvent.Actor.Teams.Select(team => Bound(team, 64)).Take(64).ToArray());
        AddNullable(command.Parameters, auditEvent.TargetTeam, 64);
        AddNullable(command.Parameters, auditEvent.RecipeId, 128);
        AddNullable(command.Parameters, NpgsqlDbType.Uuid, auditEvent.CaseId);
        var metadata = BoundMetadata(auditEvent.Metadata);
        Add(command.Parameters, NpgsqlDbType.Jsonb, JsonSerializer.Serialize(metadata));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static Dictionary<string, string> BoundMetadata(
        IReadOnlyDictionary<string, string>? supplied)
    {
        var bounded = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in supplied?.Take(32) ?? [])
        {
            bounded.TryAdd(Bound(pair.Key, 64), Bound(pair.Value, 512));
        }
        return bounded;
    }

    private static void Add(
        NpgsqlParameterCollection parameters,
        NpgsqlDbType type,
        object value) =>
        parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = type,
            Value = value
        });

    private static void AddNullable(NpgsqlParameterCollection parameters, string? value, int maximumLength)
    {
        Add(
            parameters,
            NpgsqlDbType.Text,
            string.IsNullOrWhiteSpace(value) ? DBNull.Value : Bound(value, maximumLength));
    }

    private static void AddNullable(
        NpgsqlParameterCollection parameters,
        NpgsqlDbType type,
        object? value) =>
        Add(parameters, type, value ?? DBNull.Value);

    private static string Bound(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];
}
