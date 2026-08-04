using System.Text.Json.Nodes;
using Panko.Api.Domain;
using Panko.Api.Cases;

namespace Panko.Api.Tests;

public sealed class CaseFileBoundaryTests
{
    [Fact]
    public void TrailRetention_ReservesNewestHighSignalAndCaseNearEvents()
    {
        var triggeredAt = DateTimeOffset.Parse("2026-07-11T10:00:00Z");
        var ordinary = Enumerable.Range(0, 300)
            .Select(index => new TrailCandidate(
                triggeredAt.AddMinutes(index - 200),
                "source",
                "event",
                $"ordinary-{index}",
                "info",
                null));
        var highSignal = new TrailCandidate(
            triggeredAt.AddDays(-10),
            "nomad",
            "allocation-state",
            "critical allocation failure",
            "critical",
            null);

        var retained = CaseFileComposer.RetainTrail(ordinary.Append(highSignal), triggeredAt, 10);

        Assert.Equal(10, retained.Count);
        Assert.Contains(retained, item => item.Summary == "ordinary-299");
        Assert.Contains(retained, item => item.Summary == "critical allocation failure");
        Assert.Contains(retained, item => item.Summary == "ordinary-200");
        Assert.DoesNotContain(retained, item => item.Summary == "ordinary-0");
        Assert.Equal(retained.OrderBy(item => item.OccurredAt), retained);
    }

    [Fact]
    public void SlackCrumbSections_StayWithinLimitsAfterEscaping()
    {
        var at = DateTimeOffset.Parse("2026-07-11T10:00:00Z");
        var crumbs = Enumerable.Range(1, 3)
            .Select(index => new Crumb(
                $"crumb-{index}", "gitlab", at, null, "pipeline-job-output", "critical",
                new string('&', 2_000), null, null, .99, new JsonObject()))
            .ToList();
        var causalMarkers = Enumerable.Range(1, 5)
            .Select(index => new CausalMarker(
                $"causal-{index}", "pipeline-job-output", new string('<', 500), at,
                new string('&', 2_000), "gitlab", $"crumb-{index}", null, null, null, null, []))
            .ToList();

        var crumbsText = SlackPublisher.BuildTopCrumbsText(crumbs);
        var causalText = SlackPublisher.BuildCausalSequenceText(causalMarkers);

        Assert.InRange(crumbsText.Length, 1, 3000);
        Assert.InRange(causalText.Length, 1, 3000);
        Assert.Contains("&amp;", crumbsText, StringComparison.Ordinal);
        Assert.Contains("&lt;", causalText, StringComparison.Ordinal);
        Assert.Contains("&amp;", causalText, StringComparison.Ordinal);
    }
}
