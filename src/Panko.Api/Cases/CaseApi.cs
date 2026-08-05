using Panko.Api.Contracts;
using Panko.Api.Security;
using Panko.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Panko.Api.Cases;

public static class CaseApi
{
    public static IEndpointRouteBuilder MapCaseApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/cases/{id:guid}", GetCaseFileAsync)
            .WithName("GetCaseFile")
            .WithTags("Cases")
            .Produces(StatusCodes.Status304NotModified)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);
        endpoints.MapGet("/api/cases/{id:guid}/status", GetStatusAsync)
            .WithName("GetCaseStatus")
            .WithTags("Cases")
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);
        endpoints.MapGet("/api/cases/{id:guid}/trail", GetTrailAsync)
            .WithName("GetCaseTrail")
            .WithTags("Cases")
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);
        endpoints.MapGet("/api/cases/{id:guid}/crumbs", GetCrumbsAsync)
            .WithName("GetCaseCrumbs")
            .WithTags("Cases")
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        return endpoints;
    }

    private static async Task<Results<Ok<CaseFile>, Accepted<CasePending>, NotFound, StatusCodeHttpResult>> GetCaseFileAsync(
        Guid id,
        HttpRequest request,
        HttpResponse response,
        ICaseAccessAuthorizer authorization,
        CancellationToken cancellationToken)
    {
        var grant = await authorization.AuthorizeAsync(
            request.HttpContext.User, id, CaseAccessKind.CaseFile, cancellationToken);
        if (grant is null) return TypedResults.NotFound();
        var state = grant.State;
        if (state.CaseFile is null)
        {
            return TypedResults.Accepted((string?)null, state.ToPending());
        }

        var etag = $"\"{state.CaseFileVersion}\"";
        if (request.Headers.IfNoneMatch.Any(value => string.Equals(value, etag, StringComparison.Ordinal)))
        {
            return TypedResults.StatusCode(StatusCodes.Status304NotModified);
        }

        response.Headers.ETag = etag;
        response.Headers.CacheControl = "private, no-cache";
        return TypedResults.Ok(state.CaseFile.ToContract());
    }

    private static async Task<Results<Ok<CaseStatus>, NotFound, StatusCodeHttpResult>> GetStatusAsync(
        Guid id,
        HttpContext context,
        ICaseProgressReader progress,
        ICaseAccessAuthorizer authorization,
        CancellationToken cancellationToken)
    {
        var grant = await authorization.AuthorizeAsync(
            context.User, id, CaseAccessKind.Status, cancellationToken);
        return grant is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(grant.State.ToStatus(await progress.GetProgressAsync(id, cancellationToken)));
    }

    private static async Task<Results<Ok<Page<TrailEntry>>, NotFound, StatusCodeHttpResult>> GetTrailAsync(
        Guid id,
        int? offset,
        int? limit,
        HttpContext context,
        ICaseAccessAuthorizer authorization,
        CancellationToken cancellationToken)
    {
        var grant = await authorization.AuthorizeAsync(
            context.User, id, CaseAccessKind.Trail, cancellationToken);
        if (grant?.State.CaseFile is not { } caseFile) return TypedResults.NotFound();
        var safeOffset = Math.Max(0, offset ?? 0);
        var safeLimit = Math.Clamp(limit ?? 100, 1, 500);
        var items = caseFile.Trail.Skip(safeOffset).Take(safeLimit).Select(item => item.ToContract()).ToArray();
        return TypedResults.Ok(new Page<TrailEntry>(caseFile.Trail.Count, items));
    }

    private static async Task<Results<Ok<Page<Crumb>>, NotFound, StatusCodeHttpResult>> GetCrumbsAsync(
        Guid id,
        int? offset,
        int? limit,
        HttpContext context,
        ICaseAccessAuthorizer authorization,
        CancellationToken cancellationToken)
    {
        var grant = await authorization.AuthorizeAsync(
            context.User, id, CaseAccessKind.Crumbs, cancellationToken);
        if (grant?.State.CaseFile is not { } caseFile) return TypedResults.NotFound();
        var safeOffset = Math.Max(0, offset ?? 0);
        var safeLimit = Math.Clamp(limit ?? 100, 1, 500);
        var items = caseFile.Crumbs.Skip(safeOffset).Take(safeLimit).Select(item => item.ToContract()).ToArray();
        return TypedResults.Ok(new Page<Crumb>(caseFile.Crumbs.Count, items));
    }

}
