using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentBot.Api.Connectors;
using IncidentBot.Api.Domain;
using IncidentBot.Api.Options;
using Microsoft.Extensions.Options;

namespace IncidentBot.Api.Incidents;

/// <summary>
/// Collects one bounded evidence snapshot. Historical expansion queries disjoint rings and stops
/// only after an explainable deterministic clarity rule succeeds or no wider search is permitted.
/// </summary>
public sealed class AdaptiveEvidenceCollector(
    IOptions<IncidentBotOptions> options,
    TimeProvider timeProvider,
    ILogger<AdaptiveEvidenceCollector> logger)
{
    public async Task<EvidenceCollectionResult> CollectAsync(
        InvestigationContext context,
        string profileRevision,
        IReadOnlyList<IIncidentEvidenceConnector> connectors,
        CancellationToken cancellationToken)
    {
        var configured = options.Value;
        var collectionEnd = timeProvider.GetUtcNow();
        if (connectors.Count == 0)
        {
            return new EvidenceCollectionResult(
                [],
                new EvidenceCollectionOutcome(
                    EvidenceCollectionCompletionReason.NoConnectors,
                    EvidenceClarityAssessment.Inconclusive,
                    0,
                    0,
                    context.TriggeredAt,
                    collectionEnd));
        }

        var accumulated = connectors.ToDictionary(
            connector => connector.Source,
            _ => new List<ConnectorResult>(),
            StringComparer.Ordinal);
        var expandableConnectors = connectors
            .Where(connector => connector.SupportsWindowExpansion)
            .ToArray();
        var observedFindingIds = new HashSet<string>(StringComparer.Ordinal);
        var stopwatch = Stopwatch.StartNew();
        var passCount = 0;
        var previousWindowMinutes = 0;
        var finalWindowMinutes = configured.EvidenceWindowMinutes;
        var completionReason = EvidenceCollectionCompletionReason.MaximumWindowReached;
        var clarity = EvidenceClarityAssessment.Inconclusive;

        foreach (var windowMinutes in WindowSequence(
                     configured.EvidenceWindowMinutes,
                     configured.EvidenceMaximumWindowMinutes))
        {
            var passConnectors = passCount == 0
                ? connectors
                : expandableConnectors;
            if (passConnectors.Count == 0)
            {
                completionReason = EvidenceCollectionCompletionReason.NoExpandableConnectors;
                break;
            }

            passCount++;
            finalWindowMinutes = windowMinutes;
            var scope = new EvidenceScope(
                context.TriggeredAt - TimeSpan.FromMinutes(windowMinutes),
                previousWindowMinutes == 0
                    ? collectionEnd
                    : context.TriggeredAt - TimeSpan.FromMinutes(previousWindowMinutes),
                profileRevision,
                configured.EvidenceMaximumItems,
                configured.EvidenceMaximumBytes);
            var passStopwatch = Stopwatch.StartNew();
            logger.LogInformation(
                "Evidence collection pass {CollectionPass} started for ring {EvidenceRingStart} to {EvidenceRingEnd}, extending coverage to a {EvidenceWindowMinutes}-minute lookback for {ConnectorCount} connectors",
                passCount, scope.Start, scope.End, windowMinutes, passConnectors.Count);

            var results = await Task.WhenAll(passConnectors.Select(
                connector => CollectSafelyAsync(connector, context, scope, cancellationToken)));
            foreach (var result in results)
            {
                accumulated[result.Source].Add(result);
            }

            var returnedFindingCount = results.Sum(result => result.Findings.Count);
            var newFindingCount = results
                .SelectMany(result => result.Findings.Select(finding => $"{result.Source}\u001f{finding.Id}"))
                .Count(observedFindingIds.Add);
            clarity = EvidenceClarityPolicy.Evaluate(
                context,
                accumulated.Values.SelectMany(calls => calls),
                collectionEnd,
                configured.EvidenceWindowMinutes);
            logger.LogInformation(
                "Evidence collection pass {CollectionPass} completed in {DurationMilliseconds} ms for ring {EvidenceRingStart} to {EvidenceRingEnd}: {ReturnedFindingCount} findings returned, {NewFindingCount} new stable findings, {DuplicateFindingCount} duplicate or updated findings, {CompleteCount} complete, {PartialCount} partial, {UnavailableCount} unavailable; clarity {EvidenceClarityReason}",
                passCount,
                passStopwatch.ElapsedMilliseconds,
                scope.Start,
                scope.End,
                returnedFindingCount,
                newFindingCount,
                returnedFindingCount - newFindingCount,
                results.Count(result => result.Health == SourceHealth.Complete),
                results.Count(result => result.Health == SourceHealth.Partial),
                results.Count(result => result.Health == SourceHealth.Unavailable),
                clarity.Reason);

            if (clarity.IsClear)
            {
                completionReason = EvidenceCollectionCompletionReason.ClearResult;
                break;
            }
            if (windowMinutes >= configured.EvidenceMaximumWindowMinutes)
            {
                completionReason = EvidenceCollectionCompletionReason.MaximumWindowReached;
                break;
            }
            if (expandableConnectors.Length == 0)
            {
                completionReason = EvidenceCollectionCompletionReason.NoExpandableConnectors;
                break;
            }

            var nextWindowMinutes = Math.Min(
                configured.EvidenceMaximumWindowMinutes,
                (int)Math.Min(int.MaxValue, (long)windowMinutes * 2));
            logger.LogInformation(
                "Evidence collection remains inconclusive after a {EvidenceWindowMinutes}-minute lookback; expanding with the disjoint {NextRingStart} to {NextRingEnd} ring",
                windowMinutes,
                context.TriggeredAt - TimeSpan.FromMinutes(nextWindowMinutes),
                context.TriggeredAt - TimeSpan.FromMinutes(windowMinutes));
            previousWindowMinutes = windowMinutes;
        }

        var mergedResults = accumulated
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => Merge(
                item.Key,
                item.Value,
                context.TriggeredAt,
                configured.EvidenceMaximumItems,
                configured.EvidenceMaximumBytes))
            .ToArray();
        var outcome = new EvidenceCollectionOutcome(
            completionReason,
            clarity,
            passCount,
            finalWindowMinutes,
            context.TriggeredAt - TimeSpan.FromMinutes(finalWindowMinutes),
            collectionEnd);
        logger.LogInformation(
            "Evidence collection completed in {DurationMilliseconds} ms with reason {CompletionReason} after {CollectionPassCount} pass(es) and a {EvidenceWindowMinutes}-minute lookback: clarity {EvidenceClarityReason} supported by {SupportingEvidenceIds}; {CompleteCount} complete, {PartialCount} partial, {UnavailableCount} unavailable, {FindingCount} retained findings",
            stopwatch.ElapsedMilliseconds,
            outcome.CompletionReason,
            outcome.PassCount,
            outcome.FinalLookbackMinutes,
            outcome.Clarity.Reason,
            string.Join(',', outcome.Clarity.SupportingEvidenceIds),
            mergedResults.Count(result => result.Health == SourceHealth.Complete),
            mergedResults.Count(result => result.Health == SourceHealth.Partial),
            mergedResults.Count(result => result.Health == SourceHealth.Unavailable),
            mergedResults.Sum(result => result.Findings.Count));
        return new EvidenceCollectionResult(mergedResults, outcome);
    }

    internal static IReadOnlyList<int> WindowSequence(int initialMinutes, int maximumMinutes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialMinutes);
        if (maximumMinutes < initialMinutes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumMinutes),
                "The maximum evidence window must be at least the initial evidence window.");
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

    private static ConnectorResult Merge(
        string source,
        IReadOnlyList<ConnectorResult> calls,
        DateTimeOffset incidentTriggeredAt,
        int maximumItems,
        int maximumBytes)
    {
        var canonicalFindings = calls
            .SelectMany(call => call.Findings)
            .GroupBy(finding => finding.Id, StringComparer.Ordinal)
            .Select(group => MergeDuplicateFindings(group.ToList(), incidentTriggeredAt))
            .ToList();
        var findings = EvidenceRankingPolicy.OrderForReport(
                canonicalFindings,
                incidentTriggeredAt,
                maximumItems)
            .ToList();
        var timelineCandidates = calls.SelectMany(call => call.Timeline).ToList();
        var canonicalTimeline = ReportComposer.RetainTimeline(
                timelineCandidates,
                incidentTriggeredAt,
                int.MaxValue)
            .ToList();
        var timeline = canonicalTimeline.Count <= maximumItems
            ? canonicalTimeline
            : ReportComposer.RetainTimeline(
                    canonicalTimeline,
                    incidentTriggeredAt,
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
        var itemConstrained = canonicalFindings.Count > findings.Count
            || canonicalTimeline.Count > timeline.Count
            || linkCandidates.Count > links.Count;
        var hasAvailableCall = calls.Any(call => call.Health is SourceHealth.Complete or SourceHealth.Partial);
        var health = !hasAvailableCall
            ? calls[^1].Health
            : calls.Any(call => call.Health is SourceHealth.Partial or SourceHealth.Unavailable) || itemConstrained
                ? SourceHealth.Partial
                : SourceHealth.Complete;
        var diagnostics = calls
            .Select(call => call.Diagnostic)
            .Where(diagnostic => !string.IsNullOrWhiteSpace(diagnostic))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (itemConstrained)
        {
            diagnostics.Add($"Adaptive collection retained at most {maximumItems} findings, timeline entries, and links for this source.");
        }

        var candidate = new ConnectorResult(
            source,
            health,
            findings,
            timeline,
            links,
            calls.Sum(call => call.DurationMilliseconds),
            ConnectorUtilities.CombineDiagnostics(diagnostics.ToArray()));
        if (RetainedBytes(candidate) <= maximumBytes)
        {
            return candidate;
        }

        diagnostics.Add(
            $"Adaptive collection applied the cumulative {maximumBytes.ToString("N0", CultureInfo.InvariantCulture)}-byte retained-result limit for this source.");
        candidate = candidate with
        {
            Health = SourceHealth.Partial,
            Diagnostic = ConnectorUtilities.CombineDiagnostics(diagnostics.ToArray())
        };
        return FitRetainedBytes(candidate, maximumBytes);
    }

    private static EvidenceFinding MergeDuplicateFindings(
        IReadOnlyList<EvidenceFinding> findings,
        DateTimeOffset incidentTriggeredAt)
    {
        if (findings.Count == 1) return findings[0];

        if (findings.All(finding => finding.Category == "log-count")
            && findings.All(finding => TryScopeInt64(finding, "matchCount", out _)))
        {
            var selected = EvidenceRankingPolicy.Rank(findings, incidentTriggeredAt)[0];
            var total = SumMatchCounts(findings);
            var label = ScopeText(selected, "Name") ?? selected.ObjectId ?? "Log query";
            return selected with
            {
                OccurredAt = findings.Max(finding => finding.OccurredAt),
                Summary = $"{label}: {total} matching log events across the adaptive evidence window",
                Provenance = MergeWindowProvenance(selected, findings, "matchCount", total)
            };
        }

        if (findings.All(finding => finding.Category == "metric"))
        {
            var withValues = findings
                .Select(finding => new
                {
                    Finding = finding,
                    HasValue = TryScopeDouble(finding, "maximumObservedValue", out var value),
                    Value = value
                })
                .Where(item => item.HasValue)
                .OrderByDescending(item => item.Value)
                .ThenBy(item => Math.Abs((item.Finding.OccurredAt - incidentTriggeredAt).Ticks))
                .ThenBy(item => item.Finding.Id, StringComparer.Ordinal)
                .FirstOrDefault();
            if (withValues is not null)
            {
                return withValues.Finding with
                {
                    OccurredAt = findings.Max(finding => finding.OccurredAt),
                    Provenance = MergeWindowProvenance(
                        withValues.Finding,
                        findings,
                        "maximumObservedValue",
                        withValues.Value)
                };
            }
        }

        return EvidenceRankingPolicy.Rank(findings, incidentTriggeredAt)[0];
    }

    private static JsonObject MergeWindowProvenance<T>(
        EvidenceFinding selected,
        IReadOnlyList<EvidenceFinding> findings,
        string aggregateName,
        T aggregateValue)
    {
        var provenance = selected.Provenance.DeepClone().AsObject();
        var scope = provenance["scope"] as JsonObject ?? new JsonObject();
        provenance["scope"] = scope;
        scope[aggregateName] = JsonValue.Create(aggregateValue);
        scope["adaptiveWindowSegments"] = findings.Count;
        var starts = findings
            .Select(finding => ScopeDateTime(finding, "exactWindowStart"))
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToList();
        var ends = findings
            .Select(finding => ScopeDateTime(finding, "exactWindowEnd"))
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToList();
        if (starts.Count > 0) scope["exactWindowStart"] = starts.Min();
        if (ends.Count > 0) scope["exactWindowEnd"] = ends.Max();
        return provenance;
    }

    private static ConnectorResult FitRetainedBytes(ConnectorResult result, int maximumBytes)
    {
        var findings = result.Findings.ToList();
        var timeline = result.Timeline.ToList();
        var links = result.Links.ToList();
        var candidate = result;
        while (RetainedBytes(candidate) > maximumBytes
               && (links.Count > 0 || timeline.Count > 0 || findings.Count > 0))
        {
            if (links.Count > 0) links.RemoveAt(links.Count - 1);
            else if (timeline.Count > 0) timeline.RemoveAt(timeline.Count - 1);
            else findings.RemoveAt(findings.Count - 1);
            candidate = candidate with
            {
                Findings = findings.ToArray(),
                Timeline = timeline.ToArray(),
                Links = links.ToArray()
            };
        }
        return candidate;
    }

    private static int RetainedBytes(ConnectorResult result) =>
        JsonSerializer.SerializeToUtf8Bytes(result).Length;

    private static string? ScopeText(EvidenceFinding finding, string name)
    {
        var value = ScopeNode(finding, name);
        return value is JsonValue json && json.TryGetValue<string>(out var text) ? text : null;
    }

    private static bool TryScopeInt64(EvidenceFinding finding, string name, out long value)
    {
        value = 0;
        if (ScopeNode(finding, name) is not JsonValue json) return false;
        if (json.TryGetValue<long>(out value)) return true;
        if (!json.TryGetValue<int>(out var integer)) return false;
        value = integer;
        return true;
    }

    private static bool TryScopeDouble(EvidenceFinding finding, string name, out double value)
    {
        value = 0;
        if (ScopeNode(finding, name) is not JsonValue json) return false;
        if (json.TryGetValue<double>(out value)) return true;
        if (!json.TryGetValue<long>(out var integer)) return false;
        value = integer;
        return true;
    }

    private static DateTimeOffset? ScopeDateTime(EvidenceFinding finding, string name)
    {
        if (ScopeNode(finding, name) is not JsonValue json) return null;
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

    private static JsonNode? ScopeNode(EvidenceFinding finding, string name)
    {
        if (finding.Provenance["scope"] is not JsonObject scope) return null;
        return scope.FirstOrDefault(item =>
            string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
    }

    private static long SumMatchCounts(IEnumerable<EvidenceFinding> findings)
    {
        var total = 0L;
        foreach (var finding in findings)
        {
            TryScopeInt64(finding, "matchCount", out var count);
            count = Math.Max(0, count);
            if (count > long.MaxValue - total) return long.MaxValue;
            total += count;
        }
        return total;
    }

    private async Task<ConnectorResult> CollectSafelyAsync(
        IIncidentEvidenceConnector connector,
        InvestigationContext context,
        EvidenceScope scope,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await connector.CollectAsync(context, scope, cancellationToken);
            if (result.Health == SourceHealth.Unavailable)
            {
                logger.LogWarning(
                    "Connector {Source} unavailable after {DurationMilliseconds} ms: {Diagnostic}",
                    connector.Source, result.DurationMilliseconds,
                    string.IsNullOrWhiteSpace(result.Diagnostic) ? "No diagnostic supplied" : result.Diagnostic);
            }
            else if (result.Health == SourceHealth.Partial)
            {
                logger.LogWarning(
                    "Connector {Source} returned partial evidence after {DurationMilliseconds} ms with {FindingCount} findings: {Diagnostic}",
                    connector.Source, result.DurationMilliseconds, result.Findings.Count,
                    string.IsNullOrWhiteSpace(result.Diagnostic) ? "No diagnostic supplied" : result.Diagnostic);
            }
            else
            {
                logger.LogDebug(
                    "Connector {Source} completed with health {SourceHealth} in {DurationMilliseconds} ms and returned {FindingCount} findings",
                    connector.Source, result.Health, result.DurationMilliseconds, result.Findings.Count);
            }
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Connector {Source} failed outside its normal failure boundary", connector.Source);
            var diagnostic = exception.Message.Length <= 500 ? exception.Message : exception.Message[..500] + "…";
            return ConnectorResult.Unavailable(connector.Source, stopwatch.ElapsedMilliseconds, diagnostic);
        }
    }
}
