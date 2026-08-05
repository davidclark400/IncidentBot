using System.Text.Json;
using System.Text.Json.Serialization;

namespace Panko.Contracts;

[JsonConverter(typeof(SubmittedCrumbKindJsonConverter))]
public enum SubmittedCrumbKind
{
    [JsonStringEnumMemberName("event")]
    Event,
    [JsonStringEnumMemberName("crumb")]
    Crumb,
    [JsonStringEnumMemberName("note")]
    Note
}

public sealed class SubmittedCrumbKindJsonConverter : JsonConverter<SubmittedCrumbKind>
{
    public override SubmittedCrumbKind Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Submitted Crumb type must be a string.");
        }

        return reader.GetString()?.ToLowerInvariant() switch
        {
            "event" => SubmittedCrumbKind.Event,
            "crumb" => SubmittedCrumbKind.Crumb,
            "note" => SubmittedCrumbKind.Note,
            _ => throw new JsonException("Submitted Crumb type must be one of: event, crumb, note.")
        };
    }

    public override void Write(
        Utf8JsonWriter writer,
        SubmittedCrumbKind value,
        JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            SubmittedCrumbKind.Event => "event",
            SubmittedCrumbKind.Crumb => "crumb",
            SubmittedCrumbKind.Note => "note",
            _ => throw new JsonException($"Unknown Submitted Crumb type '{value}'.")
        });
    }
}

public sealed record SubmittedCrumb(
    string ClientCrumbId,
    SubmittedCrumbKind Kind,
    DateTimeOffset OccurredAt,
    string Category,
    string Severity,
    string Summary,
    string? Excerpt = null,
    string? DeclaredSource = null,
    string? SourceReference = null,
    string? Url = null,
    string? Actor = null,
    string? ObjectType = null,
    string? ObjectId = null,
    IReadOnlyDictionary<string, JsonElement>? Attributes = null,
    string? SupersedesClientCrumbId = null);

public sealed record CreateCaseRequest(
    string RecipeId,
    string Title,
    string ServiceId,
    string Urgency,
    DateTimeOffset ReferenceTime,
    IReadOnlyDictionary<string, string>? Labels = null,
    string? IdempotencyKey = null);

public sealed record AppendCrumbsRequest(
    string BatchId,
    IReadOnlyList<SubmittedCrumb> Crumbs);

public sealed record CreateCaseResponse(
    Guid CaseId,
    CaseOriginKind Origin,
    long InputVersion,
    long ProjectedInputVersion,
    int CaseFileVersion,
    string Status,
    string CaseUrl,
    bool Duplicate = false);

public sealed record AppendCrumbsResponse(
    int Accepted,
    int Duplicates,
    long InputVersion,
    long ProjectedInputVersion,
    bool RebuildQueued,
    bool DuplicateBatch = false);

public sealed record RebuildCaseFileResponse(
    Guid CaseId,
    long TargetInputVersion,
    bool RebuildQueued);

public sealed record RefreshCaseSourcesResponse(
    Guid CaseId,
    long TargetInputVersion,
    bool RefreshQueued);

public sealed record CaseStatusResponse(
    Guid CaseId,
    CaseOriginKind Origin,
    string Status,
    string RecipeId,
    string ServiceId,
    string Title,
    long InputVersion,
    long ProjectedInputVersion,
    int CaseFileVersion,
    string? CreatedBy,
    DateTimeOffset UpdatedAt,
    string? DeterministicSummary,
    string CaseUrl);

public sealed record CaseInput(
    Guid Id,
    long Sequence,
    long InputVersion,
    string ClientCrumbId,
    string ProducerPrincipal,
    DateTimeOffset ReceivedAt,
    DateTimeOffset OccurredAt,
    SubmittedCrumbKind Kind,
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
    IReadOnlyDictionary<string, JsonElement> Attributes,
    string TrustLevel,
    Guid? SupersedesCrumbId,
    Guid? SupersededByCrumbId,
    DateTimeOffset? RetractedAt,
    long? RetractedInputVersion,
    bool Active,
    long? ProjectedInInputVersion);

public sealed record RecentCase(
    Guid CaseId,
    CaseOriginKind Origin,
    string RecipeId,
    string ServiceId,
    string Title,
    string Status,
    long InputVersion,
    long ProjectedInputVersion,
    int CaseFileVersion,
    string? CreatedBy,
    DateTimeOffset UpdatedAt,
    string CaseUrl);

public sealed record RecentCases(
    int Total,
    IReadOnlyList<RecentCase> Cases);
