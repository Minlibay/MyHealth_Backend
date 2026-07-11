using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using MyHealth.Api.Common;
using MyHealth.Api.Data;
using MyHealth.Api.Domain;

namespace MyHealth.Api.Features.Insights;

/// <summary>Персональная базовая линия показателя за 30 дней.</summary>
public record BaselineDto(
    MetricType Metric,
    double Avg,
    double StdDev,
    double? Current,
    double? DeviationPct);

/// <summary>Тренд показателя: среднее этой недели против прошлой.</summary>
public record TrendDto(
    MetricType Metric,
    double ThisWeekAvg,
    double LastWeekAvg,
    double ChangePct,
    string Direction); // up | down | flat

public record AnomalyDto(MetricType Metric, string Message, string Severity); // warn | alert

public record InsightsDto(
    DateTimeOffset At,
    int? HealthScore,
    int? SleepScore,
    int? ReadinessScore,
    List<BaselineDto> Baselines,
    List<TrendDto> Trends,
    List<AnomalyDto> Anomalies);

public static class InsightsEndpoints
{
    /// <summary>Показатели, по которым считаем базовые линии и тренды.</summary>
    private static readonly MetricType[] TrackedMetrics =
    [
        MetricType.RestingHeartRate,
        MetricType.HeartRate,
        MetricType.Hrv,
        MetricType.Sleep,
        MetricType.RespiratoryRate,
        MetricType.BodyTemperature,
        MetricType.Weight,
        MetricType.Steps,
        MetricType.ActiveEnergy,
    ];

    public static IEndpointRouteBuilder MapInsightEndpoints(this IEndpointRouteBuilder app)
    {
        // Инсайты считаются на лету из сохранённых данных — таблиц не требуется.
        app.MapGet("/api/insights", async (ClaimsPrincipal principal, AppDbContext db) =>
            {
                var userId = principal.GetUserId();
                if (userId is null) return Results.Unauthorized();

                var now = DateTimeOffset.UtcNow;
                var from = now.AddDays(-30);

                var samples = await db.Samples.AsNoTracking()
                    .Where(s => s.UserId == userId &&
                                s.RecordedAt >= from &&
                                TrackedMetrics.Contains(s.Metric))
                    .Select(s => new { s.Metric, s.Value, s.RecordedAt })
                    .ToListAsync();

                var lastSleep = await db.SleepSessions.AsNoTracking()
                    .Where(s => s.UserId == userId && s.EndedAt >= now.AddDays(-2))
                    .OrderByDescending(s => s.EndedAt)
                    .FirstOrDefaultAsync();

                var byMetric = samples
                    .GroupBy(s => s.Metric)
                    .ToDictionary(
                        g => g.Key,
                        g => g.OrderBy(s => s.RecordedAt)
                            .Select(s => new Pt(s.Value, s.RecordedAt))
                            .ToList());

                // --- Базовые линии: среднее и σ за 30 дней + текущее значение ---
                var baselines = new List<BaselineDto>();
                foreach (var (metric, list) in byMetric)
                {
                    if (list.Count < 3) continue;
                    var values = list.Select(s => s.Value).ToList();
                    var avg = values.Average();
                    var std = Math.Sqrt(values.Sum(v => (v - avg) * (v - avg)) / values.Count);
                    var latest = list[^1];
                    // «Текущим» считаем значение не старше 2 суток.
                    double? current = latest.RecordedAt >= now.AddDays(-2)
                        ? latest.Value
                        : null;
                    double? deviation = current is double c && avg != 0
                        ? (c - avg) / avg * 100
                        : null;
                    baselines.Add(new BaselineDto(
                        metric, Round(avg), Round(std), current, Round2(deviation)));
                }

                // --- Тренды: среднее за 7 дней против предыдущих 7 ---
                var trends = new List<TrendDto>();
                foreach (var (metric, list) in byMetric)
                {
                    var thisWeek = list.Where(s => s.RecordedAt >= now.AddDays(-7))
                        .Select(s => s.Value).ToList();
                    var lastWeek = list.Where(s => s.RecordedAt < now.AddDays(-7) &&
                                                   s.RecordedAt >= now.AddDays(-14))
                        .Select(s => s.Value).ToList();
                    if (thisWeek.Count < 2 || lastWeek.Count < 2) continue;
                    var a = thisWeek.Average();
                    var b = lastWeek.Average();
                    if (b == 0) continue;
                    var change = (a - b) / b * 100;
                    trends.Add(new TrendDto(
                        metric, Round(a), Round(b), Round(change),
                        Math.Abs(change) < 3 ? "flat" : change > 0 ? "up" : "down"));
                }

                // --- Скоры ---
                var sleepScore = SleepScore(byMetric, lastSleep, now);
                var readiness = ReadinessScore(baselines, sleepScore);
                int? health = null;
                if (sleepScore is int ss && readiness is int rs)
                {
                    // Активность: сегодняшние шаги против личного среднего.
                    var stepsBaseline = baselines.FirstOrDefault(
                        b => b.Metric == MetricType.Steps);
                    var activity = stepsBaseline?.Current is double cur &&
                                   stepsBaseline.Avg > 0
                        ? Clamp01(cur / stepsBaseline.Avg)
                        : 0.7; // нет данных — нейтрально
                    health = (int)Math.Round(0.4 * ss + 0.4 * rs + 20 * activity);
                }

                // --- Аномалии: отклонения от личной нормы ---
                var anomalies = Anomalies(baselines);

                return Results.Ok(new InsightsDto(
                    now, health, sleepScore, readiness,
                    baselines, trends, anomalies));
            })
            .WithTags("Insights")
            .RequireAuthorization();

        return app;
    }

    /// <summary>
    /// Оценка сна 0–100: длительность против цели 8 ч (60 баллов) + доли
    /// глубокого и REM-сна против целевых 20% (по 20 баллов). Без фаз —
    /// только длительность, растянутая на всю шкалу.
    /// </summary>
    private static int? SleepScore(
        Dictionary<MetricType, List<Pt>> byMetric,
        SleepSession? lastSession,
        DateTimeOffset now)
    {
        double? hours = null;
        double? deepShare = null, remShare = null;

        if (lastSession is not null)
        {
            var dto = Sleep.SleepSessionDto.From(lastSession);
            hours = dto.TotalHours;
            var total = dto.TotalHours <= 0 ? 1 : dto.TotalHours;
            if (dto.Stages.Count > 0)
            {
                deepShare = dto.DeepHours / total;
                remShare = dto.RemHours / total;
            }
        }
        else if (byMetric.TryGetValue(MetricType.Sleep, out var sleepSamples) &&
                 sleepSamples.Count > 0)
        {
            var latest = sleepSamples[^1];
            if (latest.RecordedAt >= now.AddDays(-2)) hours = latest.Value;
        }

        if (hours is not double h) return null;

        var durationPoints = Clamp01(h / 8) - (h > 10 ? 0.15 : 0); // пересып штрафуем
        if (deepShare is double d && remShare is double r)
        {
            return (int)Math.Round(
                60 * durationPoints + 20 * Clamp01(d / 0.2) + 20 * Clamp01(r / 0.2));
        }
        return (int)Math.Round(100 * durationPoints);
    }

    /// <summary>
    /// Готовность 0–100: пульс в покое ниже личной нормы — хорошо (35),
    /// HRV выше нормы — хорошо (35), сон (30). Недостающие компоненты
    /// перевзвешиваются на имеющиеся.
    /// </summary>
    private static int? ReadinessScore(List<BaselineDto> baselines, int? sleepScore)
    {
        var parts = new List<(double Weight, double Score)>();

        var rhr = baselines.FirstOrDefault(b =>
            b.Metric == MetricType.RestingHeartRate && b.DeviationPct is not null);
        if (rhr?.DeviationPct is double rhrDev)
            parts.Add((35, Clamp01(1 - rhrDev / 100 * 5)));

        var hrv = baselines.FirstOrDefault(b =>
            b.Metric == MetricType.Hrv && b.DeviationPct is not null);
        if (hrv?.DeviationPct is double hrvDev)
            parts.Add((35, hrvDev >= 0 ? 1 : Clamp01(1 + hrvDev / 100 * 5)));

        if (sleepScore is int ss)
            parts.Add((30, ss / 100.0));

        if (parts.Count == 0) return null;
        var totalWeight = parts.Sum(p => p.Weight);
        return (int)Math.Round(parts.Sum(p => p.Weight * p.Score) / totalWeight * 100);
    }

    private static List<AnomalyDto> Anomalies(List<BaselineDto> baselines)
    {
        var result = new List<AnomalyDto>();
        foreach (var b in baselines)
        {
            if (b.Current is not double current || b.StdDev <= 0) continue;
            var sigma = (current - b.Avg) / b.StdDev;

            switch (b.Metric)
            {
                case MetricType.RestingHeartRate when sigma > 2 && b.DeviationPct > 5:
                    result.Add(new AnomalyDto(b.Metric,
                        $"Пульс в покое ({current:0}) заметно выше вашей нормы ({b.Avg:0}).",
                        sigma > 3 ? "alert" : "warn"));
                    break;
                case MetricType.Hrv when sigma < -2:
                    result.Add(new AnomalyDto(b.Metric,
                        $"HRV ({current:0} мс) ниже вашей нормы ({b.Avg:0} мс) — возможно, организму нужно восстановление.",
                        "warn"));
                    break;
                case MetricType.BodyTemperature when current - b.Avg >= 0.4:
                    result.Add(new AnomalyDto(b.Metric,
                        $"Температура ({current:0.0}°C) выше вашей нормы ({b.Avg:0.0}°C).",
                        current - b.Avg >= 0.8 ? "alert" : "warn"));
                    break;
                case MetricType.RespiratoryRate when sigma > 2:
                    result.Add(new AnomalyDto(b.Metric,
                        $"Частота дыхания ({current:0}) выше вашей нормы ({b.Avg:0}).",
                        "warn"));
                    break;
                case MetricType.Sleep when current < 6:
                    result.Add(new AnomalyDto(b.Metric,
                        $"Прошлой ночью вы спали {current:0.0} ч — меньше рекомендуемого.",
                        "warn"));
                    break;
            }
        }
        return result;
    }

    private sealed record Pt(double Value, DateTimeOffset RecordedAt);

    private static double Clamp01(double v) => Math.Clamp(v, 0, 1);
    private static double Round(double v) => Math.Round(v, 1);
    private static double? Round2(double? v) => v is double d ? Math.Round(d, 1) : null;
}
