namespace Panko.Api.Mcp;

public sealed record McpCreateCaseResult(
    Guid CaseId,
    string Origin,
    long InputVersion,
    long ProjectedInputVersion,
    int CaseFileVersion,
    string Status,
    string CaseUrl,
    bool Duplicate);

public sealed record McpAppendCrumbsResult(
    int Accepted,
    int Duplicates,
    long InputVersion,
    long ProjectedInputVersion,
    bool RebuildQueued,
    bool DuplicateBatch);

public sealed record McpGetCaseResult(
    Guid CaseId,
    string Status,
    long InputVersion,
    long ProjectedInputVersion,
    int CaseFileVersion,
    string? DeterministicSummary,
    string CaseUrl);

public sealed record McpRebuildCaseResult(
    Guid CaseId,
    long TargetInputVersion,
    bool RebuildQueued);

public sealed record McpRefreshCaseSourcesResult(
    Guid CaseId,
    long TargetInputVersion,
    bool RefreshQueued);

public sealed record McpCloseCaseResult(
    Guid CaseId,
    string Status);
