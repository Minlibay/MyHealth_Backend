namespace MyHealth.Api.Domain;

/// <summary>
/// Отметка журнала («кофе», «алкоголь», «болею»...) — для будущих
/// корреляций «после алкоголя ваш HRV в среднем ниже».
/// </summary>
public class TagEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public AppUser? User { get; set; }

    /// <summary>Ключ тега (coffee, alcohol, late_meal, sick, stress...).</summary>
    public string Tag { get; set; } = "";

    /// <summary>Момент события.</summary>
    public DateTimeOffset At { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
