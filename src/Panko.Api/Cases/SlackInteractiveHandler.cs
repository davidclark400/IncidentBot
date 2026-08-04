using System.Text.Json;

namespace Panko.Api.Cases;

/// <summary>
/// Owns the interactive Case rebuild payload after the Socket Mode adapter has acknowledged it.
/// </summary>
public sealed class SlackInteractiveHandler(
    CaseRebuildService rebuild,
    SlackPublisher slack,
    ILogger<SlackInteractiveHandler> logger)
{
    internal const string RebuildCaseActionId = "rebuild_case";

    public async Task HandleAsync(string payloadJson, CancellationToken cancellationToken)
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
            var actionId = action.GetProperty("action_id").GetString();
            if (actionId != RebuildCaseActionId ||
                !Guid.TryParse(action.GetProperty("value").GetString(), out var caseId))
            {
                return;
            }

            var workspaceId = TryGetString(payload, "team", "id");
            var userId = TryGetString(payload, "user", "id");
            var channelId = TryGetString(payload, "container", "channel_id");
            var messageTimestamp = TryGetString(payload, "container", "message_ts");
            if (string.IsNullOrWhiteSpace(workspaceId) ||
                string.IsNullOrWhiteSpace(userId) ||
                string.IsNullOrWhiteSpace(channelId) ||
                string.IsNullOrWhiteSpace(messageTimestamp))
            {
                logger.LogWarning(
                    "Ignored Slack Case rebuild action for Case {CaseId} because its authenticated workspace, user, channel, or message identity was incomplete",
                    caseId);
                return;
            }

            var request = new SlackRebuildRequest(
                caseId,
                workspaceId,
                userId,
                channelId,
                messageTimestamp);
            if (!await rebuild.RebuildAsync(request, cancellationToken))
            {
                logger.LogWarning(
                    "Ignored Slack Case rebuild action for Case {CaseId} because the message no longer matches a Case",
                    caseId);
                return;
            }

            try
            {
                await slack.PublishAsync(caseId, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(
                    exception,
                    "Case rebuild was queued for Case {CaseId}, but Slack could not be updated immediately",
                    caseId);
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

    internal static string? TryGetString(
        JsonElement element,
        string parent,
        string property)
    {
        return element.TryGetProperty(parent, out var parentElement) &&
               parentElement.TryGetProperty(property, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
