using System.Text;
using IncidentBot.Api.Incidents;

namespace IncidentBot.Api.Tests;

public sealed class PagerDutyWebhookTests
{
    [Fact]
    public void ParsesV3EnvelopeAndBoundedCustomDetails()
    {
        var json = """
            {
              "event": {
                "id": "evt-1",
                "event_type": "incident.triggered",
                "occurred_at": "2026-07-11T10:00:00Z",
                "data": {
                  "id": "PINCIDENT",
                  "type": "incident",
                  "title": "Payments are failing",
                  "urgency": "high",
                  "html_url": "https://pagerduty.example/incidents/PINCIDENT",
                  "created_at": "2026-07-11T09:45:00Z",
                  "service": { "id": "P123PAYMENTS" },
                  "alert_rule": { "id": "rule-5" },
                  "custom_details": { "environment": "production", "cluster": "eu-west" }
                }
              }
            }
            """;

        var parsed = PagerDutyWebhookEndpoints.Parse(Encoding.UTF8.GetBytes(json));

        Assert.Equal("evt-1", parsed.EventId);
        Assert.Equal("PINCIDENT", parsed.PagerDutyIncidentId);
        Assert.Equal("P123PAYMENTS", parsed.ServiceId);
        Assert.Equal(DateTimeOffset.Parse("2026-07-11T09:45:00Z"), parsed.TriggeredAt);
        Assert.Equal(DateTimeOffset.Parse("2026-07-11T10:00:00Z"), parsed.OccurredAt);
        Assert.Equal("production", parsed.Labels["environment"]);
        Assert.Equal("rule-5", parsed.Labels["alert_rule_id"]);
    }

    [Fact]
    public async Task WebhookPayloadReaderRejectsChunkedBodiesAboveTheConfiguredLimit()
    {
        var acceptedPayload = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var accepted = await PagerDutyWebhookEndpoints.ReadBoundedPayloadAsync(
            new MemoryStream(acceptedPayload), 32, CancellationToken.None);
        var rejected = await PagerDutyWebhookEndpoints.ReadBoundedPayloadAsync(
            new MemoryStream(new byte[33]), 32, CancellationToken.None);

        Assert.Equal(acceptedPayload, accepted);
        Assert.Null(rejected);
    }
}
