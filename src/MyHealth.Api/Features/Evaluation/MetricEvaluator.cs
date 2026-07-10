using MyHealth.Api.Domain;

namespace MyHealth.Api.Features.Evaluation;

/// <summary>
/// Вся расчётная логика по показателям здоровья — на бэкенде, чтобы её можно было
/// переиспользовать в любых клиентах (мобильное приложение, web, интеграции).
/// </summary>
public static class MetricEvaluator
{
    /// <summary>Классификация значения по нормам. Для давления: value=систолическое, secondary=диастолическое.</summary>
    public static HealthStatus Evaluate(MetricType metric, double? value, double? secondary = null)
    {
        if (value is not double v) return HealthStatus.Unknown;

        switch (metric)
        {
            case MetricType.HeartRate:
                if (v >= 120 || v < 45) return HealthStatus.Alert;
                if (v > 100 || v < 55) return HealthStatus.Warn;
                return HealthStatus.Ok;

            case MetricType.BloodPressure:
                var dia = secondary ?? 0;
                if (v >= 140 || dia >= 90 || v < 90) return HealthStatus.Alert;
                if (v >= 130 || dia >= 85) return HealthStatus.Warn;
                return HealthStatus.Ok;

            case MetricType.BloodOxygen:
                if (v < 90) return HealthStatus.Alert;
                if (v < 95) return HealthStatus.Warn;
                return HealthStatus.Ok;

            case MetricType.BloodGlucose:
                if (v >= 7.0 || v < 3.9) return HealthStatus.Alert;
                if (v >= 5.6) return HealthStatus.Warn;
                return HealthStatus.Ok;

            case MetricType.Sleep:
                if (v < 5 || v > 10) return HealthStatus.Warn;
                return HealthStatus.Ok;

            case MetricType.BodyTemperature:
                if (v >= 38.5 || v < 35.0) return HealthStatus.Alert;
                if (v >= 37.5 || v < 36.0) return HealthStatus.Warn;
                return HealthStatus.Ok;

            case MetricType.RespiratoryRate:
                if (v > 24 || v < 8) return HealthStatus.Alert;
                if (v > 20 || v < 10) return HealthStatus.Warn;
                return HealthStatus.Ok;

            case MetricType.RestingHeartRate:
                if (v > 100 || v < 40) return HealthStatus.Alert;
                if (v > 90 || v < 50) return HealthStatus.Warn;
                return HealthStatus.Ok;

            // Для активности, воды, HRV, состава тела и роста
            // нет универсальной нормы — не оцениваем.
            case MetricType.Steps:
            case MetricType.Weight:
            case MetricType.ActiveEnergy:
            case MetricType.Distance:
            case MetricType.Water:
            case MetricType.Hrv:
            case MetricType.BodyFat:
            case MetricType.Height:
            default:
                return HealthStatus.Unknown;
        }
    }

    /// <summary>Человекочитаемая подпись статуса (RU).</summary>
    public static string Label(HealthStatus status) => status switch
    {
        HealthStatus.Ok => "Норма",
        HealthStatus.Warn => "Погранично",
        HealthStatus.Alert => "Вне нормы",
        _ => "",
    };

    /// <summary>Форматирование значения для отображения (для давления — "120/80").</summary>
    public static string Format(MetricType metric, double value, double? secondary)
    {
        string n(double x) => x == Math.Round(x) ? ((long)x).ToString() : x.ToString("0.0");
        if (metric == MetricType.BloodPressure && secondary is double s)
            return $"{n(value)}/{n(s)}";
        return n(value);
    }
}
