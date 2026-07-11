using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MyHealth.Api.Domain;

namespace MyHealth.Api.Auth;

public record TokenResult(string Token, DateTimeOffset ExpiresAt);

/// <summary>Пара «значение для клиента + сущность с хешем для БД».</summary>
public record RefreshTokenResult(string Token, RefreshToken Entity);

public class TokenService(IOptions<JwtSettings> options)
{
    private readonly JwtSettings _s = options.Value;

    public TokenResult Create(AppUser user)
    {
        var expires = DateTimeOffset.UtcNow.AddMinutes(_s.ExpiryMinutes);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_s.Key));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _s.Issuer,
            audience: _s.Audience,
            claims: claims,
            expires: expires.UtcDateTime,
            signingCredentials: creds);

        return new TokenResult(new JwtSecurityTokenHandler().WriteToken(token), expires);
    }

    /// <summary>Новый refresh-токен: 64 случайных байта, в БД — только хеш.</summary>
    public RefreshTokenResult CreateRefresh(Guid userId)
    {
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        var entity = new RefreshToken
        {
            UserId = userId,
            TokenHash = Hash(raw),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(_s.RefreshExpiryDays),
        };
        return new RefreshTokenResult(raw, entity);
    }

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
