using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using MyHealth.Api.Common;
using MyHealth.Api.Data;
using MyHealth.Api.Domain;
using MyHealth.Api.Domain.Tracking;
using MyHealth.Api.Features.Evaluation;

namespace MyHealth.Api.Features.Metrics;

public record MetricSampleDto(
    MetricType Metric,
    double Value,
    double? Secondary,
    string? Unit,
    DateTimeOffset RecordedAt,
    string? Source,
    string? ClientId);

public record UploadResult(int Inserted, int Skipped);

/// <summary>Запись с рассчитанными на сервере полями (статус, форматирование).</summary>
public record SampleDto(
    Guid Id,
    MetricType Metric,
    double Value,
    double? Secondary,
    string? Unit,
    DateTimeOffset RecordedAt,
    string? Source,
    string DisplayValue,
    HealthStatus Status,
    string StatusLabel)
{
    public static SampleDto From(HealthSample s)
    {
        var status = MetricEvaluator.Evaluate(s.Metric, s.Value, s.Secondary);
        return new SampleDto(
            s.Id, s.Metric, s.Value, s.Secondary, s.Unit, s.RecordedAt, s.Source,
            MetricEvaluator.Format(s.Metric, s.Value, s.Secondary),
            status, MetricEvaluator.Label(status));
    }

    /// <summary>Запись новой схемы в прежнем формате ответа.</summary>
    public static SampleDto? FromObservation(Observation o)
    {
        var metric = MetricCodeMap.FromCode(o.MetricCode);
        if (metric is null || o.ValueNum is null) return null;
        var value = o.ValueNum.Value;
        var status = MetricEvaluator.Evaluate(metric.Value, value, o.ValueSecondary);
        return new SampleDto(
            o.Id, metric.Value, value, o.ValueSecondary, o.Unit, o.StartAt,
            TrackingStore.SourceString(o.DeviceInstance),
            MetricEvaluator.Format(metric.Value, value, o.ValueSecondary),
            status, MetricEvaluator.Label(status));
    }
}

public record MetricStatsDto(
    MetricType Metric, int Count, double Avg, double Min, double Max, double Latest,
    HealthStatus LatestStatus);

public static class MetricsEndpoints
{
    /// <summary>
    /// Приведение единиц на приёме: HealthKit отдаёт проценты долей
    /// (0.9 = 90%), поэтому долевые значения домножаем на 100.
    /// </summary>
    private static double Normalize(MetricType metric, double value) =>
        metric is MetricType.BloodOxygen or MetricType.BodyFat && value > 0 && value <= 1
            ? value * 100
            : value;

    public static IEndpointRouteBuilder MapMetricEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/metrics")
            .WithTags("Metrics")
            .RequireAuthorization();

        // Пакетная загрузка измерений в observations новой схемы.
        // Контракт запроса не менялся — приложение присылает enum MetricType.
        group.MapPost("/", async (
            List<MetricSampleDto> items, ClaimsPrincipal principal, AppDbContext db) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();
            if (items.Count == 0) return Results.Ok(new UploadResult(0, 0));

            var clientIds = items
                .Where(i => i.ClientId is not null)
                .Select(i => i.ClientId!)
                .ToList();
            var existingSet = (await db.Observations
                .Where(o => o.UserId == userId && o.ClientId != null &&
                            clientIds.Contains(o.ClientId))
                .Select(o => o.ClientId!)
                .ToListAsync()).ToHashSet();

            var knownCodes = (await db.MetricDefinitions
                .Select(m => m.MetricCode).ToListAsync()).ToHashSet();
            var store = new TrackingStore(db);

            var inserted = 0;
            var seen = new HashSet<string>();
            foreach (var i in items)
            {
                if (i.ClientId is not null &&
                    (existingSet.Contains(i.ClientId) || !seen.Add(i.ClientId)))
                    continue;

                var code = MetricCodeMap.ToCode(i.Metric);
                if (!knownCodes.Contains(code)) continue;

                var device = await store.ResolveDeviceAsync(userId.Value, i.Source);
                db.Observations.Add(new Observation
                {
                    UserId = userId.Value,
                    MetricCode = code,
                    ValueNum = Normalize(i.Metric, i.Value),
                    ValueSecondary = i.Secondary,
                    Unit = i.Unit,
                    StartAt = i.RecordedAt,
                    DeviceInstanceId = device?.Id,
                    ClientId = i.ClientId,
                });
                inserted++;
            }

            await db.SaveChangesAsync();
            return Results.Ok(new UploadResult(inserted, items.Count - inserted));
        });

        // История показателя за период (со статусом, рассчитанным на сервере).
        group.MapGet("/", async (
            ClaimsPrincipal principal, AppDbContext db,
            MetricType? metric, DateTimeOffset? from, DateTimeOffset? to,
            int limit = 500) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var store = new TrackingStore(db);
            var data = await store.Query(userId.Value, metric, from, to)
                .OrderByDescending(o => o.StartAt)
                .Take(Math.Clamp(limit, 1, 5000))
                .ToListAsync();

            return Results.Ok(data.Select(SampleDto.FromObservation)
                .Where(d => d is not null));
        });

        // Последнее значение по каждому показателю (со статусом).
        group.MapGet("/latest", async (ClaimsPrincipal principal, AppDbContext db) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var store = new TrackingStore(db);
            var latest = await store.Query(userId.Value, null, null, null)
                .GroupBy(o => o.MetricCode)
                .Select(g => g.OrderByDescending(o => o.StartAt).First())
                .ToListAsync();

            return Results.Ok(latest.Select(SampleDto.FromObservation)
                .Where(d => d is not null));
        });

        // Статистика по показателю за период: среднее/мин/макс/кол-во + статус.
        group.MapGet("/stats", async (
            ClaimsPrincipal principal, AppDbContext db,
            MetricType? metric, DateTimeOffset? from, DateTimeOffset? to) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var store = new TrackingStore(db);
            var agg = await store.Query(userId.Value, metric, from, to)
                .Where(o => o.ValueNum != null)
                .GroupBy(o => o.MetricCode)
                .Select(g => new
                {
                    MetricCode = g.Key,
                    Count = g.Count(),
                    Avg = g.Average(x => x.ValueNum!.Value),
                    Min = g.Min(x => x.ValueNum!.Value),
                    Max = g.Max(x => x.ValueNum!.Value),
                    Latest = g.OrderByDescending(x => x.StartAt).First().ValueNum!.Value,
                    LatestSecondary =
                        g.OrderByDescending(x => x.StartAt).First().ValueSecondary,
                })
                .ToListAsync();

            var result = agg
                .Select(a => (Metric: MetricCodeMap.FromCode(a.MetricCode), Agg: a))
                .Where(x => x.Metric is not null)
                .Select(x => new MetricStatsDto(
                    x.Metric!.Value, x.Agg.Count, x.Agg.Avg, x.Agg.Min, x.Agg.Max,
                    x.Agg.Latest,
                    MetricEvaluator.Evaluate(
                        x.Metric.Value, x.Agg.Latest, x.Agg.LatestSecondary)));

            return Results.Ok(result);
        });

        return app;
    }
}
