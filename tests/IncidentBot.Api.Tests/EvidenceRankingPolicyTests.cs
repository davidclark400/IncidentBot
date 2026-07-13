using System.Text.Json.Nodes;
using IncidentBot.Api.Domain;
using IncidentBot.Api.Incidents;

namespace IncidentBot.Api.Tests;

public sealed class EvidenceRankingPolicyTests
{
    private static readonly DateTimeOffset TriggeredAt = DateTimeOffset.Parse("2026-07-11T10:00:00Z");

    [Fact]
    public void HardFailureOutranksCancelledFanoutAndPagerDutyContext()
    {
        var evidence = Enumerable.Range(1, 20)
            .Select(index => Finding(
                $"cancel-{index}", "gitlab", "pipeline-job-output", "warning",
                TriggeredAt.AddSeconds(index), $"Cancelled downstream job {index}", 0.95,
                pipelineId: "42"))
            .Append(Finding("pagerduty", "pagerduty", "incident", "critical", TriggeredAt, "Incident triggered", 1))
            .Append(Finding("failed", "gitlab", "pipeline-job-output", "critical",
                TriggeredAt.AddSeconds(-10), "Root build step failed", 0.95, pipelineId: "42"));

        var ranked = EvidenceRankingPolicy.Rank(evidence, TriggeredAt);

        Assert.Equal("failed", ranked[0].Id);
        Assert.True(ranked.Index().Single(item => item.Item.Id == "pagerduty").Index > 1);
    }

    [Fact]
    public void TopSignalsKeepPipelineSiblingsFromCrowdingIndependentSources()
    {
        var evidence = new[]
        {
            Finding("job-1", "gitlab", "pipeline-job-output", "critical", TriggeredAt, "Compile failed", .95, "42"),
            Finding("job-2", "gitlab", "pipeline-job-output", "critical", TriggeredAt.AddSeconds(1), "Test failed", .95, "42"),
            Finding("nomad", "nomad", "workload-failure", "warning", TriggeredAt.AddSeconds(2), "Allocation failed", .95),
            Finding("logs", "victorialogs", "first-error", "warning", TriggeredAt.AddSeconds(3), "First error", .8),
            Finding("metric", "grafana", "metric", "warning", TriggeredAt.AddSeconds(4), "Latency high", .9)
        };

        var selected = EvidenceRankingPolicy.SelectTopSignals(evidence, TriggeredAt, 3);

        Assert.Equal(3, selected.Count);
        Assert.Single(selected, item => item.Source == "gitlab");
        Assert.Contains(selected, item => item.Source == "nomad");
        Assert.Contains(selected, item => item.Source == "victorialogs");
    }

    [Fact]
    public void AllowedFailuresAndCancellationFanoutRemainContextRatherThanTopSignals()
    {
        var allowed = Finding("allowed", "gitlab", "pipeline-job-output", "warning", TriggeredAt,
            "Flaky tests failed (allowed to fail)", .9, "42") with
        {
            Provenance = new JsonObject
            {
                ["scope"] = new JsonObject
                {
                    ["project"] = "platform/payments",
                    ["pipelineId"] = "42",
                    ["allowFailure"] = true
                }
            }
        };
        var canceled = Finding("canceled", "gitlab", "pipeline-job-output", "warning", TriggeredAt,
            "Pipeline 42 canceled 100 downstream jobs", .9, "42") with
        {
            ObjectType = "pipeline-job-cancellations"
        };

        Assert.False(EvidenceRankingPolicy.IsHighSignal(allowed));
        Assert.False(EvidenceRankingPolicy.IsHighSignal(canceled));
        Assert.Empty(EvidenceRankingPolicy.SelectTopSignals([allowed, canceled], TriggeredAt, 3));
    }

    [Fact]
    public void SynthesisPreservesSourceDiversityBeforeNoisySourceExpansion()
    {
        var gitLab = Enumerable.Range(1, 20)
            .Select(index => Finding($"gitlab-{index}", "gitlab", "pipeline-job-output", "critical",
                TriggeredAt.AddSeconds(index), $"GitLab failure {index}", .99, "42"));
        var evidence = gitLab.Concat(new[]
        {
            Finding("nomad", "nomad", "workload-failure", "warning", TriggeredAt, "Nomad failure", .8),
            Finding("logs", "victorialogs", "first-error", "warning", TriggeredAt, "First error", .8),
            Finding("metric", "grafana", "metric", "warning", TriggeredAt, "Metric anomaly", .8),
            Finding("incident", "pagerduty", "incident", "critical", TriggeredAt, "Incident", 1)
        });

        var ordered = EvidenceRankingPolicy.OrderForSynthesis(evidence, TriggeredAt);
        var firstExpansion = ordered.Index().First(item => item.Item.Id == "gitlab-3").Index;

        Assert.All(new[] { "nomad", "logs", "metric", "incident" }, id =>
            Assert.True(ordered.Index().Single(item => item.Item.Id == id).Index < firstExpansion));
    }

    [Fact]
    public void PipelineStatusComesFromStructuredProvenanceNotBranchText()
    {
        var successful = Finding(
            "successful", "gitlab", "pipeline", "info", TriggeredAt,
            "Pipeline 7 on fix/failed-deploy is success", .9, "7") with
        {
            Provenance = new JsonObject
            {
                ["scope"] = new JsonObject
                {
                    ["project"] = "platform/payments",
                    ["pipelineId"] = "7",
                    ["status"] = "success"
                }
            },
            ObjectType = "pipeline",
            ObjectId = "7"
        };

        Assert.False(EvidenceRankingPolicy.IsHighSignal(successful));
    }

    [Fact]
    public void FirstHardFailureOutranksCloserCascadingSibling()
    {
        var root = Finding(
            "root", "gitlab", "pipeline-job-output", "critical",
            TriggeredAt.AddMinutes(-10), "Compile failed", .98, "42") with
        {
            Provenance = new JsonObject
            {
                ["scope"] = new JsonObject
                {
                    ["project"] = "platform/payments",
                    ["pipelineId"] = "42",
                    ["status"] = "failed",
                    ["allowFailure"] = false,
                    ["failureOrdinal"] = 1,
                    ["firstHardFailure"] = true
                }
            }
        };
        var cascade = Finding(
            "cascade", "gitlab", "pipeline-job-output", "critical",
            TriggeredAt.AddSeconds(-1), "Deploy failed", .98, "42") with
        {
            Provenance = new JsonObject
            {
                ["scope"] = new JsonObject
                {
                    ["project"] = "platform/payments",
                    ["pipelineId"] = "42",
                    ["status"] = "failed",
                    ["allowFailure"] = false,
                    ["failureOrdinal"] = 2,
                    ["firstHardFailure"] = false
                }
            }
        };

        Assert.Equal("root", EvidenceRankingPolicy.Rank([cascade, root], TriggeredAt)[0].Id);
    }

    [Fact]
    public void ReportCountsEvidenceGroupsInsteadOfRawPipelineJobPoints()
    {
        var jobs = Enumerable.Range(1, 10)
            .Select(index => Finding($"job-{index}", "gitlab", "pipeline-job-output", "critical",
                TriggeredAt.AddSeconds(index), $"Job {index} failed", .95, "42"))
            .ToList();
        var sources = new IncidentBot.Api.Connectors.EvidenceSourceRegistry(
            Array.Empty<IncidentBot.Api.Connectors.IIncidentEvidenceConnector>(),
            TestConfiguration.EvidenceSources());
        var composer = new ReportComposer(TimeProvider.System, sources);
        var incident = new IncidentRecord(
            Guid.NewGuid(), "PD-1", "payments", "profile", "Payments failing", "high",
            IncidentState.Triggered, TriggeredAt, TriggeredAt, 1, "collecting", false, null,
            "#incidents", null, new Dictionary<string, string>());
        var result = new ConnectorResult("gitlab", SourceHealth.Complete, jobs, [], [], 1, null);

        var report = composer.Compose(
            incident, new InvestigationProfile { Id = "profile" }, "v1", [result], null,
            new AiSynthesis("disabled", null, [], [], [], null));

        Assert.Contains("Found 1 high-signal evidence group across 1 source.", report.DeterministicSummary,
            StringComparison.Ordinal);
    }

    private static EvidenceFinding Finding(
        string id,
        string source,
        string category,
        string severity,
        DateTimeOffset at,
        string summary,
        double confidence,
        string? pipelineId = null)
    {
        var provenance = new JsonObject();
        if (pipelineId is not null)
        {
            provenance["scope"] = new JsonObject { ["project"] = "platform/payments", ["pipelineId"] = pipelineId };
        }
        return new EvidenceFinding(id, source, at, null, category, severity, summary, null,
            $"https://example.test/{id}", confidence, provenance, ObjectType: "test", ObjectId: id);
    }
}
