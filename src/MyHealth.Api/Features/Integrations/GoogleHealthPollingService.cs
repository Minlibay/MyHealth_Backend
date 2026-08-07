using Microsoft.EntityFrameworkCore;
using MyHealth.Api.Data;

namespace MyHealth.Api.Features.Integrations;

/// <summary>
/// Периодически опрашивает Google Health за всех подключённых
/// пользователей — данные приходят на сервер даже когда приложение
/// закрыто и телефон не участвует.
/// </summary>
public class GoogleHealthPollingService(
    IServiceScopeFactory scopeFactory,
    ILogger<GoogleHealthPollingService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(3);

    /// <summary>Пользователя не опрашиваем чаще, чем раз в интервал.</summary>
    private static readonly TimeSpan MinAge = TimeSpan.FromHours(2);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Небольшая задержка на старте: даём приложению подняться.
        await Task.Delay(TimeSpan.FromMinutes(1), ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollAllAsync(ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Google Health polling cycle failed");
            }

            try
            {
                await Task.Delay(Interval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task PollAllAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<GoogleHealthService>();

        var cutoff = DateTimeOffset.UtcNow - MinAge;
        var connections = await db.GoogleHealthConnections
            .Where(c => c.LastSyncAt == null || c.LastSyncAt < cutoff)
            .ToListAsync(ct);
        if (connections.Count == 0) return;

        logger.LogInformation(
            "Google Health polling: {Count} connection(s)", connections.Count);
        foreach (var conn in connections)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                var inserted = await svc.SyncAsync(db, conn, 7, ct);
                logger.LogInformation(
                    "Google Health polling: user {User}, inserted {Inserted}",
                    conn.UserId, inserted);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e,
                    "Google Health polling failed for user {User}", conn.UserId);
            }
        }
    }
}
