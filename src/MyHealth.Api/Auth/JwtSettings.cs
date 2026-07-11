namespace MyHealth.Api.Auth;

public class JwtSettings
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "MyHealth";
    public string Audience { get; set; } = "MyHealth";

    /// <summary>Секрет подписи. В проде задаётся через переменную окружения/секрет-хранилище.</summary>
    public string Key { get; set; } = "";

    /// <summary>Срок жизни access-токена. Короткий — обновление через refresh-токен.</summary>
    public int ExpiryMinutes { get; set; } = 60;

    /// <summary>Срок жизни refresh-токена в днях.</summary>
    public int RefreshExpiryDays { get; set; } = 90;
}
