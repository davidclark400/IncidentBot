using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Panko.Api.Options;
using Panko.Contracts;
using Microsoft.Extensions.Options;

namespace Panko.Api.Cases;

/// <summary>
/// Converts untrusted agent payloads into the only shape that may cross the canonical-input seam.
/// Trust, confidence, internal IDs, Crumb-source configuration and publication destinations are all server-owned.
/// </summary>
public sealed partial class CaseInputBoundary(IOptions<CaseOptions> options)
{
    private static readonly HashSet<string> Severities = new(StringComparer.Ordinal)
    {
        "info", "warning", "critical"
    };

    public IReadOnlyList<NormalizedCrumb> Normalize(
        Guid caseId,
        string producerPrincipal,
        DateTimeOffset referenceTime,
        IReadOnlyCollection<string> allowedCategories,
        IReadOnlyList<SubmittedCrumb> crumbs)
    {
        var limits = options.Value;
        if (crumbs.Count == 0)
        {
            throw new CaseValidationException("At least one Crumb is required.");
        }
        if (crumbs.Count > limits.MaximumInputsPerBatch)
        {
            throw new CaseValidationException(
                $"A batch may contain at most {limits.MaximumInputsPerBatch} Case inputs.");
        }
        if (crumbs.Any(crumb => !Enum.IsDefined(crumb.Kind)))
        {
            throw new CaseValidationException(
                "Input type must be one of: event, crumb, note.");
        }
        if (JsonSerializer.SerializeToUtf8Bytes(crumbs).Length > limits.MaximumRequestBytes)
        {
            throw new CaseValidationException("The submitted Crumb batch is too large.");
        }

        var allowed = allowedCategories.ToHashSet(StringComparer.Ordinal);
        var observedClientIds = new HashSet<string>(StringComparer.Ordinal);
        var output = new List<NormalizedCrumb>(crumbs.Count);
        foreach (var submitted in crumbs)
        {
            var clientCrumbId = RequiredIdentifier(submitted.ClientCrumbId, "clientEventId", 128);
            if (!observedClientIds.Add(clientCrumbId))
            {
                throw new CaseValidationException(
                    $"Client Crumb ID '{clientCrumbId}' occurs more than once in the batch.");
            }

            var category = RequiredIdentifier(submitted.Category, "category", 64).ToLowerInvariant();
            if (!allowed.Contains(category))
            {
                throw new CaseValidationException(
                    $"Input category '{category}' is not allowed by this Recipe.");
            }

            var severity = RequiredIdentifier(submitted.Severity, "severity", 16).ToLowerInvariant();
            if (!Severities.Contains(severity))
            {
                throw new CaseValidationException(
                    "Severity must be one of: info, warning, critical.");
            }

            var distance = (submitted.OccurredAt - referenceTime).Duration();
            if (distance > TimeSpan.FromHours(limits.MaximumTimestampDistanceHours))
            {
                throw new CaseValidationException(
                    "Crumb occurredAt is outside the configured distance from the Case reference time.");
            }

            var summary = BoundedText(submitted.Summary, "summary", limits.MaximumSummaryCharacters, required: true)!;
            var excerpt = BoundedText(submitted.Excerpt, "excerpt", limits.MaximumExcerptCharacters, required: false);
            RejectCredentialLikeContent(summary, "summary");
            RejectCredentialLikeContent(excerpt, "excerpt");

            var declaredSource = OptionalIdentifier(submitted.DeclaredSource, "declaredSource", 64)?.ToLowerInvariant();
            var sourceReference = OptionalIdentifier(submitted.SourceReference, "sourceReference", 256);
            var actor = OptionalIdentifier(submitted.Actor, "actor", 200);
            var objectType = OptionalIdentifier(submitted.ObjectType, "objectType", 64);
            var objectId = OptionalIdentifier(submitted.ObjectId, "objectId", 256);
            var supersedes = OptionalIdentifier(submitted.SupersedesClientCrumbId, "supersedesClientEventId", 128);
            var url = NormalizeUrl(submitted.Url);
            var attributes = NormalizeAttributes(submitted.Attributes, limits);
            var id = DeterministicCrumbId(caseId, producerPrincipal, clientCrumbId);

            var canonical = new JsonObject
            {
                ["clientEventId"] = clientCrumbId,
                ["type"] = submitted.Kind.ToString().ToLowerInvariant(),
                ["occurredAt"] = submitted.OccurredAt.ToUniversalTime().ToString("O"),
                ["category"] = category,
                ["severity"] = severity,
                ["summary"] = summary,
                ["excerpt"] = excerpt,
                ["declaredSource"] = declaredSource,
                ["sourceReference"] = sourceReference,
                ["url"] = url,
                ["actor"] = actor,
                ["objectType"] = objectType,
                ["objectId"] = objectId,
                ["attributes"] = attributes.DeepClone(),
                ["supersedesClientEventId"] = supersedes
            };
            var hash = Convert.ToHexStringLower(SHA256.HashData(
                Encoding.UTF8.GetBytes(canonical.ToJsonString())));
            output.Add(new NormalizedCrumb(
                id,
                clientCrumbId,
                submitted.Kind,
                submitted.OccurredAt.ToUniversalTime(),
                category,
                severity,
                summary,
                excerpt,
                declaredSource,
                sourceReference,
                url,
                actor,
                objectType,
                objectId,
                attributes,
                supersedes,
                hash));
        }

        return output;
    }

    public static Guid DeterministicCrumbId(Guid caseId, string producerPrincipal, string clientCrumbId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{caseId:N}\u001f{producerPrincipal}\u001f{clientCrumbId}"));
        var guidBytes = bytes.AsSpan(0, 16).ToArray();
        guidBytes[6] = (byte)((guidBytes[6] & 0x0f) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3f) | 0x80);
        return new Guid(guidBytes, bigEndian: true);
    }

    private static JsonObject NormalizeAttributes(
        IReadOnlyDictionary<string, JsonElement>? input,
        CaseOptions limits)
    {
        if (input is null || input.Count == 0) return [];
        var output = new JsonObject();
        foreach (var (keyValue, value) in input.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var key = RequiredIdentifier(keyValue, "attribute key", 128);
            if (LooksSensitiveKey(key))
            {
                throw new CaseValidationException(
                    $"Sensitive attribute key '{key}' is not allowed.");
            }
            ValidateAttributeValue(value, key, 1, limits.MaximumAttributesDepth);
            output[key] = JsonNode.Parse(value.GetRawText());
        }

        if (Encoding.UTF8.GetByteCount(output.ToJsonString()) > limits.MaximumAttributesBytes)
        {
            throw new CaseValidationException("Crumb attributes exceed the configured byte limit.");
        }
        return output;
    }

    private static void ValidateAttributeValue(JsonElement value, string path, int depth, int maximumDepth)
    {
        if (depth > maximumDepth)
        {
            throw new CaseValidationException("Crumb attributes exceed the configured depth limit.");
        }
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    if (LooksSensitiveKey(property.Name))
                    {
                        throw new CaseValidationException(
                            $"Sensitive attribute key '{property.Name}' is not allowed.");
                    }
                    ValidateAttributeValue(property.Value, $"{path}.{property.Name}", depth + 1, maximumDepth);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    ValidateAttributeValue(item, path, depth + 1, maximumDepth);
                }
                break;
            case JsonValueKind.String:
                RejectCredentialLikeContent(value.GetString(), path);
                break;
        }
    }

    private static string? NormalizeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (trimmed.Length > 2048
            || !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new CaseValidationException("url must be a bounded HTTP or HTTPS URL without user info.");
        }
        foreach (var component in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var key = Uri.UnescapeDataString(component.Split('=', 2)[0]);
            if (LooksSensitiveKey(key))
            {
                throw new CaseValidationException("Sensitive URL query parameters are not allowed.");
            }
        }
        var normalized = new UriBuilder(uri) { Fragment = "" }.Uri.AbsoluteUri;
        RejectCredentialLikeContent(
            Uri.UnescapeDataString(normalized).Replace('+', ' '),
            "url");
        return normalized;
    }

    private static string RequiredIdentifier(string? value, string name, int maximumCharacters) =>
        OptionalIdentifier(value, name, maximumCharacters)
        ?? throw new CaseValidationException($"{name} is required.");

    private static string? OptionalIdentifier(string? value, string name, int maximumCharacters)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        RejectCredentialLikeContent(trimmed, name);
        return trimmed.Length <= maximumCharacters ? trimmed : trimmed[..maximumCharacters];
    }

    private static string? BoundedText(string? value, string name, int maximumCharacters, bool required)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return required
                ? throw new CaseValidationException($"{name} is required.")
                : null;
        }
        var trimmed = value.Trim();
        return trimmed.Length <= maximumCharacters ? trimmed : trimmed[..maximumCharacters];
    }

    private static bool LooksSensitiveKey(string key)
    {
        var normalized = key.Replace("-", "_", StringComparison.Ordinal).ToLowerInvariant();
        return normalized.Contains("authorization", StringComparison.Ordinal)
            || normalized.Contains("credential", StringComparison.Ordinal)
            || normalized.Contains("password", StringComparison.Ordinal)
            || normalized.Contains("passwd", StringComparison.Ordinal)
            || normalized.Contains("secret", StringComparison.Ordinal)
            || normalized.Contains("token", StringComparison.Ordinal)
            || normalized.Contains("api_key", StringComparison.Ordinal)
            || normalized.Contains("connection_string", StringComparison.Ordinal)
            || normalized.Contains("cookie", StringComparison.Ordinal);
    }

    private static void RejectCredentialLikeContent(string? value, string field)
    {
        if (!string.IsNullOrEmpty(value) && CredentialLike().IsMatch(value))
        {
            throw new CaseValidationException(
                $"Credential-like content is not allowed in {field}.");
        }
    }

    [GeneratedRegex(
        @"(?i)(?:bearer\s+[a-z0-9._~+/=-]{12,}|-----BEGIN [A-Z ]*PRIVATE KEY-----|(?:gh[pousr]_|glpat-|xox[baprs]-)[a-z0-9_-]{8,}|(?:password|passwd|secret|api[_-]?key|token)\s*[:=]\s*\S{6,})",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex CredentialLike();
}
