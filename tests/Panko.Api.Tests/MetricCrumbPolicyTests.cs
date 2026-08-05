using System.Text.Json.Nodes;
using Panko.Api.Crumbs;
using Panko.Api.Domain;
using Panko.Api.Cases;
using Panko.Api.Options;
using Microsoft.Extensions.Logging.Abstractions;

namespace Panko.Api.Tests;

public sealed class MetricCrumbPolicyTests
{
    private static readonly DateTimeOffset TriggeredAt = DateTimeOffset.Parse("2026-07-11T10:00:00Z");
    private static readonly DateTimeOffset CollectionEnd = TriggeredAt.AddMinutes(10);

    [Fact]
    public void ChangeMustPrecedeMetricBreachOnsetRatherThanOnlyItsPeak()
    {
        var metric = Metric(
            "latency",
            TriggeredAt.AddMinutes(3),
            TriggeredAt.AddMinutes(1),
            1.7,
            sampleCount: 12);
        var lateDeployment = Crumb(
            "late-deploy",
            "gitlab",
            "deployment",
            "info",
            TriggeredAt.AddMinutes(2),
            "Deployment completed after latency had already breached");
        var precedingDeployment = lateDeployment with
        {
            Id = "preceding-deploy",
            OccurredAt = TriggeredAt,
            Summary = "Deployment completed before latency breached"
        };

        var late = CrumbClarityPolicy.Evaluate(
            Context(),
            [Result("grafana", metric), Result("gitlab", lateDeployment)],
            CollectionEnd,
            initialWindowMinutes: 30);
        var preceding = CrumbClarityPolicy.Evaluate(
            Context(),
            [Result("grafana", metric), Result("gitlab", precedingDeployment)],
            CollectionEnd,
            initialWindowMinutes: 30);

        Assert.False(late.IsClear);
        Assert.True(preceding.IsClear);
        Assert.Equal(CrumbClarityReason.ChangePrecedesFailure, preceding.Reason);
    }

    [Fact]
    public void TimestampLessMetricCannotCreateFalseCorroborationOrCausalSequence()
    {
        var scalar = Metric(
            "scalar",
            CollectionEnd,
            null,
            9,
            sampleCount: 1,
            timestampSupported: false);
        var logs = Crumb(
            "logs",
            "victorialogs",
            "first-error",
            "warning",
            CollectionEnd,
            "First error");

        var clarity = CrumbClarityPolicy.Evaluate(
            Context(),
            [Result("grafana", scalar), Result("victorialogs", logs)],
            CollectionEnd,
            initialWindowMinutes: 30);
        var causal = CaseFileComposer.BuildCausalMarkers([scalar, logs]);

        Assert.False(clarity.IsClear);
        Assert.DoesNotContain(causal, item => item.CrumbId == scalar.Id);
    }

    [Fact]
    public void TimestampedMetricIsIncludedInCausalSequenceAndStructuredDigest()
    {
        var observedAt = TriggeredAt.AddMinutes(2);
        var metric = Metric(
            "latency",
            observedAt,
            TriggeredAt.AddMinutes(1),
            1.7,
            sampleCount: 12);
        var result = Result("grafana", metric);

        var causal = Assert.Single(CaseFileComposer.BuildCausalMarkers([metric]));
        var digest = LiteLlmSynthesizer.BuildDigest(
            new CaseSubject("payments", "Latency", "high", PagerDutyIncidentState.Triggered, TriggeredAt),
            [result],
            12_000);

        Assert.Equal(TriggeredAt.AddMinutes(1), causal.OccurredAt);
        Assert.Equal("metric threshold breach", causal.Label);
        Assert.Contains("metric_reducer=maximum", digest, StringComparison.Ordinal);
        Assert.Contains("reduced_value=1.7", digest, StringComparison.Ordinal);
        Assert.Contains($"observed_at={observedAt:O}", digest, StringComparison.Ordinal);
        Assert.Contains("unit=seconds", digest, StringComparison.Ordinal);
        Assert.Contains("sample_count=12", digest, StringComparison.Ordinal);
    }

    [Fact]
    public void OngoingMetricBreachCorroboratesThroughTheObservationWindow()
    {
        var metric = Metric(
            "latency",
            TriggeredAt.AddMinutes(2),
            TriggeredAt.AddMinutes(1),
            1.7,
            sampleCount: 12,
            exactWindowEnd: TriggeredAt.AddMinutes(15));
        var laterError = Crumb(
            "logs",
            "victorialogs",
            "first-error",
            "warning",
            TriggeredAt.AddMinutes(14),
            "Errors continued while latency remained above threshold");

        var clarity = CrumbClarityPolicy.Evaluate(
            Context(),
            [Result("grafana", metric), Result("victorialogs", laterError)],
            TriggeredAt.AddMinutes(15),
            initialWindowMinutes: 30);

        Assert.True(clarity.IsClear);
        Assert.Equal(CrumbClarityReason.CorroboratedSignals, clarity.Reason);
    }

    [Fact]
    public void UnstructuredGrafanaMetricCannotUseItsQueryBoundaryAsAnOccurrenceTime()
    {
        var metric = Crumb(
            "mcp-latency",
            "grafana",
            "metric",
            "warning",
            CollectionEnd,
            "Grafana MCP metric without an observed sample timestamp") with
        {
            Provenance = new JsonObject
            {
                ["scope"] = new JsonObject
                {
                    ["name"] = "p99 latency",
                    ["datasourceUid"] = "prometheus-main"
                }
            }
        };
        var logs = Crumb(
            "logs",
            "victorialogs",
            "first-error",
            "warning",
            CollectionEnd,
            "First error at the collection boundary");

        var clarity = CrumbClarityPolicy.Evaluate(
            Context(),
            [Result("grafana", metric), Result("victorialogs", logs)],
            CollectionEnd,
            initialWindowMinutes: 30);

        Assert.False(clarity.IsClear);
        Assert.DoesNotContain(CaseFileComposer.BuildCausalMarkers([metric]), item => item.CrumbId == metric.Id);
    }

    [Fact]
    public async Task AdaptiveMergeKeepsCaseReducerTimestampAndDoesNotReplaceItWithOlderBaseline()
    {
        var caseAt = TriggeredAt.AddMinutes(1);
        var baselineAt = TriggeredAt.AddMinutes(-45);
        var connector = new RecordingConnector(scope =>
        {
            var caseRing = scope.End == CollectionEnd;
            var crumb = Metric(
                "stable-latency",
                caseRing ? caseAt : baselineAt,
                caseRing ? caseAt : baselineAt,
                caseRing ? 1.7 : 9,
                caseRing ? 12 : 20,
                comparisonPeriod: caseRing ? "case" : "pre-case",
                exactWindowStart: scope.Start,
                exactWindowEnd: scope.End);
            return Result("grafana", crumb);
        });
        var collector = new AdaptiveCrumbCollector(
            Microsoft.Extensions.Options.Options.Create(new PankoOptions
            {
                CrumbWindowMinutes = 30,
                CrumbMaximumWindowMinutes = 60,
                CrumbPostResolutionWindowMinutes = 30,
                CrumbMaximumItems = 100,
                CrumbMaximumBytes = 1_048_576
            }),
            new FixedTimeProvider(CollectionEnd),
            NullLogger<AdaptiveCrumbCollector>.Instance);

        var collection = await collector.CollectAsync(
            Context(),
            "test-v1",
            [connector],
            CancellationToken.None);
        var crumb = Assert.Single(Assert.Single(collection.SourceResults).Crumbs);
        var scope = Assert.IsType<JsonObject>(crumb.Provenance["scope"]);

        Assert.Equal(2, connector.Scopes.Count);
        Assert.Equal(caseAt, crumb.OccurredAt);
        Assert.Equal(1.7, scope["reducedValue"]!.GetValue<double>());
        Assert.Equal(12, scope["sampleCount"]!.GetValue<int>());
        Assert.Equal(2, scope["adaptiveWindowSegments"]!.GetValue<int>());
        Assert.Equal(TriggeredAt.AddMinutes(-60), scope["exactWindowStart"]!.GetValue<DateTimeOffset>());
        Assert.Equal(CollectionEnd, scope["exactWindowEnd"]!.GetValue<DateTimeOffset>());
    }

    private static CaseContext Context() => new(
        Guid.NewGuid(),
        "PD-1",
        "payments",
        "Payments failing",
        "high",
        PagerDutyIncidentState.Triggered,
        TriggeredAt,
        new Dictionary<string, string>(),
        new Recipe { Id = "recipe" });

    private static CrumbSourceResult Result(string source, params Crumb[] crumbs) =>
        new(source, CrumbSourceHealth.Complete, crumbs, [], [], 1, null);

    private static Crumb Metric(
        string id,
        DateTimeOffset occurredAt,
        DateTimeOffset? breachStartedAt,
        double reducedValue,
        int sampleCount,
        bool timestampSupported = true,
        string comparisonPeriod = "case",
        DateTimeOffset? exactWindowStart = null,
        DateTimeOffset? exactWindowEnd = null)
    {
        var observedAt = timestampSupported ? occurredAt : (DateTimeOffset?)null;
        return Crumb(
            id,
            "grafana",
            "metric",
            "warning",
            occurredAt,
            "p99 latency rose from a 220 ms pre-Case baseline to 1.7 seconds") with
        {
            Provenance = new JsonObject
            {
                ["scope"] = new JsonObject
                {
                    ["reducer"] = "maximum",
                    ["reducedValue"] = reducedValue,
                    ["observedAt"] = observedAt,
                    ["breachStartedAt"] = breachStartedAt,
                    ["breachEndedAt"] = null,
                    ["warningThreshold"] = 1.5,
                    ["criticalThreshold"] = 3,
                    ["direction"] = "above",
                    ["unit"] = "seconds",
                    ["sampleCount"] = sampleCount,
                    ["timestampSupported"] = timestampSupported,
                    ["reductionComplete"] = true,
                    ["comparisonPeriod"] = comparisonPeriod,
                    ["exactWindowStart"] = exactWindowStart ?? TriggeredAt.AddMinutes(-30),
                    ["exactWindowEnd"] = exactWindowEnd ?? CollectionEnd
                }
            }
        };
    }

    private static Crumb Crumb(
        string id,
        string source,
        string category,
        string severity,
        DateTimeOffset occurredAt,
        string summary) => new(
        id,
        source,
        occurredAt,
        null,
        category,
        severity,
        summary,
        null,
        null,
        .9,
        new JsonObject());

    private sealed class RecordingConnector(Func<CrumbScope, CrumbSourceResult> collect)
        : ICrumbSourceAdapter
    {
        public string Source => "grafana";
        public bool SupportsWindowExpansion => true;
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

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
