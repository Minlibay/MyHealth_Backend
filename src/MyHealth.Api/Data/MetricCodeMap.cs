using MyHealth.Api.Domain;

namespace MyHealth.Api.Data;

/// <summary>
/// Соответствие прежнего enum MetricType кодам реестра показателей.
/// Нужен и для миграции старых данных, и для совместимости API с
/// приложением, которое пока присылает enum.
/// </summary>
public static class MetricCodeMap
{
    private static readonly Dictionary<MetricType, string> ToCodeMap = new()
    {
        [MetricType.Steps] = "activity.steps",
        [MetricType.HeartRate] = "cardio.hr.instant",
        [MetricType.BloodPressure] = "vitals.bp.systolic",
        [MetricType.Weight] = "body.weight",
        [MetricType.Sleep] = "sleep.duration.total",
        [MetricType.BloodGlucose] = "metabolic.glucose.cgm",
        [MetricType.BloodOxygen] = "respiratory.spo2",
        [MetricType.ActiveEnergy] = "activity.calories.active",
        [MetricType.Distance] = "activity.distance",
        [MetricType.Water] = "nutrition.water",
        [MetricType.BodyTemperature] = "body.temp.skin.abs",
        [MetricType.RespiratoryRate] = "respiratory.rate",
        [MetricType.RestingHeartRate] = "cardio.hr.resting",
        [MetricType.Hrv] = "cardio.hrv.rmssd",
        [MetricType.BodyFat] = "body.fat.pct",
        [MetricType.Height] = "body.height",
        [MetricType.DietaryEnergy] = "nutrition.calories.consumed",
        [MetricType.FlightsClimbed] = "activity.floors",
        [MetricType.BasalEnergy] = "activity.calories.basal",
        [MetricType.TotalCalories] = "activity.calories.total",
        [MetricType.ExerciseTime] = "activity.intensity.minutes",
        [MetricType.StandTime] = "activity.stand.minutes",
        [MetricType.MoveMinutes] = "activity.move.minutes",
        [MetricType.Mindfulness] = "mind.mindfulness.minutes",
        [MetricType.DistanceCycling] = "activity.distance.cycling",
        [MetricType.DistanceSwimming] = "activity.distance.swimming",
        [MetricType.WalkingHeartRate] = "cardio.hr.walk.avg",
        [MetricType.LeanBodyMass] = "body.muscle.mass",
        [MetricType.Bmi] = "body.bmi",
        [MetricType.Waist] = "body.waist",
        [MetricType.BodyWater] = "body.water.mass",
        [MetricType.WalkingSpeed] = "activity.speed",
        [MetricType.Carbs] = "nutrition.carbs",
        [MetricType.Protein] = "nutrition.protein",
        [MetricType.Fat] = "nutrition.fat",
        [MetricType.SkinTemperature] = "body.temp.skin.abs",
    };

    private static readonly Dictionary<string, MetricType> FromCodeMap =
        BuildReverse();

    private static Dictionary<string, MetricType> BuildReverse()
    {
        var result = new Dictionary<string, MetricType>();
        foreach (var (metric, code) in ToCodeMap)
        {
            // SkinTemperature делит код с BodyTemperature — обратно
            // отдаём первый (BodyTemperature).
            result.TryAdd(code, metric);
        }
        return result;
    }

    public static string ToCode(MetricType metric) =>
        ToCodeMap.TryGetValue(metric, out var code) ? code : $"legacy.{metric}";

    public static MetricType? FromCode(string code) =>
        FromCodeMap.TryGetValue(code, out var m) ? m : null;

    /// <summary>Все коды, используемые совместимым API.</summary>
    public static IReadOnlyCollection<string> AllCodes => ToCodeMap.Values;

    /// <summary>
    /// Показатели, которых нет в исходном справочнике из таблицы, но
    /// которые уже собирает приложение. Добавляем в реестр, чтобы данные
    /// не терялись и внешний ключ соблюдался.
    /// </summary>
    public static readonly (string Code, string Name, string Domain, string Grain,
        string Trigger, string Derivation, string ValueType, string Unit)[] Extra =
        [
            ("body.height", "Рост", "Тело", "point", "manual · on_demand",
                "measured", "num", "см"),
            ("body.bmi", "Индекс массы тела", "Тело", "point", "on_demand",
                "vendor_estimate", "num", "кг/м²"),
            ("body.waist", "Окружность талии", "Тело", "point", "manual",
                "measured", "num", "см"),
            ("body.water.mass", "Вода в организме", "Тело", "point", "on_demand",
                "vendor_estimate", "num", "кг"),
            ("nutrition.water", "Вода выпито", "Питание", "daily", "manual",
                "measured", "num", "л"),
            ("nutrition.calories.consumed", "Калории потреблённые", "Питание",
                "daily", "manual", "vendor_aggregate", "num", "ккал"),
            ("nutrition.carbs", "Углеводы", "Питание", "daily", "manual",
                "vendor_aggregate", "num", "г"),
            ("nutrition.protein", "Белки", "Питание", "daily", "manual",
                "vendor_aggregate", "num", "г"),
            ("nutrition.fat", "Жиры", "Питание", "daily", "manual",
                "vendor_aggregate", "num", "г"),
            ("activity.stand.minutes", "Время стоя", "Активность и движение",
                "daily", "continuous", "vendor_aggregate", "num", "мин"),
            ("activity.move.minutes", "Минуты движения", "Активность и движение",
                "daily", "continuous", "vendor_aggregate", "num", "мин"),
            ("activity.distance.cycling", "Дистанция на велосипеде",
                "Активность и движение", "daily", "continuous",
                "vendor_aggregate", "num", "м"),
            ("activity.distance.swimming", "Дистанция в плавании",
                "Активность и движение", "daily", "continuous",
                "vendor_aggregate", "num", "м"),
            // Дистанция тренировки — отдельный код, чтобы не смешивалась
            // с суточной дистанцией (у неё другая единица и смысл).
            ("activity.distance.session", "Дистанция за тренировку",
                "Активность и движение", "session", "continuous",
                "vendor_aggregate", "num", "м"),
            ("mind.mindfulness.minutes", "Минуты осознанности", "Психика",
                "daily", "on_demand", "vendor_aggregate", "num", "мин"),
            ("vitals.bp.diastolic.legacy", "Давление диастолическое (совм.)",
                "Тело", "point", "on_demand", "measured", "num", "мм рт. ст."),
        ];
}
