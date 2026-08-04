using System.ComponentModel;
using Panko.Api.Cases;
using Panko.Contracts;
using ModelContextProtocol.Server;

namespace Panko.Api.Mcp;

public static class CaseMcpToolNames
{
    public const string Create = "create_case";
    public const string Append = "append_case_crumbs";
    public const string Get = "get_case";
    public const string Rebuild = "rebuild_case_file";
    public const string Refresh = "refresh_case_sources";
    public const string Close = "close_case";

}

[McpServerToolType]
public sealed class CaseMcpTools(
    ICaseCommands commands,
    ICaseQueries queries,
    IHttpContextAccessor httpContextAccessor,
    McpToolRouter router)
{
    [McpServerTool(
        Name = CaseMcpToolNames.Create,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Create a durable agent-owned Panko Case for an enabled Recipe. Retries with the same idempotency key and payload return the same Case.")]
    public Task<McpCreateCaseResult> CreateAsync(
        [Description("Caller-generated idempotency key, unique within the authenticated principal.")]
        string idempotencyKey,
        [Description("Exact Panko Recipe identifier.")]
        string recipeId,
        [Description("Short human-readable Case title.")]
        string title,
        [Description("Observed service or component represented by the Case.")]
        string serviceId,
        [Description("Case urgency: high or low.")]
        string urgency,
        [Description("Reference timestamp around which Crumbs are relevant.")]
        DateTimeOffset referenceTime,
        CancellationToken cancellationToken,
        [Description("Optional bounded labels. Recipe policy decides which labels are retained.")]
        IReadOnlyDictionary<string, string>? labels = null) =>
        router.InvokeAsync(
            CaseMcpToolNames.Create,
            async ct =>
            {
                var result = await commands.CreateAsync(
                    new CreateCase(
                        idempotencyKey,
                        recipeId,
                        title,
                        serviceId,
                        urgency,
                        referenceTime,
                        labels ?? new Dictionary<string, string>(StringComparer.Ordinal)),
                    CurrentCaller(),
                    ct);
                return new McpCreateCaseResult(
                    result.Case.Id,
                    "agent",
                    result.Case.InputVersion,
                    result.Case.ProjectedInputVersion,
                    result.Case.Version,
                    result.Case.Status,
                    $"/cases/{result.Case.Id}",
                    result.Duplicate);
            },
            cancellationToken);

    [McpServerTool(
        Name = CaseMcpToolNames.Append,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Append a bounded batch of untrusted Crumbs to a durable Case. Retries with the same batch ID and payload are idempotent.")]
    public Task<McpAppendCrumbsResult> AppendAsync(
        [Description("Persisted Panko Case UUID, not an MCP transport session ID.")]
        Guid caseId,
        [Description("Caller-generated idempotency key for this batch.")]
        string batchId,
        [Description("Restricted submitted inputs. Trust and confidence are assigned by Panko.")]
        IReadOnlyList<SubmittedCrumb> crumbs,
        CancellationToken cancellationToken) =>
        router.InvokeAsync(
            CaseMcpToolNames.Append,
            async ct =>
            {
                if (crumbs is null)
                {
                    throw new CaseValidationException("crumbs is required.");
                }
                var result = await commands.AppendCrumbsAsync(
                    caseId,
                    new AppendCrumbs(batchId, crumbs),
                    CurrentCaller(),
                    ct);
                return new McpAppendCrumbsResult(
                    result.Accepted,
                    result.Duplicates,
                    result.InputVersion,
                    result.ProjectedInputVersion,
                    result.RebuildQueued,
                    result.DuplicateBatch);
            },
            cancellationToken);

    [McpServerTool(
        Name = CaseMcpToolNames.Get,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        ReadOnly = true,
        UseStructuredContent = true)]
    [Description("Get bounded status and Case File version information for a Case visible to the caller.")]
    public Task<McpGetCaseResult> GetAsync(
        [Description("Persisted Panko Case UUID, not an MCP transport session ID.")]
        Guid caseId,
        CancellationToken cancellationToken) =>
        router.InvokeAsync(
            CaseMcpToolNames.Get,
            async ct =>
            {
                var result = await queries.GetAsync(caseId, CurrentCaller(), ct);
                return new McpGetCaseResult(
                    result.CaseId,
                    result.Status,
                    result.InputVersion,
                    result.ProjectedInputVersion,
                    result.CaseFileVersion,
                    result.DeterministicSummary,
                    result.CaseUrl);
            },
            cancellationToken);

    [McpServerTool(
        Name = CaseMcpToolNames.Rebuild,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Queue a deterministic projection rebuild from canonical inputs and retained Crumb-source snapshots. This does not refresh external sources.")]
    public Task<McpRebuildCaseResult> RebuildAsync(
        [Description("Persisted Panko Case UUID, not an MCP transport session ID.")]
        Guid caseId,
        CancellationToken cancellationToken) =>
        router.InvokeAsync(
            CaseMcpToolNames.Rebuild,
            async ct =>
            {
                var result = await commands.QueueRebuildAsync(caseId, CurrentCaller(), ct);
                return new McpRebuildCaseResult(
                    result.CaseId,
                    result.TargetInputVersion,
                    result.RebuildQueued);
            },
            cancellationToken);

    [McpServerTool(
        Name = CaseMcpToolNames.Refresh,
        Destructive = false,
        Idempotent = true,
        OpenWorld = true,
        UseStructuredContent = true)]
    [Description("Queue a policy-controlled refresh from the Case Recipe's configured Crumb sources. Endpoints, credentials, queries, and tool names cannot be supplied here.")]
    public Task<McpRefreshCaseSourcesResult> RefreshAsync(
        [Description("Persisted Panko Case UUID, not an MCP transport session ID.")]
        Guid caseId,
        CancellationToken cancellationToken) =>
        router.InvokeAsync(
            CaseMcpToolNames.Refresh,
            async ct =>
            {
                var result = await commands.QueueSourceRefreshAsync(caseId, CurrentCaller(), ct);
                return new McpRefreshCaseSourcesResult(
                    result.CaseId,
                    result.TargetInputVersion,
                    result.RefreshQueued);
            },
            cancellationToken);

    [McpServerTool(
        Name = CaseMcpToolNames.Close,
        Destructive = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Close a Case so it no longer accepts submitted Crumbs or source refresh requests.")]
    public Task<McpCloseCaseResult> CloseAsync(
        [Description("Persisted Panko Case UUID, not an MCP transport session ID.")]
        Guid caseId,
        CancellationToken cancellationToken) =>
        router.InvokeAsync(
            CaseMcpToolNames.Close,
            async ct =>
            {
                await commands.CloseAsync(caseId, CurrentCaller(), ct);
                return new McpCloseCaseResult(caseId, "closed");
            },
            cancellationToken);

    private CallerIdentity CurrentCaller()
    {
        var principal = httpContextAccessor.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true
            || (string.IsNullOrWhiteSpace(principal.FindFirst("sub")?.Value)
                && string.IsNullOrWhiteSpace(principal.Identity.Name)))
        {
            throw new CaseAuthorizationException(
                "An authenticated JWT identity is required.");
        }
        return new CallerIdentity(principal);
    }
}
