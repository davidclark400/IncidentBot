using System.Security.Cryptography;
using System.Text;
using Panko.Api.Options;
using Panko.Api.Security;
using Microsoft.Extensions.Options;

namespace Panko.Api.Tests;

public sealed class SecurityTests
{
    [Fact]
    public void PagerDutySignature_UsesConstantExpectedHmacFormat()
    {
        const string envName = "PANKO_TEST_PD_SECRET";
        const string secret = "test-secret";
        var payload = Encoding.UTF8.GetBytes("{\"event\":{}}");
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var signature = "v1=" + Convert.ToHexStringLower(hmac.ComputeHash(payload));
        var validator = new PagerDutySignatureValidator(
            Microsoft.Extensions.Options.Options.Create(new PagerDutyOptions
            {
                WebhookSecretEnv = envName,
                RequireSignature = true
            }),
            TestConfiguration.Credentials((envName, secret)));

        Assert.True(validator.Validate(payload, signature));
        Assert.False(validator.Validate(payload, "v1=" + new string('0', 64)));
    }

    [Fact]
    public void SafeTemplateRenderer_AllowsTypedValuesAndRejectsQueryInjection()
    {
        var renderer = new SafeTemplateRenderer();
        var rendered = renderer.Render("service:=\"{{service}}\" env:=\"{{environment}}\"", new Dictionary<string, string>
        {
            ["service"] = "payments-api",
            ["environment"] = "production"
        });
        Assert.Equal("service:=\"payments-api\" env:=\"production\"", rendered);

        Assert.Throws<InvalidOperationException>(() => renderer.Render("{{environment}}", new Dictionary<string, string>
        {
            ["environment"] = "production\" OR _time:1y"
        }));
        Assert.Throws<InvalidOperationException>(() => renderer.Render("{{arbitrary_query}}", new Dictionary<string, string>
        {
            ["arbitrary_query"] = "*"
        }));
    }
}
