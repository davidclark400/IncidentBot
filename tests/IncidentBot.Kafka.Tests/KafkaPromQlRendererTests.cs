namespace IncidentBot.Kafka.Tests;

public sealed class KafkaPromQlRendererTests
{
    [Fact]
    public void ValuesAreSortedEscapedAndCannotBecomeQueryFragments()
    {
        var rendered = KafkaPromQlRenderer.Render(
            "metric{cluster=~\"{{clusterRegex}}\",topic=~\"{{topicRegex}}\",consumer_group=~\"{{consumerGroupRegex}}\"}",
            KafkaMetricCatalogTests.Scope());

        Assert.Contains("cluster=~\"(prod\\\\.eu-1)\"", rendered, StringComparison.Ordinal);
        Assert.Contains("topic=~\"(orders\\\\.v1|payments\\\\+retry)\"", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("payments+retry", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("(?:", rendered, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("metric{cluster=~\"{{clusterRegex}}\"} or {{clusterRegex}}")]
    [InlineData("metric{cluster=~\"prefix-{{clusterRegex}}\"}")]
    public void EveryPlaceholderOccurrenceMustBeACompleteRegexMatcherValue(string template)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            KafkaPromQlRenderer.Render(template, KafkaMetricCatalogTests.Scope()));

        Assert.Contains("complete value", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("metric{topic=~\"{{query}}\"}")]
    [InlineData("metric{topic=~\"{{topicRegex\"}")]
    public void UnknownOrMalformedPlaceholdersAreRejected(string template)
    {
        Assert.Throws<InvalidOperationException>(() =>
            KafkaPromQlRenderer.Render(template, KafkaMetricCatalogTests.Scope()));
    }

    [Fact]
    public void RawQueryLikeResourceValuesAreRejected()
    {
        var scope = new KafkaProfileScope
        {
            MetricPackId = "pack",
            Cluster = "prod\"} or vector(1)",
            Topics = ["orders"]
        };

        Assert.Throws<InvalidOperationException>(() => KafkaPromQlRenderer.ValidateScope(scope));
    }

    [Theory]
    [InlineData("$topicRegex")]
    [InlineData("[[topicRegex]]")]
    [InlineData("label : value")]
    public void GrafanaVariableSyntaxInResourceValuesIsRejected(string topic)
    {
        var baseScope = KafkaMetricCatalogTests.Scope();
        var scope = new KafkaProfileScope
        {
            MetricPackId = baseScope.MetricPackId,
            Cluster = baseScope.Cluster,
            Topics = [topic]
        };

        Assert.Throws<InvalidOperationException>(() => KafkaPromQlRenderer.ValidateScope(scope));
    }

    [Fact]
    public void EmptyConsumerGroupAllowlistUsesAValidImpossibleRe2Expression()
    {
        var baseScope = KafkaMetricCatalogTests.Scope();
        var scope = new KafkaProfileScope
        {
            MetricPackId = baseScope.MetricPackId,
            Cluster = baseScope.Cluster,
            Topics = baseScope.Topics,
            ConsumerGroups = []
        };

        var rendered = KafkaPromQlRenderer.Render(
            "metric{consumer_group=~\"{{consumerGroupRegex}}\"}",
            scope);

        Assert.Equal("metric{consumer_group=~\"a^\"}", rendered);
        Assert.DoesNotContain("(?", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void DashboardRenderingUsesOnlyGrafanaRegexVariables()
    {
        var rendered = KafkaPromQlRenderer.RenderForGrafanaVariables(
            "metric{cluster=~\"{{clusterRegex}}\",topic=~\"{{topicRegex}}\"}",
            KafkaMetricCatalogTests.Scope());

        Assert.Equal(
            "metric{cluster=~\"${clusterRegex:regex}\",topic=~\"${topicRegex:regex}\"}",
            rendered);
    }
}
