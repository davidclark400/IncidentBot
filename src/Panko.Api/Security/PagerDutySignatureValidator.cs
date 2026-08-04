using System.Security.Cryptography;
using System.Text;
using Panko.Api.Infrastructure;
using Panko.Api.Options;
using Microsoft.Extensions.Options;

namespace Panko.Api.Security;

public sealed class PagerDutySignatureValidator(
    IOptions<PagerDutyOptions> options,
    ICredentialProvider credentials)
{
    public bool Validate(ReadOnlySpan<byte> payload, string? signatureHeader)
    {
        if (!options.Value.RequireSignature)
        {
            return true;
        }

        var secret = credentials.Get(options.Value.WebhookSecretEnv);
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(signatureHeader))
        {
            return false;
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var expected = Convert.ToHexStringLower(hmac.ComputeHash(payload.ToArray()));
        return signatureHeader.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => value.StartsWith("v1=", StringComparison.OrdinalIgnoreCase) ? value[3..] : value)
            .Any(supplied => supplied.Length == expected.Length
                && CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(supplied.ToLowerInvariant()),
                    Encoding.ASCII.GetBytes(expected)));
    }
}
