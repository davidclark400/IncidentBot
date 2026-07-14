using System.Text.Json.Nodes;
using IncidentBot.Api.Connectors;
using IncidentBot.Api.Domain;
using IncidentBot.Api.Fingerprinting;
using IncidentBot.Api.Incidents;
using IncidentBot.Api.Options;
using Microsoft.Extensions.Logging.Abstractions;

namespace IncidentBot.Api.Tests;

public sealed class KafkaEvidencePolicyTests
{
    private static readonly DateTimeOffset TriggeredAt = DateTimeOffset.Parse("2026-07-11T10:00:00Z");
    private static readonly DateTimeOffset CollectionEnd = TriggeredAt.AddMinutes(5);

    [Fact]
    public void ContextFindingIsExcludedFromHighSignalClarityAndFingerprintSymptoms()
    {
        var finding = KafkaFinding(
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

        var clarity = EvidenceClarityPolicy.Evaluate(
            Context(),
            [Result(finding)],
            CollectionEnd,
            initialWindowMinutes: 30);
        var features = new FingerprintExtractor(new FingerprintNormalizer())
            .Extract(Incident(), [finding]);

        Assert.False(EvidenceRankingPolicy.IsHighSignal(finding));
        Assert.False(clarity.IsClear);
        Assert.DoesNotContain("kafka-topic-message-rate", features.SymptomCategories);
        Assert.Empty(features.ErrorTemplates);
    }

    [Fact]
    public void StructuredCriticalAnomalyIsAnExplicitFailure()
    {
        var finding = KafkaFinding(
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

        var clarity = EvidenceClarityPolicy.Evaluate(
            Context(),
            [Result(finding)],
            CollectionEnd,
            initialWindowMinutes: 30);

        Assert.True(EvidenceRankingPolicy.IsHighSignal(finding));
        Assert.True(clarity.IsClear);
        Assert.Equal(EvidenceClarityReason.ExplicitFailure, clarity.Reason);
        Assert.Equal([finding.Id], clarity.SupportingEvidenceIds);
    }

    [Fact]
    public void KafkaFindingsGroupByStableResourceIdentity()
    {
        var lag = KafkaFinding(
            "lag",
            "kafka-consumer-lag",
            "anomaly",
            "warning",
            "above",
            1_500,
            TriggeredAt,
            objectType: "kafka-consumer-group",
            objectId: "production/orders/payments");
        var rebalance = KafkaFinding(
            "rebalance",
            "kafka-consumer-rebalances",
            "anomaly",
            "warning",
            "above",
            1,
            TriggeredAt,
            objectType: "kafka-consumer-group",
            objectId: "production/orders/payments");
        var otherGroup = KafkaFinding(
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
            EvidenceRankingPolicy.GroupKey(lag),
            EvidenceRankingPolicy.GroupKey(rebalance));
        Assert.NotEqual(
            EvidenceRankingPolicy.GroupKey(lag),
            EvidenceRankingPolicy.GroupKey(otherGroup));
    }

    [Fact]
    public void CausalSequenceIncludesOnlyTimestampSupportedKafkaAnomalies()
    {
        var timestamped = KafkaFinding(
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
        var scalar = KafkaFinding(
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
        var context = KafkaFinding(
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

        var causal = ReportComposer.BuildCausalEvents([scalar, context, timestamped]);

        var item = Assert.Single(causal);
        Assert.Equal(timestamped.Id, item.EvidenceId);
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
            var finding = KafkaFinding(
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
            return Result(finding);
        });
        var collector = new AdaptiveEvidenceCollector(
            Microsoft.Extensions.Options.Options.Create(new IncidentBotOptions
            {
                EvidenceWindowMinutes = 30,
                EvidenceMaximumWindowMinutes = 60,
                EvidenceMaximumItems = 100,
                EvidenceMaximumBytes = 1_048_576
            }),
            new FixedTimeProvider(CollectionEnd),
            NullLogger<AdaptiveEvidenceCollector>.Instance);

        var collection = await collector.CollectAsync(
            Context(),
            "test-v1",
            [connector],
            CancellationToken.None);
        var finding = Assert.Single(Assert.Single(collection.ConnectorResults).Findings);
        var scope = Assert.IsType<JsonObject>(finding.Provenance["scope"]);

        Assert.Equal(2, connector.Scopes.Count);
        Assert.Equal(expectedValue, scope["reducedValue"]!.GetValue<double>());
        Assert.Equal(olderAt, finding.OccurredAt);
        Assert.Equal(2, scope["adaptiveWindowSegments"]!.GetValue<int>());
        Assert.Equal(TriggeredAt.AddMinutes(-60), scope["exactWindowStart"]!.GetValue<DateTimeOffset>());
        Assert.Equal(CollectionEnd, scope["exactWindowEnd"]!.GetValue<DateTimeOffset>());
    }

    [Fact]
    public void FingerprintIncludesKafkaClusterAndTypedStableObjectIdentity()
    {
        var finding = KafkaFinding(
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

        var features = new FingerprintExtractor(new FingerprintNormalizer())
            .Extract(Incident(), [finding]);

        Assert.Contains("cluster:production-cluster", features.Scopes);
        Assert.Contains(
            "kafka-consumer-group:production/orders/payment-worker",
            features.Components);
    }

    private static InvestigationContext Context() => new(
        Guid.NewGuid(),
        "PD-1",
        "payments",
        "Payments failing",
        "high",
        IncidentState.Triggered,
        TriggeredAt,
        new Dictionary<string, string>(),
        new InvestigationProfile
        {
            Id = "profile",
            PagerDutyServiceId = "payments",
            SlackChannel = "#incidents"
        });

    private static IncidentRecord Incident() => new(
        Guid.NewGuid(),
        "PD-1",
        "payments",
        "profile",
        "Payments failing",
        "high",
        IncidentState.Triggered,
        TriggeredAt,
        CollectionEnd,
        1,
        "collecting",
        false,
        null,
        "#incidents",
        null,
        new Dictionary<string, string>());

    private static ConnectorResult Result(EvidenceFinding finding) =>
        new("kafka", SourceHealth.Complete, [finding], [], [], 10, null);

    private static EvidenceFinding KafkaFinding(
        string id,
        string category,
        string evidenceMode,
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
            ["evidenceMode"] = evidenceMode,
            ["thresholdState"] = thresholdState,
            ["direction"] = direction,
            ["reducedValue"] = reducedValue,
            ["timestampSupported"] = timestampSupported,
            ["cluster"] = cluster
        };
        if (exactWindowStart.HasValue) scope["exactWindowStart"] = exactWindowStart.Value;
        if (exactWindowEnd.HasValue) scope["exactWindowEnd"] = exactWindowEnd.Value;
        return new EvidenceFinding(
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

    private sealed class RecordingConnector(Func<EvidenceScope, ConnectorResult> collect)
        : IIncidentEvidenceConnector
    {
        public string Source => "kafka";
        public bool SupportsWindowExpansion => true;
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
