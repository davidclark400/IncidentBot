using System.Security.Claims;
using System.Text.Json.Nodes;
using Panko.Api.Domain;
using SubmittedCrumbKind = Panko.Contracts.SubmittedCrumbKind;
using SubmittedCrumb = Panko.Contracts.SubmittedCrumb;

namespace Panko.Api.Cases;

public enum CasePermission
{
    Create,
    Read,
    Append,
    Rebuild,
    RefreshSources,
    Close
}

public sealed record CallerIdentity(ClaimsPrincipal Principal)
{
    public string PrincipalName => Principal.FindFirstValue("sub") ?? Principal.Identity?.Name
        ?? throw new CaseAuthorizationException("An authenticated caller identity is required.");
}

public sealed record CreateCase(
    string IdempotencyKey,
    string RecipeId,
    string Title,
    string ServiceId,
    string Urgency,
    DateTimeOffset ReferenceTime,
    IReadOnlyDictionary<string, string> Labels);

public sealed record AppendCrumbs(
    string BatchId,
    IReadOnlyList<SubmittedCrumb> Crumbs);

public sealed record AcceptCaseOriginEvent(
    CaseOrigin Origin,
    string RecipeId,
    string ServiceId,
    string Title,
    string Urgency,
    PagerDutyIncidentState PagerDutyState,
    DateTimeOffset ReferenceTime,
    DateTimeOffset OccurredAt,
    IReadOnlyDictionary<string, string> Labels,
    SubmittedCrumb LifecycleCrumb);

/// <summary>
/// Durable receipt information supplied by a trusted origin adapter. The canonical lifecycle event
/// remains source-neutral; this envelope retains transport idempotency and audit provenance without
/// exposing either concern through the submitted-agent input boundary.
/// </summary>
public sealed record CaseOriginEventReceipt(
    string ProducerPrincipal,
    string IdempotencyKey,
    string SourceEventType,
    ReadOnlyMemory<byte> RawPayload,
    bool IsAuthoritativeSnapshot = false);

public sealed record CreateCaseResult(
    CaseRecord Case,
    bool Duplicate);

public sealed record AppendCrumbsResult(
    int Accepted,
    int Duplicates,
    long InputVersion,
    long ProjectedInputVersion,
    bool RebuildQueued,
    bool DuplicateBatch);

public sealed record RebuildCaseResult(
    Guid CaseId,
    long TargetInputVersion,
    bool RebuildQueued);

public sealed record RefreshCaseResult(
    Guid CaseId,
    long TargetInputVersion,
    bool RefreshQueued);

public sealed record CaseInput(
    Guid Id,
    Guid CaseId,
    long Sequence,
    long InputVersion,
    string ProducerPrincipal,
    string ClientCrumbId,
    SubmittedCrumbKind Kind,
    DateTimeOffset OccurredAt,
    DateTimeOffset ReceivedAt,
    string Category,
    string Severity,
    string Summary,
    string? Excerpt,
    string? DeclaredSource,
    string? SourceReference,
    string? Url,
    string? Actor,
    string? ObjectType,
    string? ObjectId,
    JsonObject Attributes,
    string TrustLevel,
    string PayloadHash,
    Guid? SupersedesCrumbId,
    DateTimeOffset? RetractedAt,
    long? RetractedInputVersion);

public sealed record NormalizedCrumb(
    Guid Id,
    string ClientCrumbId,
    SubmittedCrumbKind Kind,
    DateTimeOffset OccurredAt,
    string Category,
    string Severity,
    string Summary,
    string? Excerpt,
    string? DeclaredSource,
    string? SourceReference,
    string? Url,
    string? Actor,
    string? ObjectType,
    string? ObjectId,
    JsonObject Attributes,
    string? SupersedesClientCrumbId,
    string PayloadHash);

public sealed record CrumbSourceSnapshot(
    Guid CaseId,
    long SnapshotVersion,
    DateTimeOffset CollectedAt,
    CrumbSourceResult Result);

public static class CaseWorkKinds
{
    public const string Build = "build-case";
    public const string Project = "project-case";
    public const string RefreshSources = "refresh-case-sources";
    public const string Analyse = "analyse-case";

    public static bool IsBuild(string kind) =>
        string.Equals(kind, Build, StringComparison.Ordinal);

    public static bool IsProject(string kind) =>
        string.Equals(kind, Project, StringComparison.Ordinal);

    public static bool IsRefreshSources(string kind) =>
        string.Equals(kind, RefreshSources, StringComparison.Ordinal);

    public static bool IsAnalyse(string kind) =>
        string.Equals(kind, Analyse, StringComparison.Ordinal);
}

public static class CaseOutboxKinds
{
    public const string SlackCaseFile = "slack.case-file";

    public static bool IsSlackCaseFile(string kind) =>
        string.Equals(kind, SlackCaseFile, StringComparison.Ordinal);
}

public static class CaseCommandKinds
{
    public const string AppendCrumbs = "append-crumbs";
}

public sealed class CaseAuthorizationException(string message) : Exception(message);

public sealed class CaseNotFoundException(Guid caseId)
    : Exception($"Case '{caseId}' was not found.");

public sealed class CaseConflictException(string message) : Exception(message);

public sealed class CaseValidationException(string message) : Exception(message);
