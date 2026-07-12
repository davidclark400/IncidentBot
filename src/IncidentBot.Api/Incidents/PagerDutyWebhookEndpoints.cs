using System.Text.Json;
using IncidentBot.Api.Domain;
using IncidentBot.Api.Profiles;
using IncidentBot.Api.Security;
using IncidentBot.Api.Options;
using Microsoft.Extensions.Options;

namespace IncidentBot.Api.Incidents;

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
        InvestigationProfileStore profiles,
        IIncidentStore repository,
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

        var profile = profiles.Resolve(webhook.ServiceId, webhook.Labels);
        webhook = webhook with { Labels = profiles.FilterPersistedLabels(profile, webhook.Labels) };
        var accepted = await repository.AcceptWebhookAsync(webhook, profile, payload, cancellationToken);
        logger.LogInformation(
            "PagerDuty webhook {WebhookEventId} accepted for incident {IncidentId} and service {ServiceId}; duplicate: {IsDuplicate}",
            webhook.EventId, accepted.IncidentId, webhook.ServiceId, accepted.IsDuplicate);
        return Results.Accepted($"/api/incidents/{accepted.IncidentId}", new
        {
            incidentId = accepted.IncidentId,
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
        var incidentId = RequiredString(data, "id");
        var serviceId = service.ValueKind == JsonValueKind.Object
            ? RequiredString(service, "id")
            : RequiredString(data, "service_id");
        var title = OptionalString(data, "title") ?? OptionalString(data, "summary") ?? "PagerDuty incident";
        var urgency = OptionalString(data, "urgency") ?? "unknown";
        var htmlUrl = OptionalString(data, "html_url");
        var occurredAt = DateTimeOffset.Parse(RequiredString(eventElement, "occurred_at"));
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
            eventId, eventType, incidentId, serviceId, title, urgency, htmlUrl, occurredAt, labels);
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
}
