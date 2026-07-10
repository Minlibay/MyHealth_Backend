using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using MyHealth.Api.Common;
using MyHealth.Api.Data;
using MyHealth.Api.Domain;
using MyHealth.Api.Features.Metrics;

namespace MyHealth.Api.Features.Workouts;

public record WorkoutUploadDto(
    string ActivityType,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    double? EnergyKcal,
    double? DistanceMeters,
    string? Source,
    string? ClientId);

public record WorkoutDto(
    Guid Id,
    string ActivityType,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    double DurationMinutes,
    double? EnergyKcal,
    double? DistanceMeters,
    string? Source)
{
    public static WorkoutDto From(Workout w) => new(
        w.Id, w.ActivityType, w.StartedAt, w.EndedAt,
        (w.EndedAt - w.StartedAt).TotalMinutes,
        w.EnergyKcal, w.DistanceMeters, w.Source);
}

public static class WorkoutsEndpoints
{
    public static IEndpointRouteBuilder MapWorkoutEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/workouts")
            .WithTags("Workouts")
            .RequireAuthorization();

        // Пакетная загрузка тренировок. Идемпотентна по (UserId, ClientId).
        group.MapPost("/", async (
            List<WorkoutUploadDto> items, ClaimsPrincipal principal, AppDbContext db) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();
            if (items.Count == 0) return Results.Ok(new UploadResult(0, 0));

            var clientIds = items
                .Where(i => i.ClientId is not null)
                .Select(i => i.ClientId!)
                .ToList();
            var existing = await db.Workouts
                .Where(w => w.UserId == userId && w.ClientId != null &&
                            clientIds.Contains(w.ClientId))
                .Select(w => w.ClientId!)
                .ToListAsync();
            var existingSet = existing.ToHashSet();

            var inserted = 0;
            var seen = new HashSet<string>();
            foreach (var i in items)
            {
                if (i.ClientId is not null &&
                    (existingSet.Contains(i.ClientId) || !seen.Add(i.ClientId)))
                    continue;

                db.Workouts.Add(new Workout
                {
                    UserId = userId.Value,
                    ActivityType = i.ActivityType,
                    StartedAt = i.StartedAt,
                    EndedAt = i.EndedAt,
                    EnergyKcal = i.EnergyKcal,
                    DistanceMeters = i.DistanceMeters,
                    Source = i.Source,
                    ClientId = i.ClientId,
                });
                inserted++;
            }

            await db.SaveChangesAsync();
            return Results.Ok(new UploadResult(inserted, items.Count - inserted));
        });

        // Тренировки за период (новые первыми).
        group.MapGet("/", async (
            ClaimsPrincipal principal, AppDbContext db,
            DateTimeOffset? from, DateTimeOffset? to, int limit = 100) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var q = db.Workouts.AsNoTracking().Where(w => w.UserId == userId);
            if (from is not null) q = q.Where(w => w.StartedAt >= from);
            if (to is not null) q = q.Where(w => w.StartedAt <= to);

            var data = await q
                .OrderByDescending(w => w.StartedAt)
                .Take(Math.Clamp(limit, 1, 1000))
                .ToListAsync();

            return Results.Ok(data.Select(WorkoutDto.From));
        });

        return app;
    }
}
