using System.Text.Json;
using System.Text.Json.Nodes;
using Panko.Api.Crumbs;
using Panko.Api.Domain;
using Panko.Api.Cases;
using Panko.Api.Options;
using Microsoft.Extensions.Logging.Abstractions;

namespace Panko.Api.Tests;

public sealed class AdaptiveCrumbCollectorTests
{
    private static readonly DateTimeOffset TriggeredAt = DateTimeOffset.Parse("2026-07-11T10:00:00Z");
    private static readonly DateTimeOffset CollectionEnd = DateTimeOffset.Parse("2026-07-11T10:05:00Z");

    [Fact]
    public async Task ExpandsThroughDisjointRingsUntilChangeCorroboratesRecentFailure()
    {
        var logs = new RecordingConnector("victorialogs", scope => Result("victorialogs",
            scope.End == CollectionEnd
                ? [Crumb("first-error", "victorialogs", "first-error", "warning", TriggeredAt, "Payment handler failed")]
                : []));
        var gitLab = new RecordingConnector("gitlab", scope => Result("gitlab",
            CumulativeLookbackMinutes(scope) == 60
                ? [Crumb("deployment", "gitlab", "deployment", "info", TriggeredAt.AddMinutes(-45), "Revision deployed")]
                : []));

        var collection = await Collector(initialMinutes: 30, maximumMinutes: 240).CollectAsync(
            Context(), "test-v1", [logs, gitLab], CancellationToken.None);

        Assert.Equal(CrumbCollectionCompletionReason.ClearResult, collection.Outcome.CompletionReason);
        Assert.Equal(CrumbClarityReason.ChangePrecedesFailure, collection.Outcome.Clarity.Reason);
        Assert.Equal(2, collection.Outcome.PassCount);
        Assert.Equal(60, collection.Outcome.FinalLookbackMinutes);
        Assert.Equal(["deployment", "first-error"], collection.Outcome.Clarity.SupportingCrumbIds);
        AssertDisjointThirtyAndSixtyMinuteRings(logs.Scopes);
        AssertDisjointThirtyAndSixtyMinuteRings(gitLab.Scopes);
        Assert.Equal(2, collection.SourceResults.Sum(result => result.Crumbs.Count));
    }

    [Fact]
    public async Task CorroboratedIndependentSourcesStopTheInitialPass()
    {
        var logs = new RecordingConnector("victorialogs", _ => Result("victorialogs",
        [
            Crumb("logs", "victorialogs", "first-error", "warning", TriggeredAt, "First payment error")
        ]));
        var metrics = new RecordingConnector("grafana", _ => Result("grafana",
        [
            Crumb("metric", "grafana", "metric", "warning", TriggeredAt.AddMinutes(2), "Error rate elevated") with
            {
                Provenance = new JsonObject
                {
                    ["scope"] = new JsonObject
                    {
                        ["reducer"] = "maximum",
                        ["reducedValue"] = 12,
                        ["observedAt"] = TriggeredAt.AddMinutes(2),
                        ["breachStartedAt"] = TriggeredAt.AddMinutes(1),
                        ["breachEndedAt"] = null,
                        ["warningThreshold"] = 10,
                        ["criticalThreshold"] = null,
                        ["direction"] = "above",
                        ["unit"] = "requests/second",
                        ["sampleCount"] = 8,
                        ["timestampSupported"] = true,
                        ["reductionComplete"] = true,
                        ["exactWindowEnd"] = CollectionEnd
                    }
                }
            }
        ]));

        var collection = await Collector(initialMinutes: 30, maximumMinutes: 240).CollectAsync(
            Context(), "test-v1", [logs, metrics], CancellationToken.None);

        Assert.Equal(CrumbCollectionCompletionReason.ClearResult, collection.Outcome.CompletionReason);
        Assert.Equal(CrumbClarityReason.CorroboratedSignals, collection.Outcome.Clarity.Reason);
        Assert.Equal(1, collection.Outcome.PassCount);
        Assert.Single(logs.Scopes);
        Assert.Single(metrics.Scopes);
    }

    [Fact]
    public async Task PublishesEachConnectorAsItCompletesWithoutWaitingForTheSlowestSource()
    {
        var slowResult = new TaskCompletionSource<CrumbSourceResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var fast = new AsyncConnector(
            "pagerduty",
            Task.FromResult(Result("pagerduty", [])),
            supportsWindowExpansion: false);
        var slow = new AsyncConnector(
            "grafana",
            slowResult.Task,
            supportsWindowExpansion: false);
        var observer = new RecordingProgressObserver();

        var collection = Collector(initialMinutes: 30, maximumMinutes: 30).CollectAsync(
            Context(), "test-v1", [fast, slow], observer, CancellationToken.None);

        var firstCompletedSource = await observer.FirstConnectorCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("pagerduty", firstCompletedSource);
        Assert.False(collection.IsCompleted);

        slowResult.SetResult(CrumbSourceResult.Unavailable("grafana", 900, "Timeout after 1 second"));
        await collection;

        Assert.Equal(
            ["pass:1:started", "source:pagerduty", "source:grafana", "pass:1:completed"],
            observer.Events);
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
            Crumb("job", "gitlab", "pipeline-job-output", "critical", TriggeredAt, "Build failed") with
            {
                Provenance = provenance
            }
        ]));

        var collection = await Collector(initialMinutes: 30, maximumMinutes: 240).CollectAsync(
            Context(), "test-v1", [connector], CancellationToken.None);

        Assert.Equal(CrumbCollectionCompletionReason.ClearResult, collection.Outcome.CompletionReason);
        Assert.Equal(CrumbClarityReason.ExplicitFailure, collection.Outcome.Clarity.Reason);
        Assert.Equal(["job"], collection.Outcome.Clarity.SupportingCrumbIds);
        Assert.Single(connector.Scopes);
    }

    [Fact]
    public async Task SingleGenericHighSignalStopsOnlyAtTheConfiguredMaximum()
    {
        var connector = new RecordingConnector("grafana", scope => Result("grafana",
        [
            Crumb(
                $"metric-{CumulativeLookbackMinutes(scope)}",
                "grafana",
                "metric",
                "warning",
                scope.Start,
                "Elevated latency")
        ]));

        var collection = await Collector(initialMinutes: 30, maximumMinutes: 100).CollectAsync(
            Context(), "test-v1", [connector], CancellationToken.None);

        Assert.Equal(CrumbCollectionCompletionReason.MaximumWindowReached, collection.Outcome.CompletionReason);
        Assert.False(collection.Outcome.Clarity.IsClear);
        Assert.Equal([30, 60, 100], connector.Scopes.Select(CumulativeLookbackMinutes));
        Assert.Collection(
            connector.Scopes,
            first => Assert.Equal((TriggeredAt.AddMinutes(-30), CollectionEnd), (first.Start, first.End)),
            second => Assert.Equal((TriggeredAt.AddMinutes(-60), TriggeredAt.AddMinutes(-30)), (second.Start, second.End)),
            third => Assert.Equal((TriggeredAt.AddMinutes(-100), TriggeredAt.AddMinutes(-60)), (third.Start, third.End)));
    }

    [Fact]
    public async Task HistoricalResolvedPagerDutyIncidentCapsCollectionAtTheConfiguredPostResolutionWindow()
    {
        var resolvedAt = TriggeredAt.AddMinutes(25);
        var now = TriggeredAt.AddDays(3);
        var expectedEnd = resolvedAt.AddMinutes(30);
        var connector = new RecordingConnector("grafana", _ => Result("grafana", []));

        var collection = await Collector(
                initialMinutes: 30,
                maximumMinutes: 30,
                postResolutionWindowMinutes: 30,
                collectionEnd: now)
            .CollectAsync(
                Context(PagerDutyIncidentState.Resolved, resolvedAt: resolvedAt),
                "test-v1",
                [connector],
                CancellationToken.None);

        var scope = Assert.Single(connector.Scopes);
        Assert.Equal((TriggeredAt.AddMinutes(-30), expectedEnd), (scope.Start, scope.End));
        Assert.Equal(expectedEnd, collection.Outcome.CoverageEnd);
    }

    [Fact]
    public async Task RecentlyResolvedPagerDutyIncidentDoesNotExtendCollectionBeyondNow()
    {
        var resolvedAt = TriggeredAt.AddMinutes(25);
        var now = TriggeredAt.AddMinutes(40);
        var connector = new RecordingConnector("grafana", _ => Result("grafana", []));

        var collection = await Collector(
                initialMinutes: 30,
                maximumMinutes: 30,
                postResolutionWindowMinutes: 30,
                collectionEnd: now)
            .CollectAsync(
                Context(PagerDutyIncidentState.Resolved, resolvedAt: resolvedAt),
                "test-v1",
                [connector],
                CancellationToken.None);

        Assert.Equal(now, Assert.Single(connector.Scopes).End);
        Assert.Equal(now, collection.Outcome.CoverageEnd);
    }

    [Theory]
    [InlineData(PagerDutyIncidentState.Triggered)]
    [InlineData(PagerDutyIncidentState.Acknowledged)]
    public async Task UnresolvedPagerDutyIncidentStatesIgnoreAStaleResolutionTimestamp(PagerDutyIncidentState state)
    {
        var now = TriggeredAt.AddDays(3);
        var connector = new RecordingConnector("grafana", _ => Result("grafana", []));

        var collection = await Collector(
                initialMinutes: 30,
                maximumMinutes: 30,
                postResolutionWindowMinutes: 30,
                collectionEnd: now)
            .CollectAsync(
                Context(
                    state,
                    acknowledgedAt: state == PagerDutyIncidentState.Acknowledged ? TriggeredAt.AddMinutes(5) : null,
                    resolvedAt: TriggeredAt.AddMinutes(25)),
                "test-v1",
                [connector],
                CancellationToken.None);

        Assert.Equal(now, Assert.Single(connector.Scopes).End);
        Assert.Equal(now, collection.Outcome.CoverageEnd);
    }

    [Fact]
    public async Task ExactSnapshotConnectorRunsOnlyInTheInitialPassAndReturnsExplicitOutcome()
    {
        var connector = new RecordingConnector(
            "pagerduty",
            scope => Result("pagerduty",
            [
                Crumb("pagerduty-incident", "pagerduty", "pagerduty-incident", "info", scope.End, "PagerDuty incident snapshot")
            ]),
            supportsWindowExpansion: false);

        var collection = await Collector(initialMinutes: 30, maximumMinutes: 240).CollectAsync(
            Context(), "test-v1", [connector], CancellationToken.None);

        Assert.Equal(CrumbCollectionCompletionReason.NoExpandableCrumbSources, collection.Outcome.CompletionReason);
        Assert.False(collection.Outcome.Clarity.IsClear);
        Assert.Equal(1, collection.Outcome.PassCount);
        Assert.Equal([30], connector.Scopes.Select(CumulativeLookbackMinutes));
    }

    [Fact]
    public async Task NoConnectorsReturnsBoundedInconclusiveOutcome()
    {
        var collection = await Collector(initialMinutes: 30, maximumMinutes: 240).CollectAsync(
            Context(), "test-v1", [], CancellationToken.None);

        Assert.Empty(collection.SourceResults);
        Assert.Equal(CrumbCollectionCompletionReason.NoCrumbSources, collection.Outcome.CompletionReason);
        Assert.Equal(0, collection.Outcome.PassCount);
        Assert.Equal(0, collection.Outcome.FinalLookbackMinutes);
        Assert.False(collection.Outcome.Clarity.IsClear);
    }

    [Fact]
    public async Task RetainsNarrowWindowCrumbsWhenTheOlderRingIsUnavailable()
    {
        var connector = new RecordingConnector("victorialogs", scope =>
            CumulativeLookbackMinutes(scope) == 30
                ? Result("victorialogs",
                [
                    Crumb("narrow-context", "victorialogs", "annotation", "info", scope.Start, "Deployment context")
                ])
                : CrumbSourceResult.Unavailable("victorialogs", 15, "older ring query failed"));

        var collection = await Collector(initialMinutes: 30, maximumMinutes: 60).CollectAsync(
            Context(), "test-v1", [connector], CancellationToken.None);
        var result = Assert.Single(collection.SourceResults);

        Assert.Equal(CrumbCollectionCompletionReason.MaximumWindowReached, collection.Outcome.CompletionReason);
        Assert.Equal(CrumbSourceHealth.Partial, result.Health);
        Assert.Equal("narrow-context", Assert.Single(result.Crumbs).Id);
        Assert.Equal(25, result.DurationMilliseconds);
        Assert.Equal("older ring query failed", result.Diagnostic);
    }

    [Fact]
    public async Task AccumulatedCrumbsAreCappedAcrossAllPasses()
    {
        var connector = new RecordingConnector("victorialogs", scope => Result("victorialogs",
            Enumerable.Range(1, 20)
                .Select(index => Crumb(
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
        var result = Assert.Single(collection.SourceResults);

        Assert.Equal(25, result.Crumbs.Count);
        Assert.Equal(CrumbSourceHealth.Partial, result.Health);
        Assert.Contains("retained at most 25", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuplicateTrailEntriesAcrossRingEdgesDoNotMarkTheSourcePartial()
    {
        var trailEntry = new TrailCandidate(
            TriggeredAt.AddMinutes(-30),
            "gitlab",
            "deployment",
            "Revision deployed",
            "info",
            null);
        var connector = new RecordingConnector("gitlab", _ => new CrumbSourceResult(
            "gitlab", CrumbSourceHealth.Complete, [], [trailEntry], [], 10, null));

        var collection = await Collector(initialMinutes: 30, maximumMinutes: 60).CollectAsync(
            Context(), "test-v1", [connector], CancellationToken.None);
        var result = Assert.Single(collection.SourceResults);

        Assert.Equal(CrumbSourceHealth.Complete, result.Health);
        Assert.Single(result.Trail);
        Assert.Null(result.Diagnostic);
    }

    [Fact]
    public async Task AccumulatedResultIsCappedByTheCumulativeByteLimit()
    {
        var connector = new RecordingConnector("victorialogs", scope => Result("victorialogs",
            Enumerable.Range(1, 25)
                .Select(index => Crumb(
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
        var result = Assert.Single(collection.SourceResults);

        Assert.True(JsonSerializer.SerializeToUtf8Bytes(result).Length <= 65_536);
        Assert.True(result.Crumbs.Count < 50);
        Assert.Equal(CrumbSourceHealth.Partial, result.Health);
        Assert.Contains("65,536-byte retained-result limit", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisjointLogCountSnapshotsAreSummedIntoOneStableCrumb()
    {
        var connector = new RecordingConnector("victorialogs", scope =>
        {
            var count = CumulativeLookbackMinutes(scope) == 30 ? 3L : 7L;
            var crumb = Crumb(
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
            return Result("victorialogs", [crumb]);
        });

        var collection = await Collector(initialMinutes: 30, maximumMinutes: 60).CollectAsync(
            Context(), "test-v1", [connector], CancellationToken.None);
        var crumb = Assert.Single(Assert.Single(collection.SourceResults).Crumbs);

        Assert.Equal("stable-count", crumb.Id);
        Assert.Contains("10 matching log events", crumb.Summary, StringComparison.Ordinal);
        Assert.Equal(2, crumb.Provenance["scope"]!["adaptiveWindowSegments"]!.GetValue<int>());
        Assert.Equal(
            TriggeredAt.AddMinutes(-60),
            crumb.Provenance["scope"]!["exactWindowStart"]!.GetValue<DateTimeOffset>());
        Assert.Equal(
            CollectionEnd,
            crumb.Provenance["scope"]!["exactWindowEnd"]!.GetValue<DateTimeOffset>());
    }

    [Fact]
    public async Task BoundedInconclusiveOutcomeAppearsInTheDeterministicCaseFileSummary()
    {
        var connector = new RecordingConnector("grafana", scope => Result("grafana",
        [
            Crumb("metric", "grafana", "metric", "warning", scope.Start, "Elevated latency")
        ]));
        var context = Context();
        var collection = await Collector(initialMinutes: 30, maximumMinutes: 60).CollectAsync(
            context, "test-v1", [connector], CancellationToken.None);
        var composer = new CaseFileComposer(
            new FixedTimeProvider(CollectionEnd),
            new CrumbSourceRegistry([], TestConfiguration.CrumbSources()));

        var caseFile = composer.Compose(
            BuildCase(),
            context.Recipe,
            "test-v1",
            collection.SourceResults,
            null,
            new AiSynthesis("unavailable", null, [], [], [], null),
            collectionOutcome: collection.Outcome);

        Assert.StartsWith(
            "Crumb collection reached the bounded 60-minute lookback without a clear deterministic result.",
            caseFile.DeterministicSummary,
            StringComparison.Ordinal);
        Assert.Contains("Retained 1 high-signal Crumb group", caseFile.DeterministicSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WidenedRepetitiveCrumbsUseTheExistingSemanticCompressionPath()
    {
        var connector = new RecordingConnector("victorialogs", scope =>
        {
            if (CumulativeLookbackMinutes(scope) == 30)
            {
                return Result("victorialogs",
                [
                    Crumb("context", "victorialogs", "annotation", "info", scope.Start, "Deployment context")
                ]);
            }

            var logs = Enumerable.Range(1, 20)
                .Select(index => new Crumb(
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
            BuildCase(), collection.SourceResults, budget: 1_500);

        Assert.Equal([30, 60, 120, 240], connector.Scopes.Select(CumulativeLookbackMinutes));
        Assert.True(payload.SemanticCompressionApplied);
        Assert.True(payload.SuppressedCrumbCount > 0);
        Assert.True(payload.InputCrumbCount > payload.SemanticGroupCount);
    }

    [Fact]
    public void WindowSequenceDoublesAndIncludesANonPowerOfTwoMaximum()
    {
        Assert.Equal([30, 60, 100], AdaptiveCrumbCollector.WindowSequence(30, 100));
    }

    private static AdaptiveCrumbCollector Collector(
        int initialMinutes,
        int maximumMinutes,
        int maximumItems = 250,
        int maximumBytes = 1_048_576,
        int postResolutionWindowMinutes = 30,
        DateTimeOffset? collectionEnd = null) => new(
        Microsoft.Extensions.Options.Options.Create(new PankoOptions
        {
            CrumbWindowMinutes = initialMinutes,
            CrumbMaximumWindowMinutes = maximumMinutes,
            CrumbPostResolutionWindowMinutes = postResolutionWindowMinutes,
            CrumbMaximumItems = maximumItems,
            CrumbMaximumBytes = maximumBytes
        }),
        new FixedTimeProvider(collectionEnd ?? CollectionEnd),
        NullLogger<AdaptiveCrumbCollector>.Instance);

    private static CaseContext Context(
        PagerDutyIncidentState state = PagerDutyIncidentState.Triggered,
        DateTimeOffset? acknowledgedAt = null,
        DateTimeOffset? resolvedAt = null) => new(
        Guid.NewGuid(), "PD-1", "payments", "Payments failing", "high", state,
        TriggeredAt, new Dictionary<string, string>(), new Recipe
        {
            Id = "recipe",
            PagerDutyServiceId = "payments",
            SlackChannel = "#cases"
        })
        {
            AcknowledgedAt = acknowledgedAt,
            ResolvedAt = resolvedAt
        };

    private static CaseRecord BuildCase() => new(
        Guid.NewGuid(), "PD-1", "payments", "recipe", "Payments failing", "high",
        PagerDutyIncidentState.Triggered, TriggeredAt, CollectionEnd, 1, "collecting", false, null,
        "#cases", null, new Dictionary<string, string>());

    private static int CumulativeLookbackMinutes(CrumbScope scope) =>
        (int)(TriggeredAt - scope.Start).TotalMinutes;

    private static CrumbSourceResult Result(
        string source,
        IReadOnlyList<Crumb> crumbs) =>
        new(source, CrumbSourceHealth.Complete, crumbs, [], [], 10, null);

    private static Crumb Crumb(
        string id,
        string source,
        string category,
        string severity,
        DateTimeOffset occurredAt,
        string summary) => new(
        id, source, occurredAt, null, category, severity, summary,
        null, null, .8, new JsonObject());

    private static void AssertDisjointThirtyAndSixtyMinuteRings(IReadOnlyList<CrumbScope> scopes)
    {
        Assert.Collection(
            scopes,
            first => Assert.Equal((TriggeredAt.AddMinutes(-30), CollectionEnd), (first.Start, first.End)),
            second => Assert.Equal((TriggeredAt.AddMinutes(-60), TriggeredAt.AddMinutes(-30)), (second.Start, second.End)));
    }

    private sealed class RecordingConnector(
        string source,
        Func<CrumbScope, CrumbSourceResult> collect,
        bool supportsWindowExpansion = true) : ICrumbSourceAdapter
    {
        public string Source => source;
        public bool SupportsWindowExpansion => supportsWindowExpansion;
        public List<CrumbScope> Scopes { get; } = [];

        public Task<CrumbSourceResult> CollectAsync(
            CaseContext context,
            CrumbScope scope,
            CancellationToken cancellationToken)
        {
            Scopes.Add(scope);
            return Task.FromResult(collect(scope));
        }
    }

    private sealed class AsyncConnector(
        string source,
        Task<CrumbSourceResult> result,
        bool supportsWindowExpansion) : ICrumbSourceAdapter
    {
        public string Source => source;
        public bool SupportsWindowExpansion => supportsWindowExpansion;

        public Task<CrumbSourceResult> CollectAsync(
            CaseContext context,
            CrumbScope scope,
            CancellationToken cancellationToken) => result;
    }

    private sealed class RecordingProgressObserver : ICrumbCollectionProgressObserver
    {
        public List<string> Events { get; } = [];
        public TaskCompletionSource<string> FirstConnectorCompleted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task PassStartedAsync(
            CrumbCollectionPass pass,
            IReadOnlyList<string> sources,
            CancellationToken cancellationToken)
        {
            Events.Add($"pass:{pass.Number}:started");
            return Task.CompletedTask;
        }

        public Task SourceCompletedAsync(
            CrumbCollectionPass pass,
            CrumbSourceResult result,
            CancellationToken cancellationToken)
        {
            Events.Add($"source:{result.Source}");
            FirstConnectorCompleted.TrySetResult(result.Source);
            return Task.CompletedTask;
        }

        public Task PassCompletedAsync(
            CrumbCollectionPass pass,
            CrumbClarityAssessment clarity,
            CancellationToken cancellationToken)
        {
            Events.Add($"pass:{pass.Number}:completed");
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
