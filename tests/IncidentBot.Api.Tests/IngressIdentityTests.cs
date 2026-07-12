using System.Security.Claims;
using IncidentBot.Api.Options;
using IncidentBot.Api.Security;
using Microsoft.AspNetCore.Http;

namespace IncidentBot.Api.Tests;

public sealed class IngressIdentityTests
{
    [Fact]
    public async Task ProtectedApiRejectsRequestsWithoutTrustedIdentity()
    {
        var nextCalled = false;
        var middleware = new IngressIdentityMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/incidents/11111111-1111-1111-1111-111111111111";

        await middleware.InvokeAsync(
            context,
            Microsoft.Extensions.Options.Options.Create(new IngressIdentityOptions { Required = true }),
            Microsoft.Extensions.Options.Options.Create(new DemoOptions { Enabled = false }));

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task TrustedIdentityIsAttachedBeforeProtectedApiRuns()
    {
        ClaimsPrincipal? observedUser = null;
        var middleware = new IngressIdentityMiddleware(context =>
        {
            observedUser = context.User;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/incidents/11111111-1111-1111-1111-111111111111";
        context.Request.Headers["X-Forwarded-User"] = "operator@example.internal";

        await middleware.InvokeAsync(
            context,
            Microsoft.Extensions.Options.Options.Create(new IngressIdentityOptions { Required = true }),
            Microsoft.Extensions.Options.Options.Create(new DemoOptions { Enabled = false }));

        Assert.Equal("operator@example.internal", observedUser?.Identity?.Name);
        Assert.True(observedUser?.Identity?.IsAuthenticated);
    }

    [Fact]
    public async Task SignedWebhookRouteDoesNotRequireInteractiveIdentity()
    {
        var nextCalled = false;
        var middleware = new IngressIdentityMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/webhooks/pagerduty/v3";

        await middleware.InvokeAsync(
            context,
            Microsoft.Extensions.Options.Options.Create(new IngressIdentityOptions { Required = true }),
            Microsoft.Extensions.Options.Options.Create(new DemoOptions { Enabled = false }));

        Assert.True(nextCalled);
    }
}
