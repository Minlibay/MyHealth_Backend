using Microsoft.EntityFrameworkCore;
using MyHealth.Api.Domain;

namespace MyHealth.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<HealthSample> Samples => Set<HealthSample>();
    public DbSet<Workout> Workouts => Set<Workout>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<SleepSession> SleepSessions => Set<SleepSession>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<AppUser>(e =>
        {
            e.HasIndex(u => u.Email).IsUnique();
            e.Property(u => u.Email).HasMaxLength(256);
            e.Property(u => u.DisplayName).HasMaxLength(128);
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
}
