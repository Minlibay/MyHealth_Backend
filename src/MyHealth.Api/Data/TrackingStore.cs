using Microsoft.EntityFrameworkCore;
using MyHealth.Api.Domain;
using MyHealth.Api.Domain.Tracking;

namespace MyHealth.Api.Data;

/// <summary>
/// Общие операции над новой схемой трекинга для совместимых эндпоинтов:
/// разбор прежней строки Source в экземпляр устройства и чтение значений
/// показателей в терминах прежнего enum.
/// </summary>
public class TrackingStore(AppDbContext db)
{
    private readonly Dictionary<string, DeviceInstance> _devices = new();

    /// <summary>
    /// Экземпляр устройства по строке источника вида
    /// "apple_health:JCVitalPro" (формат, который присылает приложение).
    /// </summary>
    public async Task<DeviceInstance?> ResolveDeviceAsync(Guid userId, string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        var key = $"{userId}|{source}";
        if (_devices.TryGetValue(key, out var cached)) return cached;

        var i = source.IndexOf(':');
        var platform = i < 0 ? source : source[..i];
        var app = i < 0 ? null : source[(i + 1)..];

        var device = await db.DeviceInstances.FirstOrDefaultAsync(d =>
            d.UserId == userId &&
            d.IntegrationPlatform == platform &&
            d.DataOriginAppId == app);
        if (device is null)
        {
            device = new DeviceInstance
            {
                UserId = userId,
                IntegrationPlatform = platform,
                DataOriginAppId = app,
                DeviceName = app,
                DeviceType = GuessDeviceType(platform, app),
            };
            db.DeviceInstances.Add(device);
        }
        _devices[key] = device;
        return device;
    }

    private static string GuessDeviceType(string platform, string? app)
    {
        if (platform == "ring") return "ring";
        if (platform == "manual") return "other";
        var a = app?.ToLowerInvariant() ?? "";
        if (a.Contains("watch")) return "watch";
        if (a.Contains("ring") || a.Contains("jcvital")) return "ring";
        if (a.Contains("iphone") || a.Contains("phone")) return "phone";
        return "unknown";
    }

    /// <summary>Строка источника в прежнем формате — для ответов API.</summary>
    public static string? SourceString(DeviceInstance? device) =>
        device is null
            ? null
            : device.DataOriginAppId is null
                ? device.IntegrationPlatform
                : $"{device.IntegrationPlatform}:{device.DataOriginAppId}";

    /// <summary>
    /// Значения показателя за период в терминах прежнего enum.
    /// Читает новую таблицу observations.
    /// </summary>
    public IQueryable<Observation> Query(
        Guid userId, MetricType? metric, DateTimeOffset? from, DateTimeOffset? to)
    {
        var q = db.Observations.AsNoTracking()
            .Include(o => o.DeviceInstance)
            .Where(o => o.UserId == userId);

        if (metric is not null)
        {
            var code = MetricCodeMap.ToCode(metric.Value);
            q = q.Where(o => o.MetricCode == code);
        }
        else
        {
            // Только коды, которые понимает совместимый API.
            var codes = MetricCodeMap.AllCodes.ToList();
            q = q.Where(o => codes.Contains(o.MetricCode));
        }

        if (from is not null) q = q.Where(o => o.StartAt >= from);
        if (to is not null) q = q.Where(o => o.StartAt <= to);
        return q;
    }
}
