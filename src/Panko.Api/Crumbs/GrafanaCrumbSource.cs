using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Panko.Api.Domain;
using Panko.Api.Cases;
using Panko.Api.Infrastructure;
using Panko.Api.Security;

namespace Panko.Api.Crumbs;

public sealed class GrafanaCrumbSource(
    IHttpClientFactory httpClientFactory,
    IMcpCrumbSourceAdapter mcp,
    SafeTemplateRenderer templates,
    CrumbSourceConfiguration crumbSources,
    ICredentialProvider credentials) : ICrumbSourceAdapter
{
    private const int MaximumMetricSamples = 10_000;

    public string Source => CrumbSourceRegistry.Grafana;
    public bool SupportsWindowExpansion => true;

    public Task<CrumbSourceResult> CollectAsync(CaseContext context, CrumbScope scope, CancellationToken cancellationToken)
    {
        var configuration = context.Recipe.Grafana;
        if (configuration is null) return Task.FromResult(CrumbSourceResult.Excluded(Source));
        var transport = crumbSources.For(Source);
        return CrumbSourceUtilities.CollectAsync(
            Source, transport, mcp, context, scope,
            new
            {
                configuration.OrganizationId,
                configuration.Dashboards,
                configuration.Queries,
                configuration.AnnotationTags
            }, async ct =>
        {
            var crumbs = new List<Crumb>();
            var trail = new List<TrailCandidate>();
            var links = new List<SourceLink>();
            var client = httpClientFactory.CreateClient();
            var fromMilliseconds = scope.Start.ToUnixTimeMilliseconds();
            var toMilliseconds = scope.End.ToUnixTimeMilliseconds();
            var budget = new CrumbSourceResponseBudget(
                scope.MaxBytes,
                transport.MaxBytes,
                1 + configuration.Queries.Count);
            var metricDiagnostics = new List<string>();
            var metricPartial = false;

            foreach (var dashboard in configuration.Dashboards)
            {
                var baseLink = $"{transport.BaseUrl.TrimEnd('/')}/d/{Uri.EscapeDataString(dashboard.Uid)}?from={fromMilliseconds}&to={toMilliseconds}";
                links.Add(new SourceLink($"Grafana dashboard {dashboard.Uid}", baseLink));
                foreach (var panelId in dashboard.PanelIds)
                {
                    links.Add(new SourceLink($"Grafana {dashboard.Uid} panel {panelId}", $"{baseLink}&viewPanel={panelId}"));
                }
            }

            var annotationParameters = new List<string>
            {
                $"from={fromMilliseconds}", $"to={toMilliseconds}", $"limit={Math.Min(scope.MaxItems, transport.MaxItems)}"
            };
            annotationParameters.AddRange(configuration.AnnotationTags.Select(tag => $"tags={Uri.EscapeDataString(tag)}"));
            var annotationsUrl = CrumbSourceUtilities.Url(transport, $"api/annotations?{string.Join('&', annotationParameters)}");
            const string annotationOperation = "GET /api/annotations";
            var annotationJson = await budget.TryReadJsonAsync(
                annotationOperation,
                async operationCancellationToken =>
                {
                    using var annotationRequest = CrumbSourceUtilities.CreateRequest(
                        HttpMethod.Get, annotationsUrl, transport, credentials);
                    annotationRequest.Headers.TryAddWithoutValidation(
                        "X-Grafana-Org-Id", configuration.OrganizationId.ToString());
                    return await client.SendAsync(
                        annotationRequest,
                        HttpCompletionOption.ResponseHeadersRead,
                        operationCancellationToken);
                },
                ct);
            if (annotationJson is not null)
            {
                using (annotationJson)
                {
                    foreach (var annotation in annotationJson.RootElement.EnumerateArray())
                    {
                        var at = CrumbSourceUtilities.Timestamp(annotation, "time", scope.End);
                        var text = CrumbSourceUtilities.Text(annotation, "text", "Grafana annotation");
                        var url = CrumbSourceUtilities.Text(annotation, "url", annotationsUrl);
                        crumbs.Add(new Crumb(
                            CrumbSourceUtilities.Id(Source, "annotation", at.ToUnixTimeMilliseconds().ToString(), text), Source, at, null,
                            "annotation", "info", text, null, url, 0.85,
                            CrumbSourceUtilities.Provenance("GET /api/annotations", new { configuration.AnnotationTags })));
                        trail.Add(new TrailCandidate(at, Source, "annotation", text, "info", url));
                    }
                }
            }

            var refIndex = 0;
            foreach (var query in configuration.Queries)
            {
                var expression = templates.Render(query.Expression, context.Labels);
                var queryBody = new
                {
                    from = fromMilliseconds.ToString(),
                    to = toMilliseconds.ToString(),
                    queries = new[]
                    {
                        new
                        {
                            refId = ((char)('A' + refIndex++ % 26)).ToString(),
                            datasource = new { uid = query.DatasourceUid },
                            expr = expression,
                            format = "time_series",
                            intervalMs = 15000,
                            maxDataPoints = 240
                        }
                    }
                };
                var queryUrl = CrumbSourceUtilities.Url(transport, "api/ds/query");
                var operation = $"POST /api/ds/query ({query.Name})";
                var json = await budget.TryReadJsonAsync(
                    operation,
                    async operationCancellationToken =>
                    {
                        using var request = CrumbSourceUtilities.CreateRequest(
                            HttpMethod.Post, queryUrl, transport, credentials);
                        request.Headers.TryAddWithoutValidation(
                            "X-Grafana-Org-Id", configuration.OrganizationId.ToString());
                        request.Content = JsonContent.Create(queryBody);
                        return await client.SendAsync(
                            request,
                            HttpCompletionOption.ResponseHeadersRead,
                            operationCancellationToken);
                    },
                    ct);
                if (json is null)
                {
                    if (string.Equals(query.Requirement, "required", StringComparison.Ordinal))
                    {
                        metricPartial = true;
                        metricDiagnostics.Add(
                            $"Required Grafana metric '{query.Name}' did not return a readable response.");
                    }
                    continue;
                }
                using (json)
                {
                    var parsed = ParseMetricSeries(json.RootElement);
                    var samples = parsed.Series
                        .SelectMany(item => item.Samples)
                        .Where(sample => !sample.Timestamp.HasValue
                            || sample.Timestamp.Value >= scope.Start && sample.Timestamp.Value <= scope.End)
                        .ToList();
                    var timestampedSamples = samples.Where(sample => sample.Timestamp.HasValue).ToList();
                    IReadOnlyList<MetricSample> primarySamples;
                    IReadOnlyList<MetricSample> baselineSamples;
                    string comparisonPeriod;
                    if (timestampedSamples.Count > 0)
                    {
                        var caseSamples = timestampedSamples
                            .Where(sample => sample.Timestamp!.Value >= context.OpenedAt)
                            .ToList();
                        if (caseSamples.Count > 0)
                        {
                            primarySamples = caseSamples;
                            baselineSamples = timestampedSamples
                                .Where(sample => sample.Timestamp!.Value < context.OpenedAt)
                                .ToList();
                            comparisonPeriod = "case";
                        }
                        else
                        {
                            primarySamples = timestampedSamples;
                            baselineSamples = [];
                            comparisonPeriod = "pre-case";
                        }
                    }
                    else
                    {
                        primarySamples = samples;
                        baselineSamples = [];
                        comparisonPeriod = "query-window";
                    }

                    var reduction = Reduce(primarySamples, query.Reducer);
                    if (reduction is null
                        && string.Equals(query.Requirement, "required", StringComparison.Ordinal))
                    {
                        metricPartial = true;
                        metricDiagnostics.Add(
                            $"Required Grafana metric '{query.Name}' returned no numeric samples.");
                    }
                    if (reduction is not null && baselineSamples.Count > 0)
                    {
                        baselineSamples = baselineSamples
                            .Where(sample => string.Equals(
                                sample.Series.Identity,
                                reduction.Selected.Series.Identity,
                                StringComparison.Ordinal))
                            .ToList();
                    }
                    var baselineReduction = Reduce(baselineSamples, query.Reducer);
                    var warningThreshold = query.WarningThreshold;
                    var warningIsExclusive = false;
                    var seriesAmbiguous = reduction?.SeriesAmbiguous == true
                        || baselineReduction?.SeriesAmbiguous == true;
                    var reductionComplete = !parsed.SamplesTruncated
                        && !parsed.MixedTimestampSupport
                        && parsed.UnpairedTimestampSampleCount == 0
                        && !seriesAmbiguous;
                    if (parsed.SamplesTruncated)
                    {
                        metricPartial = true;
                        metricDiagnostics.Add(
                            $"Grafana query '{query.Name}' returned {parsed.NumericSampleCount} numeric samples; "
                            + $"only {parsed.StoredSampleCount} were retained, so its reducer is incomplete and temporal attribution was disabled.");
                    }
                    if (parsed.UnpairedTimestampSampleCount > 0)
                    {
                        metricPartial = true;
                        metricDiagnostics.Add(
                            $"Grafana query '{query.Name}' could not pair {parsed.UnpairedTimestampSampleCount} numeric samples "
                            + "with valid timestamps, so its reducer is incomplete and temporal attribution was disabled.");
                    }
                    if (parsed.MixedTimestampSupport)
                    {
                        metricPartial = true;
                        metricDiagnostics.Add(
                            $"Grafana query '{query.Name}' mixed {parsed.TimestampedSampleCount} timestamped and "
                            + $"{parsed.UntimestampedSampleCount} untimestamped numeric samples; its candidate reducer is incomplete "
                            + "and temporal attribution was disabled.");
                    }
                    if (seriesAmbiguous)
                    {
                        metricPartial = true;
                        metricDiagnostics.Add(
                            $"Grafana query '{query.Name}' did not have a unique logical series for the last reducer; "
                            + $"series '{reduction?.Selected.Series.Identity ?? baselineReduction?.Selected.Series.Identity}' "
                            + "was selected deterministically and temporal attribution was disabled.");
                    }
                    var severity = reduction is null
                                   || comparisonPeriod == "pre-case"
                                   || !reductionComplete
                                   || string.Equals(query.CrumbMode, "context", StringComparison.Ordinal)
                        ? "info"
                        : ThresholdState(
                            reduction.Value,
                            warningThreshold,
                            query.CriticalThreshold,
                            query.Direction,
                            warningIsExclusive);
                    var breach = reduction is null || !reductionComplete
                        ? null
                        : FindBreachWindow(
                            reduction,
                            primarySamples,
                            severity,
                            warningThreshold,
                            query.CriticalThreshold,
                            query.Direction,
                            warningIsExclusive);
                    var observedAt = reductionComplete ? reduction?.Selected.Timestamp : null;
                    var summary = Summary(query, reduction, baselineReduction, comparisonPeriod, reductionComplete);
                    var confidence = !reductionComplete
                        ? 0.5
                        : severity == "critical" ? 0.95 : severity == "warning" ? 0.9 : 0.7;
                    var objectId = $"{query.DatasourceUid}:{query.Name}";
                    var baselineWindowStart = comparisonPeriod is "case" or "pre-case"
                        ? scope.Start
                        : (DateTimeOffset?)null;
                    DateTimeOffset? baselineWindowEnd = comparisonPeriod switch
                    {
                        "case" => context.OpenedAt,
                        "pre-case" => scope.End < context.OpenedAt
                            ? scope.End
                            : context.OpenedAt,
                        _ => null
                    };
                    crumbs.Add(new Crumb(
                        CrumbSourceUtilities.Id(
                            Source,
                            "metric-snapshot",
                            configuration.OrganizationId.ToString(CultureInfo.InvariantCulture),
                            query.DatasourceUid,
                            query.Name,
                            expression),
                        Source, observedAt ?? scope.End, null, "metric", severity, summary,
                        CrumbSourceUtilities.Truncate(json.RootElement.ToString(), 1200), queryUrl, confidence,
                        CrumbSourceUtilities.Provenance("POST /api/ds/query", new
                        {
                            name = query.Name,
                            metricId = string.IsNullOrWhiteSpace(query.MetricId) ? null : query.MetricId,
                            role = string.IsNullOrWhiteSpace(query.Role) ? null : query.Role,
                            crumbMode = query.CrumbMode,
                            requirement = query.Requirement,
                            datasourceUid = query.DatasourceUid,
                            reducer = query.Reducer,
                            reducedValue = reduction?.Value,
                            observedAt,
                            breachStartedAt = breach?.StartedAt,
                            breachEndedAt = breach?.EndedAt,
                            warningThreshold,
                            criticalThreshold = query.CriticalThreshold,
                            direction = query.Direction,
                            unit = query.Unit,
                            sampleCount = primarySamples.Count,
                            timestampSupported = observedAt.HasValue,
                            reductionComplete,
                            samplesTruncated = parsed.SamplesTruncated,
                            parsedSampleCount = parsed.StoredSampleCount,
                            numericSampleCount = parsed.NumericSampleCount,
                            truncatedSampleCount = parsed.TruncatedSampleCount,
                            timestampedSampleCount = parsed.TimestampedSampleCount,
                            untimestampedSampleCount = parsed.UntimestampedSampleCount,
                            unpairedTimestampSampleCount = parsed.UnpairedTimestampSampleCount,
                            mixedTimestampSupport = parsed.MixedTimestampSupport,
                            seriesAmbiguous,
                            lastCandidateSeriesCount = query.Reducer == "last"
                                ? (int?)reduction?.CandidateSeriesCount
                                : null,
                            observedSeries = reduction?.Selected.Series.Identity,
                            baselineValue = baselineReduction?.Value,
                            baselineObservedAt = reductionComplete ? baselineReduction?.Selected.Timestamp : null,
                            baselineSampleCount = baselineSamples.Count,
                            baselineSeries = baselineReduction?.Selected.Series.Identity,
                            baselineWindowStart,
                            baselineWindowEnd,
                            caseWindowStart = comparisonPeriod == "case"
                                ? (DateTimeOffset?)context.OpenedAt
                                : null,
                            caseWindowEnd = comparisonPeriod == "case"
                                ? (DateTimeOffset?)scope.End
                                : null,
                            comparisonPeriod,
                            exactWindowStart = scope.Start,
                            exactWindowEnd = scope.End
                        }),
                        ObjectType: "metric-query",
                        ObjectId: objectId));
                    if (observedAt.HasValue && severity is "warning" or "critical")
                    {
                        trail.Add(new TrailCandidate(
                            observedAt.Value,
                            Source,
                            "metric",
                            summary,
                            severity,
                            queryUrl,
                            ObjectType: "metric-query",
                            ObjectId: objectId));
                    }
                }
            }

            var itemLimit = Math.Min(
                Math.Max(0, scope.MaxItems),
                Math.Max(0, transport.MaxItems));
            var rankedCrumbs = CrumbRankingPolicy.Rank(crumbs, context.OpenedAt);
            var orderedTrail = trail.OrderBy(item => item.OccurredAt).ToList();
            var distinctLinks = links.Distinct().ToList();
            var itemsTruncated = rankedCrumbs.Count > itemLimit
                                 || orderedTrail.Count > itemLimit
                                 || distinctLinks.Count > itemLimit;
            var diagnostic = CrumbSourceUtilities.CombineDiagnostics(
                budget.Diagnostic,
                metricDiagnostics.Count > 0 ? string.Join(' ', metricDiagnostics) : null,
                itemsTruncated ? $"Source item limit {itemLimit} truncated Crumbs, Trail entries, or links." : null);
            return new CrumbSourceResult(
                Source,
                budget.IsPartial || metricPartial || itemsTruncated ? CrumbSourceHealth.Partial : CrumbSourceHealth.Complete,
                rankedCrumbs.Take(itemLimit).ToList(),
                orderedTrail.Take(itemLimit).ToList(),
                distinctLinks.Take(itemLimit).ToList(),
                0,
                diagnostic);
        }, cancellationToken);
    }

    private static ParsedMetricResult ParseMetricSeries(JsonElement root)
    {
        var output = new List<ParsedMetricSeries>();
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Object)
        {
            return new ParsedMetricResult(output, 0, 0, 0, 0, 0);
        }

        var sampleSequence = 0;
        var storedSampleCount = 0;
        var timestampedSampleCount = 0;
        var untimestampedSampleCount = 0;
        var unpairedTimestampSampleCount = 0;
        foreach (var result in results.EnumerateObject())
        {
            if (!result.Value.TryGetProperty("frames", out var frames) || frames.ValueKind != JsonValueKind.Array) continue;
            foreach (var frame in frames.EnumerateArray())
            {
                if (frame.ValueKind != JsonValueKind.Object
                    || !frame.TryGetProperty("schema", out var schema)
                    || schema.ValueKind != JsonValueKind.Object
                    || !schema.TryGetProperty("fields", out var fields)
                    || fields.ValueKind != JsonValueKind.Array
                    || !frame.TryGetProperty("data", out var data)
                    || data.ValueKind != JsonValueKind.Object
                    || !data.TryGetProperty("values", out var values)
                    || values.ValueKind != JsonValueKind.Array) continue;
                var fieldArray = fields.EnumerateArray().ToArray();
                var valueArray = values.EnumerateArray().ToArray();
                var timeIndex = Array.FindIndex(
                    fieldArray,
                    field => string.Equals(FieldType(field), "time", StringComparison.OrdinalIgnoreCase));
                var timeValues = timeIndex >= 0
                                 && timeIndex < valueArray.Length
                                 && valueArray[timeIndex].ValueKind == JsonValueKind.Array
                    ? valueArray[timeIndex].EnumerateArray().ToArray()
                    : [];
                for (var fieldIndex = 0;
                     fieldIndex < Math.Min(fieldArray.Length, valueArray.Length);
                     fieldIndex++)
                {
                    if (!string.Equals(FieldType(fieldArray[fieldIndex]), "number", StringComparison.OrdinalIgnoreCase)
                        || valueArray[fieldIndex].ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    var metricSeries = new ParsedMetricSeries(
                        SeriesIdentity(result.Name, schema, fieldArray[fieldIndex], fieldIndex));
                    var numericValues = valueArray[fieldIndex].EnumerateArray().ToArray();
                    for (var sampleIndex = 0; sampleIndex < numericValues.Length; sampleIndex++)
                    {
                        if (!numericValues[sampleIndex].TryGetDouble(out var number)
                            || !double.IsFinite(number))
                        {
                            continue;
                        }

                        DateTimeOffset? timestamp = null;
                        if (sampleIndex < timeValues.Length
                            && TryTimestamp(timeValues[sampleIndex], out var parsedTimestamp))
                        {
                            timestamp = parsedTimestamp;
                        }
                        if (timestamp.HasValue)
                        {
                            timestampedSampleCount++;
                        }
                        else
                        {
                            untimestampedSampleCount++;
                            if (timeIndex >= 0) unpairedTimestampSampleCount++;
                        }

                        var sequence = sampleSequence++;
                        if (storedSampleCount >= MaximumMetricSamples) continue;
                        metricSeries.Samples.Add(new MetricSample(
                            number,
                            timestamp,
                            sequence,
                            metricSeries));
                        storedSampleCount++;
                    }
                    if (metricSeries.Samples.Count > 0) output.Add(metricSeries);
                }
            }
        }
        return new ParsedMetricResult(
            output,
            sampleSequence,
            storedSampleCount,
            timestampedSampleCount,
            untimestampedSampleCount,
            unpairedTimestampSampleCount);
    }

    private static string FieldType(JsonElement field) => field.ValueKind == JsonValueKind.Object
        ? CrumbSourceUtilities.Text(field, "type", "")
        : "";

    private static string SeriesIdentity(
        string resultName,
        JsonElement schema,
        JsonElement field,
        int fieldIndex)
    {
        var frameName = CrumbSourceUtilities.Text(schema, "name", "");
        var fieldName = CrumbSourceUtilities.Text(
            field,
            "name",
            $"field-{fieldIndex.ToString(CultureInfo.InvariantCulture)}");
        var labels = field.ValueKind == JsonValueKind.Object
                     && field.TryGetProperty("labels", out var labelElement)
                     && labelElement.ValueKind == JsonValueKind.Object
            ? string.Join(",", labelElement.EnumerateObject()
                .OrderBy(label => label.Name, StringComparer.Ordinal)
                .Select(label => $"{label.Name}={label.Value.GetRawText()}"))
            : "";
        return $"result={resultName}|frame={frameName}|field={fieldName}|labels={labels}";
    }

    private static MetricReduction? Reduce(
        IReadOnlyList<MetricSample> samples,
        string reducer)
    {
        if (samples.Count == 0) return null;
        if (reducer == "last") return ReduceLast(samples);
        var selected = reducer switch
        {
            "maximum" => samples
                .OrderByDescending(sample => sample.Value)
                .ThenByDescending(sample => sample.Timestamp)
                .ThenBy(sample => sample.Series.Identity, StringComparer.Ordinal)
                .ThenBy(sample => sample.Sequence)
                .First(),
            "minimum" => samples
                .OrderBy(sample => sample.Value)
                .ThenByDescending(sample => sample.Timestamp)
                .ThenBy(sample => sample.Series.Identity, StringComparer.Ordinal)
                .ThenBy(sample => sample.Sequence)
                .First(),
            _ => null
        };
        return selected is null ? null : new MetricReduction(selected.Value, selected, false, 1);
    }

    private static MetricReduction ReduceLast(IReadOnlyList<MetricSample> samples)
    {
        var timestamped = samples.Any(sample => sample.Timestamp.HasValue);
        var logicalSeries = samples
            .GroupBy(sample => sample.Series.Identity, StringComparer.Ordinal)
            .Select(group =>
            {
                var candidates = timestamped
                    ? group
                        .Where(sample => sample.Timestamp.HasValue)
                        .GroupBy(sample => sample.Timestamp)
                        .OrderByDescending(samplesAtTime => samplesAtTime.Key)
                        .FirstOrDefault()?.ToList() ?? []
                    : group
                        .GroupBy(sample => sample.Series)
                        .Select(physicalSeries => physicalSeries
                            .OrderByDescending(sample => sample.Sequence)
                            .First())
                        .ToList();
                var selected = candidates
                    .OrderByDescending(sample => sample.Value)
                    .ThenBy(sample => sample.Series.Identity, StringComparer.Ordinal)
                    .ThenBy(sample => sample.Sequence)
                    .First();
                return new
                {
                    Selected = selected,
                    AmbiguousWithinSeries = candidates
                        .Select(sample => sample.Value)
                        .Distinct()
                        .Skip(1)
                        .Any()
                };
            })
            .ToList();
        var latestTimestamp = timestamped
            ? logicalSeries.Max(item => item.Selected.Timestamp)
            : null;
        var candidates = timestamped
            ? logicalSeries.Where(item => item.Selected.Timestamp == latestTimestamp).ToList()
            : logicalSeries;
        var selected = candidates
            .OrderBy(item => item.Selected.Series.Identity, StringComparer.Ordinal)
            .ThenByDescending(item => item.Selected.Value)
            .ThenBy(item => item.Selected.Sequence)
            .First();
        var ambiguous = candidates.Count > 1 || selected.AmbiguousWithinSeries;
        return new MetricReduction(
            selected.Selected.Value,
            selected.Selected,
            ambiguous,
            candidates.Count);
    }

    private static string ThresholdState(
        double value,
        double? warningThreshold,
        double? criticalThreshold,
        string direction,
        bool warningIsExclusive)
    {
        if (direction == "above")
        {
            if (criticalThreshold.HasValue && value >= criticalThreshold.Value) return "critical";
            if (warningThreshold.HasValue
                && (warningIsExclusive ? value > warningThreshold.Value : value >= warningThreshold.Value))
            {
                return "warning";
            }
        }
        else if (direction == "below")
        {
            if (criticalThreshold.HasValue && value <= criticalThreshold.Value) return "critical";
            if (warningThreshold.HasValue
                && (warningIsExclusive ? value < warningThreshold.Value : value <= warningThreshold.Value))
            {
                return "warning";
            }
        }
        return "info";
    }

    private static BreachWindow? FindBreachWindow(
        MetricReduction reduction,
        IReadOnlyList<MetricSample> periodSamples,
        string severity,
        double? warningThreshold,
        double? criticalThreshold,
        string direction,
        bool warningIsExclusive)
    {
        var threshold = severity is "warning" or "critical"
            ? warningThreshold ?? criticalThreshold
            : null;
        if (!threshold.HasValue || !reduction.Selected.Timestamp.HasValue) return null;

        var ordered = periodSamples
            .Where(sample => string.Equals(
                sample.Series.Identity,
                reduction.Selected.Series.Identity,
                StringComparison.Ordinal))
            .OrderBy(sample => sample.Timestamp)
            .ThenBy(sample => sample.Sequence)
            .ToList();
        var selectedIndex = ordered.FindIndex(sample => sample.Sequence == reduction.Selected.Sequence);
        var thresholdIsExclusive = warningThreshold.HasValue && warningIsExclusive;
        if (selectedIndex < 0
            || !Breaches(ordered[selectedIndex].Value, threshold.Value, direction, thresholdIsExclusive))
        {
            return null;
        }

        var first = selectedIndex;
        while (first > 0
               && Breaches(ordered[first - 1].Value, threshold.Value, direction, thresholdIsExclusive))
        {
            first--;
        }
        var last = selectedIndex;
        while (last + 1 < ordered.Count
               && Breaches(ordered[last + 1].Value, threshold.Value, direction, thresholdIsExclusive))
        {
            last++;
        }
        var recoveredAt = last + 1 < ordered.Count ? ordered[last + 1].Timestamp : null;
        return ordered[first].Timestamp.HasValue
            ? new BreachWindow(ordered[first].Timestamp!.Value, recoveredAt)
            : null;
    }

    private static bool Breaches(
        double value,
        double threshold,
        string direction,
        bool thresholdIsExclusive) => direction switch
        {
            "above" => thresholdIsExclusive ? value > threshold : value >= threshold,
            "below" => thresholdIsExclusive ? value < threshold : value <= threshold,
            _ => false
        };

    private static string Summary(
        GrafanaQuery query,
        MetricReduction? reduction,
        MetricReduction? baseline,
        string comparisonPeriod,
        bool reductionComplete)
    {
        if (reduction is null) return $"{query.Name}: query returned no numeric samples";
        var value = FormatMetricValue(reduction.Value, query.Unit);
        if (!reductionComplete)
        {
            return $"{query.Name}: partial {query.Reducer} candidate {value}; incomplete samples prevent temporal attribution";
        }
        if (baseline is not null && comparisonPeriod == "case")
        {
            var baselineValue = FormatMetricValue(baseline.Value, query.Unit);
            if (reduction.Value > baseline.Value)
            {
                return $"{query.Name} rose from a {baselineValue} pre-Case baseline to {value}";
            }
            if (reduction.Value < baseline.Value)
            {
                return $"{query.Name} fell from a {baselineValue} pre-Case baseline to {value}";
            }
            return $"{query.Name} remained at {value}, matching the pre-Case baseline";
        }
        return $"{query.Name}: {query.Reducer} observed value {value}";
    }

    private static string FormatMetricValue(double value, string unit)
    {
        var normalizedUnit = unit.Trim();
        if (normalizedUnit is "s" or "second" or "seconds")
        {
            if (Math.Abs(value) < 1)
            {
                return $"{(value * 1000).ToString("0.###", CultureInfo.InvariantCulture)} ms";
            }
            var seconds = value.ToString("0.###", CultureInfo.InvariantCulture);
            return $"{seconds} {(Math.Abs(value) == 1 ? "second" : "seconds")}";
        }
        if (normalizedUnit is "ms" or "millisecond" or "milliseconds")
        {
            return $"{value.ToString("0.###", CultureInfo.InvariantCulture)} ms";
        }

        var formatted = value.ToString("0.###", CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(normalizedUnit) ? formatted : $"{formatted} {normalizedUnit}";
    }

    private static bool TryTimestamp(JsonElement value, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (value.ValueKind == JsonValueKind.String)
        {
            return DateTimeOffset.TryParse(
                value.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out timestamp);
        }
        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var numeric)
            || !double.IsFinite(numeric))
        {
            return false;
        }

        var absolute = Math.Abs(numeric);
        var milliseconds = absolute switch
        {
            >= 100_000_000_000_000_000d => numeric / 1_000_000d,
            >= 100_000_000_000_000d => numeric / 1_000d,
            >= 100_000_000_000d => numeric,
            _ => numeric * 1_000d
        };
        try
        {
            timestamp = DateTimeOffset.FromUnixTimeMilliseconds(checked((long)Math.Round(milliseconds)));
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private sealed record ParsedMetricResult(
        IReadOnlyList<ParsedMetricSeries> Series,
        int NumericSampleCount,
        int StoredSampleCount,
        int TimestampedSampleCount,
        int UntimestampedSampleCount,
        int UnpairedTimestampSampleCount)
    {
        public bool SamplesTruncated => NumericSampleCount > StoredSampleCount;
        public int TruncatedSampleCount => Math.Max(0, NumericSampleCount - StoredSampleCount);
        public bool MixedTimestampSupport => TimestampedSampleCount > 0 && UntimestampedSampleCount > 0;
    }

    private sealed class ParsedMetricSeries(string identity)
    {
        public string Identity { get; } = identity;
        public List<MetricSample> Samples { get; } = [];
    }

    private sealed record MetricSample(
        double Value,
        DateTimeOffset? Timestamp,
        int Sequence,
        ParsedMetricSeries Series);

    private sealed record MetricReduction(
        double Value,
        MetricSample Selected,
        bool SeriesAmbiguous,
        int CandidateSeriesCount);
    private sealed record BreachWindow(DateTimeOffset StartedAt, DateTimeOffset? EndedAt);
}
