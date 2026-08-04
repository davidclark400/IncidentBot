using System.Diagnostics;
using Panko.Api.Domain;

namespace Panko.Api.Cases;

/// <summary>
/// Owns one Case-File-version-scoped progress attempt. Crumb-source results are retained only long
/// enough to derive bounded status metadata and top Crumbs; canonical Crumbs are never written here.
/// </summary>
public sealed class CaseProgressTracker : ICrumbCollectionProgressObserver
{
    private const int MaximumEarlyCrumbs = 5;
    private const int MaximumEarlyCrumbSummaryLength = 300;
    private const int MaximumDiagnosticLength = 300;
    private readonly Guid caseId;
    private readonly Guid attemptId = Guid.NewGuid();
    private readonly int baseCaseFileVersion;
    private readonly DateTimeOffset caseOpenedAt;
    private readonly TimeProvider timeProvider;
    private readonly Func<CaseProgress, bool, CancellationToken,
        Task<CaseProgress?>> commit;
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private readonly Dictionary<string, CaseSourceProgress> sources;
    private readonly Dictionary<string, List<CrumbSourceResult>> sourceResultsByPass;
    private readonly Dictionary<string, Crumb> crumbs = new(StringComparer.Ordinal);
    private readonly DateTimeOffset startedAt;
    private long revision;
    private int currentPass;
    private int currentLookbackMinutes;
    private CaseProgressPhase phase = CaseProgressPhase.Collecting;
    private bool deterministicCaseFileUsable;
    private bool onlyAiSynthesisRemaining;
    private AiSynthesisProgressState aiSynthesisState = AiSynthesisProgressState.Pending;
    private bool active = true;

    public Guid AttemptId => attemptId;

    internal CaseProgressTracker(
        CaseRecord caseRecord,
        IReadOnlyList<string> selectedSources,
        int initialLookbackMinutes,
        TimeProvider timeProvider,
        Func<CaseProgress, bool, CancellationToken,
            Task<CaseProgress?>> commit)
    {
        caseId = caseRecord.Id;
        baseCaseFileVersion = caseRecord.Version;
        caseOpenedAt = caseRecord.OpenedAt;
        this.timeProvider = timeProvider;
        this.commit = commit;
        startedAt = timeProvider.GetUtcNow();
        currentLookbackMinutes = initialLookbackMinutes;
        sources = selectedSources
            .Distinct(StringComparer.Ordinal)
            .OrderBy(source => source, StringComparer.Ordinal)
            .ToDictionary(
                source => source,
                source => new CaseSourceProgress(
                    source,
                    CrumbSourceProgressState.Pending,
                    CrumbSourceHealth.Pending,
                    0,
                    initialLookbackMinutes,
                    0,
                    0,
                    null,
                    null,
                    startedAt),
                StringComparer.Ordinal);
        sourceResultsByPass = sources.Keys.ToDictionary(
            source => source,
            _ => new List<CrumbSourceResult>(),
            StringComparer.Ordinal);
    }

    internal async Task InitializeAsync(CancellationToken cancellationToken) =>
        await PersistAsync(begin: true, cancellationToken);

    public async Task PassStartedAsync(
        CrumbCollectionPass pass,
        IReadOnlyList<string> passSources,
        CancellationToken cancellationToken)
    {
        if (!active) return;
        var now = timeProvider.GetUtcNow();
        currentPass = pass.Number;
        currentLookbackMinutes = pass.LookbackMinutes;
        foreach (var source in passSources)
        {
            if (!sources.TryGetValue(source, out var current)) continue;
            sources[source] = current with
            {
                RequestState = CrumbSourceProgressState.Querying,
                Pass = pass.Number,
                LookbackMinutes = pass.LookbackMinutes,
                Diagnostic = null,
                StartedAt = now,
                UpdatedAt = now
            };
        }
        await PersistAsync(begin: false, cancellationToken);
    }

    public async Task SourceCompletedAsync(
        CrumbCollectionPass pass,
        CrumbSourceResult result,
        CancellationToken cancellationToken)
    {
        if (!active || !sources.TryGetValue(result.Source, out var current)) return;
        var now = timeProvider.GetUtcNow();
        var calls = sourceResultsByPass[result.Source];
        calls.Add(result);
        foreach (var crumb in result.Crumbs)
        {
            crumbs[$"{result.Source}\u001f{crumb.Id}"] = crumb;
        }

        sources[result.Source] = current with
        {
            RequestState = TerminalState(result),
            Health = AggregateHealth(calls),
            Pass = pass.Number,
            LookbackMinutes = pass.LookbackMinutes,
            DurationMilliseconds = SumDurations(calls),
            CrumbCount = calls
                .SelectMany(call => call.Crumbs)
                .Select(crumb => crumb.Id)
                .Distinct(StringComparer.Ordinal)
                .Count(),
            Diagnostic = string.IsNullOrWhiteSpace(result.Diagnostic)
                ? null
                : Truncate(result.Diagnostic, MaximumDiagnosticLength),
            UpdatedAt = now
        };
        await PersistAsync(begin: false, cancellationToken);
    }

    public async Task PassCompletedAsync(
        CrumbCollectionPass pass,
        CrumbClarityAssessment clarity,
        CancellationToken cancellationToken)
    {
        if (!active) return;
        currentPass = pass.Number;
        currentLookbackMinutes = pass.LookbackMinutes;
        await PersistAsync(begin: false, cancellationToken);
    }

    public async Task CollectionCompletedAsync(
        CrumbCollectionOutcome outcome,
        IReadOnlyList<CrumbSourceResult> mergedResults,
        CancellationToken cancellationToken)
    {
        if (!active) return;
        crumbs.Clear();
        foreach (var result in mergedResults)
        {
            foreach (var crumb in result.Crumbs)
            {
                crumbs[$"{result.Source}\u001f{crumb.Id}"] = crumb;
            }
            if (!sources.TryGetValue(result.Source, out var source)) continue;
            sources[result.Source] = source with
            {
                Health = result.Health,
                DurationMilliseconds = result.DurationMilliseconds,
                CrumbCount = result.Crumbs.Count,
                Diagnostic = string.IsNullOrWhiteSpace(result.Diagnostic)
                    ? null
                    : Truncate(result.Diagnostic, MaximumDiagnosticLength),
                UpdatedAt = timeProvider.GetUtcNow()
            };
        }
        currentPass = outcome.PassCount;
        currentLookbackMinutes = outcome.FinalLookbackMinutes;
        phase = CaseProgressPhase.Synthesizing;
        deterministicCaseFileUsable = true;
        onlyAiSynthesisRemaining = true;
        aiSynthesisState = AiSynthesisProgressState.Running;
        await PersistAsync(begin: false, cancellationToken);
    }

    public async Task SynthesisCompletedAsync(
        AiSynthesis synthesis,
        CancellationToken cancellationToken)
    {
        if (!active) return;
        phase = CaseProgressPhase.Finalizing;
        onlyAiSynthesisRemaining = false;
        aiSynthesisState = synthesis.Status switch
        {
            "complete" => AiSynthesisProgressState.Complete,
            "skipped" => AiSynthesisProgressState.Skipped,
            _ => AiSynthesisProgressState.Unavailable
        };
        await PersistAsync(begin: false, cancellationToken);
    }

    private async Task PersistAsync(bool begin, CancellationToken cancellationToken)
    {
        if (!active) return;
        var now = timeProvider.GetUtcNow();
        var projection = new CaseProgress(
            caseId,
            attemptId,
            revision,
            baseCaseFileVersion,
            startedAt,
            now,
            elapsed.ElapsedMilliseconds,
            phase,
            currentPass,
            currentLookbackMinutes,
            deterministicCaseFileUsable,
            onlyAiSynthesisRemaining,
            aiSynthesisState,
            sources.Values.OrderBy(source => source.Source, StringComparer.Ordinal).ToArray(),
            CrumbRankingPolicy.SelectTopCrumbs(crumbs.Values, caseOpenedAt, MaximumEarlyCrumbs)
                .Select(crumb => new CaseEarlyCrumb(
                    crumb.Id,
                    crumb.Source,
                    crumb.OccurredAt,
                    crumb.Severity,
                    Truncate(crumb.Summary, MaximumEarlyCrumbSummaryLength),
                    crumb.Confidence))
                .ToArray());
        var stored = await commit(projection, begin, cancellationToken);
        if (stored is null)
        {
            active = false;
            return;
        }
        revision = stored.Revision;
    }

    private static CrumbSourceProgressState TerminalState(CrumbSourceResult result)
    {
        if (result.Health == CrumbSourceHealth.Excluded) return CrumbSourceProgressState.Excluded;
        if (result.Health != CrumbSourceHealth.Unavailable) return CrumbSourceProgressState.Received;
        return result.Diagnostic?.Contains("timeout", StringComparison.OrdinalIgnoreCase) == true
            || result.Diagnostic?.Contains("timed out", StringComparison.OrdinalIgnoreCase) == true
                ? CrumbSourceProgressState.TimedOut
                : CrumbSourceProgressState.Failed;
    }

    private static CrumbSourceHealth AggregateHealth(IReadOnlyList<CrumbSourceResult> calls)
    {
        var hasAvailableCall = calls.Any(call => call.Health is CrumbSourceHealth.Complete or CrumbSourceHealth.Partial);
        if (!hasAvailableCall) return calls[^1].Health;
        return calls.Any(call => call.Health is CrumbSourceHealth.Partial or CrumbSourceHealth.Unavailable)
            ? CrumbSourceHealth.Partial
            : CrumbSourceHealth.Complete;
    }

    private static long SumDurations(IEnumerable<CrumbSourceResult> calls)
    {
        var total = 0L;
        foreach (var call in calls)
        {
            if (call.DurationMilliseconds >= long.MaxValue - total) return long.MaxValue;
            total += Math.Max(0, call.DurationMilliseconds);
        }
        return total;
    }

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..(maximumLength - 1)] + "…";
}
