using IncidentBot.Api.Contracts;
using IncidentBot.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;

namespace IncidentBot.Api.Incidents;

public static class IncidentApi
{
    public static IEndpointRouteBuilder MapIncidentApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/incidents/{id:guid}", GetReportAsync)
            .WithName("GetIncidentReport")
            .Produces(StatusCodes.Status304NotModified);
        endpoints.MapGet("/api/incidents/{id:guid}/status", GetStatusAsync).WithName("GetIncidentStatus");
        endpoints.MapGet("/api/incidents/{id:guid}/timeline", GetTimelineAsync).WithName("GetIncidentTimeline");
        endpoints.MapGet("/api/incidents/{id:guid}/evidence", GetEvidenceAsync).WithName("GetIncidentEvidence");
        return endpoints;
    }

    private static async Task<Results<Ok<InvestigationReport>, Accepted<IncidentPending>, NotFound, StatusCodeHttpResult>> GetReportAsync(
        Guid id,
        HttpRequest request,
        HttpResponse response,
        IIncidentReportReader reports,
        CancellationToken cancellationToken)
    {
        var state = await reports.GetAsync(id, cancellationToken);
        if (state is null) return TypedResults.NotFound();
        if (state.Report is null)
        {
            return TypedResults.Accepted((string?)null, state.ToPending());
        }

        var etag = $"\"{state.Version}\"";
        if (request.Headers.IfNoneMatch.Any(value => string.Equals(value, etag, StringComparison.Ordinal)))
        {
            return TypedResults.StatusCode(StatusCodes.Status304NotModified);
        }

        response.Headers.ETag = etag;
        response.Headers.CacheControl = "private, no-cache";
        return TypedResults.Ok(state.Report.ToContract());
    }

    private static async Task<Results<Ok<IncidentStatus>, NotFound>> GetStatusAsync(
        Guid id,
        IIncidentReportReader reports,
        CancellationToken cancellationToken)
    {
        var state = await reports.GetAsync(id, cancellationToken);
        return state is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(state.ToStatus());
    }

    private static async Task<Results<Ok<Page<TimelineEvent>>, NotFound>> GetTimelineAsync(
        Guid id, int? offset, int? limit, IIncidentReportReader reports, CancellationToken cancellationToken)
    {
        var report = (await reports.GetAsync(id, cancellationToken))?.Report;
        if (report is null) return TypedResults.NotFound();
        var safeOffset = Math.Max(0, offset ?? 0);
        var safeLimit = Math.Clamp(limit ?? 100, 1, 500);
        var items = report.Timeline.Skip(safeOffset).Take(safeLimit).Select(item => item.ToContract()).ToArray();
        return TypedResults.Ok(new Page<TimelineEvent>(report.Timeline.Count, items));
    }

    private static async Task<Results<Ok<Page<EvidenceFinding>>, NotFound>> GetEvidenceAsync(
        Guid id, int? offset, int? limit, IIncidentReportReader reports, CancellationToken cancellationToken)
    {
        var report = (await reports.GetAsync(id, cancellationToken))?.Report;
        if (report is null) return TypedResults.NotFound();
        var safeOffset = Math.Max(0, offset ?? 0);
        var safeLimit = Math.Clamp(limit ?? 100, 1, 500);
        var items = report.Evidence.Skip(safeOffset).Take(safeLimit).Select(item => item.ToContract()).ToArray();
        return TypedResults.Ok(new Page<EvidenceFinding>(report.Evidence.Count, items));
    }
}
