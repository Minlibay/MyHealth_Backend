using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using MyHealth.Api.Common;
using MyHealth.Api.Data;

namespace MyHealth.Api.Features.User;

public record DeleteAccountRequest(string Password);

/// <summary>Профиль: физические параметры и персональные цели.</summary>
public record ProfileDto(
    string? Gender,
    int? Age,
    double? HeightCm,
    double? WeightKg,
    int? StepsGoal,
    double? WaterGoalLiters,
    double? SleepGoalHours,
    int? KcalGoal);

/// <summary>GDPR: экспорт всех данных пользователя и удаление аккаунта.</summary>
public static class UserEndpoints
{
    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/user")
            .WithTags("User")
            .RequireAuthorization();

        group.MapGet("/profile", async (ClaimsPrincipal principal, AppDbContext db) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();
            var u = await db.Users.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == userId);
            if (u is null) return Results.Unauthorized();
            return Results.Ok(new ProfileDto(
                u.Gender, u.Age, u.HeightCm, u.WeightKg,
                u.StepsGoal, u.WaterGoalLiters, u.SleepGoalHours, u.KcalGoal));
        });

        group.MapPut("/profile", async (
            ProfileDto dto, ClaimsPrincipal principal, AppDbContext db) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();
            var u = await db.Users.FirstOrDefaultAsync(x => x.Id == userId);
            if (u is null) return Results.Unauthorized();

            u.Gender = dto.Gender is "male" or "female" ? dto.Gender : null;
            u.Age = Clamp(dto.Age, 5, 120);
            u.HeightCm = Clamp(dto.HeightCm, 80, 250);
            u.WeightKg = Clamp(dto.WeightKg, 20, 350);
            u.StepsGoal = Clamp(dto.StepsGoal, 1000, 100_000);
            u.WaterGoalLiters = Clamp(dto.WaterGoalLiters, 0.5, 10);
            u.SleepGoalHours = Clamp(dto.SleepGoalHours, 4, 12);
            u.KcalGoal = Clamp(dto.KcalGoal, 800, 10_000);
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        // Полный экспорт данных пользователя (право на переносимость, GDPR ст. 20).
        group.MapGet("/export", async (ClaimsPrincipal principal, AppDbContext db) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var user = await db.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == userId);
            if (user is null) return Results.Unauthorized();

            // Экспорт из новой схемы: значения показателей, события,
            // вендорские результаты и зарегистрированные устройства.
            var observations = await db.Observations.AsNoTracking()
                .Include(o => o.DeviceInstance)
                .Where(o => o.UserId == userId)
                .OrderBy(o => o.StartAt)
                .Select(o => new
                {
                    o.MetricCode, o.ValueNum, o.ValueSecondary, o.ValueJson, o.Unit,
                    o.StartAt, o.EndAt, o.TimezoneOffset,
                    Source = o.DeviceInstance == null
                        ? null
                        : o.DeviceInstance.IntegrationPlatform +
                          (o.DeviceInstance.DataOriginAppId == null
                              ? ""
                              : ":" + o.DeviceInstance.DataOriginAppId),
                    o.CreatedAt,
                })
                .ToListAsync();

            var events = await db.Events.AsNoTracking()
                .Include(e => e.DeviceInstance)
                .Where(e => e.UserId == userId)
                .OrderBy(e => e.StartAt)
                .Select(e => new
                {
                    e.EventTypeCode, e.EventName, e.StartAt, e.EndAt,
                    e.SourceEventType,
                    Source = e.DeviceInstance == null
                        ? null
                        : e.DeviceInstance.IntegrationPlatform,
                    e.CreatedAt,
                })
                .ToListAsync();

            var vendorMetrics = await db.VendorMetrics.AsNoTracking()
                .Where(v => v.UserId == userId)
                .OrderBy(v => v.EffectiveAt)
                .Select(v => new
                {
                    v.VendorMetricCode, v.ValueNum, v.ValueText, v.Unit,
                    v.EffectiveAt, v.PeriodEndAt, v.SourceState, v.CreatedAt,
                })
                .ToListAsync();

            var devices = await db.DeviceInstances.AsNoTracking()
                .Where(d => d.UserId == userId)
                .Select(d => new
                {
                    d.IntegrationPlatform, d.DeviceType, d.DeviceName,
                    d.Manufacturer, d.Model, d.DataOriginAppId, d.CreatedAt,
                })
                .ToListAsync();

            var export = new
            {
                ExportedAt = DateTimeOffset.UtcNow,
                Profile = new { user.Email, user.DisplayName, user.CreatedAt },
                Devices = devices,
                Observations = observations,
                Events = events,
                VendorMetrics = vendorMetrics,
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

    private static int? Clamp(int? v, int min, int max) =>
        v is int x ? Math.Clamp(x, min, max) : null;

    private static double? Clamp(double? v, double min, double max) =>
        v is double x ? Math.Clamp(x, min, max) : null;
}
