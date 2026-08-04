using System.Text;
using Panko.Api.Domain;
using Panko.Api.Cases;
using Panko.Api.Infrastructure;
using Panko.Api.Cases;
using Npgsql;
using SubmittedCrumbKind = Panko.Contracts.SubmittedCrumbKind;
using SubmittedCrumb = Panko.Contracts.SubmittedCrumb;

namespace Panko.Api.Tests;

public sealed class PagerDutyCaseAdapterMappingTests
{
    [Theory]
    [InlineData("incident.triggered", "pagerduty-incident-triggered", PagerDutyIncidentState.Triggered)]
    [InlineData("incident.acknowledged", "pagerduty-incident-acknowledged", PagerDutyIncidentState.Acknowledged)]
    [InlineData("incident.escalated", "pagerduty-incident-escalated", PagerDutyIncidentState.Escalated)]
    [InlineData("incident.reassigned", "pagerduty-incident-reassigned", PagerDutyIncidentState.Reassigned)]
    [InlineData("incident.resolved", "pagerduty-incident-resolved", PagerDutyIncidentState.Resolved)]
    [InlineData("incident.reopened", "pagerduty-incident-reopened", PagerDutyIncidentState.Triggered)]
    public void MapsEveryPagerDutyLifecycleEventToCanonicalVocabulary(
        string eventType,
        string category,
        PagerDutyIncidentState state)
    {
        var webhook = Webhook(eventType);

        var mapped = PagerDutyCaseAdapter.Map(webhook, "payments-production");

        Assert.Equal(CaseOriginKind.PagerDuty, mapped.Origin.Kind);
        Assert.Equal("PINCIDENT", mapped.Origin.ExternalId);
        Assert.Equal(state, mapped.PagerDutyState);
        Assert.Equal(category, mapped.LifecycleCrumb.Category);
        Assert.Equal(webhook.EventId, mapped.LifecycleCrumb.ClientCrumbId);
        Assert.Equal("pagerduty", mapped.LifecycleCrumb.DeclaredSource);
        Assert.Equal("PINCIDENT", mapped.LifecycleCrumb.SourceReference);
        Assert.Equal("pagerduty-incident", mapped.LifecycleCrumb.ObjectType);
        Assert.Equal("PINCIDENT", mapped.LifecycleCrumb.ObjectId);
    }

    private static PagerDutyWebhookEvent Webhook(string eventType) => new(
        $"event-{eventType}",
        eventType,
        "PINCIDENT",
        "P123",
        "Payments failing",
        "high",
        "https://pagerduty.example/incidents/PINCIDENT",
        DateTimeOffset.Parse("2026-08-03T09:45:00Z"),
        DateTimeOffset.Parse("2026-08-03T09:50:00Z"),
        new Dictionary<string, string> { ["environment"] = "production" });
}

[Collection(PostgresPatternCollection.Name)]
public sealed class PagerDutyCaseAdapterPersistenceTests(PostgresFixture database) : IAsyncLifetime
{
    public Task InitializeAsync() => database.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task PagerDutyLifecycleIsCollectedCanonicalInputAndPreservesRefreshSchedule()
    {
        var now = DateTimeOffset.Parse("2026-08-03T10:00:00Z");
        var repository = new PostgresCaseStore(database.DataSource, new FixedTimeProvider(now));
        var webhook = Webhook("event-triggered", "incident.triggered");

        var accepted = await repository.AcceptWebhookAsync(
            webhook,
            BuildRecipe(),
            Encoding.UTF8.GetBytes("{\"event\":\"triggered\"}"),
            CancellationToken.None);

        await using (var canonical = database.DataSource.CreateCommand("""
            select producer_principal, client_crumb_id, category, declared_source, source_reference,
                   object_type, object_id, trust_level
            from case_inputs
            where case_id = $1
            """))
        {
            canonical.Parameters.AddWithValue(accepted.CaseId);
            await using var reader = await canonical.ExecuteReaderAsync(CancellationToken.None);
            Assert.True(await reader.ReadAsync(CancellationToken.None));
            Assert.Equal("pagerduty-adapter", reader.GetString(0));
            Assert.Equal("event-triggered", reader.GetString(1));
            Assert.Equal("pagerduty-incident-triggered", reader.GetString(2));
            Assert.Equal("pagerduty", reader.GetString(3));
            Assert.Equal("PD-WEBHOOK-1", reader.GetString(4));
            Assert.Equal("pagerduty-incident", reader.GetString(5));
            Assert.Equal("PD-WEBHOOK-1", reader.GetString(6));
            Assert.Equal("collected", reader.GetString(7));
        }

        await using var schedule = database.DataSource.CreateCommand("""
            select kind, due_at
            from work_items
            where case_id = $1
            order by due_at
            """);
        schedule.Parameters.AddWithValue(accepted.CaseId);
        await using var scheduleReader = await schedule.ExecuteReaderAsync(CancellationToken.None);
        var due = new List<(string Kind, DateTimeOffset DueAt)>();
        while (await scheduleReader.ReadAsync(CancellationToken.None))
        {
            due.Add((scheduleReader.GetString(0), scheduleReader.GetFieldValue<DateTimeOffset>(1)));
        }
        Assert.Equal(
            [
                ("build-case", now),
                ("build-case", now.AddSeconds(30)),
                ("build-case", now.AddSeconds(90))
            ],
            due);
    }

    [Fact]
    public async Task NonPagerDutyOriginPersistsWithoutPagerDutyIdentityAndQueuesOnlyProjection()
    {
        var repository = new PostgresCaseStore(database.DataSource, TimeProvider.System);
        var occurredAt = DateTimeOffset.Parse("2026-08-03T09:45:00Z");
        var originEvent = new AcceptCaseOriginEvent(
            new CaseOrigin(CaseOriginKind.Manual, "manual-001"),
            "payments-production",
            "payments-api",
            "Manual Case",
            "low",
            PagerDutyIncidentState.Triggered,
            occurredAt,
            occurredAt,
            new Dictionary<string, string> { ["environment"] = "production" },
            new SubmittedCrumb(
                "manual-event-001",
                SubmittedCrumbKind.Event,
                occurredAt,
                "case-created",
                "info",
                "Case created",
                DeclaredSource: "manual"));

        var accepted = await repository.AcceptOriginEventAsync(
            originEvent,
            BuildRecipe(),
            new CaseOriginEventReceipt(
                "manual-adapter",
                "manual-event-001",
                "case.created",
                Encoding.UTF8.GetBytes("{\"event\":\"created\"}")),
            CancellationToken.None);
        var stored = await repository.GetCaseAsync(accepted.CaseId, CancellationToken.None);

        Assert.NotNull(stored);
        Assert.Null(stored.PagerDutyIncidentId);
        Assert.Equal(CaseOriginKind.Manual, stored.Origin.Kind);
        Assert.Equal("manual-001", stored.Origin.ExternalId);
        Assert.False(stored.PublishToSlack);
        await using var work = database.DataSource.CreateCommand("""
            select kind, target_input_version
            from work_items
            where case_id = $1
            """);
        work.Parameters.AddWithValue(accepted.CaseId);
        await using var reader = await work.ExecuteReaderAsync(CancellationToken.None);
        Assert.True(await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(CaseWorkKinds.Project, reader.GetString(0));
        Assert.Equal(0, reader.GetInt64(1));
        Assert.False(await reader.ReadAsync(CancellationToken.None));
    }

    private static PagerDutyWebhookEvent Webhook(string eventId, string eventType) => new(
        eventId,
        eventType,
        "PD-WEBHOOK-1",
        "P123",
        "Payments failing",
        "high",
        "https://pagerduty.example/incidents/PD-WEBHOOK-1",
        DateTimeOffset.Parse("2026-08-03T09:45:00Z"),
        DateTimeOffset.Parse("2026-08-03T09:50:00Z"),
        new Dictionary<string, string> { ["environment"] = "production" });

    private static Recipe BuildRecipe() => new()
    {
        Id = "payments-production",
        PagerDutyServiceId = "P123",
        Team = "payments",
        SlackChannel = "#cases"
    };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

internal static class PagerDutyRepositoryTestExtensions
{
    public static Task<(Guid CaseId, bool IsDuplicate)> AcceptWebhookAsync(
        this PostgresCaseStore repository,
        PagerDutyWebhookEvent webhook,
        Recipe recipe,
        ReadOnlyMemory<byte> rawPayload,
        CancellationToken cancellationToken) =>
        repository.AcceptOriginEventAsync(
            PagerDutyCaseAdapter.Map(webhook, recipe.Id),
            recipe,
            new CaseOriginEventReceipt(
                PagerDutyCaseAdapter.ProducerPrincipal,
                webhook.EventId,
                webhook.EventType,
                rawPayload,
                webhook.EventId.StartsWith(PagerDutyPullService.EventIdPrefix, StringComparison.Ordinal)),
            cancellationToken);
}
