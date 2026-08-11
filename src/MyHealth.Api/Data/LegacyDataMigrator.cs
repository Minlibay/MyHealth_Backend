using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MyHealth.Api.Domain;
using MyHealth.Api.Domain.Tracking;

namespace MyHealth.Api.Data;

/// <summary>
/// Переносит данные прежней схемы (Samples, Workouts, SleepSessions) в
/// новую модель трекинга: observations, events, device_instances.
/// Выполняется один раз при старте; повторный запуск ничего не дублирует
/// благодаря ClientId с префиксом legacy.
/// </summary>
public static class LegacyDataMigrator
{
    private const string Marker = "legacy:";

    public static async Task MigrateAsync(AppDbContext db, ILogger logger)
    {
        // Реестр должен содержать коды, которые использует совместимый API.
        await EnsureExtraMetricsAsync(db);
        await EnsureLegacyEventTypesAsync(db);
        await db.SaveChangesAsync();

        var migrated = await db.Observations
            .AnyAsync(o => o.ClientId != null && o.ClientId.StartsWith(Marker));
        if (migrated)
        {
            logger.LogInformation("Legacy data already migrated, skipping");
            return;
        }

        var samples = await MigrateSamplesAsync(db, logger);
        var events = await MigrateWorkoutsAsync(db, logger);
        var sleep = await MigrateSleepAsync(db, logger);
        if (samples + events + sleep > 0)
        {
            await db.SaveChangesAsync();
            logger.LogInformation(
                "Legacy migration done: {S} observations, {W} workouts, {Z} sleep",
                samples, events, sleep);
        }
    }

    /// <summary>Показатели, которых нет в исходном справочнике таблицы.</summary>
    private static async Task EnsureExtraMetricsAsync(AppDbContext db)
    {
        var known = await db.MetricDefinitions
            .Select(m => m.MetricCode).ToListAsync();
        var set = known.ToHashSet();
        foreach (var (code, name, domain, grain, trigger, derivation, valueType, unit)
                 in MetricCodeMap.Extra)
        {
            if (set.Contains(code)) continue;
            db.MetricDefinitions.Add(new MetricDefinition
            {
                MetricCode = code, Name = name, Domain = domain, Grain = grain,
                Trigger = trigger, Derivation = derivation,
                ValueType = valueType, Unit = unit,
            });
            set.Add(code);
        }
    }

    /// <summary>Типы событий для наших источников (сон и общий тип).</summary>
    private static async Task EnsureLegacyEventTypesAsync(AppDbContext db)
    {
        var needed = new (string Code, string Name, string Group)[]
        {
            ("sleep.main", "Основной сон", "Сон"),
            ("workout.unspecified", "Тренировка без подтипа", "Тренировка / спорт"),
        };
        var known = (await db.EventTypeDefinitions
            .Select(t => t.EventTypeCode).ToListAsync()).ToHashSet();
        foreach (var (code, name, group) in needed)
        {
            if (known.Contains(code)) continue;
            db.EventTypeDefinitions.Add(new EventTypeDefinition
            {
                EventTypeCode = code, Name = name, Group = group, Mvp = true,
            });
        }
    }

    // --- Samples → Observations ---

    private static async Task<int> MigrateSamplesAsync(AppDbContext db, ILogger logger)
    {
        var samples = await db.Samples.AsNoTracking().ToListAsync();
        if (samples.Count == 0) return 0;

        var devices = new DeviceResolver(db);
        var known = (await db.MetricDefinitions
            .Select(m => m.MetricCode).ToListAsync()).ToHashSet();

        var count = 0;
        foreach (var s in samples)
        {
            var code = MetricCodeMap.ToCode(s.Metric);
            if (!known.Contains(code)) continue;

            var device = await devices.ResolveAsync(s.UserId, s.Source);
            db.Observations.Add(new Observation
            {
                UserId = s.UserId,
                MetricCode = code,
                ValueNum = s.Value,
                ValueSecondary = s.Secondary,
                Unit = s.Unit,
                StartAt = s.RecordedAt,
                DeviceInstanceId = device?.Id,
                ClientId = $"{Marker}sample:{s.Id}",
                CreatedAt = s.CreatedAt,
            });
            count++;
        }
        logger.LogInformation("Legacy migration: {Count} samples", count);
        return count;
    }

    // --- Workouts → Events (+ метрики сессии) ---

    private static async Task<int> MigrateWorkoutsAsync(AppDbContext db, ILogger logger)
    {
        var workouts = await db.Workouts.AsNoTracking().ToListAsync();
        if (workouts.Count == 0) return 0;

        var devices = new DeviceResolver(db);
        var map = await db.SourceEventTypeMaps.AsNoTracking()
            .ToDictionaryAsync(m => m.SourceEventType.ToLowerInvariant(),
                m => m.EventTypeCode);
        var known = (await db.EventTypeDefinitions
            .Select(t => t.EventTypeCode).ToListAsync()).ToHashSet();

        foreach (var w in workouts)
        {
            var code = ResolveEventType(w.ActivityType, map, known);
            var device = await devices.ResolveAsync(w.UserId, w.Source);
            var ev = new TrackedEvent
            {
                UserId = w.UserId,
                EventTypeCode = code,
                EventName = w.ActivityType,
                StartAt = w.StartedAt,
                EndAt = w.EndedAt,
                SourceEventType = w.ActivityType,
                DeviceInstanceId = device?.Id,
                ClientId = $"{Marker}workout:{w.Id}",
                CreatedAt = w.CreatedAt,
            };
            db.Events.Add(ev);

            // Калории и дистанция сессии — как значения показателей.
            if (w.EnergyKcal is double kcal)
            {
                AddSessionObservation(db, ev, "activity.calories.session", kcal,
                    "ккал", w.UserId, device?.Id, $"{Marker}workout-kcal:{w.Id}");
            }
            if (w.DistanceMeters is double meters)
            {
                AddSessionObservation(db, ev, "activity.distance.session", meters,
                    "м", w.UserId, device?.Id, $"{Marker}workout-dist:{w.Id}");
            }
        }
        logger.LogInformation("Legacy migration: {Count} workouts", workouts.Count);
        return workouts.Count;
    }

    private static void AddSessionObservation(
        AppDbContext db, TrackedEvent ev, string code, double value, string unit,
        Guid userId, Guid? deviceId, string clientId)
    {
        var obs = new Observation
        {
            UserId = userId,
            MetricCode = code,
            ValueNum = value,
            Unit = unit,
            StartAt = ev.StartAt,
            EndAt = ev.EndAt,
            DeviceInstanceId = deviceId,
            ClientId = clientId,
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

    private static string ResolveEventType(
        string activityType, Dictionary<string, string> map, HashSet<string> known)
    {
        // Точное имя источника (HKWorkoutActivityType.running) или суффикс.
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

    // --- SleepSessions → Events + стадии как json-показатель ---

    private static async Task<int> MigrateSleepAsync(AppDbContext db, ILogger logger)
    {
        var sessions = await db.SleepSessions.AsNoTracking().ToListAsync();
        if (sessions.Count == 0) return 0;

        var devices = new DeviceResolver(db);
        foreach (var s in sessions)
        {
            var device = await devices.ResolveAsync(s.UserId, s.Source);
            var ev = new TrackedEvent
            {
                UserId = s.UserId,
                EventTypeCode = "sleep.main",
                EventName = "Основной сон",
                StartAt = s.StartedAt,
                EndAt = s.EndedAt,
                DeviceInstanceId = device?.Id,
                ClientId = $"{Marker}sleep:{s.Id}",
                CreatedAt = s.CreatedAt,
            };
            db.Events.Add(ev);

            var stages = new Observation
            {
                UserId = s.UserId,
                MetricCode = "sleep.stages",
                ValueJson = string.IsNullOrWhiteSpace(s.StagesJson) ? "[]" : s.StagesJson,
                StartAt = s.StartedAt,
                EndAt = s.EndedAt,
                DeviceInstanceId = device?.Id,
                ClientId = $"{Marker}sleep-stages:{s.Id}",
                CreatedAt = s.CreatedAt,
            };
            db.Observations.Add(stages);
            db.MeasurementEventLinks.Add(new MeasurementEventLink
            {
                MeasurementId = stages.Id,
                MeasurementType = "observation",
                EventId = ev.Id,
                LinkMethod = "source_explicit",
            });
        }
        logger.LogInformation("Legacy migration: {Count} sleep sessions", sessions.Count);
        return sessions.Count;
    }

    /// <summary>
    /// Разбирает прежнюю строку Source ("apple_health:JCVitalPro") в
    /// экземпляр устройства, переиспользуя уже созданные.
    /// </summary>
    private sealed class DeviceResolver(AppDbContext db)
    {
        private readonly Dictionary<(Guid, string), DeviceInstance> _cache = new();

        public async Task<DeviceInstance?> ResolveAsync(Guid userId, string? source)
        {
            if (string.IsNullOrWhiteSpace(source)) return null;
            var key = (userId, source);
            if (_cache.TryGetValue(key, out var cached)) return cached;

            var i = source.IndexOf(':');
            var platform = i < 0 ? source : source[..i];
            var app = i < 0 ? null : source[(i + 1)..];

            var device = await db.DeviceInstances.FirstOrDefaultAsync(d =>
                d.UserId == userId &&
                d.IntegrationPlatform == platform &&
                d.DataOriginAppId == app);
            if (device is null)
            {
                device = new DeviceInstance
                {
                    UserId = userId,
                    IntegrationPlatform = platform,
                    DataOriginAppId = app,
                    DeviceName = app,
                    DeviceType = GuessDeviceType(platform, app),
                };
                db.DeviceInstances.Add(device);
            }
            _cache[key] = device;
            return device;
        }

        private static string GuessDeviceType(string platform, string? app)
        {
            if (platform == "ring") return "ring";
            if (platform == "manual") return "other";
            var a = app?.ToLowerInvariant() ?? "";
            if (a.Contains("watch")) return "watch";
            if (a.Contains("ring") || a.Contains("jcvital")) return "ring";
            if (a.Contains("iphone") || a.Contains("phone")) return "phone";
            return "unknown";
        }
    }

    /// <summary>Опции сериализации для JSON-значений (стадии сна и т.п.).</summary>
    public static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web);
}
