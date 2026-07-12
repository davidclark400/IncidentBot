using System.Text.Json.Nodes;
using IncidentBot.Api.Domain;
using IncidentBot.Api.Incidents;

namespace IncidentBot.Api.Tests;

public sealed class ReportBoundaryTests
{
    [Fact]
    public void TimelineRetention_ReservesNewestHighSignalAndIncidentNearEvents()
    {
        var triggeredAt = DateTimeOffset.Parse("2026-07-11T10:00:00Z");
        var ordinary = Enumerable.Range(0, 300)
            .Select(index => new TimelineCandidate(
                triggeredAt.AddMinutes(index - 200),
                "source",
                "event",
                $"ordinary-{index}",
                "info",
                null));
        var highSignal = new TimelineCandidate(
            triggeredAt.AddDays(-10),
            "nomad",
            "allocation-state",
            "critical allocation failure",
            "critical",
            null);

        var retained = ReportComposer.RetainTimeline(ordinary.Append(highSignal), triggeredAt, 10);

        Assert.Equal(10, retained.Count);
        Assert.Contains(retained, item => item.Summary == "ordinary-299");
        Assert.Contains(retained, item => item.Summary == "critical allocation failure");
        Assert.Contains(retained, item => item.Summary == "ordinary-200");
        Assert.DoesNotContain(retained, item => item.Summary == "ordinary-0");
        Assert.Equal(retained.OrderBy(item => item.OccurredAt), retained);
    }

    [Fact]
    public void SlackEvidenceSections_StayWithinLimitsAfterEscaping()
    {
        var at = DateTimeOffset.Parse("2026-07-11T10:00:00Z");
        var findings = Enumerable.Range(1, 3)
            .Select(index => new EvidenceFinding(
                $"finding-{index}", "gitlab", at, null, "pipeline-job-output", "critical",
                new string('&', 2_000), null, null, .99, new JsonObject()))
            .ToList();
        var causalEvents = Enumerable.Range(1, 5)
            .Select(index => new CausalEvent(
                $"causal-{index}", "pipeline-job-output", new string('<', 500), at,
                new string('&', 2_000), "gitlab", $"finding-{index}", null, null, null, null, []))
            .ToList();

        var signalsText = SlackPublisher.BuildTopSignalsText(findings);
        var causalText = SlackPublisher.BuildCausalSequenceText(causalEvents);

        Assert.InRange(signalsText.Length, 1, 3000);
        Assert.InRange(causalText.Length, 1, 3000);
        Assert.Contains("&amp;", signalsText, StringComparison.Ordinal);
        Assert.Contains("&lt;", causalText, StringComparison.Ordinal);
        Assert.Contains("&amp;", causalText, StringComparison.Ordinal);
    }
}
