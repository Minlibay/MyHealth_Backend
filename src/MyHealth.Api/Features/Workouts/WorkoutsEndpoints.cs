using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using MyHealth.Api.Common;
using MyHealth.Api.Data;
using MyHealth.Api.Domain;
using MyHealth.Api.Domain.Tracking;
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
    /// <summary>Событие тренировки новой схемы в прежнем формате ответа.</summary>
    public static WorkoutDto From(
        TrackedEvent ev,
        double? energyKcal,
        double? distanceMeters,
        List<(DateTimeOffset At, double Hr)>? hr = null,
        double hrMax = 190)
    {
        var startedAt = ev.StartAt;
        var endedAt = ev.EndAt ?? ev.StartAt;
        var minutes = (endedAt - startedAt).TotalMinutes;
        double? avg = null, max = null, trimp = null;
        List<double>? zones = null;

        var points = hr?.Where(p => p.At >= startedAt && p.At <= endedAt)
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
            ev.Id,
            ev.EventName ?? ev.SourceEventType ?? ev.EventTypeCode,
            startedAt, endedAt, minutes,
            energyKcal, distanceMeters, TrackingStore.SourceString(ev.DeviceInstance),
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

        // Пакетная загрузка тренировок в events новой схемы
        // (калории и дистанция — как связанные значения показателей).
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
            var existingSet = (await db.Events
                .Where(e => e.UserId == userId && e.ClientId != null &&
                            clientIds.Contains(e.ClientId))
                .Select(e => e.ClientId!)
                .ToListAsync()).ToHashSet();

            var knownTypes = (await db.EventTypeDefinitions
                .Select(t => t.EventTypeCode).ToListAsync()).ToHashSet();
            var map = await db.SourceEventTypeMaps.AsNoTracking()
                .Select(m => new { m.SourceEventType, m.EventTypeCode })
                .ToListAsync();
            var bySourceType = map
                .GroupBy(m => m.SourceEventType.ToLowerInvariant())
                .ToDictionary(g => g.Key, g => g.First().EventTypeCode);
            var store = new TrackingStore(db);

            var inserted = 0;
            var seen = new HashSet<string>();
            foreach (var i in items)
            {
                if (i.ClientId is not null &&
                    (existingSet.Contains(i.ClientId) || !seen.Add(i.ClientId)))
                    continue;

                var device = await store.ResolveDeviceAsync(userId.Value, i.Source);
                var ev = new TrackedEvent
                {
                    UserId = userId.Value,
                    EventTypeCode = ResolveEventType(
                        i.ActivityType, bySourceType, knownTypes),
                    EventName = i.ActivityType,
                    StartAt = i.StartedAt,
                    EndAt = i.EndedAt,
                    SourceEventType = i.ActivityType,
                    DeviceInstanceId = device?.Id,
                    ClientId = i.ClientId,
                };
                db.Events.Add(ev);

                if (i.EnergyKcal is double kcal)
                    AddSessionMetric(db, ev, "activity.calories.session", kcal, "ккал");
                if (i.DistanceMeters is double meters)
                    AddSessionMetric(db, ev, "activity.distance.session", meters, "м");

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

            var q = db.Events.AsNoTracking()
                .Include(e => e.DeviceInstance)
                .Where(e => e.UserId == userId &&
                            e.EventTypeCode.StartsWith("workout."));
            if (from is not null) q = q.Where(e => e.StartAt >= from);
            if (to is not null) q = q.Where(e => e.StartAt <= to);

            var data = await q
                .OrderByDescending(e => e.StartAt)
                .Take(Math.Clamp(limit, 1, 1000))
                .ToListAsync();
            if (data.Count == 0) return Results.Ok(Enumerable.Empty<WorkoutDto>());

            // Калории и дистанция сессий — через связи измерение↔событие.
            var ids = data.Select(e => e.Id).ToList();
            var links = await db.MeasurementEventLinks.AsNoTracking()
                .Where(l => ids.Contains(l.EventId) &&
                            l.MeasurementType == "observation")
                .Select(l => new { l.EventId, l.MeasurementId })
                .ToListAsync();
            var obsIds = links.Select(l => l.MeasurementId).ToList();
            var sessionValues = await db.Observations.AsNoTracking()
                .Where(o => obsIds.Contains(o.Id))
                .Select(o => new { o.Id, o.MetricCode, o.ValueNum })
                .ToListAsync();
            var valueById = sessionValues.ToDictionary(o => o.Id);
            var kcalByEvent = new Dictionary<Guid, double>();
            var distByEvent = new Dictionary<Guid, double>();
            foreach (var l in links)
            {
                if (!valueById.TryGetValue(l.MeasurementId, out var o) ||
                    o.ValueNum is not double v) continue;
                if (o.MetricCode == "activity.calories.session")
                    kcalByEvent[l.EventId] = v;
                else if (o.MetricCode == "activity.distance.session")
                    distByEvent[l.EventId] = v;
            }

            // Пульс за общее окно всех тренировок одним запросом —
            // для зон и TRIMP каждой тренировки.
            // Максимальный пульс — из возраста в профиле (220 − возраст),
            // без профиля берём типовые 190 (≈ 30 лет).
            var age = await db.Users.AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => u.Age)
                .FirstOrDefaultAsync();
            var hrMax = age is int a ? 220.0 - a : 190.0;

            var minStart = data.Min(e => e.StartAt);
            var maxEnd = data.Max(e => e.EndAt ?? e.StartAt);
            var hrCode = MetricCodeMap.ToCode(MetricType.HeartRate);
            var hr = (await db.Observations.AsNoTracking()
                    .Where(o => o.UserId == userId && o.MetricCode == hrCode &&
                                o.ValueNum != null &&
                                o.StartAt >= minStart && o.StartAt <= maxEnd)
                    .Select(o => new { o.StartAt, Value = o.ValueNum!.Value })
                    .ToListAsync())
                .Select(o => (o.StartAt, o.Value))
                .ToList();

            return Results.Ok(data.Select(e => WorkoutDto.From(
                e,
                kcalByEvent.TryGetValue(e.Id, out var k) ? k : null,
                distByEvent.TryGetValue(e.Id, out var d) ? d : null,
                hr, hrMax)));
        });

        return app;
    }

    /// <summary>Значение показателя сессии, связанное с событием тренировки.</summary>
    private static void AddSessionMetric(
        AppDbContext db, TrackedEvent ev, string code, double value, string unit)
    {
        var obs = new Observation
        {
            UserId = ev.UserId,
            MetricCode = code,
            ValueNum = value,
            Unit = unit,
            StartAt = ev.StartAt,
            EndAt = ev.EndAt,
            DeviceInstanceId = ev.DeviceInstanceId,
            ClientId = ev.ClientId is null ? null : $"{ev.ClientId}#{code}",
        };
        db.Observations.Add(obs);
        db.MeasurementEventLinks.Add(new MeasurementEventLink
        {
            MeasurementId = obs.Id,
            MeasurementType = "observation",
            EventId = ev.Id,
            LinkMethod = "source_explicit",
        });
    }

    /// <summary>Тип активности источника → код события нашего реестра.</summary>
    private static string ResolveEventType(
        string activityType, Dictionary<string, string> map, HashSet<string> known)
    {
        var lower = activityType.ToLowerInvariant();
        if (map.TryGetValue(lower, out var byExact) && known.Contains(byExact))
            return byExact;

        var candidate = $"workout.{lower}";
        if (known.Contains(candidate)) return candidate;

        foreach (var (source, code) in map)
        {
            if (source.EndsWith(lower) && known.Contains(code)) return code;
        }
        return "workout.unspecified";
    }
}
