using System.Text.Json.Nodes;
using IncidentBot.Api.Domain;
using IncidentBot.Api.Incidents;
using IncidentBot.Api.Connectors;

namespace IncidentBot.Api.Tests;

public sealed class DeterminismTests
{
    [Fact]
    public void EvidenceDigest_IsStableAcrossConnectorCompletionOrder()
    {
        var incident = Incident();
        var first = Result("z-source", "z finding", 0.7);
        var second = Result("a-source", "a finding", 0.9);

        var forward = LiteLlmSynthesizer.BuildDigest(incident, [first, second], 12000);
        var reverse = LiteLlmSynthesizer.BuildDigest(incident, [second, first], 12000);

        Assert.Equal(forward, reverse);
        Assert.True(forward.IndexOf("source=a-source", StringComparison.Ordinal)
            < forward.IndexOf("source=z-source", StringComparison.Ordinal));
    }

    [Fact]
    public void EvidenceDigest_EnforcesCharacterBudget()
    {
        var digest = LiteLlmSynthesizer.BuildDigest(Incident(), [Result("source", new string('x', 400), 1)], 220);
        Assert.True(digest.Length <= 220);
    }

    [Fact]
    public void EvidenceDigest_BoundsGitLabExpansionWithoutMergingFailedJobs()
    {
        var gitLabFindings = Enumerable.Range(1, 40)
            .Select(index => new EvidenceFinding(
                $"gitlab-{index}", "gitlab", DateTimeOffset.Parse("2026-07-11T10:00:00Z").AddSeconds(index),
                null, "pipeline-job-output", "critical", $"GitLab failed job {index} {new string('x', 120)}",
                new string('t', 1000), null, .98, new JsonObject()))
            .ToList();
        var gitLab = new ConnectorResult("gitlab", SourceHealth.Complete, gitLabFindings, [], [], 10, null);
        var nomad = Result("nomad", "Nomad allocation failed", .9);
        var logs = Result("victorialogs", "First application error", .8);

        var payload = LiteLlmSynthesizer.BuildDigestPayload(Incident(), [gitLab, nomad, logs], 4000);
        var digest = payload.Text;

        Assert.Contains("evidence_id=nomad-id", digest, StringComparison.Ordinal);
        Assert.Contains("evidence_id=victorialogs-id", digest, StringComparison.Ordinal);
        Assert.False(payload.SemanticCompressionApplied);
        Assert.DoesNotContain("representative_evidence_ids=", digest, StringComparison.Ordinal);
        Assert.InRange(payload.EvidenceIds.Count(id => id.StartsWith("gitlab-", StringComparison.Ordinal)), 1, 39);
        Assert.True(digest.Length <= 4000);
    }

    [Fact]
    public void EvidenceDigest_DoesNotCompressRepetitiveLogsWhenExactInputFits()
    {
        var findings = Enumerable.Range(1, 20)
            .Select(index => new EvidenceFinding(
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
        var result = new ConnectorResult("victorialogs", SourceHealth.Complete, findings, [], [], 10, null);

        var payload = LiteLlmSynthesizer.BuildDigestPayload(Incident(), [result], 12_000);

        Assert.Equal(20, payload.InputFindingCount);
        Assert.Equal(20, payload.SemanticGroupCount);
        Assert.Equal(0, payload.SuppressedFindingCount);
        Assert.Equal(20, payload.SerializedGroupCount);
        Assert.Equal(20, payload.EvidenceIds.Count);
        Assert.False(payload.SemanticCompressionApplied);
        Assert.DoesNotContain("occurrences=20", payload.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void EvidenceDigest_CompressesRepetitiveLogsOnlyAfterExactInputExceedsBudget()
    {
        var findings = Enumerable.Range(1, 20)
            .Select(index => new EvidenceFinding(
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
        var result = new ConnectorResult("victorialogs", SourceHealth.Complete, findings, [], [], 10, null);

        var payload = LiteLlmSynthesizer.BuildDigestPayload(Incident(), [result], 2_000);

        Assert.True(payload.SemanticCompressionApplied);
        Assert.Equal(1, payload.SemanticGroupCount);
        Assert.Equal(19, payload.SuppressedFindingCount);
        Assert.Equal(1, payload.SerializedGroupCount);
        Assert.InRange(payload.EvidenceIds.Count, 1, 3);
        Assert.Contains("occurrences=20", payload.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void EvidenceDigest_UsesAvailableBudgetForRankedSummaries()
    {
        const int budget = 4000;
        var findings = Enumerable.Range(1, 40)
            .Select(index => new EvidenceFinding(
                $"finding-{index:D2}",
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
        var result = new ConnectorResult("source", SourceHealth.Complete, findings, [], [], 10, null);

        var payload = LiteLlmSynthesizer.BuildDigestPayload(Incident(), [result], budget);

        Assert.InRange(payload.Text.Length, budget * 80 / 100, budget);
        Assert.True(payload.EvidenceIds.Count >= 10);
        Assert.DoesNotContain("EVIDENCE DETAILS", payload.Text, StringComparison.Ordinal);
        Assert.Contains(findings, finding => !payload.EvidenceIds.Contains(finding.Id));
    }

    [Fact]
    public void CausalEvents_AreCategorizedAndChronological()
    {
        var evidence = new[]
        {
            Finding("error", "victorialogs", "first-error", "2026-07-11T10:04:00Z", "First error", null),
            Finding("merge", "gitlab", "merge-request-merged", "2026-07-11T10:01:00Z", "Alex merged MR !42", "Alex"),
            Finding("deploy", "gitlab", "deployment", "2026-07-11T10:02:00Z", "CI deployed abc123 to production", "CI"),
            Finding("nomad", "nomad", "workload-failure", "2026-07-11T10:03:00Z", "Allocation failed", null)
        };

        var events = ReportComposer.BuildCausalEvents(evidence);

        Assert.Equal(new[] { "merge-request-merged", "deployment", "workload-failure", "first-error" },
            events.Select(item => item.Category));
        Assert.Equal("Alex", events[0].Actor);
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

        var references = GitLabEvidenceConnector.ExtractCodeReferences(
            "https://gitlab.example", "platform/payments", "abcdef123456", json.RootElement.EnumerateArray().ToList());

        var reference = Assert.Single(references);
        Assert.Equal(43, reference.StartLine);
        Assert.Equal(44, reference.EndLine);
        Assert.Equal("src/Payments.Api/Handler.cs", reference.Path);
        Assert.EndsWith("/platform/payments/-/blob/abcdef123456/src/Payments.Api/Handler.cs#L43-44", reference.Url);
    }

    [Fact]
    public void EvidenceDigest_ExposesOnlyPrecomputedCodeReferenceIds()
    {
        var reference = new CodeReference("code-ref-1", "platform/payments", "abcdef", "src/Handler.cs", 42, 44,
            "https://gitlab/line", "+throw new TimeoutException();");
        var finding = Finding("diff", "gitlab", "code-diff", "2026-07-11T10:00:00Z", "Changed handler", "Alex") with
        {
            CodeReferences = [reference]
        };
        var result = new ConnectorResult("gitlab", SourceHealth.Complete, [finding], [], [], 10, null);

        var digest = LiteLlmSynthesizer.BuildDigest(Incident(), [result], 12000);

        Assert.Contains("evidence_id=diff", digest);
        Assert.Contains("code_ref=code-ref-1", digest);
        Assert.Contains("src/Handler.cs#L42-L44", digest);
    }

    [Fact]
    public void EvidenceDigest_UsesCanonicalDuplicateForCitationValidation()
    {
        var reference = new CodeReference("code-ref-1", "platform/payments", "abcdef", "src/Handler.cs", 42, 44,
            "https://gitlab/line", "+throw new TimeoutException();");
        var weaker = Finding("diff", "gitlab", "code-diff", "2026-07-11T10:00:00Z", "Changed handler", "Alex");
        var stronger = weaker with { Severity = "critical", Confidence = .99, CodeReferences = [reference] };
        var first = new ConnectorResult("gitlab", SourceHealth.Complete, [weaker], [], [], 10, null);
        var second = new ConnectorResult("gitlab", SourceHealth.Complete, [stronger], [], [], 10, null);

        var forward = LiteLlmSynthesizer.BuildDigestPayload(Incident(), [first, second], 12_000);
        var reverse = LiteLlmSynthesizer.BuildDigestPayload(Incident(), [second, first], 12_000);

        Assert.Equal(forward.Text, reverse.Text);
        Assert.Equal(forward.EvidenceIds, forward.EvidenceCatalog.Keys.ToHashSet(StringComparer.Ordinal));
        Assert.Equal("critical", forward.EvidenceCatalog["diff"].Severity);
        Assert.Equal("code-ref-1", Assert.Single(forward.EvidenceCatalog["diff"].CodeReferences!).Id);
        Assert.Contains("code-ref-1", forward.CodeReferenceIds);
    }

    private static IncidentRecord Incident() => new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"), "PD-1", "payments", "profile", "Incident title",
        "high", IncidentState.Triggered, DateTimeOffset.Parse("2026-07-11T10:00:00Z"),
        DateTimeOffset.Parse("2026-07-11T10:00:01Z"), 1, "collecting", false, null, "#incidents", null,
        new Dictionary<string, string>());

    private static ConnectorResult Result(string source, string summary, double confidence)
    {
        var finding = new EvidenceFinding(source + "-id", source, DateTimeOffset.Parse("2026-07-11T10:00:00Z"), null,
            "test", "warning", summary, null, null, confidence, new JsonObject());
        return new ConnectorResult(source, SourceHealth.Complete, [finding], [], [], 10, null);
    }

    private static EvidenceFinding Finding(
        string id, string source, string category, string occurredAt, string summary, string? actor) =>
        new(id, source, DateTimeOffset.Parse(occurredAt), null, category, "info", summary, null, null, 0.9,
            new JsonObject(), actor);
}
