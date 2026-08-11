using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MyHealth.Api.Common;
using MyHealth.Api.Data;
using MyHealth.Api.Domain;
using MyHealth.Api.Domain.Tracking;
using MyHealth.Api.Features.Metrics;

namespace MyHealth.Api.Features.Sleep;

public record SleepStageDto(string Stage, DateTimeOffset Start, DateTimeOffset End);

public record SleepSessionUploadDto(
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    List<SleepStageDto> Stages,
    string? Source,
    string? ClientId);

public record SleepSessionDto(
    Guid Id,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    double TotalHours,
    double DeepHours,
    double LightHours,
    double RemHours,
    double AwakeHours,
    List<SleepStageDto> Stages,
    string? Source)
{
    public static SleepSessionDto From(SleepSession s)
    {
        var stages = JsonSerializer.Deserialize<List<SleepStageDto>>(
            s.StagesJson, JsonOptions) ?? [];
        return Build(s.Id, s.StartedAt, s.EndedAt, stages, s.Source);
    }

    /// <summary>Событие сна новой схемы + стадии из json-показателя.</summary>
    public static SleepSessionDto FromEvent(TrackedEvent ev, string? stagesJson)
    {
        var stages = string.IsNullOrWhiteSpace(stagesJson)
            ? []
            : JsonSerializer.Deserialize<List<SleepStageDto>>(stagesJson, JsonOptions)
              ?? [];
        return Build(ev.Id, ev.StartAt, ev.EndAt ?? ev.StartAt, stages,
            TrackingStore.SourceString(ev.DeviceInstance));
    }

    private static SleepSessionDto Build(
        Guid id, DateTimeOffset startedAt, DateTimeOffset endedAt,
        List<SleepStageDto> stages, string? source)
    {
        double hours(string stage) => stages
            .Where(x => x.Stage == stage)
            .Sum(x => (x.End - x.Start).TotalHours);
        var awake = hours("awake");
        return new SleepSessionDto(
            id, startedAt, endedAt,
            (endedAt - startedAt).TotalHours - awake,
            hours("deep"), hours("light"), hours("rem"), awake,
            stages, source);
    }

    public static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
}

public static class SleepEndpoints
{
    public static IEndpointRouteBuilder MapSleepEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sleep")
            .WithTags("Sleep")
            .RequireAuthorization();

        // Пакетная загрузка сессий сна: событие sleep.main + стадии как
        // json-показатель sleep.stages, связанные через junction.
        group.MapPost("/", async (
            List<SleepSessionUploadDto> items, ClaimsPrincipal principal, AppDbContext db) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();
            if (items.Count == 0) return Results.Ok(new UploadResult(0, 0));

            var clientIds = items
                .Where(i => i.ClientId is not null)
                .Select(i => i.ClientId!)
                .ToList();
            var existing = (await db.Events
                .Where(e => e.UserId == userId && e.ClientId != null &&
                            clientIds.Contains(e.ClientId))
                .Select(e => e.ClientId!)
                .ToListAsync()).ToHashSet();

            var store = new TrackingStore(db);
            var inserted = 0;
            var seen = new HashSet<string>();
            foreach (var i in items)
            {
                if (i.ClientId is not null &&
                    (existing.Contains(i.ClientId) || !seen.Add(i.ClientId)))
                    continue;

                var device = await store.ResolveDeviceAsync(userId.Value, i.Source);
                var ev = new TrackedEvent
                {
                    UserId = userId.Value,
                    EventTypeCode = "sleep.main",
                    EventName = "Основной сон",
                    StartAt = i.StartedAt,
                    EndAt = i.EndedAt,
                    DeviceInstanceId = device?.Id,
                    ClientId = i.ClientId,
                };
                db.Events.Add(ev);

                var stages = new Observation
                {
                    UserId = userId.Value,
                    MetricCode = "sleep.stages",
                    ValueJson = JsonSerializer.Serialize(
                        i.Stages, SleepSessionDto.JsonOptions),
                    StartAt = i.StartedAt,
                    EndAt = i.EndedAt,
                    DeviceInstanceId = device?.Id,
                    ClientId = i.ClientId is null ? null : $"{i.ClientId}#stages",
                };
                db.Observations.Add(stages);
                db.MeasurementEventLinks.Add(new MeasurementEventLink
                {
                    MeasurementId = stages.Id,
                    MeasurementType = "observation",
                    EventId = ev.Id,
                    LinkMethod = "source_explicit",
                });
                inserted++;
            }

            await db.SaveChangesAsync();
            return Results.Ok(new UploadResult(inserted, items.Count - inserted));
        });

        // Сессии сна за период (новые первыми), с разбивкой по фазам.
        group.MapGet("/", async (
            ClaimsPrincipal principal, AppDbContext db,
            DateTimeOffset? from, DateTimeOffset? to, int limit = 60) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var q = db.Events.AsNoTracking()
                .Include(e => e.DeviceInstance)
                .Where(e => e.UserId == userId &&
                            e.EventTypeCode.StartsWith("sleep."));
            if (from is not null) q = q.Where(e => e.StartAt >= from);
            if (to is not null) q = q.Where(e => e.StartAt <= to);

            var data = await q
                .OrderByDescending(e => e.StartAt)
                .Take(Math.Clamp(limit, 1, 366))
                .ToListAsync();
            if (data.Count == 0) return Results.Ok(Enumerable.Empty<SleepSessionDto>());

            var stagesByEvent = await LoadStagesAsync(db, data.Select(e => e.Id));
            return Results.Ok(data.Select(e => SleepSessionDto.FromEvent(
                e, stagesByEvent.TryGetValue(e.Id, out var json) ? json : null)));
        });

        return app;
    }

    /// <summary>Стадии сна (json-показатель) по идентификаторам событий.</summary>
    internal static async Task<Dictionary<Guid, string>> LoadStagesAsync(
        AppDbContext db, IEnumerable<Guid> eventIds)
    {
        var ids = eventIds.ToList();
        var links = await db.MeasurementEventLinks.AsNoTracking()
            .Where(l => ids.Contains(l.EventId) && l.MeasurementType == "observation")
            .Select(l => new { l.EventId, l.MeasurementId })
            .ToListAsync();
        if (links.Count == 0) return [];

        var obsIds = links.Select(l => l.MeasurementId).ToList();
        var stages = (await db.Observations.AsNoTracking()
                .Where(o => obsIds.Contains(o.Id) && o.MetricCode == "sleep.stages")
                .Select(o => new { o.Id, o.ValueJson })
                .ToListAsync())
            .ToDictionary(o => o.Id, o => o.ValueJson);

        var result = new Dictionary<Guid, string>();
        foreach (var l in links)
        {
            if (stages.TryGetValue(l.MeasurementId, out var json) && json is not null)
                result[l.EventId] = json;
        }
        return result;
    }
}
