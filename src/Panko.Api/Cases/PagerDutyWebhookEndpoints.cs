using System.Text.Json;
using Panko.Api.Domain;
using Panko.Api.Security;
using Panko.Api.Options;
using Microsoft.Extensions.Options;

namespace Panko.Api.Cases;

public static class PagerDutyWebhookEndpoints
{
    public static IEndpointRouteBuilder MapPagerDutyWebhook(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/webhooks/pagerduty/v3", HandleAsync)
            .DisableAntiforgery();
        return endpoints;
    }

    private static async Task<IResult> HandleAsync(
        HttpRequest request,
        PagerDutySignatureValidator validator,
        IOptions<PagerDutyOptions> pagerDutyOptions,
        PagerDutyCaseAdapter adapter,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("PagerDutyWebhook");
        var maximumPayloadBytes = pagerDutyOptions.Value.MaximumWebhookPayloadBytes;
        if (request.ContentLength > maximumPayloadBytes)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var payload = await ReadBoundedPayloadAsync(request.Body, maximumPayloadBytes, cancellationToken);
        if (payload is null)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }
        var signature = request.Headers["X-PagerDuty-Signature"].FirstOrDefault();
        if (!validator.Validate(payload, signature))
        {
            logger.LogWarning(
                "PagerDuty webhook rejected because signature validation failed; payload size was {PayloadBytes} bytes",
                payload.Length);
            return Results.Unauthorized();
        }

        PagerDutyWebhookEvent webhook;
        try
        {
            webhook = Parse(payload);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            logger.LogWarning(
                "PagerDuty webhook payload shape was invalid ({FailureType}, {PayloadBytes} bytes): {Diagnostic}",
                exception.GetType().Name, payload.Length,
                exception.Message.Length <= 300 ? exception.Message : exception.Message[..300] + "…");
            return Results.BadRequest(new { error = exception.Message });
        }

        var accepted = await adapter.AcceptAsync(webhook, payload, cancellationToken);
        logger.LogInformation(
            "PagerDuty webhook {WebhookEventId} accepted for Case {CaseId} from PagerDuty incident {PagerDutyIncidentId} and service {ServiceId}; duplicate: {IsDuplicate}",
            webhook.EventId, accepted.CaseId, webhook.PagerDutyIncidentId, webhook.ServiceId, accepted.IsDuplicate);
        return Results.Accepted($"/api/cases/{accepted.CaseId}", new
        {
            caseId = accepted.CaseId,
            duplicate = accepted.IsDuplicate
        });
    }

    internal static PagerDutyWebhookEvent Parse(ReadOnlyMemory<byte> payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var eventElement = root.TryGetProperty("event", out var wrapped) ? wrapped : root;
        var data = eventElement.GetProperty("data");
        var service = data.TryGetProperty("service", out var serviceElement) ? serviceElement : default;

        var eventId = RequiredString(eventElement, "id");
        var eventType = RequiredString(eventElement, "event_type");
        var pagerDutyIncidentId = RequiredString(data, "id");
        var serviceId = service.ValueKind == JsonValueKind.Object
            ? RequiredString(service, "id")
            : RequiredString(data, "service_id");
        var title = OptionalString(data, "title") ?? OptionalString(data, "summary") ?? "PagerDuty incident";
        var urgency = OptionalString(data, "urgency") ?? "unknown";
        var htmlUrl = OptionalString(data, "html_url");
        var occurredAt = DateTimeOffset.Parse(RequiredString(eventElement, "occurred_at"));
        var triggeredAt = OptionalTimestamp(data, "created_at") ?? occurredAt;
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["service"] = serviceId
        };

        if (data.TryGetProperty("alert_rule", out var alertRule) && alertRule.ValueKind == JsonValueKind.Object)
        {
            var ruleId = OptionalString(alertRule, "id");
            if (ruleId is not null) labels["alert_rule_id"] = ruleId;
        }

        if (data.TryGetProperty("custom_details", out var details) && details.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in details.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { Length: <= 128 } value)
                {
                    labels[property.Name] = value;
                }
            }
        }

        return new PagerDutyWebhookEvent(
            eventId, eventType, pagerDutyIncidentId, serviceId, title, urgency, htmlUrl, triggeredAt, occurredAt, labels);
    }

    internal static async Task<byte[]?> ReadBoundedPayloadAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var buffer = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
        var chunk = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(chunk, cancellationToken);
            if (count == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + count > maximumBytes)
            {
                return null;
            }

            await buffer.WriteAsync(chunk.AsMemory(0, count), cancellationToken);
        }
    }

    private static string RequiredString(JsonElement element, string name) =>
        OptionalString(element, name) ?? throw new InvalidOperationException($"PagerDuty payload is missing '{name}'.");

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? OptionalTimestamp(JsonElement element, string name) =>
        OptionalString(element, name) is { } value
        && DateTimeOffset.TryParse(value, out var timestamp)
            ? timestamp.ToUniversalTime()
            : null;
}
