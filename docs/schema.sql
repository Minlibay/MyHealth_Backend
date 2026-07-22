-- MyHealth — схема БД (PostgreSQL 17), консолидированная из EF Core миграций.
-- Данные с трекеров: Samples (точечные показатели), Workouts (тренировки),
-- SleepSessions (сон с фазами). Users — владелец данных, остальные — служебные.
-- Полный runnable-скрипт с историей миграций: dotnet ef migrations script.

-- ============================================================
-- Пользователь (владелец данных, профиль и цели)
-- ============================================================
CREATE TABLE "Users" (
    "Id"              uuid PRIMARY KEY,
    "Email"           varchar(256) NOT NULL,
    "PasswordHash"    text NOT NULL,
    "DisplayName"     varchar(128),
    -- Профиль (для зон пульса, калорий кольца)
    "Gender"          varchar(16),          -- male | female
    "Age"             integer,
    "HeightCm"        double precision,
    "WeightKg"        double precision,
    -- Персональные цели (для оценок/скоров)
    "StepsGoal"       integer,
    "WaterGoalLiters" double precision,
    "SleepGoalHours"  double precision,
    "KcalGoal"        integer,
    "CreatedAt"       timestamptz NOT NULL
);
CREATE UNIQUE INDEX "IX_Users_Email" ON "Users" ("Email");

-- ============================================================
-- Samples — все точечные показатели с любого трекера
-- (пульс, шаги, SpO2, вес, давление, глюкоза, температура и т.д.)
-- ============================================================
CREATE TABLE "Samples" (
    "Id"         uuid PRIMARY KEY,
    "UserId"     uuid NOT NULL REFERENCES "Users" ("Id") ON DELETE CASCADE,
    "Metric"     varchar(32) NOT NULL,   -- enum строкой, см. список ниже
    "Value"      double precision NOT NULL,   -- для давления: систолическое
    "Secondary"  double precision,            -- для давления: диастолическое
    "Unit"       varchar(32),
    "RecordedAt" timestamptz NOT NULL,    -- время измерения на устройстве
    "Source"     varchar(128),           -- 'ключ:приложение', см. ниже
    "ClientId"   varchar(128),           -- ключ идемпотентности
    "CreatedAt"  timestamptz NOT NULL
);
-- Защита от дублей при повторной выгрузке
CREATE UNIQUE INDEX "IX_Samples_UserId_ClientId"
    ON "Samples" ("UserId", "ClientId") WHERE "ClientId" IS NOT NULL;
-- Быстрая выборка истории показателя
CREATE INDEX "IX_Samples_UserId_Metric_RecordedAt"
    ON "Samples" ("UserId", "Metric", "RecordedAt");

-- Metric (значения, строкой):
--   Steps, HeartRate, BloodPressure, Weight, Sleep, BloodGlucose,
--   BloodOxygen, ActiveEnergy, Distance, Water, BodyTemperature,
--   RespiratoryRate, RestingHeartRate, Hrv, BodyFat, Height, DietaryEnergy
--
-- Source (формат 'ключ:приложение'), примеры:
--   apple_health:Apple Watch | apple_health:JCVitalPro |
--   health_connect:Google Fit | google_health | ring:JCRing X3 | manual

-- ============================================================
-- Workouts — тренировки
-- (зоны пульса и TRIMP не хранятся — считаются на лету из Samples)
-- ============================================================
CREATE TABLE "Workouts" (
    "Id"             uuid PRIMARY KEY,
    "UserId"         uuid NOT NULL REFERENCES "Users" ("Id") ON DELETE CASCADE,
    "ActivityType"   varchar(64) NOT NULL,   -- RUNNING, YOGA, BIKING, ...
    "StartedAt"      timestamptz NOT NULL,
    "EndedAt"        timestamptz NOT NULL,
    "EnergyKcal"     double precision,
    "DistanceMeters" double precision,
    "Source"         varchar(128),
    "ClientId"       varchar(128),
    "CreatedAt"      timestamptz NOT NULL
);
CREATE UNIQUE INDEX "IX_Workouts_UserId_ClientId"
    ON "Workouts" ("UserId", "ClientId") WHERE "ClientId" IS NOT NULL;
CREATE INDEX "IX_Workouts_UserId_StartedAt"
    ON "Workouts" ("UserId", "StartedAt");

-- ============================================================
-- SleepSessions — сон с фазами
-- ============================================================
CREATE TABLE "SleepSessions" (
    "Id"         uuid PRIMARY KEY,
    "UserId"     uuid NOT NULL REFERENCES "Users" ("Id") ON DELETE CASCADE,
    "StartedAt"  timestamptz NOT NULL,
    "EndedAt"    timestamptz NOT NULL,
    -- JSON-массив сегментов фаз:
    -- [{"stage":"deep|light|rem|awake","start":"...","end":"..."}]
    "StagesJson" text NOT NULL,
    "Source"     varchar(128),
    "ClientId"   varchar(128),
    "CreatedAt"  timestamptz NOT NULL
);
CREATE UNIQUE INDEX "IX_SleepSessions_UserId_ClientId"
    ON "SleepSessions" ("UserId", "ClientId") WHERE "ClientId" IS NOT NULL;
CREATE INDEX "IX_SleepSessions_UserId_StartedAt"
    ON "SleepSessions" ("UserId", "StartedAt");

-- ============================================================
-- TagEvents — журнал тегов (кофе/алкоголь/болею и т.п.)
-- ============================================================
CREATE TABLE "TagEvents" (
    "Id"        uuid PRIMARY KEY,
    "UserId"    uuid NOT NULL REFERENCES "Users" ("Id") ON DELETE CASCADE,
    "Tag"       varchar(64) NOT NULL,   -- coffee, alcohol, sick, ...
    "At"        timestamptz NOT NULL,
    "CreatedAt" timestamptz NOT NULL
);
CREATE INDEX "IX_TagEvents_UserId_At" ON "TagEvents" ("UserId", "At");

-- ============================================================
-- Служебные таблицы (не данные трекеров)
-- ============================================================

-- Подключение к Google Health API (облачный импорт Fitbit/Google)
CREATE TABLE "GoogleHealthConnections" (
    "Id"           uuid PRIMARY KEY,
    "UserId"       uuid NOT NULL REFERENCES "Users" ("Id") ON DELETE CASCADE,
    "RefreshToken" text NOT NULL,
    "Scopes"       varchar(1024),
    "ConnectedAt"  timestamptz NOT NULL,
    "LastSyncAt"   timestamptz,
    "LastError"    text
);
CREATE UNIQUE INDEX "IX_GoogleHealthConnections_UserId"
    ON "GoogleHealthConnections" ("UserId");

-- Refresh-токены авторизации приложения
CREATE TABLE "RefreshTokens" (
    "Id"        uuid PRIMARY KEY,
    "UserId"    uuid NOT NULL REFERENCES "Users" ("Id") ON DELETE CASCADE,
    "TokenHash" varchar(64) NOT NULL,
    "ExpiresAt" timestamptz NOT NULL,
    "RevokedAt" timestamptz,
    "CreatedAt" timestamptz NOT NULL
);
CREATE UNIQUE INDEX "IX_RefreshTokens_TokenHash" ON "RefreshTokens" ("TokenHash");
CREATE INDEX "IX_RefreshTokens_UserId" ON "RefreshTokens" ("UserId");

-- Примечания:
-- * Все расчёты (скоры, тренды, зоны пульса, VO2max, ночные показатели)
--   считаются на лету из таблиц выше и в БД не хранятся.
-- * Удаление пользователя каскадно удаляет все его данные (GDPR).
