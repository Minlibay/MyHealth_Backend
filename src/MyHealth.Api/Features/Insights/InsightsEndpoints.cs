using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using MyHealth.Api.Common;
using MyHealth.Api.Data;
using MyHealth.Api.Domain;
using MyHealth.Api.Features.Sleep;

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

/// <summary>Фактор, из которого сложился скор (для объяснения «почему»).</summary>
public record ScoreFactorDto(string Name, string Detail, string Impact); // positive | negative | neutral

/// <summary>Один скор 0–100 с разбивкой по факторам.</summary>
public record ScoreDto(string Key, string Title, int Value, List<ScoreFactorDto> Factors);

/// <summary>Тренировочная нагрузка: острая (7 дней) против хронической (28).</summary>
public record TrainingLoadDto(
    double AcuteLoad,
    double ChronicLoad,
    double? Ratio,
    string Status); // low | optimal | high | risky | unknown

/// <summary>Ночные показатели, рассчитанные из последней сессии сна.</summary>
public record NightVitalsDto(
    double? RestingHr,
    double? RestingHrBaseline,
    double? Spo2Min,
    int? Spo2Dips,
    double? SleepRegularityMinutes);

/// <summary>Точка почасового стресс-таймлайна.</summary>
public record StressPointDto(DateTimeOffset At, int Value);

/// <summary>Недельный отчёт: эта неделя против прошлой.</summary>
public record WeeklyReportDto(
    DateTimeOffset From,
    DateTimeOffset To,
    List<TrendDto> Trends,
    int WorkoutsThisWeek,
    int WorkoutsLastWeek,
    double TrimpThisWeek,
    double TrimpLastWeek);

public record InsightsDto(
    DateTimeOffset At,
    int? HealthScore,
    int? SleepScore,
    int? ReadinessScore,
    List<ScoreDto> Scores,
    TrainingLoadDto TrainingLoad,
    NightVitalsDto Night,
    double? Vo2Max,
    List<StressPointDto> StressTimeline,
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
        MetricType.Water,
        MetricType.DietaryEnergy,
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

                // Персональные цели из профиля (null → значения по умолчанию).
                var profile = await db.Users.AsNoTracking()
                    .Where(u => u.Id == userId)
                    .Select(u => new Goals(
                        u.SleepGoalHours ?? 8,
                        u.WaterGoalLiters ?? 2,
                        u.KcalGoal))
                    .FirstOrDefaultAsync() ?? new Goals(8, 2, null);

                var samples = await db.Samples.AsNoTracking()
                    .Where(s => s.UserId == userId &&
                                s.RecordedAt >= from &&
                                TrackedMetrics.Contains(s.Metric))
                    .Select(s => new Pt(s.Metric, s.Value, s.RecordedAt))
                    .ToListAsync();

                // Сессии сна за 30 дней: последняя ночь + регулярность +
                // база ночного пульса покоя.
                var sleepSessions = await db.SleepSessions.AsNoTracking()
                    .Where(s => s.UserId == userId && s.EndedAt >= from)
                    .OrderByDescending(s => s.EndedAt)
                    .ToListAsync();
                var lastSleep = sleepSessions.FirstOrDefault(
                    s => s.EndedAt >= now.AddDays(-2));

                // Тренировки за 28 дней — для Strain и тренировочной нагрузки.
                var workouts = await db.Workouts.AsNoTracking()
                    .Where(w => w.UserId == userId && w.StartedAt >= now.AddDays(-28))
                    .Select(w => new WorkoutPt(w.StartedAt, w.EndedAt, w.EnergyKcal))
                    .ToListAsync();

                var byMetric = samples
                    .GroupBy(s => s.Metric)
                    .ToDictionary(
                        g => g.Key,
                        g => g.OrderBy(s => s.RecordedAt).ToList());

                var baselines = ComputeBaselines(byMetric, now);
                var trends = ComputeTrends(byMetric, now);
                var trainingLoad = ComputeTrainingLoad(workouts, now);
                var night = ComputeNightVitals(byMetric, sleepSessions, now);
                var stressTimeline = ComputeStressTimeline(byMetric, night, now);

                var ctx = new ScoreContext(
                    byMetric, baselines, lastSleep, workouts, now, profile, night);

                // VO2max по Уту—Соренсену: 15.3 × HRmax / пульс покоя.
                // Ночной пульс покоя точнее записей из хранилища.
                double? vo2max = null;
                var age = await db.Users.AsNoTracking()
                    .Where(u => u.Id == userId)
                    .Select(u => u.Age)
                    .FirstOrDefaultAsync();
                var restingHr = night.RestingHrBaseline ?? night.RestingHr ??
                    ctx.Baseline(MetricType.RestingHeartRate)?.Avg;
                if (restingHr is double rhr and > 25)
                {
                    var hrMax = age is int a ? 220.0 - a : 190.0;
                    vo2max = Math.Round(15.3 * hrMax / rhr, 1);
                }
                var scores = new List<ScoreDto>();
                void add(ScoreDto? s)
                {
                    if (s is not null) scores.Add(s);
                }

                var sleep = SleepScore(ctx);
                add(sleep);
                var sleepStress = SleepStressScore(ctx);
                add(sleepStress);
                var stress = StressScore(ctx);
                add(stress);
                add(InactiveStressScore(ctx, stress));
                var strain = StrainScore(ctx, trainingLoad);
                add(strain);
                var recovery = RecoveryScore(ctx, sleep, sleepStress);
                add(recovery);
                add(EnergyReserveScore(ctx, recovery, strain));
                add(NutritionScore(ctx));

                // Сводный Health Score — среднее ключевых скоров.
                int? health = null;
                var core = scores
                    .Where(s => s.Key is "recovery" or "sleep" or "strain" or "stress")
                    .Select(s => s.Key == "strain" || s.Key == "stress"
                        // Для strain/stress «хорошо» — умеренные значения.
                        ? 100 - Math.Abs(s.Value - 45)
                        : s.Value)
                    .ToList();
                if (core.Count >= 2) health = (int)Math.Round(core.Average());

                var anomalies = Anomalies(baselines);

                return Results.Ok(new InsightsDto(
                    now,
                    health,
                    sleep?.Value,
                    recovery?.Value,
                    scores,
                    trainingLoad,
                    night,
                    vo2max,
                    stressTimeline,
                    baselines,
                    trends,
                    anomalies));
            })
            .WithTags("Insights")
            .RequireAuthorization();

        // Недельный отчёт: тренды + тренировки этой недели против прошлой.
        app.MapGet("/api/insights/weekly", async (
                ClaimsPrincipal principal, AppDbContext db) =>
            {
                var userId = principal.GetUserId();
                if (userId is null) return Results.Unauthorized();

                var now = DateTimeOffset.UtcNow;
                var samples = await db.Samples.AsNoTracking()
                    .Where(s => s.UserId == userId &&
                                s.RecordedAt >= now.AddDays(-14) &&
                                TrackedMetrics.Contains(s.Metric))
                    .Select(s => new Pt(s.Metric, s.Value, s.RecordedAt))
                    .ToListAsync();
                var byMetric = samples
                    .GroupBy(s => s.Metric)
                    .ToDictionary(g => g.Key,
                        g => g.OrderBy(s => s.RecordedAt).ToList());

                var workouts = await db.Workouts.AsNoTracking()
                    .Where(w => w.UserId == userId &&
                                w.StartedAt >= now.AddDays(-14))
                    .Select(w => new WorkoutPt(w.StartedAt, w.EndedAt, w.EnergyKcal))
                    .ToListAsync();
                var thisWeek = workouts.Where(w => w.Start >= now.AddDays(-7)).ToList();
                var lastWeek = workouts.Where(w => w.Start < now.AddDays(-7)).ToList();

                return Results.Ok(new WeeklyReportDto(
                    now.AddDays(-7),
                    now,
                    ComputeTrends(byMetric, now),
                    thisWeek.Count,
                    lastWeek.Count,
                    Round(thisWeek.Sum(EstimateTrimp)),
                    Round(lastWeek.Sum(EstimateTrimp))));
            })
            .WithTags("Insights")
            .RequireAuthorization();

        return app;
    }

    // ===== Вспомогательные структуры =====

    private sealed record Pt(MetricType Metric, double Value, DateTimeOffset RecordedAt);
    private sealed record WorkoutPt(DateTimeOffset Start, DateTimeOffset End, double? EnergyKcal);

    /// <summary>Цели из профиля пользователя (с дефолтами).</summary>
    private sealed record Goals(double SleepHours, double WaterLiters, int? Kcal);

    private sealed record ScoreContext(
        Dictionary<MetricType, List<Pt>> ByMetric,
        List<BaselineDto> Baselines,
        SleepSession? LastSleep,
        List<WorkoutPt> Workouts,
        DateTimeOffset Now,
        Goals Goals,
        NightVitalsDto Night)
    {
        public BaselineDto? Baseline(MetricType m) =>
            Baselines.FirstOrDefault(b => b.Metric == m);

        /// <summary>Отклонение текущего значения от личной нормы, %.</summary>
        public double? Deviation(MetricType m) => Baseline(m)?.DeviationPct;

        public List<Pt> Of(MetricType m) =>
            ByMetric.TryGetValue(m, out var list) ? list : [];

        /// <summary>Сумма значений за календарные сутки [Now-1d..Now].</summary>
        public double DaySum(MetricType m) => Of(m)
            .Where(p => p.RecordedAt >= Now.AddDays(-1))
            .Sum(p => p.Value);
    }

    // ===== Ночные показатели =====

    /// <summary>5-й перцентиль (устойчив к выбросам, в отличие от минимума).</summary>
    private static double? Percentile5(List<double> values)
    {
        if (values.Count < 5) return null;
        var sorted = values.OrderBy(v => v).ToList();
        return sorted[(int)(sorted.Count * 0.05)];
    }

    private static List<double> SamplesInWindow(
        Dictionary<MetricType, List<Pt>> byMetric, MetricType metric,
        DateTimeOffset from, DateTimeOffset to)
    {
        return byMetric.TryGetValue(metric, out var list)
            ? list.Where(p => p.RecordedAt >= from && p.RecordedAt <= to)
                .Select(p => p.Value)
                .ToList()
            : [];
    }

    /// <summary>
    /// Ночные показатели: пульс покоя (5-й перцентиль пульса во сне —
    /// точнее записей RESTING_HEART_RATE), минимум SpO₂ и число «провалов»
    /// ниже 90%, регулярность отбоя за 14 дней.
    /// </summary>
    private static NightVitalsDto ComputeNightVitals(
        Dictionary<MetricType, List<Pt>> byMetric,
        List<SleepSession> sessions,
        DateTimeOffset now)
    {
        var last = sessions.FirstOrDefault(s => s.EndedAt >= now.AddDays(-2));

        double? restingHr = null;
        double? spo2Min = null;
        int? spo2Dips = null;
        if (last is not null)
        {
            restingHr = Percentile5(SamplesInWindow(
                byMetric, MetricType.HeartRate, last.StartedAt, last.EndedAt));
            var spo2 = SamplesInWindow(
                byMetric, MetricType.BloodOxygen, last.StartedAt, last.EndedAt);
            if (spo2.Count > 0)
            {
                spo2Min = spo2.Min();
                spo2Dips = spo2.Count(v => v < 90);
            }
        }

        // База ночного пульса покоя: перцентиль по каждой ночи за 30 дней.
        var nightly = sessions
            .Select(s => Percentile5(SamplesInWindow(
                byMetric, MetricType.HeartRate, s.StartedAt, s.EndedAt)))
            .OfType<double>()
            .ToList();
        double? rhrBaseline = nightly.Count >= 3 ? nightly.Average() : null;

        // Регулярность: σ времени отбоя за 14 дней. Минуты от 18:00,
        // чтобы полночь не рвала распределение.
        double? regularity = null;
        var bedtimes = sessions
            .Where(s => s.StartedAt >= now.AddDays(-14))
            .Select(s =>
            {
                var local = s.StartedAt.ToUniversalTime();
                var minutes = (local.Hour * 60 + local.Minute + 1440 - 18 * 60) % 1440;
                return (double)minutes;
            })
            .ToList();
        if (bedtimes.Count >= 4)
        {
            var avg = bedtimes.Average();
            regularity = Math.Round(
                Math.Sqrt(bedtimes.Sum(v => (v - avg) * (v - avg)) / bedtimes.Count), 0);
        }

        return new NightVitalsDto(
            restingHr is double r ? Math.Round(r) : null,
            rhrBaseline is double b ? Math.Round(b) : null,
            spo2Min,
            spo2Dips,
            regularity);
    }

    /// <summary>
    /// Почасовой стресс за последние сутки: превышение среднего пульса часа
    /// над ночным пульсом покоя. Грубая, но честная оценка без лаборатории.
    /// </summary>
    private static List<StressPointDto> ComputeStressTimeline(
        Dictionary<MetricType, List<Pt>> byMetric,
        NightVitalsDto night,
        DateTimeOffset now)
    {
        var rhr = night.RestingHrBaseline ?? night.RestingHr;
        if (rhr is not double baseline || baseline < 25) return [];

        var result = new List<StressPointDto>();
        for (var h = 24; h >= 1; h--)
        {
            var to = now.AddHours(-h + 1);
            var hr = SamplesInWindow(
                byMetric, MetricType.HeartRate, now.AddHours(-h), to);
            if (hr.Count == 0) continue;
            var elevation = (hr.Average() - baseline) / baseline;
            var stress = (int)Math.Clamp(elevation * 250, 0, 100);
            result.Add(new StressPointDto(to, stress));
        }
        return result;
    }

    // ===== Базовые линии, тренды, нагрузка =====

    private static List<BaselineDto> ComputeBaselines(
        Dictionary<MetricType, List<Pt>> byMetric, DateTimeOffset now)
    {
        var result = new List<BaselineDto>();
        foreach (var (metric, list) in byMetric)
        {
            if (list.Count < 3) continue;
            var values = list.Select(s => s.Value).ToList();
            var avg = values.Average();
            var std = Math.Sqrt(values.Sum(v => (v - avg) * (v - avg)) / values.Count);
            var latest = list[^1];
            double? current = latest.RecordedAt >= now.AddDays(-2) ? latest.Value : null;
            double? deviation = current is double c && avg != 0 ? (c - avg) / avg * 100 : null;
            result.Add(new BaselineDto(metric, Round(avg), Round(std), current, Round2(deviation)));
        }
        return result;
    }

    private static List<TrendDto> ComputeTrends(
        Dictionary<MetricType, List<Pt>> byMetric, DateTimeOffset now)
    {
        var result = new List<TrendDto>();
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
            result.Add(new TrendDto(metric, Round(a), Round(b), Round(change),
                Math.Abs(change) < 3 ? "flat" : change > 0 ? "up" : "down"));
        }
        return result;
    }

    /// <summary>
    /// TRIMP тренировки без пульса оцениваем из калорий (~10 ккал = 1 TRIMP)
    /// либо из длительности. Точный расчёт по зонам — в WorkoutsEndpoints,
    /// здесь важен относительный объём.
    /// </summary>
    private static double EstimateTrimp(WorkoutPt w)
    {
        if (w.EnergyKcal is double kcal && kcal > 0) return kcal / 10;
        return (w.End - w.Start).TotalMinutes * 0.7;
    }

    private static TrainingLoadDto ComputeTrainingLoad(
        List<WorkoutPt> workouts, DateTimeOffset now)
    {
        var acute = workouts
            .Where(w => w.Start >= now.AddDays(-7))
            .Sum(EstimateTrimp);
        var chronicTotal = workouts.Sum(EstimateTrimp);
        var chronic = chronicTotal / 4; // среднее за неделю по 28 дням

        double? ratio = chronic > 10 ? acute / chronic : null;
        var status = ratio switch
        {
            null => "unknown",
            < 0.8 => "low",
            <= 1.3 => "optimal",
            <= 1.5 => "high",
            _ => "risky",
        };
        return new TrainingLoadDto(Round(acute), Round(chronic), Round2(ratio), status);
    }

    // ===== Скоры =====

    private static ScoreFactorDto F(string name, string detail, double impact) =>
        new(name, detail, impact > 0.05 ? "positive" : impact < -0.05 ? "negative" : "neutral");

    /// <summary>Сон: длительность (60) + глубокая/REM-доли (по 20). Без фаз — только длительность.</summary>
    private static ScoreDto? SleepScore(ScoreContext ctx)
    {
        double? hours = null;
        double? deepShare = null, remShare = null;

        if (ctx.LastSleep is not null)
        {
            var dto = SleepSessionDto.From(ctx.LastSleep);
            hours = dto.TotalHours;
            var total = dto.TotalHours <= 0 ? 1 : dto.TotalHours;
            if (dto.Stages.Count > 0)
            {
                deepShare = dto.DeepHours / total;
                remShare = dto.RemHours / total;
            }
        }
        else
        {
            var sleepSamples = ctx.Of(MetricType.Sleep);
            if (sleepSamples.Count > 0 &&
                sleepSamples[^1].RecordedAt >= ctx.Now.AddDays(-2))
                hours = sleepSamples[^1].Value;
        }

        if (hours is not double h) return null;

        var goal = ctx.Goals.SleepHours;
        var over = goal + 2; // «пересып» — на два часа больше цели
        var factors = new List<ScoreFactorDto>
        {
            F("Длительность", $"{h:0.0} ч из целевых {goal:0.#}",
                Clamp01(h / goal) - 0.85),
        };
        if (h > over) factors.Add(F("Пересып", $"{h:0.0} ч — больше {over:0.#}", -0.2));

        int value;
        var durationPoints = Clamp01(h / goal) - (h > over ? 0.15 : 0);
        if (deepShare is double d && remShare is double r)
        {
            factors.Add(F("Глубокий сон", $"{d * 100:0}% при целевых 20%", Clamp01(d / 0.2) - 0.8));
            factors.Add(F("REM-сон", $"{r * 100:0}% при целевых 20%", Clamp01(r / 0.2) - 0.8));
            value = (int)Math.Round(
                60 * durationPoints + 20 * Clamp01(d / 0.2) + 20 * Clamp01(r / 0.2));
        }
        else
        {
            value = (int)Math.Round(100 * durationPoints);
        }

        // Регулярность: стабильный отбой (±30 мин) — бонус, разброс
        // больше двух часов — штраф (социальный джетлаг).
        if (ctx.Night.SleepRegularityMinutes is double sigma)
        {
            var regularity = Clamp01(1 - (sigma - 30) / 90); // 30мин→1, 120мин→0
            value += (int)Math.Round((regularity - 0.5) * 16);
            factors.Add(F("Регулярность",
                $"Разброс отбоя ±{sigma:0} мин за 14 дней", regularity - 0.5));
        }

        return new ScoreDto("sleep", "Сон", Math.Clamp(value, 0, 100), factors);
    }

    /// <summary>
    /// Ночной стресс: пульс во сне против дневной нормы пульса покоя,
    /// доля бодрствования внутри сессии. Выше — хуже (это «стресс»).
    /// </summary>
    private static ScoreDto? SleepStressScore(ScoreContext ctx)
    {
        if (ctx.LastSleep is null) return null;
        var dto = SleepSessionDto.From(ctx.LastSleep);

        var nightHr = ctx.Of(MetricType.HeartRate)
            .Where(p => p.RecordedAt >= ctx.LastSleep.StartedAt &&
                        p.RecordedAt <= ctx.LastSleep.EndedAt)
            .Select(p => p.Value)
            .ToList();
        var rhrBaseline = ctx.Baseline(MetricType.RestingHeartRate)?.Avg
            ?? ctx.Baseline(MetricType.HeartRate)?.Avg;

        var factors = new List<ScoreFactorDto>();
        double stress = 20; // спокойная ночь по умолчанию

        var sessionHours = (ctx.LastSleep.EndedAt - ctx.LastSleep.StartedAt).TotalHours;
        if (sessionHours > 0 && dto.AwakeHours / sessionHours is var awakeShare && awakeShare > 0)
        {
            stress += awakeShare * 150; // 20% бодрствования → +30
            factors.Add(F("Пробуждения",
                $"{awakeShare * 100:0}% ночи без сна", -awakeShare * 2));
        }
        if (nightHr.Count >= 3 && rhrBaseline is double rhr && rhr > 0)
        {
            var avgNight = nightHr.Average();
            var elevation = (avgNight - rhr) / rhr;
            if (elevation > 0) stress += elevation * 200; // +10% пульса → +20
            factors.Add(F("Ночной пульс",
                $"{avgNight:0} уд/мин при вашей норме покоя {rhr:0}", -elevation * 2));
        }

        return new ScoreDto("sleepStress", "Стресс во сне",
            Math.Clamp((int)Math.Round(stress), 0, 100), factors);
    }

    /// <summary>
    /// Дневной стресс: подавленный HRV и повышенный пульс покоя
    /// относительно личной нормы. Выше — хуже.
    /// </summary>
    private static ScoreDto? StressScore(ScoreContext ctx)
    {
        var hrvDev = ctx.Deviation(MetricType.Hrv);
        var rhrDev = ctx.Deviation(MetricType.RestingHeartRate)
            ?? ctx.Deviation(MetricType.HeartRate);
        if (hrvDev is null && rhrDev is null) return null;

        double stress = 30;
        var factors = new List<ScoreFactorDto>();
        if (hrvDev is double hd)
        {
            if (hd < 0) stress += -hd * 1.5; // HRV -20% → +30 стресса
            factors.Add(F("HRV", $"{hd:+0;-0}% к вашей норме", hd / 50));
        }
        if (rhrDev is double rd)
        {
            if (rd > 0) stress += rd * 2; // пульс +10% → +20 стресса
            factors.Add(F("Пульс покоя", $"{rd:+0;-0}% к вашей норме", -rd / 25));
        }
        return new ScoreDto("stress", "Стресс",
            Math.Clamp((int)Math.Round(stress), 0, 100), factors);
    }

    /// <summary>
    /// Стресс без нагрузки: высокий дневной стресс при низкой активности —
    /// маркер психического напряжения, а не тренировок.
    /// </summary>
    private static ScoreDto? InactiveStressScore(ScoreContext ctx, ScoreDto? stress)
    {
        if (stress is null) return null;
        var stepsDev = ctx.Deviation(MetricType.Steps);
        // Насколько день пассивнее обычного: 1 — совсем без движения.
        var inactivity = stepsDev is double sd ? Clamp01(-sd / 100 + 0.5) : 0.5;
        var value = (int)Math.Round(stress.Value * inactivity);
        var factors = new List<ScoreFactorDto>(stress.Factors)
        {
            F("Активность", stepsDev is double d
                ? $"Шаги {d:+0;-0}% к вашей норме"
                : "Активность обычная", inactivity < 0.4 ? 0.2 : -0.2),
        };
        return new ScoreDto("inactiveStress", "Стресс без нагрузки",
            Math.Clamp(value, 0, 100), factors);
    }

    /// <summary>
    /// Нагрузка дня: шаги и активные калории против личной нормы +
    /// сегодняшние тренировки. Выше — интенсивнее день.
    /// </summary>
    private static ScoreDto? StrainScore(ScoreContext ctx, TrainingLoadDto load)
    {
        var stepsDev = ctx.Deviation(MetricType.Steps);
        var energyDev = ctx.Deviation(MetricType.ActiveEnergy);
        var todayTrimp = ctx.Workouts
            .Where(w => w.Start >= ctx.Now.AddDays(-1))
            .Sum(EstimateTrimp);
        if (stepsDev is null && energyDev is null && todayTrimp == 0) return null;

        double strain = 30;
        var factors = new List<ScoreFactorDto>();
        if (stepsDev is double sd)
        {
            strain += sd * 0.35;
            factors.Add(F("Шаги", $"{sd:+0;-0}% к вашей норме", sd / 100));
        }
        if (energyDev is double ed)
        {
            strain += ed * 0.35;
            factors.Add(F("Активные калории", $"{ed:+0;-0}% к вашей норме", ed / 100));
        }
        if (todayTrimp > 0)
        {
            strain += Math.Min(todayTrimp, 40);
            factors.Add(F("Тренировки", $"Нагрузка за сутки ≈ {todayTrimp:0} TRIMP", 0.3));
        }
        return new ScoreDto("strain", "Нагрузка",
            Math.Clamp((int)Math.Round(strain), 0, 100), factors);
    }

    /// <summary>
    /// Восстановление: HRV и пульс покоя против нормы + качество сна,
    /// минус ночной стресс.
    /// </summary>
    private static ScoreDto? RecoveryScore(
        ScoreContext ctx, ScoreDto? sleep, ScoreDto? sleepStress)
    {
        var parts = new List<(double Weight, double Score01, ScoreFactorDto Factor)>();

        if (ctx.Deviation(MetricType.RestingHeartRate) is double rhrDev)
            parts.Add((30, Clamp01(1 - rhrDev / 100 * 5),
                F("Пульс покоя", $"{rhrDev:+0;-0}% к вашей норме", -rhrDev / 25)));
        if (ctx.Deviation(MetricType.Hrv) is double hrvDev)
            parts.Add((30, hrvDev >= 0 ? 1 : Clamp01(1 + hrvDev / 100 * 5),
                F("HRV", $"{hrvDev:+0;-0}% к вашей норме", hrvDev / 50)));
        if (sleep is not null)
            parts.Add((25, sleep.Value / 100.0,
                F("Сон", $"Оценка сна {sleep.Value}", (sleep.Value - 70) / 100.0)));
        if (sleepStress is not null)
            parts.Add((15, 1 - sleepStress.Value / 100.0,
                F("Ночной стресс", $"Оценка {sleepStress.Value}",
                    (30 - sleepStress.Value) / 100.0)));

        if (parts.Count == 0) return null;
        var totalWeight = parts.Sum(p => p.Weight);
        var value = (int)Math.Round(
            parts.Sum(p => p.Weight * p.Score01) / totalWeight * 100);
        return new ScoreDto("recovery", "Восстановление",
            Math.Clamp(value, 0, 100), parts.Select(p => p.Factor).ToList());
    }

    /// <summary>
    /// Запас энергии — «батарейка»: накопленный баланс восстановления
    /// и нагрузки за последнюю неделю.
    /// </summary>
    private static ScoreDto? EnergyReserveScore(
        ScoreContext ctx, ScoreDto? recovery, ScoreDto? strain)
    {
        if (recovery is null) return null;
        // Недельный баланс: сон против цели и нагрузка против нормы.
        var sleepList = ctx.Of(MetricType.Sleep)
            .Where(p => p.RecordedAt >= ctx.Now.AddDays(-7))
            .Select(p => p.Value).ToList();
        var sleepBalance = sleepList.Count > 0
            ? sleepList.Average() / ctx.Goals.SleepHours - 1 // недосып копится
            : 0;
        var weekTrimp = ctx.Workouts
            .Where(w => w.Start >= ctx.Now.AddDays(-7))
            .Sum(EstimateTrimp);
        var loadPenalty = Math.Min(weekTrimp / 30, 15);

        var value = (int)Math.Round(
            recovery.Value * 0.6 + 40 * Clamp01(1 + sleepBalance) - loadPenalty +
            (strain is null ? 0 : (45 - strain.Value) * 0.15));
        var factors = new List<ScoreFactorDto>
        {
            F("Восстановление сегодня", $"Оценка {recovery.Value}",
                (recovery.Value - 60) / 100.0),
            F("Сон за неделю", sleepList.Count > 0
                ? $"В среднем {sleepList.Average():0.0} ч/ночь"
                : "Нет данных", sleepBalance),
            F("Нагрузка за неделю", $"≈ {weekTrimp:0} TRIMP", -loadPenalty / 30),
        };
        return new ScoreDto("energy", "Запас энергии",
            Math.Clamp(value, 0, 100), factors);
    }

    /// <summary>
    /// Питание: потреблённые калории против личной нормы (или 2000 ккал)
    /// и вода против 2 л.
    /// </summary>
    private static ScoreDto? NutritionScore(ScoreContext ctx)
    {
        var kcal = ctx.DaySum(MetricType.DietaryEnergy);
        var water = ctx.DaySum(MetricType.Water);
        if (kcal <= 0 && water <= 0) return null;

        // Цель калорий: профиль → личное среднее → 2000.
        var kcalTarget = ctx.Goals.Kcal
            ?? (ctx.Baseline(MetricType.DietaryEnergy)?.Avg is double avg && avg > 500
                ? avg
                : 2000);

        var factors = new List<ScoreFactorDto>();
        double value = 0;
        double weight = 0;
        if (kcal > 0)
        {
            // 100% при попадании в норму, штраф за сильный недобор/перебор.
            var ratio = kcal / kcalTarget;
            var kcalScore = Clamp01(1 - Math.Abs(ratio - 1));
            value += 70 * kcalScore;
            weight += 70;
            factors.Add(F("Калории", $"{kcal:0} из ~{kcalTarget:0} ккал",
                kcalScore - 0.7));
        }
        if (water > 0)
        {
            var waterScore = Clamp01(water / ctx.Goals.WaterLiters);
            value += 30 * waterScore;
            weight += 30;
            factors.Add(F("Вода",
                $"{water:0.0} л из {ctx.Goals.WaterLiters:0.#} л",
                waterScore - 0.7));
        }
        if (weight == 0) return null;
        return new ScoreDto("nutrition", "Питание",
            Math.Clamp((int)Math.Round(value / weight * 100), 0, 100), factors);
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

    private static double Clamp01(double v) => Math.Clamp(v, 0, 1);
    private static double Round(double v) => Math.Round(v, 1);
    private static double? Round2(double? v) => v is double d ? Math.Round(d, 1) : null;
}
