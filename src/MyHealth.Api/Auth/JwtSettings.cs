namespace MyHealth.Api.Auth;

public class JwtSettings
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "MyHealth";
    public string Audience { get; set; } = "MyHealth";

    /// <summary>Секрет подписи. В проде задаётся через переменную окружения/секрет-хранилище.</summary>
    public string Key { get; set; } = "";

    public int ExpiryMinutes { get; set; } = 60 * 24 * 7;
}
