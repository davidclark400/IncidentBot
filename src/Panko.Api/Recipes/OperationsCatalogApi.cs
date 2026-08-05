using Panko.Contracts;

namespace Panko.Api.Recipes;

public static class OperationsCatalogApi
{
    public static IEndpointRouteBuilder MapOperationsCatalogApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/catalog", Get)
            .WithName("GetOperationsCatalog")
            .WithTags("Operations catalog")
            .WithSummary("Browse authorized teams, service collections, and observed services")
            .Produces<OperationsCatalog>();
        return endpoints;
    }

    private static IResult Get(HttpContext context, OperationsCatalogBrowser catalog)
    {
        context.Response.Headers.CacheControl = "private, no-store";
        return Results.Ok(catalog.Browse(context.User));
    }
}
