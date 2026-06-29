using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using MyHealth.Api.Common;
using MyHealth.Api.Data;
using MyHealth.Api.Domain;

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

public static class MetricsEndpoints
{
    public static IEndpointRouteBuilder MapMetricEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/metrics")
            .WithTags("Metrics")
            .RequireAuthorization();

        // Пакетная загрузка измерений. Идемпотентна по (UserId, ClientId).
        group.MapPost("/", async (
            List<MetricSampleDto> items, ClaimsPrincipal principal, AppDbContext db) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();
            if (items.Count == 0) return Results.Ok(new UploadResult(0, 0));

            // Уже загруженные клиентские записи — чтобы не плодить дубли.
            var clientIds = items
                .Where(i => i.ClientId is not null)
                .Select(i => i.ClientId!)
                .ToList();
            var existing = await db.Samples
                .Where(s => s.UserId == userId && s.ClientId != null &&
                            clientIds.Contains(s.ClientId))
                .Select(s => s.ClientId!)
                .ToListAsync();
            var existingSet = existing.ToHashSet();

            var inserted = 0;
            var seen = new HashSet<string>();
            foreach (var i in items)
            {
                if (i.ClientId is not null &&
                    (existingSet.Contains(i.ClientId) || !seen.Add(i.ClientId)))
                    continue;

                db.Samples.Add(new HealthSample
                {
                    UserId = userId.Value,
                    Metric = i.Metric,
                    Value = i.Value,
                    Secondary = i.Secondary,
                    Unit = i.Unit,
                    RecordedAt = i.RecordedAt,
                    Source = i.Source,
                    ClientId = i.ClientId,
                });
                inserted++;
            }

            await db.SaveChangesAsync();
            return Results.Ok(new UploadResult(inserted, items.Count - inserted));
        });

        // История показателя за период.
        group.MapGet("/", async (
            ClaimsPrincipal principal, AppDbContext db,
            MetricType? metric, DateTimeOffset? from, DateTimeOffset? to,
            int limit = 500) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var q = db.Samples.AsNoTracking().Where(s => s.UserId == userId);
            if (metric is not null) q = q.Where(s => s.Metric == metric);
            if (from is not null) q = q.Where(s => s.RecordedAt >= from);
            if (to is not null) q = q.Where(s => s.RecordedAt <= to);

            var data = await q
                .OrderByDescending(s => s.RecordedAt)
                .Take(Math.Clamp(limit, 1, 5000))
                .ToListAsync();

            return Results.Ok(data);
        });

        // Последнее значение по каждому показателю.
        group.MapGet("/latest", async (ClaimsPrincipal principal, AppDbContext db) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var latest = await db.Samples.AsNoTracking()
                .Where(s => s.UserId == userId)
                .GroupBy(s => s.Metric)
                .Select(g => g.OrderByDescending(s => s.RecordedAt).First())
                .ToListAsync();

            return Results.Ok(latest);
        });

        return app;
    }
}
