namespace MyHealth.Api.Domain;

/// <summary>
/// Refresh-токен пользователя. Хранится только SHA-256 хеш значения —
/// сам токен знает только клиент. При обновлении токен ротируется:
/// старый отзывается, выдаётся новый.
/// </summary>
public class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public AppUser? User { get; set; }

    /// <summary>SHA-256 хеш значения токена (hex).</summary>
    public required string TokenHash { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Момент отзыва (ротация, logout или удаление аккаунта).</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool IsActive => RevokedAt is null && ExpiresAt > DateTimeOffset.UtcNow;
}
