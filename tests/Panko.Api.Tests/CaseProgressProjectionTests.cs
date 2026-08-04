using System.Text.Json;
using System.Text.Json.Nodes;
using Panko.Api.Domain;
using Panko.Api.Cases;

namespace Panko.Api.Tests;

public sealed class CaseProgressProjectionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-11T10:05:00Z");

    [Fact]
    public async Task ProjectsCumulativeSourceStateTopSignalsAndSynthesisReadinessWithoutCrumbPayloads()
    {
        var snapshots = new List<CaseProgress>();
        var nextRevision = 0L;
        Task<CaseProgress?> Commit(
            CaseProgress progress,
            bool begin,
            CancellationToken cancellationToken)
        {
            var stored = progress with { Revision = ++nextRevision };
            snapshots.Add(stored);
            return Task.FromResult<CaseProgress?>(stored);
        }

        var tracker = new CaseProgressTracker(
            BuildCase(),
            ["victorialogs", "grafana"],
            30,
            new FixedTimeProvider(Now),
            Commit);
        await tracker.InitializeAsync(CancellationToken.None);

        var firstPass = new CrumbCollectionPass(1, 30, Now.AddMinutes(-30), Now);
        await tracker.PassStartedAsync(firstPass, ["victorialogs", "grafana"], CancellationToken.None);
        await tracker.SourceCompletedAsync(
            firstPass,
            Result("victorialogs", 420, [Crumb("first-error", "Initial checkout timeout")]),
            CancellationToken.None);
        await tracker.SourceCompletedAsync(
            firstPass,
            CrumbSourceResult.Unavailable("grafana", 1_800, "Timeout after 2 seconds"),
            CancellationToken.None);
        await tracker.PassCompletedAsync(
            firstPass,
            CrumbClarityAssessment.Inconclusive,
            CancellationToken.None);

        var secondPass = new CrumbCollectionPass(2, 120, Now.AddMinutes(-120), Now);
        await tracker.PassStartedAsync(secondPass, ["victorialogs"], CancellationToken.None);
        await tracker.SourceCompletedAsync(
            secondPass,
            Result("victorialogs", 900,
            [
                Crumb("first-error", "Checkout timeout confirmed in the wider window"),
                Crumb("second-error", "A second high-signal checkout failure")
            ]),
            CancellationToken.None);
        await tracker.PassCompletedAsync(
            secondPass,
            new CrumbClarityAssessment(true, CrumbClarityReason.ExplicitFailure, ["first-error"]),
            CancellationToken.None);
        await tracker.CollectionCompletedAsync(
            new CrumbCollectionOutcome(
                CrumbCollectionCompletionReason.ClearResult,
                new CrumbClarityAssessment(true, CrumbClarityReason.ExplicitFailure, ["first-error"]),
                2,
                120,
                Now.AddMinutes(-120),
                Now),
            [
                Result("victorialogs", 1_320,
                [
                    Crumb("first-error", "Checkout timeout confirmed in the wider window"),
                    Crumb("second-error", "A second high-signal checkout failure")
                ]),
                CrumbSourceResult.Unavailable("grafana", 1_800, "Timeout after 2 seconds")
            ],
            CancellationToken.None);

        var final = snapshots[^1];
        Assert.Equal(CaseProgressPhase.Synthesizing, final.Phase);
        Assert.Equal(2, final.CurrentPass);
        Assert.Equal(120, final.CurrentLookbackMinutes);
        Assert.True(final.DeterministicCaseFileUsable);
        Assert.True(final.OnlyAiSynthesisRemaining);
        Assert.Equal(AiSynthesisProgressState.Running, final.AiSynthesisState);

        var logs = final.CrumbSources.Single(source => source.Source == "victorialogs");
        Assert.Equal(CrumbSourceProgressState.Received, logs.RequestState);
        Assert.Equal(CrumbSourceHealth.Complete, logs.Health);
        Assert.Equal(1_320, logs.DurationMilliseconds);
        Assert.Equal(2, logs.CrumbCount);
        Assert.Equal(2, logs.Pass);
        Assert.Equal(120, logs.LookbackMinutes);

        var grafana = final.CrumbSources.Single(source => source.Source == "grafana");
        Assert.Equal(CrumbSourceProgressState.TimedOut, grafana.RequestState);
        Assert.Equal(CrumbSourceHealth.Unavailable, grafana.Health);
        Assert.Equal(1_800, grafana.DurationMilliseconds);

        Assert.Equal(["first-error", "second-error"], final.EarlyCrumbs.Select(crumb => crumb.Id));
        var json = JsonSerializer.Serialize(final);
        Assert.DoesNotContain("provenance", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("excerpt", json, StringComparison.OrdinalIgnoreCase);

        await tracker.SynthesisCompletedAsync(
            new AiSynthesis("complete", "done", [], [], [], "hash"),
            CancellationToken.None);
        var finalizing = snapshots[^1];
        Assert.Equal(CaseProgressPhase.Finalizing, finalizing.Phase);
        Assert.False(finalizing.OnlyAiSynthesisRemaining);
        Assert.Equal(AiSynthesisProgressState.Complete, finalizing.AiSynthesisState);
    }

    [Fact]
    public async Task BoundsResponderFacingSignalAndDiagnosticMetadata()
    {
        CaseProgress? snapshot = null;
        var revision = 0L;
        Task<CaseProgress?> Commit(
            CaseProgress progress,
            bool begin,
            CancellationToken cancellationToken)
        {
            snapshot = progress with { Revision = ++revision };
            return Task.FromResult<CaseProgress?>(snapshot);
        }

        var tracker = new CaseProgressTracker(
            BuildCase(),
            ["victorialogs"],
            30,
            new FixedTimeProvider(Now),
            Commit);
        await tracker.InitializeAsync(CancellationToken.None);

        var pass = new CrumbCollectionPass(1, 30, Now.AddMinutes(-30), Now);
        await tracker.PassStartedAsync(pass, ["victorialogs"], CancellationToken.None);
        await tracker.SourceCompletedAsync(
            pass,
            new CrumbSourceResult(
                "victorialogs",
                CrumbSourceHealth.Partial,
                Enumerable.Range(1, 8)
                    .Select(index => Crumb($"signal-{index}", new string('s', 400)))
                    .ToArray(),
                [],
                [],
                420,
                new string('d', 400)),
            CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal(5, snapshot.EarlyCrumbs.Count);
        Assert.All(snapshot.EarlyCrumbs, crumb => Assert.InRange(crumb.Summary.Length, 1, 300));
        Assert.Equal(300, Assert.Single(snapshot.CrumbSources).Diagnostic?.Length);
    }

    private static CaseRecord BuildCase() => new(
        Guid.NewGuid(),
        "PD-1",
        "payments",
        "recipe",
        "Payments failing",
        "high",
        PagerDutyIncidentState.Triggered,
        Now.AddMinutes(-5),
        Now,
        3,
        "collecting",
        false,
        null,
        "#cases",
        null,
        new Dictionary<string, string>());

    private static CrumbSourceResult Result(
        string source,
        long durationMilliseconds,
        IReadOnlyList<Crumb> crumbs) =>
        new(source, CrumbSourceHealth.Complete, crumbs, [], [], durationMilliseconds, null);

    private static Crumb Crumb(string id, string summary) => new(
        id,
        "victorialogs",
        Now,
        null,
        "first-error",
        "warning",
        summary,
        "full Crumb excerpt that must not enter progress",
        null,
        .95,
        new JsonObject { ["scope"] = new JsonObject { ["secret"] = "not-progress" } });

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
