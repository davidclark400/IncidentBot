using System.Text.Json;
using System.Threading.Channels;
using Panko.Api.Options;

namespace Panko.Api.Cases;

internal enum SlackPromptAdmissionOutcome
{
    Dropped,
    Accepted,
    Busy
}

internal readonly record struct SlackPromptAdmissionResult(
    SlackPromptAdmissionOutcome Outcome,
    SlackMention? BusyMention);

internal readonly record struct SlackBotIdentity(string TeamId, string UserId);

/// <summary>
/// Owns the complete in-process admission decision for Slack prompt mentions.
/// </summary>
internal sealed class SlackPromptAdmission
{
    private static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(1);

    private readonly int maximumPromptCharacters;
    private readonly bool allowExternalSharedChannels;
    private readonly int dedupeCapacity;
    private readonly int perUserRateLimit;
    private readonly int globalRateLimit;
    private readonly TimeProvider timeProvider;
    private readonly Channel<SlackMention> accepted;
    private readonly HashSet<string> eventIds = new(StringComparer.Ordinal);
    private readonly Queue<string> oldestEventIds = new();
    private readonly Queue<DateTimeOffset> globalRequests = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> principalRequests = new(StringComparer.Ordinal);
    private readonly Lock gate = new();

    internal SlackPromptAdmission(SlackOptions options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumPromptCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.PromptQueueCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.PromptWorkerCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.PromptRequestsPerMinutePerUser);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.PromptRequestsPerMinute);

        maximumPromptCharacters = options.MaximumPromptCharacters;
        allowExternalSharedChannels = options.AllowExternalSharedChannels;
        dedupeCapacity = Math.Max(64, options.PromptQueueCapacity * 4);
        perUserRateLimit = options.PromptRequestsPerMinutePerUser;
        globalRateLimit = options.PromptRequestsPerMinute;
        this.timeProvider = timeProvider;
        accepted = Channel.CreateBounded<SlackMention>(new BoundedChannelOptions(
            options.PromptQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = options.PromptWorkerCount == 1,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });
    }

    internal SlackPromptAdmissionResult Admit(
        JsonElement envelope,
        SlackBotIdentity ownIdentity)
    {
        if (!TryCreateMention(envelope, ownIdentity, out var mention) || mention is null)
        {
            return new SlackPromptAdmissionResult(SlackPromptAdmissionOutcome.Dropped, null);
        }

        lock (gate)
        {
            if (!Remember(mention.EventId))
            {
                return new SlackPromptAdmissionResult(SlackPromptAdmissionOutcome.Dropped, null);
            }

            if (!AcquireRateBudget(mention.TeamId, mention.ChannelId, mention.UserId))
            {
                return new SlackPromptAdmissionResult(SlackPromptAdmissionOutcome.Busy, mention);
            }
        }

        return accepted.Writer.TryWrite(mention)
            ? new SlackPromptAdmissionResult(SlackPromptAdmissionOutcome.Accepted, null)
            : new SlackPromptAdmissionResult(SlackPromptAdmissionOutcome.Busy, mention);
    }

    internal IAsyncEnumerable<SlackMention> ReadAllAsync(CancellationToken cancellationToken) =>
        accepted.Reader.ReadAllAsync(cancellationToken);

    internal void Complete() => accepted.Writer.TryComplete();

    private bool TryCreateMention(
        JsonElement envelope,
        SlackBotIdentity ownIdentity,
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

    private bool Remember(string eventId)
    {
        if (!eventIds.Add(eventId))
        {
            return false;
        }

        oldestEventIds.Enqueue(eventId);
        if (oldestEventIds.Count > dedupeCapacity)
        {
            eventIds.Remove(oldestEventIds.Dequeue());
        }
        return true;
    }

    private bool AcquireRateBudget(string teamId, string channelId, string userId)
    {
        var principal = $"{teamId}\u001f{channelId}\u001f{userId}";
        var now = timeProvider.GetUtcNow();
        var cutoff = now - RateWindow;
        Prune(globalRequests, cutoff);
        foreach (var stale in principalRequests
                     .Where(item =>
                     {
                         Prune(item.Value, cutoff);
                         return item.Value.Count == 0;
                     })
                     .Select(item => item.Key)
                     .ToArray())
        {
            principalRequests.Remove(stale);
        }

        if (globalRequests.Count >= globalRateLimit)
        {
            return false;
        }

        if (!principalRequests.TryGetValue(principal, out var perUser))
        {
            perUser = new Queue<DateTimeOffset>();
            principalRequests.Add(principal, perUser);
        }
        if (perUser.Count >= perUserRateLimit)
        {
            return false;
        }

        globalRequests.Enqueue(now);
        perUser.Enqueue(now);
        return true;
    }

    private static string NormalizePrompt(string text, string ownUserId)
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

    private static void Prune(Queue<DateTimeOffset> values, DateTimeOffset cutoff)
    {
        while (values.TryPeek(out var value) && value <= cutoff)
        {
            values.Dequeue();
        }
    }
}
