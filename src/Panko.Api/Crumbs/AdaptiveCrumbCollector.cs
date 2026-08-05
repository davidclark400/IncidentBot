using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Panko.Api.Crumbs;
using Panko.Api.Domain;
using Panko.Api.Options;
using Microsoft.Extensions.Options;

namespace Panko.Api.Crumbs;

/// <summary>
/// Collects one bounded Crumb snapshot. Historical expansion queries disjoint rings and stops
/// only after an explainable deterministic clarity rule succeeds or no wider search is permitted.
/// </summary>
public sealed class AdaptiveCrumbCollector(
    IOptions<PankoOptions> options,
    TimeProvider timeProvider,
    ILogger<AdaptiveCrumbCollector> logger)
{
    public Task<CrumbCollectionResult> CollectAsync(
        CaseContext context,
        string recipeRevision,
        IReadOnlyList<ICrumbSourceAdapter> sourceAdapters,
        CancellationToken cancellationToken) =>
        CollectAsync(context, recipeRevision, sourceAdapters, null, cancellationToken);

    public async Task<CrumbCollectionResult> CollectAsync(
        CaseContext context,
        string recipeRevision,
        IReadOnlyList<ICrumbSourceAdapter> sourceAdapters,
        ICrumbCollectionProgressObserver? progress,
        CancellationToken cancellationToken)
    {
        var configured = options.Value;
        var collectionEnd = ResolveCollectionEnd(
            context,
            timeProvider.GetUtcNow(),
            configured.CrumbPostResolutionWindowMinutes);
        if (sourceAdapters.Count == 0)
        {
            return new CrumbCollectionResult(
                [],
                new CrumbCollectionOutcome(
                    CrumbCollectionCompletionReason.NoCrumbSources,
                    CrumbClarityAssessment.Inconclusive,
                    0,
                    0,
                    context.OpenedAt,
                    collectionEnd));
        }

        var accumulated = sourceAdapters.ToDictionary(
            sourceAdapter => sourceAdapter.Source,
            _ => new List<CrumbSourceResult>(),
            StringComparer.Ordinal);
        var expandableSourceAdapters = sourceAdapters
            .Where(sourceAdapter => sourceAdapter.SupportsWindowExpansion)
            .ToArray();
        var observedCrumbIds = new HashSet<string>(StringComparer.Ordinal);
        var stopwatch = Stopwatch.StartNew();
        var passCount = 0;
        var previousWindowMinutes = 0;
        var finalWindowMinutes = configured.CrumbWindowMinutes;
        var completionReason = CrumbCollectionCompletionReason.MaximumWindowReached;
        var clarity = CrumbClarityAssessment.Inconclusive;

        foreach (var windowMinutes in WindowSequence(
                     configured.CrumbWindowMinutes,
                     configured.CrumbMaximumWindowMinutes))
        {
            var passSourceAdapters = passCount == 0
                ? sourceAdapters
                : expandableSourceAdapters;
            if (passSourceAdapters.Count == 0)
            {
                completionReason = CrumbCollectionCompletionReason.NoExpandableCrumbSources;
                break;
            }

            passCount++;
            finalWindowMinutes = windowMinutes;
            var scope = new CrumbScope(
                context.OpenedAt - TimeSpan.FromMinutes(windowMinutes),
                previousWindowMinutes == 0
                    ? collectionEnd
                    : context.OpenedAt - TimeSpan.FromMinutes(previousWindowMinutes),
                recipeRevision,
                configured.CrumbMaximumItems,
                configured.CrumbMaximumBytes);
            var passStopwatch = Stopwatch.StartNew();
            logger.LogInformation(
                "Crumb collection pass {CollectionPass} started for ring {CrumbRingStart} to {CrumbRingEnd}, extending coverage to a {CrumbWindowMinutes}-minute lookback for {CrumbSourceCount} sources",
                passCount, scope.Start, scope.End, windowMinutes, passSourceAdapters.Count);

            var pass = new CrumbCollectionPass(
                passCount,
                windowMinutes,
                context.OpenedAt - TimeSpan.FromMinutes(windowMinutes),
                collectionEnd);
            if (progress is not null)
            {
                await progress.PassStartedAsync(
                    pass,
                    passSourceAdapters.Select(sourceAdapter => sourceAdapter.Source).ToArray(),
                    cancellationToken);
            }

            var pending = passSourceAdapters
                .Select(sourceAdapter => CollectSafelyAsync(sourceAdapter, context, scope, cancellationToken))
                .ToList();
            var results = new List<CrumbSourceResult>(pending.Count);
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending);
                pending.Remove(completed);
                var result = await completed;
                results.Add(result);
                if (progress is not null)
                {
                    await progress.SourceCompletedAsync(pass, result, cancellationToken);
                }
            }
            foreach (var result in results)
            {
                accumulated[result.Source].Add(result);
            }

            var returnedCrumbCount = results.Sum(result => result.Crumbs.Count);
            var newCrumbCount = results
                .SelectMany(result => result.Crumbs.Select(crumb => $"{result.Source}\u001f{crumb.Id}"))
                .Count(observedCrumbIds.Add);
            clarity = CrumbClarityPolicy.Evaluate(
                context,
                accumulated.Values.SelectMany(calls => calls),
                collectionEnd,
                configured.CrumbWindowMinutes);
            if (progress is not null)
            {
                await progress.PassCompletedAsync(pass, clarity, cancellationToken);
            }
            logger.LogInformation(
                "Crumb collection pass {CollectionPass} completed in {DurationMilliseconds} ms for ring {CrumbRingStart} to {CrumbRingEnd}: {ReturnedCrumbCount} Crumbs returned, {NewCrumbCount} new stable Crumbs, {DuplicateCrumbCount} duplicate or updated Crumbs, {CompleteCount} complete, {PartialCount} partial, {UnavailableCount} unavailable; clarity {CrumbClarityReason}",
                passCount,
                passStopwatch.ElapsedMilliseconds,
                scope.Start,
                scope.End,
                returnedCrumbCount,
                newCrumbCount,
                returnedCrumbCount - newCrumbCount,
                results.Count(result => result.Health == CrumbSourceHealth.Complete),
                results.Count(result => result.Health == CrumbSourceHealth.Partial),
                results.Count(result => result.Health == CrumbSourceHealth.Unavailable),
                clarity.Reason);

            if (clarity.IsClear)
            {
                completionReason = CrumbCollectionCompletionReason.ClearResult;
                break;
            }
            if (windowMinutes >= configured.CrumbMaximumWindowMinutes)
            {
                completionReason = CrumbCollectionCompletionReason.MaximumWindowReached;
                break;
            }
            if (expandableSourceAdapters.Length == 0)
            {
                completionReason = CrumbCollectionCompletionReason.NoExpandableCrumbSources;
                break;
            }

            var nextWindowMinutes = Math.Min(
                configured.CrumbMaximumWindowMinutes,
                (int)Math.Min(int.MaxValue, (long)windowMinutes * 2));
            logger.LogInformation(
                "Crumb collection remains inconclusive after a {CrumbWindowMinutes}-minute lookback; expanding with the disjoint {NextRingStart} to {NextRingEnd} ring",
                windowMinutes,
                context.OpenedAt - TimeSpan.FromMinutes(nextWindowMinutes),
                context.OpenedAt - TimeSpan.FromMinutes(windowMinutes));
            previousWindowMinutes = windowMinutes;
        }

        var mergedResults = accumulated
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => Merge(
                item.Key,
                item.Value,
                context.OpenedAt,
                configured.CrumbMaximumItems,
                configured.CrumbMaximumBytes))
            .ToArray();
        var outcome = new CrumbCollectionOutcome(
            completionReason,
            clarity,
            passCount,
            finalWindowMinutes,
            context.OpenedAt - TimeSpan.FromMinutes(finalWindowMinutes),
            collectionEnd);
        logger.LogInformation(
            "Crumb collection completed in {DurationMilliseconds} ms with reason {CompletionReason} after {CollectionPassCount} pass(es) and a {CrumbWindowMinutes}-minute lookback: clarity {CrumbClarityReason} supported by {SupportingCrumbIds}; {CompleteCount} complete, {PartialCount} partial, {UnavailableCount} unavailable, {CrumbCount} Crumbs retained",
            stopwatch.ElapsedMilliseconds,
            outcome.CompletionReason,
            outcome.PassCount,
            outcome.FinalLookbackMinutes,
            outcome.Clarity.Reason,
            string.Join(',', outcome.Clarity.SupportingCrumbIds),
            mergedResults.Count(result => result.Health == CrumbSourceHealth.Complete),
            mergedResults.Count(result => result.Health == CrumbSourceHealth.Partial),
            mergedResults.Count(result => result.Health == CrumbSourceHealth.Unavailable),
            mergedResults.Sum(result => result.Crumbs.Count));
        return new CrumbCollectionResult(mergedResults, outcome);
    }

    internal static DateTimeOffset ResolveCollectionEnd(
        CaseContext context,
        DateTimeOffset now,
        int postCaseWindowMinutes)
    {
        if (context.PagerDutyState != PagerDutyIncidentState.Resolved
            || context.ResolvedAt is not { } resolvedAt
            || resolvedAt < context.OpenedAt)
        {
            return now;
        }

        var postCaseWindow = TimeSpan.FromMinutes(Math.Max(0, postCaseWindowMinutes));
        var postCaseEnd = resolvedAt > DateTimeOffset.MaxValue - postCaseWindow
            ? DateTimeOffset.MaxValue
            : resolvedAt + postCaseWindow;
        return postCaseEnd < now ? postCaseEnd : now;
    }

    internal static IReadOnlyList<int> WindowSequence(int initialMinutes, int maximumMinutes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialMinutes);
        if (maximumMinutes < initialMinutes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumMinutes),
                "The maximum Crumb window must be at least the initial Crumb window.");
        }

        var windows = new List<int>();
        var current = initialMinutes;
        while (true)
        {
            windows.Add(current);
            if (current >= maximumMinutes)
            {
                return windows;
            }

            current = (int)Math.Min(maximumMinutes, (long)current * 2);
        }
    }

    private static CrumbSourceResult Merge(
        string source,
        IReadOnlyList<CrumbSourceResult> calls,
        DateTimeOffset caseOpenedAt,
        int maximumItems,
        int maximumBytes)
    {
        var canonicalCrumbs = calls
            .SelectMany(call => call.Crumbs)
            .GroupBy(crumb => crumb.Id, StringComparer.Ordinal)
            .Select(group => MergeDuplicateCrumbs(group.ToList(), caseOpenedAt))
            .ToList();
        var crumbs = CrumbRankingPolicy.OrderForCaseFile(
                canonicalCrumbs,
                caseOpenedAt,
                maximumItems)
            .ToList();
        var trailCandidates = calls.SelectMany(call => call.Trail).ToList();
        var canonicalTrail = CaseFileComposer.RetainTrail(
                trailCandidates,
                caseOpenedAt,
                int.MaxValue)
            .ToList();
        var trail = canonicalTrail.Count <= maximumItems
            ? canonicalTrail
            : CaseFileComposer.RetainTrail(
                    canonicalTrail,
                    caseOpenedAt,
                    maximumItems)
                .ToList();
        var linkCandidates = calls
            .SelectMany(call => call.Links)
            .GroupBy(link => link.Url, StringComparer.Ordinal)
            .Select(group => group.OrderBy(link => link.Label, StringComparer.Ordinal).First())
            .OrderBy(link => link.Label, StringComparer.Ordinal)
            .ThenBy(link => link.Url, StringComparer.Ordinal)
            .ToList();
        var links = linkCandidates.Take(maximumItems).ToList();
        var itemConstrained = canonicalCrumbs.Count > crumbs.Count
            || canonicalTrail.Count > trail.Count
            || linkCandidates.Count > links.Count;
        var hasAvailableCall = calls.Any(call => call.Health is CrumbSourceHealth.Complete or CrumbSourceHealth.Partial);
        var health = !hasAvailableCall
            ? calls[^1].Health
            : calls.Any(call => call.Health is CrumbSourceHealth.Partial or CrumbSourceHealth.Unavailable) || itemConstrained
                ? CrumbSourceHealth.Partial
                : CrumbSourceHealth.Complete;
        var diagnostics = calls
            .Select(call => call.Diagnostic)
            .Where(diagnostic => !string.IsNullOrWhiteSpace(diagnostic))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (itemConstrained)
        {
            diagnostics.Add($"Adaptive collection retained at most {maximumItems} Crumbs, Trail entries, and links for this source.");
        }

        var candidate = new CrumbSourceResult(
            source,
            health,
            crumbs,
            trail,
            links,
            calls.Sum(call => call.DurationMilliseconds),
            CrumbSourceUtilities.CombineDiagnostics(diagnostics.ToArray()));
        if (RetainedBytes(candidate) <= maximumBytes)
        {
            return candidate;
        }

        diagnostics.Add(
            $"Adaptive collection applied the cumulative {maximumBytes.ToString("N0", CultureInfo.InvariantCulture)}-byte retained-result limit for this source.");
        candidate = candidate with
        {
            Health = CrumbSourceHealth.Partial,
            Diagnostic = CrumbSourceUtilities.CombineDiagnostics(diagnostics.ToArray())
        };
        return FitRetainedBytes(candidate, maximumBytes);
    }

    private static Crumb MergeDuplicateCrumbs(
        IReadOnlyList<Crumb> crumbs,
        DateTimeOffset caseOpenedAt)
    {
        if (crumbs.Count == 1) return crumbs[0];

        if (crumbs.All(crumb => crumb.Category == "log-count")
            && crumbs.All(crumb => TryScopeInt64(crumb, "matchCount", out _)))
        {
            var selected = CrumbRankingPolicy.Rank(crumbs, caseOpenedAt)[0];
            var total = SumMatchCounts(crumbs);
            var label = ScopeText(selected, "Name") ?? selected.ObjectId ?? "Log query";
            return selected with
            {
                OccurredAt = crumbs.Max(crumb => crumb.OccurredAt),
                Summary = $"{label}: {total} matching log events across the adaptive Crumb window",
                Provenance = MergeWindowProvenance(selected, crumbs, "matchCount", total)
            };
        }

        if (crumbs.All(CrumbRankingPolicy.IsKafkaCrumb))
        {
            var directions = crumbs
                .Select(crumb => ScopeText(crumb, "direction")?.ToLowerInvariant())
                .ToList();
            var identities = crumbs
                .Select(crumb => $"{crumb.Category}\u001f{crumb.ObjectType}\u001f{crumb.ObjectId}")
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var withValues = crumbs
                .Select(crumb => new
                {
                    Crumb = crumb,
                    HasValue = TryScopeDouble(crumb, "reducedValue", out var value),
                    Value = value
                })
                .Where(item => item.HasValue)
                .ToList();
            if (directions.All(direction => direction is "above" or "below")
                && directions.Distinct(StringComparer.Ordinal).Count() == 1
                && identities.Count == 1
                && withValues.Count == crumbs.Count)
            {
                var selected = directions[0] == "below"
                    ? withValues
                        .OrderBy(item => item.Value)
                        .ThenByDescending(item => CrumbRankingPolicy.Score(item.Crumb, caseOpenedAt))
                        .ThenBy(item => item.Crumb.Summary, StringComparer.Ordinal)
                        .First()
                    : withValues
                        .OrderByDescending(item => item.Value)
                        .ThenByDescending(item => CrumbRankingPolicy.Score(item.Crumb, caseOpenedAt))
                        .ThenBy(item => item.Crumb.Summary, StringComparer.Ordinal)
                        .First();
                return selected.Crumb with
                {
                    Provenance = MergeWindowProvenance(
                        selected.Crumb,
                        crumbs,
                        "reducedValue",
                        selected.Value)
                };
            }
        }

        if (crumbs.All(crumb => crumb.Category == "metric"))
        {
            var withValues = crumbs
                .Select(crumb => new
                {
                    Crumb = crumb,
                    HasMetric = MetricCrumb.TryRead(crumb, out var metric),
                    Metric = metric
                })
                .Where(item => item.HasMetric && item.Metric.ReducedValue.HasValue)
                .ToList();
            if (withValues.Count > 0)
            {
                // The first adaptive ring spans the Case. Older rings are baseline-only;
                // never let an older baseline extreme replace the actual Case observation.
                var caseValues = withValues
                    .Where(item => string.Equals(
                        ScopeText(item.Crumb, "comparisonPeriod"),
                        "case",
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var candidates = caseValues.Count > 0 ? caseValues : withValues;
                var reducers = candidates
                    .Select(item => item.Metric.Reducer)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (reducers.Count == 1)
                {
                    var reducer = reducers[0];
                    var selected = reducer switch
                    {
                        "minimum" => candidates
                            .OrderBy(item => item.Metric.ReducedValue)
                            .ThenByDescending(item => item.Metric.ObservedAt)
                            .ThenBy(item => item.Crumb.Id, StringComparer.Ordinal)
                            .First(),
                        "last" => candidates
                            .OrderByDescending(item => item.Metric.ObservedAt ?? item.Crumb.OccurredAt)
                            .ThenByDescending(item => item.Crumb.OccurredAt)
                            .ThenBy(item => item.Crumb.Id, StringComparer.Ordinal)
                            .First(),
                        _ => candidates
                            .OrderByDescending(item => item.Metric.ReducedValue)
                            .ThenByDescending(item => item.Metric.ObservedAt)
                            .ThenBy(item => item.Crumb.Id, StringComparer.Ordinal)
                            .First()
                    };
                    var provenance = MergeWindowProvenance(
                        selected.Crumb,
                        crumbs,
                        "reducedValue",
                        selected.Metric.ReducedValue!.Value);
                    if (provenance["scope"] is JsonObject scope)
                    {
                        scope["sampleCount"] = candidates.Sum(item => item.Metric.SampleCount);
                    }
                    // Preserve the timestamp of the sample chosen by the reducer.
                    return selected.Crumb with { Provenance = provenance };
                }
            }
        }

        return CrumbRankingPolicy.Rank(crumbs, caseOpenedAt)[0];
    }

    private static JsonObject MergeWindowProvenance<T>(
        Crumb selected,
        IReadOnlyList<Crumb> crumbs,
        string aggregateName,
        T aggregateValue)
    {
        var provenance = selected.Provenance.DeepClone().AsObject();
        var scope = provenance["scope"] as JsonObject ?? new JsonObject();
        provenance["scope"] = scope;
        scope[aggregateName] = JsonValue.Create(aggregateValue);
        scope["adaptiveWindowSegments"] = crumbs.Count;
        var starts = crumbs
            .Select(crumb => ScopeDateTime(crumb, "exactWindowStart"))
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToList();
        var ends = crumbs
            .Select(crumb => ScopeDateTime(crumb, "exactWindowEnd"))
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToList();
        if (starts.Count > 0) scope["exactWindowStart"] = starts.Min();
        if (ends.Count > 0) scope["exactWindowEnd"] = ends.Max();
        return provenance;
    }

    private static CrumbSourceResult FitRetainedBytes(CrumbSourceResult result, int maximumBytes)
    {
        var crumbs = result.Crumbs.ToList();
        var trail = result.Trail.ToList();
        var links = result.Links.ToList();
        var candidate = result;
        while (RetainedBytes(candidate) > maximumBytes
               && (links.Count > 0 || trail.Count > 0 || crumbs.Count > 0))
        {
            if (links.Count > 0) links.RemoveAt(links.Count - 1);
            else if (trail.Count > 0) trail.RemoveAt(trail.Count - 1);
            else crumbs.RemoveAt(crumbs.Count - 1);
            candidate = candidate with
            {
                Crumbs = crumbs.ToArray(),
                Trail = trail.ToArray(),
                Links = links.ToArray()
            };
        }
        return candidate;
    }

    private static int RetainedBytes(CrumbSourceResult result) =>
        JsonSerializer.SerializeToUtf8Bytes(result).Length;

    private static string? ScopeText(Crumb crumb, string name)
    {
        var value = ScopeNode(crumb, name);
        return value is JsonValue json && json.TryGetValue<string>(out var text) ? text : null;
    }

    private static bool TryScopeInt64(Crumb crumb, string name, out long value)
    {
        value = 0;
        if (ScopeNode(crumb, name) is not JsonValue json) return false;
        if (json.TryGetValue<long>(out value)) return true;
        if (!json.TryGetValue<int>(out var integer)) return false;
        value = integer;
        return true;
    }

    private static bool TryScopeDouble(Crumb crumb, string name, out double value)
    {
        value = 0;
        if (ScopeNode(crumb, name) is not JsonValue json) return false;
        if (json.TryGetValue<double>(out value)) return double.IsFinite(value);
        if (json.TryGetValue<long>(out var integer))
        {
            value = integer;
            return true;
        }
        if (!json.TryGetValue<int>(out var smallerInteger)) return false;
        value = smallerInteger;
        return true;
    }

    private static DateTimeOffset? ScopeDateTime(Crumb crumb, string name)
    {
        if (ScopeNode(crumb, name) is not JsonValue json) return null;
        if (json.TryGetValue<DateTimeOffset>(out var dateTimeOffset)) return dateTimeOffset;
        if (json.TryGetValue<DateTime>(out var dateTime)) return new DateTimeOffset(dateTime);
        return json.TryGetValue<string>(out var text)
               && DateTimeOffset.TryParse(
                   text,
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.RoundtripKind,
                   out var parsed)
            ? parsed
            : null;
    }

    private static JsonNode? ScopeNode(Crumb crumb, string name)
    {
        if (crumb.Provenance["scope"] is not JsonObject scope) return null;
        return scope.FirstOrDefault(item =>
            string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
    }

    private static long SumMatchCounts(IEnumerable<Crumb> crumbs)
    {
        var total = 0L;
        foreach (var crumb in crumbs)
        {
            TryScopeInt64(crumb, "matchCount", out var count);
            count = Math.Max(0, count);
            if (count > long.MaxValue - total) return long.MaxValue;
            total += count;
        }
        return total;
    }

    private async Task<CrumbSourceResult> CollectSafelyAsync(
        ICrumbSourceAdapter sourceAdapter,
        CaseContext context,
        CrumbScope scope,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await sourceAdapter.CollectAsync(context, scope, cancellationToken);
            if (result.Health == CrumbSourceHealth.Unavailable)
            {
                logger.LogWarning(
                    "Crumb source {Source} unavailable after {DurationMilliseconds} ms: {Diagnostic}",
                    sourceAdapter.Source, result.DurationMilliseconds,
                    string.IsNullOrWhiteSpace(result.Diagnostic) ? "No diagnostic supplied" : result.Diagnostic);
            }
            else if (result.Health == CrumbSourceHealth.Partial)
            {
                logger.LogWarning(
                    "Crumb source {Source} returned a partial result after {DurationMilliseconds} ms with {CrumbCount} Crumbs: {Diagnostic}",
                    sourceAdapter.Source, result.DurationMilliseconds, result.Crumbs.Count,
                    string.IsNullOrWhiteSpace(result.Diagnostic) ? "No diagnostic supplied" : result.Diagnostic);
            }
            else
            {
                logger.LogDebug(
                    "Crumb source {Source} completed with health {CrumbSourceHealth} in {DurationMilliseconds} ms and returned {CrumbCount} Crumbs",
                    sourceAdapter.Source, result.Health, result.DurationMilliseconds, result.Crumbs.Count);
            }
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Crumb source {Source} failed outside its normal failure boundary", sourceAdapter.Source);
            var diagnostic = exception.Message.Length <= 500 ? exception.Message : exception.Message[..500] + "…";
            return CrumbSourceResult.Unavailable(sourceAdapter.Source, stopwatch.ElapsedMilliseconds, diagnostic);
        }
    }
}
