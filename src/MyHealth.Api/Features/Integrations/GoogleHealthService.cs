using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MyHealth.Api.Data;
using MyHealth.Api.Domain;

namespace MyHealth.Api.Features.Integrations;

/// <summary>Настройки Google Health API (клиент — из appsettings/env).</summary>
public class GoogleHealthSettings
{
    public const string SectionName = "GoogleHealth";

    /// <summary>OAuth client_id (нативный iOS/Android клиент, без секрета).</summary>
    public string ClientId { get; set; } = "";

    /// <summary>Секрет — только если используется web-клиент; для installed пусто.</summary>
    public string? ClientSecret { get; set; }
}

/// <summary>
/// Опрос Google Health API: обновляет access-токен по refresh-токену и
/// тянет суточные роллапы по типам данных, раскладывая их в HealthSample
/// с источником google_health. Форма JSON-ответа v4 местами уточняется
/// по факту — парсинг намеренно устойчивый.
/// </summary>
public class GoogleHealthService(
    IHttpClientFactory httpFactory,
    Microsoft.Extensions.Options.IOptions<GoogleHealthSettings> options,
    ILogger<GoogleHealthService> logger)
{
    private readonly GoogleHealthSettings _s = options.Value;

    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string ApiBase = "https://health.googleapis.com/v4/users/me/dataTypes";

    /// <summary>dataType Google Health API → (наша метрика, единица).</summary>
    private static readonly (string DataType, MetricType Metric)[] Mappings =
    [
        ("steps", MetricType.Steps),
        ("heart-rate", MetricType.HeartRate),
        ("daily-resting-heart-rate", MetricType.RestingHeartRate),
        ("daily-oxygen-saturation", MetricType.BloodOxygen),
        ("active-energy-burned", MetricType.ActiveEnergy),
        ("distance", MetricType.Distance),
        ("weight", MetricType.Weight),
        ("body-fat", MetricType.BodyFat),
        ("height", MetricType.Height),
        ("daily-respiratory-rate", MetricType.RespiratoryRate),
        ("daily-heart-rate-variability", MetricType.Hrv),
        ("blood-glucose", MetricType.BloodGlucose),
        ("hydration-log", MetricType.Water),
        ("nutrition-log", MetricType.DietaryEnergy),
        ("sleep", MetricType.Sleep),
        ("core-body-temperature", MetricType.BodyTemperature),
    ];

    /// <summary>Обменять refresh-токен на короткоживущий access-токен.</summary>
    public async Task<string?> GetAccessTokenAsync(string refreshToken, CancellationToken ct)
    {
        var http = httpFactory.CreateClient();
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = _s.ClientId,
        };
        if (!string.IsNullOrEmpty(_s.ClientSecret))
            form["client_secret"] = _s.ClientSecret;

        var res = await http.PostAsync(
            TokenEndpoint, new FormUrlEncodedContent(form), ct);
        if (!res.IsSuccessStatusCode)
        {
            logger.LogWarning("Google token refresh failed: {Status}", res.StatusCode);
            return null;
        }
        var json = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
        return json.TryGetProperty("access_token", out var t) ? t.GetString() : null;
    }

    /// <summary>
    /// Синхронизировать данные пользователя за последние [days] дней
    /// (окнами ≤ 14 дней — ограничение API). Возвращает число вставленных
    /// записей. Ошибки по одному типу не срывают остальные.
    /// </summary>
    public async Task<int> SyncAsync(
        AppDbContext db, GoogleHealthConnection conn, int days, CancellationToken ct)
    {
        var accessToken = await GetAccessTokenAsync(conn.RefreshToken, ct);
        if (accessToken is null)
        {
            conn.LastError = "Не удалось обновить токен Google (переподключите аккаунт).";
            await db.SaveChangesAsync(ct);
            return 0;
        }

        var http = httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", accessToken);

        var now = DateTimeOffset.UtcNow;
        var from = now.AddDays(-Math.Clamp(days, 1, 30));
        var inserted = 0;
        var errors = new List<string>();
        string? firstErrorDetail = null;

        foreach (var (dataType, metric) in Mappings)
        {
            try
            {
                inserted += await SyncDataTypeAsync(
                    db, http, conn.UserId, dataType, metric, from, now, ct);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Google Health sync failed for {DataType}", dataType);
                errors.Add(dataType);
                // Сохраняем текст первой ошибки Google — по нему видно причину
                // (неверное имя поля/метод/скоуп) и можно чинить прицельно.
                firstErrorDetail ??= e.Message;
            }
        }

        await db.SaveChangesAsync(ct);
        conn.LastSyncAt = now;
        conn.LastError = errors.Count == 0
            ? null
            : firstErrorDetail is null
                ? $"Не пришли: {string.Join(", ", errors)}"
                : $"Ошибка Google ({errors.Count} типов). Детали: {Truncate(firstErrorDetail, 400)}";
        await db.SaveChangesAsync(ct);
        return inserted;
    }

    /// <summary>
    /// Один тип данных: окнами по 14 дней вызываем dailyRollUp, парсим
    /// точки и добавляем недостающие HealthSample (идемпотентно по ClientId).
    /// </summary>
    private async Task<int> SyncDataTypeAsync(
        AppDbContext db, HttpClient http, Guid userId, string dataType,
        MetricType metric, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var points = new List<(DateTimeOffset At, double Value)>();

        // API отдаёт максимум 14 дней за запрос.
        var windowStart = from;
        while (windowStart < to)
        {
            var windowEnd = windowStart.AddDays(14);
            if (windowEnd > to) windowEnd = to;

            var url = $"{ApiBase}/{dataType}/dataPoints:dailyRollUp";
            var body = new
            {
                startTime = windowStart.UtcDateTime.ToString("o"),
                endTime = windowEnd.UtcDateTime.ToString("o"),
            };
            var res = await http.PostAsJsonAsync(url, body, ct);
            if (!res.IsSuccessStatusCode)
            {
                // 404 — тип недоступен у пользователя; молча пропускаем.
                if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return 0;
                // Тело ответа Google несёт точную причину — сохраняем его.
                var errBody = await res.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException(
                    $"{dataType} [{(int)res.StatusCode}]: {Truncate(errBody, 300)}");
            }
            var json = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
            points.AddRange(ExtractPoints(json));
            windowStart = windowEnd;
        }
        if (points.Count == 0) return 0;

        // Идемпотентность: не дублируем уже загруженные точки.
        var clientIds = points
            .Select(p => ClientId(dataType, p.At))
            .ToList();
        var existing = (await db.Samples
            .Where(s => s.UserId == userId && s.ClientId != null &&
                        clientIds.Contains(s.ClientId))
            .Select(s => s.ClientId!)
            .ToListAsync(ct)).ToHashSet();

        var inserted = 0;
        var seen = new HashSet<string>();
        foreach (var p in points)
        {
            var cid = ClientId(dataType, p.At);
            if (existing.Contains(cid) || !seen.Add(cid)) continue;
            db.Samples.Add(new HealthSample
            {
                UserId = userId,
                Metric = metric,
                Value = Convert(metric, p.Value),
                RecordedAt = p.At,
                Source = "google_health",
                ClientId = cid,
            });
            inserted++;
        }
        return inserted;
    }

    private static string ClientId(string dataType, DateTimeOffset at) =>
        $"gh-{dataType}-{at.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}";

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max];

    /// <summary>Приведение единиц Google к нашим (метры→км, сон сек/мин→часы).</summary>
    private static double Convert(MetricType metric, double v) => metric switch
    {
        MetricType.Distance => v > 100 ? v / 1000 : v, // метры → км
        MetricType.Sleep => v > 120 ? v / 3600 : v / 60, // сек/мин → часы
        _ => v,
    };

    /// <summary>
    /// Устойчивый разбор ответа роллапа: ищем массив точек и в каждой —
    /// числовое значение и дату. Имена полей v4 подтверждаются на живых
    /// данных; здесь перебор частых вариантов.
    /// </summary>
    private static IEnumerable<(DateTimeOffset At, double Value)> ExtractPoints(
        JsonElement root)
    {
        var result = new List<(DateTimeOffset, double)>();

        JsonElement array;
        if (root.ValueKind == JsonValueKind.Array)
            array = root;
        else if (root.TryGetProperty("dataPoints", out var dp))
            array = dp;
        else if (root.TryGetProperty("points", out var pts))
            array = pts;
        else if (root.TryGetProperty("rollUps", out var ru))
            array = ru;
        else
            return result;

        foreach (var el in array.EnumerateArray())
        {
            var value = ExtractValue(el);
            var at = ExtractTime(el);
            if (value is double v && at is DateTimeOffset t) result.Add((t, v));
        }
        return result;
    }

    private static double? ExtractValue(JsonElement el)
    {
        foreach (var key in new[]
                 {
                     "value", "total", "average", "avg", "count", "sum",
                     "fpVal", "intVal", "doubleValue"
                 })
        {
            if (el.TryGetProperty(key, out var v))
            {
                if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
                if (v.ValueKind == JsonValueKind.Object) return ExtractValue(v);
            }
        }
        return null;
    }

    private static DateTimeOffset? ExtractTime(JsonElement el)
    {
        foreach (var key in new[]
                 {
                     "startTime", "date", "startDate", "civilStartTime",
                     "endTime", "time"
                 })
        {
            if (el.TryGetProperty(key, out var v) &&
                v.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(v.GetString(), out var dt))
                return dt;
        }
        return null;
    }
}
