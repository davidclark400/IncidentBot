using IncidentBot.Api.Options;
using IncidentBot.Contracts;
using Microsoft.Extensions.Options;

namespace IncidentBot.Api.Incidents;

public static class PagerDutyPullApi
{
    public static IEndpointRouteBuilder MapPagerDutyPullApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/pagerduty/incidents", GetRecentAsync)
            .WithName("GetRecentPagerDutyIncidents")
            .Produces<RecentPagerDutyIncidents>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        endpoints.MapPost("/api/pagerduty/incidents/{id}/trigger", TriggerAsync)
            .WithName("TriggerPagerDutyIncident")
            .Produces<IncidentTriggerResult>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return endpoints;
    }

    private static async Task<IResult> GetRecentAsync(
        DateTimeOffset? since,
        DateTimeOffset? until,
        IPagerDutyPullService pagerDuty,
        IOptions<PagerDutyOptions> options,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var effectiveUntil = (until ?? now).ToUniversalTime();
        var effectiveSince = (since ?? effectiveUntil - TimeSpan.FromHours(24)).ToUniversalTime();
        if (effectiveSince >= effectiveUntil)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid PagerDuty time frame",
                detail: "The start of the time frame must be before the end.");
        }
        if (effectiveUntil - effectiveSince > TimeSpan.FromDays(options.Value.MaximumLookbackDays))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "PagerDuty time frame is too large",
                detail: $"Choose a time frame of {options.Value.MaximumLookbackDays} days or less.");
        }

        try
        {
            var page = await pagerDuty.GetRecentAsync(effectiveSince, effectiveUntil, cancellationToken);
            return Results.Ok(new RecentPagerDutyIncidents(
                effectiveSince,
                effectiveUntil,
                page.HasMore,
                page.Incidents.Select(ToContract).ToArray()));
        }
        catch (PagerDutyPullException exception)
        {
            loggerFactory.CreateLogger("PagerDutyPullApi").LogWarning(
                "PagerDuty recent-incident query failed with {FailureType}",
                exception.GetType().Name);
            return Results.Problem(
                statusCode: exception.StatusCode,
                title: "PagerDuty request failed",
                detail: exception.Message);
        }
    }

    private static async Task<IResult> TriggerAsync(
        string id,
        IPagerDutyPullService pagerDuty,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await pagerDuty.TriggerAsync(id, cancellationToken);
            if (result is null)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: "PagerDuty incident not found",
                    detail: "The selected incident no longer exists or is not accessible.");
            }

            return Results.Accepted(
                result.IncidentUrl,
                new IncidentTriggerResult(result.IncidentId, result.IncidentUrl, result.Duplicate));
        }
        catch (PagerDutyPullException exception)
        {
            loggerFactory.CreateLogger("PagerDutyPullApi").LogWarning(
                "PagerDuty incident trigger failed with {FailureType}",
                exception.GetType().Name);
            return Results.Problem(
                statusCode: exception.StatusCode,
                title: "Unable to start investigation",
                detail: exception.Message);
        }
    }

    private static RecentPagerDutyIncident ToContract(PagerDutyIncidentSnapshot incident) => new(
        incident.Id,
        incident.IncidentNumber,
        incident.Title,
        incident.Status,
        incident.Urgency,
        incident.CreatedAt,
        incident.LastStatusChangeAt,
        incident.ServiceId,
        incident.ServiceName,
        incident.Assignees,
        incident.HtmlUrl);
}
