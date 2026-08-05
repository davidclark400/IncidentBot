using System.Text.Json;
using Panko.Api.Domain;

namespace Panko.Api.Cases;

public sealed class OutboxWorker(
    IDurableQueue<OutboxItem> queue,
    SlackPublisher slack,
    ILogger<OutboxWorker> logger) :
    DurableWorker<OutboxItem>(queue, TimeSpan.FromMilliseconds(750), logger)
{
    protected override async Task ProcessAsync(OutboxItem item, CancellationToken cancellationToken)
    {
        if (CaseOutboxKinds.IsSlackCaseFile(item.Kind))
        {
            using var payload = JsonDocument.Parse(item.Payload);
            var caseId = payload.RootElement.GetProperty("caseId").GetGuid();
            await slack.PublishAsync(caseId, cancellationToken);
        }
    }
}
