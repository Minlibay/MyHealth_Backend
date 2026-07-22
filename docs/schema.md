# MyHealth — структура базы данных

PostgreSQL 17, EF Core (.NET 10). Ниже — таблицы, куда складываются данные с трекеров (Apple Health, Health Connect, Google Health, кольцо/браслет JCRing, ручной ввод), и связанные служебные таблицы.

**Общие правила:**
- Все `Id` — `uuid` (первичный ключ).
- Время — `timestamptz` (UTC, `timestamp with time zone`).
- Данные привязаны к пользователю через `UserId` с каскадным удалением (удаление аккаунта стирает все данные — GDPR).
- `ClientId` — ключ идемпотентности: повторная выгрузка той же записи не создаёт дубль (уникальный частичный индекс).
- **Расчёты не хранятся**: скоры, тренды, зоны пульса, VO₂max, ночные показатели считаются на лету из таблиц ниже.

---

## Samples — точечные показатели

Главная таблица. Сюда идут все измерения с любого источника: пульс, шаги, SpO₂, вес, давление, глюкоза, температура и т.д.

| Колонка | Тип | Null | Описание |
|---|---|---|---|
| `Id` | uuid | нет | PK |
| `UserId` | uuid | нет | FK → Users, каскад |
| `Metric` | varchar(32) | нет | тип показателя (enum строкой, см. ниже) |
| `Value` | double | нет | значение; для давления — систолическое |
| `Secondary` | double | да | доп. значение; для давления — диастолическое |
| `Unit` | varchar(32) | да | единица измерения, если передана |
| `RecordedAt` | timestamptz | нет | время измерения на устройстве |
| `Source` | varchar(128) | да | источник, формат `ключ:приложение` (см. ниже) |
| `ClientId` | varchar(128) | да | ключ идемпотентности |
| `CreatedAt` | timestamptz | нет | момент попадания на сервер |

**Индексы:**
- `UNIQUE (UserId, ClientId) WHERE ClientId IS NOT NULL` — защита от дублей.
- `(UserId, Metric, RecordedAt)` — выборка истории показателя.

**Значения `Metric` (17):** `Steps`, `HeartRate`, `BloodPressure`, `Weight`, `Sleep`, `BloodGlucose`, `BloodOxygen`, `ActiveEnergy`, `Distance`, `Water`, `BodyTemperature`, `RespiratoryRate`, `RestingHeartRate`, `Hrv`, `BodyFat`, `Height`, `DietaryEnergy`.

**Единицы по метрикам:** шаги — шт; пульс/пульс покоя — уд/мин; давление — мм рт.ст.; вес — кг; сон — часы; глюкоза — ммоль/л; SpO₂/жир — %; активные и потреблённые калории — ккал; дистанция — км; вода — л; температура — °C; дыхание — вд/мин; HRV — мс; рост — см.

**Формат `Source`** — `ключ:приложение` (часть после `:` необязательна):

| Пример | Что значит |
|---|---|
| `apple_health:Apple Watch` | Apple Health, записало приложение Apple Watch |
| `apple_health:JCVitalPro` | Apple Health, записало приложение кольца |
| `health_connect:Google Fit` | Health Connect (Android), источник Google Fit |
| `google_health` | облако Google Health (Fitbit) |
| `ring:JCRing X3` | напрямую с кольца по Bluetooth |
| `manual` | ручной ввод пользователем |

---

## Workouts — тренировки

| Колонка | Тип | Null | Описание |
|---|---|---|---|
| `Id` | uuid | нет | PK |
| `UserId` | uuid | нет | FK → Users, каскад |
| `ActivityType` | varchar(64) | нет | тип активности (`RUNNING`, `YOGA`, `BIKING`…) |
| `StartedAt` | timestamptz | нет | начало |
| `EndedAt` | timestamptz | нет | конец |
| `EnergyKcal` | double | да | сожжённые калории |
| `DistanceMeters` | double | да | дистанция, метры |
| `Source` | varchar(128) | да | источник (как у Samples) |
| `ClientId` | varchar(128) | да | ключ идемпотентности |
| `CreatedAt` | timestamptz | нет | момент попадания на сервер |

**Индексы:** `UNIQUE (UserId, ClientId) WHERE ClientId IS NOT NULL`; `(UserId, StartedAt)`.

> Средний/максимальный пульс, минуты в зонах пульса и TRIMP **не хранятся** — считаются при запросе из `Samples` за окно тренировки.

---

## SleepSessions — сон с фазами

| Колонка | Тип | Null | Описание |
|---|---|---|---|
| `Id` | uuid | нет | PK |
| `UserId` | uuid | нет | FK → Users, каскад |
| `StartedAt` | timestamptz | нет | начало сна |
| `EndedAt` | timestamptz | нет | конец сна |
| `StagesJson` | text | нет | JSON-массив сегментов фаз (см. ниже) |
| `Source` | varchar(128) | да | источник (обычно кольцо) |
| `ClientId` | varchar(128) | да | ключ идемпотентности |
| `CreatedAt` | timestamptz | нет | момент попадания на сервер |

**Индексы:** `UNIQUE (UserId, ClientId) WHERE ClientId IS NOT NULL`; `(UserId, StartedAt)`.

**`StagesJson`:**
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

## TagEvents — журнал тегов

Отметки образа жизни для будущих корреляций («после алкоголя HRV ниже»).

| Колонка | Тип | Null | Описание |
|---|---|---|---|
| `Id` | uuid | нет | PK |
| `UserId` | uuid | нет | FK → Users, каскад |
| `Tag` | varchar(64) | нет | `coffee`, `alcohol`, `late_meal`, `sick`, `stress`… |
| `At` | timestamptz | нет | момент события |
| `CreatedAt` | timestamptz | нет | момент записи |

**Индекс:** `(UserId, At)`.

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

## Служебные таблицы (не данные трекеров)

### GoogleHealthConnections — подключение к Google Health API
`Id`, `UserId` (FK, `UNIQUE`), `RefreshToken` (text), `Scopes` (varchar 1024), `ConnectedAt`, `LastSyncAt`, `LastError`.

### RefreshTokens — токены авторизации приложения
`Id`, `UserId` (FK), `TokenHash` (varchar 64, `UNIQUE`), `ExpiresAt`, `RevokedAt`, `CreatedAt`.

---

## Связи

```
Users 1──∞ Samples
Users 1──∞ Workouts
Users 1──∞ SleepSessions
Users 1──∞ TagEvents
Users 1──∞ RefreshTokens
Users 1──1 GoogleHealthConnections
```

Полный runnable-DDL — в [`schema.sql`](schema.sql). Точный скрипт с историей миграций: `dotnet ef migrations script`.
