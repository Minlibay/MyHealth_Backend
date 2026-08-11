using Microsoft.EntityFrameworkCore;
using MyHealth.Api.Domain;
using MyHealth.Api.Domain.Tracking;

namespace MyHealth.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<TagEvent> TagEvents => Set<TagEvent>();
    public DbSet<GoogleHealthConnection> GoogleHealthConnections =>
        Set<GoogleHealthConnection>();

    // --- Прежняя схема (совместимость и миграция данных) ---
    public DbSet<HealthSample> Samples => Set<HealthSample>();
    public DbSet<Workout> Workouts => Set<Workout>();
    public DbSet<SleepSession> SleepSessions => Set<SleepSession>();

    // --- Новая схема трекинга ---
    public DbSet<MetricDefinition> MetricDefinitions => Set<MetricDefinition>();
    public DbSet<EventTypeDefinition> EventTypeDefinitions =>
        Set<EventTypeDefinition>();
    public DbSet<SourceEventTypeMap> SourceEventTypeMaps =>
        Set<SourceEventTypeMap>();
    public DbSet<VendorMetricDefinition> VendorMetricDefinitions =>
        Set<VendorMetricDefinition>();
    public DbSet<ValueDictionaryEntry> ValueDictionary =>
        Set<ValueDictionaryEntry>();

    public DbSet<DeviceInstance> DeviceInstances => Set<DeviceInstance>();
    public DbSet<Observation> Observations => Set<Observation>();
    public DbSet<TrackedEvent> Events => Set<TrackedEvent>();
    public DbSet<VendorMetric> VendorMetrics => Set<VendorMetric>();
    public DbSet<MeasurementEventLink> MeasurementEventLinks =>
        Set<MeasurementEventLink>();
    public DbSet<DerivedMetric> DerivedMetrics => Set<DerivedMetric>();
    public DbSet<ReferenceRange> ReferenceRanges => Set<ReferenceRange>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        ConfigureTracking(b);
        b.Entity<AppUser>(e =>
        {
            e.HasIndex(u => u.Email).IsUnique();
            e.Property(u => u.Email).HasMaxLength(256);
            e.Property(u => u.DisplayName).HasMaxLength(128);
            e.Property(u => u.Gender).HasMaxLength(16);
        });

        b.Entity<HealthSample>(e =>
        {
            // Храним тип показателя строкой — стабильно и читаемо в БД.
            e.Property(s => s.Metric).HasConversion<string>().HasMaxLength(32);
            e.Property(s => s.Unit).HasMaxLength(32);
            e.Property(s => s.Source).HasMaxLength(128);
            e.Property(s => s.ClientId).HasMaxLength(128);

            e.HasOne(s => s.User)
                .WithMany(u => u.Samples)
                .HasForeignKey(s => s.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // Быстрая выборка истории показателя пользователя.
            e.HasIndex(s => new { s.UserId, s.Metric, s.RecordedAt });

            // Идемпотентность загрузки: одна клиентская запись на пользователя.
            e.HasIndex(s => new { s.UserId, s.ClientId })
                .IsUnique()
                .HasFilter("\"ClientId\" IS NOT NULL");
        });

        b.Entity<RefreshToken>(e =>
        {
            e.Property(t => t.TokenHash).HasMaxLength(64);
            e.HasIndex(t => t.TokenHash).IsUnique();
            e.HasIndex(t => t.UserId);

            e.HasOne(t => t.User)
                .WithMany()
                .HasForeignKey(t => t.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<GoogleHealthConnection>(e =>
        {
            // Одно подключение на пользователя.
            e.HasIndex(c => c.UserId).IsUnique();
            e.Property(c => c.Scopes).HasMaxLength(1024);
            e.HasOne(c => c.User)
                .WithMany()
                .HasForeignKey(c => c.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<TagEvent>(e =>
        {
            e.Property(t => t.Tag).HasMaxLength(64);
            e.HasOne(t => t.User)
                .WithMany()
                .HasForeignKey(t => t.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(t => new { t.UserId, t.At });
        });

        b.Entity<SleepSession>(e =>
        {
            e.Property(s => s.Source).HasMaxLength(128);
            e.Property(s => s.ClientId).HasMaxLength(128);

            e.HasOne(s => s.User)
                .WithMany()
                .HasForeignKey(s => s.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(s => new { s.UserId, s.StartedAt });

            e.HasIndex(s => new { s.UserId, s.ClientId })
                .IsUnique()
                .HasFilter("\"ClientId\" IS NOT NULL");
        });

        b.Entity<Workout>(e =>
        {
            e.Property(w => w.ActivityType).HasMaxLength(64);
            e.Property(w => w.Source).HasMaxLength(128);
            e.Property(w => w.ClientId).HasMaxLength(128);

            e.HasOne(w => w.User)
                .WithMany()
                .HasForeignKey(w => w.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // Быстрая выборка тренировок пользователя за период.
            e.HasIndex(w => new { w.UserId, w.StartedAt });

            // Идемпотентность загрузки: одна клиентская запись на пользователя.
            e.HasIndex(w => new { w.UserId, w.ClientId })
                .IsUnique()
                .HasFilter("\"ClientId\" IS NOT NULL");
        });
    }

    /// <summary>
    /// Схема трекинга: реестры (показатели, типы событий, вендорские
    /// показатели) и данные (observations, events, vendor metrics, связи).
    /// </summary>
    private static void ConfigureTracking(ModelBuilder b)
    {
        // --- Реестры ---
        b.Entity<MetricDefinition>(e =>
        {
            e.HasKey(m => m.MetricCode);
            e.Property(m => m.MetricCode).HasMaxLength(96);
            e.Property(m => m.Name).HasMaxLength(256);
            e.Property(m => m.Domain).HasMaxLength(96);
            e.Property(m => m.Grain).HasMaxLength(16);
            e.Property(m => m.Trigger).HasMaxLength(32);
            e.Property(m => m.Derivation).HasMaxLength(32);
            e.Property(m => m.ValueType).HasMaxLength(16);
            e.Property(m => m.Unit).HasMaxLength(48);
        });

        b.Entity<EventTypeDefinition>(e =>
        {
            e.HasKey(t => t.EventTypeCode);
            e.Property(t => t.EventTypeCode).HasMaxLength(96);
            e.Property(t => t.Name).HasMaxLength(256);
            e.Property(t => t.Group).HasMaxLength(96);
            e.HasIndex(t => t.Mvp);
        });

        b.Entity<SourceEventTypeMap>(e =>
        {
            e.Property(m => m.Source).HasMaxLength(64);
            e.Property(m => m.SourceEventType).HasMaxLength(160);
            e.Property(m => m.EventTypeCode).HasMaxLength(96);
            e.Property(m => m.Availability).HasMaxLength(64);
            // Один тип источника → один наш код.
            e.HasIndex(m => new { m.Source, m.SourceEventType }).IsUnique();
        });

        b.Entity<VendorMetricDefinition>(e =>
        {
            e.HasKey(v => v.VendorMetricCode);
            e.Property(v => v.VendorMetricCode).HasMaxLength(96);
            e.Property(v => v.Name).HasMaxLength(256);
            e.Property(v => v.Vendor).HasMaxLength(48);
            e.Property(v => v.UsePolicy).HasMaxLength(32);
            e.Property(v => v.VendorMetricType).HasMaxLength(32);
        });

        b.Entity<ValueDictionaryEntry>(e =>
        {
            e.Property(v => v.Column).HasMaxLength(48);
            e.Property(v => v.Value).HasMaxLength(64);
            e.HasIndex(v => new { v.Column, v.Value }).IsUnique();
        });

        // --- Источники ---
        b.Entity<DeviceInstance>(e =>
        {
            e.Property(d => d.IntegrationPlatform).HasMaxLength(32);
            e.Property(d => d.SourceDeviceId).HasMaxLength(160);
            e.Property(d => d.DeviceType).HasMaxLength(24);
            e.Property(d => d.DeviceName).HasMaxLength(128);
            e.Property(d => d.Manufacturer).HasMaxLength(96);
            e.Property(d => d.Model).HasMaxLength(96);
            e.Property(d => d.DataOriginAppId).HasMaxLength(160);
            e.HasOne(d => d.User).WithMany().HasForeignKey(d => d.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            // Устройство пользователя определяется платформой + приложением
            // + идентификатором источника (если он есть).
            e.HasIndex(d => new
            {
                d.UserId, d.IntegrationPlatform, d.DataOriginAppId, d.SourceDeviceId
            });
        });

        // --- Значения показателей ---
        b.Entity<Observation>(e =>
        {
            e.Property(o => o.MetricCode).HasMaxLength(96);
            e.Property(o => o.Unit).HasMaxLength(48);
            e.Property(o => o.SourceRecordId).HasMaxLength(160);
            e.Property(o => o.ClientId).HasMaxLength(160);
            e.Property(o => o.ValueJson).HasColumnType("jsonb");

            e.HasOne(o => o.User).WithMany().HasForeignKey(o => o.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(o => o.Metric).WithMany().HasForeignKey(o => o.MetricCode)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(o => o.DeviceInstance).WithMany()
                .HasForeignKey(o => o.DeviceInstanceId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasIndex(o => new { o.UserId, o.MetricCode, o.StartAt });
            e.HasIndex(o => new { o.UserId, o.ClientId })
                .IsUnique()
                .HasFilter("\"ClientId\" IS NOT NULL");
        });

        // --- События ---
        b.Entity<TrackedEvent>(e =>
        {
            e.Property(t => t.EventTypeCode).HasMaxLength(96);
            e.Property(t => t.EventName).HasMaxLength(256);
            e.Property(t => t.SourceRecordId).HasMaxLength(160);
            e.Property(t => t.SourceEventType).HasMaxLength(160);
            e.Property(t => t.SourceParentRecordId).HasMaxLength(160);
            e.Property(t => t.ClientId).HasMaxLength(160);

            e.HasOne(t => t.User).WithMany().HasForeignKey(t => t.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(t => t.EventType).WithMany()
                .HasForeignKey(t => t.EventTypeCode)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(t => t.DeviceInstance).WithMany()
                .HasForeignKey(t => t.DeviceInstanceId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasIndex(t => new { t.UserId, t.StartAt });
            e.HasIndex(t => new { t.UserId, t.EventTypeCode, t.StartAt });
            e.HasIndex(t => new { t.UserId, t.ClientId })
                .IsUnique()
                .HasFilter("\"ClientId\" IS NOT NULL");
        });

        // --- Вендорские показатели ---
        b.Entity<VendorMetric>(e =>
        {
            e.Property(v => v.VendorMetricCode).HasMaxLength(96);
            e.Property(v => v.ValueText).HasMaxLength(256);
            e.Property(v => v.Unit).HasMaxLength(48);
            e.Property(v => v.SourceRecordId).HasMaxLength(160);
            e.Property(v => v.SourceState).HasMaxLength(32);
            e.Property(v => v.SourceDetailsSchemaVersion).HasMaxLength(64);
            e.Property(v => v.ClientId).HasMaxLength(160);
            e.Property(v => v.SourceDetails).HasColumnType("jsonb");

            e.HasOne(v => v.User).WithMany().HasForeignKey(v => v.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(v => v.Definition).WithMany()
                .HasForeignKey(v => v.VendorMetricCode)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(v => v.DeviceInstance).WithMany()
                .HasForeignKey(v => v.DeviceInstanceId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasIndex(v => new { v.UserId, v.VendorMetricCode, v.EffectiveAt });
            e.HasIndex(v => new { v.UserId, v.ClientId })
                .IsUnique()
                .HasFilter("\"ClientId\" IS NOT NULL");
        });

        // --- Связи значений с событиями ---
        b.Entity<MeasurementEventLink>(e =>
        {
            e.Property(l => l.MeasurementType).HasMaxLength(24);
            e.Property(l => l.LinkMethod).HasMaxLength(24);
            e.HasOne(l => l.Event).WithMany().HasForeignKey(l => l.EventId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(l => new { l.MeasurementId, l.MeasurementType });
            e.HasIndex(l => new
            {
                l.EventId, l.MeasurementId, l.MeasurementType
            }).IsUnique();
        });

        // --- Наши расчёты ---
        b.Entity<DerivedMetric>(e =>
        {
            e.Property(d => d.MetricCode).HasMaxLength(96);
            e.Property(d => d.Name).HasMaxLength(256);
            e.Property(d => d.Unit).HasMaxLength(48);
            e.Property(d => d.AlgorithmVersion).HasMaxLength(32);
            e.Property(d => d.ValueJson).HasColumnType("jsonb");
            e.Property(d => d.FactorsJson).HasColumnType("jsonb");
            e.HasOne(d => d.User).WithMany().HasForeignKey(d => d.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(d => new { d.UserId, d.MetricCode, d.EffectiveAt })
                .IsUnique();
        });

        // --- Нормативные диапазоны ---
        b.Entity<ReferenceRange>(e =>
        {
            e.Property(r => r.MetricCode).HasMaxLength(96);
            e.Property(r => r.Population).HasMaxLength(48);
            e.Property(r => r.Unit).HasMaxLength(48);
            e.HasIndex(r => new { r.MetricCode, r.Population }).IsUnique();
        });
    }
}
