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

    // --- Профиль: физические параметры (для зон пульса, калорий кольца) ---

    /// <summary>"male" | "female".</summary>
    public string? Gender { get; set; }

    public int? Age { get; set; }
    public double? HeightCm { get; set; }
    public double? WeightKg { get; set; }

    // --- Персональные цели (для скоров; null — цели по умолчанию) ---

    public int? StepsGoal { get; set; }
    public double? WaterGoalLiters { get; set; }
    public double? SleepGoalHours { get; set; }
    public int? KcalGoal { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<HealthSample> Samples { get; set; } = new List<HealthSample>();
}
