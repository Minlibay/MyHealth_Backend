using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MyHealth.Api.Common;
using MyHealth.Api.Data;
using MyHealth.Api.Domain;
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
        double hours(string stage) => stages
            .Where(x => x.Stage == stage)
            .Sum(x => (x.End - x.Start).TotalHours);
        var awake = hours("awake");
        return new SleepSessionDto(
            s.Id, s.StartedAt, s.EndedAt,
            (s.EndedAt - s.StartedAt).TotalHours - awake,
            hours("deep"), hours("light"), hours("rem"), awake,
            stages, s.Source);
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

        // Пакетная загрузка сессий сна. Идемпотентна по (UserId, ClientId).
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
            var existing = (await db.SleepSessions
                .Where(s => s.UserId == userId && s.ClientId != null &&
                            clientIds.Contains(s.ClientId))
                .Select(s => s.ClientId!)
                .ToListAsync()).ToHashSet();

            var inserted = 0;
            var seen = new HashSet<string>();
            foreach (var i in items)
            {
                if (i.ClientId is not null &&
                    (existing.Contains(i.ClientId) || !seen.Add(i.ClientId)))
                    continue;

                db.SleepSessions.Add(new SleepSession
                {
                    UserId = userId.Value,
                    StartedAt = i.StartedAt,
                    EndedAt = i.EndedAt,
                    StagesJson = JsonSerializer.Serialize(
                        i.Stages, SleepSessionDto.JsonOptions),
                    Source = i.Source,
                    ClientId = i.ClientId,
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

            var q = db.SleepSessions.AsNoTracking().Where(s => s.UserId == userId);
            if (from is not null) q = q.Where(s => s.StartedAt >= from);
            if (to is not null) q = q.Where(s => s.StartedAt <= to);

            var data = await q
                .OrderByDescending(s => s.StartedAt)
                .Take(Math.Clamp(limit, 1, 366))
                .ToListAsync();

            return Results.Ok(data.Select(SleepSessionDto.From));
        });

        return app;
    }
}
