using IncidentBot.Api.Demo;

namespace IncidentBot.Api.Tests;

public sealed class DemoTests
{
    [Fact]
    public void ReplayBuildsProductionShapedVersionsAndCitedDiagnosis()
    {
        var store = new DemoIncidentStore(TimeProvider.System);
        var reset = store.Reset();

        Assert.Equal(1, reset.Report.Version);
        Assert.Equal("collecting", reset.Report.Status);
        Assert.Empty(reset.Report.CausalEvents!);

        var final = store.Advance(reset.Generation, 6)!;

        Assert.Equal(7, final.Version);
        Assert.Equal("ready", final.Status);
        Assert.Equal(new[]
        {
            "merge-request-created", "merge-request-merged", "deployment", "workload-failure", "first-error", "pipeline"
        }, final.CausalEvents!.Select(item => item.Category));
        var diagnosis = Assert.Single(final.Ai.Diagnoses!);
        var reference = Assert.Single(diagnosis.CodeReferences);
        Assert.Equal(43, reference.StartLine);
        Assert.Equal(44, reference.EndLine);
    }

    [Fact]
    public void StaleReplayCannotOverwriteANewerReset()
    {
        var store = new DemoIncidentStore(TimeProvider.System);
        var oldReplay = store.Reset();
        var newReplay = store.Reset();

        Assert.Null(store.Advance(oldReplay.Generation, 4));
        Assert.NotNull(store.Advance(newReplay.Generation, 1));
    }
}
