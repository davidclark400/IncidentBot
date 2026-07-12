using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentBot.Api.Connectors;
using IncidentBot.Api.Domain;

namespace IncidentBot.Api.Tests;

public sealed class McpConnectorResultBoundaryTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-07-11T10:00:00Z");

    [Fact]
    public void NormalizesHighVolumeResultsDeterministicallyWithinLimits()
    {
        var findings = Enumerable.Range(0, 20)
            .Select(index => Finding(
                index == 0 ? "duplicate" : $"finding-{index}",
                index == 18 ? "error" : index == 19 ? "critical" : "info",
                At.AddMinutes(index),
                new string('x', 3000)))
            .Append(Finding("duplicate", "warning", At.AddMinutes(-1), "duplicate"))
            .Append(Finding("wrong-source", "critical", At.AddHours(2), "spoofed") with { Source = "nomad" })
            .ToList();
        var result = Result(findings);
        var scope = Scope(maxItems: 3, maxBytes: 5000);

        var normalized = McpConnectorResultBoundary.Normalize(
            "gitlab", result, scope, At, "https://gitlab.example", GitLabResources(), null);
        var reversed = McpConnectorResultBoundary.Normalize(
            "gitlab", result with { Findings = findings.AsEnumerable().Reverse().ToList() },
            scope, At, "https://gitlab.example", GitLabResources(), null);

        Assert.Equal("gitlab", normalized.Source);
        Assert.Equal(3, normalized.Findings.Count);
        Assert.All(normalized.Findings, finding =>
        {
            Assert.Equal("gitlab", finding.Source);
            Assert.Matches("^[0-9a-f]{24}$", finding.Id);
            Assert.Contains(finding.Severity, new[] { "critical", "warning", "info" });
        });
        Assert.Equal(2, normalized.Findings.Count(finding => finding.Severity == "critical"));
        Assert.Equal(normalized.Findings.Select(finding => finding.Id), reversed.Findings.Select(finding => finding.Id));
        Assert.Equal(normalized.Findings.Count, normalized.Findings.Select(finding => finding.Id).Distinct().Count());
        Assert.True(McpConnectorResultBoundary.EstimateRetainedBytes(normalized)
                    <= McpConnectorResultBoundary.RetainedByteLimit(scope.MaxBytes));
    }

    [Fact]
    public void SanitizesKnownAndPatternBasedSecretsRecursively()
    {
        const string credential = "mcp-super-secret";
        var provenance = new JsonObject
        {
            ["token"] = "provenance-token",
            ["scope"] = new JsonObject
            {
                ["project"] = "group/payments",
                ["pipelineId"] = "42",
                ["status"] = "failed",
                ["allowFailure"] = false
            },
            ["nested"] = new JsonObject
            {
                ["message"] = "client_secret=deep-secret",
                ["safe"] = "retained"
            }
        };
        var finding = Finding(
            "secret-finding",
            "warning",
            At,
            $"Authorization: Bearer {credential}; password=hunter2") with
        {
            Excerpt = "api_key=abc123 eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.signaturevalue",
            Provenance = provenance
        };
        var result = Result([finding]) with
        {
            Diagnostic = $"credential={credential} private_token=diagnostic-token"
        };

        var normalized = McpConnectorResultBoundary.Normalize(
            "gitlab", result, Scope(10, 16000), At, "https://gitlab.example", GitLabResources(), credential);
        var json = JsonSerializer.Serialize(normalized);

        Assert.DoesNotContain(credential, json, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", json, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", json, StringComparison.Ordinal);
        Assert.DoesNotContain("deep-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("diagnostic-token", json, StringComparison.Ordinal);
        Assert.DoesNotContain("provenance-token", json, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", json, StringComparison.Ordinal);
        Assert.Equal("retained", normalized.Findings.Single().Provenance["nested"]?["safe"]?.GetValue<string>());
    }

    [Fact]
    public void RestrictsAndCleansUrlsWhenAnAllowedBaseIsConfigured()
    {
        var findings = new[]
        {
            Finding("allowed", "warning", At, "allowed") with
            {
                Url = "https://gitlab.example/group/payments/-/jobs/2?view=trace&private_token=secret"
            },
            Finding("outside-path", "warning", At, "outside") with
            {
                Url = "https://gitlab.example/admin"
            },
            Finding("outside-host", "warning", At, "evil") with
            {
                Url = "https://evil.example/api/projects/1"
            }
        };
        var result = Result(findings) with
        {
            Timeline =
            [
                new TimelineCandidate(At, "gitlab", "job", "bad link", "warning", "https://evil.example/group/payments/-/jobs/2")
            ],
            Links =
            [
                new SourceLink("Allowed", "https://gitlab.example/group/payments"),
                new SourceLink("Duplicate", "https://gitlab.example/group/payments"),
                new SourceLink("Outside", "https://evil.example/api/projects/1")
            ]
        };

        var normalized = McpConnectorResultBoundary.Normalize(
            "gitlab", result, Scope(10, 16000), At, "https://gitlab.example", GitLabResources(), null);

        Assert.Equal(
            "https://gitlab.example/group/payments/-/jobs/2?view=trace",
            normalized.Findings.Single(item => item.Summary == "allowed").Url);
        Assert.Null(normalized.Findings.Single(item => item.Summary == "outside").Url);
        Assert.Null(normalized.Findings.Single(item => item.Summary == "evil").Url);
        Assert.Null(normalized.Timeline.Single().Url);
        Assert.Single(normalized.Links);
        Assert.Equal("Allowed", normalized.Links.Single().Label);
    }

    [Fact]
    public void RejectsAResultThatClaimsAnotherSource()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            McpConnectorResultBoundary.Normalize(
                "gitlab", Result([]) with { Source = "nomad" }, Scope(10, 16000), At, null, GitLabResources(), null));

        Assert.Contains("source", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsAnOversizedMcpResponseBeforeDeserialization()
    {
        using var stream = new NonSeekableMemoryStream(new byte[1025]);
        using var content = new StreamContent(stream);
        Assert.Null(content.Headers.ContentLength);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            McpStreamableHttpClient.ReadBoundedContentAsync(content, 1024, CancellationToken.None));

        Assert.Contains("1024-byte limit", exception.Message, StringComparison.Ordinal);
        Assert.Equal(64 * 1024, McpStreamableHttpClient.ToolResponseByteLimit(1));
        Assert.Equal(4 * 1024 * 1024, McpStreamableHttpClient.ToolResponseByteLimit(int.MaxValue));
    }

    [Fact]
    public void RejectsSameHostEvidenceOutsideTheConfiguredProject()
    {
        var allowed = Finding("allowed", "critical", At, "allowed");
        var outside = Finding("outside", "critical", At, "outside") with
        {
            Url = "https://gitlab.example/other/project/-/jobs/3",
            Provenance = new JsonObject
            {
                ["scope"] = new JsonObject
                {
                    ["project"] = "other/project",
                    ["pipelineId"] = "99",
                    ["status"] = "failed",
                    ["allowFailure"] = false
                }
            }
        };
        var unstructuredJob = Finding("unstructured", "critical", At, "unstructured") with
        {
            Provenance = new JsonObject
            {
                ["scope"] = new JsonObject { ["project"] = "group/payments" }
            }
        };
        var outOfWindow = Finding("late", "critical", At.AddHours(2), "late");

        var normalized = McpConnectorResultBoundary.Normalize(
            "gitlab", Result([outside, unstructuredJob, outOfWindow, allowed]), Scope(10, 16000), At,
            "https://gitlab.example", GitLabResources(), null);

        Assert.Single(normalized.Findings);
        Assert.Equal("allowed", normalized.Findings.Single().Summary);
        Assert.Equal(SourceHealth.Partial, normalized.Health);
    }

    [Fact]
    public void DeniesUrlsWhenNoTrustedRootIsConfigured()
    {
        var result = Result([Finding("finding", "critical", At, "finding")]) with
        {
            Links = [new SourceLink("Job", "https://gitlab.example/group/payments/-/jobs/2")]
        };

        var normalized = McpConnectorResultBoundary.Normalize(
            "gitlab", result, Scope(10, 16000), At, null, GitLabResources(), null);

        Assert.Null(normalized.Findings.Single().Url);
        Assert.Empty(normalized.Links);
        Assert.Equal(SourceHealth.Partial, normalized.Health);
    }

    [Fact]
    public void EnforcesNomadNamespaceAndJobAllowlist()
    {
        static EvidenceFinding NomadFinding(string id, string job) => new(
            id, "nomad", At, null, "workload-failure", "warning", job, null,
            $"https://nomad.example/v1/job/{job}?namespace=payments", .9,
            new JsonObject
            {
                ["scope"] = new JsonObject
                {
                    ["namespace"] = "payments",
                    ["job"] = job
                }
            });
        var resources = new JsonObject
        {
            ["region"] = "global",
            ["namespaces"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "payments",
                    ["jobs"] = new JsonArray("checkout")
                }
            }
        };
        var result = new ConnectorResult(
            "nomad", SourceHealth.Complete,
            [NomadFinding("other", "admin"), NomadFinding("allowed", "checkout")], [], [], 1, null);

        var normalized = McpConnectorResultBoundary.Normalize(
            "nomad", result, Scope(10, 16000), At, "https://nomad.example", resources, null);

        Assert.Single(normalized.Findings);
        Assert.Equal("checkout", normalized.Findings.Single().Summary);
        Assert.Equal(SourceHealth.Partial, normalized.Health);
    }

    [Fact]
    public void RecomputesMcpGitLabFailureOrdinalsInsteadOfTrustingClaimedRank()
    {
        var root = Finding("root", "critical", At.AddMinutes(-10), "root");
        var cascade = Finding("cascade", "critical", At.AddSeconds(-1), "cascade");
        root.Provenance["scope"]!["firstHardFailure"] = false;
        root.Provenance["scope"]!["failureOrdinal"] = 99;
        cascade.Provenance["scope"]!["firstHardFailure"] = true;
        cascade.Provenance["scope"]!["failureOrdinal"] = 1;

        var normalized = McpConnectorResultBoundary.Normalize(
            "gitlab", Result([cascade, root]), Scope(10, 16000), At,
            "https://gitlab.example", GitLabResources(), null);

        Assert.Equal("root", normalized.Findings[0].Summary);
        Assert.True(normalized.Findings[0].Provenance["scope"]?["firstHardFailure"]?.GetValue<bool>());
        Assert.Equal(1, normalized.Findings[0].Provenance["scope"]?["failureOrdinal"]?.GetValue<int>());
        Assert.False(normalized.Findings[1].Provenance["scope"]?["firstHardFailure"]?.GetValue<bool>());
    }

    private sealed class NonSeekableMemoryStream(byte[] buffer) : MemoryStream(buffer, writable: false)
    {
        public override bool CanSeek => false;
    }

    private static EvidenceScope Scope(int maxItems, int maxBytes) =>
        new(At.AddMinutes(-30), At.AddMinutes(30), "v1", maxItems, maxBytes);

    private static ConnectorResult Result(IReadOnlyList<EvidenceFinding> findings) =>
        new("GitLab", SourceHealth.Complete, findings, [], [], 42, null);

    private static JsonObject GitLabResources() => new()
    {
        ["projects"] = new JsonArray
        {
            new JsonObject { ["id"] = "group/payments", ["branch"] = "main" }
        },
        ["includePipelineJobOutput"] = true
    };

    private static EvidenceFinding Finding(
        string id,
        string severity,
        DateTimeOffset occurredAt,
        string summary) =>
        new(
            id,
            "GITLAB",
            occurredAt,
            null,
            "pipeline-job-output",
            severity,
            summary,
            null,
            "https://gitlab.example/group/payments/-/jobs/2",
            0.9,
            new JsonObject
            {
                ["operation"] = "GET jobs",
                ["scope"] = new JsonObject
                {
                    ["project"] = "group/payments",
                    ["pipelineId"] = "42",
                    ["status"] = "failed",
                    ["allowFailure"] = false
                }
            });
}
