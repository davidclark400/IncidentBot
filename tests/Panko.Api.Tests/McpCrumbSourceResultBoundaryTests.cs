using System.Text.Json;
using System.Text.Json.Nodes;
using Panko.Api.Crumbs;
using Panko.Api.Domain;

namespace Panko.Api.Tests;

public sealed class McpConnectorResultBoundaryTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-07-11T10:00:00Z");

    [Fact]
    public void NormalizesHighVolumeResultsDeterministicallyWithinLimits()
    {
        var crumbs = Enumerable.Range(0, 20)
            .Select(index => Crumb(
                index == 0 ? "duplicate" : $"crumb-{index}",
                index == 18 ? "error" : index == 19 ? "critical" : "info",
                At.AddMinutes(index),
                new string('x', 3000)))
            .Append(Crumb("duplicate", "warning", At.AddMinutes(-1), "duplicate"))
            .Append(Crumb("wrong-source", "critical", At.AddHours(2), "spoofed") with { Source = "nomad" })
            .ToList();
        var result = Result(crumbs);
        var scope = Scope(maxItems: 3, maxBytes: 5000);

        var normalized = McpCrumbSourceBoundary.Normalize(
            "gitlab", result, scope, At, "https://gitlab.example", GitLabResources(), null);
        var reversed = McpCrumbSourceBoundary.Normalize(
            "gitlab", result with { Crumbs = crumbs.AsEnumerable().Reverse().ToList() },
            scope, At, "https://gitlab.example", GitLabResources(), null);

        Assert.Equal("gitlab", normalized.Source);
        Assert.Equal(3, normalized.Crumbs.Count);
        Assert.All(normalized.Crumbs, crumb =>
        {
            Assert.Equal("gitlab", crumb.Source);
            Assert.Matches("^[0-9a-f]{24}$", crumb.Id);
            Assert.Contains(crumb.Severity, new[] { "critical", "warning", "info" });
        });
        Assert.Equal(2, normalized.Crumbs.Count(crumb => crumb.Severity == "critical"));
        Assert.Equal(normalized.Crumbs.Select(crumb => crumb.Id), reversed.Crumbs.Select(crumb => crumb.Id));
        Assert.Equal(normalized.Crumbs.Count, normalized.Crumbs.Select(crumb => crumb.Id).Distinct().Count());
        Assert.True(McpCrumbSourceBoundary.EstimateRetainedBytes(normalized)
                    <= McpCrumbSourceBoundary.RetainedByteLimit(scope.MaxBytes));
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
        var crumb = Crumb(
            "secret-crumb",
            "warning",
            At,
            $"Authorization: Bearer {credential}; password=hunter2") with
        {
            Excerpt = "api_key=abc123 eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.signaturevalue",
            Provenance = provenance
        };
        var result = Result([crumb]) with
        {
            Diagnostic = $"credential={credential} private_token=diagnostic-token"
        };

        var normalized = McpCrumbSourceBoundary.Normalize(
            "gitlab", result, Scope(10, 16000), At, "https://gitlab.example", GitLabResources(), credential);
        var json = JsonSerializer.Serialize(normalized);

        Assert.DoesNotContain(credential, json, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", json, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", json, StringComparison.Ordinal);
        Assert.DoesNotContain("deep-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("diagnostic-token", json, StringComparison.Ordinal);
        Assert.DoesNotContain("provenance-token", json, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", json, StringComparison.Ordinal);
        Assert.Equal("retained", normalized.Crumbs.Single().Provenance["nested"]?["safe"]?.GetValue<string>());
    }

    [Fact]
    public void RestrictsAndCleansUrlsWhenAnAllowedBaseIsConfigured()
    {
        var crumbs = new[]
        {
            Crumb("allowed", "warning", At, "allowed") with
            {
                Url = "https://gitlab.example/group/payments/-/jobs/2?view=trace&private_token=secret"
            },
            Crumb("outside-path", "warning", At, "outside") with
            {
                Url = "https://gitlab.example/admin"
            },
            Crumb("outside-host", "warning", At, "evil") with
            {
                Url = "https://evil.example/api/projects/1"
            }
        };
        var result = Result(crumbs) with
        {
            Trail =
            [
                new TrailCandidate(At, "gitlab", "job", "bad link", "warning", "https://evil.example/group/payments/-/jobs/2")
            ],
            Links =
            [
                new SourceLink("Allowed", "https://gitlab.example/group/payments"),
                new SourceLink("Duplicate", "https://gitlab.example/group/payments"),
                new SourceLink("Outside", "https://evil.example/api/projects/1")
            ]
        };

        var normalized = McpCrumbSourceBoundary.Normalize(
            "gitlab", result, Scope(10, 16000), At, "https://gitlab.example", GitLabResources(), null);

        Assert.Equal(
            "https://gitlab.example/group/payments/-/jobs/2?view=trace",
            normalized.Crumbs.Single(item => item.Summary == "allowed").Url);
        Assert.Null(normalized.Crumbs.Single(item => item.Summary == "outside").Url);
        Assert.Null(normalized.Crumbs.Single(item => item.Summary == "evil").Url);
        Assert.Null(normalized.Trail.Single().Url);
        Assert.Single(normalized.Links);
        Assert.Equal("Allowed", normalized.Links.Single().Label);
    }

    [Fact]
    public void RejectsAResultThatClaimsAnotherSource()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            McpCrumbSourceBoundary.Normalize(
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
            McpCrumbSourceClient.ReadBoundedContentAsync(content, 1024, CancellationToken.None));

        Assert.Contains("1024-byte limit", exception.Message, StringComparison.Ordinal);
        Assert.Equal(64 * 1024, McpCrumbSourceClient.ToolResponseByteLimit(1));
        Assert.Equal(4 * 1024 * 1024, McpCrumbSourceClient.ToolResponseByteLimit(int.MaxValue));
    }

    [Fact]
    public void RejectsSameHostCrumbsOutsideTheConfiguredProject()
    {
        var allowed = Crumb("allowed", "critical", At, "allowed");
        var outside = Crumb("outside", "critical", At, "outside") with
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
        var unstructuredJob = Crumb("unstructured", "critical", At, "unstructured") with
        {
            Provenance = new JsonObject
            {
                ["scope"] = new JsonObject { ["project"] = "group/payments" }
            }
        };
        var outOfWindow = Crumb("late", "critical", At.AddHours(2), "late");

        var normalized = McpCrumbSourceBoundary.Normalize(
            "gitlab", Result([outside, unstructuredJob, outOfWindow, allowed]), Scope(10, 16000), At,
            "https://gitlab.example", GitLabResources(), null);

        Assert.Single(normalized.Crumbs);
        Assert.Equal("allowed", normalized.Crumbs.Single().Summary);
        Assert.Equal(CrumbSourceHealth.Partial, normalized.Health);
    }

    [Fact]
    public void DeniesUrlsWhenNoTrustedRootIsConfigured()
    {
        var result = Result([Crumb("crumb", "critical", At, "crumb")]) with
        {
            Links = [new SourceLink("Job", "https://gitlab.example/group/payments/-/jobs/2")]
        };

        var normalized = McpCrumbSourceBoundary.Normalize(
            "gitlab", result, Scope(10, 16000), At, null, GitLabResources(), null);

        Assert.Null(normalized.Crumbs.Single().Url);
        Assert.Empty(normalized.Links);
        Assert.Equal(CrumbSourceHealth.Partial, normalized.Health);
    }

    [Fact]
    public void EnforcesNomadNamespaceAndJobAllowlist()
    {
        static Crumb NomadCrumb(string id, string job) => new(
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
        var result = new CrumbSourceResult(
            "nomad", CrumbSourceHealth.Complete,
            [NomadCrumb("other", "admin"), NomadCrumb("allowed", "checkout")], [], [], 1, null);

        var normalized = McpCrumbSourceBoundary.Normalize(
            "nomad", result, Scope(10, 16000), At, "https://nomad.example", resources, null);

        Assert.Single(normalized.Crumbs);
        Assert.Equal("checkout", normalized.Crumbs.Single().Summary);
        Assert.Equal(CrumbSourceHealth.Partial, normalized.Health);
    }

    [Fact]
    public void EnforcesConsulServiceNamespaceAndDatacenterAllowlist()
    {
        static Crumb ConsulCrumb(
            string id,
            string service,
            string serviceNamespace,
            string datacenter = "primary") => new(
            id, "consul", At, null, "service-registration", "critical", service, null,
            $"https://consul.example/v1/health/service/{service}?dc={datacenter}&ns={serviceNamespace}", 1,
            new JsonObject
            {
                ["scope"] = new JsonObject
                {
                    ["datacenter"] = datacenter,
                    ["partition"] = "",
                    ["namespace"] = serviceNamespace,
                    ["service"] = service,
                    ["status"] = "unregistered"
                }
            });
        var resources = new JsonObject
        {
            ["datacenter"] = "primary",
            ["partition"] = "",
            ["services"] = new JsonArray
            {
                new JsonObject { ["name"] = "payments-api", ["namespace"] = "payments" }
            }
        };
        var result = new CrumbSourceResult(
            "consul", CrumbSourceHealth.Complete,
            [
                ConsulCrumb("wrong-name", "admin", "payments"),
                ConsulCrumb("wrong-namespace", "payments-api", "admin"),
                ConsulCrumb("wrong-datacenter", "payments-api", "payments", "secondary"),
                ConsulCrumb("allowed", "payments-api", "payments")
            ], [], [], 1, null);

        var normalized = McpCrumbSourceBoundary.Normalize(
            "consul", result, Scope(10, 16000), At, "https://consul.example", resources, null);

        Assert.Single(normalized.Crumbs);
        Assert.Equal("payments-api", normalized.Crumbs.Single().Summary);
        Assert.Equal(CrumbSourceHealth.Partial, normalized.Health);
    }

    [Fact]
    public void RecomputesMcpGitLabFailureOrdinalsInsteadOfTrustingClaimedRank()
    {
        var root = Crumb("root", "critical", At.AddMinutes(-10), "root");
        var cascade = Crumb("cascade", "critical", At.AddSeconds(-1), "cascade");
        root.Provenance["scope"]!["firstHardFailure"] = false;
        root.Provenance["scope"]!["failureOrdinal"] = 99;
        cascade.Provenance["scope"]!["firstHardFailure"] = true;
        cascade.Provenance["scope"]!["failureOrdinal"] = 1;

        var normalized = McpCrumbSourceBoundary.Normalize(
            "gitlab", Result([cascade, root]), Scope(10, 16000), At,
            "https://gitlab.example", GitLabResources(), null);

        Assert.Equal("root", normalized.Crumbs[0].Summary);
        Assert.True(normalized.Crumbs[0].Provenance["scope"]?["firstHardFailure"]?.GetValue<bool>());
        Assert.Equal(1, normalized.Crumbs[0].Provenance["scope"]?["failureOrdinal"]?.GetValue<int>());
        Assert.False(normalized.Crumbs[1].Provenance["scope"]?["firstHardFailure"]?.GetValue<bool>());
    }

    [Fact]
    public void GrafanaContextMetricCannotBecomeAnMcpAnomaly()
    {
        var resources = GrafanaResources(
            new JsonObject
            {
                ["name"] = "Request rate",
                ["datasourceUid"] = "prometheus-main",
                ["crumbMode"] = "context",
                ["requirement"] = "required"
            });
        var result = new CrumbSourceResult(
            "grafana",
            CrumbSourceHealth.Complete,
            [GrafanaCrumb("request-rate", "Request rate", "critical")],
            [],
            [],
            1,
            null);

        var normalized = McpCrumbSourceBoundary.Normalize(
            "grafana", result, Scope(10, 16000), At, "https://grafana.example", resources, null);

        Assert.Equal(CrumbSourceHealth.Complete, normalized.Health);
        Assert.Equal("info", Assert.Single(normalized.Crumbs).Severity);
    }

    [Fact]
    public void MissingRequiredGrafanaMcpMetricIsPartial()
    {
        var resources = GrafanaResources(
            new JsonObject
            {
                ["name"] = "Availability",
                ["datasourceUid"] = "prometheus-main",
                ["crumbMode"] = "anomaly",
                ["requirement"] = "required"
            },
            new JsonObject
            {
                ["name"] = "Request rate",
                ["datasourceUid"] = "prometheus-main",
                ["crumbMode"] = "context",
                ["requirement"] = "optional"
            });
        var result = new CrumbSourceResult(
            "grafana",
            CrumbSourceHealth.Complete,
            [GrafanaCrumb("request-rate", "Request rate", "info")],
            [],
            [],
            1,
            null);

        var normalized = McpCrumbSourceBoundary.Normalize(
            "grafana", result, Scope(10, 16000), At, "https://grafana.example", resources, null);

        Assert.Equal(CrumbSourceHealth.Partial, normalized.Health);
        Assert.Contains("Availability", normalized.Diagnostic, StringComparison.Ordinal);
    }

    private sealed class NonSeekableMemoryStream(byte[] buffer) : MemoryStream(buffer, writable: false)
    {
        public override bool CanSeek => false;
    }

    private static CrumbScope Scope(int maxItems, int maxBytes) =>
        new(At.AddMinutes(-30), At.AddMinutes(30), "v1", maxItems, maxBytes);

    private static CrumbSourceResult Result(IReadOnlyList<Crumb> crumbs) =>
        new("GitLab", CrumbSourceHealth.Complete, crumbs, [], [], 42, null);

    private static JsonObject GitLabResources() => new()
    {
        ["projects"] = new JsonArray
        {
            new JsonObject { ["id"] = "group/payments", ["branch"] = "main" }
        },
        ["includePipelineJobOutput"] = true
    };

    private static JsonObject GrafanaResources(params JsonObject[] queries) => new()
    {
        ["queries"] = new JsonArray(queries.Select(query => (JsonNode)query).ToArray()),
        ["dashboards"] = new JsonArray(),
        ["annotationTags"] = new JsonArray()
    };

    private static Crumb GrafanaCrumb(
        string id,
        string name,
        string severity) => new(
        id,
        "grafana",
        At,
        null,
        "metric",
        severity,
        name,
        null,
        null,
        .9,
        new JsonObject
        {
            ["operation"] = "POST /api/ds/query",
            ["scope"] = new JsonObject
            {
                ["name"] = name,
                ["datasourceUid"] = "prometheus-main"
            }
        },
        ObjectType: "metric-query",
        ObjectId: $"prometheus-main:{name}");

    private static Crumb Crumb(
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
