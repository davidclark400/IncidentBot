using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using IncidentBot.Api.Infrastructure;
using IncidentBot.Api.Options;
using Microsoft.Extensions.Options;

namespace IncidentBot.Api.Incidents;

public sealed class SlackSocketModeWorker(
    IHttpClientFactory httpClientFactory,
    IOptions<SlackOptions> options,
    SlackInteractiveHandler interactiveHandler,
    SlackMentionHandler mentionHandler,
    ISlackReplyPublisher replyPublisher,
    ICredentialProvider credentials,
    ILogger<SlackSocketModeWorker> logger) : BackgroundService
{
    private const string BusyReply =
        "IncidentBot is already processing the maximum number of Slack requests. Please try again shortly.";

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

        var queue = Channel.CreateBounded<SlackMention>(new BoundedChannelOptions(
            options.Value.PromptQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = options.Value.PromptWorkerCount == 1,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });
        var rejections = Channel.CreateBounded<SlackMention>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });
        var workers = options.Value.PromptMentionsEnabled
            ? Enumerable.Range(0, options.Value.PromptWorkerCount)
                .Select(_ => ProcessMentionsAsync(queue.Reader, stoppingToken))
                .ToArray()
            : [];
        var rejectionWorker = options.Value.PromptMentionsEnabled
            ? ProcessRejectionsAsync(rejections.Reader, stoppingToken)
            : Task.CompletedTask;
        var dedupe = new SlackEventDedupe(Math.Max(64, options.Value.PromptQueueCapacity * 4));
        var rateLimiter = new SlackPromptRateLimiter(
            options.Value.PromptRequestsPerMinutePerUser,
            options.Value.PromptRequestsPerMinute,
            TimeProvider.System);
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
                        queue.Writer,
                        rejections.Writer,
                        dedupe,
                        rateLimiter,
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
            queue.Writer.TryComplete();
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
        ChannelWriter<SlackMention> mentionWriter,
        ChannelWriter<SlackMention> rejectionWriter,
        SlackEventDedupe dedupe,
        SlackPromptRateLimiter rateLimiter,
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

            if (botIdentity is not { } identity ||
                !SlackMentionParser.TryParseEventsApiEnvelope(
                    root,
                    identity,
                    options.Value.MaximumPromptCharacters,
                    options.Value.AllowExternalSharedChannels,
                    out var mention) ||
                mention is null)
            {
                continue;
            }

            if (!dedupe.TryRemember(mention.EventId))
            {
                continue;
            }

            if (!rateLimiter.TryAcquire(mention.TeamId, mention.ChannelId, mention.UserId) ||
                !mentionWriter.TryWrite(mention))
            {
                // Never wait for chat.postMessage in the socket receive/ACK loop. One
                // separately supervised slot coalesces overload replies.
                rejectionWriter.TryWrite(mention);
            }
        }
    }

    private async Task ProcessMentionsAsync(
        ChannelReader<SlackMention> reader,
        CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var mention in reader.ReadAllAsync(stoppingToken))
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

internal readonly record struct SlackBotIdentity(string TeamId, string UserId);

internal static class SlackMentionParser
{
    internal static bool TryParseEventsApiEnvelope(
        JsonElement envelope,
        SlackBotIdentity ownIdentity,
        int maximumPromptCharacters,
        bool allowExternalSharedChannels,
        out SlackMention? mention)
    {
        mention = null;
        if (GetString(envelope, "type") != "events_api" ||
            !envelope.TryGetProperty("payload", out var payload) ||
            payload.ValueKind != JsonValueKind.Object ||
            GetString(payload, "type") != "event_callback" ||
            !string.Equals(
                GetString(payload, "team_id"),
                ownIdentity.TeamId,
                StringComparison.Ordinal) ||
            !payload.TryGetProperty("event", out var slackEvent) ||
            slackEvent.ValueKind != JsonValueKind.Object ||
            GetString(slackEvent, "type") != "app_mention" ||
            (!allowExternalSharedChannels &&
             (GetBoolean(payload, "is_ext_shared_channel") ||
              GetBoolean(slackEvent, "is_ext_shared_channel"))) ||
            IsBotEvent(slackEvent))
        {
            return false;
        }

        var eventId = GetString(payload, "event_id");
        var channelId = GetString(slackEvent, "channel");
        var userId = GetString(slackEvent, "user");
        var messageTimestamp = GetString(slackEvent, "ts");
        var text = GetString(slackEvent, "text");
        if (string.IsNullOrWhiteSpace(eventId) ||
            string.IsNullOrWhiteSpace(channelId) ||
            string.IsNullOrWhiteSpace(userId) ||
            string.Equals(userId, ownIdentity.UserId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(messageTimestamp) ||
            text is null)
        {
            return false;
        }

        var prompt = NormalizePrompt(text, ownIdentity.UserId);
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > maximumPromptCharacters)
        {
            return false;
        }

        mention = new SlackMention(
            eventId,
            ownIdentity.TeamId,
            channelId,
            userId,
            messageTimestamp,
            GetString(slackEvent, "thread_ts"),
            prompt);
        return true;
    }

    internal static string NormalizePrompt(string text, string ownUserId)
    {
        var ownMention = $"<@{ownUserId}>";
        return text
            .Replace(ownMention, string.Empty, StringComparison.Ordinal)
            .Replace("&lt;", "<", StringComparison.Ordinal)
            .Replace("&gt;", ">", StringComparison.Ordinal)
            .Replace("&amp;", "&", StringComparison.Ordinal)
            .Trim();
    }

    private static bool IsBotEvent(JsonElement slackEvent) =>
        HasNonEmptyString(slackEvent, "bot_id") ||
        HasNonEmptyString(slackEvent, "app_id") ||
        slackEvent.TryGetProperty("bot_profile", out var botProfile) &&
        botProfile.ValueKind == JsonValueKind.Object ||
        string.Equals(
            GetString(slackEvent, "subtype"),
            "bot_message",
            StringComparison.Ordinal);

    private static bool HasNonEmptyString(JsonElement element, string property) =>
        !string.IsNullOrWhiteSpace(GetString(element, property));

    private static bool GetBoolean(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.True;

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

internal sealed class SlackEventDedupe
{
    private readonly int _capacity;
    private readonly HashSet<string> _eventIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _oldestFirst = new();
    private readonly Lock _gate = new();

    internal SlackEventDedupe(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
    }

    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _eventIds.Count;
            }
        }
    }

    internal bool TryRemember(string eventId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        lock (_gate)
        {
            if (!_eventIds.Add(eventId))
            {
                return false;
            }

            _oldestFirst.Enqueue(eventId);
            if (_oldestFirst.Count > _capacity)
            {
                _eventIds.Remove(_oldestFirst.Dequeue());
            }
            return true;
        }
    }
}

internal sealed class SlackPromptRateLimiter
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly int _perUserLimit;
    private readonly int _globalLimit;
    private readonly TimeProvider _timeProvider;
    private readonly Queue<DateTimeOffset> _global = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _principals = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    internal SlackPromptRateLimiter(
        int perUserLimit,
        int globalLimit,
        TimeProvider timeProvider)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(perUserLimit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(globalLimit);
        _perUserLimit = perUserLimit;
        _globalLimit = globalLimit;
        _timeProvider = timeProvider;
    }

    internal bool TryAcquire(string teamId, string channelId, string userId)
    {
        var principal = $"{teamId}\u001f{channelId}\u001f{userId}";
        var now = _timeProvider.GetUtcNow();
        var cutoff = now - Window;
        lock (_gate)
        {
            Prune(_global, cutoff);
            foreach (var stale in _principals
                         .Where(item =>
                         {
                             Prune(item.Value, cutoff);
                             return item.Value.Count == 0;
                         })
                         .Select(item => item.Key)
                         .ToArray())
            {
                _principals.Remove(stale);
            }

            if (_global.Count >= _globalLimit)
            {
                return false;
            }

            if (!_principals.TryGetValue(principal, out var perUser))
            {
                perUser = new Queue<DateTimeOffset>();
                _principals.Add(principal, perUser);
            }
            if (perUser.Count >= _perUserLimit)
            {
                return false;
            }

            _global.Enqueue(now);
            perUser.Enqueue(now);
            return true;
        }
    }

    private static void Prune(Queue<DateTimeOffset> values, DateTimeOffset cutoff)
    {
        while (values.TryPeek(out var value) && value <= cutoff)
        {
            values.Dequeue();
        }
    }
}
