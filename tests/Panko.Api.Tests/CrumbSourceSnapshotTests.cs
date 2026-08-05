using System.Net;
using System.Text;
using Panko.Api.Crumbs;
using Panko.Api.Domain;
using Panko.Api.Options;
using Panko.Api.Cases;
using Panko.Api.Security;

namespace Panko.Api.Tests;

public sealed class CrumbSourceSnapshotTests
{
    private static readonly DateTimeOffset WindowStart = DateTimeOffset.Parse("2026-07-11T09:30:00Z");
    private static readonly DateTimeOffset FirstWindowEnd = DateTimeOffset.Parse("2026-07-11T10:05:00Z");
    private static readonly DateTimeOffset SecondWindowEnd = DateTimeOffset.Parse("2026-07-11T10:10:00Z");

    [Fact]
    public async Task GrafanaMetricSnapshot_RetainsReducerTimestampBaselineAndBreachWindow()
    {
        var metricCalls = 0;
        var baselineAt = DateTimeOffset.Parse("2026-07-11T09:50:00Z");
        var firstBreachAt = DateTimeOffset.Parse("2026-07-11T10:01:00Z");
        var breachContinuesAt = DateTimeOffset.Parse("2026-07-11T10:02:00Z");
        var secondBreachAt = DateTimeOffset.Parse("2026-07-11T10:03:00Z");
        var handler = new DelegateHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/api/annotations", StringComparison.Ordinal))
            {
                return Json("[]");
            }

            Assert.EndsWith("/api/ds/query", request.RequestUri.AbsolutePath, StringComparison.Ordinal);
            var values = Interlocked.Increment(ref metricCalls) == 1
                ? "99, 0.22, 0.8, 1.7, 1.6, 0.7"
                : "99, 0.22, 0.8, 1.2, 1.6, 1.9";
            return Json($$"""
                {
                  "results": {
                    "A": {
                      "frames": [{
                        "schema": { "fields": [
                          { "name": "Time", "type": "time" },
                          { "name": "latency", "type": "number" },
                          { "name": "secondary", "type": "number" }
                        ] },
                        "data": { "values": [
                          [
                            {{WindowStart.AddMinutes(-1).ToUnixTimeMilliseconds()}},
                            {{baselineAt.ToUnixTimeMilliseconds()}},
                            {{DateTimeOffset.Parse("2026-07-11T10:00:30Z").ToUnixTimeMilliseconds()}},
                            {{firstBreachAt.ToUnixTimeMilliseconds()}},
                            {{breachContinuesAt.ToUnixTimeMilliseconds()}},
                            {{secondBreachAt.ToUnixTimeMilliseconds()}}
                          ],
                          [{{values}}],
                          [99, 0.1, 0.2, 0.3, 0.2, 0.1]
                        ] }
                      }]
                    }
                  }
                }
                """);
        });
        var transport = Transport("https://grafana.example");
        var recipe = new Recipe
        {
            Id = "recipe",
            Grafana = new GrafanaScope
            {
                OrganizationId = 42,
                Queries =
                [
                    new GrafanaQuery
                    {
                        Name = "request failures",
                        DatasourceUid = "prometheus-main",
                        Expression = "sum(rate(http_requests_failed_total[5m]))",
                        Reducer = "maximum",
                        WarningThreshold = 1,
                        CriticalThreshold = 1.5,
                        Direction = "above",
                        Unit = "seconds"
                    }
                ]
            }
        };
        var connector = new GrafanaCrumbSource(
            new StubHttpClientFactory(handler), new ThrowingMcpAdapter(), new SafeTemplateRenderer(),
            TestConfiguration.CrumbSources(grafana: transport), TestConfiguration.Credentials());

        var first = await connector.CollectAsync(Context(recipe), Scope(FirstWindowEnd), CancellationToken.None);
        var second = await connector.CollectAsync(Context(recipe), Scope(SecondWindowEnd), CancellationToken.None);

        var firstMetric = Assert.Single(first.Crumbs);
        var secondMetric = Assert.Single(second.Crumbs);
        Assert.Equal(firstMetric.Id, secondMetric.Id);
        Assert.Equal("metric-query", secondMetric.ObjectType);
        Assert.Equal("prometheus-main:request failures", secondMetric.ObjectId);
        Assert.Equal(firstBreachAt, firstMetric.OccurredAt);
        Assert.Equal(secondBreachAt, secondMetric.OccurredAt);
        Assert.Equal("critical", firstMetric.Severity);
        Assert.Contains("rose from a 220 ms pre-Case baseline to 1.7 seconds", firstMetric.Summary, StringComparison.Ordinal);
        Assert.Contains("rose from a 220 ms pre-Case baseline to 1.9 seconds", secondMetric.Summary, StringComparison.Ordinal);

        var firstScope = firstMetric.Provenance["scope"]!.AsObject();
        Assert.Equal(firstBreachAt, firstScope["breachStartedAt"]!.GetValue<DateTimeOffset>());
        Assert.Equal(secondBreachAt, firstScope["breachEndedAt"]!.GetValue<DateTimeOffset>());
        var scope = secondMetric.Provenance["scope"]!.AsObject();
        Assert.Equal("maximum", scope["reducer"]!.GetValue<string>());
        Assert.Equal(1.9, scope["reducedValue"]!.GetValue<double>());
        Assert.Equal(secondBreachAt, scope["observedAt"]!.GetValue<DateTimeOffset>());
        Assert.Equal(firstBreachAt, scope["breachStartedAt"]!.GetValue<DateTimeOffset>());
        Assert.True(scope.ContainsKey("breachEndedAt"));
        Assert.Null(scope["breachEndedAt"]);
        Assert.Equal(1, scope["warningThreshold"]!.GetValue<double>());
        Assert.Equal(1.5, scope["criticalThreshold"]!.GetValue<double>());
        Assert.Equal("above", scope["direction"]!.GetValue<string>());
        Assert.Equal("seconds", scope["unit"]!.GetValue<string>());
        Assert.Equal(8, scope["sampleCount"]!.GetValue<int>());
        Assert.True(scope["timestampSupported"]!.GetValue<bool>());
        Assert.Equal(0.22, scope["baselineValue"]!.GetValue<double>());
        Assert.Equal(baselineAt, scope["baselineObservedAt"]!.GetValue<DateTimeOffset>());
        Assert.Equal(1, scope["baselineSampleCount"]!.GetValue<int>());
        Assert.Equal("case", scope["comparisonPeriod"]!.GetValue<string>());
        Assert.Equal(WindowStart, scope["exactWindowStart"]!.GetValue<DateTimeOffset>());
        Assert.Equal(SecondWindowEnd, scope["exactWindowEnd"]!.GetValue<DateTimeOffset>());
        var trailEntry = Assert.Single(second.Trail);
        Assert.Equal(secondBreachAt, trailEntry.OccurredAt);
        Assert.Equal("metric", trailEntry.Kind);
        Assert.Equal("critical", trailEntry.Severity);

        var caseFile = ComposeTwice(first, second);

        var accumulatedMetric = Assert.Single(caseFile.Crumbs);
        Assert.Equal(secondMetric.Id, accumulatedMetric.Id);
        Assert.Equal(secondMetric.Summary, accumulatedMetric.Summary);
        Assert.Equal(secondBreachAt, accumulatedMetric.OccurredAt);
    }

    [Theory]
    [InlineData("minimum", 2, "2026-07-11T10:01:00Z")]
    [InlineData("last", 5, "2026-07-11T10:02:00Z")]
    public async Task GrafanaMetricSnapshot_SelectsConfiguredReducerSample(
        string reducer,
        double expectedValue,
        string expectedObservedAt)
    {
        var handler = new DelegateHandler(request => request.RequestUri!.AbsolutePath.EndsWith(
            "/api/annotations", StringComparison.Ordinal)
            ? Json("[]")
            : Json("""
                {"results":{"A":{"frames":[{
                  "schema":{"fields":[{"type":"time"},{"type":"number"}]},
                  "data":{"values":[
                    ["2026-07-11T10:00:30Z","2026-07-11T10:01:00Z","2026-07-11T10:02:00Z"],
                    [7,2,5]
                  ]}
                }]}}}
                """));
        var recipe = new Recipe
        {
            Id = "recipe",
            Grafana = new GrafanaScope
            {
                Queries =
                [
                    new GrafanaQuery
                    {
                        Name = "queue depth",
                        DatasourceUid = "prometheus-main",
                        Expression = "queue_depth",
                        Reducer = reducer,
                        Unit = "items"
                    }
                ]
            }
        };
        var connector = new GrafanaCrumbSource(
            new StubHttpClientFactory(handler), new ThrowingMcpAdapter(), new SafeTemplateRenderer(),
            TestConfiguration.CrumbSources(grafana: Transport("https://grafana.example")),
            TestConfiguration.Credentials());

        var result = await connector.CollectAsync(
            Context(recipe), Scope(FirstWindowEnd), CancellationToken.None);

        var crumb = Assert.Single(result.Crumbs);
        var scope = crumb.Provenance["scope"]!.AsObject();
        Assert.Equal(expectedValue, scope["reducedValue"]!.GetValue<double>());
        Assert.Equal(DateTimeOffset.Parse(expectedObservedAt), crumb.OccurredAt);
        Assert.Equal(DateTimeOffset.Parse(expectedObservedAt), scope["observedAt"]!.GetValue<DateTimeOffset>());
        Assert.Equal(reducer, scope["reducer"]!.GetValue<string>());
        Assert.Equal(3, scope["sampleCount"]!.GetValue<int>());
        Assert.Equal("info", crumb.Severity);
        Assert.Empty(result.Trail);
    }

    [Fact]
    public async Task GrafanaMetricSnapshot_PreTriggerOnlyBreachIsInformational()
    {
        var observedAt = DateTimeOffset.Parse("2026-07-11T09:55:00Z");
        var handler = new DelegateHandler(request => request.RequestUri!.AbsolutePath.EndsWith(
            "/api/annotations", StringComparison.Ordinal)
            ? Json("[]")
            : Json($$$"""
                {
                  "results": {
                    "A": {
                      "frames": [{
                        "schema": { "fields": [{ "type": "time" }, { "type": "number" }] },
                        "data": { "values": [[{{{observedAt.ToUnixTimeMilliseconds()}}}], [9]] }
                      }]
                    }
                  }
                }
                """));
        var recipe = new Recipe
        {
            Id = "recipe",
            Grafana = new GrafanaScope
            {
                Queries =
                [
                    new GrafanaQuery
                    {
                        Name = "request failures",
                        DatasourceUid = "prometheus-main",
                        Expression = "failures",
                        WarningThreshold = 5
                    }
                ]
            }
        };
        var connector = new GrafanaCrumbSource(
            new StubHttpClientFactory(handler), new ThrowingMcpAdapter(), new SafeTemplateRenderer(),
            TestConfiguration.CrumbSources(grafana: Transport("https://grafana.example")),
            TestConfiguration.Credentials());

        var result = await connector.CollectAsync(
            Context(recipe), Scope(FirstWindowEnd), CancellationToken.None);

        var crumb = Assert.Single(result.Crumbs);
        var scope = crumb.Provenance["scope"]!.AsObject();
        Assert.Equal(observedAt, crumb.OccurredAt);
        Assert.Equal("pre-case", scope["comparisonPeriod"]!.GetValue<string>());
        Assert.Equal(WindowStart, scope["baselineWindowStart"]!.GetValue<DateTimeOffset>());
        Assert.Equal(
            DateTimeOffset.Parse("2026-07-11T10:00:00Z"),
            scope["baselineWindowEnd"]!.GetValue<DateTimeOffset>());
        Assert.Equal("info", crumb.Severity);
        Assert.Null(scope["breachStartedAt"]);
        Assert.Null(scope["breachEndedAt"]);
        Assert.Empty(result.Trail);
    }

    [Fact]
    public async Task GrafanaMetricSnapshot_UsesWindowEndOnlyWhenFrameHasNoTimestamp()
    {
        var handler = new DelegateHandler(request => request.RequestUri!.AbsolutePath.EndsWith(
            "/api/annotations", StringComparison.Ordinal)
            ? Json("[]")
            : Json("""
                {"results":{"A":{"frames":[{
                  "schema":{"fields":[{"type":"number"}]},
                  "data":{"values":[[7,9]]}
                }]}}}
                """));
        var recipe = new Recipe
        {
            Id = "recipe",
            Grafana = new GrafanaScope
            {
                Queries =
                [
                    new GrafanaQuery
                    {
                        Name = "request failures",
                        DatasourceUid = "prometheus-main",
                        Expression = "failures",
                        WarningThreshold = 5
                    }
                ]
            }
        };
        var connector = new GrafanaCrumbSource(
            new StubHttpClientFactory(handler), new ThrowingMcpAdapter(), new SafeTemplateRenderer(),
            TestConfiguration.CrumbSources(grafana: Transport("https://grafana.example")),
            TestConfiguration.Credentials());

        var result = await connector.CollectAsync(
            Context(recipe), Scope(FirstWindowEnd), CancellationToken.None);

        var crumb = Assert.Single(result.Crumbs);
        var scope = crumb.Provenance["scope"]!.AsObject();
        Assert.Equal(FirstWindowEnd, crumb.OccurredAt);
        Assert.Equal("warning", crumb.Severity);
        Assert.Equal(9, scope["reducedValue"]!.GetValue<double>());
        Assert.Equal(5, scope["warningThreshold"]!.GetValue<double>());
        Assert.False(scope["timestampSupported"]!.GetValue<bool>());
        Assert.True(scope.ContainsKey("observedAt"));
        Assert.Null(scope["observedAt"]);
        Assert.Equal(2, scope["sampleCount"]!.GetValue<int>());
        Assert.Equal("query-window", scope["comparisonPeriod"]!.GetValue<string>());
        Assert.Empty(result.Trail);
    }

    [Fact]
    public async Task GrafanaMetricSnapshot_ComparesBaselineFromTheObservedLogicalSeries()
    {
        var baselineAt = DateTimeOffset.Parse("2026-07-11T09:50:00Z");
        var currentAt = DateTimeOffset.Parse("2026-07-11T10:01:00Z");
        var handler = new DelegateHandler(request => request.RequestUri!.AbsolutePath.EndsWith(
            "/api/annotations", StringComparison.Ordinal)
            ? Json("[]")
            : Json($$$"""
                {
                  "results": {
                    "A": {
                      "frames": [{
                        "schema": {
                          "name": "latency",
                          "fields": [
                            { "name": "Time", "type": "time" },
                            { "name": "Value", "type": "number", "labels": { "pod": "a" } },
                            { "name": "Value", "type": "number", "labels": { "pod": "b" } }
                          ]
                        },
                        "data": { "values": [
                          [{{{baselineAt.ToUnixTimeMilliseconds()}}}, {{{currentAt.ToUnixTimeMilliseconds()}}}],
                          [9, 10],
                          [1, 20]
                        ] }
                      }]
                    }
                  }
                }
                """));
        var recipe = new Recipe
        {
            Id = "recipe",
            Grafana = new GrafanaScope
            {
                Queries =
                [
                    new GrafanaQuery
                    {
                        Name = "pod latency",
                        DatasourceUid = "prometheus-main",
                        Expression = "latency",
                        Reducer = "maximum",
                        Unit = "items"
                    }
                ]
            }
        };
        var connector = new GrafanaCrumbSource(
            new StubHttpClientFactory(handler), new ThrowingMcpAdapter(), new SafeTemplateRenderer(),
            TestConfiguration.CrumbSources(grafana: Transport("https://grafana.example")),
            TestConfiguration.Credentials());

        var result = await connector.CollectAsync(
            Context(recipe), Scope(FirstWindowEnd), CancellationToken.None);

        var crumb = Assert.Single(result.Crumbs);
        var scope = crumb.Provenance["scope"]!.AsObject();
        Assert.Equal(CrumbSourceHealth.Complete, result.Health);
        Assert.Equal(20, scope["reducedValue"]!.GetValue<double>());
        Assert.Equal(1, scope["baselineValue"]!.GetValue<double>());
        Assert.Equal(1, scope["baselineSampleCount"]!.GetValue<int>());
        Assert.Equal(scope["observedSeries"]!.GetValue<string>(), scope["baselineSeries"]!.GetValue<string>());
        Assert.Contains("pod=\"b\"", scope["observedSeries"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(WindowStart, scope["baselineWindowStart"]!.GetValue<DateTimeOffset>());
        Assert.Equal(DateTimeOffset.Parse("2026-07-11T10:00:00Z"), scope["baselineWindowEnd"]!.GetValue<DateTimeOffset>());
        Assert.Contains("rose from a 1 items pre-Case baseline to 20 items", crumb.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GrafanaMetricSnapshot_CombinesOneLogicalSeriesSplitAcrossFrames()
    {
        var handler = new DelegateHandler(request => request.RequestUri!.AbsolutePath.EndsWith(
            "/api/annotations", StringComparison.Ordinal)
            ? Json("[]")
            : Json("""
                {"results":{"A":{"frames":[
                  {
                    "schema":{"name":"pod-a","fields":[
                      {"name":"Time","type":"time"},
                      {"name":"Value","type":"number","labels":{"pod":"a"}}
                    ]},
                    "data":{"values":[
                      ["2026-07-11T09:50:00Z","2026-07-11T10:01:00Z"],
                      [0.2,1.2]
                    ]}
                  },
                  {
                    "schema":{"name":"pod-a","fields":[
                      {"name":"Time","type":"time"},
                      {"name":"Value","type":"number","labels":{"pod":"a"}}
                    ]},
                    "data":{"values":[
                      ["2026-07-11T10:02:00Z","2026-07-11T10:03:00Z"],
                      [1.7,0.5]
                    ]}
                  }
                ]}}}
                """));
        var recipe = new Recipe
        {
            Id = "recipe",
            Grafana = new GrafanaScope
            {
                Queries =
                [
                    new GrafanaQuery
                    {
                        Name = "pod latency",
                        DatasourceUid = "prometheus-main",
                        Expression = "latency",
                        Reducer = "maximum",
                        WarningThreshold = 1,
                        CriticalThreshold = 1.5,
                        Unit = "seconds"
                    }
                ]
            }
        };
        var connector = new GrafanaCrumbSource(
            new StubHttpClientFactory(handler), new ThrowingMcpAdapter(), new SafeTemplateRenderer(),
            TestConfiguration.CrumbSources(grafana: Transport("https://grafana.example")),
            TestConfiguration.Credentials());

        var result = await connector.CollectAsync(
            Context(recipe), Scope(FirstWindowEnd), CancellationToken.None);

        var crumb = Assert.Single(result.Crumbs);
        var scope = crumb.Provenance["scope"]!.AsObject();
        Assert.Equal(CrumbSourceHealth.Complete, result.Health);
        Assert.Equal(1.7, scope["reducedValue"]!.GetValue<double>());
        Assert.Equal(0.2, scope["baselineValue"]!.GetValue<double>());
        Assert.Equal(DateTimeOffset.Parse("2026-07-11T10:01:00Z"), scope["breachStartedAt"]!.GetValue<DateTimeOffset>());
        Assert.Equal(DateTimeOffset.Parse("2026-07-11T10:03:00Z"), scope["breachEndedAt"]!.GetValue<DateTimeOffset>());
        Assert.Equal(scope["observedSeries"]!.GetValue<string>(), scope["baselineSeries"]!.GetValue<string>());
        Assert.True(scope["reductionComplete"]!.GetValue<bool>());
    }

    [Fact]
    public async Task GrafanaMetricSnapshot_LastIsStableAcrossFrameOrderAndDisablesAmbiguousTiming()
    {
        const string frameA = """
            {"schema":{"name":"pod-a","fields":[{"name":"Time","type":"time"},{"name":"Value","type":"number","labels":{"pod":"a"}}]},"data":{"values":[["2026-07-11T10:01:00Z"],[5]]}}
            """;
        const string frameB = """
            {"schema":{"name":"pod-b","fields":[{"name":"Time","type":"time"},{"name":"Value","type":"number","labels":{"pod":"b"}}]},"data":{"values":[["2026-07-11T10:01:00Z"],[9]]}}
            """;
        var metricCalls = 0;
        var handler = new DelegateHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/api/annotations", StringComparison.Ordinal))
            {
                return Json("[]");
            }

            var frames = Interlocked.Increment(ref metricCalls) == 1
                ? $"{frameA},{frameB}"
                : $"{frameB},{frameA}";
            return Json("{\"results\":{\"A\":{\"frames\":[" + frames + "]}}}");
        });
        var recipe = new Recipe
        {
            Id = "recipe",
            Grafana = new GrafanaScope
            {
                Queries =
                [
                    new GrafanaQuery
                    {
                        Name = "queue depth",
                        DatasourceUid = "prometheus-main",
                        Expression = "queue_depth",
                        Reducer = "last",
                        WarningThreshold = 4,
                        Unit = "items"
                    }
                ]
            }
        };
        var connector = new GrafanaCrumbSource(
            new StubHttpClientFactory(handler), new ThrowingMcpAdapter(), new SafeTemplateRenderer(),
            TestConfiguration.CrumbSources(grafana: Transport("https://grafana.example")),
            TestConfiguration.Credentials());

        var first = await connector.CollectAsync(Context(recipe), Scope(FirstWindowEnd), CancellationToken.None);
        var second = await connector.CollectAsync(Context(recipe), Scope(FirstWindowEnd), CancellationToken.None);

        var firstCrumb = Assert.Single(first.Crumbs);
        var secondCrumb = Assert.Single(second.Crumbs);
        var firstScope = firstCrumb.Provenance["scope"]!.AsObject();
        var secondScope = secondCrumb.Provenance["scope"]!.AsObject();
        Assert.Equal(CrumbSourceHealth.Partial, first.Health);
        Assert.Equal(CrumbSourceHealth.Partial, second.Health);
        Assert.Equal(firstScope["reducedValue"]!.GetValue<double>(), secondScope["reducedValue"]!.GetValue<double>());
        Assert.Equal(firstScope["observedSeries"]!.GetValue<string>(), secondScope["observedSeries"]!.GetValue<string>());
        Assert.Contains("pod=\"a\"", firstScope["observedSeries"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.True(firstScope["seriesAmbiguous"]!.GetValue<bool>());
        Assert.False(firstScope["reductionComplete"]!.GetValue<bool>());
        Assert.Equal(2, firstScope["lastCandidateSeriesCount"]!.GetValue<int>());
        Assert.Null(firstScope["observedAt"]);
        Assert.False(firstScope["timestampSupported"]!.GetValue<bool>());
        Assert.Equal("info", firstCrumb.Severity);
        Assert.Empty(first.Trail);
        Assert.Contains("did not have a unique logical series", first.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GrafanaMetricSnapshot_MixedTimestampPairingIsPartialAndCannotCreateTemporalCrumbs()
    {
        var observedAt = DateTimeOffset.Parse("2026-07-11T10:01:00Z");
        var handler = new DelegateHandler(request => request.RequestUri!.AbsolutePath.EndsWith(
            "/api/annotations", StringComparison.Ordinal)
            ? Json("[]")
            : Json(
                "{\"results\":{\"A\":{\"frames\":[{\"schema\":{\"fields\":[{\"type\":\"time\"},{\"type\":\"number\"}]},"
                + "\"data\":{\"values\":[["
                + observedAt.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ",null],[1,100]]}}]}}}"));
        var recipe = new Recipe
        {
            Id = "recipe",
            Grafana = new GrafanaScope
            {
                Queries =
                [
                    new GrafanaQuery
                    {
                        Name = "request failures",
                        DatasourceUid = "prometheus-main",
                        Expression = "failures",
                        WarningThreshold = 0.5
                    }
                ]
            }
        };
        var connector = new GrafanaCrumbSource(
            new StubHttpClientFactory(handler), new ThrowingMcpAdapter(), new SafeTemplateRenderer(),
            TestConfiguration.CrumbSources(grafana: Transport("https://grafana.example")),
            TestConfiguration.Credentials());

        var result = await connector.CollectAsync(
            Context(recipe), Scope(FirstWindowEnd), CancellationToken.None);

        var crumb = Assert.Single(result.Crumbs);
        var scope = crumb.Provenance["scope"]!.AsObject();
        Assert.Equal(CrumbSourceHealth.Partial, result.Health);
        Assert.Equal(1, scope["reducedValue"]!.GetValue<double>());
        Assert.Equal(2, scope["numericSampleCount"]!.GetValue<int>());
        Assert.Equal(1, scope["timestampedSampleCount"]!.GetValue<int>());
        Assert.Equal(1, scope["untimestampedSampleCount"]!.GetValue<int>());
        Assert.Equal(1, scope["unpairedTimestampSampleCount"]!.GetValue<int>());
        Assert.True(scope["mixedTimestampSupport"]!.GetValue<bool>());
        Assert.False(scope["reductionComplete"]!.GetValue<bool>());
        Assert.Null(scope["observedAt"]);
        Assert.False(scope["timestampSupported"]!.GetValue<bool>());
        Assert.Equal(FirstWindowEnd, crumb.OccurredAt);
        Assert.Equal("info", crumb.Severity);
        Assert.Empty(result.Trail);
        Assert.Contains("could not pair 1 numeric samples", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GrafanaMetricSnapshot_TruncationIsPartialAndCannotCreateTemporalCrumbs()
    {
        var values = string.Join(',', Enumerable.Repeat("0", 10_000).Append("99"));
        var handler = new DelegateHandler(request => request.RequestUri!.AbsolutePath.EndsWith(
            "/api/annotations", StringComparison.Ordinal)
            ? Json("[]")
            : Json(
                "{\"results\":{\"A\":{\"frames\":[{\"schema\":{\"fields\":[{\"type\":\"number\"}]},"
                + "\"data\":{\"values\":[[" + values + "]]}}]}}}"));
        var recipe = new Recipe
        {
            Id = "recipe",
            Grafana = new GrafanaScope
            {
                Queries =
                [
                    new GrafanaQuery
                    {
                        Name = "request failures",
                        DatasourceUid = "prometheus-main",
                        Expression = "failures",
                        WarningThreshold = 1
                    }
                ]
            }
        };
        var connector = new GrafanaCrumbSource(
            new StubHttpClientFactory(handler), new ThrowingMcpAdapter(), new SafeTemplateRenderer(),
            TestConfiguration.CrumbSources(grafana: Transport("https://grafana.example")),
            TestConfiguration.Credentials());

        var result = await connector.CollectAsync(
            Context(recipe), Scope(FirstWindowEnd), CancellationToken.None);

        var crumb = Assert.Single(result.Crumbs);
        var scope = crumb.Provenance["scope"]!.AsObject();
        Assert.Equal(CrumbSourceHealth.Partial, result.Health);
        Assert.Equal(0, scope["reducedValue"]!.GetValue<double>());
        Assert.True(scope["samplesTruncated"]!.GetValue<bool>());
        Assert.Equal(10_000, scope["parsedSampleCount"]!.GetValue<int>());
        Assert.Equal(10_001, scope["numericSampleCount"]!.GetValue<int>());
        Assert.Equal(1, scope["truncatedSampleCount"]!.GetValue<int>());
        Assert.False(scope["reductionComplete"]!.GetValue<bool>());
        Assert.Null(scope["observedAt"]);
        Assert.False(scope["timestampSupported"]!.GetValue<bool>());
        Assert.Equal("info", crumb.Severity);
        Assert.Contains("only 10000 were retained", result.Diagnostic, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(4, "warning")]
    [InlineData(5, "warning")]
    [InlineData(6, "info")]
    public async Task GrafanaMetricSnapshot_CanonicalWarningThresholdIsInclusive(
        double warningThreshold,
        string expectedSeverity)
    {
        var handler = new DelegateHandler(request => request.RequestUri!.AbsolutePath.EndsWith(
            "/api/annotations", StringComparison.Ordinal)
            ? Json("[]")
            : Json("""
                {"results":{"A":{"frames":[{
                  "schema":{"fields":[{"type":"time"},{"type":"number"}]},
                  "data":{"values":[["2026-07-11T10:01:00Z"],[5]]}
                }]}}}
                """));
        var recipe = new Recipe
        {
            Id = "recipe",
            Grafana = new GrafanaScope
            {
                Queries =
                [
                    new GrafanaQuery
                    {
                        Name = "request failures",
                        DatasourceUid = "prometheus-main",
                        Expression = "failures",
                        WarningThreshold = warningThreshold
                    }
                ]
            }
        };
        var connector = new GrafanaCrumbSource(
            new StubHttpClientFactory(handler), new ThrowingMcpAdapter(), new SafeTemplateRenderer(),
            TestConfiguration.CrumbSources(grafana: Transport("https://grafana.example")),
            TestConfiguration.Credentials());

        var result = await connector.CollectAsync(
            Context(recipe), Scope(FirstWindowEnd), CancellationToken.None);

        var crumb = Assert.Single(result.Crumbs);
        var scope = crumb.Provenance["scope"]!.AsObject();
        Assert.Equal(CrumbSourceHealth.Complete, result.Health);
        Assert.True(scope["reductionComplete"]!.GetValue<bool>());
        Assert.Equal(expectedSeverity, crumb.Severity);
    }

    [Fact]
    public async Task GrafanaMetricSnapshot_ContextMetricCannotCreateAnAnomaly()
    {
        var handler = new DelegateHandler(request => request.RequestUri!.AbsolutePath.EndsWith(
            "/api/annotations", StringComparison.Ordinal)
            ? Json("[]")
            : Json("""
                {"results":{"A":{"frames":[{
                  "schema":{"fields":[{"type":"time"},{"type":"number"}]},
                  "data":{"values":[["2026-07-11T10:01:00Z"],[99]]}
                }]}}}
                """));
        var recipe = new Recipe
        {
            Id = "recipe",
            Grafana = new GrafanaScope
            {
                Queries =
                [
                    new GrafanaQuery
                    {
                        Name = "request rate",
                        DatasourceUid = "prometheus-main",
                        Expression = "request_rate",
                        MetricId = "request-rate",
                        Role = "traffic",
                        CrumbMode = "context",
                        Requirement = "required",
                        WarningThreshold = 1,
                        CriticalThreshold = 2
                    }
                ]
            }
        };
        var connector = new GrafanaCrumbSource(
            new StubHttpClientFactory(handler), new ThrowingMcpAdapter(), new SafeTemplateRenderer(),
            TestConfiguration.CrumbSources(grafana: Transport("https://grafana.example")),
            TestConfiguration.Credentials());

        var result = await connector.CollectAsync(
            Context(recipe), Scope(FirstWindowEnd), CancellationToken.None);

        var crumb = Assert.Single(result.Crumbs);
        var crumbScope = crumb.Provenance["scope"]!.AsObject();
        Assert.Equal(CrumbSourceHealth.Complete, result.Health);
        Assert.Equal("info", crumb.Severity);
        Assert.Empty(result.Trail);
        Assert.Equal("request-rate", crumbScope["metricId"]!.GetValue<string>());
        Assert.Equal("traffic", crumbScope["role"]!.GetValue<string>());
        Assert.Equal("context", crumbScope["crumbMode"]!.GetValue<string>());
        Assert.Equal("required", crumbScope["requirement"]!.GetValue<string>());
    }

    [Fact]
    public async Task GrafanaMetricSnapshot_MissingRequiredMetricIsPartial()
    {
        var handler = new DelegateHandler(request => request.RequestUri!.AbsolutePath.EndsWith(
            "/api/annotations", StringComparison.Ordinal)
            ? Json("[]")
            : Json("""{"results":{"A":{"frames":[]}}}"""));
        var recipe = new Recipe
        {
            Id = "recipe",
            Grafana = new GrafanaScope
            {
                Queries =
                [
                    new GrafanaQuery
                    {
                        Name = "availability",
                        DatasourceUid = "prometheus-main",
                        Expression = "availability",
                        MetricId = "availability",
                        Role = "availability",
                        CrumbMode = "anomaly",
                        Requirement = "required",
                        WarningThreshold = 0.999,
                        CriticalThreshold = 0,
                        Direction = "below"
                    }
                ]
            }
        };
        var connector = new GrafanaCrumbSource(
            new StubHttpClientFactory(handler), new ThrowingMcpAdapter(), new SafeTemplateRenderer(),
            TestConfiguration.CrumbSources(grafana: Transport("https://grafana.example")),
            TestConfiguration.Credentials());

        var result = await connector.CollectAsync(
            Context(recipe), Scope(FirstWindowEnd), CancellationToken.None);

        var crumb = Assert.Single(result.Crumbs);
        Assert.Equal(CrumbSourceHealth.Partial, result.Health);
        Assert.Equal("info", crumb.Severity);
        Assert.Contains("Required Grafana metric 'availability' returned no numeric samples", result.Diagnostic);
    }

    [Fact]
    public async Task VictoriaLogsRecollection_ReplacesCountAndPreservesDistinctRealEvents()
    {
        var hitsCalls = 0;
        var sampleCalls = 0;
        const string firstEvent = "{\"_time\":\"2026-07-11T10:01:00Z\",\"_msg\":\"connection reset\"}";
        const string secondEvent = "{\"_time\":\"2026-07-11T10:02:00Z\",\"_msg\":\"connection reset\"}";
        var handler = new DelegateHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/select/logsql/hits", StringComparison.Ordinal))
            {
                var total = Interlocked.Increment(ref hitsCalls);
                return Json($$"""{"hits":[{"total":{{total}}}]}""");
            }

            Assert.EndsWith("/select/logsql/query", request.RequestUri.AbsolutePath, StringComparison.Ordinal);
            var sample = Interlocked.Increment(ref sampleCalls) == 1
                ? firstEvent
                : $"{firstEvent}\n{secondEvent}";
            return Text(sample);
        });
        var transport = Transport("https://logs.example");
        var recipe = new Recipe
        {
            Id = "recipe",
            VictoriaLogs = new VictoriaLogsScope
            {
                AccountId = "12",
                ProjectId = "payments",
                StreamFilters = new Dictionary<string, string> { ["environment"] = "production" },
                Queries =
                [
                    new VictoriaLogsQuery
                    {
                        Name = "connection errors",
                        Expression = "_msg:~\"connection reset\""
                    }
                ]
            }
        };
        var connector = new VictoriaLogsCrumbSource(
            new StubHttpClientFactory(handler), new ThrowingMcpAdapter(), new SafeTemplateRenderer(),
            TestConfiguration.CrumbSources(victoriaLogs: transport), TestConfiguration.Credentials());

        var first = await connector.CollectAsync(Context(recipe), Scope(FirstWindowEnd), CancellationToken.None);
        var second = await connector.CollectAsync(Context(recipe), Scope(SecondWindowEnd), CancellationToken.None);

        var firstCount = Assert.Single(first.Crumbs, crumb => crumb.Category == "log-count");
        var secondCount = Assert.Single(second.Crumbs, crumb => crumb.Category == "log-count");
        Assert.Equal(firstCount.Id, secondCount.Id);
        Assert.Equal("log-query", secondCount.ObjectType);
        Assert.Equal("connection errors", secondCount.ObjectId);
        Assert.Contains(": 1 matching log events", firstCount.Summary, StringComparison.Ordinal);
        Assert.Contains(": 2 matching log events", secondCount.Summary, StringComparison.Ordinal);

        var firstLogEvent = Assert.Single(first.Crumbs, crumb => crumb.Category == "first-error");
        var secondRunEvents = second.Crumbs
            .Where(crumb => crumb.Category is "first-error" or "log-sample")
            .OrderBy(crumb => crumb.OccurredAt)
            .ToList();
        Assert.Equal(2, secondRunEvents.Count);
        Assert.Equal(firstLogEvent.Id, secondRunEvents[0].Id);
        Assert.NotEqual(secondRunEvents[0].Id, secondRunEvents[1].Id);

        var caseFile = ComposeTwice(first, second);

        Assert.Single(caseFile.Crumbs, crumb => crumb.Category == "log-count");
        var accumulatedEvents = caseFile.Crumbs
            .Where(crumb => crumb.Category is "first-error" or "log-sample")
            .OrderBy(crumb => crumb.OccurredAt)
            .ToList();
        Assert.Equal(2, accumulatedEvents.Count);
        Assert.Equal(new[]
        {
            DateTimeOffset.Parse("2026-07-11T10:01:00Z"),
            DateTimeOffset.Parse("2026-07-11T10:02:00Z")
        }, accumulatedEvents.Select(crumb => crumb.OccurredAt));
    }

    [Fact]
    public async Task VictoriaLogsPromotesFirstConfiguredPatternMatchToAnAnchor()
    {
        var handler = new DelegateHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/select/logsql/hits", StringComparison.Ordinal))
            {
                return Json("{\"hits\":[{\"total\":3}]}");
            }

            return Text("""
                {"_time":"2026-07-11T10:00:01Z","_msg":"generic error"}
                {"_time":"2026-07-11T10:00:02Z","_msg":"payment authorization exceeded 750ms"}
                {"_time":"2026-07-11T10:00:03Z","_msg":"payment authorization exceeded 900ms"}
                """);
        });
        var recipe = new Recipe
        {
            Id = "recipe",
            VictoriaLogs = new VictoriaLogsScope
            {
                StreamFilters = new Dictionary<string, string> { ["service"] = "payments" },
                Queries =
                [
                    new VictoriaLogsQuery
                    {
                        Name = "Errors",
                        Expression = "level:error",
                        AnchorPatterns =
                        [
                            new VictoriaLogsAnchorPattern
                            {
                                Name = "Payment authorisation timeout",
                                Pattern = "(?i)payment authori[sz]ation exceeded [0-9]+ms"
                            }
                        ]
                    }
                ]
            }
        };
        var connector = new VictoriaLogsCrumbSource(
            new StubHttpClientFactory(handler), new ThrowingMcpAdapter(), new SafeTemplateRenderer(),
            TestConfiguration.CrumbSources(victoriaLogs: Transport("https://logs.example")),
            TestConfiguration.Credentials());

        var result = await connector.CollectAsync(
            Context(recipe), Scope(FirstWindowEnd), CancellationToken.None);

        var anchors = result.Crumbs
            .Where(crumb => crumb.Category == "first-error")
            .OrderBy(crumb => crumb.OccurredAt)
            .ToList();
        Assert.Equal(2, anchors.Count);
        Assert.Contains("First observed Errors", anchors[0].Summary, StringComparison.Ordinal);
        Assert.Contains("Payment authorisation timeout", anchors[1].Summary, StringComparison.Ordinal);
        Assert.Equal(.9, anchors[1].Confidence);
        Assert.Contains("Payment authorisation timeout", anchors[1].Provenance.ToJsonString(), StringComparison.Ordinal);
        Assert.Single(result.Crumbs, crumb => crumb.Category == "log-sample");
        Assert.Equal(2, result.Trail.Count);
    }

    [Fact]
    public async Task PagerDutyRecollection_ReplacesMutableIncidentStatusSnapshot()
    {
        var calls = 0;
        var handler = new DelegateHandler(_ =>
        {
            var status = Interlocked.Increment(ref calls) == 1 ? "triggered" : "resolved";
            return Json($$"""
                {
                  "incident": {
                    "id": "PD-1",
                    "status": "{{status}}",
                    "created_at": "2026-07-11T10:00:00Z",
                    "html_url": "https://pagerduty.example/incidents/PD-1"
                  }
                }
                """);
        });
        var transport = Transport("https://pagerduty.example/api");
        var recipe = new Recipe
        {
            Id = "recipe",
            PagerDuty = new PagerDutyScope()
        };
        var connector = new PagerDutyCrumbSource(
            new StubHttpClientFactory(handler), new ThrowingMcpAdapter(),
            TestConfiguration.CrumbSources(pagerDuty: transport), TestConfiguration.Credentials());

        var first = await connector.CollectAsync(Context(recipe), Scope(FirstWindowEnd), CancellationToken.None);
        var second = await connector.CollectAsync(Context(recipe), Scope(SecondWindowEnd), CancellationToken.None);

        var firstCrumb = Assert.Single(first.Crumbs);
        var secondCrumb = Assert.Single(second.Crumbs);
        Assert.Equal(firstCrumb.Id, secondCrumb.Id);
        Assert.Contains("triggered", firstCrumb.Summary, StringComparison.Ordinal);
        Assert.Contains("resolved", secondCrumb.Summary, StringComparison.Ordinal);

        var caseFile = ComposeTwice(first, second);

        var retained = Assert.Single(caseFile.Crumbs);
        Assert.Equal(secondCrumb.Id, retained.Id);
        Assert.Equal(secondCrumb.Summary, retained.Summary);
    }

    [Fact]
    public async Task ResolvedPagerDutySnapshotProducesLifecycleTrail()
    {
        var handler = new DelegateHandler(_ => Json("""
            {
              "incident": {
                "id": "PD-1",
                "status": "resolved",
                "urgency": "high",
                "created_at": "2026-07-11T10:00:00Z",
                "last_status_change_at": "2026-07-11T10:20:00Z",
                "html_url": "https://pagerduty.example/incidents/PD-1"
              }
            }
            """));
        var transport = Transport("https://pagerduty.example/api");
        var recipe = new Recipe
        {
            Id = "recipe",
            PagerDuty = new PagerDutyScope()
        };
        var connector = new PagerDutyCrumbSource(
            new StubHttpClientFactory(handler), new ThrowingMcpAdapter(),
            TestConfiguration.CrumbSources(pagerDuty: transport), TestConfiguration.Credentials());

        var result = await connector.CollectAsync(Context(recipe), Scope(SecondWindowEnd), CancellationToken.None);

        Assert.Equal(2, result.Trail.Count);
        Assert.Collection(
            result.Trail,
            item =>
            {
                Assert.Equal("pagerduty-incident-triggered", item.Kind);
                Assert.Equal(DateTimeOffset.Parse("2026-07-11T10:00:00Z"), item.OccurredAt);
            },
            item =>
            {
                Assert.Equal("pagerduty-incident-state", item.Kind);
                Assert.Equal("PagerDuty incident resolved", item.Summary);
                Assert.Equal(DateTimeOffset.Parse("2026-07-11T10:20:00Z"), item.OccurredAt);
            });
    }

    [Fact]
    public async Task NomadRecollection_ReplacesMutableStatusesForStableObjects()
    {
        var collection = 0;
        var handler = new DelegateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/v1/job/payments", StringComparison.Ordinal))
            {
                var current = Interlocked.Increment(ref collection);
                var status = current == 1 ? "pending" : "dead";
                return Json($$"""{"Status":"{{status}}","SubmitTime":"2026-07-11T10:00:00Z"}""");
            }

            var secondCollection = Volatile.Read(ref collection) > 1;
            if (path.EndsWith("/allocations", StringComparison.Ordinal))
            {
                var status = secondCollection ? "lost" : "failed";
                return Json($$"""[{"ID":"alloc-123","ClientStatus":"{{status}}","ModifyTime":"2026-07-11T10:01:00Z"}]""");
            }
            if (path.EndsWith("/deployments", StringComparison.Ordinal))
            {
                var status = secondCollection ? "failed" : "running";
                return Json($$"""[{"ID":"deploy-123","Status":"{{status}}","ModifyTime":"2026-07-11T10:02:00Z"}]""");
            }

            Assert.EndsWith("/evaluations", path, StringComparison.Ordinal);
            var evaluationStatus = secondCollection ? "canceled" : "blocked";
            return Json($$"""[{"ID":"eval-123","Status":"{{evaluationStatus}}","ModifyTime":"2026-07-11T10:03:00Z"}]""");
        });
        var transport = Transport("https://nomad.example");
        var recipe = new Recipe
        {
            Id = "recipe",
            Nomad = new NomadScope
            {
                Region = "global",
                Namespaces = [new NomadNamespace { Name = "production", Jobs = ["payments"] }]
            }
        };
        var connector = new NomadCrumbSource(
            new StubHttpClientFactory(handler), new ThrowingMcpAdapter(),
            TestConfiguration.CrumbSources(nomad: transport), TestConfiguration.Credentials());

        var first = await connector.CollectAsync(Context(recipe), Scope(FirstWindowEnd), CancellationToken.None);
        var second = await connector.CollectAsync(Context(recipe), Scope(SecondWindowEnd), CancellationToken.None);

        var firstByObject = first.Crumbs.ToDictionary(CrumbObjectIdentity, StringComparer.Ordinal);
        var secondByObject = second.Crumbs.ToDictionary(CrumbObjectIdentity, StringComparer.Ordinal);
        Assert.Equal(firstByObject.Keys.Order(StringComparer.Ordinal), secondByObject.Keys.Order(StringComparer.Ordinal));
        Assert.All(firstByObject, pair => Assert.Equal(pair.Value.Id, secondByObject[pair.Key].Id));
        Assert.Contains(second.Crumbs, crumb => crumb.Summary.Contains("is dead", StringComparison.Ordinal));
        Assert.Contains(second.Crumbs, crumb => crumb.Summary.Contains("is lost", StringComparison.Ordinal));

        var caseFile = ComposeTwice(first, second);

        Assert.Equal(second.Crumbs.Count, caseFile.Crumbs.Count);
        Assert.All(second.Crumbs, expected =>
            Assert.Equal(expected.Summary, caseFile.Crumbs.Single(actual => actual.Id == expected.Id).Summary));
    }

    private static CaseFile ComposeTwice(CrumbSourceResult first, CrumbSourceResult second)
    {
        var composer = new CaseFileComposer(
            TimeProvider.System,
            new CrumbSourceRegistry(
                Array.Empty<ICrumbSourceAdapter>(),
                TestConfiguration.CrumbSources()));
        var caseRecord = new CaseRecord(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "PD-1",
            "payments",
            "recipe",
            "Payments failing",
            "high",
            PagerDutyIncidentState.Triggered,
            DateTimeOffset.Parse("2026-07-11T10:00:00Z"),
            DateTimeOffset.Parse("2026-07-11T10:00:01Z"),
            1,
            "collecting",
            false,
            null,
            "#cases",
            null,
            new Dictionary<string, string>());
        var recipe = new Recipe { Id = "recipe" };
        var ai = new AiSynthesis("unavailable", null, [], [], [], null);
        var initial = composer.Compose(caseRecord, recipe, "v1", [first], null, ai);
        return composer.Compose(caseRecord, recipe, "v1", [second], initial, ai);
    }

    private static CaseContext Context(Recipe recipe) => new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        "PD-1",
        "payments",
        "Payments failing",
        "high",
        PagerDutyIncidentState.Triggered,
        DateTimeOffset.Parse("2026-07-11T10:00:00Z"),
        new Dictionary<string, string>(),
        recipe);

    private static string CrumbObjectIdentity(Crumb crumb) =>
        $"{crumb.ObjectType}:{crumb.ObjectId}";

    private static CrumbScope Scope(DateTimeOffset end) => new(WindowStart, end, "v1", 100, 262144);

    private static ConnectorTransport Transport(string baseUrl) => new()
    {
        Mode = "api",
        BaseUrl = baseUrl,
        CredentialEnv = "TEST_TOKEN",
        TimeoutSeconds = 5,
        MaxItems = 100,
        MaxBytes = 262144
    };

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Text(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/x-ndjson")
    };

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(response(request));
    }

    private sealed class ThrowingMcpAdapter : IMcpCrumbSourceAdapter
    {
        public Task<CrumbSourceResult> CollectAsync(
            string source,
            McpToolConfiguration configuration,
            CaseContext context,
            CrumbScope scope,
            object allowedResources,
            string? allowedBaseUrl,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("MCP should not be called by native connector tests.");
    }
}
