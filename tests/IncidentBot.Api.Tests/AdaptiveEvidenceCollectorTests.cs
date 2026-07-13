using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentBot.Api.Connectors;
using IncidentBot.Api.Domain;
using IncidentBot.Api.Incidents;
using IncidentBot.Api.Options;
using Microsoft.Extensions.Logging.Abstractions;

namespace IncidentBot.Api.Tests;

public sealed class AdaptiveEvidenceCollectorTests
{
    private static readonly DateTimeOffset TriggeredAt = DateTimeOffset.Parse("2026-07-11T10:00:00Z");
    private static readonly DateTimeOffset CollectionEnd = DateTimeOffset.Parse("2026-07-11T10:05:00Z");

    [Fact]
    public async Task ExpandsThroughDisjointRingsUntilChangeCorroboratesRecentFailure()
    {
        var logs = new RecordingConnector("victorialogs", scope => Result("victorialogs",
            scope.End == CollectionEnd
                ? [Finding("first-error", "victorialogs", "first-error", "warning", TriggeredAt, "Payment handler failed")]
                : []));
        var gitLab = new RecordingConnector("gitlab", scope => Result("gitlab",
            CumulativeLookbackMinutes(scope) == 60
                ? [Finding("deployment", "gitlab", "deployment", "info", TriggeredAt.AddMinutes(-45), "Revision deployed")]
                : []));

        var collection = await Collector(initialMinutes: 30, maximumMinutes: 240).CollectAsync(
            Context(), "test-v1", [logs, gitLab], CancellationToken.None);

        Assert.Equal(EvidenceCollectionCompletionReason.ClearResult, collection.Outcome.CompletionReason);
        Assert.Equal(EvidenceClarityReason.ChangePrecedesFailure, collection.Outcome.Clarity.Reason);
        Assert.Equal(2, collection.Outcome.PassCount);
        Assert.Equal(60, collection.Outcome.FinalLookbackMinutes);
        Assert.Equal(["deployment", "first-error"], collection.Outcome.Clarity.SupportingEvidenceIds);
        AssertDisjointThirtyAndSixtyMinuteRings(logs.Scopes);
        AssertDisjointThirtyAndSixtyMinuteRings(gitLab.Scopes);
        Assert.Equal(2, collection.ConnectorResults.Sum(result => result.Findings.Count));
    }

    [Fact]
    public async Task CorroboratedIndependentSourcesStopTheInitialPass()
    {
        var logs = new RecordingConnector("victorialogs", _ => Result("victorialogs",
        [
            Finding("logs", "victorialogs", "first-error", "warning", TriggeredAt, "First payment error")
        ]));
        var metrics = new RecordingConnector("grafana", _ => Result("grafana",
        [
            Finding("metric", "grafana", "metric", "warning", TriggeredAt.AddMinutes(2), "Error rate elevated")
        ]));

        var collection = await Collector(initialMinutes: 30, maximumMinutes: 240).CollectAsync(
            Context(), "test-v1", [logs, metrics], CancellationToken.None);

        Assert.Equal(EvidenceCollectionCompletionReason.ClearResult, collection.Outcome.CompletionReason);
        Assert.Equal(EvidenceClarityReason.CorroboratedSignals, collection.Outcome.Clarity.Reason);
        Assert.Equal(1, collection.Outcome.PassCount);
        Assert.Single(logs.Scopes);
        Assert.Single(metrics.Scopes);
    }

    [Fact]
    public async Task StructuredExplicitFailureStopsTheInitialPass()
    {
        var provenance = new JsonObject
        {
            ["scope"] = new JsonObject
            {
                ["firstHardFailure"] = true,
                ["allowFailure"] = false
            }
        };
        var connector = new RecordingConnector("gitlab", _ => Result("gitlab",
        [
            Finding("job", "gitlab", "pipeline-job-output", "critical", TriggeredAt, "Build failed") with
            {
                Provenance = provenance
            }
        ]));

        var collection = await Collector(initialMinutes: 30, maximumMinutes: 240).CollectAsync(
            Context(), "test-v1", [connector], CancellationToken.None);

        Assert.Equal(EvidenceCollectionCompletionReason.ClearResult, collection.Outcome.CompletionReason);
        Assert.Equal(EvidenceClarityReason.ExplicitFailure, collection.Outcome.Clarity.Reason);
        Assert.Equal(["job"], collection.Outcome.Clarity.SupportingEvidenceIds);
        Assert.Single(connector.Scopes);
    }

    [Fact]
    public async Task SingleGenericHighSignalStopsOnlyAtTheConfiguredMaximum()
    {
        var connector = new RecordingConnector("grafana", scope => Result("grafana",
        [
            Finding(
                $"metric-{CumulativeLookbackMinutes(scope)}",
                "grafana",
                "metric",
                "warning",
                scope.Start,
                "Elevated latency")
        ]));

        var collection = await Collector(initialMinutes: 30, maximumMinutes: 100).CollectAsync(
            Context(), "test-v1", [connector], CancellationToken.None);

        Assert.Equal(EvidenceCollectionCompletionReason.MaximumWindowReached, collection.Outcome.CompletionReason);
        Assert.False(collection.Outcome.Clarity.IsClear);
        Assert.Equal([30, 60, 100], connector.Scopes.Select(CumulativeLookbackMinutes));
        Assert.Collection(
            connector.Scopes,
            first => Assert.Equal((TriggeredAt.AddMinutes(-30), CollectionEnd), (first.Start, first.End)),
            second => Assert.Equal((TriggeredAt.AddMinutes(-60), TriggeredAt.AddMinutes(-30)), (second.Start, second.End)),
            third => Assert.Equal((TriggeredAt.AddMinutes(-100), TriggeredAt.AddMinutes(-60)), (third.Start, third.End)));
    }

    [Fact]
    public async Task ExactSnapshotConnectorRunsOnlyInTheInitialPassAndReturnsExplicitOutcome()
    {
        var connector = new RecordingConnector(
            "pagerduty",
            scope => Result("pagerduty",
            [
                Finding("incident", "pagerduty", "incident", "info", scope.End, "Incident snapshot")
            ]),
            supportsWindowExpansion: false);

        var collection = await Collector(initialMinutes: 30, maximumMinutes: 240).CollectAsync(
            Context(), "test-v1", [connector], CancellationToken.None);

        Assert.Equal(EvidenceCollectionCompletionReason.NoExpandableConnectors, collection.Outcome.CompletionReason);
        Assert.False(collection.Outcome.Clarity.IsClear);
        Assert.Equal(1, collection.Outcome.PassCount);
        Assert.Equal([30], connector.Scopes.Select(CumulativeLookbackMinutes));
    }

    [Fact]
    public async Task NoConnectorsReturnsBoundedInconclusiveOutcome()
    {
        var collection = await Collector(initialMinutes: 30, maximumMinutes: 240).CollectAsync(
            Context(), "test-v1", [], CancellationToken.None);

        Assert.Empty(collection.ConnectorResults);
        Assert.Equal(EvidenceCollectionCompletionReason.NoConnectors, collection.Outcome.CompletionReason);
        Assert.Equal(0, collection.Outcome.PassCount);
        Assert.Equal(0, collection.Outcome.FinalLookbackMinutes);
        Assert.False(collection.Outcome.Clarity.IsClear);
    }

    [Fact]
    public async Task RetainsNarrowWindowEvidenceWhenTheOlderRingIsUnavailable()
    {
        var connector = new RecordingConnector("victorialogs", scope =>
            CumulativeLookbackMinutes(scope) == 30
                ? Result("victorialogs",
                [
                    Finding("narrow-context", "victorialogs", "annotation", "info", scope.Start, "Deployment context")
                ])
                : ConnectorResult.Unavailable("victorialogs", 15, "older ring query failed"));

        var collection = await Collector(initialMinutes: 30, maximumMinutes: 60).CollectAsync(
            Context(), "test-v1", [connector], CancellationToken.None);
        var result = Assert.Single(collection.ConnectorResults);

        Assert.Equal(EvidenceCollectionCompletionReason.MaximumWindowReached, collection.Outcome.CompletionReason);
        Assert.Equal(SourceHealth.Partial, result.Health);
        Assert.Equal("narrow-context", Assert.Single(result.Findings).Id);
        Assert.Equal(25, result.DurationMilliseconds);
        Assert.Equal("older ring query failed", result.Diagnostic);
    }

    [Fact]
    public async Task AccumulatedFindingsAreCappedAcrossAllPasses()
    {
        var connector = new RecordingConnector("victorialogs", scope => Result("victorialogs",
            Enumerable.Range(1, 20)
                .Select(index => Finding(
                    $"{CumulativeLookbackMinutes(scope)}-{index}",
                    "victorialogs",
                    "annotation",
                    "info",
                    scope.Start.AddSeconds(index),
                    $"Context {index}"))
                .ToArray()));

        var collection = await Collector(
                initialMinutes: 30,
                maximumMinutes: 60,
                maximumItems: 25)
            .CollectAsync(Context(), "test-v1", [connector], CancellationToken.None);
        var result = Assert.Single(collection.ConnectorResults);

        Assert.Equal(25, result.Findings.Count);
        Assert.Equal(SourceHealth.Partial, result.Health);
        Assert.Contains("retained at most 25", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuplicateTimelineEventsAcrossRingEdgesDoNotMarkTheSourcePartial()
    {
        var timeline = new TimelineCandidate(
            TriggeredAt.AddMinutes(-30),
            "gitlab",
            "deployment",
            "Revision deployed",
            "info",
            null);
        var connector = new RecordingConnector("gitlab", _ => new ConnectorResult(
            "gitlab", SourceHealth.Complete, [], [timeline], [], 10, null));

        var collection = await Collector(initialMinutes: 30, maximumMinutes: 60).CollectAsync(
            Context(), "test-v1", [connector], CancellationToken.None);
        var result = Assert.Single(collection.ConnectorResults);

        Assert.Equal(SourceHealth.Complete, result.Health);
        Assert.Single(result.Timeline);
        Assert.Null(result.Diagnostic);
    }

    [Fact]
    public async Task AccumulatedResultIsCappedByTheCumulativeByteLimit()
    {
        var connector = new RecordingConnector("victorialogs", scope => Result("victorialogs",
            Enumerable.Range(1, 25)
                .Select(index => Finding(
                    $"{CumulativeLookbackMinutes(scope)}-{index}",
                    "victorialogs",
                    "annotation",
                    "info",
                    scope.Start.AddSeconds(index),
                    $"Context {index}") with
                {
                    Excerpt = new string((char)('a' + index % 20), 2_500)
                })
                .ToArray()));

        var collection = await Collector(
                initialMinutes: 30,
                maximumMinutes: 60,
                maximumItems: 100,
                maximumBytes: 65_536)
            .CollectAsync(Context(), "test-v1", [connector], CancellationToken.None);
        var result = Assert.Single(collection.ConnectorResults);

        Assert.True(JsonSerializer.SerializeToUtf8Bytes(result).Length <= 65_536);
        Assert.True(result.Findings.Count < 50);
        Assert.Equal(SourceHealth.Partial, result.Health);
        Assert.Contains("65,536-byte retained-result limit", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisjointLogCountSnapshotsAreSummedIntoOneStableFinding()
    {
        var connector = new RecordingConnector("victorialogs", scope =>
        {
            var count = CumulativeLookbackMinutes(scope) == 30 ? 3L : 7L;
            var finding = Finding(
                "stable-count",
                "victorialogs",
                "log-count",
                "warning",
                scope.End,
                $"checkout-errors: {count} matching log events") with
            {
                ObjectType = "log-query",
                ObjectId = "checkout-errors",
                Provenance = new JsonObject
                {
                    ["scope"] = new JsonObject
                    {
                        ["Name"] = "checkout-errors",
                        ["matchCount"] = count,
                        ["exactWindowStart"] = scope.Start,
                        ["exactWindowEnd"] = scope.End
                    }
                }
            };
            return Result("victorialogs", [finding]);
        });

        var collection = await Collector(initialMinutes: 30, maximumMinutes: 60).CollectAsync(
            Context(), "test-v1", [connector], CancellationToken.None);
        var finding = Assert.Single(Assert.Single(collection.ConnectorResults).Findings);

        Assert.Equal("stable-count", finding.Id);
        Assert.Contains("10 matching log events", finding.Summary, StringComparison.Ordinal);
        Assert.Equal(2, finding.Provenance["scope"]!["adaptiveWindowSegments"]!.GetValue<int>());
        Assert.Equal(
            TriggeredAt.AddMinutes(-60),
            finding.Provenance["scope"]!["exactWindowStart"]!.GetValue<DateTimeOffset>());
        Assert.Equal(
            CollectionEnd,
            finding.Provenance["scope"]!["exactWindowEnd"]!.GetValue<DateTimeOffset>());
    }

    [Fact]
    public async Task BoundedInconclusiveOutcomeAppearsInTheDeterministicReportSummary()
    {
        var connector = new RecordingConnector("grafana", scope => Result("grafana",
        [
            Finding("metric", "grafana", "metric", "warning", scope.Start, "Elevated latency")
        ]));
        var context = Context();
        var collection = await Collector(initialMinutes: 30, maximumMinutes: 60).CollectAsync(
            context, "test-v1", [connector], CancellationToken.None);
        var composer = new ReportComposer(
            new FixedTimeProvider(CollectionEnd),
            new EvidenceSourceRegistry([], TestConfiguration.EvidenceSources()));

        var report = composer.Compose(
            Incident(),
            context.Profile,
            "test-v1",
            collection.ConnectorResults,
            null,
            new AiSynthesis("unavailable", null, [], [], [], null),
            collectionOutcome: collection.Outcome);

        Assert.StartsWith(
            "Evidence collection reached the bounded 60-minute lookback without a clear deterministic result.",
            report.DeterministicSummary,
            StringComparison.Ordinal);
        Assert.Contains("Retained 1 high-signal evidence group", report.DeterministicSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WidenedRepetitiveEvidenceUsesTheExistingSemanticCompressionPath()
    {
        var connector = new RecordingConnector("victorialogs", scope =>
        {
            if (CumulativeLookbackMinutes(scope) == 30)
            {
                return Result("victorialogs",
                [
                    Finding("context", "victorialogs", "annotation", "info", scope.Start, "Deployment context")
                ]);
            }

            var logs = Enumerable.Range(1, 20)
                .Select(index => new EvidenceFinding(
                    $"log-{index:D2}",
                    "victorialogs",
                    TriggeredAt.AddSeconds(index),
                    null,
                    "log-sample",
                    "warning",
                    $"Checkout timeout for request 550e8400-e29b-41d4-a716-{index:D12}.",
                    null,
                    null,
                    .9,
                    new JsonObject
                    {
                        ["scope"] = new JsonObject { ["Name"] = "checkout-timeouts" }
                    },
                    ObjectType: "log-query",
                    ObjectId: "checkout-timeouts"))
                .ToArray();
            return Result("victorialogs", logs);
        });
        var collection = await Collector(initialMinutes: 30, maximumMinutes: 240).CollectAsync(
            Context(), "test-v1", [connector], CancellationToken.None);

        var payload = LiteLlmSynthesizer.BuildDigestPayload(
            Incident(), collection.ConnectorResults, budget: 1_500);

        Assert.Equal([30, 60, 120, 240], connector.Scopes.Select(CumulativeLookbackMinutes));
        Assert.True(payload.SemanticCompressionApplied);
        Assert.True(payload.SuppressedFindingCount > 0);
        Assert.True(payload.InputFindingCount > payload.SemanticGroupCount);
    }

    [Fact]
    public void WindowSequenceDoublesAndIncludesANonPowerOfTwoMaximum()
    {
        Assert.Equal([30, 60, 100], AdaptiveEvidenceCollector.WindowSequence(30, 100));
    }

    private static AdaptiveEvidenceCollector Collector(
        int initialMinutes,
        int maximumMinutes,
        int maximumItems = 250,
        int maximumBytes = 1_048_576) => new(
        Microsoft.Extensions.Options.Options.Create(new IncidentBotOptions
        {
            EvidenceWindowMinutes = initialMinutes,
            EvidenceMaximumWindowMinutes = maximumMinutes,
            EvidenceMaximumItems = maximumItems,
            EvidenceMaximumBytes = maximumBytes
        }),
        new FixedTimeProvider(CollectionEnd),
        NullLogger<AdaptiveEvidenceCollector>.Instance);

    private static InvestigationContext Context() => new(
        Guid.NewGuid(), "PD-1", "payments", "Payments failing", "high", IncidentState.Triggered,
        TriggeredAt, new Dictionary<string, string>(), new InvestigationProfile
        {
            Id = "profile",
            PagerDutyServiceId = "payments",
            SlackChannel = "#incidents"
        });

    private static IncidentRecord Incident() => new(
        Guid.NewGuid(), "PD-1", "payments", "profile", "Payments failing", "high",
        IncidentState.Triggered, TriggeredAt, CollectionEnd, 1, "collecting", false, null,
        "#incidents", null, new Dictionary<string, string>());

    private static int CumulativeLookbackMinutes(EvidenceScope scope) =>
        (int)(TriggeredAt - scope.Start).TotalMinutes;

    private static ConnectorResult Result(
        string source,
        IReadOnlyList<EvidenceFinding> findings) =>
        new(source, SourceHealth.Complete, findings, [], [], 10, null);

    private static EvidenceFinding Finding(
        string id,
        string source,
        string category,
        string severity,
        DateTimeOffset occurredAt,
        string summary) => new(
        id, source, occurredAt, null, category, severity, summary,
        null, null, .8, new JsonObject());

    private static void AssertDisjointThirtyAndSixtyMinuteRings(IReadOnlyList<EvidenceScope> scopes)
    {
        Assert.Collection(
            scopes,
            first => Assert.Equal((TriggeredAt.AddMinutes(-30), CollectionEnd), (first.Start, first.End)),
            second => Assert.Equal((TriggeredAt.AddMinutes(-60), TriggeredAt.AddMinutes(-30)), (second.Start, second.End)));
    }

    private sealed class RecordingConnector(
        string source,
        Func<EvidenceScope, ConnectorResult> collect,
        bool supportsWindowExpansion = true) : IIncidentEvidenceConnector
    {
        public string Source => source;
        public bool SupportsWindowExpansion => supportsWindowExpansion;
        public List<EvidenceScope> Scopes { get; } = [];

        public Task<ConnectorResult> CollectAsync(
            InvestigationContext context,
            EvidenceScope scope,
            CancellationToken cancellationToken)
        {
            Scopes.Add(scope);
            return Task.FromResult(collect(scope));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
