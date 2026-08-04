using System.Text.Json.Nodes;
using Panko.Api.Domain;
using Panko.Api.Cases;
using Panko.Api.Crumbs;

namespace Panko.Api.Tests;

public sealed class DeterminismTests
{
    [Fact]
    public void CrumbDigest_IsStableAcrossConnectorCompletionOrder()
    {
        var caseRecord = BuildCase();
        var first = Result("z-source", "z crumb", 0.7);
        var second = Result("a-source", "a crumb", 0.9);

        var forward = LiteLlmSynthesizer.BuildDigest(caseRecord, [first, second], 12000);
        var reverse = LiteLlmSynthesizer.BuildDigest(caseRecord, [second, first], 12000);

        Assert.Equal(forward, reverse);
        Assert.True(forward.IndexOf("source=a-source", StringComparison.Ordinal)
            < forward.IndexOf("source=z-source", StringComparison.Ordinal));
    }

    [Fact]
    public void CrumbDigest_EnforcesCharacterBudget()
    {
        var digest = LiteLlmSynthesizer.BuildDigest(BuildCase(), [Result("source", new string('x', 400), 1)], 220);
        Assert.True(digest.Length <= 220);
    }

    [Fact]
    public void CrumbDigest_BoundsGitLabExpansionWithoutMergingFailedJobs()
    {
        var gitLabCrumbs = Enumerable.Range(1, 40)
            .Select(index => new Crumb(
                $"gitlab-{index}", "gitlab", DateTimeOffset.Parse("2026-07-11T10:00:00Z").AddSeconds(index),
                null, "pipeline-job-output", "critical", $"GitLab failed job {index} {new string('x', 120)}",
                new string('t', 1000), null, .98, new JsonObject()))
            .ToList();
        var gitLab = new CrumbSourceResult("gitlab", CrumbSourceHealth.Complete, gitLabCrumbs, [], [], 10, null);
        var nomad = Result("nomad", "Nomad allocation failed", .9);
        var logs = Result("victorialogs", "First application error", .8);

        var payload = LiteLlmSynthesizer.BuildDigestPayload(BuildCase(), [gitLab, nomad, logs], 4000);
        var digest = payload.Text;

        Assert.Contains("crumb_id=nomad-id", digest, StringComparison.Ordinal);
        Assert.Contains("crumb_id=victorialogs-id", digest, StringComparison.Ordinal);
        Assert.False(payload.SemanticCompressionApplied);
        Assert.DoesNotContain("representative_crumb_ids=", digest, StringComparison.Ordinal);
        Assert.InRange(payload.CrumbIds.Count(id => id.StartsWith("gitlab-", StringComparison.Ordinal)), 1, 39);
        Assert.True(digest.Length <= 4000);
    }

    [Fact]
    public void CrumbDigest_DoesNotCompressRepetitiveLogsWhenExactInputFits()
    {
        var crumbs = Enumerable.Range(1, 20)
            .Select(index => new Crumb(
                $"log-{index:D2}",
                "victorialogs",
                DateTimeOffset.Parse("2026-07-11T10:00:00Z").AddSeconds(index),
                null,
                "log-sample",
                "warning",
                $"Checkout timeout for request 550e8400-e29b-41d4-a716-{index:D12}.",
                null,
                null,
                .9,
                new JsonObject
                {
                    ["scope"] = new JsonObject { ["Name"] = "checkout-timeouts" }
                },
                ObjectType: "log-query",
                ObjectId: "checkout-timeouts"))
            .ToList();
        var result = new CrumbSourceResult("victorialogs", CrumbSourceHealth.Complete, crumbs, [], [], 10, null);

        var payload = LiteLlmSynthesizer.BuildDigestPayload(BuildCase(), [result], 12_000);

        Assert.Equal(20, payload.InputCrumbCount);
        Assert.Equal(20, payload.SemanticGroupCount);
        Assert.Equal(0, payload.SuppressedCrumbCount);
        Assert.Equal(20, payload.SerializedGroupCount);
        Assert.Equal(20, payload.CrumbIds.Count);
        Assert.False(payload.SemanticCompressionApplied);
        Assert.DoesNotContain("occurrences=20", payload.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void CrumbDigest_CompressesRepetitiveLogsOnlyAfterExactInputExceedsBudget()
    {
        var crumbs = Enumerable.Range(1, 20)
            .Select(index => new Crumb(
                $"log-{index:D2}",
                "victorialogs",
                DateTimeOffset.Parse("2026-07-11T10:00:00Z").AddSeconds(index),
                null,
                "log-sample",
                "warning",
                $"Checkout timeout for request 550e8400-e29b-41d4-a716-{index:D12} {new string('x', 100)}.",
                null,
                null,
                .9,
                new JsonObject
                {
                    ["scope"] = new JsonObject { ["Name"] = "checkout-timeouts" }
                },
                ObjectType: "log-query",
                ObjectId: "checkout-timeouts"))
            .ToList();
        var result = new CrumbSourceResult("victorialogs", CrumbSourceHealth.Complete, crumbs, [], [], 10, null);

        var payload = LiteLlmSynthesizer.BuildDigestPayload(BuildCase(), [result], 2_000);

        Assert.True(payload.SemanticCompressionApplied);
        Assert.Equal(1, payload.SemanticGroupCount);
        Assert.Equal(19, payload.SuppressedCrumbCount);
        Assert.Equal(1, payload.SerializedGroupCount);
        Assert.InRange(payload.CrumbIds.Count, 1, 3);
        Assert.Contains("occurrences=20", payload.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void CrumbDigest_UsesAvailableBudgetForRankedSummaries()
    {
        const int budget = 4000;
        var crumbs = Enumerable.Range(1, 40)
            .Select(index => new Crumb(
                $"crumb-{index:D2}",
                "source",
                DateTimeOffset.Parse("2026-07-11T10:00:00Z").AddSeconds(index),
                null,
                "test",
                "warning",
                $"Summary {index:D2} {new string('x', 180)}",
                null,
                null,
                .9,
                new JsonObject()))
            .ToList();
        var result = new CrumbSourceResult("source", CrumbSourceHealth.Complete, crumbs, [], [], 10, null);

        var payload = LiteLlmSynthesizer.BuildDigestPayload(BuildCase(), [result], budget);

        Assert.InRange(payload.Text.Length, budget * 80 / 100, budget);
        Assert.True(payload.CrumbIds.Count >= 10);
        Assert.DoesNotContain("CRUMB DETAILS", payload.Text, StringComparison.Ordinal);
        Assert.Contains(crumbs, crumb => !payload.CrumbIds.Contains(crumb.Id));
    }

    [Fact]
    public void CausalMarkers_AreCategorizedAndChronological()
    {
        var crumbs = new[]
        {
            Crumb("error", "victorialogs", "first-error", "2026-07-11T10:04:00Z", "First error", null),
            Crumb("merge", "gitlab", "merge-request-merged", "2026-07-11T10:01:00Z", "Alex merged MR !42", "Alex"),
            Crumb("deploy", "gitlab", "deployment", "2026-07-11T10:02:00Z", "CI deployed abc123 to production", "CI"),
            Crumb("nomad", "nomad", "workload-failure", "2026-07-11T10:03:00Z", "Allocation failed", null)
        };

        var markers = CaseFileComposer.BuildCausalMarkers(crumbs);

        Assert.Equal(new[] { "merge-request-merged", "deployment", "workload-failure", "first-error" },
            markers.Select(item => item.Category));
        Assert.Equal("Alex", markers[0].Actor);
    }

    [Fact]
    public void GitLabDiffs_ProduceImmutableLineAnchors()
    {
        using var json = System.Text.Json.JsonDocument.Parse("""
            [{
              "new_path": "src/Payments.Api/Handler.cs",
              "old_path": "src/Payments.Api/Handler.cs",
              "diff": "@@ -40,2 +42,3 @@ public void Handle()\n context.Start();\n+throw new TimeoutException();\n+metrics.Fail();\n"
            }]
            """);

        var references = GitLabCrumbSource.ExtractCodeReferences(
            "https://gitlab.example", "platform/payments", "abcdef123456", json.RootElement.EnumerateArray().ToList());

        var reference = Assert.Single(references);
        Assert.Equal(43, reference.StartLine);
        Assert.Equal(44, reference.EndLine);
        Assert.Equal("src/Payments.Api/Handler.cs", reference.Path);
        Assert.EndsWith("/platform/payments/-/blob/abcdef123456/src/Payments.Api/Handler.cs#L43-44", reference.Url);
    }

    [Fact]
    public void CrumbDigest_ExposesOnlyPrecomputedCodeReferenceIds()
    {
        var reference = new CodeReference("code-ref-1", "platform/payments", "abcdef", "src/Handler.cs", 42, 44,
            "https://gitlab/line", "+throw new TimeoutException();");
        var crumb = Crumb("diff", "gitlab", "code-diff", "2026-07-11T10:00:00Z", "Changed handler", "Alex") with
        {
            CodeReferences = [reference]
        };
        var result = new CrumbSourceResult("gitlab", CrumbSourceHealth.Complete, [crumb], [], [], 10, null);

        var digest = LiteLlmSynthesizer.BuildDigest(BuildCase(), [result], 12000);

        Assert.Contains("crumb_id=diff", digest);
        Assert.Contains("code_ref=code-ref-1", digest);
        Assert.Contains("src/Handler.cs#L42-L44", digest);
    }

    [Fact]
    public void CrumbDigest_UsesCanonicalDuplicateForCitationValidation()
    {
        var reference = new CodeReference("code-ref-1", "platform/payments", "abcdef", "src/Handler.cs", 42, 44,
            "https://gitlab/line", "+throw new TimeoutException();");
        var weaker = Crumb("diff", "gitlab", "code-diff", "2026-07-11T10:00:00Z", "Changed handler", "Alex");
        var stronger = weaker with { Severity = "critical", Confidence = .99, CodeReferences = [reference] };
        var first = new CrumbSourceResult("gitlab", CrumbSourceHealth.Complete, [weaker], [], [], 10, null);
        var second = new CrumbSourceResult("gitlab", CrumbSourceHealth.Complete, [stronger], [], [], 10, null);

        var forward = LiteLlmSynthesizer.BuildDigestPayload(BuildCase(), [first, second], 12_000);
        var reverse = LiteLlmSynthesizer.BuildDigestPayload(BuildCase(), [second, first], 12_000);

        Assert.Equal(forward.Text, reverse.Text);
        Assert.Equal(forward.CrumbIds, forward.CrumbCatalog.Keys.ToHashSet(StringComparer.Ordinal));
        Assert.Equal("critical", forward.CrumbCatalog["diff"].Severity);
        Assert.Equal("code-ref-1", Assert.Single(forward.CrumbCatalog["diff"].CodeReferences!).Id);
        Assert.Contains("code-ref-1", forward.CodeReferenceIds);
    }

    private static CaseRecord BuildCase() => new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"), "PD-1", "payments", "recipe", "Case title",
        "high", PagerDutyIncidentState.Triggered, DateTimeOffset.Parse("2026-07-11T10:00:00Z"),
        DateTimeOffset.Parse("2026-07-11T10:00:01Z"), 1, "collecting", false, null, "#cases", null,
        new Dictionary<string, string>());

    private static CrumbSourceResult Result(string source, string summary, double confidence)
    {
        var crumb = new Crumb(source + "-id", source, DateTimeOffset.Parse("2026-07-11T10:00:00Z"), null,
            "test", "warning", summary, null, null, confidence, new JsonObject());
        return new CrumbSourceResult(source, CrumbSourceHealth.Complete, [crumb], [], [], 10, null);
    }

    private static Crumb Crumb(
        string id, string source, string category, string occurredAt, string summary, string? actor) =>
        new(id, source, DateTimeOffset.Parse(occurredAt), null, category, "info", summary, null, null, 0.9,
            new JsonObject(), actor);
}
