using Microsoft.EntityFrameworkCore;
using MyHealth.Api.Data;
using MyHealth.Api.Domain;

namespace MyHealth.Api.Auth;

public record RegisterRequest(string Email, string Password, string? DisplayName);
public record LoginRequest(string Email, string Password);
public record AuthResponse(string Token, DateTimeOffset ExpiresAt, Guid UserId, string Email);

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

            var t = tokens.Create(user);
            return Results.Ok(new AuthResponse(t.Token, t.ExpiresAt, user.Id, user.Email));
        });

        group.MapPost("/login", async (
            LoginRequest req, AppDbContext db, TokenService tokens) =>
        {
            var email = req.Email.Trim().ToLowerInvariant();
            var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
            if (user is null ||
                !BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash))
                return Results.Unauthorized();

            var t = tokens.Create(user);
            return Results.Ok(new AuthResponse(t.Token, t.ExpiresAt, user.Id, user.Email));
        });

        return app;
    }
}
