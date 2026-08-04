using System.Security.Claims;
using Panko.Api.Cases;

namespace Panko.Api.Security;

public static class DevelopmentOpenAccessIdentity
{
    public const string Subject = "local-development";
    public const string AuthenticationType = "development-open-access";

    public static ClaimsPrincipal CreatePrincipal() => new(
        new ClaimsIdentity(
        [
            new Claim("sub", Subject),
            new Claim(ClaimTypes.Name, Subject),
            new Claim(CaseAuthorization.PermissionClaimType, "*")
        ],
        AuthenticationType,
        ClaimTypes.Name,
        ClaimTypes.Role));
}
