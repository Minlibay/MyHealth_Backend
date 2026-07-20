using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using MyHealth.Api.Common;
using MyHealth.Api.Data;
using MyHealth.Api.Domain;

namespace MyHealth.Api.Features.Tags;

public record TagUploadDto(string Tag, DateTimeOffset? At);
public record TagDto(Guid Id, string Tag, DateTimeOffset At);

/// <summary>Журнал тегов: быстрые отметки образа жизни.</summary>
public static class TagsEndpoints
{
    public static IEndpointRouteBuilder MapTagEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tags")
            .WithTags("Tags")
            .RequireAuthorization();

        group.MapPost("/", async (
            TagUploadDto dto, ClaimsPrincipal principal, AppDbContext db) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();
            var tag = dto.Tag.Trim().ToLowerInvariant();
            if (tag.Length is 0 or > 64)
                return Results.BadRequest(new { error = "Некорректный тег." });

            var entity = new TagEvent
            {
                UserId = userId.Value,
                Tag = tag,
                At = dto.At ?? DateTimeOffset.UtcNow,
            };
            db.TagEvents.Add(entity);
            await db.SaveChangesAsync();
            return Results.Ok(new TagDto(entity.Id, entity.Tag, entity.At));
        });

        group.MapGet("/", async (
            ClaimsPrincipal principal, AppDbContext db,
            DateTimeOffset? from, int limit = 100) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var q = db.TagEvents.AsNoTracking().Where(t => t.UserId == userId);
            if (from is not null) q = q.Where(t => t.At >= from);
            var data = await q
                .OrderByDescending(t => t.At)
                .Take(Math.Clamp(limit, 1, 500))
                .Select(t => new TagDto(t.Id, t.Tag, t.At))
                .ToListAsync();
            return Results.Ok(data);
        });

        group.MapDelete("/{id:guid}", async (
            Guid id, ClaimsPrincipal principal, AppDbContext db) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();
            var entity = await db.TagEvents
                .FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId);
            if (entity is null) return Results.NotFound();
            db.TagEvents.Remove(entity);
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        return app;
    }
}
