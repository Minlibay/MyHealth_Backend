using Microsoft.EntityFrameworkCore;
using MyHealth.Api.Data;
using MyHealth.Api.Domain;

namespace MyHealth.Api.Auth;

public record RegisterRequest(string Email, string Password, string? DisplayName);
public record LoginRequest(string Email, string Password);
public record RefreshRequest(string RefreshToken);

public record AuthResponse(
    string Token,
    DateTimeOffset ExpiresAt,
    Guid UserId,
    string Email,
    string RefreshToken,
    DateTimeOffset RefreshExpiresAt);

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapPost("/register", async (
            RegisterRequest req, AppDbContext db, TokenService tokens) =>
        {
            var email = req.Email.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
                return Results.BadRequest(new { error = "Некорректный email." });
            if (req.Password.Length < 8)
                return Results.BadRequest(new { error = "Пароль не короче 8 символов." });

            if (await db.Users.AnyAsync(u => u.Email == email))
                return Results.Conflict(new { error = "Пользователь уже существует." });

            var user = new AppUser
            {
                Email = email,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.Password),
                DisplayName = req.DisplayName?.Trim(),
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();

            return Results.Ok(await IssueAsync(user, db, tokens));
        });

        group.MapPost("/login", async (
            LoginRequest req, AppDbContext db, TokenService tokens) =>
        {
            var email = req.Email.Trim().ToLowerInvariant();
            var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
            if (user is null ||
                !BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash))
                return Results.Unauthorized();

            return Results.Ok(await IssueAsync(user, db, tokens));
        });

        // Обновление пары токенов. Refresh-токен ротируется: повторное
        // использование старого значения невозможно.
        group.MapPost("/refresh", async (
            RefreshRequest req, AppDbContext db, TokenService tokens) =>
        {
            if (string.IsNullOrWhiteSpace(req.RefreshToken))
                return Results.Unauthorized();

            var hash = TokenService.Hash(req.RefreshToken);
            var stored = await db.RefreshTokens
                .Include(t => t.User)
                .FirstOrDefaultAsync(t => t.TokenHash == hash);
            if (stored is null || !stored.IsActive || stored.User is null)
                return Results.Unauthorized();

            stored.RevokedAt = DateTimeOffset.UtcNow;
            return Results.Ok(await IssueAsync(stored.User, db, tokens));
        });

        // Выход: отзыв refresh-токена (access доживает свой короткий срок).
        group.MapPost("/logout", async (RefreshRequest req, AppDbContext db) =>
        {
            if (!string.IsNullOrWhiteSpace(req.RefreshToken))
            {
                var hash = TokenService.Hash(req.RefreshToken);
                var stored = await db.RefreshTokens
                    .FirstOrDefaultAsync(t => t.TokenHash == hash);
                if (stored is not null && stored.RevokedAt is null)
                {
                    stored.RevokedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync();
                }
            }
            return Results.Ok();
        });

        return app;
    }

    private static async Task<AuthResponse> IssueAsync(
        AppUser user, AppDbContext db, TokenService tokens)
    {
        var access = tokens.Create(user);
        var refresh = tokens.CreateRefresh(user.Id);
        db.RefreshTokens.Add(refresh.Entity);
        await db.SaveChangesAsync();
        return new AuthResponse(
            access.Token, access.ExpiresAt, user.Id, user.Email,
            refresh.Token, refresh.Entity.ExpiresAt);
    }
}
