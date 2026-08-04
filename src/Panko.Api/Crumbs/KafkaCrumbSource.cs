using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Panko.Api.Domain;
using Panko.Api.Cases;
using Panko.Api.Infrastructure;
using Panko.Api.Options;
using Panko.Api.Recipes;
using Panko.Kafka;

namespace Panko.Api.Crumbs;

/// <summary>
/// Collects recipe-scoped Kafka metrics through Grafana's read-only data-source query endpoint.
/// Metric targets are batched independently of Kafka resources so collection never fans out by
/// topic, consumer group, broker, or partition.
/// </summary>
public sealed class KafkaCrumbSource(
    IHttpClientFactory httpClientFactory,
    KafkaMetricPlanStore metricPlans,
    CrumbSourceConfiguration crumbSources,
    ICredentialProvider credentials) : ICrumbSourceAdapter
{
    private const int MaximumTargetsPerBatch = 8;
    private const int MaximumSamplesPerMetric = 10_000;
    private const int MaximumLabelsPerSeries = 32;

    public string Source => CrumbSourceRegistry.Kafka;
    public bool SupportsWindowExpansion => true;

    public Task<CrumbSourceResult> CollectAsync(
        CaseContext context,
        CrumbScope scope,
        CancellationToken cancellationToken)
    {
        var configuration = context.Recipe.Kafka;
        if (configuration is null)
        {
            return Task.FromResult(CrumbSourceResult.Excluded(Source));
        }

        var transport = crumbSources.For(Source);
        if (!string.Equals(transport.Mode, "api", StringComparison.Ordinal))
        {
            return Task.FromResult(CrumbSourceResult.Unavailable(
                Source,
                0,
                "The Kafka Crumb source supports only Grafana API transport; MCP mode is not supported."));
        }

        return CrumbSourceUtilities.ExecuteAsync(
            Source,
            transport.TimeoutSeconds,
            ct => CollectNativeAsync(context, scope, configuration, transport, ct),
            cancellationToken);
    }

    private async Task<CrumbSourceResult> CollectNativeAsync(
        CaseContext context,
        CrumbScope scope,
        KafkaRecipeScope configuration,
        ConnectorTransport transport,
        CancellationToken cancellationToken)
    {
        var plan = metricPlans.Resolve(configuration);
        var batches = plan.Metrics
            .Chunk(MaximumTargetsPerBatch)
            .Select(batch => batch
                .Select((metric, index) => new BatchMetric(
                    metric,
                    ((char)('A' + index)).ToString(CultureInfo.InvariantCulture)))
                .ToArray())
            .ToArray();
        var budget = new CrumbSourceResponseBudget(scope.MaxBytes, transport.MaxBytes, batches.Length);
        var series = new Dictionary<SeriesKey, MetricSeries>();
        var diagnostics = new List<string>();
        var partial = false;
        var successfulBatches = 0;
        var queryUrl = CrumbSourceUtilities.Url(transport, "api/ds/query");
        var dashboardUrl = DashboardUrl(transport, context.Recipe.Id, scope);
        var client = httpClientFactory.CreateClient();

        for (var batchIndex = 0; batchIndex < batches.Length; batchIndex++)
        {
            var batch = batches[batchIndex];
            var operation = $"POST /api/ds/query (Kafka batch {batchIndex + 1}/{batches.Length})";
            try
            {
                var targets = batch.Select(item => new
                {
                    refId = item.RefId,
                    datasource = new { uid = item.Metric.DatasourceUid },
                    expr = item.Metric.RuntimePromQl,
                    format = "time_series",
                    intervalMs = QueryIntervalMilliseconds(scope),
                    maxDataPoints = 240
                }).ToArray();
                var body = new
                {
                    from = scope.Start.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
                    to = scope.End.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
                    queries = targets
                };

                var json = await budget.TryReadJsonAsync(
                    operation,
                    async operationCancellationToken =>
                    {
                        using var request = CrumbSourceUtilities.CreateRequest(
                            HttpMethod.Post,
                            queryUrl,
                            transport,
                            credentials);
                        request.Content = JsonContent.Create(body);
                        return await client.SendAsync(
                            request,
                            HttpCompletionOption.ResponseHeadersRead,
                            operationCancellationToken);
                    },
                    cancellationToken);
                if (json is null)
                {
                    partial = true;
                    continue;
                }
                using (json)
                {
                    successfulBatches++;
                    if (!ParseBatch(
                        json.RootElement,
                        batch,
                        plan,
                        series,
                        diagnostics))
                    {
                        partial = true;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                partial = true;
                AddDiagnostic(
                    diagnostics,
                    $"Kafka metric batch {batchIndex + 1} failed with {exception.GetType().Name}: "
                    + CrumbSourceUtilities.Truncate(exception.Message, 180));
            }
        }

        var batchDiagnostic = CrumbSourceUtilities.CombineDiagnostics(
            budget.Diagnostic,
            diagnostics.Count == 0 ? null : string.Join("; ", diagnostics));
        if (successfulBatches == 0 && batches.Length > 0)
        {
            return CrumbSourceResult.Unavailable(
                Source,
                0,
                batchDiagnostic ?? "Every Kafka Grafana query batch failed.");
        }

        var crumbs = new List<Crumb>();
        var trail = new List<TrailCandidate>();
        foreach (var item in series.Values
                     .OrderBy(item => item.Metric.Id, StringComparer.Ordinal)
                     .ThenBy(item => item.LabelIdentity, StringComparer.Ordinal)
                     .ThenBy(item => item.FieldName, StringComparer.Ordinal))
        {
            if (!TryReduce(
                    item,
                    scope.End,
                    out var reducedValue,
                    out var observedAt,
                    out var timestampSupported))
            {
                partial = true;
                AddDiagnostic(diagnostics, $"Kafka metric '{item.Metric.Id}' returned no finite reducible samples.");
                continue;
            }

            var severity = item.Metric.CrumbMode == "context"
                ? "info"
                : item.Metric.Thresholds.State(reducedValue);
            if (item.Metric.CrumbMode == "anomaly" && severity == "info")
            {
                continue;
            }

            var objectType = ObjectType(item.Metric.ResourceScope);
            var objectId = ObjectId(item.Metric, plan, item.Labels);
            var summary = Summary(item.Metric, reducedValue, severity, objectId);
            var crumb = new Crumb(
                CrumbSourceUtilities.Id(
                    Source,
                    "metric",
                    context.Recipe.Id,
                    plan.MetricPackId,
                    item.Metric.Id,
                    item.FieldName,
                    item.LabelIdentity),
                Source,
                observedAt,
                null,
                item.Metric.Category,
                severity,
                summary,
                null,
                dashboardUrl,
                severity == "critical" ? 0.95 : severity == "warning" ? 0.9 : 0.75,
                CrumbSourceUtilities.Provenance("POST /api/ds/query", new
                {
                    metricPackId = plan.MetricPackId,
                    metricId = item.Metric.Id,
                    metricTitle = item.Metric.Title,
                    datasourceUid = item.Metric.DatasourceUid,
                    resourceScope = item.Metric.ResourceScope,
                    unit = item.Metric.Unit,
                    reducer = item.Metric.TimeReducer,
                    crumbMode = item.Metric.CrumbMode,
                    requirement = item.Metric.Requirement,
                    cluster = plan.Cluster,
                    returnedLabels = item.Labels,
                    reducedValue,
                    observedAt = timestampSupported ? observedAt : (DateTimeOffset?)null,
                    thresholdState = severity,
                    warningThreshold = item.Metric.Thresholds.Warning,
                    criticalThreshold = item.Metric.Thresholds.Critical,
                    direction = item.Metric.Thresholds.Direction,
                    timestampSupported,
                    reductionComplete = true,
                    exactWindowStart = scope.Start,
                    exactWindowEnd = scope.End
                }),
                ObjectType: objectType,
                ObjectId: objectId);
            crumbs.Add(crumb);
            if (timestampSupported && severity is ("warning" or "critical"))
            {
                trail.Add(new TrailCandidate(
                    observedAt,
                    Source,
                    item.Metric.Category,
                    summary,
                    severity,
                    dashboardUrl,
                    ObjectType: objectType,
                    ObjectId: objectId));
            }
        }

        // A required target that returned no valid series is an incomplete source result. Optional
        // targets may legitimately be absent when an exporter does not expose that metric family.
        var returnedMetricIds = series.Values
            .Select(item => item.Metric.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var metric in plan.Metrics
                     .Where(metric => metric.IsRequired && !returnedMetricIds.Contains(metric.Id)))
        {
            partial = true;
            AddDiagnostic(diagnostics, $"Required Kafka metric '{metric.Id}' returned no in-scope numeric series.");
        }

        var itemLimit = Math.Min(
            Math.Max(0, scope.MaxItems),
            Math.Max(0, transport.MaxItems));
        var rankedCrumbs = CrumbRankingPolicy.Rank(crumbs, context.OpenedAt);
        var orderedTrail = trail
            .OrderBy(item => item.OccurredAt)
            .ThenBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.ObjectId, StringComparer.Ordinal)
            .ToList();
        var links = new[] { new SourceLink("Kafka responder dashboard", dashboardUrl) };
        var itemsTruncated = rankedCrumbs.Count > itemLimit
            || orderedTrail.Count > itemLimit
            || links.Length > itemLimit;
        partial |= budget.IsPartial || itemsTruncated;
        var diagnostic = CrumbSourceUtilities.CombineDiagnostics(
            budget.Diagnostic,
            diagnostics.Count == 0 ? null : string.Join("; ", diagnostics),
            itemsTruncated
                ? $"Source item limit {itemLimit} truncated Kafka Crumbs, Trail entries, or links."
                : null);
        return new CrumbSourceResult(
            Source,
            partial ? CrumbSourceHealth.Partial : CrumbSourceHealth.Complete,
            rankedCrumbs.Take(itemLimit).ToArray(),
            orderedTrail.Take(itemLimit).ToArray(),
            links.Take(itemLimit).ToArray(),
            0,
            diagnostic);
    }

    private static bool ParseBatch(
        JsonElement root,
        IReadOnlyList<BatchMetric> batch,
        KafkaMetricPlan plan,
        IDictionary<SeriesKey, MetricSeries> output,
        ICollection<string> diagnostics)
    {
        if (!root.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Object)
        {
            AddDiagnostic(diagnostics, "Kafka Grafana response did not contain a results object.");
            return false;
        }

        var complete = true;
        foreach (var planned in batch)
        {
            if (!results.TryGetProperty(planned.RefId, out var result)
                || result.ValueKind != JsonValueKind.Object)
            {
                complete = false;
                AddDiagnostic(diagnostics, $"Kafka metric '{planned.Metric.Id}' had no Grafana result.");
                continue;
            }
            if (ResultError(result) is { } resultError)
            {
                complete = false;
                AddDiagnostic(
                    diagnostics,
                    $"Kafka metric '{planned.Metric.Id}' failed in Grafana: "
                    + CrumbSourceUtilities.Truncate(resultError, 160));
                continue;
            }
            if (!result.TryGetProperty("frames", out var frames)
                || frames.ValueKind != JsonValueKind.Array)
            {
                if (planned.Metric.IsRequired)
                {
                    complete = false;
                    AddDiagnostic(diagnostics, $"Required Kafka metric '{planned.Metric.Id}' returned no frames.");
                }
                continue;
            }

            var retainedSamples = 0;
            foreach (var frame in frames.EnumerateArray())
            {
                if (!TryFrameColumns(frame, out var fields, out var values))
                {
                    complete = false;
                    AddDiagnostic(diagnostics, $"Kafka metric '{planned.Metric.Id}' returned a malformed frame.");
                    continue;
                }

                var timeIndex = Array.FindIndex(fields, field => FieldType(field) == "time");
                for (var fieldIndex = 0; fieldIndex < fields.Length; fieldIndex++)
                {
                    if (FieldType(fields[fieldIndex]) != "number"
                        || fieldIndex >= values.Length
                        || values[fieldIndex].ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    var allLabels = ReadLabels(fields[fieldIndex]);
                    if (!LabelsAreInScope(
                            allLabels,
                            planned.Metric,
                            plan,
                            out var scopeFailure))
                    {
                        complete = false;
                        AddDiagnostic(
                            diagnostics,
                            $"Kafka metric '{planned.Metric.Id}' rejected an out-of-scope series: {scopeFailure}");
                        continue;
                    }

                    var labels = BoundLabels(allLabels, planned.Metric);
                    if (labels.Count != allLabels.Count)
                    {
                        complete = false;
                        AddDiagnostic(
                            diagnostics,
                            $"Kafka metric '{planned.Metric.Id}' returned more than {MaximumLabelsPerSeries} labels.");
                    }
                    var fieldName = FieldName(fields[fieldIndex]);
                    var labelIdentity = LabelIdentity(labels);
                    var key = new SeriesKey(planned.Metric.Id, fieldName, labelIdentity);
                    if (!output.TryGetValue(key, out var metricSeries))
                    {
                        metricSeries = new MetricSeries(
                            planned.Metric,
                            fieldName,
                            labels,
                            labelIdentity);
                        output[key] = metricSeries;
                    }

                    var numericValues = values[fieldIndex].EnumerateArray().ToArray();
                    var timeValues = timeIndex >= 0
                                     && timeIndex < values.Length
                                     && values[timeIndex].ValueKind == JsonValueKind.Array
                        ? values[timeIndex].EnumerateArray().ToArray()
                        : [];
                    for (var sampleIndex = 0; sampleIndex < numericValues.Length; sampleIndex++)
                    {
                        if (retainedSamples >= MaximumSamplesPerMetric)
                        {
                            complete = false;
                            AddDiagnostic(
                                diagnostics,
                                $"Kafka metric '{planned.Metric.Id}' exceeded its {MaximumSamplesPerMetric}-sample parse limit.");
                            break;
                        }
                        if (!numericValues[sampleIndex].TryGetDouble(out var value)
                            || !double.IsFinite(value))
                        {
                            continue;
                        }

                        DateTimeOffset? timestamp = null;
                        if (sampleIndex < timeValues.Length
                            && TryTimestamp(timeValues[sampleIndex], out var parsedTimestamp))
                        {
                            timestamp = parsedTimestamp;
                        }
                        metricSeries.Samples.Add(new MetricSample(
                            value,
                            timestamp,
                            metricSeries.Samples.Count));
                        retainedSamples++;
                    }
                }
            }
        }
        return complete;
    }

    private static bool TryFrameColumns(
        JsonElement frame,
        out JsonElement[] fields,
        out JsonElement[] values)
    {
        fields = [];
        values = [];
        if (!frame.TryGetProperty("schema", out var schema)
            || !schema.TryGetProperty("fields", out var fieldElement)
            || fieldElement.ValueKind != JsonValueKind.Array
            || !frame.TryGetProperty("data", out var data)
            || !data.TryGetProperty("values", out var valueElement)
            || valueElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }
        fields = fieldElement.EnumerateArray().ToArray();
        values = valueElement.EnumerateArray().ToArray();
        return true;
    }

    private static string? ResultError(JsonElement result)
    {
        if (result.TryGetProperty("error", out var error)
            && error.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(error.GetString()))
        {
            return error.GetString();
        }
        if (result.TryGetProperty("status", out var status)
            && status.TryGetInt32(out var statusCode)
            && statusCode >= 400)
        {
            return $"HTTP status {statusCode}";
        }
        return null;
    }

    private static IReadOnlyDictionary<string, string> ReadLabels(JsonElement field)
    {
        var labels = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (!field.TryGetProperty("labels", out var labelElement)
            || labelElement.ValueKind != JsonValueKind.Object)
        {
            return labels;
        }
        foreach (var label in labelElement.EnumerateObject())
        {
            labels[label.Name] = label.Value.ValueKind == JsonValueKind.String
                ? label.Value.GetString() ?? ""
                : label.Value.ToString();
        }
        return labels;
    }

    private static IReadOnlyDictionary<string, string> BoundLabels(
        IReadOnlyDictionary<string, string> labels,
        KafkaPlannedMetric metric)
    {
        var bounded = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var label in labels
                     .OrderBy(label => label.Key, StringComparer.Ordinal)
                     .Take(MaximumLabelsPerSeries))
        {
            var valueLimit = IsScopeLabelName(label.Key, metric) ? 256 : 160;
            bounded.TryAdd(
                CrumbSourceUtilities.Truncate(label.Key, 80),
                CrumbSourceUtilities.Truncate(label.Value, valueLimit));
        }
        return bounded;
    }

    private static bool IsScopeLabelName(string name, KafkaPlannedMetric metric) =>
        metric.ExpectedScopeLabels.Contains(name);

    private static bool LabelsAreInScope(
        IReadOnlyDictionary<string, string> labels,
        KafkaPlannedMetric metric,
        KafkaMetricPlan plan,
        out string failure)
    {
        foreach (var label in labels)
        {
            if (metric.ExpectedScopeLabels.Cluster.Contains(label.Key)
                && !string.Equals(label.Value, plan.Cluster, StringComparison.Ordinal))
            {
                failure = "cluster label is not allowlisted";
                return false;
            }
            if (metric.ExpectedScopeLabels.Topic.Contains(label.Key)
                && !plan.Topics.Contains(label.Value, StringComparer.Ordinal))
            {
                failure = "topic label is not allowlisted";
                return false;
            }
            if (metric.ExpectedScopeLabels.ConsumerGroup.Contains(label.Key)
                && !plan.ConsumerGroups.Contains(label.Value, StringComparer.Ordinal))
            {
                failure = "consumer-group label is not allowlisted";
                return false;
            }
        }
        failure = "";
        return true;
    }

    private static bool TryReduce(
        MetricSeries series,
        DateTimeOffset fallbackTimestamp,
        out double value,
        out DateTimeOffset observedAt,
        out bool timestampSupported)
    {
        value = 0;
        observedAt = fallbackTimestamp;
        timestampSupported = false;
        if (series.Samples.Count == 0) return false;

        MetricSample? selected = null;
        switch (series.Metric.TimeReducer)
        {
            case "maximum":
                selected = series.Samples
                    .OrderByDescending(sample => sample.Value)
                    .ThenByDescending(sample => sample.Timestamp)
                    .ThenBy(sample => sample.Sequence)
                    .First();
                value = selected.Value.Value;
                break;
            case "minimum":
                selected = series.Samples
                    .OrderBy(sample => sample.Value)
                    .ThenByDescending(sample => sample.Timestamp)
                    .ThenBy(sample => sample.Sequence)
                    .First();
                value = selected.Value.Value;
                break;
            case "last":
                selected = series.Samples.Any(sample => sample.Timestamp.HasValue)
                    ? series.Samples
                        .OrderByDescending(sample => sample.Timestamp)
                        .ThenByDescending(sample => sample.Sequence)
                        .First()
                    : series.Samples.OrderByDescending(sample => sample.Sequence).First();
                value = selected.Value.Value;
                break;
            case "average":
                value = series.Samples.Average(sample => sample.Value);
                break;
            case "sum":
                value = series.Samples.Sum(sample => sample.Value);
                break;
            default:
                return false;
        }

        if (!double.IsFinite(value)) return false;
        // A maximum, minimum, or last reducer identifies one real sample. Average and
        // sum values span a window, so assigning any sample timestamp would overstate
        // temporal precision and make an unsound causal marker.
        var actualTimestamp = selected?.Timestamp;
        if (actualTimestamp.HasValue)
        {
            observedAt = actualTimestamp.Value;
            timestampSupported = true;
        }
        return true;
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
        if (!value.TryGetDouble(out var numeric) || !double.IsFinite(numeric))
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

    private static int QueryIntervalMilliseconds(CrumbScope scope)
    {
        var interval = (scope.End - scope.Start).TotalMilliseconds / 240d;
        return (int)Math.Clamp(interval, 1_000d, int.MaxValue);
    }

    private static string DashboardUrl(
        ConnectorTransport transport,
        string recipeId,
        CrumbScope scope)
    {
        var uid = KafkaDashboardIdentity.Uid(recipeId);
        return CrumbSourceUtilities.Url(
            transport,
            $"d/{Uri.EscapeDataString(uid)}"
            + $"?from={scope.Start.ToUnixTimeMilliseconds()}"
            + $"&to={scope.End.ToUnixTimeMilliseconds()}");
    }

    private static string FieldType(JsonElement field) =>
        CrumbSourceUtilities.Text(field, "type", "").ToLowerInvariant();

    private static string FieldName(JsonElement field) =>
        CrumbSourceUtilities.Truncate(CrumbSourceUtilities.Text(field, "name", "value"), 120);

    private static string LabelIdentity(IReadOnlyDictionary<string, string> labels) =>
        string.Join("\u001f", labels
            .OrderBy(label => label.Key, StringComparer.Ordinal)
            .Select(label => $"{label.Key}={label.Value}"));

    private static string ObjectType(string resourceScope) => resourceScope switch
    {
        "topic" => "kafka-topic",
        "consumer-group" => "kafka-consumer-group",
        _ => "kafka-cluster"
    };

    private static string ObjectId(
        KafkaPlannedMetric metric,
        KafkaMetricPlan plan,
        IReadOnlyDictionary<string, string> labels)
    {
        var cluster = LabelValue(labels, metric.ExpectedScopeLabels.Cluster) ?? plan.Cluster;
        if (metric.ResourceScope == "topic")
        {
            var topic = LabelValue(labels, metric.ExpectedScopeLabels.Topic)
                ?? (plan.Topics.Length == 1 ? plan.Topics[0] : "allowlisted-topics");
            return $"{cluster}/{topic}";
        }
        if (metric.ResourceScope == "consumer-group")
        {
            var topic = LabelValue(labels, metric.ExpectedScopeLabels.Topic)
                ?? (plan.Topics.Length == 1 ? plan.Topics[0] : "allowlisted-topics");
            var group = LabelValue(labels, metric.ExpectedScopeLabels.ConsumerGroup)
                ?? (plan.ConsumerGroups.Length == 1
                    ? plan.ConsumerGroups[0]
                    : "allowlisted-consumer-groups");
            return $"{cluster}/{topic}/{group}";
        }
        return cluster;
    }

    private static string? LabelValue(
        IReadOnlyDictionary<string, string> labels,
        IReadOnlySet<string> names) => labels
        .FirstOrDefault(label => names.Contains(label.Key))
        .Value;

    private static string Summary(
        KafkaPlannedMetric metric,
        double value,
        string severity,
        string objectId)
    {
        var formatted = value.ToString("0.###", CultureInfo.InvariantCulture);
        return $"{metric.Title}: {metric.TimeReducer} observed value {formatted} {metric.Unit} "
               + $"for {objectId} ({severity})";
    }

    private static void AddDiagnostic(ICollection<string> diagnostics, string diagnostic)
    {
        if (diagnostics.Count >= 40) return;
        var bounded = CrumbSourceUtilities.Truncate(diagnostic, 220);
        if (!diagnostics.Contains(bounded, StringComparer.Ordinal)) diagnostics.Add(bounded);
    }

    private sealed record BatchMetric(KafkaPlannedMetric Metric, string RefId);
    private sealed record SeriesKey(string MetricId, string FieldName, string LabelIdentity);

    private sealed class MetricSeries(
        KafkaPlannedMetric metric,
        string fieldName,
        IReadOnlyDictionary<string, string> labels,
        string labelIdentity)
    {
        public KafkaPlannedMetric Metric { get; } = metric;
        public string FieldName { get; } = fieldName;
        public IReadOnlyDictionary<string, string> Labels { get; } = labels;
        public string LabelIdentity { get; } = labelIdentity;
        public List<MetricSample> Samples { get; } = [];
    }

    private readonly record struct MetricSample(
        double Value,
        DateTimeOffset? Timestamp,
        int Sequence);
}
