using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using IncidentBot.Api.Connectors;
using IncidentBot.Api.Domain;
using IncidentBot.Api.Options;

namespace IncidentBot.Api.Tests;

public sealed class GitLabEvidenceConnectorTests
{
    [Fact]
    public async Task MultiJobPipelinePaginatesRanksCollapsesAndBudgetsTraces()
    {
        var handler = new MultiJobPipelineHandler();
        var connector = CreateConnector(handler);

        var result = await connector.CollectAsync(
            Context(), Scope(maxItems: 20, maxBytes: 1_000_000), CancellationToken.None);

        var jobEvidence = result.Findings
            .Where(finding => finding.Category == "pipeline-job-output")
            .ToList();

        Assert.Equal(SourceHealth.Partial, result.Health);
        Assert.Equal(20, result.Findings.Count);
        Assert.True(result.Timeline.Count <= 20);
        Assert.Equal([1, 2], handler.PipelinePages);
        Assert.Equal([1, 1, 1, 2], handler.JobPages);
        Assert.Equal(40, handler.JobPipelineIds.Count);
        Assert.All(handler.JobQueries, query =>
        {
            var decoded = Uri.UnescapeDataString(query);
            Assert.True(
                decoded.Contains("scope[]=failed", StringComparison.Ordinal)
                ^ decoded.Contains("scope[]=canceled", StringComparison.Ordinal));
            Assert.Contains("include_retried=", decoded, StringComparison.Ordinal);
        });
        Assert.Contains(handler.JobQueries, query => query.Contains("include_retried=false", StringComparison.Ordinal));
        Assert.Contains(handler.JobQueries, query => query.Contains("include_retried=true", StringComparison.Ordinal));
        Assert.Equal(4, jobEvidence.Count);
        Assert.DoesNotContain(jobEvidence, finding => finding.Summary.Contains("recovered-test", StringComparison.Ordinal));

        var hardFailure = jobEvidence[0];
        Assert.Equal("critical", hardFailure.Severity);
        Assert.Contains("compile", hardFailure.Summary, StringComparison.Ordinal);
        Assert.Contains("script_failure", hardFailure.Summary, StringComparison.Ordinal);
        Assert.Contains("2 failed or canceled attempts", hardFailure.Summary, StringComparison.Ordinal);
        Assert.Contains("BOUNDED-SCAN-EVIDENCE", hardFailure.Excerpt, StringComparison.Ordinal);
        Assert.DoesNotContain("BEGIN-TRACE", hardFailure.Excerpt, StringComparison.Ordinal);
        Assert.DoesNotContain("REAL-END-OUTSIDE-BUDGET", hardFailure.Excerpt, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", hardFailure.Excerpt, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", hardFailure.Excerpt, StringComparison.Ordinal);

        Assert.Contains("firstHardFailure\":true", hardFailure.Provenance.ToJsonString(), StringComparison.Ordinal);
        var cascadingFailure = jobEvidence[1];
        Assert.Contains("cascade-deploy", cascadingFailure.Summary, StringComparison.Ordinal);
        Assert.Contains("failureOrdinal\":2", cascadingFailure.Provenance.ToJsonString(), StringComparison.Ordinal);

        var allowedFailure = jobEvidence[2];
        Assert.Equal("warning", allowedFailure.Severity);
        Assert.Contains("allowed to fail", allowedFailure.Summary, StringComparison.Ordinal);
        Assert.Contains("runner_system_failure", allowedFailure.Summary, StringComparison.Ordinal);

        var cancellations = jobEvidence[3];
        Assert.Equal("pipeline-job-cancellations", cancellations.ObjectType);
        Assert.Contains("canceled 100 jobs", cancellations.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(jobEvidence, finding => finding.ObjectId is not null
            && finding.ObjectId.StartsWith("10", StringComparison.Ordinal));

        Assert.Equal(3, handler.TraceRanges.Count);
        Assert.All(handler.TraceRanges, range => Assert.Equal("bytes=-20000", range));
        Assert.Equal(3, jobEvidence.Count(finding => finding.ObjectType == "pipeline-job"));
        Assert.Contains("retryAttemptCount\":2", hardFailure.Provenance.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("traceBytesScanned\":20000", hardFailure.Provenance.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("Pipeline job discovery limited to 40 of 101", result.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("not a verified trace tail", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChildPipelineIsCollectedAndUnverifiedPartialTraceFallsBackToRealTail()
    {
        var handler = new ChildPipelineHandler();
        var connector = CreateConnector(handler);

        var result = await connector.CollectAsync(
            Context(), Scope(maxItems: 10, maxBytes: 10_000), CancellationToken.None);

        var job = Assert.Single(result.Findings,
            finding => finding.Category == "pipeline-job-output");
        Assert.True(handler.RequestedChildPipelines);
        Assert.Equal(2, handler.TraceRequestCount);
        Assert.Equal(SourceHealth.Partial, result.Health);
        Assert.Contains("BOUNDED-FALLBACK-EVIDENCE", job.Excerpt, StringComparison.Ordinal);
        Assert.DoesNotContain("REAL-FAILURE-END", job.Excerpt, StringComparison.Ordinal);
        Assert.DoesNotContain("UNVERIFIED-PREFIX", job.Excerpt, StringComparison.Ordinal);
    }

    private static GitLabEvidenceConnector CreateConnector(HttpMessageHandler handler) =>
        new(
            new StubHttpClientFactory(handler),
            new UnexpectedMcpAdapter(),
            TestConfiguration.EvidenceSources(gitLab: Transport()),
            TestConfiguration.Credentials());

    private static InvestigationContext Context()
    {
        return new InvestigationContext(
            Guid.NewGuid(), "PD-1", "payments", "Checkout failures", "high", IncidentState.Triggered,
            DateTimeOffset.Parse("2026-07-11T10:00:00Z"), new Dictionary<string, string>(),
            new InvestigationProfile
            {
                Id = "payments",
                GitLab = new GitLabScope
                {
                    Projects =
                    [
                        new GitLabProject
                        {
                            Id = "group/payments",
                            Branch = "main"
                        }
                    ]
                }
            });
    }

    private static ConnectorTransport Transport() => new()
    {
        Mode = "api",
        BaseUrl = "https://gitlab.example/",
        CredentialEnv = "GITLAB_READ_TOKEN",
        TimeoutSeconds = 30,
        MaxItems = 50,
        MaxBytes = 200_000
    };

    private static EvidenceScope Scope(int maxItems, int maxBytes) => new(
        DateTimeOffset.Parse("2026-07-11T09:00:00Z"),
        DateTimeOffset.Parse("2026-07-11T11:00:00Z"),
        "v1", maxItems, maxBytes);

    private abstract class GitLabHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = Handle(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }

        protected abstract HttpResponseMessage Handle(HttpRequestMessage request);

        protected static HttpResponseMessage JsonResponse(object value, HttpStatusCode status = HttpStatusCode.OK)
        {
            var json = JsonSerializer.Serialize(value);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }

        protected static int QueryInt(Uri uri, string name)
        {
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                if (Uri.UnescapeDataString(parts[0]) == name && parts.Length == 2
                    && int.TryParse(Uri.UnescapeDataString(parts[1]), out var value))
                {
                    return value;
                }
            }
            return 0;
        }

        protected static object FailedJob(
            int id,
            string name,
            string stage,
            string status,
            bool allowFailure,
            string failureReason,
            bool retried,
            string finishedAt) => new
            {
                id,
                name,
                stage,
                status,
                allow_failure = allowFailure,
                failure_reason = failureReason,
                retried,
                finished_at = finishedAt,
                web_url = $"https://gitlab.example/group/payments/-/jobs/{id}",
                user = new { name = "CI Runner" }
            };

        protected static bool IsCommonEmptyEndpoint(string path) =>
            path.EndsWith("/merge_requests", StringComparison.Ordinal)
            || path.EndsWith("/repository/commits", StringComparison.Ordinal);
    }

    private sealed class MultiJobPipelineHandler : GitLabHandler
    {
        public List<int> JobPages { get; } = [];
        public List<int> PipelinePages { get; } = [];
        public List<string> JobQueries { get; } = [];
        public List<string> TraceRanges { get; } = [];
        public HashSet<string> JobPipelineIds { get; } = [];

        protected override HttpResponseMessage Handle(HttpRequestMessage request)
        {
            var uri = request.RequestUri!;
            var path = uri.AbsolutePath;
            if (IsCommonEmptyEndpoint(path)) return JsonResponse(Array.Empty<object>());
            if (path.EndsWith("/pipelines/77/jobs", StringComparison.Ordinal))
            {
                JobPipelineIds.Add("77");
                var page = QueryInt(uri, "page");
                JobPages.Add(page);
                JobQueries.Add(uri.Query);
                var decodedQuery = Uri.UnescapeDataString(uri.Query);
                var includeRetried = uri.Query.Contains("include_retried=true", StringComparison.Ordinal);
                var canceledScope = decodedQuery.Contains("scope[]=canceled", StringComparison.Ordinal);
                if (canceledScope && page == 1)
                {
                    var jobs = Enumerable.Range(0, 100)
                        .Select(index => FailedJob(
                            1000 + index, $"fanout-{index}", "deploy", "canceled", false,
                            "job_execution_timeout", false, "2026-07-11T10:05:00Z"))
                        .ToArray();
                    var response = JsonResponse(jobs);
                    response.Headers.Add("X-Next-Page", "2");
                    return response;
                }
                if (canceledScope) return JsonResponse(Array.Empty<object>());
                if (page != 1) return JsonResponse(Array.Empty<object>());
                var pageJobs = new List<object>();
                if (includeRetried)
                {
                    pageJobs.Add(FailedJob(200, "compile", "build", "failed", false, "script_failure", true,
                        "2026-07-11T09:44:00Z"));
                    pageJobs.Add(FailedJob(302, "recovered-test", "test", "failed", false, "script_failure", true,
                        "2026-07-11T10:00:30Z"));
                }
                pageJobs.Add(FailedJob(201, "compile", "build", "failed", false, "script_failure", false,
                    "2026-07-11T09:45:00Z"));
                pageJobs.Add(FailedJob(202, "cascade-deploy", "deploy", "failed", false, "script_failure", false,
                    "2026-07-11T09:59:00Z"));
                pageJobs.Add(FailedJob(301, "flaky-integration", "test", "failed", true, "runner_system_failure", false,
                    "2026-07-11T10:02:00Z"));
                return JsonResponse(pageJobs);
            }
            if (path.Contains("/pipelines/", StringComparison.Ordinal)
                && path.EndsWith("/jobs", StringComparison.Ordinal))
            {
                var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var pipelineIndex = Array.IndexOf(parts, "pipelines");
                JobPipelineIds.Add(parts[pipelineIndex + 1]);
                return JsonResponse(Array.Empty<object>());
            }
            if (path.EndsWith("/jobs/201/trace", StringComparison.Ordinal))
            {
                TraceRanges.Add(request.Headers.Range!.ToString());
                var trace = "BEGIN-TRACE\n" + new string('x', 18_000)
                    + "\npassword=hunter2\nBOUNDED-SCAN-EVIDENCE"
                    + new string('z', 30_000) + "\nREAL-END-OUTSIDE-BUDGET";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(trace, Encoding.UTF8, "text/plain")
                };
            }
            if (path.EndsWith("/jobs/202/trace", StringComparison.Ordinal))
            {
                TraceRanges.Add(request.Headers.Range!.ToString());
                const string trace = "cascading deploy failure";
                var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new StringContent(trace, Encoding.UTF8, "text/plain")
                };
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                    20_000 - Encoding.UTF8.GetByteCount(trace), 19_999, 20_000);
                return response;
            }
            if (path.EndsWith("/jobs/301/trace", StringComparison.Ordinal))
            {
                TraceRanges.Add(request.Headers.Range!.ToString());
                const string trace = "allowed job runner_system_failure";
                var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new StringContent(trace, Encoding.UTF8, "text/plain")
                };
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                    10_000 - Encoding.UTF8.GetByteCount(trace), 9_999, 10_000);
                return response;
            }
            if (path.EndsWith("/pipelines", StringComparison.Ordinal))
            {
                if (Uri.UnescapeDataString(uri.Query).Contains("source=parent_pipeline", StringComparison.Ordinal))
                {
                    return JsonResponse(Array.Empty<object>());
                }
                var page = QueryInt(uri, "page");
                PipelinePages.Add(page);
                if (page == 1)
                {
                    var response = JsonResponse(Enumerable.Range(0, 100).Select(index => new
                    {
                        id = 5000 + index,
                        status = "failed",
                        updated_at = "2026-07-11T09:30:00Z",
                        web_url = $"https://gitlab.example/group/payments/-/pipelines/{5000 + index}"
                    }).ToArray());
                    response.Headers.Add("X-Next-Page", "2");
                    return response;
                }
                return JsonResponse(new[]
                {
                    new
                    {
                        id = 77,
                        status = "failed",
                        updated_at = "2026-07-11T10:06:00Z",
                        web_url = "https://gitlab.example/group/payments/-/pipelines/77"
                    }
                });
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private sealed class ChildPipelineHandler : GitLabHandler
    {
        public bool RequestedChildPipelines { get; private set; }
        public int TraceRequestCount { get; private set; }

        protected override HttpResponseMessage Handle(HttpRequestMessage request)
        {
            var uri = request.RequestUri!;
            var path = uri.AbsolutePath;
            if (IsCommonEmptyEndpoint(path)) return JsonResponse(Array.Empty<object>());
            if (path.EndsWith("/pipelines/88/jobs", StringComparison.Ordinal))
            {
                return JsonResponse(new[]
                {
                    FailedJob(401, "child-test", "test", "failed", false, "script_failure", false,
                        "2026-07-11T10:03:00Z")
                });
            }
            if (path.EndsWith("/jobs/401/trace", StringComparison.Ordinal))
            {
                TraceRequestCount++;
                if (request.Headers.Range is not null)
                {
                    var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
                    {
                        Content = new StringContent("UNVERIFIED-PREFIX", Encoding.UTF8, "text/plain")
                    };
                    response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 16, 10_000);
                    return response;
                }
                var trace = "actual trace\n" + new string('y', 2700)
                    + "\nBOUNDED-FALLBACK-EVIDENCE" + new string('z', 3000) + "\nREAL-FAILURE-END";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(trace, Encoding.UTF8, "text/plain")
                };
            }
            if (path.EndsWith("/pipelines", StringComparison.Ordinal))
            {
                if (Uri.UnescapeDataString(uri.Query).Contains("source=parent_pipeline", StringComparison.Ordinal))
                {
                    RequestedChildPipelines = true;
                    return JsonResponse(new[]
                    {
                        new
                        {
                            id = 88,
                            status = "failed",
                            updated_at = "2026-07-11T10:04:00Z",
                            web_url = "https://gitlab.example/group/payments/-/pipelines/88"
                        }
                    });
                }
                return JsonResponse(Array.Empty<object>());
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class UnexpectedMcpAdapter : IMcpEvidenceAdapter
    {
        public Task<ConnectorResult> CollectAsync(
            string source,
            McpToolConfiguration configuration,
            InvestigationContext context,
            EvidenceScope scope,
            object allowedResources,
            string? allowedBaseUrl,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("MCP should not be used by native connector tests.");
    }
}
