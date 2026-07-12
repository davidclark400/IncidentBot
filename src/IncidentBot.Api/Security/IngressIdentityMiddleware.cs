using System.Security.Claims;
using IncidentBot.Api.Options;
using Microsoft.Extensions.Options;

namespace IncidentBot.Api.Security;

public sealed class IngressIdentityMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        IOptions<IngressIdentityOptions> options,
        IOptions<DemoOptions> demoOptions)
    {
        var path = context.Request.Path;
        var exempt = demoOptions.Value.Enabled
            || path.StartsWithSegments("/api/webhooks/pagerduty")
            || path.StartsWithSegments("/health")
            || !path.StartsWithSegments("/api") && !path.StartsWithSegments("/hubs");

        var identityHeader = options.Value.HeaderName;
        if (context.Request.Headers.TryGetValue(identityHeader, out var user) && !string.IsNullOrWhiteSpace(user))
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, user.ToString())], "Ingress"));
        }
        else if (options.Value.Required && !exempt)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Trusted ingress identity is required." });
            return;
        }

        await next(context);
    }
}
