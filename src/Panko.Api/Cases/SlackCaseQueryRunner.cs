using System.Security.Cryptography;
using System.Text;
using Panko.Api.Crumbs;
using Panko.Api.Domain;
using Panko.Api.Options;
using Panko.Api.Security;
using Microsoft.Extensions.Options;

namespace Panko.Api.Cases;

public sealed record SlackPromptRequest(string RecipeId, SlackMention Mention);

public sealed record SlackPromptOutcome(string Summary, string PlanYaml);

/// <summary>
/// Turns one reviewed Slack prompt into one bounded, non-persistent Case query result.
/// Recipe resolution, query planning, Crumb collection, and synthesis are deliberately
/// hidden behind this single interface so callers cannot bypass the reviewed query plan.
/// </summary>
public sealed class SlackCaseQueryRunner(
    ISlackQueryRecipeProvider recipes,
    ISlackQueryPlanner planner,
    SlackQueryPlanCompiler compiler,
    CrumbSourceRegistry crumbSources,
    CrumbSourceConfiguration sourceConfiguration,
    AdaptiveCrumbCollector crumbCollector,
    ICaseFileSynthesizer synthesizer,
    TimeProvider timeProvider,
    ILogger<SlackCaseQueryRunner> logger)
{
    internal const int MaximumSummaryCharacters = 1600;

    public async Task<SlackPromptOutcome> RunAsync(
        SlackPromptRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RecipeId);
        ArgumentNullException.ThrowIfNull(request.Mention);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Mention.Prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Mention.EventId);

        var reviewed = recipes.Resolve(request.RecipeId);
        var plan = await planner.PlanAsync(
            request.Mention.Prompt,
            reviewed.Recipe,
            cancellationToken);
        var compiled = compiler.Compile(plan, reviewed.Recipe);
        var mcpSource = compiled.Sources.FirstOrDefault(selection =>
            string.Equals(
                sourceConfiguration.For(selection.Source).Mode,
                "mcp",
                StringComparison.Ordinal));
        if (mcpSource is not null)
        {
            throw new InvalidOperationException(
                $"Slack Case queries do not execute MCP source '{mcpSource.Source}'.");
        }

        var now = timeProvider.GetUtcNow();
        var caseId = DeterministicCaseId(request.Mention.TeamId, request.Mention.EventId);
        var syntheticPagerDutyIncidentId = $"slack:{caseId:N}";
        var subject = new CaseSubject(
            compiled.Recipe.PagerDutyServiceId,
            compiled.Question,
            "informational",
            PagerDutyIncidentState.Unknown,
            now);
        var context = new CaseContext(
            caseId,
            syntheticPagerDutyIncidentId,
            subject.ServiceId,
            subject.Title,
            subject.Urgency,
            subject.PagerDutyState,
            subject.OpenedAt,
            compiled.Labels,
            compiled.Recipe);

        var selectedCrumbSources = crumbSources.Select(compiled.Recipe);
        var collection = await crumbCollector.CollectAsync(
            context,
            reviewed.Revision,
            selectedCrumbSources,
            cancellationToken);

        AiSynthesis? synthesis = null;
        try
        {
            synthesis = await synthesizer.SynthesizeAsync(
                subject,
                collection.SourceResults,
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
                "Slack prompt synthesis failed with {FailureType}; returning the deterministic Crumb summary",
                exception.GetType().Name);
        }

        var summary = !string.IsNullOrWhiteSpace(synthesis?.Summary)
            ? Bound(synthesis.Summary, MaximumSummaryCharacters)
            : BuildDeterministicSummary(collection.SourceResults, subject.OpenedAt);
        return new SlackPromptOutcome(summary, compiled.AuditYaml);
    }

    private static string BuildDeterministicSummary(
        IReadOnlyList<CrumbSourceResult> results,
        DateTimeOffset referenceTime)
    {
        if (results.Count == 0)
        {
            return "The reviewed query plan did not select any data sources.";
        }

        var crumbs = results.SelectMany(result => result.Crumbs).ToArray();
        if (crumbs.Length == 0)
        {
            var available = results.Count(result => result.Health is CrumbSourceHealth.Complete or CrumbSourceHealth.Partial);
            return Bound(
                $"The query completed against {results.Count} data source(s), with {available} available, but returned no Crumbs in the Case window.",
                MaximumSummaryCharacters);
        }

        var topCrumbs = CrumbRankingPolicy
            .OrderForSynthesis(crumbs, referenceTime)
            .Take(3)
            .Select(crumb => $"{crumb.Source}: {Bound(crumb.Summary, 300)}")
            .ToArray();
        var sourceCount = crumbs.Select(crumb => crumb.Source).Distinct(StringComparer.Ordinal).Count();
        return Bound(
            $"Collected {crumbs.Length} Crumb(s) from {sourceCount} data source(s). Top Crumbs: {string.Join(" | ", topCrumbs)}",
            MaximumSummaryCharacters);
    }

    private static Guid DeterministicCaseId(string teamId, string eventId)
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
    SlackCaseQueryRunner queries,
    ISlackReplyPublisher replies,
    IOptions<SlackOptions> options,
    IRecipeOwnershipCatalog recipes,
    ISecurityAuditTrail audit,
    ILogger<SlackMentionHandler> logger)
{
    internal const int MaximumReplyCharacters = 3900;
    internal const string UnauthorizedReply =
        "This Slack channel is not authorized for Case queries. Ask an administrator to map it to a Recipe.";
    internal const string TimeoutReply =
        "The Case query timed out before it could finish. Narrow the question and try again.";
    internal const string FailureReply =
        "The Case query could not be completed. Try again or contact the Panko operator.";

    public async Task HandleAsync(SlackMention mention, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mention);
        var target = new SlackReplyTarget(
            mention.ChannelId,
            string.IsNullOrWhiteSpace(mention.ThreadTimestamp)
                ? mention.MessageTimestamp
                : mention.ThreadTimestamp);

        var access = SlackChannelAuthorization.ResolvePrompt(
            options.Value,
            recipes,
            mention.ChannelId);
        string reply;
        if (!access.IsAuthorized)
        {
            try
            {
                await audit.RecordAsync(
                    PromptAudit("denied", mention, access),
                    cancellationToken);
                reply = UnauthorizedReply;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    "Slack prompt audit failed for event {EventId} with {FailureType}",
                    mention.EventId,
                    exception.GetType().Name);
                reply = FailureReply;
            }
        }
        else
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.PromptTimeoutSeconds));
            try
            {
                await audit.RecordAsync(
                    PromptAudit("allowed", mention, access),
                    timeout.Token);
                var outcome = await queries.RunAsync(
                    new SlackPromptRequest(access.RecipeId!, mention),
                    timeout.Token);
                reply = FormatSuccess(outcome);
            }
            catch (OperationCanceledException) when (
                timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    "Slack Case query {EventId} timed out after {TimeoutSeconds} seconds",
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
                    "Slack Case query {EventId} failed with {FailureType}",
                    mention.EventId,
                    exception.GetType().Name);
                reply = FailureReply;
            }
        }

        await replies.ReplyAsync(target, BoundReply(reply), cancellationToken);
    }

    internal static string? ResolveExactRecipe(
        IReadOnlyDictionary<string, string> channelRecipes,
        string channelId) => SlackChannelAuthorization.ResolveExact(channelRecipes, channelId);

    internal static string FormatSuccess(SlackPromptOutcome outcome) => BoundReply(
        $"Case query result\n{outcome.Summary}\n\nQuery plan (YAML)\n{outcome.PlanYaml.TrimEnd()}");

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

    private static SecurityAuditEvent PromptAudit(
        string outcome,
        SlackMention mention,
        SlackPromptChannelAccess access) => new(
        SecurityAuditActions.SlackPrompt,
        outcome,
        SecurityAuditActor.Slack(mention.TeamId, mention.UserId, access.Team),
        access.Team,
        access.RecipeId,
        Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["channel_id"] = mention.ChannelId,
            ["slack_event_id"] = mention.EventId
        });
}
