using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using IncidentBot.Api.Domain;
using IncidentBot.Api.Infrastructure;
using IncidentBot.Api.Options;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace IncidentBot.Api.Connectors;

public sealed class McpStreamableHttpClient(
    IHttpClientFactory httpClientFactory,
    IOptions<IncidentBotOptions> options,
    ICredentialProvider credentials,
    ILogger<McpStreamableHttpClient> logger) : IMcpEvidenceAdapter
{
    private const int MaximumProtocolResponseBytes = 1024 * 1024;
    private const int MaximumToolResponseBytes = 4 * 1024 * 1024;
    private const int MinimumToolResponseBytes = 64 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task CheckConnectionAsync(
        McpToolConfiguration configuration,
        CancellationToken cancellationToken)
    {
        string? sessionId = null;
        var client = httpClientFactory.CreateClient();
        var initialize = await SendAsync(client, configuration, sessionId, new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["protocolVersion"] = "2025-06-18",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "incident-bot", ["version"] = "1.0.0" }
            }
        }, cancellationToken);
        sessionId = initialize.SessionId;

        await SendAsync(client, configuration, sessionId, new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "notifications/initialized"
        }, cancellationToken, expectResponse: false);

        var tools = await SendAsync(client, configuration, sessionId, new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 2,
            ["method"] = "tools/list",
            ["params"] = new JsonObject()
        }, cancellationToken);
        var advertised = tools.Body?["result"]?["tools"]?.AsArray()
            .Any(tool => string.Equals(
                tool?["name"]?.GetValue<string>(), configuration.ToolName, StringComparison.Ordinal)) == true;
        if (!advertised)
        {
            throw new InvalidOperationException(
                $"MCP tool '{configuration.ToolName}' is not advertised by the configured server.");
        }
    }

    public async Task<ConnectorResult> CollectAsync(
        string source,
        McpToolConfiguration configuration,
        InvestigationContext context,
        EvidenceScope scope,
        object allowedResources,
        string? allowedBaseUrl,
        CancellationToken cancellationToken)
    {
        if (!options.Value.McpEnabled)
        {
            return ConnectorResult.Unavailable(source, 0, "MCP is disabled by feature flag.");
        }

        var stopwatch = Stopwatch.StartNew();
        var phase = "initialize";
        try
        {
            logger.LogDebug(
                "MCP collection started for {Source} using tool {McpTool} on {McpServerHost}",
                source, configuration.ToolName, ServerHost(configuration.ServerUrl));
            var client = httpClientFactory.CreateClient();
            string? sessionId = null;
            var initialize = await SendAsync(client, configuration, sessionId, new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "initialize",
                ["params"] = new JsonObject
                {
                    ["protocolVersion"] = "2025-06-18",
                    ["capabilities"] = new JsonObject(),
                    ["clientInfo"] = new JsonObject { ["name"] = "incident-bot", ["version"] = "1.0.0" }
                }
            }, cancellationToken);
            sessionId = initialize.SessionId;

            phase = "initialized-notification";
            await SendAsync(client, configuration, sessionId, new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "notifications/initialized"
            }, cancellationToken, expectResponse: false);

            phase = "tools-list";
            var tools = await SendAsync(client, configuration, sessionId, new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 2,
                ["method"] = "tools/list",
                ["params"] = new JsonObject()
            }, cancellationToken);
            var available = tools.Body?["result"]?["tools"]?.AsArray()
                .Any(tool => string.Equals(tool?["name"]?.GetValue<string>(), configuration.ToolName, StringComparison.Ordinal)) == true;
            if (!available)
            {
                throw new InvalidOperationException($"MCP tool '{configuration.ToolName}' is not advertised by the configured server.");
            }

            var allowedResourceNode = JsonSerializer.SerializeToNode(allowedResources, JsonOptions);
            var arguments = new JsonObject
            {
                ["incident"] = JsonSerializer.SerializeToNode(new
                {
                    id = context.IncidentId,
                    pagerDutyIncidentId = context.PagerDutyIncidentId,
                    context.ServiceId,
                    context.Title,
                    state = context.State.ToString(),
                    context.Labels
                }, JsonOptions),
                ["window"] = JsonSerializer.SerializeToNode(new { scope.Start, scope.End }, JsonOptions),
                ["limits"] = JsonSerializer.SerializeToNode(new { scope.MaxItems, scope.MaxBytes }, JsonOptions),
                ["allowedResources"] = allowedResourceNode
            };
            phase = "tools-call";
            var called = await SendAsync(client, configuration, sessionId, new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 3,
                ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = configuration.ToolName, ["arguments"] = arguments }
            }, cancellationToken, maxResponseBytes: ToolResponseByteLimit(scope.MaxBytes));

            phase = "result-validation";
            var result = called.Body?["result"];
            if (result?["isError"]?.GetValue<bool>() == true)
            {
                throw new InvalidOperationException("The configured MCP tool returned an error result.");
            }

            var structured = result?["structuredContent"];
            var json = structured?.ToJsonString() ?? result?["content"]?.AsArray()
                .FirstOrDefault(item => item?["type"]?.GetValue<string>() == "text")?["text"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new InvalidOperationException("MCP tool did not return structured content or JSON text.");
            }

            var connectorResult = JsonSerializer.Deserialize<ConnectorResult>(json, JsonOptions)
                ?? throw new InvalidOperationException("MCP connector result was empty.");
            var credential = credentials.Get(configuration.CredentialEnv);
            connectorResult = McpConnectorResultBoundary.Normalize(
                source, connectorResult, scope, context.TriggeredAt, allowedBaseUrl, allowedResourceNode, credential);
            logger.LogDebug(
                "MCP collection completed for {Source} in {DurationMilliseconds} ms with {FindingCount} findings",
                source, stopwatch.ElapsedMilliseconds, connectorResult.Findings.Count);
            return connectorResult;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning(
                "MCP collection was cancelled or timed out during {McpPhase} for {Source} after {DurationMilliseconds} ms using tool {McpTool}",
                phase, source, stopwatch.ElapsedMilliseconds, configuration.ToolName);
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "MCP collection failed during {McpPhase} for {Source} after {DurationMilliseconds} ms using tool {McpTool}: {FailureType}",
                phase, source, stopwatch.ElapsedMilliseconds, configuration.ToolName, exception.GetType().Name);
            throw;
        }
    }

    private static string ServerHost(string serverUrl) =>
        Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri) ? uri.Host : "invalid-host";

    private async Task<McpResponse> SendAsync(
        HttpClient client,
        McpToolConfiguration configuration,
        string? sessionId,
        JsonObject message,
        CancellationToken cancellationToken,
        bool expectResponse = true,
        int maxResponseBytes = MaximumProtocolResponseBytes)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, configuration.ServerUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2025-06-18");
        if (!string.IsNullOrWhiteSpace(sessionId)) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
        var credential = credentials.Get(configuration.CredentialEnv);
        if (!string.IsNullOrWhiteSpace(credential)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        request.Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var returnedSession = response.Headers.TryGetValues("Mcp-Session-Id", out var values) ? values.FirstOrDefault() : sessionId;
        if (!expectResponse || response.StatusCode == System.Net.HttpStatusCode.Accepted)
        {
            return new McpResponse(returnedSession, null);
        }

        var content = await ReadBoundedContentAsync(response.Content, maxResponseBytes, cancellationToken);
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
        {
            content = string.Join('\n', content.Split('\n')
                .Where(line => line.StartsWith("data:", StringComparison.Ordinal))
                .Select(line => line[5..].Trim()));
        }
        return new McpResponse(returnedSession, JsonNode.Parse(content));
    }

    internal static int ToolResponseByteLimit(int maxBytes) =>
        (int)Math.Min(
            MaximumToolResponseBytes,
            Math.Max(MinimumToolResponseBytes, Math.Max(0L, (long)maxBytes) * 2));

    internal static async Task<string> ReadBoundedContentAsync(
        HttpContent content,
        int maxResponseBytes,
        CancellationToken cancellationToken)
    {
        var limit = Math.Max(1, maxResponseBytes);
        if (content.Headers.ContentLength is > 0 and var contentLength && contentLength > limit)
        {
            throw new InvalidOperationException($"MCP response exceeded its {limit}-byte limit.");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[(int)Math.Min(8192L, (long)limit + 1)];
        while (true)
        {
            var count = await stream.ReadAsync(chunk, cancellationToken);
            if (count == 0) break;
            if (buffer.Length + count > limit)
            {
                throw new InvalidOperationException($"MCP response exceeded its {limit}-byte limit.");
            }
            await buffer.WriteAsync(chunk.AsMemory(0, count), cancellationToken);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    private sealed record McpResponse(string? SessionId, JsonNode? Body);
}
