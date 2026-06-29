namespace MyHealth.Api.Domain;

/// <summary>
/// Тип показателя здоровья. Имена совпадают с enum HealthMetric во Flutter-клиенте,
/// чтобы сериализация туда-обратно была однозначной.
/// </summary>
public enum MetricType
{
    Steps,
    HeartRate,
    BloodPressure,
    Weight,
    Sleep,
    BloodGlucose,
    BloodOxygen,
}
