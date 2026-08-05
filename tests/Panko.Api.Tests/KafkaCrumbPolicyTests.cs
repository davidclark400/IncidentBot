using System.Text.Json.Nodes;
using Panko.Api.Crumbs;
using Panko.Api.Domain;
using Panko.Api.Patterns;
using Panko.Api.Signatures;
using Panko.Api.Cases;
using Panko.Api.Options;
using Microsoft.Extensions.Logging.Abstractions;

namespace Panko.Api.Tests;

public sealed class KafkaCrumbPolicyTests
{
    private static readonly DateTimeOffset TriggeredAt = DateTimeOffset.Parse("2026-07-11T10:00:00Z");
    private static readonly DateTimeOffset CollectionEnd = TriggeredAt.AddMinutes(5);

    [Fact]
    public void ContextCrumbIsExcludedFromHighSignalClarityAndSignatureSymptoms()
    {
        var crumb = KafkaCrumb(
            "topic-rate",
            "kafka-topic-message-rate",
            "context",
            "critical",
            "above",
            2_500,
            TriggeredAt,
            timestampSupported: true,
            objectType: "kafka-topic",
            objectId: "production/orders");

        var clarity = CrumbClarityPolicy.Evaluate(
            Context(),
            [Result(crumb)],
            CollectionEnd,
            initialWindowMinutes: 30);
        var features = new SignatureExtractor(new SignatureNormalizer())
            .Extract(BuildCase(), [crumb]);

        Assert.False(CrumbRankingPolicy.IsHighSignal(crumb));
        Assert.False(clarity.IsClear);
        Assert.DoesNotContain("kafka-topic-message-rate", features.SymptomCategories);
        Assert.Empty(features.ErrorTemplates);
    }

    [Fact]
    public void StructuredCriticalAnomalyIsAnExplicitFailure()
    {
        var crumb = KafkaCrumb(
            "offline-partitions",
            "kafka-offline-partitions",
            "anomaly",
            "critical",
            "above",
            4,
            TriggeredAt.AddMinutes(1),
            timestampSupported: true,
            objectType: "kafka-cluster",
            objectId: "production");

        var clarity = CrumbClarityPolicy.Evaluate(
            Context(),
            [Result(crumb)],
            CollectionEnd,
            initialWindowMinutes: 30);

        Assert.True(CrumbRankingPolicy.IsHighSignal(crumb));
        Assert.True(clarity.IsClear);
        Assert.Equal(CrumbClarityReason.ExplicitFailure, clarity.Reason);
        Assert.Equal([crumb.Id], clarity.SupportingCrumbIds);
    }

    [Fact]
    public void KafkaCrumbsGroupByStableResourceIdentity()
    {
        var lag = KafkaCrumb(
            "lag",
            "kafka-consumer-lag",
            "anomaly",
            "warning",
            "above",
            1_500,
            TriggeredAt,
            objectType: "kafka-consumer-group",
            objectId: "production/orders/payments");
        var rebalance = KafkaCrumb(
            "rebalance",
            "kafka-consumer-rebalances",
            "anomaly",
            "warning",
            "above",
            1,
            TriggeredAt,
            objectType: "kafka-consumer-group",
            objectId: "production/orders/payments");
        var otherGroup = KafkaCrumb(
            "other-lag",
            "kafka-consumer-lag",
            "anomaly",
            "warning",
            "above",
            1_500,
            TriggeredAt,
            objectType: "kafka-consumer-group",
            objectId: "production/orders/fulfilment");

        Assert.Equal(
            CrumbRankingPolicy.GroupKey(lag),
            CrumbRankingPolicy.GroupKey(rebalance));
        Assert.NotEqual(
            CrumbRankingPolicy.GroupKey(lag),
            CrumbRankingPolicy.GroupKey(otherGroup));
    }

    [Fact]
    public void CausalSequenceIncludesOnlyTimestampSupportedKafkaAnomalies()
    {
        var timestamped = KafkaCrumb(
            "lag",
            "kafka-consumer-lag",
            "anomaly",
            "warning",
            "above",
            1_500,
            TriggeredAt.AddMinutes(1),
            timestampSupported: true,
            objectType: "kafka-consumer-group",
            objectId: "production/orders/payments");
        var scalar = KafkaCrumb(
            "producer-errors",
            "kafka-producer-errors",
            "anomaly",
            "critical",
            "above",
            2,
            TriggeredAt.AddMinutes(2),
            timestampSupported: false,
            objectType: "kafka-topic",
            objectId: "production/orders");
        var context = KafkaCrumb(
            "topic-rate",
            "kafka-topic-message-rate",
            "context",
            "critical",
            "above",
            2_500,
            TriggeredAt.AddMinutes(3),
            timestampSupported: true,
            objectType: "kafka-topic",
            objectId: "production/orders");

        var causal = CaseFileComposer.BuildCausalMarkers([scalar, context, timestamped]);

        var item = Assert.Single(causal);
        Assert.Equal(timestamped.Id, item.CrumbId);
        Assert.Equal("Kafka lag observed", item.Label);
        Assert.Equal(timestamped.OccurredAt, item.OccurredAt);
    }

    [Theory]
    [InlineData("above", 5d, 9d, 9d)]
    [InlineData("below", 5d, 2d, 2d)]
    public async Task AdaptiveMergeRetainsDirectionAwareWorstValueAndItsTimestamp(
        string direction,
        double recentValue,
        double olderValue,
        double expectedValue)
    {
        var recentAt = TriggeredAt.AddMinutes(-5);
        var olderAt = TriggeredAt.AddMinutes(-45);
        var connector = new RecordingConnector(scope =>
        {
            var recentRing = scope.End == CollectionEnd;
            var crumb = KafkaCrumb(
                "stable-lag",
                "kafka-consumer-lag",
                "anomaly",
                "warning",
                direction,
                recentRing ? recentValue : olderValue,
                recentRing ? recentAt : olderAt,
                timestampSupported: true,
                objectType: "kafka-consumer-group",
                objectId: "production/orders/payments",
                exactWindowStart: scope.Start,
                exactWindowEnd: scope.End);
            return Result(crumb);
        });
        var collector = new AdaptiveCrumbCollector(
            Microsoft.Extensions.Options.Options.Create(new PankoOptions
            {
                CrumbWindowMinutes = 30,
                CrumbMaximumWindowMinutes = 60,
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
        Assert.Equal(expectedValue, scope["reducedValue"]!.GetValue<double>());
        Assert.Equal(olderAt, crumb.OccurredAt);
        Assert.Equal(2, scope["adaptiveWindowSegments"]!.GetValue<int>());
        Assert.Equal(TriggeredAt.AddMinutes(-60), scope["exactWindowStart"]!.GetValue<DateTimeOffset>());
        Assert.Equal(CollectionEnd, scope["exactWindowEnd"]!.GetValue<DateTimeOffset>());
    }

    [Fact]
    public void SignatureIncludesKafkaClusterAndTypedStableObjectIdentity()
    {
        var crumb = KafkaCrumb(
            "consumer-lag",
            "kafka-consumer-lag",
            "anomaly",
            "warning",
            "above",
            1_500,
            TriggeredAt,
            objectType: "kafka-consumer-group",
            objectId: "production/orders/payment-worker",
            cluster: "production-cluster");

        var features = new SignatureExtractor(new SignatureNormalizer())
            .Extract(BuildCase(), [crumb]);

        Assert.Contains("cluster:production-cluster", features.Scopes);
        Assert.Contains(
            "kafka-consumer-group:production/orders/payment-worker",
            features.Components);
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
        new Recipe
        {
            Id = "recipe",
            PagerDutyServiceId = "payments",
            SlackChannel = "#cases"
        });

    private static CaseRecord BuildCase() => new(
        Guid.NewGuid(),
        "PD-1",
        "payments",
        "recipe",
        "Payments failing",
        "high",
        PagerDutyIncidentState.Triggered,
        TriggeredAt,
        CollectionEnd,
        1,
        "collecting",
        false,
        null,
        "#cases",
        null,
        new Dictionary<string, string>());

    private static CrumbSourceResult Result(Crumb crumb) =>
        new("kafka", CrumbSourceHealth.Complete, [crumb], [], [], 10, null);

    private static Crumb KafkaCrumb(
        string id,
        string category,
        string crumbMode,
        string thresholdState,
        string direction,
        double reducedValue,
        DateTimeOffset occurredAt,
        bool timestampSupported = false,
        string objectType = "kafka-cluster",
        string objectId = "production",
        string cluster = "production",
        DateTimeOffset? exactWindowStart = null,
        DateTimeOffset? exactWindowEnd = null)
    {
        var scope = new JsonObject
        {
            ["crumbMode"] = crumbMode,
            ["thresholdState"] = thresholdState,
            ["direction"] = direction,
            ["reducedValue"] = reducedValue,
            ["timestampSupported"] = timestampSupported,
            ["cluster"] = cluster
        };
        if (exactWindowStart.HasValue) scope["exactWindowStart"] = exactWindowStart.Value;
        if (exactWindowEnd.HasValue) scope["exactWindowEnd"] = exactWindowEnd.Value;
        return new Crumb(
            id,
            "kafka",
            occurredAt,
            null,
            category,
            thresholdState,
            $"{category} reduced value {reducedValue}",
            null,
            null,
            .9,
            new JsonObject { ["scope"] = scope },
            ObjectType: objectType,
            ObjectId: objectId);
    }

    private sealed class RecordingConnector(Func<CrumbScope, CrumbSourceResult> collect)
        : ICrumbSourceAdapter
    {
        public string Source => "kafka";
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
