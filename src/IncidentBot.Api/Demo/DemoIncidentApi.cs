using IncidentBot.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;

namespace IncidentBot.Api.Demo;

public static class DemoIncidentApi
{
    public static IEndpointRouteBuilder MapDemoIncidentApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/demo", () => TypedResults.Ok(new DemoAvailability(
            true,
            DemoIncidentStore.IncidentId,
            $"/incidents/{DemoIncidentStore.IncidentId}"))).WithName("GetDemoAvailability");
        endpoints.MapPost("/api/demo/reset", ResetAsync).WithName("ResetDemo");
        return endpoints;
    }

    private static async Task<Ok<DemoReset>> ResetAsync(
        DemoReplay replay,
        CancellationToken cancellationToken)
    {
        var reset = await replay.ResetAsync(cancellationToken);
        return TypedResults.Ok(new DemoReset(
            DemoIncidentStore.IncidentId,
            $"/incidents/{DemoIncidentStore.IncidentId}",
            reset.Report.Version));
    }

}
