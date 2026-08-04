using Panko.Api.Options;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace Panko.Api.Cases;

public sealed class CaseRequestSizeMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        IOptions<CaseOptions> options)
    {
        var boundedRoute = context.Request.Path.StartsWithSegments("/api/cases")
            || context.Request.Path.StartsWithSegments("/api/mcp");
        if (!boundedRoute || HttpMethods.IsGet(context.Request.Method))
        {
            await next(context);
            return;
        }

        var maximumBytes = options.Value.MaximumRequestBytes;
        if (context.Request.ContentLength is > 0 and var declaredBytes && declaredBytes > maximumBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await context.Response.WriteAsJsonAsync(new
            {
                error = $"Request body exceeds the configured {maximumBytes}-byte limit."
            });
            return;
        }

        var bodySize = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySize is { IsReadOnly: false })
        {
            bodySize.MaxRequestBodySize = maximumBytes;
        }
        await next(context);
    }
}
