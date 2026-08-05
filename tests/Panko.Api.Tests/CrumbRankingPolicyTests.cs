using System.Text.Json.Nodes;
using Panko.Api.Domain;
using Panko.Api.Cases;

namespace Panko.Api.Tests;

public sealed class CrumbRankingPolicyTests
{
    private static readonly DateTimeOffset TriggeredAt = DateTimeOffset.Parse("2026-07-11T10:00:00Z");

    [Fact]
    public void HardFailureOutranksCancelledFanoutAndPagerDutyContext()
    {
        var crumbs = Enumerable.Range(1, 20)
            .Select(index => Crumb(
                $"cancel-{index}", "gitlab", "pipeline-job-output", "warning",
                TriggeredAt.AddSeconds(index), $"Cancelled downstream job {index}", 0.95,
                pipelineId: "42"))
            .Append(Crumb("pagerduty", "pagerduty", "pagerduty-incident", "critical", TriggeredAt, "PagerDuty incident triggered", 1))
            .Append(Crumb("failed", "gitlab", "pipeline-job-output", "critical",
                TriggeredAt.AddSeconds(-10), "Root build step failed", 0.95, pipelineId: "42"));

        var ranked = CrumbRankingPolicy.Rank(crumbs, TriggeredAt);

        Assert.Equal("failed", ranked[0].Id);
        Assert.True(ranked.Index().Single(item => item.Item.Id == "pagerduty").Index > 1);
    }

    [Fact]
    public void TopCrumbsKeepPipelineSiblingsFromCrowdingIndependentSources()
    {
        var crumbs = new[]
        {
            Crumb("job-1", "gitlab", "pipeline-job-output", "critical", TriggeredAt, "Compile failed", .95, "42"),
            Crumb("job-2", "gitlab", "pipeline-job-output", "critical", TriggeredAt.AddSeconds(1), "Test failed", .95, "42"),
            Crumb("nomad", "nomad", "workload-failure", "warning", TriggeredAt.AddSeconds(2), "Allocation failed", .95),
            Crumb("logs", "victorialogs", "first-error", "warning", TriggeredAt.AddSeconds(3), "First error", .8),
            Crumb("metric", "grafana", "metric", "warning", TriggeredAt.AddSeconds(4), "Latency high", .9)
        };

        var selected = CrumbRankingPolicy.SelectTopCrumbs(crumbs, TriggeredAt, 3);

        Assert.Equal(3, selected.Count);
        Assert.Single(selected, item => item.Source == "gitlab");
        Assert.Contains(selected, item => item.Source == "nomad");
        Assert.Contains(selected, item => item.Source == "victorialogs");
    }

    [Fact]
    public void AllowedFailuresAndCancellationFanoutRemainContextRatherThanTopCrumbs()
    {
        var allowed = Crumb("allowed", "gitlab", "pipeline-job-output", "warning", TriggeredAt,
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
        var canceled = Crumb("canceled", "gitlab", "pipeline-job-output", "warning", TriggeredAt,
            "Pipeline 42 canceled 100 downstream jobs", .9, "42") with
        {
            ObjectType = "pipeline-job-cancellations"
        };

        Assert.False(CrumbRankingPolicy.IsHighSignal(allowed));
        Assert.False(CrumbRankingPolicy.IsHighSignal(canceled));
        Assert.Empty(CrumbRankingPolicy.SelectTopCrumbs([allowed, canceled], TriggeredAt, 3));
    }

    [Fact]
    public void SynthesisPreservesSourceDiversityBeforeNoisySourceExpansion()
    {
        var gitLab = Enumerable.Range(1, 20)
            .Select(index => Crumb($"gitlab-{index}", "gitlab", "pipeline-job-output", "critical",
                TriggeredAt.AddSeconds(index), $"GitLab failure {index}", .99, "42"));
        var crumbs = gitLab.Concat(new[]
        {
            Crumb("nomad", "nomad", "workload-failure", "warning", TriggeredAt, "Nomad failure", .8),
            Crumb("logs", "victorialogs", "first-error", "warning", TriggeredAt, "First error", .8),
            Crumb("metric", "grafana", "metric", "warning", TriggeredAt, "Metric anomaly", .8),
            Crumb("pagerduty-incident", "pagerduty", "pagerduty-incident", "critical", TriggeredAt, "PagerDuty incident", 1)
        });

        var ordered = CrumbRankingPolicy.OrderForSynthesis(crumbs, TriggeredAt);
        var firstExpansion = ordered.Index().First(item => item.Item.Id == "gitlab-3").Index;

        Assert.All(new[] { "nomad", "logs", "metric", "pagerduty-incident" }, id =>
            Assert.True(ordered.Index().Single(item => item.Item.Id == id).Index < firstExpansion));
    }

    [Fact]
    public void PipelineStatusComesFromStructuredProvenanceNotBranchText()
    {
        var successful = Crumb(
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

        Assert.False(CrumbRankingPolicy.IsHighSignal(successful));
    }

    [Fact]
    public void FirstHardFailureOutranksCloserCascadingSibling()
    {
        var root = Crumb(
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
        var cascade = Crumb(
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

        Assert.Equal("root", CrumbRankingPolicy.Rank([cascade, root], TriggeredAt)[0].Id);
    }

    [Fact]
    public void CaseFileCountsCrumbGroupsInsteadOfRawPipelineJobPoints()
    {
        var jobs = Enumerable.Range(1, 10)
            .Select(index => Crumb($"job-{index}", "gitlab", "pipeline-job-output", "critical",
                TriggeredAt.AddSeconds(index), $"Job {index} failed", .95, "42"))
            .ToList();
        var sources = new Panko.Api.Crumbs.CrumbSourceRegistry(
            Array.Empty<Panko.Api.Crumbs.ICrumbSourceAdapter>(),
            TestConfiguration.CrumbSources());
        var composer = new CaseFileComposer(TimeProvider.System, sources);
        var caseRecord = new CaseRecord(
            Guid.NewGuid(), "PD-1", "payments", "recipe", "Payments failing", "high",
            PagerDutyIncidentState.Triggered, TriggeredAt, TriggeredAt, 1, "collecting", false, null,
            "#cases", null, new Dictionary<string, string>());
        var result = new CrumbSourceResult("gitlab", CrumbSourceHealth.Complete, jobs, [], [], 1, null);

        var caseFile = composer.Compose(
            caseRecord, new Recipe { Id = "recipe" }, "v1", [result], null,
            new AiSynthesis("disabled", null, [], [], [], null));

        Assert.Contains("Found 1 high-signal Crumb group across 1 source.", caseFile.DeterministicSummary,
            StringComparison.Ordinal);
    }

    private static Crumb Crumb(
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
        return new Crumb(id, source, at, null, category, severity, summary, null,
            $"https://example.test/{id}", confidence, provenance, ObjectType: "test", ObjectId: id);
    }
}
