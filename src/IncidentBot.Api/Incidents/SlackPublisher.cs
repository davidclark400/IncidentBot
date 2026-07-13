using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using IncidentBot.Api.Domain;
using IncidentBot.Api.Infrastructure;
using IncidentBot.Api.Options;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace IncidentBot.Api.Incidents;

public sealed class SlackPublisher(
    IHttpClientFactory httpClientFactory,
    IIncidentStore repository,
    IOptions<SlackOptions> slackOptions,
    IOptions<IncidentBotOptions> botOptions,
    ICredentialProvider credentials,
    TimeProvider timeProvider,
    ILogger<SlackPublisher> logger)
{
    private static readonly TimeSpan RestartButtonDelay = TimeSpan.FromMinutes(1);

    public async Task PublishAsync(Guid incidentId, CancellationToken cancellationToken)
    {
        if (!slackOptions.Value.Enabled)
        {
            logger.LogDebug("Slack publication skipped because Slack is disabled");
            return;
        }
        var stopwatch = Stopwatch.StartNew();
        var incident = await repository.GetIncidentAsync(incidentId, cancellationToken)
            ?? throw new InvalidOperationException($"Slack incident '{incidentId}' was not found.");
        var report = await repository.GetReportAsync(incidentId, cancellationToken)
            ?? throw new InvalidOperationException($"Slack report '{incidentId}' was not found.");
        var token = credentials.Get(slackOptions.Value.BotTokenEnv);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException($"Slack token environment variable '{slackOptions.Value.BotTokenEnv}' is missing.");
        }

        var critical = EvidenceRankingPolicy.SelectTopSignals(report.Evidence, report.TriggeredAt, 3);
        var sourceStatus = string.Join(" · ", report.Sources.Select(source => $"{Icon(source.Health)} {source.Source}"));
        var reportUrl = $"{botOptions.Value.PublicBaseUrl.TrimEnd('/')}/incidents/{report.Id}";
        var text = $"[{report.Urgency.ToUpperInvariant()}] {report.Title} — {report.DeterministicSummary}";
        var actions = new List<object>();
        if (IncidentProgression.NeedsStuckNotification(report.Status)
            && IncidentProgression.CanRequestRestart(incident.Status) &&
            timeProvider.GetUtcNow() - report.UpdatedAt >= RestartButtonDelay)
        {
            actions.Add(new
            {
                type = "button",
                text = new { type = "plain_text", text = "Restart agent", emoji = true },
                action_id = "restart_agent",
                value = report.Id.ToString()
            });
        }
        actions.Add(new
        {
            type = "button",
            text = new { type = "plain_text", text = "Open live investigation", emoji = true },
            url = reportUrl,
            action_id = "open_incident_report"
        });
        var blocks = new List<object>
        {
            new { type = "header", text = new { type = "plain_text", text = Truncate($"{StateIcon(report.State)} {report.Title}", 150), emoji = true } },
            new
            {
                type = "section",
                fields = new object[]
                {
                    new { type = "mrkdwn", text = $"*Service*\n{Escape(report.ServiceId)}" },
                    new { type = "mrkdwn", text = $"*State*\n{report.State}" },
                    new { type = "mrkdwn", text = $"*Agent*\n{DisplayStatus(incident, report)}" },
                    new { type = "mrkdwn", text = $"*Urgency*\n{Escape(report.Urgency)}" },
                    new { type = "mrkdwn", text = $"*Updated*\n<!date^{report.UpdatedAt.ToUnixTimeSeconds()}^{{time_secs}} {{date_short}}|{report.UpdatedAt:O}>" }
                }
            },
            new { type = "section", text = new { type = "mrkdwn", text = Truncate(Escape(report.Ai.Summary ?? report.DeterministicSummary), 2800) } },
            new { type = "context", elements = new[] { new { type = "mrkdwn", text = Truncate(sourceStatus, 2800) } } },
            new { type = "actions", elements = actions }
        };
        if (report.CausalEvents is { Count: > 0 })
        {
            blocks.Insert(3, new
            {
                type = "section",
                text = new { type = "mrkdwn", text = BuildCausalSequenceText(report.CausalEvents) }
            });
        }
        if (BuildProblemText(report.Problem) is { } problemText)
        {
            blocks.Insert(2, new
            {
                type = "section",
                text = new { type = "mrkdwn", text = problemText }
            });
        }
        if (critical.Count > 0)
        {
            blocks.Insert(3, new
            {
                type = "section",
                text = new { type = "mrkdwn", text = BuildTopSignalsText(critical) }
            });
        }

        var method = incident.SlackTimestamp is null ? "chat.postMessage" : "chat.update";
        object body = incident.SlackTimestamp is null
            ? new { channel = incident.SlackChannel, text, blocks = (object)blocks, unfurl_links = false, unfurl_media = false }
            : new { channel = incident.SlackChannel, ts = incident.SlackTimestamp, text, blocks = (object)blocks, unfurl_links = false, unfurl_media = false };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{slackOptions.Value.ApiBaseUrl.TrimEnd('/')}/{method}")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(slackOptions.Value.TimeoutSeconds));
        logger.LogDebug(
            "Slack publication started for incident {IncidentId} using operation {SlackOperation}",
            incidentId, method);
        HttpResponseMessage response;
        try
        {
            response = await httpClientFactory.CreateClient().SendAsync(request, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Slack publication timed out for incident {IncidentId} during {SlackOperation} after {DurationMilliseconds} ms",
                incidentId, method, stopwatch.ElapsedMilliseconds);
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
            if (incident.SlackTimestamp is null)
            {
                await repository.SetSlackTimestampAsync(incidentId, json.RootElement.GetProperty("ts").GetString()!, cancellationToken);
            }
        }
        logger.LogInformation(
            "Slack publication completed for incident {IncidentId} using operation {SlackOperation} in {DurationMilliseconds} ms",
            incidentId, method, stopwatch.ElapsedMilliseconds);
    }

    private static string Icon(SourceHealth health) => health switch
    {
        SourceHealth.Complete => "✅",
        SourceHealth.Partial => "⚠️",
        SourceHealth.Unavailable => "❌",
        SourceHealth.Excluded => "➖",
        _ => "⏳"
    };
    private static string StateIcon(IncidentState state) => state == IncidentState.Resolved ? "✅" : "🚨";
    private static string DisplayStatus(IncidentRecord incident, InvestigationReport report) =>
        IncidentProgression.DisplayStatus(incident.Status, report.Status);
    internal static string? BuildProblemText(ProblemContext? problem)
    {
        if (problem is null) return null;
        if (problem.Availability == "unavailable") return "*Problem matching unavailable*";
        if (problem.Availability == "provisional") return "*Problem match pending* · provisional fingerprint";
        if (problem.ProblemKey is null || problem.LifecycleState is null) return null;
        var match = problem.MatchScore is { } score ? $"{score}% {problem.MatchType ?? "match"}" : problem.MatchType ?? "new";
        var lastSeen = problem.LastSeen is { } date ? $" · last seen {date:dd MMMM}" : "";
        var explanation = problem.MatchedFeatures.Count > 0
            ? $"\nMatched on {string.Join(", ", problem.MatchedFeatures.Take(3).Select(value => Escape(Truncate(value.Contains(':') ? value[(value.IndexOf(':') + 1)..].Trim() : value, 100))))}"
            : "";
        return $"*{problem.LifecycleState} problem {Escape(problem.ProblemKey)}*\n{match} · {problem.OccurrenceCount} occurrence{(problem.OccurrenceCount == 1 ? "" : "s")}{lastSeen}{explanation}";
    }

    internal static string BuildTopSignalsText(IEnumerable<EvidenceFinding> findings)
    {
        var lines = findings.Take(3)
            .Select(item => $"• {EscapeAndTruncate(item.Summary, 850)}");
        return Truncate($"*Top signals*\n{string.Join("\n", lines)}", 3000);
    }

    internal static string BuildCausalSequenceText(IEnumerable<CausalEvent> causalEvents)
    {
        var lines = causalEvents.Take(5).Select((item, index) =>
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
