using System.Text.Json;
using IncidentBot.Api.Domain;

namespace IncidentBot.Api.Incidents;

public sealed class OutboxWorker(
    IDurableQueue<OutboxItem> queue,
    SlackPublisher slack,
    ILogger<OutboxWorker> logger) :
    DurableWorker<OutboxItem>(queue, TimeSpan.FromMilliseconds(750), logger)
{
    protected override async Task ProcessAsync(OutboxItem item, CancellationToken cancellationToken)
    {
        if (item.Kind == "slack.report")
        {
            using var payload = JsonDocument.Parse(item.Payload);
            var incidentId = payload.RootElement.GetProperty("incidentId").GetGuid();
            await slack.PublishAsync(incidentId, cancellationToken);
        }
    }
}
