using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using MyHealth.Api.Common;
using MyHealth.Api.Data;
using MyHealth.Api.Domain.Tracking;
using MyHealth.Api.Features.Metrics;

namespace MyHealth.Api.Features.Tracking;

// --- Реестры ---

public record MetricDefinitionDto(
    string MetricCode, string Name, string? Domain, string? Grain,
    string? Trigger, string? Derivation, string? Episodes, string? ValueType,
    string? Unit, bool Oura, bool Garmin, bool AppleWatch, bool Whoop);

public record EventTypeDto(
    string EventTypeCode, string Name, string? Group, bool Mvp);

public record VendorMetricDefinitionDto(
    string VendorMetricCode, string Name, string? Vendor, string? Grain,
    string? ScaleUnit, string? VendorMetricType, string? UsePolicy,
    string? Direction, bool AvailableInMvp);

// --- Данные ---

public record ObservationDto(
    Guid Id, string MetricCode, string? MetricName, double? Value,
    double? Secondary, string? ValueJson, string? Unit, DateTimeOffset StartAt,
    DateTimeOffset? EndAt, string? Source, string? DeviceType);

public record ObservationUploadDto(
    string MetricCode, double? Value, double? Secondary, string? ValueJson,
    string? Unit, DateTimeOffset StartAt, DateTimeOffset? EndAt,
    string? TimezoneOffset, string? IntegrationPlatform, string? DataOriginAppId,
    string? DeviceType, string? DeviceName, string? Manufacturer, string? Model,
    string? SourceRecordId, string? ClientId);

public record EventDto(
    Guid Id, string EventTypeCode, string EventName, DateTimeOffset StartAt,
    DateTimeOffset? EndAt, string? SourceEventType, string? Source,
    double? DurationMinutes);

public record EventUploadDto(
    string? EventTypeCode, string? SourceEventType, string? Source,
    string EventName, DateTimeOffset StartAt, DateTimeOffset? EndAt,
    string? IntegrationPlatform, string? DataOriginAppId,
    string? SourceRecordId, string? SourceParentRecordId, string? ClientId);

public record VendorMetricUploadDto(
    string VendorMetricCode, double? Value, string? ValueText, string? Unit,
    DateTimeOffset EffectiveAt, DateTimeOffset? PeriodEndAt,
    string? SourceRecordId, string? SourceState, string? SourceDetails,
    string? SourceDetailsSchemaVersion, string? ClientId);

public record VendorMetricDto(
    Guid Id, string VendorMetricCode, string? Name, double? Value,
    string? ValueText, string? Unit, DateTimeOffset EffectiveAt,
    string? Vendor, string? UsePolicy);

/// <summary>
/// API новой модели трекинга: реестры, значения показателей, события,
/// вендорские результаты и связи между ними.
/// </summary>
public static class TrackingEndpoints
{
    public static IEndpointRouteBuilder MapTrackingEndpoints(
        this IEndpointRouteBuilder app)
    {
        MapRegistry(app);
        MapObservations(app);
        MapEvents(app);
        MapVendorMetrics(app);
        return app;
    }

    // ===== Реестры (публичные, без авторизации — это справочники) =====

    private static void MapRegistry(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/registry").WithTags("Registry");

        group.MapGet("/metrics", async (AppDbContext db, string? domain) =>
        {
            var q = db.MetricDefinitions.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(domain)) q = q.Where(m => m.Domain == domain);
            var data = await q.OrderBy(m => m.Domain).ThenBy(m => m.MetricCode)
                .Select(m => new MetricDefinitionDto(
                    m.MetricCode, m.Name, m.Domain, m.Grain, m.Trigger,
                    m.Derivation, m.Episodes, m.ValueType, m.Unit,
                    m.VendorOura, m.VendorGarmin, m.VendorAppleWatch, m.VendorWhoop))
                .ToListAsync();
            return Results.Ok(data);
        });

        group.MapGet("/event-types", async (AppDbContext db, bool? mvpOnly) =>
        {
            var q = db.EventTypeDefinitions.AsNoTracking();
            if (mvpOnly == true) q = q.Where(t => t.Mvp);
            var data = await q.OrderBy(t => t.Group).ThenBy(t => t.EventTypeCode)
                .Select(t => new EventTypeDto(
                    t.EventTypeCode, t.Name, t.Group, t.Mvp))
                .ToListAsync();
            return Results.Ok(data);
        });

        group.MapGet("/vendor-metrics", async (AppDbContext db) =>
        {
            var data = await db.VendorMetricDefinitions.AsNoTracking()
                .OrderBy(v => v.Vendor).ThenBy(v => v.VendorMetricCode)
                .Select(v => new VendorMetricDefinitionDto(
                    v.VendorMetricCode, v.Name, v.Vendor, v.Grain, v.ScaleUnit,
                    v.VendorMetricType, v.UsePolicy, v.Direction, v.AvailableInMvp))
                .ToListAsync();
            return Results.Ok(data);
        });

        group.MapGet("/dictionary", async (AppDbContext db, string? column) =>
        {
            var q = db.ValueDictionary.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(column)) q = q.Where(v => v.Column == column);
            var data = await q.OrderBy(v => v.Column).ThenBy(v => v.Value)
                .Select(v => new { v.Column, v.Value, v.Label, v.WhenSet, v.Example })
                .ToListAsync();
            return Results.Ok(data);
        });

        // Маппинг типа события источника в наш код.
        group.MapGet("/event-type-map", async (
            AppDbContext db, string source, string sourceEventType) =>
        {
            var code = await db.SourceEventTypeMaps.AsNoTracking()
                .Where(m => m.Source == source &&
                            m.SourceEventType == sourceEventType)
                .Select(m => m.EventTypeCode)
                .FirstOrDefaultAsync();
            return code is null ? Results.NotFound() : Results.Ok(new { code });
        });
    }

    // ===== Значения показателей =====

    private static void MapObservations(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/observations")
            .WithTags("Observations")
            .RequireAuthorization();

        group.MapPost("/", async (
            List<ObservationUploadDto> items, ClaimsPrincipal principal,
            AppDbContext db) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();
            if (items.Count == 0) return Results.Ok(new UploadResult(0, 0));

            var known = (await db.MetricDefinitions
                .Select(m => m.MetricCode).ToListAsync()).ToHashSet();
            var clientIds = items.Where(i => i.ClientId is not null)
                .Select(i => i.ClientId!).ToList();
            var existing = (await db.Observations
                .Where(o => o.UserId == userId && o.ClientId != null &&
                            clientIds.Contains(o.ClientId))
                .Select(o => o.ClientId!)
                .ToListAsync()).ToHashSet();

            var devices = new DeviceRegistry(db, userId.Value);
            var inserted = 0;
            var seen = new HashSet<string>();
            foreach (var i in items)
            {
                if (!known.Contains(i.MetricCode)) continue;
                if (i.ClientId is not null &&
                    (existing.Contains(i.ClientId) || !seen.Add(i.ClientId)))
                    continue;

                var device = await devices.ResolveAsync(
                    i.IntegrationPlatform, i.DataOriginAppId, i.DeviceType,
                    i.DeviceName, i.Manufacturer, i.Model);
                db.Observations.Add(new Observation
                {
                    UserId = userId.Value,
                    MetricCode = i.MetricCode,
                    ValueNum = i.Value,
                    ValueSecondary = i.Secondary,
                    ValueJson = i.ValueJson,
                    Unit = i.Unit,
                    StartAt = i.StartAt,
                    EndAt = i.EndAt,
                    TimezoneOffset = ParseOffset(i.TimezoneOffset),
                    DeviceInstanceId = device?.Id,
                    SourceRecordId = i.SourceRecordId,
                    ClientId = i.ClientId,
                });
                inserted++;
            }
            await db.SaveChangesAsync();
            return Results.Ok(new UploadResult(inserted, items.Count - inserted));
        });

        group.MapGet("/", async (
            ClaimsPrincipal principal, AppDbContext db,
            string? metricCode, DateTimeOffset? from, DateTimeOffset? to,
            int limit = 500) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var q = db.Observations.AsNoTracking()
                .Include(o => o.Metric)
                .Include(o => o.DeviceInstance)
                .Where(o => o.UserId == userId);
            if (!string.IsNullOrWhiteSpace(metricCode))
                q = q.Where(o => o.MetricCode == metricCode);
            if (from is not null) q = q.Where(o => o.StartAt >= from);
            if (to is not null) q = q.Where(o => o.StartAt <= to);

            var data = await q.OrderByDescending(o => o.StartAt)
                .Take(Math.Clamp(limit, 1, 5000))
                .Select(o => new ObservationDto(
                    o.Id, o.MetricCode, o.Metric!.Name, o.ValueNum,
                    o.ValueSecondary, o.ValueJson, o.Unit ?? o.Metric.Unit,
                    o.StartAt, o.EndAt,
                    o.DeviceInstance == null
                        ? null
                        : o.DeviceInstance.IntegrationPlatform +
                          (o.DeviceInstance.DataOriginAppId == null
                              ? ""
                              : ":" + o.DeviceInstance.DataOriginAppId),
                    o.DeviceInstance == null ? null : o.DeviceInstance.DeviceType))
                .ToListAsync();
            return Results.Ok(data);
        });
    }

    // ===== События =====

    private static void MapEvents(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/events")
            .WithTags("Events")
            .RequireAuthorization();

        group.MapPost("/", async (
            List<EventUploadDto> items, ClaimsPrincipal principal, AppDbContext db) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();
            if (items.Count == 0) return Results.Ok(new UploadResult(0, 0));

            var known = (await db.EventTypeDefinitions
                .Select(t => t.EventTypeCode).ToListAsync()).ToHashSet();
            var map = await db.SourceEventTypeMaps.AsNoTracking()
                .ToDictionaryAsync(m => (m.Source, m.SourceEventType),
                    m => m.EventTypeCode);

            var clientIds = items.Where(i => i.ClientId is not null)
                .Select(i => i.ClientId!).ToList();
            var existing = (await db.Events
                .Where(e => e.UserId == userId && e.ClientId != null &&
                            clientIds.Contains(e.ClientId))
                .Select(e => e.ClientId!)
                .ToListAsync()).ToHashSet();

            var devices = new DeviceRegistry(db, userId.Value);
            var inserted = 0;
            var seen = new HashSet<string>();
            foreach (var i in items)
            {
                if (i.ClientId is not null &&
                    (existing.Contains(i.ClientId) || !seen.Add(i.ClientId)))
                    continue;

                // Код берём явный, иначе через маппинг источника.
                var code = i.EventTypeCode;
                if ((code is null || !known.Contains(code)) &&
                    i.Source is not null && i.SourceEventType is not null &&
                    map.TryGetValue((i.Source, i.SourceEventType), out var mapped))
                {
                    code = mapped;
                }
                if (code is null || !known.Contains(code)) code = "workout.unspecified";

                var device = await devices.ResolveAsync(
                    i.IntegrationPlatform, i.DataOriginAppId, null, null, null, null);
                db.Events.Add(new TrackedEvent
                {
                    UserId = userId.Value,
                    EventTypeCode = code,
                    EventName = i.EventName,
                    StartAt = i.StartAt,
                    EndAt = i.EndAt,
                    SourceEventType = i.SourceEventType,
                    SourceRecordId = i.SourceRecordId,
                    SourceParentRecordId = i.SourceParentRecordId,
                    DeviceInstanceId = device?.Id,
                    ClientId = i.ClientId,
                });
                inserted++;
            }
            await db.SaveChangesAsync();
            return Results.Ok(new UploadResult(inserted, items.Count - inserted));
        });

        group.MapGet("/", async (
            ClaimsPrincipal principal, AppDbContext db,
            string? eventTypeCode, string? group_, DateTimeOffset? from,
            DateTimeOffset? to, int limit = 200) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var q = db.Events.AsNoTracking()
                .Include(e => e.EventType)
                .Include(e => e.DeviceInstance)
                .Where(e => e.UserId == userId);
            if (!string.IsNullOrWhiteSpace(eventTypeCode))
                q = q.Where(e => e.EventTypeCode == eventTypeCode);
            if (!string.IsNullOrWhiteSpace(group_))
                q = q.Where(e => e.EventType!.Group == group_);
            if (from is not null) q = q.Where(e => e.StartAt >= from);
            if (to is not null) q = q.Where(e => e.StartAt <= to);

            var data = await q.OrderByDescending(e => e.StartAt)
                .Take(Math.Clamp(limit, 1, 2000))
                .Select(e => new EventDto(
                    e.Id, e.EventTypeCode, e.EventName, e.StartAt, e.EndAt,
                    e.SourceEventType,
                    e.DeviceInstance == null
                        ? null
                        : e.DeviceInstance.IntegrationPlatform,
                    e.EndAt == null
                        ? null
                        : (e.EndAt.Value - e.StartAt).TotalMinutes))
                .ToListAsync();
            return Results.Ok(data);
        });

        // Значения, связанные с событием (через junction).
        group.MapGet("/{id:guid}/measurements", async (
            Guid id, ClaimsPrincipal principal, AppDbContext db) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var links = await db.MeasurementEventLinks.AsNoTracking()
                .Where(l => l.EventId == id)
                .ToListAsync();
            var obsIds = links.Where(l => l.MeasurementType == "observation")
                .Select(l => l.MeasurementId).ToList();

            var data = await db.Observations.AsNoTracking()
                .Include(o => o.Metric)
                .Where(o => o.UserId == userId && obsIds.Contains(o.Id))
                .Select(o => new ObservationDto(
                    o.Id, o.MetricCode, o.Metric!.Name, o.ValueNum,
                    o.ValueSecondary, o.ValueJson, o.Unit, o.StartAt, o.EndAt,
                    null, null))
                .ToListAsync();
            return Results.Ok(data);
        });
    }

    // ===== Вендорские показатели =====

    private static void MapVendorMetrics(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/vendor-metrics")
            .WithTags("VendorMetrics")
            .RequireAuthorization();

        group.MapPost("/", async (
            List<VendorMetricUploadDto> items, ClaimsPrincipal principal,
            AppDbContext db) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();
            if (items.Count == 0) return Results.Ok(new UploadResult(0, 0));

            var known = (await db.VendorMetricDefinitions
                .Select(v => v.VendorMetricCode).ToListAsync()).ToHashSet();
            var clientIds = items.Where(i => i.ClientId is not null)
                .Select(i => i.ClientId!).ToList();
            var existing = (await db.VendorMetrics
                .Where(v => v.UserId == userId && v.ClientId != null &&
                            clientIds.Contains(v.ClientId))
                .Select(v => v.ClientId!)
                .ToListAsync()).ToHashSet();

            var inserted = 0;
            var seen = new HashSet<string>();
            foreach (var i in items)
            {
                if (!known.Contains(i.VendorMetricCode)) continue;
                if (i.ClientId is not null &&
                    (existing.Contains(i.ClientId) || !seen.Add(i.ClientId)))
                    continue;

                db.VendorMetrics.Add(new VendorMetric
                {
                    UserId = userId.Value,
                    VendorMetricCode = i.VendorMetricCode,
                    ValueNum = i.Value,
                    ValueText = i.ValueText,
                    Unit = i.Unit,
                    EffectiveAt = i.EffectiveAt,
                    PeriodEndAt = i.PeriodEndAt,
                    SourceRecordId = i.SourceRecordId,
                    SourceState = i.SourceState,
                    SourceDetails = i.SourceDetails,
                    SourceDetailsSchemaVersion = i.SourceDetailsSchemaVersion,
                    ClientId = i.ClientId,
                });
                inserted++;
            }
            await db.SaveChangesAsync();
            return Results.Ok(new UploadResult(inserted, items.Count - inserted));
        });

        group.MapGet("/", async (
            ClaimsPrincipal principal, AppDbContext db,
            string? vendorMetricCode, DateTimeOffset? from, int limit = 200) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var q = db.VendorMetrics.AsNoTracking()
                .Include(v => v.Definition)
                .Where(v => v.UserId == userId);
            if (!string.IsNullOrWhiteSpace(vendorMetricCode))
                q = q.Where(v => v.VendorMetricCode == vendorMetricCode);
            if (from is not null) q = q.Where(v => v.EffectiveAt >= from);

            var data = await q.OrderByDescending(v => v.EffectiveAt)
                .Take(Math.Clamp(limit, 1, 2000))
                .Select(v => new VendorMetricDto(
                    v.Id, v.VendorMetricCode, v.Definition!.Name, v.ValueNum,
                    v.ValueText, v.Unit ?? v.Definition.ScaleUnit, v.EffectiveAt,
                    v.Definition.Vendor, v.Definition.UsePolicy))
                .ToListAsync();
            return Results.Ok(data);
        });
    }

    private static TimeSpan? ParseOffset(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? null
            : TimeSpan.TryParse(raw.TrimStart('.', '+'), out var ts)
                ? (raw.Contains('-') ? -ts : ts)
                : null;

    /// <summary>Находит или создаёт экземпляр устройства пользователя.</summary>
    private sealed class DeviceRegistry(AppDbContext db, Guid userId)
    {
        private readonly Dictionary<string, DeviceInstance> _cache = new();

        public async Task<DeviceInstance?> ResolveAsync(
            string? platform, string? app, string? deviceType, string? deviceName,
            string? manufacturer, string? model)
        {
            if (string.IsNullOrWhiteSpace(platform)) return null;
            var key = $"{platform}|{app}";
            if (_cache.TryGetValue(key, out var cached)) return cached;

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
                    DeviceType = deviceType ?? "unknown",
                    DeviceName = deviceName ?? app,
                    Manufacturer = manufacturer,
                    Model = model,
                };
                db.DeviceInstances.Add(device);
            }
            else
            {
                // Обогащаем метаданные, если пришли новые.
                device.DeviceType = deviceType ?? device.DeviceType;
                device.DeviceName = deviceName ?? device.DeviceName;
                device.Manufacturer = manufacturer ?? device.Manufacturer;
                device.Model = model ?? device.Model;
            }
            _cache[key] = device;
            return device;
        }
    }
}
