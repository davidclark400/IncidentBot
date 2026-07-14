using System.Security.Cryptography;
using System.Text;
using IncidentBot.Api.Connectors;
using IncidentBot.Api.Domain;
using IncidentBot.Api.Options;
using Microsoft.Extensions.Options;

namespace IncidentBot.Api.Incidents;

public sealed record SlackPromptRequest(string ProfileId, SlackMention Mention);

public sealed record SlackPromptOutcome(string Summary, string PlanYaml);

/// <summary>
/// Turns one reviewed Slack prompt into one bounded, non-persistent investigation result.
/// Profile resolution, query planning, evidence collection, and synthesis are deliberately
/// hidden behind this single interface so callers cannot bypass the reviewed query plan.
/// </summary>
public sealed class SlackPromptInvestigator(
    ISlackQueryProfileProvider profiles,
    ISlackQueryPlanner planner,
    SlackQueryPlanCompiler compiler,
    EvidenceSourceRegistry evidenceSources,
    EvidenceSourceConfiguration sourceConfiguration,
    AdaptiveEvidenceCollector evidenceCollector,
    IInvestigationSynthesizer synthesizer,
    TimeProvider timeProvider,
    ILogger<SlackPromptInvestigator> logger)
{
    internal const int MaximumSummaryCharacters = 1600;

    public async Task<SlackPromptOutcome> InvestigateAsync(
        SlackPromptRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProfileId);
        ArgumentNullException.ThrowIfNull(request.Mention);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Mention.Prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Mention.EventId);

        var reviewed = profiles.Resolve(request.ProfileId);
        var plan = await planner.PlanAsync(
            request.Mention.Prompt,
            reviewed.Profile,
            cancellationToken);
        var compiled = compiler.Compile(plan, reviewed.Profile);
        var mcpSource = compiled.Sources.FirstOrDefault(selection =>
            string.Equals(
                sourceConfiguration.For(selection.Source).Mode,
                "mcp",
                StringComparison.Ordinal));
        if (mcpSource is not null)
        {
            throw new InvalidOperationException(
                $"Slack prompt investigations do not execute MCP source '{mcpSource.Source}'.");
        }

        var now = timeProvider.GetUtcNow();
        var incidentId = DeterministicIncidentId(request.Mention.TeamId, request.Mention.EventId);
        var syntheticPagerDutyId = $"slack:{incidentId:N}";
        var subject = new InvestigationSubject(
            compiled.Profile.PagerDutyServiceId,
            compiled.Question,
            "informational",
            IncidentState.Unknown,
            now);
        var context = new InvestigationContext(
            incidentId,
            syntheticPagerDutyId,
            subject.ServiceId,
            subject.Title,
            subject.Urgency,
            subject.State,
            subject.TriggeredAt,
            compiled.Labels,
            compiled.Profile);

        var selectedConnectors = evidenceSources.Select(compiled.Profile);
        var collection = await evidenceCollector.CollectAsync(
            context,
            reviewed.Revision,
            selectedConnectors,
            cancellationToken);

        AiSynthesis? synthesis = null;
        try
        {
            synthesis = await synthesizer.SynthesizeAsync(
                subject,
                collection.ConnectorResults,
                null,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Slack prompt synthesis failed with {FailureType}; returning the deterministic evidence summary",
                exception.GetType().Name);
        }

        var summary = !string.IsNullOrWhiteSpace(synthesis?.Summary)
            ? Bound(synthesis.Summary, MaximumSummaryCharacters)
            : BuildDeterministicSummary(collection.ConnectorResults, subject.TriggeredAt);
        return new SlackPromptOutcome(summary, compiled.AuditYaml);
    }

    private static string BuildDeterministicSummary(
        IReadOnlyList<ConnectorResult> results,
        DateTimeOffset referenceTime)
    {
        if (results.Count == 0)
        {
            return "The reviewed query plan did not select any data sources.";
        }

        var findings = results.SelectMany(result => result.Findings).ToArray();
        if (findings.Length == 0)
        {
            var available = results.Count(result => result.Health is SourceHealth.Complete or SourceHealth.Partial);
            return Bound(
                $"The query completed against {results.Count} data source(s), with {available} available, but returned no evidence in the investigation window.",
                MaximumSummaryCharacters);
        }

        var topFindings = EvidenceRankingPolicy
            .OrderForSynthesis(findings, referenceTime)
            .Take(3)
            .Select(finding => $"{finding.Source}: {Bound(finding.Summary, 300)}")
            .ToArray();
        var sourceCount = findings.Select(finding => finding.Source).Distinct(StringComparer.Ordinal).Count();
        return Bound(
            $"Collected {findings.Length} evidence item(s) from {sourceCount} data source(s). Top evidence: {string.Join(" | ", topFindings)}",
            MaximumSummaryCharacters);
    }

    private static Guid DeterministicIncidentId(string teamId, string eventId)
    {
        var identity = Encoding.UTF8.GetBytes($"{teamId}\u001f{eventId}");
        var hash = SHA256.HashData(identity);
        return new Guid(hash.AsSpan(0, 16));
    }

    private static string Bound(string value, int maximumCharacters)
    {
        var trimmed = value.Trim();
        if (trimmed.Length <= maximumCharacters)
        {
            return trimmed;
        }

        var retained = maximumCharacters - 1;
        if (retained > 0 && char.IsHighSurrogate(trimmed[retained - 1]))
        {
            retained--;
        }

        return $"{trimmed[..retained]}…";
    }
}

/// <summary>
/// Authorizes a mention by exact channel ID, applies one workflow deadline, and emits exactly
/// one threaded response through the Slack reply seam.
/// </summary>
public sealed class SlackMentionHandler(
    SlackPromptInvestigator investigator,
    ISlackReplyPublisher replies,
    IOptions<SlackOptions> options,
    ILogger<SlackMentionHandler> logger)
{
    internal const int MaximumReplyCharacters = 3900;
    internal const string UnauthorizedReply =
        "This Slack channel is not authorized for prompt investigations. Ask an administrator to map it to an investigation profile.";
    internal const string TimeoutReply =
        "The investigation timed out before it could finish. Narrow the question and try again.";
    internal const string FailureReply =
        "The investigation could not be completed. Try again or contact the IncidentBot operator.";

    public async Task HandleAsync(SlackMention mention, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mention);
        var target = new SlackReplyTarget(
            mention.ChannelId,
            string.IsNullOrWhiteSpace(mention.ThreadTimestamp)
                ? mention.MessageTimestamp
                : mention.ThreadTimestamp);

        string reply;
        var profileId = ResolveExactProfile(options.Value.PromptChannelProfiles, mention.ChannelId);
        if (profileId is null)
        {
            reply = UnauthorizedReply;
        }
        else
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.PromptTimeoutSeconds));
            try
            {
                var outcome = await investigator.InvestigateAsync(
                    new SlackPromptRequest(profileId, mention),
                    timeout.Token);
                reply = FormatSuccess(outcome);
            }
            catch (OperationCanceledException) when (
                timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    "Slack prompt investigation {EventId} timed out after {TimeoutSeconds} seconds",
                    mention.EventId,
                    options.Value.PromptTimeoutSeconds);
                reply = TimeoutReply;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    "Slack prompt investigation {EventId} failed with {FailureType}",
                    mention.EventId,
                    exception.GetType().Name);
                reply = FailureReply;
            }
        }

        await replies.ReplyAsync(target, BoundReply(reply), cancellationToken);
    }

    internal static string? ResolveExactProfile(
        IReadOnlyDictionary<string, string> channelProfiles,
        string channelId)
    {
        foreach (var mapping in channelProfiles)
        {
            if (string.Equals(mapping.Key, channelId, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(mapping.Value))
            {
                return mapping.Value;
            }
        }

        return null;
    }

    internal static string FormatSuccess(SlackPromptOutcome outcome) => BoundReply(
        $"Investigation result\n{outcome.Summary}\n\nQuery plan (YAML)\n{outcome.PlanYaml.TrimEnd()}");

    private static string BoundReply(string value)
    {
        if (value.Length <= MaximumReplyCharacters)
        {
            return value;
        }

        var retained = MaximumReplyCharacters - 1;
        if (char.IsHighSurrogate(value[retained - 1]))
        {
            retained--;
        }

        return $"{value[..retained]}…";
    }
}
