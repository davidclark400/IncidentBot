namespace IncidentBot.Api.Incidents;

public sealed record PagerDutyIncidentSnapshot(
    string Id,
    int IncidentNumber,
    string Title,
    string Status,
    string Urgency,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastStatusChangeAt,
    string ServiceId,
    string ServiceName,
    IReadOnlyList<string> Assignees,
    string? HtmlUrl,
    IReadOnlyDictionary<string, string> Labels);

public sealed record PagerDutyIncidentPage(
    bool HasMore,
    IReadOnlyList<PagerDutyIncidentSnapshot> Incidents);

public sealed record PulledIncidentTrigger(
    Guid IncidentId,
    string IncidentUrl,
    bool Duplicate);

public interface IPagerDutyPullService
{
    Task<PagerDutyIncidentPage> GetRecentAsync(
        DateTimeOffset since,
        DateTimeOffset until,
        CancellationToken cancellationToken);

    Task<PulledIncidentTrigger?> TriggerAsync(
        string pagerDutyIncidentId,
        CancellationToken cancellationToken);
}

public sealed class PagerDutyPullException(string message, int statusCode = StatusCodes.Status502BadGateway)
    : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
