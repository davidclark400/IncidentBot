using System.Text.Json.Nodes;
using Panko.Api.Domain;
using Panko.Api.Cases;
using SubmittedCrumbKind = Panko.Contracts.SubmittedCrumbKind;

namespace Panko.Api.Tests;

public sealed class CaseProjectionTests
{
    private static readonly Guid CaseId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTimeOffset TriggeredAt =
        DateTimeOffset.Parse("2026-08-03T09:45:00Z");
    private static readonly DateTimeOffset ProjectedAt =
        DateTimeOffset.Parse("2026-08-03T10:15:00Z");

    [Fact]
    public void BuildOrdersEqualTimestampsByDurableSequenceThenStableId()
    {
        var caseRecord = BuildCase(inputVersion: 2);
        var first = Input(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"),
            sequence: 1,
            inputVersion: 1,
            occurredAt: TriggeredAt.AddMinutes(5),
            summary: "first durable input");
        var second = Input(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002"),
            sequence: 2,
            inputVersion: 2,
            occurredAt: TriggeredAt.AddMinutes(5),
            summary: "second durable input");
        var connector = SourceResult(
            trail:
            [
                new TrailCandidate(
                    "connector-z",
                    TriggeredAt.AddMinutes(5),
                    "gitlab",
                    "deployment",
                    "connector event z",
                    "warning",
                    null),
                new TrailCandidate(
                    "connector-a",
                    TriggeredAt.AddMinutes(5),
                    "gitlab",
                    "deployment",
                    "connector event a",
                    "warning",
                    null)
            ]);

        var caseFile = Builder().Build(
            caseRecord,
            BuildRecipe(),
            "revision-1",
            2,
            [second, CreatedInput(), first],
            [connector],
            PendingAi(),
            null);

        Assert.Equal(
            new[]
            {
                "Case created by agent",
                "first durable input",
                "second durable input",
                "connector event a",
                "connector event z"
            },
            caseFile.Trail.Select(item => item.Summary));
    }

    [Fact]
    public void BuildDeduplicatesTrailByStableIdAndKeepsTheStrongestCandidate()
    {
        var inputId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
        var stableId = $"case-input:{inputId:N}:trail";
        var submitted = Input(
            inputId,
            sequence: 1,
            inputVersion: 1,
            occurredAt: TriggeredAt.AddMinutes(1),
            summary: "submitted observation",
            severity: "warning");
        var collected = new TrailCandidate(
            stableId,
            submitted.OccurredAt,
            "gitlab",
            "deployment",
            "independently collected observation",
            "critical",
            "https://gitlab.example/deployments/42");

        var caseFile = Builder().Build(
            BuildCase(inputVersion: 1),
            BuildRecipe(),
            "revision-1",
            1,
            [CreatedInput(), submitted],
            [SourceResult(trail: [collected])],
            PendingAi(),
            null);

        var retained = Assert.Single(caseFile.Trail, item => item.StableId == stableId);
        Assert.Equal("independently collected observation", retained.Summary);
        Assert.Equal("gitlab", retained.Source);
        Assert.Equal("critical", retained.Severity);
    }

    [Fact]
    public void BoundedTrailRetainsCreationAndNewestDurableInputsBeforeOrdering()
    {
        var inputs = Enumerable.Range(1, 250)
            .Select(sequence => Input(
                Guid.Parse($"aaaaaaaa-0000-0000-0000-{sequence:D12}"),
                sequence,
                sequence,
                TriggeredAt.AddMinutes(sequence),
                $"historic input {sequence}"))
            .Prepend(CreatedInput())
            .ToList();
        var newest = Input(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000251"),
            sequence: 251,
            inputVersion: 251,
            occurredAt: TriggeredAt.AddMinutes(300),
            summary: "newest accepted backdated input") with
        {
            ReceivedAt = TriggeredAt.AddDays(1)
        };
        inputs.Add(newest);

        var caseFile = Builder().Build(
            BuildCase(inputVersion: 251),
            BuildRecipe(),
            "revision-1",
            251,
            inputs,
            [],
            PendingAi(),
            null);

        Assert.Equal(250, caseFile.Trail.Count);
        Assert.Contains(caseFile.Trail, item => item.Kind == "case-created");
        Assert.Contains(caseFile.Trail, item => item.Summary == newest.Summary);
        Assert.DoesNotContain(caseFile.Trail, item => item.Summary == "historic input 1");
        Assert.Equal(
            caseFile.Trail.OrderBy(item => item.OccurredAt)
                .ThenBy(item => item.StableId, StringComparer.Ordinal),
            caseFile.Trail);
    }

    [Fact]
    public void BuildUsesThePointInTimeActiveSetForSupersessionAndRetraction()
    {
        var originalId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
        var original = Input(
            originalId,
            sequence: 1,
            inputVersion: 1,
            occurredAt: TriggeredAt.AddMinutes(1),
            summary: "original observation") with
        {
            RetractedAt = TriggeredAt.AddMinutes(3),
            RetractedInputVersion = 2
        };
        var replacement = Input(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002"),
            sequence: 2,
            inputVersion: 2,
            occurredAt: TriggeredAt.AddMinutes(2),
            summary: "corrected observation") with
        {
            SupersedesCrumbId = originalId
        };
        var caseRecord = BuildCase(inputVersion: 2);

        // The repository supplies the active set as of the requested input version: the original
        // remains visible at v1, while only the replacement is active at v2.
        var historic = Builder().Build(
            caseRecord,
            BuildRecipe(),
            "revision-1",
            1,
            [CreatedInput(), original],
            [],
            PendingAi(),
            null);
        var current = Builder().Build(
            caseRecord,
            BuildRecipe(),
            "revision-1",
            2,
            [CreatedInput(), replacement],
            [],
            PendingAi(),
            null);

        Assert.Contains(historic.Crumbs, item => item.Summary == "original observation");
        Assert.DoesNotContain(historic.Crumbs, item => item.Summary == "corrected observation");
        Assert.DoesNotContain(current.Crumbs, item => item.Summary == "original observation");
        Assert.Contains(current.Crumbs, item => item.Summary == "corrected observation");
        Assert.DoesNotContain(current.Trail, item => item.Summary == "original observation");
    }

    [Fact]
    public void SubmittedInputsUseServerTrustConfidenceAndProvenancePolicy()
    {
        var submittedEvent = Input(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"),
            sequence: 1,
            inputVersion: 1,
            occurredAt: TriggeredAt.AddMinutes(1),
            summary: "agent event",
            kind: SubmittedCrumbKind.Event,
            declaredSource: "pagerduty");
        var submittedCrumb = Input(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002"),
            sequence: 2,
            inputVersion: 2,
            occurredAt: TriggeredAt.AddMinutes(2),
            summary: "agent crumb",
            kind: SubmittedCrumbKind.Crumb,
            declaredSource: "gitlab") with
        {
            SourceReference = "pipeline-42",
            Attributes = new JsonObject { ["environment"] = "production" }
        };
        var submittedNote = Input(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003"),
            sequence: 3,
            inputVersion: 3,
            occurredAt: TriggeredAt.AddMinutes(3),
            summary: "agent note",
            kind: SubmittedCrumbKind.Note,
            declaredSource: "victorialogs");

        var caseFile = Builder().Build(
            BuildCase(inputVersion: 3),
            BuildRecipe(),
            "revision-1",
            3,
            [CreatedInput(), submittedEvent, submittedCrumb, submittedNote],
            [],
            PendingAi(),
            null);

        Assert.All(caseFile.Crumbs, crumb => Assert.Equal("submitted", crumb.Source));
        Assert.Equal(0.50, CrumbBySummary("agent event").Confidence);
        Assert.Equal(0.55, CrumbBySummary("agent crumb").Confidence);
        Assert.Equal(0.20, CrumbBySummary("agent note").Confidence);
        Assert.Equal("gitlab", CrumbBySummary("agent crumb").Provenance["declaredSource"]?.GetValue<string>());
        Assert.Equal("pipeline-42", CrumbBySummary("agent crumb").Provenance["sourceReference"]?.GetValue<string>());
        Assert.Equal("submitted", CrumbBySummary("agent crumb").Provenance["trustLevel"]?.GetValue<string>());
        Assert.Equal("agent@example.internal", CrumbBySummary("agent crumb").Provenance["producerPrincipal"]?.GetValue<string>());
        Assert.Equal("crumb", CrumbBySummary("agent crumb").Provenance["inputType"]?.GetValue<string>());
        Assert.Equal("production", CrumbBySummary("agent crumb").Provenance["attributes"]?["environment"]?.GetValue<string>());
        Assert.Null(CrumbBySummary("agent crumb").CodeReferences);
        var source = Assert.Single(caseFile.CrumbSources);
        Assert.Equal("submitted", source.Source);
        Assert.Equal(3, source.CrumbCount);
        Assert.Contains("across 1 effective source", caseFile.DeterministicSummary, StringComparison.Ordinal);

        Crumb CrumbBySummary(string summary) => caseFile.Crumbs.Single(item => item.Summary == summary);
    }

    [Fact]
    public void SubmittedNotesRemainVisibleButNeverBecomeCausalMarkers()
    {
        var causalEvent = Input(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"),
            sequence: 1,
            inputVersion: 1,
            occurredAt: TriggeredAt.AddMinutes(1),
            summary: "deployment event",
            kind: SubmittedCrumbKind.Event,
            category: "deployment");
        var note = Input(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002"),
            sequence: 2,
            inputVersion: 2,
            occurredAt: TriggeredAt.AddMinutes(2),
            summary: "deployment note",
            kind: SubmittedCrumbKind.Note,
            category: "deployment");

        var caseFile = Builder().Build(
            BuildCase(inputVersion: 2),
            BuildRecipe(),
            "revision-1",
            2,
            [CreatedInput(), causalEvent, note],
            [],
            PendingAi(),
            null);

        Assert.Contains(caseFile.Trail, item => item.Summary == "deployment note");
        Assert.Contains(caseFile.Crumbs, item => item.Summary == "deployment note");
        var causal = Assert.Single(caseFile.CausalMarkers!);
        Assert.Equal("deployment event", causal.Summary);
        Assert.DoesNotContain(caseFile.CausalMarkers!, item => item.Summary == "deployment note");
    }

    [Fact]
    public void StaleTargetCarriesExactProjectedVersionAndExcludesFutureInputs()
    {
        var caseRecord = BuildCase(inputVersion: 5);
        var throughTarget = Input(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003"),
            sequence: 3,
            inputVersion: 3,
            occurredAt: TriggeredAt.AddMinutes(3),
            summary: "included at target");
        var future = Input(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000004"),
            sequence: 4,
            inputVersion: 4,
            occurredAt: TriggeredAt.AddMinutes(4),
            summary: "accepted after target");

        var caseFile = Builder().Build(
            caseRecord,
            BuildRecipe(),
            "revision-1",
            3,
            [CreatedInput(), future, throughTarget],
            [],
            PendingAi(),
            null);

        Assert.Equal(5, caseFile.InputVersion);
        Assert.Equal(3, caseFile.ProjectedInputVersion);
        Assert.Equal(CaseProgression.Rebuilding, caseFile.Status);
        Assert.Contains(caseFile.Crumbs, item => item.Summary == "included at target");
        Assert.DoesNotContain(caseFile.Crumbs, item => item.Summary == "accepted after target");
    }

    [Fact]
    public void CurrentTargetIsReadyAndUnacceptedTargetsAreRejected()
    {
        var caseRecord = BuildCase(inputVersion: 5);
        var builder = Builder();

        var current = builder.Build(
            caseRecord,
            BuildRecipe(),
            "revision-1",
            5,
            [CreatedInput()],
            [],
            PendingAi(),
            null);

        Assert.Equal(CaseProgression.Ready, current.Status);
        Assert.Equal(5, current.ProjectedInputVersion);
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.Build(
            caseRecord, BuildRecipe(), "revision-1", 6, [CreatedInput()], [], PendingAi(), null));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.Build(
            caseRecord, BuildRecipe(), "revision-1", -1, [CreatedInput()], [], PendingAi(), null));
    }

    private static CaseFileProjectionBuilder Builder() =>
        new(new FixedTimeProvider(ProjectedAt));

    private static CaseRecord BuildCase(long inputVersion) => new(
        CaseId,
        null,
        "payments-api",
        "payments-production",
        "Payment timeouts",
        "high",
        PagerDutyIncidentState.Triggered,
        TriggeredAt,
        TriggeredAt,
        7,
        CaseProgression.Open,
        false,
        null,
        string.Empty,
        null,
        new Dictionary<string, string>())
    {
        Origin = new CaseOrigin(CaseOriginKind.Agent, "agent-run-001"),
        InputVersion = inputVersion,
        ProjectedInputVersion = 0,
        CreatedBy = "agent@example.internal",
        PublishToSlack = false
    };

    private static Recipe BuildRecipe() => new()
    {
        Id = "payments-production",
        AgentCases = new AgentCasePolicy
        {
            Enabled = true,
            AllowedInputCategories = ["deployment", "note"]
        }
    };

    private static CaseInput CreatedInput() => new(
        Guid.Parse("aaaaaaaa-0000-0000-0000-000000000000"),
        CaseId,
        0,
        0,
        "panko",
        "case-created",
        SubmittedCrumbKind.Event,
        TriggeredAt,
        TriggeredAt,
        "case-created",
        "critical",
        "Case created by agent",
        null,
        null,
        null,
        null,
        null,
        "case",
        CaseId.ToString(),
        new JsonObject(),
        "system",
        "created-hash",
        null,
        null,
        null);

    private static CaseInput Input(
        Guid id,
        long sequence,
        long inputVersion,
        DateTimeOffset occurredAt,
        string summary,
        SubmittedCrumbKind kind = SubmittedCrumbKind.Event,
        string category = "deployment",
        string severity = "warning",
        string? declaredSource = "gitlab") => new(
        id,
        CaseId,
        sequence,
        inputVersion,
        "agent@example.internal",
        $"client-{sequence}",
        kind,
        occurredAt,
        TriggeredAt.AddMinutes(sequence),
        category,
        severity,
        summary,
        "bounded excerpt",
        declaredSource,
        null,
        null,
        "deploy-bot",
        "deployment",
        sequence.ToString(),
        new JsonObject(),
        "submitted",
        $"hash-{sequence}",
        null,
        null,
        null);

    private static CrumbSourceResult SourceResult(
        IReadOnlyList<Crumb>? crumbs = null,
        IReadOnlyList<TrailCandidate>? trail = null) => new(
        "gitlab",
        CrumbSourceHealth.Complete,
        crumbs ?? [],
        trail ?? [],
        [],
        10,
        null);

    private static AiSynthesis PendingAi() => new("pending", null, [], [], [], null);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
