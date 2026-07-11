using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MyHealth.Api.Common;
using MyHealth.Api.Data;
using MyHealth.Api.Features.Sleep;

namespace MyHealth.Api.Features.User;

public record DeleteAccountRequest(string Password);

/// <summary>GDPR: экспорт всех данных пользователя и удаление аккаунта.</summary>
public static class UserEndpoints
{
    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/user")
            .WithTags("User")
            .RequireAuthorization();

        // Полный экспорт данных пользователя (право на переносимость, GDPR ст. 20).
        group.MapGet("/export", async (ClaimsPrincipal principal, AppDbContext db) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var user = await db.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == userId);
            if (user is null) return Results.Unauthorized();

            var samples = await db.Samples.AsNoTracking()
                .Where(s => s.UserId == userId)
                .OrderBy(s => s.RecordedAt)
                .Select(s => new
                {
                    s.Metric, s.Value, s.Secondary, s.Unit,
                    s.RecordedAt, s.Source, s.CreatedAt,
                })
                .ToListAsync();

            var workouts = await db.Workouts.AsNoTracking()
                .Where(w => w.UserId == userId)
                .OrderBy(w => w.StartedAt)
                .Select(w => new
                {
                    w.ActivityType, w.StartedAt, w.EndedAt,
                    w.EnergyKcal, w.DistanceMeters, w.Source, w.CreatedAt,
                })
                .ToListAsync();

            var sleep = (await db.SleepSessions.AsNoTracking()
                .Where(s => s.UserId == userId)
                .OrderBy(s => s.StartedAt)
                .ToListAsync())
                .Select(s => new
                {
                    s.StartedAt, s.EndedAt, s.Source, s.CreatedAt,
                    Stages = JsonSerializer.Deserialize<List<SleepStageDto>>(
                        s.StagesJson, SleepSessionDto.JsonOptions),
                });

            var export = new
            {
                ExportedAt = DateTimeOffset.UtcNow,
                Profile = new { user.Email, user.DisplayName, user.CreatedAt },
                Samples = samples,
                Workouts = workouts,
                SleepSessions = sleep,
            };

            return Results.Json(export);
        });

        // Удаление аккаунта и всех данных (право на забвение, GDPR ст. 17).
        // Требует подтверждения паролем. Каскад удаляет измерения, тренировки,
        // сессии сна и refresh-токены.
        group.MapPost("/delete", async (
            DeleteAccountRequest req, ClaimsPrincipal principal, AppDbContext db) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user is null) return Results.Unauthorized();
            if (!BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash))
                return Results.BadRequest(new { error = "Неверный пароль." });

            db.Users.Remove(user);
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        return app;
    }
}
