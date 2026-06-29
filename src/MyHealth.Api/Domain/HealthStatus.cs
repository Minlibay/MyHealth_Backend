namespace MyHealth.Api.Domain;

/// <summary>
/// Оценка значения показателя относительно ориентировочных норм для взрослых.
/// Не медицинский диагноз — визуальный ориентир. Единый источник правды для всех клиентов.
/// </summary>
public enum HealthStatus
{
    Unknown,
    Ok,
    Warn,
    Alert,
}
