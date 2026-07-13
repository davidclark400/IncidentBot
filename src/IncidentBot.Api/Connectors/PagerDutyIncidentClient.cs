using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using IncidentBot.Api.Domain;
using IncidentBot.Api.Incidents;
using IncidentBot.Api.Infrastructure;
using IncidentBot.Api.Options;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Options;

namespace IncidentBot.Api.Connectors;

public sealed class PagerDutyIncidentClient(
    IHttpClientFactory httpClientFactory,
    IOptions<PagerDutyOptions> options,
    EvidenceSourceConfiguration evidenceSources,
    ICredentialProvider credentials)
{
    private readonly PagerDutyOptions _options = options.Value;
    private readonly ConnectorTransport _transport = evidenceSources.For(EvidenceSourceRegistry.PagerDuty);

    public async Task<PagerDutyIncidentPage> GetRecentAsync(
        DateTimeOffset since,
        DateTimeOffset until,
        CancellationToken cancellationToken)
    {
        var query = new QueryBuilder
        {
            { "since", Iso(since) },
            { "until", Iso(until) },
            { "time_zone", "UTC" },
            { "statuses[]", "triggered" },
            { "statuses[]", "acknowledged" },
            { "statuses[]", "resolved" },
            { "sort_by", "created_at:desc" },
            { "limit", _options.MaximumRecentIncidents.ToString(CultureInfo.InvariantCulture) },
            { "total", "false" }
        };
        using var json = await GetJsonAsync($"incidents{query.ToQueryString()}", false, cancellationToken)
            ?? throw new PagerDutyPullException("PagerDuty did not return an incident list.");
        var root = json.RootElement;
        if (!root.TryGetProperty("incidents", out var incidents) || incidents.ValueKind != JsonValueKind.Array)
        {
            throw new PagerDutyPullException("PagerDuty returned an invalid incident list.");
        }

        var items = incidents.EnumerateArray()
            .Take(_options.MaximumRecentIncidents)
            .Select(ParseIncident)
            .ToArray();
        var hasMore = root.TryGetProperty("more", out var more) && more.ValueKind == JsonValueKind.True;
        return new PagerDutyIncidentPage(hasMore, items);
    }

    public async Task<PagerDutyIncidentSnapshot?> GetAsync(
        string pagerDutyIncidentId,
        CancellationToken cancellationToken)
    {
        var escapedId = Uri.EscapeDataString(pagerDutyIncidentId);
        using var incidentJson = await GetJsonAsync($"incidents/{escapedId}", true, cancellationToken);
        if (incidentJson is null) return null;

        var root = incidentJson.RootElement;
        var incidentElement = root.TryGetProperty("incident", out var wrapped) ? wrapped : root;
        var incident = ParseIncident(incidentElement);

        var query = new QueryBuilder
        {
            { "limit", "100" },
            { "total", "false" }
        };
        using var alertsJson = await GetJsonAsync(
            $"incidents/{escapedId}/alerts{query.ToQueryString()}", false, cancellationToken);
        if (alertsJson is null) return incident;

        var labels = new Dictionary<string, string>(incident.Labels, StringComparer.Ordinal);
        if (alertsJson.RootElement.TryGetProperty("alerts", out var alerts)
            && alerts.ValueKind == JsonValueKind.Array)
        {
            foreach (var alert in alerts.EnumerateArray())
            {
                MergeLabels(alert, labels);
            }
        }

        return incident with { Labels = labels };
    }

    internal static PagerDutyIncidentSnapshot ParseIncident(JsonElement incident)
    {
        var id = RequiredText(incident, "id", 128);
        var service = RequiredObject(incident, "service");
        var serviceId = RequiredText(service, "id", 128);
        var serviceName = OptionalText(service, "summary", 160)
            ?? OptionalText(service, "name", 160)
            ?? serviceId;
        var title = OptionalText(incident, "title", 300)
            ?? OptionalText(incident, "summary", 300)
            ?? "PagerDuty incident";
        var status = OptionalText(incident, "status", 32) ?? "unknown";
        var urgency = OptionalText(incident, "urgency", 32) ?? "unknown";
        var createdAt = RequiredTimestamp(incident, "created_at");
        var lastStatusChangeAt = OptionalTimestamp(incident, "last_status_change_at") ?? createdAt;
        var incidentNumber = incident.TryGetProperty("incident_number", out var number)
            && number.TryGetInt32(out var parsedNumber)
                ? parsedNumber
                : 0;
        var assignees = new HashSet<string>(StringComparer.Ordinal);
        if (incident.TryGetProperty("assignments", out var assignments)
            && assignments.ValueKind == JsonValueKind.Array)
        {
            foreach (var assignment in assignments.EnumerateArray())
            {
                if (!assignment.TryGetProperty("assignee", out var assignee)
                    || assignee.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var name = OptionalText(assignee, "summary", 128) ?? OptionalText(assignee, "name", 128);
                if (name is not null) assignees.Add(name);
            }
        }

        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["service"] = serviceId
        };
        MergeLabels(incident, labels);
        return new PagerDutyIncidentSnapshot(
            id,
            incidentNumber,
            title,
            status,
            urgency,
            createdAt,
            lastStatusChangeAt,
            serviceId,
            serviceName,
            assignees.ToArray(),
            SafeHttpUrl(OptionalText(incident, "html_url", 2048)),
            labels);
    }

    private async Task<JsonDocument?> GetJsonAsync(
        string pathAndQuery,
        bool allowNotFound,
        CancellationToken cancellationToken)
    {
        var token = credentials.Get(_transport.CredentialEnv);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new PagerDutyPullException(
                $"PagerDuty access is not configured; set the {_transport.CredentialEnv} environment variable.",
                StatusCodes.Status503ServiceUnavailable);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.PullTimeoutSeconds));
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{_transport.BaseUrl.TrimEnd('/')}/{pathAndQuery.TrimStart('/')}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Token", $"token={token}");
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.pagerduty+json;version=2");
            using var response = await httpClientFactory.CreateClient().SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound) return null;
            return await ConnectorUtilities.ReadBoundedJsonAsync(
                response,
                _options.MaximumApiResponseBytes,
                timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PagerDutyPullException("PagerDuty did not respond before the request timed out.");
        }
        catch (HttpRequestException exception) when (!cancellationToken.IsCancellationRequested)
        {
            var status = exception.StatusCode is null ? "a network error" : $"HTTP {(int)exception.StatusCode}";
            throw new PagerDutyPullException($"PagerDuty returned {status}.");
        }
        catch (JsonException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PagerDutyPullException("PagerDuty returned invalid JSON.");
        }
        catch (InvalidOperationException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PagerDutyPullException("PagerDuty returned an invalid or oversized response.");
        }
    }

    private static void MergeLabels(JsonElement element, IDictionary<string, string> labels)
    {
        if (element.TryGetProperty("alert_rule", out var alertRule)
            && alertRule.ValueKind == JsonValueKind.Object
            && OptionalText(alertRule, "id", 128) is { } ruleId)
        {
            labels["alert_rule_id"] = ruleId;
        }

        JsonElement details = default;
        if (element.TryGetProperty("custom_details", out var customDetails))
        {
            details = customDetails;
        }
        else if (element.TryGetProperty("body", out var body)
                 && body.ValueKind == JsonValueKind.Object
                 && body.TryGetProperty("details", out var bodyDetails))
        {
            details = bodyDetails;
        }

        if (details.ValueKind != JsonValueKind.Object) return;
        foreach (var property in details.EnumerateObject().Take(32))
        {
            if (property.Value.ValueKind == JsonValueKind.String
                && property.Value.GetString() is { Length: <= 128 } value)
            {
                labels[property.Name] = value;
            }
        }
    }

    private static JsonElement RequiredObject(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : throw new PagerDutyPullException($"PagerDuty incident is missing {name}.");

    private static string RequiredText(JsonElement element, string name, int maximumLength) =>
        OptionalText(element, name, maximumLength)
        ?? throw new PagerDutyPullException($"PagerDuty incident is missing {name}.");

    private static string? OptionalText(JsonElement element, string name, int maximumLength)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text)) return null;
        return text.Length <= maximumLength ? text : text[..maximumLength] + "…";
    }

    private static DateTimeOffset RequiredTimestamp(JsonElement element, string name) =>
        OptionalTimestamp(element, name)
        ?? throw new PagerDutyPullException($"PagerDuty incident is missing {name}.");

    private static DateTimeOffset? OptionalTimestamp(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToUniversalTime()
            : null;

    private static string? SafeHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri.ToString()
            : null;

    private static string Iso(DateTimeOffset value) =>
        value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}
