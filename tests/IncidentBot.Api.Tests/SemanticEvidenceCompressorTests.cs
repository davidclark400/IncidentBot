using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentBot.Api.Domain;
using IncidentBot.Api.Incidents.Compression;

namespace IncidentBot.Api.Tests;

public sealed class SemanticEvidenceCompressorTests
{
    private static readonly DateTimeOffset TriggeredAt = DateTimeOffset.Parse("2026-07-11T10:00:00Z");

    [Fact]
    public void VictoriaLogsCollapsesDynamicLogVariantsButPreservesFirstErrorAnchor()
    {
        var firstError = Finding(
            "first-error",
            "victorialogs",
            "first-error",
            TriggeredAt.AddSeconds(1),
            "First observed checkout timeout: request 550e8400-e29b-41d4-a716-446655440000 failed.",
            Scope(("Name", "checkout-timeouts")),
            objectType: "log-query",
            objectId: "checkout-timeouts");
        var samples = new[]
        {
            Finding("log-1", "victorialogs", "log-sample", TriggeredAt.AddSeconds(2),
                "Checkout timeout: request 550e8400-e29b-41d4-a716-446655440001 failed.",
                Scope(("Name", "checkout-timeouts")), objectType: "log-query", objectId: "checkout-timeouts"),
            Finding("log-2", "victorialogs", "log-sample", TriggeredAt.AddSeconds(5),
                "Checkout timeout: request 550e8400-e29b-41d4-a716-446655440002 failed.",
                Scope(("Name", "checkout-timeouts")), objectType: "log-query", objectId: "checkout-timeouts"),
            Finding("log-3", "victorialogs", "log-sample", TriggeredAt.AddSeconds(8),
                "Checkout timeout: request 550e8400-e29b-41d4-a716-446655440003 failed.",
                Scope(("Name", "checkout-timeouts")), objectType: "log-query", objectId: "checkout-timeouts")
        };

        var result = Compress(Result("victorialogs", [firstError, .. samples]));

        Assert.Equal(4, result.InputFindingCount);
        Assert.Equal(2, result.OutputGroupCount);
        Assert.Equal(2, result.SemanticallyCollapsedFindingCount);
        var anchor = Assert.Single(result.Groups, group => group.Category == "first-error");
        Assert.Equal("preserve", anchor.Strategy);
        Assert.Equal(["first-error"], anchor.MemberEvidenceIds);
        var logs = Assert.Single(result.Groups, group => group.Strategy == "victorialogs.log-template");
        Assert.Equal(3, logs.OccurrenceCount);
        Assert.Equal(["log-1", "log-2", "log-3"], logs.MemberEvidenceIds);
        Assert.Contains("3 similar log events", logs.Summary, StringComparison.Ordinal);
        Assert.Contains("Representative:", logs.Summary, StringComparison.Ordinal);
        Assert.EndsWith("failed.", logs.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void NomadCollapsesAllocationVariantsWithinAJobOnly()
    {
        var checkout = new[]
        {
            NomadFailure("allocation-a", "payments", "checkout", "Allocation aaaaaaaa is failed", 2),
            NomadFailure("allocation-b", "payments", "checkout", "Allocation bbbbbbbb is failed", 7)
        };
        var worker = NomadFailure(
            "allocation-c", "payments", "worker", "Allocation cccccccc is failed", 9);

        var result = Compress(Result("nomad", [.. checkout, worker]));

        Assert.Equal(2, result.OutputGroupCount);
        var checkoutGroup = Assert.Single(result.Groups,
            group => group.MemberEvidenceIds.Contains("allocation-a", StringComparer.Ordinal));
        Assert.Equal("nomad.failure-template", checkoutGroup.Strategy);
        Assert.Equal(["allocation-a", "allocation-b"], checkoutGroup.MemberEvidenceIds);
        Assert.Single(result.Groups,
            group => group.MemberEvidenceIds.SequenceEqual(["allocation-c"], StringComparer.Ordinal));
    }

    [Fact]
    public void CodeBearingEvidenceAlwaysRemainsIndependentlyCitable()
    {
        var reference = new CodeReference(
            "code-1", "platform/payments", "abc123", "src/Handler.cs", 42, 42,
            "https://gitlab.test/platform/payments/blob/abc123/src/Handler.cs#L42", "+throw;");
        var codeBearing = Finding(
            "log-code", "victorialogs", "log-sample", TriggeredAt.AddSeconds(1),
            "Checkout timeout for request 550e8400-e29b-41d4-a716-446655440000.",
            Scope(("Name", "timeouts")), objectType: "log-query", objectId: "timeouts",
            codeReferences: [reference]);
        var similar = Finding(
            "log-similar", "victorialogs", "log-sample", TriggeredAt.AddSeconds(2),
            "Checkout timeout for request 550e8400-e29b-41d4-a716-446655440001.",
            Scope(("Name", "timeouts")), objectType: "log-query", objectId: "timeouts");

        var result = Compress(Result("victorialogs", [codeBearing, similar]));

        var preserved = Assert.Single(result.Groups, group => group.MemberEvidenceIds.Contains("log-code"));
        Assert.Equal("preserve", preserved.Strategy);
        Assert.Equal("code-1", Assert.Single(preserved.CodeReferences).Reference.Id);
    }

    [Fact]
    public void GitLabFailuresAndChangeAnchorsRemainIndependentlyCitable()
    {
        var firstReference = new CodeReference(
            "code-a", "platform/payments", "abc123", "src/Handler.cs", 42, 42,
            "https://gitlab.test/platform/payments/blob/abc123/src/Handler.cs#L42", "throw new TimeoutException();");
        var secondReference = firstReference with { Id = "code-b", StartLine = 87, EndLine = 87 };
        var firstJob = GitLabJob("job-101", "101", "501", 3,
            "request_id=550e8400-e29b-41d4-a716-446655440000 TimeoutException at Handler.cs:42",
            firstReference);
        var secondJob = GitLabJob("job-102", "102", "502", 8,
            "request_id=550e8400-e29b-41d4-a716-446655440001 TimeoutException at Handler.cs:87",
            secondReference);
        var deployments = new[]
        {
            Finding("deploy-101", "gitlab", "deployment", TriggeredAt.AddSeconds(4),
                "Deployment 101 is failed", Scope(("project", "platform/payments")), objectType: "deployment", objectId: "101"),
            Finding("deploy-102", "gitlab", "deployment", TriggeredAt.AddSeconds(9),
                "Deployment 102 is failed", Scope(("project", "platform/payments")), objectType: "deployment", objectId: "102")
        };

        var result = Compress(Result("gitlab", [firstJob, secondJob, .. deployments]));

        Assert.Equal(4, result.OutputGroupCount);
        Assert.Equal(2, result.Groups.Count(group =>
            group.Category == "pipeline-job-output" && group.Strategy == "preserve"));
        Assert.Contains(result.Groups, group =>
            group.MemberEvidenceIds.SequenceEqual(["job-101"], StringComparer.Ordinal)
            && group.CodeReferences.Select(item => item.Reference.Id).SequenceEqual(["code-a"]));
        Assert.Contains(result.Groups, group =>
            group.MemberEvidenceIds.SequenceEqual(["job-102"], StringComparer.Ordinal)
            && group.CodeReferences.Select(item => item.Reference.Id).SequenceEqual(["code-b"]));
        Assert.Equal(2, result.Groups.Count(group => group.Category == "deployment" && group.Strategy == "preserve"));
    }

    [Fact]
    public void GrafanaValuesAndPagerDutyEventsRemainIndependent()
    {
        var grafana = Result("grafana",
        [
            Finding("metric-1", "grafana", "metric", TriggeredAt.AddSeconds(1),
                "checkout latency: maximum observed value 812.4", Scope(("Name", "checkout latency")),
                objectType: "metric-query", objectId: "prom:checkout latency"),
            Finding("metric-2", "grafana", "metric", TriggeredAt.AddSeconds(6),
                "checkout latency: maximum observed value 973.1", Scope(("Name", "checkout latency")),
                objectType: "metric-query", objectId: "prom:checkout latency"),
            Finding("annotation-1", "grafana", "annotation", TriggeredAt.AddSeconds(2),
                "Production deployment completed", Scope()),
            Finding("annotation-2", "grafana", "annotation", TriggeredAt.AddSeconds(7),
                "Production deployment completed", Scope())
        ]);
        var pagerDuty = Result("pagerduty",
        [
            Finding("pd-1", "pagerduty", "incident", TriggeredAt, "Incident is triggered", Scope()),
            Finding("pd-2", "pagerduty", "incident", TriggeredAt.AddSeconds(10), "Incident is triggered", Scope())
        ]);

        var result = Compress(grafana, pagerDuty);

        Assert.Equal(4, result.Groups.Count(group => group.Source == "grafana" && group.Strategy == "preserve"));
        Assert.Contains(result.Groups, group => group.Summary.Contains("812.4", StringComparison.Ordinal));
        Assert.Contains(result.Groups, group => group.Summary.Contains("973.1", StringComparison.Ordinal));
        Assert.Equal(2, result.Groups.Count(group => group.Source == "pagerduty" && group.Strategy == "preserve"));
    }

    [Fact]
    public void DuplicateCanonicalizationAndSemanticOutputAreOrderIndependent()
    {
        var reference = new CodeReference(
            "code-1", "platform/payments", "abc123", "src/Handler.cs", 42, 42,
            "https://gitlab.test/platform/payments/blob/abc123/src/Handler.cs#L42", "+throw;");
        var weaker = Finding(
            "same-id", "victorialogs", "log-sample", TriggeredAt.AddSeconds(2),
            "Timeout for request 550e8400-e29b-41d4-a716-446655440000.",
            Scope(("Name", "timeouts")), severity: "info", confidence: .6,
            objectType: "log-query", objectId: "timeouts");
        var stronger = weaker with
        {
            Severity = "critical",
            Confidence = .98,
            Excerpt = "full diagnostic excerpt",
            CodeReferences = [reference]
        };
        var sibling = Finding(
            "other-id", "victorialogs", "log-sample", TriggeredAt.AddSeconds(5),
            "Timeout for request 550e8400-e29b-41d4-a716-446655440001.",
            Scope(("Name", "timeouts")), objectType: "log-query", objectId: "timeouts");

        var forward = Compress(
            Result("victorialogs", [weaker, sibling]),
            Result("victorialogs", [stronger]));
        var reverse = Compress(
            Result("victorialogs", [stronger]),
            Result("victorialogs", [sibling, weaker]));

        Assert.Equal(1, forward.DuplicateFindingCount);
        Assert.Equal(0, forward.SemanticallyCollapsedFindingCount);
        var group = Assert.Single(forward.Groups, item => item.MemberEvidenceIds.Contains("same-id"));
        Assert.Equal("critical", group.Severity);
        Assert.Contains(group.Representatives, finding => finding.Excerpt == "full diagnostic excerpt");
        Assert.Equal("code-1", Assert.Single(group.CodeReferences).Reference.Id);
        Assert.Single(forward.Groups, item => item.MemberEvidenceIds.Contains("other-id"));
        Assert.Equal(Snapshot(forward), Snapshot(reverse));
    }

    private static SemanticCompressionResult Compress(params ConnectorResult[] results) =>
        new SemanticEvidenceCompressor().Compress(results, TriggeredAt);

    private static ConnectorResult Result(string source, IReadOnlyList<EvidenceFinding> findings) =>
        new(source, SourceHealth.Complete, findings, [], [], 10, null);

    private static EvidenceFinding NomadFailure(
        string id,
        string jobNamespace,
        string job,
        string summary,
        int seconds) =>
        Finding(
            id,
            "nomad",
            "workload-failure",
            TriggeredAt.AddSeconds(seconds),
            summary,
            Scope(("namespace", jobNamespace), ("job", job)),
            objectType: "nomad-allocation",
            objectId: id);

    private static EvidenceFinding GitLabJob(
        string id,
        string pipelineId,
        string jobId,
        int seconds,
        string excerpt,
        CodeReference reference) =>
        Finding(
            id,
            "gitlab",
            "pipeline-job-output",
            TriggeredAt.AddSeconds(seconds),
            $"Job integration-tests in pipeline {pipelineId} (test) is failed; failure reason: script_failure",
            Scope(
                ("project", "platform/payments"),
                ("pipelineId", pipelineId),
                ("jobId", jobId),
                ("jobName", "integration-tests"),
                ("stage", "test"),
                ("status", "failed"),
                ("failureReason", "script_failure"),
                ("allowFailure", false)),
            severity: "critical",
            confidence: .98,
            excerpt: excerpt,
            objectType: "pipeline-job",
            objectId: jobId,
            codeReferences: [reference]);

    private static EvidenceFinding Finding(
        string id,
        string source,
        string category,
        DateTimeOffset occurredAt,
        string summary,
        JsonObject provenance,
        string severity = "warning",
        double confidence = .9,
        string? excerpt = null,
        string? objectType = null,
        string? objectId = null,
        IReadOnlyList<CodeReference>? codeReferences = null) =>
        new(
            id,
            source,
            occurredAt,
            null,
            category,
            severity,
            summary,
            excerpt,
            null,
            confidence,
            provenance,
            ObjectType: objectType,
            ObjectId: objectId,
            CodeReferences: codeReferences);

    private static JsonObject Scope(params (string Name, object Value)[] values)
    {
        var scope = new JsonObject();
        foreach (var (name, value) in values) scope[name] = JsonValue.Create(value);
        return new JsonObject { ["operation"] = "test", ["scope"] = scope };
    }

    private static string Snapshot(SemanticCompressionResult result) => JsonSerializer.Serialize(
        result.Groups.Select(group => new
        {
            group.Source,
            group.Category,
            group.Strategy,
            group.SemanticKey,
            group.OccurrenceCount,
            group.FirstOccurredAt,
            group.LastOccurredAt,
            group.Severity,
            group.Confidence,
            group.Summary,
            Representatives = group.Representatives.Select(finding => new
            {
                finding.Id,
                finding.Severity,
                finding.Confidence,
                finding.Excerpt,
                CodeReferences = (finding.CodeReferences ?? []).Select(reference => reference.Id)
            }),
            group.MemberEvidenceIds,
            CodeReferences = group.CodeReferences.Select(item => new { item.EvidenceId, item.Reference.Id })
        }));
}
