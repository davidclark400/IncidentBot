using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentBot.Api.Domain;
using IncidentBot.Api.Infrastructure;
using IncidentBot.Api.Options;

namespace IncidentBot.Api.Connectors;

internal static class ConnectorUtilities
{
    public static Task<ConnectorResult> CollectAsync(
        string source,
        ConnectorTransport transport,
        IMcpEvidenceAdapter mcp,
        InvestigationContext context,
        EvidenceScope scope,
        object allowedResources,
        Func<CancellationToken, Task<ConnectorResult>> collectNative,
        CancellationToken cancellationToken) =>
        ExecuteAsync(source, transport.TimeoutSeconds, ct =>
            transport.Mode == "mcp"
                ? mcp.CollectAsync(
                    source,
                    transport.Mcp!,
                    context,
                    EffectiveMcpScope(scope, transport),
                    allowedResources,
                    transport.BaseUrl,
                    ct)
                : collectNative(ct), cancellationToken);

    private static EvidenceScope EffectiveMcpScope(EvidenceScope scope, ConnectorTransport transport) =>
        scope with
        {
            MaxItems = Math.Min(Math.Max(0, scope.MaxItems), Math.Max(0, transport.MaxItems)),
            MaxBytes = Math.Min(Math.Max(0, scope.MaxBytes), Math.Max(0, transport.MaxBytes))
        };

    public static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string url,
        ConnectorTransport transport,
        ICredentialProvider credentials)
    {
        var request = new HttpRequestMessage(method, url);
        var credential = string.IsNullOrWhiteSpace(transport.CredentialEnv)
            ? null
            : credentials.Get(transport.CredentialEnv);
        if (!string.IsNullOrWhiteSpace(credential))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        }
        return request;
    }

    public static string Url(ConnectorTransport transport, string path) =>
        $"{transport.BaseUrl.TrimEnd('/')}/{path.TrimStart('/')}";

    public static string Id(string source, params string[] values)
    {
        var input = string.Join('|', new[] { source }.Concat(values));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)))[..24];
    }

    public static JsonObject Provenance(string operation, object scope) => new()
    {
        ["operation"] = operation,
        ["scope"] = JsonSerializer.SerializeToNode(scope)
    };

    public static DateTimeOffset Timestamp(JsonElement element, string name, DateTimeOffset fallback)
    {
        if (!element.TryGetProperty(name, out var value)) return fallback;
        if (value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), out var parsed)) return parsed;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var unix))
        {
            if (unix > 100_000_000_000_000_000) unix /= 1_000_000_000;
            else if (unix > 100_000_000_000_000) unix /= 1_000_000;
            else if (unix > 100_000_000_000) unix /= 1_000;
            return DateTimeOffset.FromUnixTimeSeconds(unix);
        }
        return fallback;
    }

    public static string Text(JsonElement element, string name, string fallback = "unknown") =>
        element.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : value.ToString()
            : fallback;

    public static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Length <= max ? value : value[..max] + "…";
    }

    public static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    public static async Task<JsonDocument> ReadBoundedJsonAsync(
        HttpResponseMessage response,
        int maxBytes,
        CancellationToken ct,
        Action<int>? observeBytesRead = null)
    {
        response.EnsureSuccessStatusCode();
        maxBytes = Math.Max(0, maxBytes);
        if (response.Content.Headers.ContentLength is { } contentLength && contentLength > maxBytes)
        {
            // Conservatively exhaust a caller's shared allowance even when the declared
            // response is rejected before its body is downloaded.
            observeBytesRead?.Invoke(maxBytes);
            throw new InvalidOperationException("Connector response exceeded its configured byte limit.");
        }
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        while (true)
        {
            // Read at most one byte beyond the remaining allowance. This proves that the
            // response is oversized without buffering or downloading another full chunk.
            var remaining = Math.Max(0, maxBytes - checked((int)buffer.Length));
            var count = await stream.ReadAsync(bytes.AsMemory(0, Math.Min(bytes.Length, remaining + 1)), ct);
            if (count == 0) break;
            observeBytesRead?.Invoke(count);
            if (buffer.Length + count > maxBytes)
            {
                throw new InvalidOperationException("Connector response exceeded its configured byte limit.");
            }
            await buffer.WriteAsync(bytes.AsMemory(0, count), ct);
        }
        buffer.Position = 0;
        return await JsonDocument.ParseAsync(buffer, cancellationToken: ct);
    }

    public static async Task<string> ReadBoundedTextAsync(
        HttpResponseMessage response,
        int maxBytes,
        CancellationToken ct,
        Action<int>? observeBytesRead = null)
    {
        response.EnsureSuccessStatusCode();
        maxBytes = Math.Max(0, maxBytes);
        if (response.Content.Headers.ContentLength is { } contentLength && contentLength > maxBytes)
        {
            observeBytesRead?.Invoke(maxBytes);
            throw new InvalidOperationException("Connector response exceeded its configured byte limit.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        while (true)
        {
            var remaining = Math.Max(0, maxBytes - checked((int)buffer.Length));
            var count = await stream.ReadAsync(bytes.AsMemory(0, Math.Min(bytes.Length, remaining + 1)), ct);
            if (count == 0) break;
            observeBytesRead?.Invoke(count);
            if (buffer.Length + count > maxBytes)
            {
                throw new InvalidOperationException("Connector response exceeded its configured byte limit.");
            }

            await buffer.WriteAsync(bytes.AsMemory(0, count), ct);
        }

        var encoding = Encoding.UTF8;
        var charset = response.Content.Headers.ContentType?.CharSet;
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                encoding = Encoding.GetEncoding(charset.Trim('"'));
            }
            catch (ArgumentException)
            {
                // UTF-8 is the safe default for connector JSON/NDJSON responses.
            }
        }

        return encoding.GetString(buffer.ToArray());
    }

    public static bool IsByteLimitException(InvalidOperationException exception) =>
        exception.Message.Contains("configured byte limit", StringComparison.Ordinal);

    public static string? CombineDiagnostics(params string?[] diagnostics)
    {
        var combined = string.Join(" ", diagnostics.Where(item => !string.IsNullOrWhiteSpace(item)));
        if (combined.Length == 0) return null;
        return combined.Length <= 500 ? combined : combined[..499] + "…";
    }

    public static async Task<ConnectorResult> ExecuteAsync(
        string source,
        int timeoutSeconds,
        Func<CancellationToken, Task<ConnectorResult>> action,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 120)));
        try
        {
            var result = await action(timeout.Token);
            return result with { DurationMilliseconds = stopwatch.ElapsedMilliseconds };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ConnectorResult.Unavailable(
                source,
                stopwatch.ElapsedMilliseconds,
                $"Timeout after {Math.Clamp(timeoutSeconds, 1, 120)} seconds");
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            return ConnectorResult.Unavailable(
                source,
                stopwatch.ElapsedMilliseconds,
                $"{exception.GetType().Name}: {Truncate(exception.Message, 450)}");
        }
    }
}
