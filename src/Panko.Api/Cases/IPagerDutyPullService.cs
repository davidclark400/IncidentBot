using Panko.Api.Security;

namespace Panko.Api.Cases;

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

public sealed record PulledCaseTrigger(
    Guid CaseId,
    string CaseUrl,
    bool Duplicate);

public interface IPagerDutyPullService
{
    Task<PagerDutyIncidentPage> GetRecentAsync(
        DateTimeOffset since,
        DateTimeOffset until,
        IReadOnlyList<string> authorizedServiceIds,
        CancellationToken cancellationToken);

    Task<PulledCaseTrigger?> TriggerAsync(
        string pagerDutyIncidentId,
        TeamAccessScope accessScope,
        CancellationToken cancellationToken);
}

public sealed class PagerDutyPullException(string message, int statusCode = StatusCodes.Status502BadGateway)
    : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
