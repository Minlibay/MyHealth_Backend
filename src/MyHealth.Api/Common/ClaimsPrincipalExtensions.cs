using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;

namespace MyHealth.Api.Common;

public static class ClaimsPrincipalExtensions
{
    /// <summary>Id текущего пользователя из claim "sub" (или NameIdentifier).</summary>
    public static Guid? GetUserId(this ClaimsPrincipal user)
    {
        var id = user.FindFirstValue(JwtRegisteredClaimNames.Sub)
                 ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(id, out var guid) ? guid : null;
    }
}
