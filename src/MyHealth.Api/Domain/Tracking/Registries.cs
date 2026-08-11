namespace MyHealth.Api.Domain.Tracking;

/// <summary>
/// Справочник показателей (лист «Показатели»). Классификация задаёт
/// политику анализа: по vendor_score базлайны не строим, vendor_estimate
/// сравниваем только внутри одного вендора.
/// </summary>
public class MetricDefinition
{
    /// <summary>Код вида cardio.hr.instant — первичный ключ реестра.</summary>
    public required string MetricCode { get; set; }

    public required string Name { get; set; }
    public string? Domain { get; set; }

    /// <summary>point · interval · daily · session.</summary>
    public string? Grain { get; set; }

    /// <summary>continuous · on_demand · opportunistic · manual.</summary>
    public string? Trigger { get; set; }

    /// <summary>measured · vendor_aggregate · vendor_estimate · vendor_score · own.</summary>
    public string? Derivation { get; set; }

    /// <summary>Контексты-эпизоды: day · sleep · workout · walk.</summary>
    public string? Episodes { get; set; }

    /// <summary>num · json.</summary>
    public string? ValueType { get; set; }

    public string? Unit { get; set; }

    // Покрытие вендорами (матрица из таблицы).
    public bool VendorOura { get; set; }
    public bool VendorGarmin { get; set; }
    public bool VendorAppleWatch { get; set; }
    public bool VendorWhoop { get; set; }

    /// <summary>Наше кольцо/браслет JCRing — в таблице колонки нет, ведём сами.</summary>
    public bool VendorRing { get; set; }
}

/// <summary>Справочник типов событий (лист «События»).</summary>
public class EventTypeDefinition
{
    /// <summary>Код вида workout.running · sleep.main.</summary>
    public required string EventTypeCode { get; set; }

    public required string Name { get; set; }

    /// <summary>Сон · Тренировка / спорт · Бытовая активность · Восстановление.</summary>
    public string? Group { get; set; }

    public string? WhenCreated { get; set; }
    public string? TimeBounds { get; set; }
    public string? RelatedData { get; set; }

    /// <summary>Входит в MVP-набор продукта.</summary>
    public bool Mvp { get; set; }
}

/// <summary>
/// Маппинг типа события источника в наш нормализованный код
/// (Apple Health, Health Connect, WHOOP, Oura, Garmin).
/// </summary>
public class SourceEventTypeMap
{
    public int Id { get; set; }

    /// <summary>Apple Health · Health Connect · WHOOP · Oura API · Garmin API.</summary>
    public required string Source { get; set; }

    public string? Entity { get; set; }

    /// <summary>Как назвал тип сам источник (HKWorkoutActivityType.running).</summary>
    public required string SourceEventType { get; set; }

    public required string EventTypeCode { get; set; }

    /// <summary>напрямую · через маппинг · ограниченно · рассчитываем · deprecated.</summary>
    public string? Availability { get; set; }

    public string? Note { get; set; }
}

/// <summary>
/// Справочник вендорских показателей (лист «Рассчитанные показатели
/// вендора») с политикой использования.
/// </summary>
public class VendorMetricDefinition
{
    /// <summary>Код вида whoop.score.recovery.</summary>
    public required string VendorMetricCode { get; set; }

    public required string Name { get; set; }
    public string? Domain { get; set; }
    public string? Grain { get; set; }
    public string? Episodes { get; set; }
    public string? ValueType { get; set; }

    /// <summary>Шкала или единица: 0–100, 0–21, enum.</summary>
    public string? ScaleUnit { get; set; }

    public string? Vendor { get; set; }
    public string? VendorField { get; set; }

    /// <summary>composite_score · status · recommendation.</summary>
    public string? VendorMetricType { get; set; }

    public string? Direction { get; set; }

    /// <summary>display_only · assistant_context.</summary>
    public string? UsePolicy { get; set; }

    public string? ComparisonRule { get; set; }
    public string? FormulaTransparency { get; set; }
    public string? KnownInputs { get; set; }
    public string? VendorApi { get; set; }
    public string? AppleHealth { get; set; }
    public string? HealthConnect { get; set; }
    public bool AvailableInMvp { get; set; }
    public string? Docs { get; set; }
}

/// <summary>Словарь значений enum (лист «Словaрь знaчений»).</summary>
public class ValueDictionaryEntry
{
    public int Id { get; set; }

    /// <summary>Колонка, к которой относится значение: grain · trigger · …</summary>
    public required string Column { get; set; }

    public required string Value { get; set; }
    public string? Label { get; set; }
    public string? WhenSet { get; set; }
    public string? Example { get; set; }
}
