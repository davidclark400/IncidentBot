using Panko.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Panko.Api.Demo;

public static class DemoCaseApi
{
    public static IEndpointRouteBuilder MapDemoCaseApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/demo", () => TypedResults.Ok(new DemoAvailability(
            true,
            DemoCaseStore.CaseId,
            $"/cases/{DemoCaseStore.CaseId}"))).WithName("GetDemoAvailability");
        endpoints.MapGet("/api/cases", ListCases).WithName("ListCases");
        endpoints.MapGet("/api/cases/{id:guid}/inputs", ListInputs).WithName("ListCaseInputs");
        endpoints.MapPost("/api/demo/reset", ResetAsync).WithName("ResetDemo");
        return endpoints;
    }

    internal static Ok<RecentCases> ListCases(DemoCaseStore store)
    {
        var caseFile = store.Get();
        var origin = caseFile.Origin.Kind switch
        {
            Domain.CaseOriginKind.PagerDuty => CaseOriginKind.PagerDuty,
            Domain.CaseOriginKind.Agent => CaseOriginKind.Agent,
            Domain.CaseOriginKind.Manual => CaseOriginKind.Manual,
            _ => throw new InvalidOperationException($"Unknown Case origin '{caseFile.Origin.Kind}'.")
        };
        var recentCase = new RecentCase(
            caseFile.CaseId,
            origin,
            caseFile.RecipeId,
            caseFile.ServiceId,
            caseFile.Title,
            caseFile.Status,
            caseFile.InputVersion,
            caseFile.ProjectedInputVersion,
            caseFile.CaseFileVersion,
            caseFile.CreatedBy,
            caseFile.UpdatedAt,
            $"/cases/{caseFile.CaseId}");
        return TypedResults.Ok(new RecentCases(1, [recentCase]));
    }

    internal static IResult ListInputs(Guid id) => id == DemoCaseStore.CaseId
        ? TypedResults.Ok(new Page<Panko.Contracts.CaseInput>(0, []))
        : TypedResults.NotFound();

    private static async Task<Ok<DemoReset>> ResetAsync(
        DemoReplay replay,
        CancellationToken cancellationToken)
    {
        var reset = await replay.ResetAsync(cancellationToken);
        return TypedResults.Ok(new DemoReset(
            DemoCaseStore.CaseId,
            $"/cases/{DemoCaseStore.CaseId}",
            reset.CaseFile.CaseFileVersion));
    }

}
