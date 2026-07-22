using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using MyHealth.Api.Common;
using MyHealth.Api.Data;
using MyHealth.Api.Domain;

namespace MyHealth.Api.Features.Integrations;

public record GoogleHealthConnectRequest(string RefreshToken, string? Scopes);
public record GoogleHealthStatusDto(
    bool Connected, DateTimeOffset? LastSyncAt, string? LastError);
public record GoogleHealthSyncResult(int Inserted);

public static class GoogleHealthEndpoints
{
    public static IEndpointRouteBuilder MapGoogleHealthEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/integrations/google-health")
            .WithTags("Integrations")
            .RequireAuthorization();

        // Подключение: клиент прошёл OAuth и прислал refresh-токен.
        group.MapPost("/connect", async (
            GoogleHealthConnectRequest req, ClaimsPrincipal principal,
            AppDbContext db, GoogleHealthService svc) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(req.RefreshToken))
                return Results.BadRequest(new { error = "Пустой токен." });

            var conn = await db.GoogleHealthConnections
                .FirstOrDefaultAsync(c => c.UserId == userId);
            if (conn is null)
            {
                conn = new GoogleHealthConnection
                {
                    UserId = userId.Value,
                    RefreshToken = req.RefreshToken,
                    Scopes = req.Scopes,
                };
                db.GoogleHealthConnections.Add(conn);
            }
            else
            {
                conn.RefreshToken = req.RefreshToken;
                conn.Scopes = req.Scopes;
                conn.ConnectedAt = DateTimeOffset.UtcNow;
                conn.LastError = null;
            }
            await db.SaveChangesAsync();

            // Сразу подтягиваем данные (30 дней).
            var inserted = await svc.SyncAsync(db, conn, 30, default);
            return Results.Ok(new GoogleHealthSyncResult(inserted));
        });

        group.MapGet("/", async (ClaimsPrincipal principal, AppDbContext db) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();
            var conn = await db.GoogleHealthConnections.AsNoTracking()
                .FirstOrDefaultAsync(c => c.UserId == userId);
            return Results.Ok(new GoogleHealthStatusDto(
                conn is not null, conn?.LastSyncAt, conn?.LastError));
        });

        // Ручной/периодический опрос (клиент дёргает при синхронизации).
        group.MapPost("/sync", async (
            ClaimsPrincipal principal, AppDbContext db, GoogleHealthService svc) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();
            var conn = await db.GoogleHealthConnections
                .FirstOrDefaultAsync(c => c.UserId == userId);
            if (conn is null) return Results.Ok(new GoogleHealthSyncResult(0));
            var inserted = await svc.SyncAsync(db, conn, 7, default);
            return Results.Ok(new GoogleHealthSyncResult(inserted));
        });

        group.MapDelete("/", async (ClaimsPrincipal principal, AppDbContext db) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();
            var conn = await db.GoogleHealthConnections
                .FirstOrDefaultAsync(c => c.UserId == userId);
            if (conn is not null)
            {
                db.GoogleHealthConnections.Remove(conn);
                await db.SaveChangesAsync();
            }
            return Results.Ok();
        });

        return app;
    }
}
