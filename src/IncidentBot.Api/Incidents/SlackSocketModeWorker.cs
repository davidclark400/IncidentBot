using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text.Json;
using IncidentBot.Api.Options;
using Microsoft.Extensions.Options;

namespace IncidentBot.Api.Incidents;

public sealed class SlackSocketModeWorker(
    IHttpClientFactory httpClientFactory,
    IOptions<SlackOptions> options,
    InvestigationRestartService restart,
    SlackPublisher slack,
    ILogger<SlackSocketModeWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogDebug("Slack Socket Mode is disabled");
            return;
        }

        var appToken = Environment.GetEnvironmentVariable(options.Value.AppTokenEnv);
        if (string.IsNullOrWhiteSpace(appToken))
        {
            logger.LogCritical(
                "Slack is enabled but the Socket Mode app token environment variable {AppTokenEnv} is missing",
                options.Value.AppTokenEnv);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var socketUrl = await OpenConnectionAsync(appToken, stoppingToken);
                await ReceiveAsync(socketUrl, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Slack Socket Mode connection failed; reconnecting");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(options.Value.ReconnectDelaySeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task<Uri> OpenConnectionAsync(string appToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{options.Value.ApiBaseUrl.TrimEnd('/')}/apps.connections.open");
        request.Content = new FormUrlEncodedContent(Array.Empty<KeyValuePair<string, string>>());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", appToken);
        using var response = await httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;
        if (!root.GetProperty("ok").GetBoolean())
        {
            throw new InvalidOperationException(
                $"Slack apps.connections.open failed: {root.GetProperty("error").GetString()}");
        }

        var url = root.GetProperty("url").GetString();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var socketUrl))
        {
            throw new InvalidOperationException("Slack apps.connections.open returned an invalid websocket URL.");
        }

        logger.LogInformation("Slack Socket Mode connection opened");
        return socketUrl;
    }

    private async Task ReceiveAsync(Uri socketUrl, CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(socketUrl, cancellationToken);
        logger.LogInformation("Slack Socket Mode websocket connected");

        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var message = await ReceiveMessageAsync(socket, cancellationToken);
            if (message is null)
            {
                return;
            }

            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            if (root.TryGetProperty("envelope_id", out var envelopeIdElement))
            {
                await SendAckAsync(socket, envelopeIdElement.GetString()!, cancellationToken);
            }

            if (root.TryGetProperty("type", out var typeElement) &&
                typeElement.GetString() == "interactive" &&
                root.TryGetProperty("payload", out var payload))
            {
                _ = HandleInteractiveAsync(payload.GetRawText(), cancellationToken);
            }
        }
    }

    private async Task HandleInteractiveAsync(string payloadJson, CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var payload = document.RootElement;
            if (payload.GetProperty("type").GetString() != "block_actions" ||
                !payload.TryGetProperty("actions", out var actions) ||
                actions.GetArrayLength() == 0)
            {
                return;
            }

            var action = actions[0];
            if (action.GetProperty("action_id").GetString() != "restart_agent" ||
                !Guid.TryParse(action.GetProperty("value").GetString(), out var incidentId))
            {
                return;
            }

            var channel = TryGetString(payload, "container", "channel_id");
            var timestamp = TryGetString(payload, "container", "message_ts");
            if (!await restart.RestartAsync(incidentId, channel, timestamp, cancellationToken))
            {
                logger.LogWarning(
                    "Ignored Slack restart action for incident {IncidentId} because the message no longer matches an incident",
                    incidentId);
                return;
            }

            try
            {
                await slack.PublishAsync(incidentId, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(
                    exception,
                    "Investigation restart was queued for incident {IncidentId}, but Slack could not be updated immediately",
                    incidentId);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Slack interactive action handling failed");
        }
    }

    private static async Task SendAckAsync(
        ClientWebSocket socket,
        string envelopeId,
        CancellationToken cancellationToken)
    {
        var acknowledgement = JsonSerializer.SerializeToUtf8Bytes(new { envelope_id = envelopeId });
        await socket.SendAsync(
            new ArraySegment<byte>(acknowledgement),
            WebSocketMessageType.Text,
            true,
            cancellationToken);
    }

    private static async Task<byte[]?> ReceiveMessageAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        using var message = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return message.ToArray();
            }
        }
    }

    private static string? TryGetString(JsonElement element, string parent, string property)
    {
        return element.TryGetProperty(parent, out var parentElement) &&
               parentElement.TryGetProperty(property, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
