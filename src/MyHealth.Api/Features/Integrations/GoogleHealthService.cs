using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MyHealth.Api.Data;
using MyHealth.Api.Domain;
using MyHealth.Api.Domain.Tracking;

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

    /// <summary>
    /// Накопительные за сутки типы: Google отдаёт промежуточные снимки
    /// счётчика, поэтому храним одну запись за день.
    /// </summary>
    private static readonly HashSet<string> _cumulativeTypes =
    [
        "steps", "distance", "active-energy-burned", "total-calories",
        "hydration-log", "nutrition-log", "floors", "active-minutes",
    ];

    /// <summary>Точечные (sample) типы — фильтруются по sample_time.</summary>
    private static readonly HashSet<string> _sampleTypes =
    [
        "heart-rate", "weight", "body-fat", "height", "blood-glucose",
        "core-body-temperature",
    ];

    /// <summary>
    /// Кандидаты фильтра для типа, в порядке вероятности. Документация v4
    /// не описывает ограничения для всех типов, поэтому подбираем рабочий
    /// вариант пробным запросом и сообщаем его в сводке.
    /// </summary>
    private static List<(string Name,
        Func<DateTimeOffset, DateTimeOffset, string> Build)> FilterCandidates(
        string dataType, string field)
    {
        string Iso(DateTimeOffset d) => d.UtcDateTime.ToString("o");
        string Day(DateTimeOffset d) => d.UtcDateTime.ToString("yyyy-MM-dd");

        var all = new List<(string, Func<DateTimeOffset, DateTimeOffset, string>)>
        {
            ("interval.start", (a, b) =>
                $"{field}.interval.start_time >= \"{Iso(a)}\" AND " +
                $"{field}.interval.start_time < \"{Iso(b)}\""),
            ("interval.end", (a, b) =>
                $"{field}.interval.end_time >= \"{Iso(a)}\" AND " +
                $"{field}.interval.end_time < \"{Iso(b)}\""),
            ("sample_time", (a, b) =>
                $"{field}.sample_time.physical_time >= \"{Iso(a)}\" AND " +
                $"{field}.sample_time.physical_time < \"{Iso(b)}\""),
            ("date", (a, b) =>
                $"{field}.date >= \"{Day(a)}\" AND {field}.date < \"{Day(b)}\""),
            ("civil_start", (a, b) =>
                $"{field}.interval.civil_start_time >= \"{Day(a)}\" AND " +
                $"{field}.interval.civil_start_time < \"{Day(b)}\""),
            ("civil_sample", (a, b) =>
                $"{field}.sample_time.civil_time >= \"{Day(a)}\" AND " +
                $"{field}.sample_time.civil_time < \"{Day(b)}\""),
            // Без фильтра — если тип его не поддерживает вовсе.
            ("none", (_, _) => ""),
        };

        // Наиболее вероятный вариант ставим первым.
        var preferred = dataType switch
        {
            "sleep" => "interval.end",
            _ when dataType.StartsWith("daily-") => "date",
            _ when _sampleTypes.Contains(dataType) => "sample_time",
            _ => "interval.start",
        };
        return all.OrderByDescending(c => c.Item1 == preferred).ToList();
    }

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

    /// <summary>
    /// Реально выданные токену скоупы (Google tokeninfo). Нужны, чтобы
    /// отличить «нет прав в токене» от «тип закрыт для приложения»:
    /// после одобрения новых типов старый refresh-токен прав не получает —
    /// требуется переподключение аккаунта.
    /// </summary>
    private async Task<string?> GetGrantedScopesAsync(
        string accessToken, CancellationToken ct)
    {
        try
        {
            var http = httpFactory.CreateClient();
            var res = await http.GetAsync(
                $"https://oauth2.googleapis.com/tokeninfo?access_token={accessToken}", ct);
            if (!res.IsSuccessStatusCode) return null;
            var json = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
            if (!json.TryGetProperty("scope", out var scope)) return null;
            // Оставляем только короткие имена googlehealth-скоупов.
            var names = (scope.GetString() ?? "")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Contains("googlehealth.")
                    ? s[(s.IndexOf("googlehealth.", StringComparison.Ordinal) + 13)..]
                    : s)
                .Where(s => !s.StartsWith("http"));
            return string.Join(", ", names);
        }
        catch
        {
            return null;
        }
    }

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
        // Сводка: импортированные типы, реальные ошибки и типы, закрытые
        // Google (по-типовый доступ приложения / верификация).
        var imported = new List<string>();
        var ok = new List<string>();
        var failed = new List<string>();
        var restricted = new List<string>();
        string? parseSample = null;

        foreach (var (dataType, metric) in Mappings)
        {
            try
            {
                var (n, usedFilter) = await SyncDataTypeWithInfoAsync(
                    db, http, conn.UserId, dataType, metric, from, now, ct);
                inserted += n;
                // Указываем сработавший вариант фильтра — так видно, какой
                // формат приняли типы, где раньше была ошибка.
                if (n > 0) imported.Add($"{dataType}:{n}");
                else if (usedFilter is not null) ok.Add($"{dataType}({usedFilter}):0");
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Google Health sync failed for {DataType}", dataType);
                if (e.Message.Contains("RESTRICTION"))
                {
                    restricted.Add(dataType);
                }
                else
                {
                    failed.Add($"{dataType}:{ShortCode(e.Message)}");
                    if (e.Message.Contains("не распознано")) parseSample ??= e.Message;
                }
            }
        }

        await db.SaveChangesAsync(ct);
        conn.LastSyncAt = now;
        var lines = new List<string>();
        if (imported.Count > 0) lines.Add($"Загружено: {string.Join(", ", imported)}");
        if (ok.Count > 0) lines.Add($"Доступно, но пусто: {string.Join(", ", ok)}");
        if (restricted.Count > 0)
        {
            var scopes = await GetGrantedScopesAsync(accessToken, ct);
            lines.Add($"Закрыто Google: {string.Join(", ", restricted)}");
            lines.Add(scopes is null
                ? "Права токена определить не удалось."
                : $"Права текущего токена: {scopes}. Если нужных прав нет — " +
                  "нажмите «Отключить» и подключитесь заново (после одобрения " +
                  "новых типов старый токен прав не получает).");
        }
        if (failed.Count > 0) lines.Add($"Ошибки: {string.Join(", ", failed)}");
        if (parseSample is not null) lines.Add(parseSample);
        conn.LastError = failed.Count == 0 && restricted.Count == 0
            ? null
            : Truncate(string.Join("\n", lines), 1000);
        await db.SaveChangesAsync(ct);
        return inserted;
    }

    /// <summary>
    /// Один тип данных: GET-метод list с фильтром по интервалу
    /// (AIP-160), с пагинацией. Парсим точки и добавляем недостающие
    /// HealthSample (идемпотентно по ClientId).
    /// </summary>
    private async Task<(int Inserted, string? Filter)> SyncDataTypeWithInfoAsync(
        AppDbContext db, HttpClient http, Guid userId, string dataType,
        MetricType metric, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var points = new List<(DateTimeOffset At, double Value)>();
        JsonElement? rawSample = null;
        var rawTotal = 0;
        // Какой вариант фильтра сработал (для диагностики в сводке).
        string? usedFilter = null;

        var field = ToCamelCase(dataType);
        // Высокочастотные «сырые» типы (пульс) API отдаёт только узким
        // окном — иначе DATA_TYPE_RESTRICTION. Тянем окнами.
        var windowDays = dataType == "heart-rate" ? 1 : 14;

        var windowStart = from;
        while (windowStart < to)
        {
            var windowEnd = windowStart.AddDays(windowDays);
            if (windowEnd > to) windowEnd = to;

            // Вариант фильтра подбирается автоматически (см. Candidates):
            // документация не описывает ограничения для всех типов, поэтому
            // перебираем кандидатов и запоминаем сработавший.
            var candidates = FilterCandidates(dataType, field);
            string? filter = null;
            string? lastErr = null;
            // Принятый, но пустой вариант держим как запасной: возможно,
            // другой вариант фильтра реально отдаст записи.
            string? emptyFilter = null;
            string? emptyName = null;
            foreach (var (name, build) in candidates)
            {
                var probe = build(windowStart, windowEnd);
                var probeUrl = $"{ApiBase}/{dataType}/dataPoints" +
                               (probe.Length == 0
                                   ? "?pageSize=1"
                                   : $"?filter={Uri.EscapeDataString(probe)}&pageSize=1");
                var probeRes = await http.GetAsync(probeUrl, ct);
                if (probeRes.IsSuccessStatusCode)
                {
                    var probeJson = await probeRes.Content.ReadFromJsonAsync<JsonElement>(ct);
                    var (_, probeRaw, _) = ExtractPointsDiag(probeJson, field);
                    if (probeRaw > 0)
                    {
                        filter = probe;
                        usedFilter = name;
                        break;
                    }
                    emptyFilter ??= probe;
                    emptyName ??= name;
                    continue;
                }
                if (probeRes.StatusCode == System.Net.HttpStatusCode.NotFound) return (0, null);
                lastErr = await probeRes.Content.ReadAsStringAsync(ct);
            }
            if (filter is null && emptyFilter is not null)
            {
                filter = emptyFilter;
                usedFilter = emptyName;
            }
            if (filter is null)
            {
                throw new HttpRequestException(
                    $"{dataType} [400]: {Truncate(lastErr ?? "no variant accepted", 300)}");
            }

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
                    if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return (0, null);
                    var errBody = await res.Content.ReadAsStringAsync(ct);
                    throw new HttpRequestException(
                        $"{dataType} [{(int)res.StatusCode}]: {Truncate(errBody, 300)}");
                }
                var json = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
                var (extracted, seenRaw, sample) = ExtractPointsDiag(json, field);
                points.AddRange(extracted);
                rawTotal += seenRaw;
                rawSample ??= sample;
                pageToken = json.TryGetProperty("nextPageToken", out var np)
                    ? np.GetString()
                    : null;
            } while (!string.IsNullOrEmpty(pageToken) && ++pages < 20);

            windowStart = windowEnd;
        }

        // Запрос прошёл, точки есть, но значение не распозналось — сохраняем
        // образец JSON, чтобы поправить парсинг под реальный формат.
        if (points.Count == 0 && rawTotal > 0 && rawSample is JsonElement s)
        {
            throw new HttpRequestException(
                $"{dataType}: получено {rawTotal}, но не распознано. Пример: " +
                Truncate(s.GetRawText(), 300));
        }
        if (points.Count == 0) return (0, usedFilter);

        // Накопительные за сутки типы Google отдаёт снимками счётчика
        // (несколько записей за день) — оставляем одну, максимальную.
        if (_cumulativeTypes.Contains(dataType))
        {
            points = points
                .GroupBy(p => p.At.ToUniversalTime().Date)
                .Select(g => g.OrderByDescending(p => p.Value).First())
                .ToList();
        }

        // Идемпотентность: не дублируем уже загруженные точки.
        var clientIds = points
            .Select(p => ClientId(dataType, p.At))
            .ToList();
        var existing = (await db.Observations
            .Where(o => o.UserId == userId && o.ClientId != null &&
                        clientIds.Contains(o.ClientId))
            .Select(o => o.ClientId!)
            .ToListAsync(ct)).ToHashSet();

        var metricCode = MetricCodeMap.ToCode(metric);
        var device = await new TrackingStore(db)
            .ResolveDeviceAsync(userId, "google_health");

        var inserted = 0;
        var seen = new HashSet<string>();
        foreach (var p in points)
        {
            var cid = ClientId(dataType, p.At);
            if (existing.Contains(cid) || !seen.Add(cid)) continue;
            var value = Convert(metric, p.Value);
            // Не пишем мусор: единица могла не распознаться.
            if (!IsPlausible(metric, value))
            {
                logger.LogWarning(
                    "Google Health: implausible {Metric} value {Value} (raw {Raw})",
                    metric, value, p.Value);
                continue;
            }
            db.Observations.Add(new Observation
            {
                UserId = userId,
                MetricCode = metricCode,
                ValueNum = value,
                StartAt = p.At,
                DeviceInstanceId = device?.Id,
                ClientId = cid,
            });
            inserted++;
        }
        return (inserted, usedFilter);
    }

    /// <summary>
    /// Ключ идемпотентности. У накопительных за сутки типов — по дате,
    /// чтобы более поздний снимок счётчика не создавал вторую запись.
    /// </summary>
    private static string ClientId(string dataType, DateTimeOffset at) =>
        _cumulativeTypes.Contains(dataType)
            ? $"gh-{dataType}-{at.ToUniversalTime():yyyy-MM-dd}"
            : $"gh-{dataType}-{at.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}";

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max];

    /// <summary>Короткий код причины из текста ошибки Google (для сводки).</summary>
    private static string ShortCode(string message)
    {
        if (message.Contains("RESTRICTION")) return "ERR:RESTRICTION";
        if (message.Contains("MEMBER")) return "ERR:MEMBER";
        if (message.Contains("Unknown name")) return "ERR:FIELD";
        if (message.Contains("PERMISSION") || message.Contains("[403")) return "ERR:PERM";
        if (message.Contains("не распознано")) return "ERR:PARSE";
        if (message.Contains("[404")) return "ERR:404";
        if (message.Contains("[400")) return "ERR:400";
        if (message.Contains("[401")) return "ERR:401";
        // Неизвестная причина — показываем начало реального текста,
        // иначе диагностировать нечего.
        var flat = message.Replace('\n', ' ').Replace('\r', ' ');
        return $"ERR:{Truncate(flat, 120)}";
    }

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

    /// <summary>
    /// Приведение к нашим единицам после ExtractValueWithUnit (длина там
    /// уже в метрах, масса в кг, объём в литрах).
    /// </summary>
    private static double Convert(MetricType metric, double v) => metric switch
    {
        MetricType.Distance => v > 100 ? v / 1000 : v, // метры → км
        // Сон уже считается в часах из интервала; на всякий случай
        // распознаём секунды (>1440) и минуты (>24).
        MetricType.Sleep => v > 1440 ? v / 3600 : v > 24 ? v / 60 : v,
        MetricType.Height => v < 3 ? v * 100 : v, // метры → см
        _ => v,
    };

    /// <summary>
    /// Правдоподобность значения — не даём мусору (вес 150000, SpO₂ 0.9)
    /// попасть в базу, даже если единицу распознать не удалось.
    /// </summary>
    private static bool IsPlausible(MetricType metric, double v) => metric switch
    {
        MetricType.Weight => v is > 2 and < 400,
        MetricType.Height => v is > 30 and < 260,
        MetricType.HeartRate or MetricType.RestingHeartRate or
            MetricType.WalkingHeartRate => v is >= 25 and <= 260,
        MetricType.BloodOxygen or MetricType.BodyFat => v is > 1 and <= 100,
        MetricType.BodyTemperature => v is >= 30 and <= 45,
        MetricType.RespiratoryRate => v is >= 3 and <= 60,
        MetricType.BloodGlucose => v is > 0.5 and < 40,
        MetricType.Hrv => v is > 0 and < 400,
        MetricType.Sleep => v is > 0 and <= 24,
        MetricType.Bmi => v is > 5 and < 100,
        _ => v >= 0,
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
        JsonElement? Sample) ExtractPointsDiag(JsonElement root, string field)
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
            // Значение и время вложены в поле с именем типа (напр. "weight").
            var payload = el.TryGetProperty(field, out var p) &&
                          p.ValueKind == JsonValueKind.Object
                ? p
                : el;
            var at = ExtractTime(payload) ?? ExtractTime(el);
            // Сон приходит интервалом — длительность считаем из начала и
            // конца, а не из случайного числового поля.
            var value = field == "sleep"
                ? ExtractDurationHours(payload)
                : ExtractValueWithUnit(payload);
            if (value is double v && at is DateTimeOffset t) result.Add((t, v));
        }
        return (result, raw, sample);
    }

    // Ключи, которые не являются значением показателя (время/источник/мета).
    private static readonly HashSet<string> _nonValueKeys =
    [
        "sampleTime", "interval", "date", "dataSource", "origin", "name",
        "id", "startUtcOffset", "endUtcOffset", "utcOffset",
    ];

    /// <summary>
    /// Коэффициенты приведения к нашим единицам по имени поля-единицы.
    /// Google отдаёт величину в поле, названном единицей (grams, meters...),
    /// поэтому определяем единицу по имени, а не угадываем по величине.
    /// </summary>
    private static readonly (string Key, double Factor)[] _unitFactors =
    [
        // Масса → кг
        ("micrograms", 1e-9), ("milligrams", 1e-6), ("grams", 1e-3),
        ("kilograms", 1), ("pounds", 0.45359237), ("ounces", 0.0283495),
        // Длина → метры (дальше Convert переводит в км/см по метрике)
        ("millimeters", 0.001), ("centimeters", 0.01), ("meters", 1),
        ("kilometers", 1000), ("miles", 1609.344), ("inches", 0.0254),
        ("feet", 0.3048),
        // Энергия → ккал
        ("kilocalories", 1), ("calories", 0.001), ("kilojoules", 0.239006),
        ("joules", 0.000239006),
        // Объём → литры
        ("milliliters", 0.001), ("liters", 1), ("fluidOuncesUs", 0.0295735),
        // Прочее — как есть
        ("celsius", 1), ("beatsPerMinute", 1), ("percentage", 1),
        ("percent", 1), ("milliseconds", 1), ("count", 1), ("steps", 1),
        ("breathsPerMinute", 1), ("millimolesPerLiter", 1),
    ];

    /// <summary>
    /// CivilDateTime Google → DateTimeOffset (UTC). Часы/минуты опциональны.
    /// </summary>
    private static DateTimeOffset? ParseCivil(JsonElement el)
    {
        int? Get(string name) =>
            el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
                ? v.GetInt32()
                : null;

        var year = Get("year");
        var month = Get("month");
        var day = Get("day");
        if (year is null || month is null || day is null) return null;
        try
        {
            return new DateTimeOffset(
                year.Value, month.Value, day.Value,
                Get("hours") ?? 12, Get("minutes") ?? 0, Get("seconds") ?? 0,
                TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>
    /// Длительность в часах из интервала (start/end) — для сна и других
    /// интервальных типов, где величина не хранится числом.
    /// </summary>
    private static double? ExtractDurationHours(JsonElement el)
    {
        if (!el.TryGetProperty("interval", out var interval) ||
            interval.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        DateTimeOffset? Read(params string[] keys)
        {
            foreach (var k in keys)
            {
                if (interval.TryGetProperty(k, out var v) &&
                    v.ValueKind == JsonValueKind.String &&
                    DateTimeOffset.TryParse(v.GetString(), out var dt))
                    return dt;
            }
            return null;
        }

        var start = Read("startTime", "start_time");
        var end = Read("endTime", "end_time");
        if (start is null || end is null) return null;
        var hours = (end.Value - start.Value).TotalHours;
        return hours > 0 ? Math.Round(hours, 2) : null;
    }

    /// <summary>
    /// Значение с учётом единицы: ищем поле, названное единицей измерения,
    /// и приводим к нашей. Если такого нет — берём первое число (fallback).
    /// Отдельно обрабатываем °F и mg/dL.
    /// </summary>
    private static double? ExtractValueWithUnit(JsonElement el, int depth = 0)
    {
        if (depth > 4) return null;
        if (el.ValueKind != JsonValueKind.Object) return ExtractNumericLeaf(el, depth);

        foreach (var prop in el.EnumerateObject())
        {
            if (_nonValueKeys.Contains(prop.Name)) continue;

            // Фаренгейты и mg/dL требуют формулы, а не множителя.
            if (prop.Name.Equals("fahrenheit", StringComparison.OrdinalIgnoreCase) &&
                ExtractNumericLeaf(prop.Value, depth) is double f)
                return (f - 32) * 5 / 9;
            if (prop.Name.Contains("milligramsPerDeciliter",
                    StringComparison.OrdinalIgnoreCase) &&
                ExtractNumericLeaf(prop.Value, depth) is double mgdl)
                return mgdl / 18.0; // mg/dL → ммоль/л

            foreach (var (key, factor) in _unitFactors)
            {
                if (!prop.Name.Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
                if (ExtractNumericLeaf(prop.Value, depth) is double v)
                    return v * factor;
            }

            // Вложенный объект может нести единицу глубже.
            if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                var nested = ExtractValueWithUnit(prop.Value, depth + 1);
                if (nested is not null) return nested;
            }
        }
        // Единицу не нашли — прежнее поведение.
        return ExtractNumericLeaf(el, depth);
    }

    /// <summary>
    /// Первое числовое значение в объекте, кроме служебных полей (время,
    /// смещения, источник). Так достаём величину показателя из вложенной
    /// структуры v4, не зная точного имени поля значения. Числа-строки
    /// тоже принимаем — протобуф кодирует int64 строкой.
    /// </summary>
    private static double? ExtractNumericLeaf(JsonElement el, int depth)
    {
        if (depth > 4) return null;
        switch (el.ValueKind)
        {
            case JsonValueKind.Number:
                return el.GetDouble();
            case JsonValueKind.String:
                return double.TryParse(
                    el.GetString(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsed)
                    ? parsed
                    : null;
            case JsonValueKind.Object:
                foreach (var prop in el.EnumerateObject())
                {
                    if (_nonValueKeys.Contains(prop.Name)) continue;
                    if (prop.Name.Contains("Time") || prop.Name.Contains("time"))
                        continue;
                    var v = ExtractNumericLeaf(prop.Value, depth + 1);
                    if (v is not null) return v;
                }
                return null;
            default:
                return null;
        }
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
        // Гражданская дата приходит объектом {year, month, day[, hours...]}.
        foreach (var key in new[]
                 {
                     "date", "civilDate", "civilTime", "civilStartTime",
                     "civilEndTime"
                 })
        {
            if (el.TryGetProperty(key, out var civil) &&
                civil.ValueKind == JsonValueKind.Object &&
                ParseCivil(civil) is DateTimeOffset cd)
                return cd;
        }

        // Время может быть вложено: interval (интервальные) или
        // sampleTime (точечные).
        foreach (var nested in new[] {"interval", "sampleTime", "sample_time"})
        {
            if (el.TryGetProperty(nested, out var obj) &&
                obj.ValueKind == JsonValueKind.Object)
            {
                var t = ExtractTime(obj);
                if (t is not null) return t;
            }
        }
        foreach (var key in new[]
                 {
                     "startTime", "start_time", "physicalTime", "physical_time",
                     "date", "startDate", "civilStartTime", "endTime",
                     "end_time", "time", "civilTime"
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
