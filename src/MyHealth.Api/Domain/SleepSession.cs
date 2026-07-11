namespace MyHealth.Api.Domain;

/// <summary>
/// Сессия сна с фазами (с кольца или другого источника, умеющего фазы).
/// Фазы хранятся JSON-массивом сегментов — структура редко запрашивается
/// по частям, поэтому отдельная таблица сегментов не нужна.
/// </summary>
public class SleepSession
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public AppUser? User { get; set; }

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset EndedAt { get; set; }

    /// <summary>JSON: [{"stage":"deep|light|rem|awake","start":...,"end":...}].</summary>
    public string StagesJson { get; set; } = "[]";

    /// <summary>Источник данных (кольцо и т.п.).</summary>
    public string? Source { get; set; }

    /// <summary>Идентификатор записи на клиенте для идемпотентной загрузки.</summary>
    public string? ClientId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
