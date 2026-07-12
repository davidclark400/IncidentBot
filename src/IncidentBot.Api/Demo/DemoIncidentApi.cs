using IncidentBot.Api.Incidents;
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
        DemoIncidentStore store,
        IIncidentUpdatePublisher updates,
        CancellationToken cancellationToken)
    {
        var reset = store.Reset();
        await updates.PublishReportAsync(
            DemoIncidentStore.IncidentId,
            reset.Report.Version,
            reset.Report.Status,
            ["status", "summary", "timeline", "evidence", "sources", "causalEvents", "ai", "problem"],
            cancellationToken);
        return TypedResults.Ok(new DemoReset(
            DemoIncidentStore.IncidentId,
            $"/incidents/{DemoIncidentStore.IncidentId}",
            reset.Report.Version));
    }

}
