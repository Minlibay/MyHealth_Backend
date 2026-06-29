namespace MyHealth.Api.Domain;

/// <summary>
/// Одно измерение показателя здоровья, привязанное к пользователю.
/// Для давления: <see cref="Value"/> — систолическое, <see cref="Secondary"/> — диастолическое.
/// </summary>
public class HealthSample
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public AppUser? User { get; set; }

    public MetricType Metric { get; set; }

    public double Value { get; set; }

    /// <summary>Доп. значение (диастолическое давление и т.п.).</summary>
    public double? Secondary { get; set; }

    public string? Unit { get; set; }

    /// <summary>Время измерения на устройстве.</summary>
    public DateTimeOffset RecordedAt { get; set; }

    /// <summary>Источник данных (приложение/устройство).</summary>
    public string? Source { get; set; }

    /// <summary>
    /// Идентификатор записи на клиенте — для идемпотентной загрузки
    /// (повторная отправка той же записи не создаёт дубль).
    /// </summary>
    public string? ClientId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
