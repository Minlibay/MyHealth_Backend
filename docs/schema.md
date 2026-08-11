# MyHealth — структура базы данных

PostgreSQL 17, EF Core (.NET 10). Модель трекинга нормализована: **справочники** описывают, что вообще бывает, **данные** ссылаются на справочники кодами.

**Три слоя:**

| Слой | Таблицы |
|---|---|
| Справочники (реестры) | `MetricDefinitions`, `EventTypeDefinitions`, `SourceEventTypeMaps`, `VendorMetricDefinitions`, `ValueDictionary`, `ReferenceRanges` |
| Данные пользователя | `DeviceInstances`, `Observations`, `Events`, `MeasurementEventLinks`, `VendorMetrics`, `DerivedMetrics` |
| Служебные | `Users`, `RefreshTokens`, `GoogleHealthConnections`, `TagEvents` |

**Общие правила:**
- Все `Id` данных — `uuid` (PK); у справочников PK — сам код (`MetricCode`, `EventTypeCode`, `VendorMetricCode`).
- Время — `timestamptz` (UTC). Местное смещение при измерении — отдельная колонка `TimezoneOffset` (`interval`).
- Данные привязаны к пользователю через `UserId` с каскадным удалением (удаление аккаунта стирает всё — GDPR).
- `ClientId` — ключ идемпотентности: повторная выгрузка той же записи не создаёт дубль (уникальный частичный индекс).
- **Разделение ответственности:** наши измерения — в `Observations`, готовые оценки вендоров (Readiness, Body Battery…) — в `VendorMetrics`, наши расчёты — в `DerivedMetrics`. Смешивать нельзя: у вендорских шкал разные формулы и они не сравнимы между собой.
- Скоры, тренды, зоны пульса, VO₂max, ночные показатели считаются **на лету** и в БД не хранятся (`DerivedMetrics` — задел, если понадобится сохранять).

---

## Справочники

Заполняются автоматически при старте приложения из JSON (`src/MyHealth.Api/Data/Seed`, `RegistrySeeder`). Обновление идемпотентно: существующие записи обновляются, новые добавляются, ничего не удаляется. Доступны клиенту через `GET /api/registry/*` без авторизации.

### MetricDefinitions — реестр показателей

Единый код показателя для всех источников: `cardio.hr.instant`, `activity.steps`, `sleep.efficiency`.

| Колонка | Тип | Описание |
|---|---|---|
| `MetricCode` | varchar(96) | **PK**, код показателя |
| `Name` | varchar(256) | название |
| `Domain` | varchar(96) | домен: Сердце / Активность / Сон / Тело / Дыхание… |
| `Grain` | varchar(16) | `instant` \| `interval` \| `daily` \| `session` |
| `Trigger` | varchar(32) | `continuous` \| `on_demand` \| `derived` \| `manual` |
| `Derivation` | varchar(32) | `raw` \| `aggregate` \| `model` \| `vendor` |
| `Episodes` | text | бывают ли эпизоды (например, апноэ) |
| `ValueType` | varchar(16) | `number` \| `json` \| `text` \| `boolean` |
| `Unit` | varchar(48) | единица измерения |
| `VendorOura`, `VendorGarmin`, `VendorAppleWatch`, `VendorWhoop`, `VendorRing` | boolean | доступность у типовых устройств (справочно) |

В реестре 67 показателей: 52 из спецификации + 15 добавлены под то, что уже собирает приложение (рост, ИМТ, вода, минуты стояния, дистанция за тренировку и т.д.). Источник справочника — `metric_definitions.json`.

### EventTypeDefinitions — реестр типов событий

| Колонка | Тип | Описание |
|---|---|---|
| `EventTypeCode` | varchar(96) | **PK**: `workout.running`, `sleep.main`, `activity.stand`… |
| `Name` | varchar(256) | название |
| `Group` | varchar(96) | группа: Сон / Тренировка / Активность / Сессия |
| `WhenCreated` | text | когда событие возникает |
| `TimeBounds` | text | чем заданы границы времени |
| `RelatedData` | text | какие значения к нему привязаны |
| `Mvp` | boolean | входит в MVP |

230 типов из спецификации (+ `workout.unspecified` для активности без подтипа): 155 видов тренировок, 38 сессий восстановления, 34 вида активности, 3 вида сна. 28 помечены как MVP.

### SourceEventTypeMaps — «тип события источника → наш код»

| Колонка | Тип | Описание |
|---|---|---|
| `Id` | integer | PK (identity) |
| `Source` | varchar(64) | `apple_health` \| `health_connect` \| `google_health` \| `ring` |
| `Entity` | text | сущность в API источника |
| `SourceEventType` | varchar(160) | как называет источник (`HKWorkoutActivityTypeRunning`) |
| `EventTypeCode` | varchar(96) | наш код события |
| `Availability` | varchar(64) | доступность в API |
| `Note` | text | примечание |

**Индекс:** `UNIQUE (Source, SourceEventType)`. 367 соответствий.

### VendorMetricDefinitions — реестр вендорских оценок

Описывает готовые баллы вендоров: что за шкала, известна ли формула, можно ли сравнивать.

Ключевые колонки: `VendorMetricCode` (**PK**), `Name`, `Vendor` (`oura` \| `garmin` \| `whoop` \| `apple` \| `fitbit`), `ScaleUnit` (шкала, например 0–100), `VendorMetricType` (`score` \| `index` \| `state`), `Direction`, `UsePolicy` (как разрешено использовать), `ComparisonRule` (сравнимость между вендорами), `FormulaTransparency`, `KnownInputs`, `VendorApi`, `AppleHealth`, `HealthConnect`, `AvailableInMvp`, `Docs`. 20 записей.

### ValueDictionary — словарь допустимых значений

Разрешённые значения служебных колонок: `grain`, `trigger`, `derivation`, `device_type`, `link_method`.

| Колонка | Тип | Описание |
|---|---|---|
| `Id` | integer | PK (identity) |
| `Column` | varchar(48) | к какой колонке относится |
| `Value` | varchar(64) | значение |
| `Label`, `WhenSet`, `Example` | text | пояснения |

**Индекс:** `UNIQUE (Column, Value)`.

### ReferenceRanges — референсные диапазоны

`Id`, `MetricCode`, `Population` (`all` \| `male` \| `female` \| возрастная группа), `MinNormal`, `MaxNormal`, `MinWarn`, `MaxWarn`, `Unit`, `Source`. **Индекс:** `UNIQUE (MetricCode, Population)`.

---

## DeviceInstances — источники данных пользователя

Конкретное устройство или приложение, откуда пришли данные. Прежняя строка источника `apple_health:JCVitalPro` раскладывается на `IntegrationPlatform` + `DataOriginAppId`.

| Колонка | Тип | Null | Описание |
|---|---|---|---|
| `Id` | uuid | нет | PK |
| `UserId` | uuid | нет | FK → Users, каскад |
| `IntegrationPlatform` | varchar(32) | нет | `apple_health` \| `health_connect` \| `google_health` \| `ring` \| `manual` |
| `SourceDeviceId` | varchar(160) | да | идентификатор устройства в API источника |
| `DeviceType` | varchar(24) | нет | `ring` \| `watch` \| `phone` \| `band` \| `scale` \| `other` \| `unknown` |
| `DeviceName` | varchar(128) | да | название (`Apple Watch`, `JCRing X3`) |
| `Manufacturer` | varchar(96) | да | производитель |
| `Model` | varchar(96) | да | модель |
| `DataOriginAppId` | varchar(160) | да | приложение, записавшее данные (`JCVitalPro`) |
| `CreatedAt` | timestamptz | нет | первое появление |

**Индекс:** `(UserId, IntegrationPlatform, DataOriginAppId, SourceDeviceId)`.

Устройство создаётся автоматически при первой записи с новым источником.

---

## Observations — значения показателей

Главная таблица данных. Сюда идут все измерения с любого источника: пульс, шаги, SpO₂, вес, давление, сон, температура и т.д.

| Колонка | Тип | Null | Описание |
|---|---|---|---|
| `Id` | uuid | нет | PK |
| `UserId` | uuid | нет | FK → Users, каскад |
| `MetricCode` | varchar(96) | нет | FK → MetricDefinitions |
| `ValueNum` | double | да | числовое значение; для давления — систолическое |
| `ValueJson` | jsonb | да | структурное значение (например стадии сна) |
| `ValueSecondary` | double | да | доп. значение; для давления — диастолическое |
| `Unit` | varchar(48) | да | единица, если источник её передал |
| `StartAt` | timestamptz | нет | время измерения (для интервала — начало) |
| `EndAt` | timestamptz | да | конец интервала |
| `TimezoneOffset` | interval | да | местное смещение в момент измерения |
| `DeviceInstanceId` | uuid | да | FK → DeviceInstances (`ON DELETE SET NULL`) |
| `SourceRecordId` | varchar(160) | да | id записи в API источника |
| `SourceUpdatedAt` | timestamptz | да | версия записи у источника |
| `ClientId` | varchar(160) | да | ключ идемпотентности |
| `CreatedAt` | timestamptz | нет | момент попадания на сервер |

**Индексы:**
- `UNIQUE (UserId, ClientId) WHERE ClientId IS NOT NULL` — защита от дублей.
- `(UserId, MetricCode, StartAt)` — выборка истории показателя.
- `(MetricCode)`, `(DeviceInstanceId)`.

---

## Events — события

Всё, что имеет начало и конец: сон, тренировка, сессия активности.

| Колонка | Тип | Null | Описание |
|---|---|---|---|
| `Id` | uuid | нет | PK |
| `UserId` | uuid | нет | FK → Users, каскад |
| `EventTypeCode` | varchar(96) | нет | FK → EventTypeDefinitions |
| `EventName` | varchar(256) | нет | название для показа |
| `StartAt` | timestamptz | нет | начало |
| `EndAt` | timestamptz | да | конец (у длящегося события пусто) |
| `TimezoneOffset` | interval | да | местное смещение |
| `DeviceInstanceId` | uuid | да | FK → DeviceInstances (`ON DELETE SET NULL`) |
| `SourceRecordId` | varchar(160) | да | id записи у источника |
| `SourceEventType` | varchar(160) | да | как назвал источник (до маппинга) |
| `SourceUpdatedAt` | timestamptz | да | версия записи у источника |
| `SourceParentRecordId` | varchar(160) | да | вложенность: интервал внутри сессии |
| `ClientId` | varchar(160) | да | ключ идемпотентности |
| `CreatedAt` | timestamptz | нет | момент попадания на сервер |

**Индексы:** `UNIQUE (UserId, ClientId) WHERE ClientId IS NOT NULL`; `(UserId, EventTypeCode, StartAt)`; `(UserId, StartAt)`; `(EventTypeCode)`; `(DeviceInstanceId)`.

**Тренировка** = событие `workout.*`; её калории и дистанция — отдельные `Observations` (`activity.calories.session`, `activity.distance.session`), связанные через `MeasurementEventLinks`. Средний/максимальный пульс, минуты в зонах и TRIMP считаются при запросе из `cardio.hr.instant` за окно события.

**Сон** = событие `sleep.main`; стадии — `Observation` с `MetricCode = sleep.stages` и `ValueJson`:

```json
[
  {"stage": "light", "start": "2026-07-20T22:40:00Z", "end": "2026-07-20T23:15:00Z"},
  {"stage": "deep",  "start": "2026-07-20T23:15:00Z", "end": "2026-07-20T23:55:00Z"},
  {"stage": "rem",   "start": "2026-07-20T23:55:00Z", "end": "2026-07-21T00:30:00Z"},
  {"stage": "awake", "start": "2026-07-21T00:30:00Z", "end": "2026-07-21T00:35:00Z"}
]
```
`stage` ∈ `deep | light | rem | awake`.

---

## MeasurementEventLinks — связь значение ↔ событие

Одно значение может относиться к событию (калории тренировки), и одно событие имеет много значений.

| Колонка | Тип | Null | Описание |
|---|---|---|---|
| `Id` | uuid | нет | PK |
| `MeasurementId` | uuid | нет | id значения |
| `MeasurementType` | varchar(24) | нет | `observation` \| `vendor_metric` \| `derived_metric` |
| `EventId` | uuid | нет | FK → Events, каскад |
| `LinkMethod` | varchar(24) | нет | `source_explicit` \| `time_overlap` \| `derived` \| `manual` |
| `LinkedAt` | timestamptz | нет | когда связали |

**Индексы:** `UNIQUE (EventId, MeasurementId, MeasurementType)`; `(MeasurementId, MeasurementType)`.

`LinkMethod` важен для доверия к данным: `source_explicit` — связь пришла от источника, `time_overlap` — мы связали по пересечению времени.

---

## VendorMetrics — готовые оценки вендоров

Баллы, которые считает вендор (Oura Readiness, Garmin Body Battery, Whoop Recovery). Держим отдельно от своих измерений.

| Колонка | Тип | Null | Описание |
|---|---|---|---|
| `Id` | uuid | нет | PK |
| `UserId` | uuid | нет | FK → Users, каскад |
| `VendorMetricCode` | varchar(96) | нет | FK → VendorMetricDefinitions |
| `ValueNum` | double | да | балл по шкале вендора |
| `ValueText` | varchar(256) | да | состояние словом |
| `Unit` | varchar(48) | да | единица/шкала |
| `EffectiveAt` | timestamptz | нет | на какой момент оценка |
| `PeriodEndAt` | timestamptz | да | конец периода оценки |
| `SourceRecordId` | varchar(160) | да | id записи у вендора |
| `SourceState` | varchar(32) | да | статус записи у вендора |
| `SourceUpdatedAt` | timestamptz | да | версия записи |
| `SourceDetails` | jsonb | да | исходный ответ как есть |
| `SourceDetailsSchemaVersion` | varchar(64) | да | версия схемы ответа |
| `DeviceInstanceId` | uuid | да | FK → DeviceInstances (`ON DELETE SET NULL`) |
| `ClientId` | varchar(160) | да | ключ идемпотентности |
| `CreatedAt` | timestamptz | нет | момент попадания на сервер |

**Индексы:** `UNIQUE (UserId, ClientId) WHERE ClientId IS NOT NULL`; `(UserId, VendorMetricCode, EffectiveAt)`; `(VendorMetricCode)`; `(DeviceInstanceId)`.

---

## DerivedMetrics — наши расчёты

Задел под сохранение собственных вычислений (сейчас скоры считаются на лету).

| Колонка | Тип | Null | Описание |
|---|---|---|---|
| `Id` | uuid | нет | PK |
| `UserId` | uuid | нет | FK → Users, каскад |
| `MetricCode` | varchar(96) | нет | код нашего показателя |
| `Name` | varchar(256) | нет | название |
| `ValueNum` | double | да | значение |
| `ValueJson` | jsonb | да | структурный результат |
| `Unit` | varchar(48) | да | единица |
| `EffectiveAt` | timestamptz | нет | на какой момент |
| `PeriodStartAt`, `PeriodEndAt` | timestamptz | да | период расчёта |
| `AlgorithmVersion` | varchar(32) | да | версия формулы |
| `FactorsJson` | jsonb | да | вклад факторов (объяснимость) |
| `CreatedAt` | timestamptz | нет | момент расчёта |

**Индекс:** `UNIQUE (UserId, MetricCode, EffectiveAt)`.

---

## Users — владелец данных, профиль и цели

| Колонка | Тип | Null | Описание |
|---|---|---|---|
| `Id` | uuid | нет | PK |
| `Email` | varchar(256) | нет | логин (уникальный) |
| `PasswordHash` | text | нет | BCrypt-хеш |
| `DisplayName` | varchar(128) | да | имя |
| `Gender` | varchar(16) | да | `male` / `female` |
| `Age` | integer | да | возраст (зоны пульса) |
| `HeightCm` | double | да | рост |
| `WeightKg` | double | да | вес |
| `StepsGoal` | integer | да | цель по шагам |
| `WaterGoalLiters` | double | да | цель по воде |
| `SleepGoalHours` | double | да | цель по сну |
| `KcalGoal` | integer | да | цель по калориям |
| `CreatedAt` | timestamptz | нет | регистрация |

**Индекс:** `UNIQUE (Email)`.

---

## Служебные таблицы

### TagEvents — журнал отметок
`Id`, `UserId` (FK, каскад), `Tag` (varchar 64: `coffee`, `alcohol`, `late_meal`, `sick`, `stress`…), `At`, `CreatedAt`. **Индекс:** `(UserId, At)`.

### GoogleHealthConnections — подключение к Google Health API
`Id`, `UserId` (FK, `UNIQUE`), `RefreshToken` (text), `Scopes` (varchar 1024), `ConnectedAt`, `LastSyncAt`, `LastError`.

### RefreshTokens — токены авторизации приложения
`Id`, `UserId` (FK), `TokenHash` (varchar, `UNIQUE`), `ExpiresAt`, `RevokedAt`, `CreatedAt`.

---

## Прежние таблицы

`Samples`, `Workouts`, `SleepSessions` остались от предыдущей (ненормализованной) модели. При первом старте приложения их содержимое один раз переносится в `Observations` / `Events` / `MeasurementEventLinks` (`LegacyDataMigrator`, признак переноса — `ClientId` с префиксом `legacy:`). Новые записи туда не пишутся.

---

## Связи

```
Users 1──∞ DeviceInstances
Users 1──∞ Observations ∞──1 MetricDefinitions
Users 1──∞ Events       ∞──1 EventTypeDefinitions
Users 1──∞ VendorMetrics ∞──1 VendorMetricDefinitions
Users 1──∞ DerivedMetrics
Users 1──∞ TagEvents
Users 1──∞ RefreshTokens
Users 1──1 GoogleHealthConnections

DeviceInstances 1──∞ Observations / Events / VendorMetrics
Events 1──∞ MeasurementEventLinks ∞──1 Observations (или VendorMetrics / DerivedMetrics)
EventTypeDefinitions 1──∞ SourceEventTypeMaps
MetricDefinitions 1──∞ ReferenceRanges
```

---

## API поверх схемы

| Эндпоинт | Назначение |
|---|---|
| `GET /api/registry/metrics`, `/event-types`, `/vendor-metrics`, `/dictionary`, `/event-type-map` | справочники (без авторизации) |
| `POST/GET /api/observations` | значения показателей в терминах `metric_code` |
| `POST/GET /api/events`, `GET /api/events/{id}/measurements` | события и привязанные к ним значения |
| `POST/GET /api/vendor-metrics` | вендорские оценки |
| `/api/metrics`, `/api/workouts`, `/api/sleep`, `/api/insights` | прежний контракт мобильного приложения; читают и пишут те же новые таблицы |

Полный runnable-DDL — в [`schema.sql`](schema.sql). Точный скрипт с историей миграций: `dotnet ef migrations script`.
