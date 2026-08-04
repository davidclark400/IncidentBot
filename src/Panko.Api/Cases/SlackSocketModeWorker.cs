using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Panko.Api.Infrastructure;
using Panko.Api.Options;
using Microsoft.Extensions.Options;

namespace Panko.Api.Cases;

public sealed class SlackSocketModeWorker(
    IHttpClientFactory httpClientFactory,
    IOptions<SlackOptions> options,
    SlackInteractiveHandler interactiveHandler,
    SlackMentionHandler mentionHandler,
    ISlackReplyPublisher replyPublisher,
    ICredentialProvider credentials,
    TimeProvider timeProvider,
    ILogger<SlackSocketModeWorker> logger) : BackgroundService
{
    private const string BusyReply =
        "Panko is already processing the maximum number of Slack requests. Please try again shortly.";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogDebug("Slack Socket Mode is disabled");
            return;
        }

        var appToken = credentials.Get(options.Value.AppTokenEnv);
        if (string.IsNullOrWhiteSpace(appToken))
        {
            logger.LogCritical(
                "Slack is enabled but the Socket Mode app token environment variable {AppTokenEnv} is missing",
                options.Value.AppTokenEnv);
            return;
        }

        string? botToken = null;
        if (options.Value.PromptMentionsEnabled)
        {
            botToken = credentials.Get(options.Value.BotTokenEnv);
            if (string.IsNullOrWhiteSpace(botToken))
            {
                logger.LogCritical(
                    "Slack prompt mentions are enabled but the bot token environment variable {BotTokenEnv} is missing",
                    options.Value.BotTokenEnv);
                return;
            }
        }

        var admission = new SlackPromptAdmission(options.Value, timeProvider);
        var rejections = Channel.CreateBounded<SlackMention>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });
        var workers = options.Value.PromptMentionsEnabled
            ? Enumerable.Range(0, options.Value.PromptWorkerCount)
                .Select(_ => ProcessMentionsAsync(admission, stoppingToken))
                .ToArray()
            : [];
        var rejectionWorker = options.Value.PromptMentionsEnabled
            ? ProcessRejectionsAsync(rejections.Reader, stoppingToken)
            : Task.CompletedTask;
        SlackBotIdentity? botIdentity = null;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (options.Value.PromptMentionsEnabled && botIdentity is null)
                    {
                        botIdentity = await ResolveBotIdentityAsync(botToken!, stoppingToken);
                    }

                    var socketUrl = await OpenConnectionAsync(appToken, stoppingToken);
                    await ReceiveAsync(
                        socketUrl,
                        botIdentity,
                        admission,
                        rejections.Writer,
                        stoppingToken);
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
                    await Task.Delay(
                        TimeSpan.FromSeconds(options.Value.ReconnectDelaySeconds),
                        stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            admission.Complete();
            rejections.Writer.TryComplete();
            await Task.WhenAll(workers.Append(rejectionWorker));
        }
    }

    private async Task<SlackBotIdentity> ResolveBotIdentityAsync(
        string botToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{options.Value.ApiBaseUrl.TrimEnd('/')}/auth.test")
        {
            Content = new FormUrlEncodedContent([])
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", botToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.TimeoutSeconds));
        try
        {
            using var response = await httpClientFactory.CreateClient().SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            response.EnsureSuccessStatusCode();
            using var document = await SlackWebApiJson.ReadAsync(response.Content, timeout.Token);
            var root = document.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            {
                throw new InvalidOperationException(
                    $"Slack auth.test failed: {GetSlackError(root)}");
            }

            var teamId = GetRequiredString(root, "team_id", "Slack auth.test");
            var userId = GetRequiredString(root, "user_id", "Slack auth.test");
            logger.LogInformation("Slack prompt identity resolved for team {SlackTeamId}", teamId);
            return new SlackBotIdentity(teamId, userId);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Slack auth.test timed out after {options.Value.TimeoutSeconds} seconds.");
        }
    }

    private async Task<Uri> OpenConnectionAsync(
        string appToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{options.Value.ApiBaseUrl.TrimEnd('/')}/apps.connections.open")
        {
            Content = new FormUrlEncodedContent([])
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", appToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.TimeoutSeconds));
        try
        {
            using var response = await httpClientFactory.CreateClient().SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            response.EnsureSuccessStatusCode();
            using var document = await SlackWebApiJson.ReadAsync(response.Content, timeout.Token);
            var root = document.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            {
                throw new InvalidOperationException(
                    $"Slack apps.connections.open failed: {GetSlackError(root)}");
            }

            var url = GetRequiredString(root, "url", "Slack apps.connections.open");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var socketUrl) ||
                socketUrl.Scheme != "wss")
            {
                throw new InvalidOperationException(
                    "Slack apps.connections.open returned an invalid websocket URL.");
            }

            logger.LogInformation("Slack Socket Mode connection opened");
            return socketUrl;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Slack apps.connections.open timed out after {options.Value.TimeoutSeconds} seconds.");
        }
    }

    private async Task ReceiveAsync(
        Uri socketUrl,
        SlackBotIdentity? botIdentity,
        SlackPromptAdmission admission,
        ChannelWriter<SlackMention> rejectionWriter,
        CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(options.Value.TimeoutSeconds));
            try
            {
                await socket.ConnectAsync(socketUrl, connectTimeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Slack Socket Mode websocket connection timed out after {options.Value.TimeoutSeconds} seconds.");
            }
        }
        logger.LogInformation("Slack Socket Mode websocket connected");

        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var message = await ReceiveMessageAsync(
                socket,
                options.Value.MaximumEnvelopeBytes,
                cancellationToken);
            if (message is null)
            {
                return;
            }

            using var document = JsonDocument.Parse(
                message,
                new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            var envelopeId = GetString(root, "envelope_id");
            if (!string.IsNullOrWhiteSpace(envelopeId))
            {
                await SendAckAsync(socket, envelopeId, cancellationToken);
            }

            var envelopeType = GetString(root, "type");
            if (envelopeType == "disconnect")
            {
                logger.LogInformation(
                    "Slack requested Socket Mode disconnect: {DisconnectReason}",
                    GetString(root, "reason") ?? "unspecified");
                return;
            }

            if (envelopeType == "interactive" &&
                root.TryGetProperty("payload", out var interactivePayload))
            {
                _ = interactiveHandler.HandleAsync(interactivePayload.GetRawText(), cancellationToken);
                continue;
            }

            if (botIdentity is not { } identity)
            {
                continue;
            }

            var result = admission.Admit(root, identity);
            if (result is not
                {
                    Outcome: SlackPromptAdmissionOutcome.Busy,
                    BusyMention: { } busyMention
                })
            {
                continue;
            }

            // Never wait for chat.postMessage in the socket receive/ACK loop. One
            // separately supervised slot coalesces overload replies.
            rejectionWriter.TryWrite(busyMention);
        }
    }

    private async Task ProcessMentionsAsync(
        SlackPromptAdmission admission,
        CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var mention in admission.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await mentionHandler.HandleAsync(mention, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    logger.LogError(
                        exception,
                        "Slack prompt event {SlackEventId} failed",
                        mention.EventId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task ReplyBusyAsync(
        SlackMention mention,
        CancellationToken cancellationToken)
    {
        try
        {
            await replyPublisher.ReplyAsync(
                new SlackReplyTarget(
                    mention.ChannelId,
                    mention.ThreadTimestamp ?? mention.MessageTimestamp),
                BusyReply,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Slack busy reply failed for event {SlackEventId}",
                mention.EventId);
        }
    }

    private async Task ProcessRejectionsAsync(
        ChannelReader<SlackMention> reader,
        CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var mention in reader.ReadAllAsync(stoppingToken))
            {
                await ReplyBusyAsync(mention, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
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

    internal static async Task<byte[]?> ReceiveMessageAsync(
        ClientWebSocket socket,
        int maximumEnvelopeBytes,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEnvelopeBytes);

        using var message = new MemoryStream(capacity: Math.Min(8192, maximumEnvelopeBytes));
        var buffer = new byte[Math.Min(8192, maximumEnvelopeBytes)];
        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (socket.State == WebSocketState.CloseReceived)
                {
                    await socket.CloseOutputAsync(
                        result.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                        result.CloseStatusDescription,
                        cancellationToken);
                }
                return null;
            }
            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new InvalidDataException("Slack Socket Mode sent a non-text message.");
            }
            if (message.Length + result.Count > maximumEnvelopeBytes)
            {
                throw new InvalidDataException(
                    $"Slack Socket Mode envelope exceeded {maximumEnvelopeBytes} bytes.");
            }

            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return message.ToArray();
            }
        }
    }

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string GetRequiredString(
        JsonElement element,
        string property,
        string operation)
    {
        var value = GetString(element, property);
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"{operation} omitted '{property}'.");
    }

    private static string GetSlackError(JsonElement root) =>
        GetString(root, "error") ?? "unknown_error";
}
