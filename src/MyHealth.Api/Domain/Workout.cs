namespace MyHealth.Api.Domain;

/// <summary>
/// Одна тренировка пользователя из HealthKit / Health Connect.
/// </summary>
public class Workout
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public AppUser? User { get; set; }

    /// <summary>Тип активности как в клиенте (enum пакета health), напр. "RUNNING".</summary>
    public string ActivityType { get; set; } = "";

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset EndedAt { get; set; }

    /// <summary>Сожжённые калории, если известны.</summary>
    public double? EnergyKcal { get; set; }

    /// <summary>Дистанция в метрах, если известна.</summary>
    public double? DistanceMeters { get; set; }

    /// <summary>Источник данных (приложение/устройство).</summary>
    public string? Source { get; set; }

    /// <summary>
    /// Идентификатор записи на клиенте — для идемпотентной загрузки
    /// (повторная отправка той же тренировки не создаёт дубль).
    /// </summary>
    public string? ClientId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
