using Sayserne.Api.Auth;
using Sayserne.Api.Data;
using Sayserne.Api.Email;
using Sayserne.Api.Media;
using Sayserne.Api.Moderation;

namespace Sayserne.Api.Endpoints;

/// <summary>Staff-only moderation: every review (hide / restore with a reason) and every conversation (read, audited; write as the team).</summary>
public static class ModerationEndpoints
{
    public sealed record HideInput(string? Reason);
    public sealed record StaffMessageInput(string? Content);

    private static AppException Invalid(string field, string message) => new("VALIDATION_FAILED", 400, fieldErrors: new Dictionary<string, string> { [field] = message });
    private static string? Clean(string? s, int max, string field)
    {
        var t = string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        if (t is { } v && v.Length > max) throw Invalid(field, $"At most {max} characters.");
        return t;
    }

    public static void MapModeration(this IEndpointRouteBuilder app)
    {
        var rv = app.MapGroup("/api/admin/reviews");
        rv.MapGet("", async (IModerationRepository mod, string? status, string? search, int? page, int? pageSize) =>
            Results.Ok(await mod.ReviewsAsync(Clean(status, 10, "status"), Clean(search, 100, "search"), page ?? 1, pageSize ?? 25))).RequirePermission("reviews.read");
        rv.MapPost("/{id:long}/hide", async (HttpContext ctx, long id, HideInput b, IModerationRepository mod) =>
            Results.Ok(await mod.SetReviewDeletedAsync(ctx.UserId(), id, true, Clean(b.Reason, 300, "reason")))).RequirePermission("reviews.manage");
        rv.MapPost("/{id:long}/restore", async (HttpContext ctx, long id, IModerationRepository mod) =>
            Results.Ok(await mod.SetReviewDeletedAsync(ctx.UserId(), id, false, null))).RequirePermission("reviews.manage");

        var ch = app.MapGroup("/api/admin/chats");
        ch.MapGet("", async (IModerationRepository mod, string? filter, string? search, int? page, int? pageSize) =>
            Results.Ok(await mod.ChatsAsync(Clean(filter, 10, "filter"), Clean(search, 100, "search"), page ?? 1, pageSize ?? 25))).RequirePermission("chats.read");
        // reading a conversation is itself recorded in the audit log (inside the database call)
        ch.MapGet("/{roomId:long}/messages", async (HttpContext ctx, long roomId, IModerationRepository mod, int? limit, int? offset) =>
            Results.Ok(await mod.ReadChatAsync(ctx.UserId(), roomId, limit ?? 100, offset ?? 0))).RequirePermission("chats.read");
        ch.MapPost("/{roomId:long}/messages", async (HttpContext ctx, long roomId, StaffMessageInput b, IModerationRepository mod, MarketplaceNotifier notify) =>
        {
            var sent = await mod.PostChatAsync(ctx.UserId(), roomId, b.Content ?? "");
            var thread = await mod.ReadChatAsync(ctx.UserId(), roomId, 1, 0);     // (to know who is in the conversation; this read is audited too, which is accurate)
            await notify.StaffMessage(thread.Room);
            return Results.Json(sent, statusCode: StatusCodes.Status201Created);
        }).RequirePermission("chats.post");
        ch.MapGet("/{roomId:long}/files/{fileId}", async (HttpContext ctx, long roomId, string fileId, IModerationRepository mod, IFileRepository files, IStorageRouter storage) =>
        {
            if (fileId.Length != 32 || !await mod.ChatFileBelongsAsync(roomId, fileId, ctx.RequestAborted)) throw new AppException("FILE_NOT_FOUND", 404);
            var file = await files.GetAsync(fileId, ctx.RequestAborted);
            return await FileEndpoints.ServeAsync(ctx, file, storage);
        }).RequirePermission("chats.read");
    }
}
