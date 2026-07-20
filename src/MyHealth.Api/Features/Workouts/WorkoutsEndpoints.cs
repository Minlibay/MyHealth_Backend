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
    string? Source,
    double? AvgHr,
    double? MaxHr,
    /// <summary>Минуты в зонах пульса Z1..Z5 (50-60-70-80-90%+ от HRmax).</summary>
    List<double>? ZonesMinutes,
    /// <summary>TRIMP по Эдвардсу: Σ минут в зоне × номер зоны.</summary>
    double? Trimp)
{
    public static WorkoutDto From(
        Workout w,
        List<(DateTimeOffset At, double Hr)>? hr = null,
        double hrMax = 190)
    {
        var minutes = (w.EndedAt - w.StartedAt).TotalMinutes;
        double? avg = null, max = null, trimp = null;
        List<double>? zones = null;

        var points = hr?.Where(p => p.At >= w.StartedAt && p.At <= w.EndedAt)
            .Select(p => p.Hr)
            .ToList();
        if (points is { Count: >= 2 })
        {
            avg = Math.Round(points.Average());
            max = points.Max();
            // Точки пульса распределяем равномерно по длительности тренировки.
            var minutesPerPoint = minutes / points.Count;
            zones = [0, 0, 0, 0, 0];
            foreach (var p in points)
            {
                var pct = p / hrMax;
                var zone = pct switch
                {
                    < 0.6 => 0,
                    < 0.7 => 1,
                    < 0.8 => 2,
                    < 0.9 => 3,
                    _ => 4,
                };
                zones[zone] += minutesPerPoint;
            }
            zones = zones.Select(z => Math.Round(z, 1)).ToList();
            trimp = Math.Round(zones.Select((z, i) => z * (i + 1)).Sum(), 1);
        }

        return new WorkoutDto(
            w.Id, w.ActivityType, w.StartedAt, w.EndedAt, minutes,
            w.EnergyKcal, w.DistanceMeters, w.Source,
            avg, max, zones, trimp);
    }
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
            if (data.Count == 0) return Results.Ok(Enumerable.Empty<WorkoutDto>());

            // Пульс за общее окно всех тренировок одним запросом —
            // для зон и TRIMP каждой тренировки.
            // Максимальный пульс — из возраста в профиле (220 − возраст),
            // без профиля берём типовые 190 (≈ 30 лет).
            var age = await db.Users.AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => u.Age)
                .FirstOrDefaultAsync();
            var hrMax = age is int a ? 220.0 - a : 190.0;

            var minStart = data.Min(w => w.StartedAt);
            var maxEnd = data.Max(w => w.EndedAt);
            var hr = (await db.Samples.AsNoTracking()
                    .Where(s => s.UserId == userId &&
                                s.Metric == MetricType.HeartRate &&
                                s.RecordedAt >= minStart && s.RecordedAt <= maxEnd)
                    .Select(s => new { s.RecordedAt, s.Value })
                    .ToListAsync())
                .Select(s => (s.RecordedAt, s.Value))
                .ToList();

            return Results.Ok(data.Select(w => WorkoutDto.From(w, hr, hrMax)));
        });

        return app;
    }
}
