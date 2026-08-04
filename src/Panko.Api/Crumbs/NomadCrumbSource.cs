using System.Text.Json;
using Panko.Api.Domain;
using Panko.Api.Cases;
using Panko.Api.Infrastructure;
using Panko.Api.Options;

namespace Panko.Api.Crumbs;

public sealed class NomadCrumbSource(
    IHttpClientFactory httpClientFactory,
    IMcpCrumbSourceAdapter mcp,
    CrumbSourceConfiguration crumbSources,
    ICredentialProvider credentials) : ICrumbSourceAdapter
{
    public string Source => CrumbSourceRegistry.Nomad;
    public bool SupportsWindowExpansion => false;

    public Task<CrumbSourceResult> CollectAsync(CaseContext context, CrumbScope scope, CancellationToken cancellationToken)
    {
        var configuration = context.Recipe.Nomad;
        if (configuration is null) return Task.FromResult(CrumbSourceResult.Excluded(Source));
        var transport = crumbSources.For(Source);
        return CrumbSourceUtilities.CollectAsync(
            Source, transport, mcp, context, scope,
            new
            {
                configuration.Region,
                namespaces = configuration.Namespaces.Select(item => new { item.Name, item.Jobs })
            }, async ct =>
        {
            var crumbs = new List<Crumb>();
            var trail = new List<TrailCandidate>();
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
                        CrumbSourceUtilities.Url(transport, $"v1/job/{encodedJob}?{query}"));
                }))
                .Take(candidateLimit)
                .ToList();
            var budget = new CrumbSourceResponseBudget(
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
                var jobJson = await budget.TryReadJsonAsync(
                    operation,
                    async operationCancellationToken =>
                    {
                        using var jobRequest = CrumbSourceUtilities.CreateRequest(
                            HttpMethod.Get, job.Url, transport, credentials);
                        SetNomadToken(jobRequest, transport, credentials);
                        return await client.SendAsync(
                            jobRequest,
                            HttpCompletionOption.ResponseHeadersRead,
                            operationCancellationToken);
                    },
                    ct);
                if (jobJson is null) continue;
                using (jobJson)
                {
                    var status = CrumbSourceUtilities.Text(jobJson.RootElement, "Status");
                    var modifyTime = CrumbSourceUtilities.Timestamp(jobJson.RootElement, "SubmitTime", scope.End);
                    var summary = $"Nomad job {job.Namespace}/{job.Name} is {status}";
                    var severity = status == "running" ? "info" : "warning";
                    crumbs.Add(new Crumb(
                        CrumbSourceUtilities.Id(Source, "job", job.Namespace, job.Name), Source, modifyTime, null,
                        status == "running" ? "workload" : "workload-failure", severity, summary, null, job.Url,
                        status == "running" ? 0.8 : 0.95,
                        CrumbSourceUtilities.Provenance("GET /v1/job/{id}", new
                        {
                            @namespace = job.Namespace,
                            job = job.Name
                        }),
                        ObjectType: "nomad-job", ObjectId: $"{job.Namespace}/{job.Name}"));
                    trail.Add(new TrailCandidate(modifyTime, Source, "job-state", summary, severity, job.Url));
                    links.Add(new SourceLink($"Nomad {job.Namespace}/{job.Name}", job.Url));
                }
            }

            foreach (var job in jobs)
            {
                var allocationsUrl = CrumbSourceUtilities.Url(
                    transport,
                    $"v1/job/{job.EncodedName}/allocations?{job.Query}&all=true");
                var operation = $"GET /v1/job/{{id}}/allocations ({job.Namespace}/{job.Name})";
                var allocationJson = await budget.TryReadJsonAsync(
                    operation,
                    async operationCancellationToken =>
                    {
                        using var allocationRequest = CrumbSourceUtilities.CreateRequest(
                            HttpMethod.Get, allocationsUrl, transport, credentials);
                        SetNomadToken(allocationRequest, transport, credentials);
                        return await client.SendAsync(
                            allocationRequest,
                            HttpCompletionOption.ResponseHeadersRead,
                            operationCancellationToken);
                    },
                    ct);
                if (allocationJson is null) continue;
                using (allocationJson)
                {
                    foreach (var allocation in allocationJson.RootElement.EnumerateArray().Take(transport.MaxItems))
                    {
                        var clientStatus = CrumbSourceUtilities.Text(allocation, "ClientStatus");
                        if (clientStatus is "running" or "complete") continue;
                        var allocationId = CrumbSourceUtilities.Text(allocation, "ID");
                        var at = CrumbSourceUtilities.Timestamp(allocation, "ModifyTime", scope.End);
                        var allocationSummary = $"Allocation {allocationId[..Math.Min(8, allocationId.Length)]} is {clientStatus}";
                        crumbs.Add(new Crumb(
                            CrumbSourceUtilities.Id(Source, "allocation", allocationId), Source, at, null,
                            "workload-failure", "warning", allocationSummary,
                            CrumbSourceUtilities.Truncate(allocation.ToString(), 1000), allocationsUrl, 0.9,
                            CrumbSourceUtilities.Provenance("GET /v1/job/{id}/allocations", new
                            {
                                @namespace = job.Namespace,
                                job = job.Name
                            }),
                            ObjectType: "nomad-allocation", ObjectId: allocationId));
                        trail.Add(new TrailCandidate(at, Source, "allocation-state", allocationSummary, "warning", allocationsUrl));
                    }
                }
            }

            foreach (var operationName in new[] { "deployments", "evaluations" })
            {
                foreach (var job in jobs)
                {
                    var operationUrl = CrumbSourceUtilities.Url(transport,
                        $"v1/job/{job.EncodedName}/{operationName}?{job.Query}");
                    var operation = $"GET /v1/job/{{id}}/{operationName} ({job.Namespace}/{job.Name})";
                    var operationJson = await budget.TryReadJsonAsync(
                        operation,
                        async operationCancellationToken =>
                        {
                            using var operationRequest = CrumbSourceUtilities.CreateRequest(
                                HttpMethod.Get, operationUrl, transport, credentials);
                            SetNomadToken(operationRequest, transport, credentials);
                            return await client.SendAsync(
                                operationRequest,
                                HttpCompletionOption.ResponseHeadersRead,
                                operationCancellationToken);
                        },
                        ct);
                    if (operationJson is null) continue;
                    using (operationJson)
                    {
                        foreach (var item in operationJson.RootElement.EnumerateArray().Take(transport.MaxItems))
                        {
                            var itemId = CrumbSourceUtilities.Text(item, "ID");
                            var itemStatus = CrumbSourceUtilities.Text(item, "Status");
                            var at = CrumbSourceUtilities.Timestamp(item, "ModifyTime",
                                CrumbSourceUtilities.Timestamp(item, "CreateTime", scope.End));
                            var kind = operationName == "deployments" ? "deployment" : "evaluation";
                            var healthy = itemStatus is "successful" or "complete";
                            var itemSummary = $"Nomad {kind} {itemId[..Math.Min(8, itemId.Length)]} is {itemStatus}";
                            crumbs.Add(new Crumb(
                                CrumbSourceUtilities.Id(Source, kind, itemId), Source, at, null,
                                healthy ? $"nomad-{kind}" : "workload-failure",
                                healthy ? "info" : "warning", itemSummary,
                                CrumbSourceUtilities.Truncate(item.ToString(), 1000), operationUrl, healthy ? 0.8 : 0.95,
                                CrumbSourceUtilities.Provenance($"GET /v1/job/{{id}}/{operationName}", new
                                {
                                    @namespace = job.Namespace,
                                    job = job.Name
                                }), ObjectType: $"nomad-{kind}", ObjectId: itemId));
                            trail.Add(new TrailCandidate(at, Source, kind, itemSummary,
                                healthy ? "info" : "warning", operationUrl));
                        }
                    }
                }
            }
            var rankedCrumbs = CrumbRankingPolicy.Rank(crumbs, context.OpenedAt);
            var orderedTrail = trail.OrderBy(item => item.OccurredAt).ToList();
            var distinctLinks = links.Distinct().ToList();
            var itemsTruncated = rankedCrumbs.Count > sourceMaximumItems
                                 || orderedTrail.Count > sourceMaximumItems
                                 || distinctLinks.Count > sourceMaximumItems;
            var diagnostic = CrumbSourceUtilities.CombineDiagnostics(
                budget.Diagnostic,
                itemsTruncated
                    ? $"Source item limit {sourceMaximumItems} truncated Crumbs, Trail entries, or links."
                    : null);
            return new CrumbSourceResult(
                Source,
                budget.IsPartial || itemsTruncated ? CrumbSourceHealth.Partial : CrumbSourceHealth.Complete,
                rankedCrumbs.Take(sourceMaximumItems).ToList(),
                orderedTrail.Take(sourceMaximumItems).ToList(),
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
