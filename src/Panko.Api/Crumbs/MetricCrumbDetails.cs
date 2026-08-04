using System.Globalization;
using System.Text.Json.Nodes;
using Panko.Api.Domain;

namespace Panko.Api.Crumbs;

/// <summary>
/// Reads the canonical structured metric payload stored in a Crumb's provenance.
/// </summary>
internal sealed record MetricCrumbDetails(
    string Reducer,
    double? ReducedValue,
    DateTimeOffset? ObservedAt,
    DateTimeOffset? BreachStartedAt,
    DateTimeOffset? BreachEndedAt,
    double? WarningThreshold,
    double? CriticalThreshold,
    string Direction,
    string Unit,
    int SampleCount,
    DateTimeOffset? ObservationWindowEnd,
    bool TimestampSupported);

internal static class MetricCrumb
{
    public static bool TryRead(Crumb crumb, out MetricCrumbDetails details)
    {
        details = default!;
        if (crumb.Provenance["scope"] is not JsonObject scope)
        {
            return false;
        }

        var reducer = Text(scope, "reducer");
        var reducedValue = Number(scope, "reducedValue");
        if (reducer is null)
        {
            return false;
        }

        var observedAt = Timestamp(scope, "observedAt");
        if (Boolean(scope, "timestampSupported") is not { } timestampSupported
            || Boolean(scope, "reductionComplete") is not { } reductionComplete)
        {
            return false;
        }

        details = new MetricCrumbDetails(
            reducer.ToLowerInvariant(),
            reducedValue,
            observedAt,
            Timestamp(scope, "breachStartedAt"),
            Timestamp(scope, "breachEndedAt"),
            Number(scope, "warningThreshold"),
            Number(scope, "criticalThreshold"),
            (Text(scope, "direction") ?? "above").ToLowerInvariant(),
            Text(scope, "unit") ?? "",
            Math.Max(0, Integer(scope, "sampleCount") ?? 0),
            Timestamp(scope, "exactWindowEnd"),
            reductionComplete && timestampSupported && observedAt.HasValue);
        return true;
    }

    public static bool HasReliableTimestamp(Crumb crumb)
    {
        if (!TryRead(crumb, out var metric))
        {
            // A Grafana metric is a temporal Crumb only when it carries a canonical observed
            // sample timestamp. Other sources may submit timestamped metric-shaped Crumbs
            // without using Grafana's provenance contract.
            return !string.Equals(crumb.Source, "grafana", StringComparison.Ordinal)
                || crumb.Category != "metric";
        }

        return metric.TimestampSupported;
    }

    public static DateTimeOffset Start(Crumb crumb)
    {
        if (!TryRead(crumb, out var metric)) return crumb.OccurredAt;
        return metric.BreachStartedAt ?? metric.ObservedAt ?? crumb.OccurredAt;
    }

    public static DateTimeOffset End(Crumb crumb, DateTimeOffset collectionEnd)
    {
        if (!TryRead(crumb, out var metric)) return crumb.EndedAt ?? crumb.OccurredAt;
        var start = metric.BreachStartedAt ?? metric.ObservedAt ?? crumb.OccurredAt;
        var observationEnd = metric.ObservationWindowEnd is { } windowEnd && windowEnd < collectionEnd
            ? windowEnd
            : collectionEnd;
        // A null breach end means recovery was not observed. Keep the reasoning interval open
        // through the bounded observation window without serializing a made-up recovery time.
        var end = metric.BreachEndedAt
            ?? (metric.BreachStartedAt.HasValue ? (DateTimeOffset?)observationEnd : null)
            ?? crumb.EndedAt
            ?? metric.ObservedAt
            ?? crumb.OccurredAt;
        return end < start ? start : end;
    }

    private static string? Text(JsonObject scope, string name) =>
        Find(scope, name).Value is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : null;

    private static double? Number(JsonObject scope, string name)
    {
        if (Find(scope, name).Value is not JsonValue value) return null;
        if (value.TryGetValue<double>(out var number) && double.IsFinite(number)) return number;
        if (value.TryGetValue<long>(out var integer)) return integer;
        if (value.TryGetValue<int>(out var smallerInteger)) return smallerInteger;
        return value.TryGetValue<string>(out var text)
               && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number)
               && double.IsFinite(number)
            ? number
            : null;
    }

    private static int? Integer(JsonObject scope, string name)
    {
        if (Find(scope, name).Value is not JsonValue value) return null;
        if (value.TryGetValue<int>(out var integer)) return integer;
        if (value.TryGetValue<long>(out var longer)
            && longer is >= int.MinValue and <= int.MaxValue)
        {
            return (int)longer;
        }
        return null;
    }

    private static bool? Boolean(JsonObject scope, string name)
    {
        if (Find(scope, name).Value is not JsonValue value) return null;
        if (value.TryGetValue<bool>(out var boolean)) return boolean;
        return value.TryGetValue<string>(out var text)
               && bool.TryParse(text, out boolean)
            ? boolean
            : null;
    }

    private static DateTimeOffset? Timestamp(JsonObject scope, string name)
    {
        if (Find(scope, name).Value is not JsonValue value) return null;
        if (value.TryGetValue<DateTimeOffset>(out var timestamp)) return timestamp;
        if (value.TryGetValue<DateTime>(out var dateTime)) return new DateTimeOffset(dateTime);
        return value.TryGetValue<string>(out var text)
               && DateTimeOffset.TryParse(
                   text,
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.RoundtripKind,
                   out timestamp)
            ? timestamp
            : null;
    }

    private static KeyValuePair<string, JsonNode?> Find(JsonObject scope, string name) =>
        scope.FirstOrDefault(item => string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase));
}
