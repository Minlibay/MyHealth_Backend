using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MyHealth.Api.Domain.Tracking;

namespace MyHealth.Api.Data;

/// <summary>
/// Загружает справочники трекинга (показатели, типы событий, вендорские
/// показатели, маппинг источников, словарь значений) из JSON рядом с
/// приложением. Идемпотентно: обновляет существующие записи и добавляет
/// новые, ничего не удаляя — справочники версионируются вместе с кодом.
/// </summary>
public static class RegistrySeeder
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web);

    public static async Task SeedAsync(AppDbContext db, ILogger logger, string seedDir)
    {
        if (!Directory.Exists(seedDir))
        {
            logger.LogWarning("Registry seed directory not found: {Dir}", seedDir);
            return;
        }

        await SeedMetricsAsync(db, logger, seedDir);
        await SeedEventTypesAsync(db, logger, seedDir);
        await SeedSourceMapAsync(db, logger, seedDir);
        await SeedVendorMetricsAsync(db, logger, seedDir);
        await SeedValueDictionaryAsync(db, logger, seedDir);
        await SeedLinkMethodsAsync(db, logger, seedDir);
        await db.SaveChangesAsync();
    }

    private static List<T>? Load<T>(string seedDir, string file, ILogger logger)
    {
        var path = Path.Combine(seedDir, file);
        if (!File.Exists(path))
        {
            logger.LogWarning("Registry seed file missing: {File}", file);
            return null;
        }
        return JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path), Json);
    }

    // --- Показатели ---

    private sealed record MetricSeed(
        string? Domain, string MetricCode, string Name, string? Grain,
        string? Trigger, string? Derivation, string? Episodes,
        string? ValueType, string? Unit, Dictionary<string, bool>? Vendors);

    private static async Task SeedMetricsAsync(
        AppDbContext db, ILogger logger, string dir)
    {
        var seed = Load<MetricSeed>(dir, "metric_definitions.json", logger);
        if (seed is null) return;

        var existing = await db.MetricDefinitions.ToDictionaryAsync(m => m.MetricCode);
        foreach (var s in seed)
        {
            var m = existing.GetValueOrDefault(s.MetricCode);
            if (m is null)
            {
                m = new MetricDefinition { MetricCode = s.MetricCode, Name = s.Name };
                db.MetricDefinitions.Add(m);
                // В справочнике возможны повторы кода — второй проход
                // должен обновлять уже добавленную запись, а не дублировать её.
                existing[s.MetricCode] = m;
            }
            m.Name = s.Name;
            m.Domain = s.Domain;
            m.Grain = s.Grain;
            m.Trigger = s.Trigger;
            m.Derivation = s.Derivation;
            m.Episodes = s.Episodes;
            m.ValueType = s.ValueType;
            m.Unit = s.Unit;
            m.VendorOura = s.Vendors?.GetValueOrDefault("oura") ?? false;
            m.VendorGarmin = s.Vendors?.GetValueOrDefault("garmin") ?? false;
            m.VendorAppleWatch = s.Vendors?.GetValueOrDefault("appleWatch") ?? false;
            m.VendorWhoop = s.Vendors?.GetValueOrDefault("whoop") ?? false;
        }
        logger.LogInformation("Registry: {Count} metric definitions", seed.Count);
    }

    // --- Типы событий ---

    private sealed record EventTypeSeed(
        string EventTypeCode, string? Group, string Name, string? WhenCreated,
        string? TimeBounds, string? RelatedData, bool Mvp);

    private static async Task SeedEventTypesAsync(
        AppDbContext db, ILogger logger, string dir)
    {
        var seed = Load<EventTypeSeed>(dir, "event_types.json", logger);
        if (seed is null) return;

        var existing = await db.EventTypeDefinitions
            .ToDictionaryAsync(t => t.EventTypeCode);
        foreach (var s in seed)
        {
            var t = existing.GetValueOrDefault(s.EventTypeCode);
            if (t is null)
            {
                t = new EventTypeDefinition
                {
                    EventTypeCode = s.EventTypeCode, Name = s.Name,
                };
                db.EventTypeDefinitions.Add(t);
                existing[s.EventTypeCode] = t;
            }
            t.Name = s.Name;
            t.Group = s.Group;
            t.WhenCreated = s.WhenCreated;
            t.TimeBounds = s.TimeBounds;
            t.RelatedData = s.RelatedData;
            t.Mvp = s.Mvp;
        }
        logger.LogInformation("Registry: {Count} event types", seed.Count);
    }

    // --- Маппинг типов источников ---

    private sealed record SourceMapSeed(
        string Source, string? Entity, string SourceEventType,
        string EventTypeCode, string? Availability, string? Note);

    private static async Task SeedSourceMapAsync(
        AppDbContext db, ILogger logger, string dir)
    {
        var seed = Load<SourceMapSeed>(dir, "source_event_type_map.json", logger);
        if (seed is null) return;

        // Типы событий, которых нет в реестре, пропускаем — иначе FK упадёт.
        var knownTypes = await db.EventTypeDefinitions
            .Select(t => t.EventTypeCode).ToListAsync();
        var known = knownTypes
            .Concat(db.EventTypeDefinitions.Local.Select(t => t.EventTypeCode))
            .ToHashSet();

        var existing = await db.SourceEventTypeMaps
            .ToDictionaryAsync(m => (m.Source, m.SourceEventType));
        var added = 0;
        foreach (var s in seed.DistinctBy(x => (x.Source, x.SourceEventType)))
        {
            if (!known.Contains(s.EventTypeCode)) continue;
            var m = existing.GetValueOrDefault((s.Source, s.SourceEventType));
            if (m is null)
            {
                m = new SourceEventTypeMap
                {
                    Source = s.Source,
                    SourceEventType = s.SourceEventType,
                    EventTypeCode = s.EventTypeCode,
                };
                db.SourceEventTypeMaps.Add(m);
                existing[(s.Source, s.SourceEventType)] = m;
                added++;
            }
            m.Entity = s.Entity;
            m.EventTypeCode = s.EventTypeCode;
            m.Availability = s.Availability;
            m.Note = s.Note;
        }
        logger.LogInformation(
            "Registry: source event map, {Added} new of {Total}", added, seed.Count);
    }

    // --- Вендорские показатели ---

    private sealed record VendorMetricSeed(
        string? Domain, string VendorMetricCode, string Name, string? Grain,
        string? Episodes, string? ValueType, string? ScaleUnit, string? Vendor,
        string? VendorField, string? VendorMetricType, string? Direction,
        string? UsePolicy, string? ComparisonRule, string? FormulaTransparency,
        string? KnownInputs, string? VendorApi, string? AppleHealth,
        string? HealthConnect, bool AvailableInMvp, string? Docs);

    private static async Task SeedVendorMetricsAsync(
        AppDbContext db, ILogger logger, string dir)
    {
        var seed = Load<VendorMetricSeed>(dir, "vendor_metric_definitions.json", logger);
        if (seed is null) return;

        var existing = await db.VendorMetricDefinitions
            .ToDictionaryAsync(v => v.VendorMetricCode);
        foreach (var s in seed)
        {
            var v = existing.GetValueOrDefault(s.VendorMetricCode);
            if (v is null)
            {
                v = new VendorMetricDefinition
                {
                    VendorMetricCode = s.VendorMetricCode, Name = s.Name,
                };
                db.VendorMetricDefinitions.Add(v);
                existing[s.VendorMetricCode] = v;
            }
            v.Name = s.Name;
            v.Domain = s.Domain;
            v.Grain = s.Grain;
            v.Episodes = s.Episodes;
            v.ValueType = s.ValueType;
            v.ScaleUnit = s.ScaleUnit;
            v.Vendor = s.Vendor;
            v.VendorField = s.VendorField;
            v.VendorMetricType = s.VendorMetricType;
            v.Direction = s.Direction;
            v.UsePolicy = s.UsePolicy;
            v.ComparisonRule = s.ComparisonRule;
            v.FormulaTransparency = s.FormulaTransparency;
            v.KnownInputs = s.KnownInputs;
            v.VendorApi = s.VendorApi;
            v.AppleHealth = s.AppleHealth;
            v.HealthConnect = s.HealthConnect;
            v.AvailableInMvp = s.AvailableInMvp;
            v.Docs = s.Docs;
        }
        logger.LogInformation("Registry: {Count} vendor metrics", seed.Count);
    }

    // --- Словарь значений ---

    private sealed record ValueSeed(
        string Column, string Value, string? Label, string? WhenSet, string? Example);

    private static async Task SeedValueDictionaryAsync(
        AppDbContext db, ILogger logger, string dir)
    {
        var seed = Load<ValueSeed>(dir, "value_dictionary.json", logger);
        if (seed is null) return;

        var existing = await db.ValueDictionary
            .ToDictionaryAsync(v => (v.Column, v.Value));
        foreach (var s in seed)
        {
            var v = existing.GetValueOrDefault((s.Column, s.Value));
            if (v is null)
            {
                v = new ValueDictionaryEntry { Column = s.Column, Value = s.Value };
                db.ValueDictionary.Add(v);
                existing[(s.Column, s.Value)] = v;
            }
            v.Label = s.Label;
            v.WhenSet = s.WhenSet;
            v.Example = s.Example;
        }
        logger.LogInformation("Registry: {Count} dictionary values", seed.Count);
    }

    // --- Способы связи измерения с событием ---

    private sealed record LinkMethodSeed(string Code, string? Label, string? WhenSet);

    /// <summary>
    /// Способы связи (link_method) кладём в общий словарь значений —
    /// колонка "link_method", чтобы клиент читал их одним эндпоинтом.
    /// </summary>
    private static async Task SeedLinkMethodsAsync(
        AppDbContext db, ILogger logger, string dir)
    {
        var seed = Load<LinkMethodSeed>(dir, "link_methods.json", logger);
        if (seed is null) return;

        var existing = await db.ValueDictionary
            .Where(v => v.Column == "link_method")
            .ToDictionaryAsync(v => v.Value);
        foreach (var s in seed)
        {
            var v = existing.GetValueOrDefault(s.Code);
            if (v is null)
            {
                v = new ValueDictionaryEntry { Column = "link_method", Value = s.Code };
                db.ValueDictionary.Add(v);
                existing[s.Code] = v;
            }
            v.Label = s.Label;
            v.WhenSet = s.WhenSet;
        }
        logger.LogInformation("Registry: {Count} link methods", seed.Count);
    }
}
