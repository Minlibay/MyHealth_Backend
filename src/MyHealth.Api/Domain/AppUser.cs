namespace MyHealth.Api.Domain;

/// <summary>Учётная запись пользователя.</summary>
public class AppUser
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Email в нижнем регистре (уникальный логин).</summary>
    public required string Email { get; set; }

    /// <summary>Хеш пароля (BCrypt). Сам пароль не хранится.</summary>
    public required string PasswordHash { get; set; }

    public string? DisplayName { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<HealthSample> Samples { get; set; } = new List<HealthSample>();
}
