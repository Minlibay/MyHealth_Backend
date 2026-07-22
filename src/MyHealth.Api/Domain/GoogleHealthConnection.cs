namespace MyHealth.Api.Domain;

/// <summary>
/// Подключение пользователя к Google Health API (облачные данные Fitbit/
/// Google). Храним только refresh-токен — им бэкенд обновляет короткий
/// access-токен и опрашивает API.
/// </summary>
public class GoogleHealthConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public AppUser? User { get; set; }

    /// <summary>Refresh-токен Google (offline access).</summary>
    public required string RefreshToken { get; set; }

    /// <summary>Выданные скоупы (диагностика).</summary>
    public string? Scopes { get; set; }

    public DateTimeOffset ConnectedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSyncAt { get; set; }

    /// <summary>Последняя ошибка синхронизации (для показа/диагностики).</summary>
    public string? LastError { get; set; }
}
