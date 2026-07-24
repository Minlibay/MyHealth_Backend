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
    /// Один тип данных: GET-метод list с фильтром по интервалу
    /// (AIP-160), с пагинацией. Парсим точки и добавляем недостающие
    /// HealthSample (идемпотентно по ClientId).
    /// </summary>
    private async Task<int> SyncDataTypeAsync(
        AppDbContext db, HttpClient http, Guid userId, string dataType,
        MetricType metric, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var points = new List<(DateTimeOffset At, double Value)>();
        JsonElement? rawSample = null;
        var rawTotal = 0;

        // Член фильтра — dataType в camelCase (steps, heartRate,
        // dailyRestingHeartRate...), как имена полей v4. snake_case здесь
        // отвергается (INVALID_DATA_POINT_FILTER_DATA_TYPE_MEMBER).
        var field = ToCamelCase(dataType);
        var filter =
            $"{field}.interval.start_time >= \"{from.UtcDateTime:o}\" AND " +
            $"{field}.interval.start_time < \"{to.UtcDateTime:o}\"";

        string? pageToken = null;
        var pages = 0;
        do
        {
            var url = $"{ApiBase}/{dataType}/dataPoints" +
                      $"?filter={Uri.EscapeDataString(filter)}&pageSize=1000" +
                      (pageToken is null ? "" : $"&pageToken={Uri.EscapeDataString(pageToken)}");
            var res = await http.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode)
            {
                // 404 — тип недоступен у пользователя; молча пропускаем.
                if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return 0;
                var errBody = await res.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException(
                    $"{dataType} [{(int)res.StatusCode}]: {Truncate(errBody, 300)}");
            }
            var json = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
            var (extracted, seenRaw, sample) = ExtractPointsDiag(json);
            points.AddRange(extracted);
            rawTotal += seenRaw;
            rawSample ??= sample;
            pageToken = json.TryGetProperty("nextPageToken", out var np)
                ? np.GetString()
                : null;
        } while (!string.IsNullOrEmpty(pageToken) && ++pages < 20);

        // Запрос прошёл, точки есть, но значение не распозналось — сохраняем
        // образец JSON, чтобы поправить парсинг под реальный формат.
        if (points.Count == 0 && rawTotal > 0 && rawSample is JsonElement s)
        {
            throw new HttpRequestException(
                $"{dataType}: получено {rawTotal}, но не распознано. Пример: " +
                Truncate(s.GetRawText(), 300));
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

    /// <summary>"daily-resting-heart-rate" → "dailyRestingHeartRate".</summary>
    private static string ToCamelCase(string hyphenated)
    {
        var parts = hyphenated.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return hyphenated;
        var sb = new System.Text.StringBuilder(parts[0]);
        for (var i = 1; i < parts.Length; i++)
            sb.Append(char.ToUpperInvariant(parts[i][0])).Append(parts[i][1..]);
        return sb.ToString();
    }

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
    /// <summary>
    /// Разбор + диагностика: возвращает распознанные точки, число сырых
    /// записей и образец первой записи (для отладки формата v4).
    /// </summary>
    private static (List<(DateTimeOffset At, double Value)> Points, int RawCount,
        JsonElement? Sample) ExtractPointsDiag(JsonElement root)
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
        else if (root.TryGetProperty("dailyRollUps", out var dru))
            array = dru;
        else
            return (result, 0, null);

        var raw = 0;
        JsonElement? sample = null;
        foreach (var el in array.EnumerateArray())
        {
            raw++;
            sample ??= el;
            var value = ExtractValue(el);
            var at = ExtractTime(el);
            if (value is double v && at is DateTimeOffset t) result.Add((t, v));
        }
        return (result, raw, sample);
    }

    private static double? ExtractValue(JsonElement el)
    {
        foreach (var key in new[]
                 {
                     "value", "total", "average", "avg", "mean", "count", "sum",
                     "fpVal", "intVal", "doubleValue", "quantity", "amount",
                     "bpm", "steps", "meters", "kilocalories", "kcal",
                     "celsius", "percentage", "milliseconds"
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
        // Точка может нести интервал вложенным объектом.
        if (el.TryGetProperty("interval", out var interval) &&
            interval.ValueKind == JsonValueKind.Object)
        {
            var t = ExtractTime(interval);
            if (t is not null) return t;
        }
        foreach (var key in new[]
                 {
                     "startTime", "start_time", "date", "startDate",
                     "civilStartTime", "endTime", "end_time", "time"
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
