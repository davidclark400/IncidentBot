using System.Text.Json;

namespace IncidentBot.Api.Incidents;

/// <summary>
/// Owns the interactive restart payload after the Socket Mode adapter has acknowledged it.
/// </summary>
public sealed class SlackInteractiveHandler(
    InvestigationRestartService restart,
    SlackPublisher slack,
    ILogger<SlackInteractiveHandler> logger)
{
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
