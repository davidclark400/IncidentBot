using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using IncidentBot.Api.Domain;
using IncidentBot.Api.Incidents;

namespace IncidentBot.Api.Connectors;

public sealed partial class GitLabEvidenceConnector(
    IHttpClientFactory httpClientFactory,
    IMcpEvidenceAdapter mcp) : IIncidentEvidenceConnector
{
    private const int MaximumPipelineJobFindings = 10;
    private const int MaximumTraceExcerptCharacters = 6000;
    private const int GitLabPageSize = 100;
    private const int MaximumPaginationPages = 100;

    public string Source => EvidenceSourceRegistry.GitLab;

    public Task<ConnectorResult> CollectAsync(InvestigationContext context, EvidenceScope scope, CancellationToken cancellationToken)
    {
        var configuration = context.Profile.GitLab;
        if (configuration is null) return Task.FromResult(ConnectorResult.Excluded(Source));
        return ConnectorUtilities.CollectAsync(
            Source, configuration.Connector, mcp, context, scope,
            new { projects = configuration.Projects, includePipelineJobOutput = true }, async ct =>
        {
            var findings = new List<EvidenceFinding>();
            var timeline = new List<TimelineCandidate>();
            var links = new List<SourceLink>();
            var diagnostics = new List<string>();
            var failedPipelines = new List<GitLabPipelineTarget>();
            var sourceMaximumItems = Math.Max(0, Math.Min(scope.MaxItems, configuration.Connector.MaxItems));
            var reservedContextItems = sourceMaximumItems switch
            {
                <= 1 => 0,
                <= 3 => 1,
                _ => Math.Max(2, sourceMaximumItems / 3)
            };
            var maximumPipelineJobFindings = Math.Min(
                Math.Max(0, sourceMaximumItems - reservedContextItems),
                MaximumPipelineJobFindings);
            var sourceMaximumBytes = Math.Max(0, Math.Min(scope.MaxBytes, configuration.Connector.MaxBytes));
            var jobMetadataBytes = maximumPipelineJobFindings > 0 ? sourceMaximumBytes * 35 / 100 : 0;
            var maximumTraceBytes = maximumPipelineJobFindings > 0 ? sourceMaximumBytes * 30 / 100 : 0;
            var pipelineMetadataBytes = maximumPipelineJobFindings > 0
                ? sourceMaximumBytes * 25 / 100
                : sourceMaximumBytes * 30 / 100;
            var contextMetadataBytes = Math.Max(
                0, sourceMaximumBytes - jobMetadataBytes - maximumTraceBytes - pipelineMetadataBytes);
            var contextBudget = new GitLabByteBudget(contextMetadataBytes);
            var pipelineBudget = new GitLabByteBudget(pipelineMetadataBytes);
            var jobBudget = new GitLabByteBudget(jobMetadataBytes);
            foreach (var project in configuration.Projects)
            {
                var encodedProject = Uri.EscapeDataString(project.Id);
                var since = Uri.EscapeDataString(ConnectorUtilities.Iso(scope.Start));
                var until = Uri.EscapeDataString(ConnectorUtilities.Iso(scope.End));
                var mergeRequestsUrl = ConnectorUtilities.Url(configuration.Connector,
                    $"api/v4/projects/{encodedProject}/merge_requests?scope=all&state=merged&updated_after={since}&per_page={Math.Min(scope.MaxItems, configuration.Connector.MaxItems)}");
                using (var mergeRequest = ConnectorUtilities.CreateRequest(HttpMethod.Get, mergeRequestsUrl, configuration.Connector))
                using (var mergeResponse = await httpClientFactory.CreateClient().SendAsync(
                           mergeRequest, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    using var mergeJson = await ReadBudgetedJsonAsync(
                        mergeResponse, contextBudget, $"Merge requests for {project.Id}", diagnostics, ct);
                    if (mergeJson is not null)
                    {
                        foreach (var mergeRequestItem in mergeJson.RootElement.EnumerateArray())
                        {
                            var iid = ConnectorUtilities.Text(mergeRequestItem, "iid");
                            var title = ConnectorUtilities.Text(mergeRequestItem, "title");
                            var url = ConnectorUtilities.Text(mergeRequestItem, "web_url", mergeRequestsUrl);
                            var author = NestedText(mergeRequestItem, "author", "name")
                                ?? NestedText(mergeRequestItem, "author", "username") ?? "Unknown user";
                            var createdAt = ConnectorUtilities.Timestamp(mergeRequestItem, "created_at", scope.Start);
                            var mergeSha = ConnectorUtilities.Text(mergeRequestItem, "merge_commit_sha", "");
                            var mergedAt = ConnectorUtilities.Timestamp(mergeRequestItem, "merged_at", scope.End);
                            if (mergedAt < scope.Start || mergedAt > scope.End) continue;
                            var summary = $"{author} created MR !{iid}: {title}";
                            findings.Add(new EvidenceFinding(
                                ConnectorUtilities.Id(Source, project.Id, "mr-created", iid), Source, createdAt, null,
                                "merge-request-created", "info", summary, null, url, 0.95,
                                ConnectorUtilities.Provenance("GET merge_requests", new { project = project.Id, iid }),
                                author, string.IsNullOrWhiteSpace(mergeSha) ? "merge-request" : "commit",
                                string.IsNullOrWhiteSpace(mergeSha) ? $"{project.Id}!{iid}" : mergeSha));
                            timeline.Add(new TimelineCandidate(createdAt, Source, "merge-request-created", summary, "info", url,
                                author, "merge-request", $"{project.Id}!{iid}"));

                            var mergedBy = NestedText(mergeRequestItem, "merge_user", "name")
                                ?? NestedText(mergeRequestItem, "merge_user", "username") ?? author;
                            var mergedSummary = $"{mergedBy} merged MR !{iid}: {title}";
                            findings.Add(new EvidenceFinding(
                                ConnectorUtilities.Id(Source, project.Id, "mr-merged", iid), Source, mergedAt, null,
                                "merge-request-merged", "info", mergedSummary, null, url, 0.98,
                                ConnectorUtilities.Provenance("GET merge_requests", new { project = project.Id, iid, mergeSha }),
                                mergedBy, "commit", string.IsNullOrWhiteSpace(mergeSha) ? $"{project.Id}!{iid}" : mergeSha));
                            timeline.Add(new TimelineCandidate(mergedAt, Source, "merge-request-merged", mergedSummary, "info", url,
                                mergedBy, "commit", string.IsNullOrWhiteSpace(mergeSha) ? null : mergeSha));
                        }
                    }
                }

                var commitsUrl = ConnectorUtilities.Url(configuration.Connector,
                    $"api/v4/projects/{encodedProject}/repository/commits?ref_name={Uri.EscapeDataString(project.Branch)}&since={since}&until={until}&per_page={Math.Min(scope.MaxItems, configuration.Connector.MaxItems)}");
                using var commitRequest = ConnectorUtilities.CreateRequest(HttpMethod.Get, commitsUrl, configuration.Connector);
                using var commitResponse = await httpClientFactory.CreateClient().SendAsync(
                    commitRequest, HttpCompletionOption.ResponseHeadersRead, ct);
                using var commitJson = await ReadBudgetedJsonAsync(
                    commitResponse, contextBudget, $"Commits for {project.Id}", diagnostics, ct);
                foreach (var commit in commitJson?.RootElement.EnumerateArray()
                             .Take(Math.Min(5, configuration.Connector.MaxItems)) ?? [])
                {
                    var id = ConnectorUtilities.Text(commit, "id");
                    var shortId = ConnectorUtilities.Text(commit, "short_id", id[..Math.Min(8, id.Length)]);
                    var title = ConnectorUtilities.Text(commit, "title");
                    var at = ConnectorUtilities.Timestamp(commit, "committed_date", scope.End);
                    var url = ConnectorUtilities.Text(commit, "web_url", commitsUrl);
                    var summary = $"Commit {shortId}: {title}";
                    findings.Add(new EvidenceFinding(
                        ConnectorUtilities.Id(Source, project.Id, id), Source, at, null, "code-change", "info",
                        summary, ConnectorUtilities.Truncate(ConnectorUtilities.Text(commit, "message", title), 1000), url, 0.75,
                        ConnectorUtilities.Provenance("GET repository/commits", new { project = project.Id, project.Branch })));
                    timeline.Add(new TimelineCandidate(at, Source, "commit", summary, "info", url));

                    var diffUrl = ConnectorUtilities.Url(configuration.Connector,
                        $"api/v4/projects/{encodedProject}/repository/commits/{Uri.EscapeDataString(id)}/diff?per_page=30");
                    using var diffRequest = ConnectorUtilities.CreateRequest(HttpMethod.Get, diffUrl, configuration.Connector);
                    using var diffResponse = await httpClientFactory.CreateClient().SendAsync(
                        diffRequest, HttpCompletionOption.ResponseHeadersRead, ct);
                    using var diffJson = await ReadBudgetedJsonAsync(
                        diffResponse, contextBudget, $"Commit {shortId} diff", diagnostics, ct);
                    var relevantDiffs = (diffJson?.RootElement.EnumerateArray() ?? [])
                        .Where(diff => IsRelevantPath(ConnectorUtilities.Text(diff, "new_path"), project.RelevantPaths)
                            || IsRelevantPath(ConnectorUtilities.Text(diff, "old_path"), project.RelevantPaths))
                        .Take(10)
                        .ToList();
                    if (relevantDiffs.Count > 0)
                    {
                        var changedPaths = relevantDiffs.Select(diff => ConnectorUtilities.Text(diff, "new_path")).Distinct().ToList();
                        var codeReferences = ExtractCodeReferences(
                            configuration.Connector.BaseUrl, project.Id, id, relevantDiffs);
                        var excerpt = string.Join("\n\n", relevantDiffs.Select(diff =>
                            $"--- {ConnectorUtilities.Text(diff, "new_path")}\n{ConnectorUtilities.Text(diff, "diff", "")}"));
                        findings.Add(new EvidenceFinding(
                            ConnectorUtilities.Id(Source, project.Id, id, "diff"), Source, at, null, "code-diff", "info",
                            $"Commit {shortId} changed {changedPaths.Count} allowlisted path{(changedPaths.Count == 1 ? "" : "s")}: {string.Join(", ", changedPaths)}",
                            ConnectorUtilities.Truncate(excerpt, 3000), url, 0.85,
                            ConnectorUtilities.Provenance("GET repository/commits/{sha}/diff", new
                            {
                                project = project.Id,
                                commit = id,
                                allowedPaths = project.RelevantPaths
                            }), ObjectType: "commit", ObjectId: id, CodeReferences: codeReferences));
                    }
                }

                var pipelinesUrl = ConnectorUtilities.Url(configuration.Connector,
                    $"api/v4/projects/{encodedProject}/pipelines?ref={Uri.EscapeDataString(project.Branch)}&updated_after={since}&updated_before={until}&per_page={GitLabPageSize}");
                var pipelineItems = await ReadPaginatedJsonArrayAsync(
                    httpClientFactory.CreateClient(), configuration.Connector, pipelinesUrl,
                    $"Pipelines for {project.Id}", diagnostics, pipelineBudget, ct);
                var childPipelinesUrl = ConnectorUtilities.Url(configuration.Connector,
                    $"api/v4/projects/{encodedProject}/pipelines?source=parent_pipeline&ref={Uri.EscapeDataString(project.Branch)}&updated_after={since}&updated_before={until}&per_page={GitLabPageSize}");
                try
                {
                    pipelineItems.AddRange(await ReadPaginatedJsonArrayAsync(
                        httpClientFactory.CreateClient(), configuration.Connector, childPipelinesUrl,
                        $"Child pipelines for {project.Id}", diagnostics, pipelineBudget, ct));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    // Older GitLab versions may not support source=parent_pipeline. Keep the parent
                    // pipeline evidence instead of making the entire source unavailable.
                    diagnostics.Add($"Child pipelines for {project.Id} unavailable: {exception.GetType().Name}");
                }

                foreach (var pipeline in pipelineItems
                    .DistinctBy(item => ConnectorUtilities.Text(item, "id"), StringComparer.Ordinal))
                {
                    var pipelineId = ConnectorUtilities.Text(pipeline, "id");
                    var status = ConnectorUtilities.Text(pipeline, "status");
                    var at = ConnectorUtilities.Timestamp(pipeline, "updated_at", scope.End);
                    var url = ConnectorUtilities.Text(pipeline, "web_url", pipelinesUrl);
                    var summary = $"Pipeline {pipelineId} on {project.Branch} is {status}";
                    var severity = IsFailedPipeline(status) ? "warning" : "info";
                    findings.Add(new EvidenceFinding(
                        ConnectorUtilities.Id(Source, project.Id, "pipeline", pipelineId), Source, at, null,
                        "pipeline", severity, summary, null, url, 0.85,
                        ConnectorUtilities.Provenance("GET pipelines", new
                        {
                            project = project.Id,
                            project.Branch,
                            pipelineId,
                            status
                        }),
                        ObjectType: "pipeline", ObjectId: pipelineId));
                    timeline.Add(new TimelineCandidate(at, Source, "pipeline", summary,
                        severity, url, ObjectType: "pipeline", ObjectId: pipelineId));

                    if (IsFailedPipeline(status))
                    {
                        failedPipelines.Add(new GitLabPipelineTarget(
                            project.Id, encodedProject, pipelineId, status, at, url));
                    }
                }

                foreach (var environment in project.Environments)
                {
                    var deploymentsUrl = ConnectorUtilities.Url(configuration.Connector,
                        $"api/v4/projects/{encodedProject}/deployments?environment={Uri.EscapeDataString(environment)}&updated_after={since}&updated_before={until}&per_page={Math.Min(scope.MaxItems, configuration.Connector.MaxItems)}");
                    using var deploymentRequest = ConnectorUtilities.CreateRequest(HttpMethod.Get, deploymentsUrl, configuration.Connector);
                    using var deploymentResponse = await httpClientFactory.CreateClient().SendAsync(
                        deploymentRequest, HttpCompletionOption.ResponseHeadersRead, ct);
                    using var deploymentJson = await ReadBudgetedJsonAsync(
                        deploymentResponse, contextBudget, $"Deployments for {project.Id}/{environment}", diagnostics, ct);
                    foreach (var deployment in deploymentJson?.RootElement.EnumerateArray() ?? [])
                    {
                        var deploymentId = ConnectorUtilities.Text(deployment, "id");
                        var status = ConnectorUtilities.Text(deployment, "status");
                        var at = ConnectorUtilities.Timestamp(deployment, "finished_at",
                            ConnectorUtilities.Timestamp(deployment, "updated_at",
                                ConnectorUtilities.Timestamp(deployment, "created_at", scope.End)));
                        var actor = NestedText(deployment, "user", "name")
                            ?? NestedText(deployment, "user", "username") ?? "GitLab CI";
                        var sha = ConnectorUtilities.Text(deployment, "sha", "");
                        var summary = $"{actor} deployed {ShortSha(sha)} to {environment}; deployment {deploymentId} is {status}";
                        findings.Add(new EvidenceFinding(
                            ConnectorUtilities.Id(Source, project.Id, environment, deploymentId), Source, at, null,
                            "deployment", status == "success" ? "info" : "warning", summary,
                            ConnectorUtilities.Truncate(deployment.ToString(), 1000), deploymentsUrl, 0.95,
                            ConnectorUtilities.Provenance("GET deployments", new { project = project.Id, environment, sha }),
                            actor, "commit", string.IsNullOrWhiteSpace(sha) ? deploymentId : sha));
                        timeline.Add(new TimelineCandidate(at, Source, "deployment", summary,
                            status == "success" ? "info" : "warning", deploymentsUrl,
                            actor, "commit", string.IsNullOrWhiteSpace(sha) ? null : sha));
                    }
                }

                links.Add(new SourceLink($"GitLab {project.Id}",
                    $"{configuration.Connector.BaseUrl.TrimEnd('/')}/{project.Id.TrimStart('/')}"));
                foreach (var path in project.RelevantPaths)
                {
                    links.Add(new SourceLink($"Code: {path}",
                        $"{configuration.Connector.BaseUrl.TrimEnd('/')}/{project.Id.TrimStart('/')}/-/tree/{Uri.EscapeDataString(project.Branch)}/{path.TrimStart('/')}"));
                }
            }

            if (maximumPipelineJobFindings > 0 && failedPipelines.Count > 0)
            {
                var pipelineDiscoveryLimit = Math.Max(maximumPipelineJobFindings * 4, 40);
                var rankedPipelineCandidates = failedPipelines
                    .OrderBy(item => item.Status == "failed" ? 0 : 1)
                    .ThenBy(item => Math.Abs((item.OccurredAt - context.TriggeredAt).TotalSeconds))
                    .ThenByDescending(item => item.OccurredAt)
                    .ToList();
                if (rankedPipelineCandidates.Count > pipelineDiscoveryLimit)
                {
                    diagnostics.Add(
                        $"Pipeline job discovery limited to {pipelineDiscoveryLimit} of {rankedPipelineCandidates.Count} failed or canceled pipelines");
                }
                var jobOutput = await CollectPipelineJobOutputAsync(
                    httpClientFactory.CreateClient(), configuration.Connector,
                    rankedPipelineCandidates.Take(pipelineDiscoveryLimit).ToList(),
                    maximumPipelineJobFindings, maximumTraceBytes, scope.End, jobBudget, ct);
                findings.AddRange(jobOutput.Findings);
                timeline.AddRange(jobOutput.Timeline);
                diagnostics.AddRange(jobOutput.Diagnostics);
            }

            var referencesByCommit = findings
                .Where(finding => finding.ObjectType == "commit" && !string.IsNullOrWhiteSpace(finding.ObjectId)
                    && finding.CodeReferences is { Count: > 0 })
                .GroupBy(finding => finding.ObjectId!, StringComparer.Ordinal)
                .ToDictionary(group => group.Key,
                    group => (IReadOnlyList<CodeReference>)group.SelectMany(item => item.CodeReferences ?? [])
                        .DistinctBy(reference => reference.Id, StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
            var enrichedFindings = findings.Select(finding =>
                finding.ObjectType == "commit" && finding.ObjectId is not null
                    && referencesByCommit.TryGetValue(finding.ObjectId, out var references)
                    ? finding with { CodeReferences = references }
                    : finding).ToList();
            var selectedJobFindings = EvidenceRankingPolicy.Rank(
                    enrichedFindings.Where(item => item.Category == "pipeline-job-output"),
                    context.TriggeredAt)
                .Take(maximumPipelineJobFindings)
                .ToList();
            var contextSlots = Math.Max(0, sourceMaximumItems - selectedJobFindings.Count);
            var selectedContext = EvidenceRankingPolicy.SelectDiverse(
                enrichedFindings.Where(item => item.Category != "pipeline-job-output"),
                context.TriggeredAt,
                contextSlots,
                maximumPerGroup: 2,
                maximumPerSource: sourceMaximumItems);
            var selectedFindings = EvidenceRankingPolicy.Rank(
                    selectedJobFindings.Concat(selectedContext),
                    context.TriggeredAt)
                .Take(sourceMaximumItems)
                .ToList();
            var distinctFindingCount = enrichedFindings.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count();
            if (distinctFindingCount > selectedFindings.Count)
            {
                diagnostics.Add($"Evidence selection retained {selectedFindings.Count} of {distinctFindingCount} GitLab findings");
            }
            var rankedTimeline = timeline
                .DistinctBy(item => $"{item.OccurredAt:O}|{item.Kind}|{item.Summary}", StringComparer.Ordinal)
                .OrderByDescending(item => item.Kind == "pipeline-job")
                .ThenBy(item => PipelineTimelineRank(item))
                .ThenBy(item => item.OccurredAt)
                .ToList();
            var selectedTimeline = rankedTimeline.Take(sourceMaximumItems)
                .OrderBy(item => item.OccurredAt)
                .ToList();
            if (rankedTimeline.Count > selectedTimeline.Count)
            {
                diagnostics.Add($"Timeline selection retained {selectedTimeline.Count} of {rankedTimeline.Count} GitLab events");
            }
            var distinctLinks = links
                .GroupBy(item => item.Url, StringComparer.Ordinal)
                .Select(group => group.OrderBy(item => item.Label, StringComparer.Ordinal).First())
                .OrderBy(item => item.Label, StringComparer.Ordinal)
                .ThenBy(item => item.Url, StringComparer.Ordinal)
                .ToList();
            var selectedLinks = distinctLinks.Take(sourceMaximumItems).ToList();
            if (distinctLinks.Count > selectedLinks.Count)
            {
                diagnostics.Add($"Link selection retained {selectedLinks.Count} of {distinctLinks.Count} GitLab links");
            }

            return new ConnectorResult(Source, diagnostics.Count == 0 ? SourceHealth.Complete : SourceHealth.Partial,
                selectedFindings,
                selectedTimeline, selectedLinks, 0,
                diagnostics.Count == 0 ? null : ConnectorUtilities.Truncate(string.Join("; ", diagnostics), 450));
        }, cancellationToken);
    }

    private static async Task<PipelineJobOutput> CollectPipelineJobOutputAsync(
        HttpClient client,
        ConnectorTransport connector,
        IReadOnlyList<GitLabPipelineTarget> pipelines,
        int maximumFindings,
        int maximumTraceBytes,
        DateTimeOffset fallbackTimestamp,
        GitLabByteBudget jobBudget,
        CancellationToken cancellationToken)
    {
        var findings = new List<EvidenceFinding>();
        var timeline = new List<TimelineCandidate>();
        var diagnostics = new List<string>();

        var discoveries = new List<PipelineJobDiscovery>();
        foreach (var pipeline in pipelines
            .DistinctBy(item => $"{item.ProjectId}\u001f{item.PipelineId}", StringComparer.Ordinal))
        {
            try
            {
                discoveries.Add(await DiscoverPipelineJobsAsync(
                    client, connector, pipeline, fallbackTimestamp, diagnostics, jobBudget, cancellationToken));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                diagnostics.Add($"Pipeline {pipeline.PipelineId} jobs unavailable: {exception.GetType().Name}");
            }
        }

        var selectedJobs = RoundRobin(discoveries, JobDisposition.HardFailure)
            .Concat(RoundRobin(discoveries, JobDisposition.AllowedFailure))
            .Take(maximumFindings)
            .ToList();
        var hardFailureOrdinals = discoveries
            .SelectMany(discovery => discovery.Jobs
                .Where(job => job.Disposition == JobDisposition.HardFailure)
                .OrderBy(job => job.OccurredAt)
                .ThenBy(job => JobIdOrder(job.Id))
                .Select((job, ordinal) => new
                {
                    Key = JobSelectionKey(discovery.Pipeline, job),
                    Ordinal = ordinal + 1
                }))
            .ToDictionary(item => item.Key, item => item.Ordinal, StringComparer.Ordinal);
        var traceBudgets = AllocateTraceBudgets(maximumTraceBytes, selectedJobs.Count);
        for (var index = 0; index < selectedJobs.Count; index++)
        {
            var selected = selectedJobs[index];
            var job = selected.Job;
            string? excerpt = null;
            TraceReadResult? traceResult = null;
            if (traceBudgets[index].RetainedBytes > 0)
            {
                var traceUrl = ConnectorUtilities.Url(connector,
                    $"api/v4/projects/{selected.Pipeline.EncodedProject}/jobs/{Uri.EscapeDataString(job.Id)}/trace");
                try
                {
                    traceResult = await FetchUsefulTraceTailAsync(
                        client, connector, traceUrl, traceBudgets[index], cancellationToken);
                    excerpt = PrepareTraceExcerpt(traceResult.Text, connector);
                    if (!traceResult.TailVerified)
                    {
                        diagnostics.Add(
                            $"Pipeline {selected.Pipeline.PipelineId} job {job.Id} trace exceeded its "
                            + $"{traceBudgets[index].ScanBytes}-byte scan budget; excerpt is not a verified trace tail");
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    diagnostics.Add(
                        $"Pipeline {selected.Pipeline.PipelineId} job {job.Id} trace unavailable: {exception.GetType().Name}");
                }
            }

            var reason = string.IsNullOrWhiteSpace(job.FailureReason)
                ? ""
                : $"; failure reason: {job.FailureReason}";
            var retries = job.AttemptCount > 1
                ? $" after {job.AttemptCount} failed or canceled attempts"
                : "";
            var allowed = job.AllowFailure ? " (allowed to fail)" : "";
            var summary = $"Job {job.Name} in pipeline {selected.Pipeline.PipelineId} ({job.Stage}) is {job.Status}{allowed}{retries}{reason}";
            var severity = job.Disposition == JobDisposition.HardFailure ? "critical" : "warning";
            var failureOrdinal = hardFailureOrdinals.GetValueOrDefault(JobSelectionKey(selected.Pipeline, job));
            findings.Add(new EvidenceFinding(
                ConnectorUtilities.Id(EvidenceSourceRegistry.GitLab, selected.Pipeline.ProjectId,
                    "pipeline", selected.Pipeline.PipelineId, "job-family", job.Stage, job.Name),
                EvidenceSourceRegistry.GitLab, job.OccurredAt, null, "pipeline-job-output", severity,
                summary, excerpt, job.Url, job.Disposition == JobDisposition.HardFailure ? 0.98 : 0.90,
                ConnectorUtilities.Provenance(
                    "GET pipelines/{pipeline_id}/jobs?scope[]=failed&scope[]=canceled and jobs/{job_id}/trace",
                    new
                    {
                        project = selected.Pipeline.ProjectId,
                        pipelineId = selected.Pipeline.PipelineId,
                        jobId = job.Id,
                        jobName = job.Name,
                        stage = job.Stage,
                        status = job.Status,
                        failureReason = job.FailureReason,
                        allowFailure = job.AllowFailure,
                        failureOrdinal,
                        firstHardFailure = failureOrdinal == 1,
                        retryAttemptCount = job.AttemptCount,
                        traceRetainedByteBudget = traceBudgets[index].RetainedBytes,
                        traceScanByteBudget = traceBudgets[index].ScanBytes,
                        traceBytesScanned = traceResult?.BytesScanned ?? 0,
                        traceTailVerified = traceResult?.TailVerified ?? false
                    }), job.Actor, "pipeline-job", job.Id));
            timeline.Add(new TimelineCandidate(
                job.OccurredAt, EvidenceSourceRegistry.GitLab, "pipeline-job", summary, severity,
                job.Url, job.Actor, "pipeline-job", job.Id));
        }

        var cancellationSlots = maximumFindings - selectedJobs.Count;
        foreach (var discovery in discoveries
            .Where(item => item.Jobs.Any(job => job.Disposition == JobDisposition.Canceled))
            .Take(cancellationSlots))
        {
            var canceledJobs = discovery.Jobs
                .Where(job => job.Disposition == JobDisposition.Canceled)
                .OrderBy(job => job.OccurredAt)
                .ToList();
            var stages = canceledJobs.Select(job => job.Stage).Distinct(StringComparer.Ordinal).Order().ToList();
            var sampleNames = canceledJobs.Select(job => job.Name).Distinct(StringComparer.Ordinal).Take(5).ToList();
            var canceledAttempts = canceledJobs.Sum(job => job.AttemptCount);
            var at = canceledJobs.Max(job => job.OccurredAt);
            var summary = $"Pipeline {discovery.Pipeline.PipelineId} canceled {canceledJobs.Count} job"
                + $"{(canceledJobs.Count == 1 ? "" : "s")} across {stages.Count} stage{(stages.Count == 1 ? "" : "s")}: "
                + string.Join(", ", sampleNames)
                + (sampleNames.Count < canceledJobs.Count ? ", …" : "");
            findings.Add(new EvidenceFinding(
                ConnectorUtilities.Id(EvidenceSourceRegistry.GitLab, discovery.Pipeline.ProjectId,
                    "pipeline", discovery.Pipeline.PipelineId, "canceled-jobs"),
                EvidenceSourceRegistry.GitLab, at, null, "pipeline-job-output", "warning", summary, null,
                discovery.Pipeline.Url, 0.90,
                ConnectorUtilities.Provenance("GET pipelines/{pipeline_id}/jobs?scope[]=failed&scope[]=canceled", new
                {
                    project = discovery.Pipeline.ProjectId,
                    pipelineId = discovery.Pipeline.PipelineId,
                    pipelineStatus = discovery.Pipeline.Status,
                    status = "canceled",
                    canceledJobFamilies = canceledJobs.Count,
                    canceledAttempts,
                    stages,
                    sampleJobs = sampleNames
                }), "GitLab CI", "pipeline-job-cancellations", discovery.Pipeline.PipelineId));
            timeline.Add(new TimelineCandidate(
                at, EvidenceSourceRegistry.GitLab, "pipeline-job", summary, "warning",
                discovery.Pipeline.Url, "GitLab CI", "pipeline-job-cancellations", discovery.Pipeline.PipelineId));
        }

        return new PipelineJobOutput(findings, timeline, diagnostics);
    }

    private static async Task<PipelineJobDiscovery> DiscoverPipelineJobsAsync(
        HttpClient client,
        ConnectorTransport connector,
        GitLabPipelineTarget pipeline,
        DateTimeOffset fallbackTimestamp,
        List<string> diagnostics,
        GitLabByteBudget jobBudget,
        CancellationToken cancellationToken)
    {
        // Failed jobs are queried before canceled fanout so a busy cancellation cascade
        // cannot consume the byte allowance before the actionable failed steps are seen.
        // GitLab's documented default excludes retried jobs; use that view for current
        // state and a separate failed-only history view to count attempts.
        var currentFailures = await ReadFilteredPipelineJobsAsync(
            client, connector, pipeline, "failed", includeRetried: false,
            diagnostics, jobBudget, cancellationToken);
        IReadOnlyList<JsonElement> historicalFailures;
        try
        {
            historicalFailures = await ReadFilteredPipelineJobsAsync(
                client, connector, pipeline, "failed", includeRetried: true,
                diagnostics, jobBudget, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            diagnostics.Add(
                $"Pipeline {pipeline.PipelineId} retry history unavailable: {exception.GetType().Name}; current failures retained");
            historicalFailures = currentFailures;
        }
        IReadOnlyList<JsonElement> currentCancellations;
        try
        {
            currentCancellations = await ReadFilteredPipelineJobsAsync(
                client, connector, pipeline, "canceled", includeRetried: false,
                diagnostics, jobBudget, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            diagnostics.Add(
                $"Pipeline {pipeline.PipelineId} canceled jobs unavailable: {exception.GetType().Name}; failed jobs retained");
            currentCancellations = [];
        }

        var currentJobs = currentFailures.Concat(currentCancellations).ToList();
        var historicalJobs = historicalFailures.Concat(currentCancellations).ToList();
        var parsedHistory = historicalJobs
            .DistinctBy(job => ConnectorUtilities.Text(job, "id"), StringComparer.Ordinal)
            .Select(job => ParseJob(job, pipeline, connector, fallbackTimestamp))
            .Where(job => job.Status is "failed" or "canceled")
            .GroupBy(JobFamilyKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var parsedCurrent = currentJobs
            .DistinctBy(job => ConnectorUtilities.Text(job, "id"), StringComparer.Ordinal)
            .Select(job => ParseJob(job, pipeline, connector, fallbackTimestamp))
            .Where(job => job.Status is "failed" or "canceled")
            .ToList();
        var activeFamilies = parsedCurrent
            .GroupBy(JobFamilyKey, StringComparer.Ordinal)
            .Select(family => family
                .OrderByDescending(job => job.OccurredAt)
                .ThenByDescending(job => JobIdOrder(job.Id))
                .First())
            .Select(current => current with
            {
                AttemptCount = Math.Max(1, parsedHistory.GetValueOrDefault(JobFamilyKey(current)))
            })
            .ToList();

        return new PipelineJobDiscovery(pipeline, activeFamilies);
    }

    private static async Task<List<JsonElement>> ReadFilteredPipelineJobsAsync(
        HttpClient client,
        ConnectorTransport connector,
        GitLabPipelineTarget pipeline,
        string jobStatus,
        bool includeRetried,
        List<string> diagnostics,
        GitLabByteBudget jobBudget,
        CancellationToken cancellationToken)
    {
        var jobs = new List<JsonElement>();
        var page = 1;
        while (page <= MaximumPaginationPages)
        {
            if (!jobBudget.CanRead)
            {
                diagnostics.Add($"GitLab job discovery exhausted its {jobBudget.MaximumBytes}-byte reserved budget");
                break;
            }
            var jobsUrl = ConnectorUtilities.Url(connector,
                $"api/v4/projects/{pipeline.EncodedProject}/pipelines/{Uri.EscapeDataString(pipeline.PipelineId)}/jobs"
                + $"?scope%5B%5D={Uri.EscapeDataString(jobStatus)}&include_retried={includeRetried.ToString().ToLowerInvariant()}"
                + $"&per_page={GitLabPageSize}&page={page}");
            using var jobsRequest = ConnectorUtilities.CreateRequest(HttpMethod.Get, jobsUrl, connector);
            using var jobsResponse = await client.SendAsync(
                jobsRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            JsonDocument jobsJson;
            try
            {
                jobsJson = await ConnectorUtilities.ReadBoundedJsonAsync(
                    jobsResponse,
                    Math.Min(connector.MaxBytes, jobBudget.RemainingBytes),
                    cancellationToken,
                    jobBudget.Consume);
            }
            catch (InvalidOperationException exception)
            {
                diagnostics.Add($"Pipeline {pipeline.PipelineId} job pagination stopped: {exception.Message}");
                break;
            }
            using (jobsJson)
            {
                var pageItems = jobsJson.RootElement.EnumerateArray().Select(item => item.Clone()).ToList();
                jobs.AddRange(pageItems);

                var nextPage = NextPage(jobsResponse, page, pageItems.Count);
                if (nextPage is null) break;
                if (page == MaximumPaginationPages)
                {
                    diagnostics.Add(
                        $"Pipeline {pipeline.PipelineId} {(includeRetried ? "job history" : "current jobs")} ({jobStatus}) exceeded "
                        + $"{MaximumPaginationPages * GitLabPageSize} filtered results");
                    break;
                }
                page = nextPage.Value;
            }
        }
        return jobs;
    }

    private static GitLabJob ParseJob(
        JsonElement job,
        GitLabPipelineTarget pipeline,
        ConnectorTransport connector,
        DateTimeOffset fallbackTimestamp)
    {
        var id = ConnectorUtilities.Text(job, "id");
        var name = PrepareMetadata(ConnectorUtilities.Text(job, "name", $"job {id}"), connector);
        var stage = PrepareMetadata(ConnectorUtilities.Text(job, "stage", "unknown stage"), connector);
        var status = ConnectorUtilities.Text(job, "status").ToLowerInvariant();
        var allowFailure = Boolean(job, "allow_failure");
        var failureReason = PrepareMetadata(ConnectorUtilities.Text(job, "failure_reason", ""), connector);
        var at = ConnectorUtilities.Timestamp(job, "finished_at",
            ConnectorUtilities.Timestamp(job, "updated_at", fallbackTimestamp));
        var url = ConnectorUtilities.Text(job, "web_url",
            $"{connector.BaseUrl.TrimEnd('/')}/{pipeline.ProjectId.TrimStart('/')}/-/jobs/{id}");
        var actor = PrepareMetadata(
            NestedText(job, "user", "name") ?? NestedText(job, "user", "username") ?? "GitLab CI", connector);
        var disposition = status == "canceled"
            ? JobDisposition.Canceled
            : allowFailure ? JobDisposition.AllowedFailure : JobDisposition.HardFailure;
        return new GitLabJob(
            id, name, stage, status, failureReason, allowFailure,
            at, url, actor, disposition, 1);
    }

    private static string JobFamilyKey(GitLabJob job) => $"{job.Stage}\u001f{job.Name}";

    private static string JobSelectionKey(GitLabPipelineTarget pipeline, GitLabJob job) =>
        $"{pipeline.ProjectId}\u001f{pipeline.PipelineId}\u001f{job.Id}";

    private static IEnumerable<SelectedPipelineJob> RoundRobin(
        IReadOnlyList<PipelineJobDiscovery> discoveries,
        JobDisposition disposition)
    {
        var buckets = discoveries
            .Select(discovery => new
            {
                discovery.Pipeline,
                Jobs = discovery.Jobs.Where(job => job.Disposition == disposition)
                    .OrderBy(job => job.OccurredAt)
                    .ThenBy(job => JobIdOrder(job.Id))
                    .ToList()
            })
            .Where(bucket => bucket.Jobs.Count > 0)
            .ToList();
        for (var depth = 0; buckets.Any(bucket => depth < bucket.Jobs.Count); depth++)
        {
            foreach (var bucket in buckets)
            {
                if (depth < bucket.Jobs.Count)
                {
                    yield return new SelectedPipelineJob(bucket.Pipeline, bucket.Jobs[depth]);
                }
            }
        }
    }

    private static IReadOnlyList<TraceBudget> AllocateTraceBudgets(int maximumBytes, int count)
    {
        if (count == 0) return [];
        var bytes = Math.Max(0, maximumBytes);
        var allocation = bytes / count;
        var remainder = bytes % count;
        return Enumerable.Range(0, count)
            .Select(index => allocation + (index < remainder ? 1 : 0))
            .Select(scanBytes =>
            {
                var retainedBytes = Math.Min(scanBytes, MaximumTraceExcerptCharacters * 4);
                scanBytes = Math.Min(scanBytes, retainedBytes * 8);
                return new TraceBudget(retainedBytes, scanBytes);
            })
            .ToList();
    }

    private static async Task<TraceReadResult> FetchUsefulTraceTailAsync(
        HttpClient client,
        ConnectorTransport connector,
        string traceUrl,
        TraceBudget budget,
        CancellationToken cancellationToken)
    {
        using (var traceRequest = ConnectorUtilities.CreateRequest(HttpMethod.Get, traceUrl, connector))
        {
            traceRequest.Headers.Range = new RangeHeaderValue(null, budget.RetainedBytes);
            using var traceResponse = await client.SendAsync(
                traceRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (traceResponse.StatusCode == HttpStatusCode.OK)
            {
                // GitLab does not document Range support. A 200 means the range was ignored,
                // so retain a rolling tail across only the deliberately bounded scan window.
                return await ReadTraceTailAsync(
                    traceResponse, budget.RetainedBytes, budget.ScanBytes, cancellationToken);
            }
            if (IsVerifiedSuffixResponse(traceResponse, budget.RetainedBytes))
            {
                var trace = await ReadBoundedTraceAsync(
                    traceResponse, budget.RetainedBytes, cancellationToken);
                return new TraceReadResult(trace.Text, trace.ByteCount, trace.ByteCount, true);
            }
            if (traceResponse.StatusCode != HttpStatusCode.PartialContent
                && traceResponse.StatusCode != HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                traceResponse.EnsureSuccessStatusCode();
            }
        }

        // An unverified 206 cannot be assumed to contain the suffix. Retry without Range and
        // calculate the tail locally with bounded memory.
        using var fallbackRequest = ConnectorUtilities.CreateRequest(HttpMethod.Get, traceUrl, connector);
        using var fallbackResponse = await client.SendAsync(
            fallbackRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        fallbackResponse.EnsureSuccessStatusCode();
        return await ReadTraceTailAsync(
            fallbackResponse, budget.RetainedBytes, budget.ScanBytes, cancellationToken);
    }

    private static bool IsVerifiedSuffixResponse(HttpResponseMessage response, int maximumBytes)
    {
        if (response.StatusCode != HttpStatusCode.PartialContent) return false;
        var range = response.Content.Headers.ContentRange;
        return range is { From: not null, To: not null, Length: not null }
            && range.To == range.Length - 1
            && range.To - range.From + 1 <= maximumBytes;
    }

    private static async Task<(string Text, int ByteCount)> ReadBoundedTraceAsync(
        HttpResponseMessage response,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (maximumBytes <= 0) return ("", 0);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream(Math.Min(maximumBytes, 8192));
        var bytes = new byte[Math.Min(maximumBytes, 8192)];
        while (buffer.Length < maximumBytes)
        {
            var count = await stream.ReadAsync(
                bytes.AsMemory(0, Math.Min(bytes.Length, maximumBytes - (int)buffer.Length)), cancellationToken);
            if (count == 0) break;
            await buffer.WriteAsync(bytes.AsMemory(0, count), cancellationToken);
        }
        return (Encoding.UTF8.GetString(buffer.ToArray()), (int)buffer.Length);
    }

    private static async Task<TraceReadResult> ReadTraceTailAsync(
        HttpResponseMessage response,
        int retainedByteLimit,
        int scanByteLimit,
        CancellationToken cancellationToken)
    {
        if (retainedByteLimit <= 0 || scanByteLimit <= 0) return new TraceReadResult("", 0, 0, false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var tail = new byte[retainedByteLimit];
        var chunk = new byte[Math.Min(8192, scanByteLimit)];
        long totalBytes = 0;
        var reachedEnd = false;
        while (totalBytes < scanByteLimit)
        {
            var count = await stream.ReadAsync(
                chunk.AsMemory(0, Math.Min(chunk.Length, scanByteLimit - (int)totalBytes)), cancellationToken);
            if (count == 0)
            {
                reachedEnd = true;
                break;
            }
            var sourceOffset = 0;
            while (sourceOffset < count)
            {
                var destinationOffset = (int)(totalBytes % retainedByteLimit);
                var copyLength = Math.Min(count - sourceOffset, retainedByteLimit - destinationOffset);
                chunk.AsSpan(sourceOffset, copyLength).CopyTo(tail.AsSpan(destinationOffset, copyLength));
                sourceOffset += copyLength;
                totalBytes += copyLength;
            }
        }

        var contentLength = response.Content.Headers.ContentLength;
        var tailVerified = reachedEnd || contentLength is not null && contentLength <= totalBytes;
        var retainedBytes = (int)Math.Min(totalBytes, retainedByteLimit);
        if (totalBytes <= retainedByteLimit)
        {
            return new TraceReadResult(
                Encoding.UTF8.GetString(tail, 0, retainedBytes), retainedBytes, (int)totalBytes, tailVerified);
        }

        var start = (int)(totalBytes % retainedByteLimit);
        var ordered = new byte[retainedBytes];
        var firstLength = retainedByteLimit - start;
        Buffer.BlockCopy(tail, start, ordered, 0, firstLength);
        if (start > 0) Buffer.BlockCopy(tail, 0, ordered, firstLength, start);
        return new TraceReadResult(
            Encoding.UTF8.GetString(ordered), retainedBytes, (int)totalBytes, tailVerified);
    }

    private static async Task<JsonDocument?> ReadBudgetedJsonAsync(
        HttpResponseMessage response,
        GitLabByteBudget budget,
        string diagnosticLabel,
        List<string> diagnostics,
        CancellationToken cancellationToken)
    {
        if (!budget.CanRead)
        {
            diagnostics.Add($"{diagnosticLabel} skipped after exhausting its {budget.MaximumBytes}-byte reserved budget");
            return null;
        }

        try
        {
            return await ConnectorUtilities.ReadBoundedJsonAsync(
                response, budget.RemainingBytes, cancellationToken, budget.Consume);
        }
        catch (InvalidOperationException exception)
        {
            diagnostics.Add($"{diagnosticLabel} skipped: {exception.Message}");
            return null;
        }
    }

    private static async Task<List<JsonElement>> ReadPaginatedJsonArrayAsync(
        HttpClient client,
        ConnectorTransport connector,
        string url,
        string diagnosticLabel,
        List<string> diagnostics,
        GitLabByteBudget pipelineBudget,
        CancellationToken cancellationToken)
    {
        var items = new List<JsonElement>();
        var page = 1;
        while (page <= MaximumPaginationPages)
        {
            if (!pipelineBudget.CanRead)
            {
                diagnostics.Add($"{diagnosticLabel} exhausted the {pipelineBudget.MaximumBytes}-byte reserved budget");
                break;
            }
            using var request = ConnectorUtilities.CreateRequest(
                HttpMethod.Get, $"{url}&page={page}", connector);
            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            JsonDocument json;
            try
            {
                json = await ConnectorUtilities.ReadBoundedJsonAsync(
                    response,
                    Math.Min(connector.MaxBytes, pipelineBudget.RemainingBytes),
                    cancellationToken,
                    pipelineBudget.Consume);
            }
            catch (InvalidOperationException exception)
            {
                diagnostics.Add($"{diagnosticLabel} pagination stopped: {exception.Message}");
                break;
            }
            using (json)
            {
                var pageItems = json.RootElement.EnumerateArray().Select(item => item.Clone()).ToList();
                items.AddRange(pageItems);
                var nextPage = NextPage(response, page, pageItems.Count);
                if (nextPage is null) break;
                if (page == MaximumPaginationPages)
                {
                    diagnostics.Add($"{diagnosticLabel} exceeded {MaximumPaginationPages * GitLabPageSize} results");
                    break;
                }
                page = nextPage.Value;
            }
        }
        return items;
    }

    private static int? NextPage(HttpResponseMessage response, int currentPage, int itemCount)
    {
        if (response.Headers.TryGetValues("X-Next-Page", out var values)
            && int.TryParse(values.FirstOrDefault(), out var nextPage)
            && nextPage > currentPage)
        {
            return nextPage;
        }
        return itemCount == GitLabPageSize ? currentPage + 1 : null;
    }

    private static string? PrepareTraceExcerpt(string trace, ConnectorTransport connector)
    {
        if (string.IsNullOrWhiteSpace(trace)) return null;
        var cleaned = RedactSensitiveText(trace, connector).Trim();
        if (cleaned.Length > MaximumTraceExcerptCharacters)
        {
            cleaned = "…" + cleaned[^MaximumTraceExcerptCharacters..];
        }
        return cleaned;
    }

    private static string PrepareMetadata(string value, ConnectorTransport connector)
    {
        var cleaned = RedactSensitiveText(value, connector)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        return ConnectorUtilities.Truncate(cleaned, 300);
    }

    private static string RedactSensitiveText(string value, ConnectorTransport connector)
    {
        var cleaned = AnsiEscape().Replace(value, "");
        cleaned = ControlCharacter().Replace(cleaned, "");
        cleaned = SecretAssignment().Replace(cleaned, "$1=[REDACTED]");
        cleaned = BearerToken().Replace(cleaned, "Bearer [REDACTED]");
        cleaned = GitLabToken().Replace(cleaned, "[REDACTED]");
        cleaned = JsonWebToken().Replace(cleaned, "[REDACTED]");
        var credential = string.IsNullOrWhiteSpace(connector.CredentialEnv)
            ? null
            : Environment.GetEnvironmentVariable(connector.CredentialEnv);
        if (!string.IsNullOrWhiteSpace(credential))
        {
            cleaned = cleaned.Replace(credential, "[REDACTED]", StringComparison.Ordinal);
        }
        return cleaned;
    }

    private static bool IsFailedPipeline(string status) =>
        status.Equals("failed", StringComparison.OrdinalIgnoreCase)
        || status.Equals("canceled", StringComparison.OrdinalIgnoreCase);

    private static bool Boolean(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
        && (property.ValueKind == JsonValueKind.True
            || property.ValueKind == JsonValueKind.String
            && bool.TryParse(property.GetString(), out var parsed) && parsed);

    private static long JobIdOrder(string id) => long.TryParse(id, out var numericId) ? numericId : 0;

    private static int PipelineTimelineRank(TimelineCandidate candidate) =>
        candidate.Kind != "pipeline-job" ? 3
        : candidate.Severity == "critical" ? 0
        : candidate.ObjectType == "pipeline-job" ? 1
        : 2;

    private static bool IsRelevantPath(string path, IReadOnlyList<string> allowlist) =>
        allowlist.Any(allowed => path.Equals(allowed, StringComparison.Ordinal)
            || path.StartsWith(allowed.TrimEnd('/') + "/", StringComparison.Ordinal));

    private static string? NestedText(JsonElement element, string objectName, string propertyName) =>
        element.TryGetProperty(objectName, out var nested) && nested.ValueKind == JsonValueKind.Object
            ? ConnectorUtilities.Text(nested, propertyName, "") is { Length: > 0 } value ? value : null
            : null;

    private static string ShortSha(string sha) => string.IsNullOrWhiteSpace(sha) ? "an unknown commit" : sha[..Math.Min(8, sha.Length)];

    [GeneratedRegex(@"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])", RegexOptions.CultureInvariant)]
    private static partial Regex AnsiEscape();

    [GeneratedRegex(@"[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]", RegexOptions.CultureInvariant)]
    private static partial Regex ControlCharacter();

    [GeneratedRegex(@"(?i)\b([a-z0-9_-]*(?:api[_-]?key|authorization|credential|password|passwd|secret|token|connection[_-]?string)[a-z0-9_-]*)\s*[:=]\s*(?:""[^""\r\n]*""|'[^'\r\n]*'|[^\s,;]+)", RegexOptions.CultureInvariant)]
    private static partial Regex SecretAssignment();

    [GeneratedRegex(@"(?i)\bBearer\s+[A-Za-z0-9._~+/-]+=*", RegexOptions.CultureInvariant)]
    private static partial Regex BearerToken();

    [GeneratedRegex(@"\bglpat-[A-Za-z0-9_-]+\b", RegexOptions.CultureInvariant)]
    private static partial Regex GitLabToken();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b", RegexOptions.CultureInvariant)]
    private static partial Regex JsonWebToken();

    private sealed record PipelineJobOutput(
        IReadOnlyList<EvidenceFinding> Findings,
        IReadOnlyList<TimelineCandidate> Timeline,
        IReadOnlyList<string> Diagnostics);

    private sealed record TraceBudget(int RetainedBytes, int ScanBytes);

    private sealed record TraceReadResult(
        string Text,
        int RetainedBytes,
        int BytesScanned,
        bool TailVerified);

    private sealed class GitLabByteBudget(int maximumBytes)
    {
        public int MaximumBytes { get; } = maximumBytes;
        public int RemainingBytes { get; private set; } = maximumBytes;
        public bool CanRead => RemainingBytes > 0;

        public void Consume(int bytes) => RemainingBytes = Math.Max(0, RemainingBytes - Math.Max(0, bytes));
    }

    private sealed record GitLabPipelineTarget(
        string ProjectId,
        string EncodedProject,
        string PipelineId,
        string Status,
        DateTimeOffset OccurredAt,
        string Url);

    private sealed record PipelineJobDiscovery(
        GitLabPipelineTarget Pipeline,
        IReadOnlyList<GitLabJob> Jobs);

    private sealed record SelectedPipelineJob(
        GitLabPipelineTarget Pipeline,
        GitLabJob Job);

    private sealed record GitLabJob(
        string Id,
        string Name,
        string Stage,
        string Status,
        string FailureReason,
        bool AllowFailure,
        DateTimeOffset OccurredAt,
        string Url,
        string Actor,
        JobDisposition Disposition,
        int AttemptCount);

    private enum JobDisposition
    {
        HardFailure,
        AllowedFailure,
        Canceled
    }

    internal static IReadOnlyList<CodeReference> ExtractCodeReferences(
        string baseUrl,
        string projectId,
        string commitSha,
        IReadOnlyList<JsonElement> diffs)
    {
        var references = new List<CodeReference>();
        foreach (var diff in diffs)
        {
            var path = ConnectorUtilities.Text(diff, "new_path");
            var patch = ConnectorUtilities.Text(diff, "diff", "");
            var matches = Regex.Matches(patch, @"^@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@", RegexOptions.Multiline | RegexOptions.CultureInvariant);
            for (var index = 0; index < matches.Count && references.Count < 20; index++)
            {
                var match = matches[index];
                var currentLine = int.Parse(match.Groups[1].Value);
                var excerptEnd = index + 1 < matches.Count ? matches[index + 1].Index : patch.Length;
                var rawHunk = patch[(match.Index + match.Length)..excerptEnd];
                var firstNewline = rawHunk.IndexOf('\n');
                var hunk = firstNewline >= 0 ? rawHunk[(firstNewline + 1)..] : "";
                int? changeStart = null;
                var changedLines = new List<string>();

                void FlushChange()
                {
                    if (changeStart is null || changedLines.Count == 0 || references.Count >= 20) return;
                    var startLine = changeStart.Value;
                    var endLine = startLine + changedLines.Count - 1;
                    var excerpt = ConnectorUtilities.Truncate(string.Join('\n', changedLines), 1200);
                    var url = $"{baseUrl.TrimEnd('/')}/{projectId.TrimStart('/')}/-/blob/{commitSha}/{path}#L{startLine}-{endLine}";
                    references.Add(new CodeReference(
                        ConnectorUtilities.Id("code", projectId, commitSha, path, startLine.ToString(), endLine.ToString()),
                        projectId, commitSha, path, startLine, endLine, url, excerpt));
                    changeStart = null;
                    changedLines.Clear();
                }

                foreach (var line in hunk.Split('\n'))
                {
                    if (line.StartsWith('+'))
                    {
                        changeStart ??= currentLine;
                        changedLines.Add(line);
                        currentLine++;
                    }
                    else if (line.StartsWith('-'))
                    {
                        FlushChange();
                    }
                    else if (!line.StartsWith('\\'))
                    {
                        FlushChange();
                        currentLine++;
                    }
                }
                FlushChange();
            }
        }
        return references;
    }
}
