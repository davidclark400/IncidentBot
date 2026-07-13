using System.Text.Json;
using IncidentBot.Api.Domain;
using IncidentBot.Api.Incidents;
using IncidentBot.Api.Infrastructure;
using IncidentBot.Api.Options;

namespace IncidentBot.Api.Connectors;

public sealed class NomadEvidenceConnector(
    IHttpClientFactory httpClientFactory,
    IMcpEvidenceAdapter mcp,
    EvidenceSourceConfiguration evidenceSources,
    ICredentialProvider credentials) : IIncidentEvidenceConnector
{
    public string Source => EvidenceSourceRegistry.Nomad;
    public bool SupportsWindowExpansion => false;

    public Task<ConnectorResult> CollectAsync(InvestigationContext context, EvidenceScope scope, CancellationToken cancellationToken)
    {
        var configuration = context.Profile.Nomad;
        if (configuration is null) return Task.FromResult(ConnectorResult.Excluded(Source));
        var transport = evidenceSources.For(Source);
        return ConnectorUtilities.CollectAsync(
            Source, transport, mcp, context, scope,
            new
            {
                configuration.Region,
                namespaces = configuration.Namespaces.Select(item => new { item.Name, item.Jobs })
            }, async ct =>
        {
            var findings = new List<EvidenceFinding>();
            var timeline = new List<TimelineCandidate>();
            var links = new List<SourceLink>();
            var sourceMaximumItems = Math.Min(
                Math.Max(0, scope.MaxItems),
                Math.Max(0, transport.MaxItems));
            var candidateLimit = Math.Max(
                sourceMaximumItems,
                Math.Min(Math.Max(0, scope.MaxItems) * 4, 400));
            var jobs = configuration.Namespaces
                .SelectMany(namespaceScope => namespaceScope.Jobs.Select(job =>
                {
                    var encodedJob = Uri.EscapeDataString(job);
                    var query = $"namespace={Uri.EscapeDataString(namespaceScope.Name)}"
                                + $"&region={Uri.EscapeDataString(configuration.Region)}";
                    return new NomadJobTarget(
                        namespaceScope.Name,
                        job,
                        encodedJob,
                        query,
                        ConnectorUtilities.Url(transport, $"v1/job/{encodedJob}?{query}"));
                }))
                .Take(candidateLimit)
                .ToList();
            var budget = new ConnectorByteBudget(
                scope.MaxBytes,
                transport.MaxBytes,
                jobs.Count * 4);
            var client = httpClientFactory.CreateClient();

            // Establish the state of every selected job before requesting any of the
            // larger detail collections. This keeps one job's allocations from hiding
            // a later job's primary state when the source budget is tight.
            foreach (var job in jobs)
            {
                var operation = $"GET /v1/job/{{id}} ({job.Namespace}/{job.Name})";
                var allowance = budget.BeginOperation(operation);
                if (allowance <= 0) continue;
                try
                {
                    using var jobRequest = ConnectorUtilities.CreateRequest(
                        HttpMethod.Get, job.Url, transport, credentials);
                    SetNomadToken(jobRequest, transport, credentials);
                    using var jobResponse = await client.SendAsync(
                        jobRequest, HttpCompletionOption.ResponseHeadersRead, ct);
                    using var jobJson = await ConnectorUtilities.ReadBoundedJsonAsync(
                        jobResponse,
                        budget.SafeReadLimit(allowance, jobResponse.Content),
                        ct,
                        budget.ObserveBytesRead);
                    var status = ConnectorUtilities.Text(jobJson.RootElement, "Status");
                    var modifyTime = ConnectorUtilities.Timestamp(jobJson.RootElement, "SubmitTime", scope.End);
                    var summary = $"Nomad job {job.Namespace}/{job.Name} is {status}";
                    var severity = status == "running" ? "info" : "warning";
                    findings.Add(new EvidenceFinding(
                        ConnectorUtilities.Id(Source, "job", job.Namespace, job.Name), Source, modifyTime, null,
                        status == "running" ? "workload" : "workload-failure", severity, summary, null, job.Url,
                        status == "running" ? 0.8 : 0.95,
                        ConnectorUtilities.Provenance("GET /v1/job/{id}", new
                        {
                            @namespace = job.Namespace,
                            job = job.Name
                        }),
                        ObjectType: "nomad-job", ObjectId: $"{job.Namespace}/{job.Name}"));
                    timeline.Add(new TimelineCandidate(modifyTime, Source, "job-state", summary, severity, job.Url));
                    links.Add(new SourceLink($"Nomad {job.Namespace}/{job.Name}", job.Url));
                }
                catch (InvalidOperationException exception) when (ConnectorUtilities.IsByteLimitException(exception))
                {
                    budget.RecordLimited(operation);
                }
            }

            foreach (var job in jobs)
            {
                var allocationsUrl = ConnectorUtilities.Url(
                    transport,
                    $"v1/job/{job.EncodedName}/allocations?{job.Query}&all=true");
                var operation = $"GET /v1/job/{{id}}/allocations ({job.Namespace}/{job.Name})";
                var allowance = budget.BeginOperation(operation);
                if (allowance <= 0) continue;
                try
                {
                    using var allocationRequest = ConnectorUtilities.CreateRequest(
                        HttpMethod.Get, allocationsUrl, transport, credentials);
                    SetNomadToken(allocationRequest, transport, credentials);
                    using var allocationResponse = await client.SendAsync(
                        allocationRequest, HttpCompletionOption.ResponseHeadersRead, ct);
                    using var allocationJson = await ConnectorUtilities.ReadBoundedJsonAsync(
                        allocationResponse,
                        budget.SafeReadLimit(allowance, allocationResponse.Content),
                        ct,
                        budget.ObserveBytesRead);
                    foreach (var allocation in allocationJson.RootElement.EnumerateArray().Take(transport.MaxItems))
                    {
                        var clientStatus = ConnectorUtilities.Text(allocation, "ClientStatus");
                        if (clientStatus is "running" or "complete") continue;
                        var allocationId = ConnectorUtilities.Text(allocation, "ID");
                        var at = ConnectorUtilities.Timestamp(allocation, "ModifyTime", scope.End);
                        var allocationSummary = $"Allocation {allocationId[..Math.Min(8, allocationId.Length)]} is {clientStatus}";
                        findings.Add(new EvidenceFinding(
                            ConnectorUtilities.Id(Source, "allocation", allocationId), Source, at, null,
                            "workload-failure", "warning", allocationSummary,
                            ConnectorUtilities.Truncate(allocation.ToString(), 1000), allocationsUrl, 0.9,
                            ConnectorUtilities.Provenance("GET /v1/job/{id}/allocations", new
                            {
                                @namespace = job.Namespace,
                                job = job.Name
                            }),
                            ObjectType: "nomad-allocation", ObjectId: allocationId));
                        timeline.Add(new TimelineCandidate(at, Source, "allocation-state", allocationSummary, "warning", allocationsUrl));
                    }
                }
                catch (InvalidOperationException exception) when (ConnectorUtilities.IsByteLimitException(exception))
                {
                    budget.RecordLimited(operation);
                }
            }

            foreach (var operationName in new[] { "deployments", "evaluations" })
            {
                foreach (var job in jobs)
                {
                    var operationUrl = ConnectorUtilities.Url(transport,
                        $"v1/job/{job.EncodedName}/{operationName}?{job.Query}");
                    var operation = $"GET /v1/job/{{id}}/{operationName} ({job.Namespace}/{job.Name})";
                    var allowance = budget.BeginOperation(operation);
                    if (allowance <= 0) continue;
                    try
                    {
                        using var operationRequest = ConnectorUtilities.CreateRequest(HttpMethod.Get, operationUrl, transport, credentials);
                        SetNomadToken(operationRequest, transport, credentials);
                        using var operationResponse = await client.SendAsync(
                            operationRequest, HttpCompletionOption.ResponseHeadersRead, ct);
                        using var operationJson = await ConnectorUtilities.ReadBoundedJsonAsync(
                            operationResponse,
                            budget.SafeReadLimit(allowance, operationResponse.Content),
                            ct,
                            budget.ObserveBytesRead);
                        foreach (var item in operationJson.RootElement.EnumerateArray().Take(transport.MaxItems))
                        {
                            var itemId = ConnectorUtilities.Text(item, "ID");
                            var itemStatus = ConnectorUtilities.Text(item, "Status");
                            var at = ConnectorUtilities.Timestamp(item, "ModifyTime",
                                ConnectorUtilities.Timestamp(item, "CreateTime", scope.End));
                            var kind = operationName == "deployments" ? "deployment" : "evaluation";
                            var healthy = itemStatus is "successful" or "complete";
                            var itemSummary = $"Nomad {kind} {itemId[..Math.Min(8, itemId.Length)]} is {itemStatus}";
                            findings.Add(new EvidenceFinding(
                                ConnectorUtilities.Id(Source, kind, itemId), Source, at, null,
                                healthy ? $"nomad-{kind}" : "workload-failure",
                                healthy ? "info" : "warning", itemSummary,
                                ConnectorUtilities.Truncate(item.ToString(), 1000), operationUrl, healthy ? 0.8 : 0.95,
                                ConnectorUtilities.Provenance($"GET /v1/job/{{id}}/{operationName}", new
                                {
                                    @namespace = job.Namespace,
                                    job = job.Name
                                }), ObjectType: $"nomad-{kind}", ObjectId: itemId));
                            timeline.Add(new TimelineCandidate(at, Source, kind, itemSummary,
                                healthy ? "info" : "warning", operationUrl));
                        }
                    }
                    catch (InvalidOperationException exception) when (ConnectorUtilities.IsByteLimitException(exception))
                    {
                        budget.RecordLimited(operation);
                    }
                }
            }
            var rankedFindings = EvidenceRankingPolicy.Rank(findings, context.TriggeredAt);
            var orderedTimeline = timeline.OrderBy(item => item.OccurredAt).ToList();
            var distinctLinks = links.Distinct().ToList();
            var itemsTruncated = rankedFindings.Count > sourceMaximumItems
                                 || orderedTimeline.Count > sourceMaximumItems
                                 || distinctLinks.Count > sourceMaximumItems;
            var diagnostic = ConnectorUtilities.CombineDiagnostics(
                budget.Diagnostic,
                itemsTruncated
                    ? $"Source item limit {sourceMaximumItems} truncated findings, timeline entries, or links."
                    : null);
            return new ConnectorResult(
                Source,
                budget.IsPartial || itemsTruncated ? SourceHealth.Partial : SourceHealth.Complete,
                rankedFindings.Take(sourceMaximumItems).ToList(),
                orderedTimeline.Take(sourceMaximumItems).ToList(),
                distinctLinks.Take(sourceMaximumItems).ToList(),
                0,
                diagnostic);
        }, cancellationToken);
    }

    private static void SetNomadToken(
        HttpRequestMessage request,
        ConnectorTransport transport,
        ICredentialProvider credentials)
    {
        request.Headers.Authorization = null;
        var token = credentials.Get(transport.CredentialEnv);
        if (!string.IsNullOrWhiteSpace(token)) request.Headers.TryAddWithoutValidation("X-Nomad-Token", token);
    }

    private sealed record NomadJobTarget(
        string Namespace,
        string Name,
        string EncodedName,
        string Query,
        string Url);
}
