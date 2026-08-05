using Panko.Contracts;
using Microsoft.AspNetCore.Mvc;
using ContractOriginKind = Panko.Contracts.CaseOriginKind;
using DomainOriginKind = Panko.Api.Domain.CaseOriginKind;

namespace Panko.Api.Cases;

public static class CaseEndpoints
{
    internal const string IdempotencyHeaderName = "Idempotency-Key";
    internal const int DefaultRecentLimit = 50;
    internal const int DefaultEventLimit = 100;
    internal const int MaximumPageSize = 500;

    private const string Tag = "Cases";

    public static IEndpointRouteBuilder MapCaseManagementApi(this IEndpointRouteBuilder endpoints)
    {
        var cases = endpoints.MapGroup("/api/cases").WithTags(Tag);
        cases.MapPost(string.Empty, CreateAsync)
            .WithName("CreateCase")
            .WithSummary("Create a Case")
            .WithDescription(
                "Creates a durable Case. Supply Idempotency-Key on every attempt; the " +
                "idempotencyKey body field is also accepted, and both values must match when supplied.")
            .Produces<CreateCaseResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        cases.MapGet(string.Empty, ListRecentAsync)
            .WithName("ListCases")
            .WithSummary("List recent Cases")
            .WithDescription($"Returns at most {MaximumPageSize} Cases visible to the caller.")
            .Produces<RecentCases>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        cases.MapPost("/{id:guid}/crumbs", AppendCrumbsAsync)
            .WithName("AppendCaseCrumbs")
            .WithSummary("Append Crumbs to a Case")
            .WithDescription(
                "The batchId is an idempotency key scoped to the Case and authenticated producer. " +
                "Reusing it with a different payload returns 409 Conflict.")
            .Produces<AppendCrumbsResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        cases.MapGet("/{id:guid}/inputs", ListInputsAsync)
            .WithName("ListCaseInputs")
            .WithSummary("List canonical Case inputs")
            .WithDescription($"Returns a bounded audit page of at most {MaximumPageSize} inputs.")
            .Produces<Page<Panko.Contracts.CaseInput>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        cases.MapPost("/{id:guid}/rebuild", RebuildAsync)
            .WithName("RebuildCaseFile")
            .WithSummary("Queue a deterministic Case File rebuild")
            .Produces<RebuildCaseFileResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        cases.MapPost("/{id:guid}/refresh-sources", RefreshSourcesAsync)
            .WithName("RefreshCaseSources")
            .WithSummary("Queue a Crumb-source refresh")
            .Produces<RefreshCaseSourcesResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        cases.MapPost("/{id:guid}/close", CloseAsync)
            .WithName("CloseCase")
            .WithSummary("Close a Case")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return endpoints;
    }

    internal static Task<IResult> CreateAsync(
        [FromBody] CreateCaseRequest request,
        [FromHeader(Name = IdempotencyHeaderName)] string? idempotencyKey,
        HttpContext context,
        [FromServices] ICaseCommands commands,
        CancellationToken cancellationToken) =>
        ExecuteAsync(context, async caller =>
        {
            var effectiveIdempotencyKey = ResolveIdempotencyKey(
                idempotencyKey,
                request.IdempotencyKey,
                "idempotencyKey");
            var result = await commands.CreateAsync(
                new CreateCase(
                    effectiveIdempotencyKey,
                    request.RecipeId,
                    request.Title,
                    request.ServiceId,
                    request.Urgency,
                    request.ReferenceTime,
                    request.Labels ?? new Dictionary<string, string>(StringComparer.Ordinal)),
                caller,
                cancellationToken);
            var @case = result.Case;
            var response = new CreateCaseResponse(
                @case.Id,
                ToContract(@case.Origin.Kind),
                @case.InputVersion,
                @case.ProjectedInputVersion,
                @case.Version,
                @case.Status,
                $"/cases/{@case.Id}",
                result.Duplicate);
            return Results.Created($"/api/cases/{@case.Id}", response);
        });

    internal static Task<IResult> ListRecentAsync(
        int? limit,
        HttpContext context,
        [FromServices] ICaseQueries queries,
        CancellationToken cancellationToken)
    {
        SetPrivateNoStore(context.Response);
        return ExecuteAsync(context, async caller =>
            Results.Ok(await queries.ListRecentAsync(
                BoundLimit(limit, DefaultRecentLimit), caller, cancellationToken)));
    }

    internal static Task<IResult> GetAsync(
        Guid id,
        HttpContext context,
        [FromServices] ICaseQueries queries,
        CancellationToken cancellationToken)
    {
        SetPrivateNoStore(context.Response);
        return ExecuteAsync(context, async caller =>
            Results.Ok(await queries.GetAsync(id, caller, cancellationToken)));
    }

    internal static Task<IResult> AppendCrumbsAsync(
        Guid id,
        [FromBody] AppendCrumbsRequest request,
        HttpContext context,
        [FromServices] ICaseCommands commands,
        CancellationToken cancellationToken) =>
        ExecuteAsync(context, async caller =>
        {
            var crumbs = request.Crumbs ?? throw new CaseValidationException(
                "crumbs is required.");
            var result = await commands.AppendCrumbsAsync(
                id,
                new AppendCrumbs(request.BatchId, crumbs),
                caller,
                cancellationToken);
            return Results.Ok(new AppendCrumbsResponse(
                result.Accepted,
                result.Duplicates,
                result.InputVersion,
                result.ProjectedInputVersion,
                result.RebuildQueued,
                result.DuplicateBatch));
        });

    internal static Task<IResult> ListInputsAsync(
        Guid id,
        int? offset,
        int? limit,
        HttpContext context,
        [FromServices] ICaseQueries queries,
        CancellationToken cancellationToken)
    {
        SetPrivateNoStore(context.Response);
        return ExecuteAsync(context, async caller =>
            Results.Ok(await queries.ListInputsAsync(
                id,
                Math.Max(0, offset ?? 0),
                BoundLimit(limit, DefaultEventLimit),
                caller,
                cancellationToken)));
    }

    internal static Task<IResult> RebuildAsync(
        Guid id,
        HttpContext context,
        [FromServices] ICaseCommands commands,
        CancellationToken cancellationToken) =>
        ExecuteAsync(context, async caller =>
        {
            var result = await commands.QueueRebuildAsync(id, caller, cancellationToken);
            return Results.Accepted(
                $"/api/cases/{id}",
                new RebuildCaseFileResponse(
                    result.CaseId, result.TargetInputVersion, result.RebuildQueued));
        });

    internal static Task<IResult> RefreshSourcesAsync(
        Guid id,
        HttpContext context,
        [FromServices] ICaseCommands commands,
        CancellationToken cancellationToken) =>
        ExecuteAsync(context, async caller =>
        {
            var result = await commands.QueueSourceRefreshAsync(id, caller, cancellationToken);
            return Results.Accepted(
                $"/api/cases/{id}",
                new RefreshCaseSourcesResponse(
                    result.CaseId, result.TargetInputVersion, result.RefreshQueued));
        });

    internal static Task<IResult> CloseAsync(
        Guid id,
        HttpContext context,
        [FromServices] ICaseCommands commands,
        CancellationToken cancellationToken) =>
        ExecuteAsync(context, async caller =>
        {
            await commands.CloseAsync(id, caller, cancellationToken);
            return Results.NoContent();
        });

    internal static int BoundLimit(int? requested, int defaultValue) =>
        Math.Clamp(requested ?? defaultValue, 1, MaximumPageSize);

    internal static string ResolveIdempotencyKey(
        string? headerValue,
        string? bodyValue,
        string bodyFieldName)
    {
        var header = NormalizeOptional(headerValue);
        var body = NormalizeOptional(bodyValue);
        if (header is not null && body is not null
            && !string.Equals(header, body, StringComparison.Ordinal))
        {
            throw new CaseValidationException(
                $"{IdempotencyHeaderName} and {bodyFieldName} must match when both are supplied.");
        }

        return header ?? body ?? throw new CaseValidationException(
            $"{IdempotencyHeaderName} is required.");
    }

    private static async Task<IResult> ExecuteAsync(
        HttpContext context,
        Func<CallerIdentity, Task<IResult>> action)
    {
        if (!HasAuthenticatedIdentity(context.User))
        {
            return Problem(
                StatusCodes.Status401Unauthorized,
                "Authentication required",
                "An authenticated caller identity is required.");
        }

        try
        {
            return await action(new CallerIdentity(context.User));
        }
        catch (CaseValidationException exception)
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Invalid Case request",
                exception.Message);
        }
        catch (CaseAuthorizationException exception)
        {
            return Problem(
                StatusCodes.Status403Forbidden,
                "Case access denied",
                exception.Message);
        }
        catch (CaseNotFoundException exception)
        {
            return Problem(
                StatusCodes.Status404NotFound,
                "Case not found",
                exception.Message);
        }
        catch (CaseConflictException exception)
        {
            return Problem(
                StatusCodes.Status409Conflict,
                "Case conflict",
                exception.Message);
        }
    }

    private static IResult Problem(int statusCode, string title, string detail) =>
        Results.Problem(statusCode: statusCode, title: title, detail: detail);

    private static bool HasAuthenticatedIdentity(System.Security.Claims.ClaimsPrincipal principal) =>
        principal.Identity?.IsAuthenticated == true
        && (!string.IsNullOrWhiteSpace(principal.FindFirst("sub")?.Value)
            || !string.IsNullOrWhiteSpace(principal.Identity.Name));

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void SetPrivateNoStore(HttpResponse response) =>
        response.Headers.CacheControl = "private, no-store";

    private static ContractOriginKind ToContract(DomainOriginKind kind) => kind switch
    {
        DomainOriginKind.Agent => ContractOriginKind.Agent,
        DomainOriginKind.Manual => ContractOriginKind.Manual,
        _ => ContractOriginKind.PagerDuty
    };
}
