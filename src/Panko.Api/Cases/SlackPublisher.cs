using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Panko.Api.Domain;
using Panko.Api.Infrastructure;
using Panko.Api.Options;
using Panko.Api.Security;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace Panko.Api.Cases;

public sealed class SlackPublisher(
    IHttpClientFactory httpClientFactory,
    ICaseStore repository,
    IOptions<SlackOptions> slackOptions,
    IOptions<PankoOptions> pankoOptions,
    ICredentialProvider credentials,
    ISecurityAuditTrail audit,
    TimeProvider timeProvider,
    ILogger<SlackPublisher> logger)
{
    private static readonly TimeSpan RebuildButtonDelay = TimeSpan.FromMinutes(1);

    public async Task PublishAsync(Guid caseId, CancellationToken cancellationToken)
    {
        if (!slackOptions.Value.Enabled)
        {
            logger.LogDebug("Slack publication skipped because Slack is disabled");
            return;
        }
        var stopwatch = Stopwatch.StartNew();
        var caseRecord = await repository.GetCaseAsync(caseId, cancellationToken);
        if (caseRecord is null)
        {
            await audit.RecordAsync(
                AuditEvent("not_found", caseId, null, null, null),
                cancellationToken);
            throw new InvalidOperationException($"Slack Case '{caseId}' was not found.");
        }
        var channelTeam = string.IsNullOrWhiteSpace(caseRecord.SlackChannel)
            ? null
            : SlackChannelAuthorization.ResolveTeam(slackOptions.Value, caseRecord.SlackChannel);
        if (!TeamKey.IsCanonical(caseRecord.Team)
            || !string.Equals(channelTeam, caseRecord.Team, StringComparison.Ordinal))
        {
            await audit.RecordAsync(
                AuditEvent(
                    "denied",
                    caseRecord.Id,
                    caseRecord.Team,
                    caseRecord.RecipeId,
                    caseRecord.SlackChannel),
                cancellationToken);
            throw new InvalidOperationException(
                $"Slack channel is not authorized for Case '{caseId}'.");
        }
        await audit.RecordAsync(
            AuditEvent(
                "allowed",
                caseRecord.Id,
                caseRecord.Team,
                caseRecord.RecipeId,
                caseRecord.SlackChannel),
            cancellationToken);
        var caseFile = await repository.GetCaseFileAsync(caseId, cancellationToken)
            ?? throw new InvalidOperationException($"Slack Case File '{caseId}' was not found.");
        var token = credentials.Get(slackOptions.Value.BotTokenEnv);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException($"Slack token environment variable '{slackOptions.Value.BotTokenEnv}' is missing.");
        }

        var topCrumbs = CrumbRankingPolicy.SelectTopCrumbs(caseFile.Crumbs, caseFile.OpenedAt, 3);
        var sourceStatus = string.Join(" · ", caseFile.CrumbSources.Select(source => $"{Icon(source.Health)} {source.Source}"));
        var caseUrl = $"{pankoOptions.Value.PublicBaseUrl.TrimEnd('/')}/cases/{caseFile.CaseId}";
        var text = $"[{caseFile.Urgency.ToUpperInvariant()}] {caseFile.Title} — {caseFile.DeterministicSummary}";
        var actions = new List<object>();
        if (CaseProgression.NeedsStuckNotification(caseFile.Status)
            && CaseProgression.CanRequestRebuild(caseRecord.Status) &&
            timeProvider.GetUtcNow() - caseFile.UpdatedAt >= RebuildButtonDelay)
        {
            actions.Add(new
            {
                type = "button",
                text = new { type = "plain_text", text = "Rebuild Case File", emoji = true },
                action_id = SlackInteractiveHandler.RebuildCaseActionId,
                value = caseFile.CaseId.ToString()
            });
        }
        actions.Add(new
        {
            type = "button",
            text = new { type = "plain_text", text = "Open Case File", emoji = true },
            url = caseUrl,
            action_id = "open_case_file"
        });
        var blocks = new List<object>
        {
            new { type = "header", text = new { type = "plain_text", text = Truncate($"{StateIcon(caseFile.PagerDutyState)} {caseFile.Title}", 150), emoji = true } },
            new
            {
                type = "section",
                fields = new object[]
                {
                    new { type = "mrkdwn", text = $"*Service*\n{Escape(caseFile.ServiceId)}" },
                    new { type = "mrkdwn", text = $"*State*\n{caseFile.PagerDutyState}" },
                    new { type = "mrkdwn", text = $"*Agent*\n{DisplayStatus(caseRecord, caseFile)}" },
                    new { type = "mrkdwn", text = $"*Urgency*\n{Escape(caseFile.Urgency)}" },
                    new { type = "mrkdwn", text = $"*Updated*\n<!date^{caseFile.UpdatedAt.ToUnixTimeSeconds()}^{{time_secs}} {{date_short}}|{caseFile.UpdatedAt:O}>" }
                }
            },
            new { type = "section", text = new { type = "mrkdwn", text = Truncate(Escape(caseFile.Ai.Summary ?? caseFile.DeterministicSummary), 2800) } },
            new { type = "context", elements = new[] { new { type = "mrkdwn", text = Truncate(sourceStatus, 2800) } } },
            new { type = "actions", elements = actions }
        };
        if (caseFile.CausalMarkers is { Count: > 0 })
        {
            blocks.Insert(3, new
            {
                type = "section",
                text = new { type = "mrkdwn", text = BuildCausalSequenceText(caseFile.CausalMarkers) }
            });
        }
        if (BuildPatternText(caseFile.Pattern) is { } patternText)
        {
            blocks.Insert(2, new
            {
                type = "section",
                text = new { type = "mrkdwn", text = patternText }
            });
        }
        if (topCrumbs.Count > 0)
        {
            blocks.Insert(3, new
            {
                type = "section",
                text = new { type = "mrkdwn", text = BuildTopCrumbsText(topCrumbs) }
            });
        }

        var method = caseRecord.SlackTimestamp is null ? "chat.postMessage" : "chat.update";
        object body = caseRecord.SlackTimestamp is null
            ? new { channel = caseRecord.SlackChannel, text, blocks = (object)blocks, unfurl_links = false, unfurl_media = false }
            : new { channel = caseRecord.SlackChannel, ts = caseRecord.SlackTimestamp, text, blocks = (object)blocks, unfurl_links = false, unfurl_media = false };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{slackOptions.Value.ApiBaseUrl.TrimEnd('/')}/{method}")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(slackOptions.Value.TimeoutSeconds));
        logger.LogDebug(
            "Slack Case File publication started for Case {CaseId} using operation {SlackOperation}",
            caseId, method);
        HttpResponseMessage response;
        try
        {
            response = await httpClientFactory.CreateClient().SendAsync(request, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Slack Case File publication timed out for Case {CaseId} during {SlackOperation} after {DurationMilliseconds} ms",
                caseId, method, stopwatch.ElapsedMilliseconds);
            throw new TimeoutException($"Slack {method} timed out after {slackOptions.Value.TimeoutSeconds} seconds.");
        }
        using (response)
        {
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            if (!json.RootElement.GetProperty("ok").GetBoolean())
            {
                throw new InvalidOperationException($"Slack {method} failed: {json.RootElement.GetProperty("error").GetString()}");
            }
            if (caseRecord.SlackTimestamp is null)
            {
                await repository.SetSlackTimestampAsync(caseId, json.RootElement.GetProperty("ts").GetString()!, cancellationToken);
            }
        }
        logger.LogInformation(
            "Slack Case File publication completed for Case {CaseId} using operation {SlackOperation} in {DurationMilliseconds} ms",
            caseId, method, stopwatch.ElapsedMilliseconds);
    }

    private static SecurityAuditEvent AuditEvent(
        string outcome,
        Guid caseId,
        string? team,
        string? recipeId,
        string? channelId) => new(
        SecurityAuditActions.CaseFileAccess,
        outcome,
        new SecurityAuditActor(
            "panko:slack-publisher",
            "system",
            TeamKey.IsCanonical(team) ? [team!] : []),
        team,
        recipeId,
        caseId,
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["surface"] = "slack-publication",
            ["channel_id"] = channelId ?? "unknown"
        });

    private static string Icon(CrumbSourceHealth health) => health switch
    {
        CrumbSourceHealth.Complete => "✅",
        CrumbSourceHealth.Partial => "⚠️",
        CrumbSourceHealth.Unavailable => "❌",
        CrumbSourceHealth.Excluded => "➖",
        _ => "⏳"
    };
    private static string StateIcon(PagerDutyIncidentState state) => state == PagerDutyIncidentState.Resolved ? "✅" : "🚨";
    private static string DisplayStatus(CaseRecord caseRecord, CaseFile caseFile) =>
        CaseProgression.DisplayStatus(caseRecord.Status, caseFile.Status);
    internal static string? BuildPatternText(PatternContext? pattern)
    {
        if (pattern is null) return null;
        if (pattern.Availability == "unavailable") return "*Pattern matching unavailable*";
        if (pattern.Availability == "provisional") return "*Pattern match pending* · provisional signature";
        if (pattern.PatternKey is null || pattern.LifecycleState is null) return null;
        var match = pattern.MatchScore is { } score ? $"{score}% {pattern.MatchType ?? "match"}" : pattern.MatchType ?? "new";
        var lastSeen = pattern.LastSeen is { } date ? $" · last seen {date:dd MMMM}" : "";
        var explanation = pattern.MatchedFeatures.Count > 0
            ? $"\nMatched on {string.Join(", ", pattern.MatchedFeatures.Take(3).Select(value => Escape(Truncate(value.Contains(':') ? value[(value.IndexOf(':') + 1)..].Trim() : value, 100))))}"
            : "";
        return $"*{pattern.LifecycleState} Pattern {Escape(pattern.PatternKey)}*\n{match} · {pattern.OccurrenceCount} occurrence{(pattern.OccurrenceCount == 1 ? "" : "s")}{lastSeen}{explanation}";
    }

    internal static string BuildTopCrumbsText(IEnumerable<Crumb> crumbs)
    {
        var lines = crumbs.Take(3)
            .Select(item => $"• {EscapeAndTruncate(item.Summary, 850)}");
        return Truncate($"*Top Crumbs*\n{string.Join("\n", lines)}", 3000);
    }

    internal static string BuildCausalSequenceText(IEnumerable<CausalMarker> causalMarkers)
    {
        var lines = causalMarkers.Take(5).Select((item, index) =>
            $"{index + 1}. *{EscapeAndTruncate(item.Label ?? item.Category, 120)}* — {EscapeAndTruncate(item.Summary, 380)}");
        return Truncate($"*Candidate sequence*\n{string.Join("\n", lines)}", 3000);
    }

    private static string Escape(string value) => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string EscapeAndTruncate(string value, int max)
    {
        if (max <= 0 || value.Length == 0) return "";
        var builder = new StringBuilder(Math.Min(value.Length, max));
        foreach (var character in value)
        {
            var escaped = character switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                _ => character.ToString()
            };
            if (builder.Length + escaped.Length > max)
            {
                if (builder.Length < max) builder.Append('…');
                break;
            }
            builder.Append(escaped);
        }
        return builder.ToString();
    }

    private static string Truncate(string value, int max)
    {
        if (max <= 0) return "";
        if (value.Length <= max) return value;
        return max == 1 ? "…" : value[..(max - 1)] + "…";
    }
}
