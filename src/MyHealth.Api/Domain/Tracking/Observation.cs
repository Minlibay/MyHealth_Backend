namespace MyHealth.Api.Domain.Tracking;

/// <summary>
/// Экземпляр устройства-источника (блок «Метаданные источника»).
/// device_instance_id создаём мы всегда; source_device_id — только если
/// исходная платформа его передала.
/// </summary>
public class DeviceInstance
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public AppUser? User { get; set; }

    /// <summary>apple_health · health_connect · google_health · ring · manual.</summary>
    public required string IntegrationPlatform { get; set; }

    /// <summary>ID устройства из исходной платформы (может отсутствовать).</summary>
    public string? SourceDeviceId { get; set; }

    /// <summary>watch · ring · band · chest_strap · scale · bp_monitor · cgm · phone · other · unknown.</summary>
    public string DeviceType { get; set; } = "unknown";

    public string? DeviceName { get; set; }
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }

    /// <summary>Приложение, записавшее значение в хранилище платформы.</summary>
    public string? DataOriginAppId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Одна запись показателя (БД-1, «Контракт одной записи показателя»).
/// Значение хранится числом либо структурой JSON — по value_type метрики.
/// </summary>
public class Observation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public AppUser? User { get; set; }

    /// <summary>Код показателя из реестра (cardio.hr.instant).</summary>
    public required string MetricCode { get; set; }
    public MetricDefinition? Metric { get; set; }

    /// <summary>Числовое значение (для value_type = num).</summary>
    public double? ValueNum { get; set; }

    /// <summary>Составное значение (для value_type = json): стадии, зоны, маршрут.</summary>
    public string? ValueJson { get; set; }

    /// <summary>Доп. числовое значение (диастолическое давление).</summary>
    public double? ValueSecondary { get; set; }

    public string? Unit { get; set; }

    /// <summary>Начало измерения или периода.</summary>
    public DateTimeOffset StartAt { get; set; }

    /// <summary>Конец интервала/суток/сессии (для grain ≠ point).</summary>
    public DateTimeOffset? EndAt { get; set; }

    /// <summary>Локальное смещение от UTC, если известно.</summary>
    public TimeSpan? TimezoneOffset { get; set; }

    // --- Источник ---
    public Guid? DeviceInstanceId { get; set; }
    public DeviceInstance? DeviceInstance { get; set; }

    /// <summary>ID записи в исходной платформе (HKSample UUID).</summary>
    public string? SourceRecordId { get; set; }

    /// <summary>Когда источник последний раз менял запись.</summary>
    public DateTimeOffset? SourceUpdatedAt { get; set; }

    /// <summary>Ключ идемпотентности нашей загрузки.</summary>
    public string? ClientId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Событие-эпизод (БД-2): сон, тренировка, бытовая активность,
/// восстановительная сессия.
/// </summary>
public class TrackedEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public AppUser? User { get; set; }

    /// <summary>Нормализованный код типа (workout.running).</summary>
    public required string EventTypeCode { get; set; }
    public EventTypeDefinition? EventType { get; set; }

    public required string EventName { get; set; }

    public DateTimeOffset StartAt { get; set; }

    /// <summary>Заполняется для завершённого события.</summary>
    public DateTimeOffset? EndAt { get; set; }

    public TimeSpan? TimezoneOffset { get; set; }

    // --- Источник ---
    public Guid? DeviceInstanceId { get; set; }
    public DeviceInstance? DeviceInstance { get; set; }

    public string? SourceRecordId { get; set; }

    /// <summary>Как источник сам назвал тип (HKWorkoutActivityType.running).</summary>
    public string? SourceEventType { get; set; }

    public DateTimeOffset? SourceUpdatedAt { get; set; }

    /// <summary>ID родительской записи у вендора (cycle_456).</summary>
    public string? SourceParentRecordId { get; set; }

    public string? ClientId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Результат расчёта вендора (БД-3): закрытые скоры и статусы.
/// Своих базлайнов по ним не строим — только показ и контекст.
/// </summary>
public class VendorMetric
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public AppUser? User { get; set; }

    public required string VendorMetricCode { get; set; }
    public VendorMetricDefinition? Definition { get; set; }

    public double? ValueNum { get; set; }

    /// <summary>Значение-enum (Resilience: Solid) или структура.</summary>
    public string? ValueText { get; set; }

    public string? Unit { get; set; }

    /// <summary>Дата/начало периода, к которому относится результат.</summary>
    public DateTimeOffset EffectiveAt { get; set; }
    public DateTimeOffset? PeriodEndAt { get; set; }

    public string? SourceRecordId { get; set; }

    /// <summary>Состояние результата у вендора (final · pending).</summary>
    public string? SourceState { get; set; }

    public DateTimeOffset? SourceUpdatedAt { get; set; }

    /// <summary>Breakdown вендора как есть.</summary>
    public string? SourceDetails { get; set; }

    public string? SourceDetailsSchemaVersion { get; set; }

    public Guid? DeviceInstanceId { get; set; }
    public DeviceInstance? DeviceInstance { get; set; }

    public string? ClientId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Связь значения показателя с событием (junction, БД-4).
/// Одно значение может быть связано с несколькими событиями.
/// </summary>
public class MeasurementEventLink
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>ID значения: observation или vendor_metric.</summary>
    public Guid MeasurementId { get; set; }

    /// <summary>observation · vendor_metric.</summary>
    public required string MeasurementType { get; set; }

    public Guid EventId { get; set; }
    public TrackedEvent? Event { get; set; }

    /// <summary>source_explicit · time_overlap · derived · manual.</summary>
    public required string LinkMethod { get; set; }

    public DateTimeOffset LinkedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Наш расчёт (лист «Рассчитанные показатели», derivation = own):
/// скоры, базовые линии, производные показатели.
/// </summary>
public class DerivedMetric
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public AppUser? User { get; set; }

    /// <summary>Код нашего расчёта (own.score.recovery, own.baseline.rhr).</summary>
    public required string MetricCode { get; set; }

    public required string Name { get; set; }

    public double? ValueNum { get; set; }
    public string? ValueJson { get; set; }
    public string? Unit { get; set; }

    public DateTimeOffset EffectiveAt { get; set; }
    public DateTimeOffset? PeriodStartAt { get; set; }
    public DateTimeOffset? PeriodEndAt { get; set; }

    /// <summary>Версия алгоритма — чтобы отличать пересчёты.</summary>
    public string? AlgorithmVersion { get; set; }

    /// <summary>Входы и промежуточные факторы расчёта.</summary>
    public string? FactorsJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Общие нормативные показатели (лист «Общие нормативные показатели»):
/// референсные диапазоны, независимые от конкретного пользователя.
/// </summary>
public class ReferenceRange
{
    public int Id { get; set; }

    public required string MetricCode { get; set; }

    /// <summary>Кому применим: all · male · female · age_18_40 и т.п.</summary>
    public string Population { get; set; } = "all";

    public double? MinNormal { get; set; }
    public double? MaxNormal { get; set; }
    public double? MinWarn { get; set; }
    public double? MaxWarn { get; set; }

    public string? Unit { get; set; }
    public string? Source { get; set; }
}
