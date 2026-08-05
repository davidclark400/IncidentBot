using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Panko.Api.Infrastructure;
using Panko.Api.Options;
using Microsoft.Extensions.Options;

namespace Panko.Api.Cases;

public sealed record SlackMention(
    string EventId,
    string TeamId,
    string ChannelId,
    string UserId,
    string MessageTimestamp,
    string? ThreadTimestamp,
    string Prompt);

public readonly record struct SlackReplyTarget(string ChannelId, string ThreadTimestamp);

public interface ISlackReplyPublisher
{
    Task ReplyAsync(SlackReplyTarget target, string text, CancellationToken cancellationToken);
}

public sealed class SlackReplyPublisher(
    IHttpClientFactory httpClientFactory,
    IOptions<SlackOptions> options,
    ICredentialProvider credentials) : ISlackReplyPublisher
{
    internal const int MaximumReplyCharacters = 3900;

    public async Task ReplyAsync(
        SlackReplyTarget target,
        string text,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target.ChannelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(target.ThreadTimestamp);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var token = credentials.Get(options.Value.BotTokenEnv);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                $"Slack token environment variable '{options.Value.BotTokenEnv}' is missing.");
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{options.Value.ApiBaseUrl.TrimEnd('/')}/chat.postMessage")
        {
            Content = JsonContent.Create(new
            {
                channel = target.ChannelId,
                thread_ts = target.ThreadTimestamp,
                text = EscapeAndTruncate(text, MaximumReplyCharacters),
                mrkdwn = false,
                parse = "none",
                reply_broadcast = false,
                unfurl_links = false,
                unfurl_media = false
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

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
                var error = root.TryGetProperty("error", out var errorElement) &&
                            errorElement.ValueKind == JsonValueKind.String
                    ? errorElement.GetString()
                    : "unknown_error";
                throw new InvalidOperationException($"Slack chat.postMessage failed: {error}");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Slack chat.postMessage timed out after {options.Value.TimeoutSeconds} seconds.");
        }
    }

    internal static string EscapeAndTruncate(string value, int maximumCharacters)
    {
        if (maximumCharacters <= 0 || value.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(value.Length, maximumCharacters));
        foreach (var rune in value.EnumerateRunes())
        {
            var escaped = rune.Value switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                _ => rune.ToString()
            };
            if (builder.Length + escaped.Length > maximumCharacters)
            {
                if (builder.Length < maximumCharacters)
                {
                    builder.Append('…');
                }
                break;
            }

            builder.Append(escaped);
        }

        return builder.ToString();
    }
}

internal static class SlackWebApiJson
{
    internal const int MaximumResponseBytes = 65_536;

    internal static async Task<JsonDocument> ReadAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var responseStream = await content.ReadAsStreamAsync(cancellationToken);
        using var bounded = new MemoryStream(capacity: 8192);
        var buffer = new byte[8192];
        while (true)
        {
            var count = await responseStream.ReadAsync(buffer, cancellationToken);
            if (count == 0)
            {
                break;
            }

            if (bounded.Length + count > MaximumResponseBytes)
            {
                throw new InvalidDataException(
                    $"Slack response exceeded {MaximumResponseBytes} bytes.");
            }

            bounded.Write(buffer, 0, count);
        }

        return JsonDocument.Parse(
            bounded.GetBuffer().AsMemory(0, checked((int)bounded.Length)),
            new JsonDocumentOptions { MaxDepth = 16 });
    }
}
