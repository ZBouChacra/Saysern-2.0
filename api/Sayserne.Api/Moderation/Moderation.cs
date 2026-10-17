using System.Data.Common;
using Sayserne.Api.Data;

namespace Sayserne.Api.Moderation;

public sealed record AdminReview(long Id, int Rating, string? Comment, DateTime CreatedAt, string? ReplyText, DateTime? RepliedAt, bool IsDeleted, DateTime? DeletedAt, string? DeleteReason,
    long ReviewerId, string ReviewerName, long ProfessionalId, string ProfessionalName, string? DeletedByName);

public sealed record AdminChatRoom(long Id, long UserAId, string UserAName, string UserAEmail, long UserBId, string UserBName, string UserBEmail, DateTime CreatedAt, DateTime LastMessageAt,
    int MessageCount, int DeletedMessages, int StaffMessages, DateTime? DeletedByAAt, DateTime? DeletedByBAt);
public sealed record AdminChatHeader(long Id, long UserAId, string UserAName, long UserBId, string UserBName);
public sealed record AdminChatMessage(long Id, long RoomId, long SenderId, string SenderName, string MessageType, string? Content, decimal? Latitude, decimal? Longitude, string? FileId, bool IsRead,
    bool IsStaff, DateTime CreatedAt, DateTime? DeletedAt, long? DeletedBy, string? DeletedByName);
public sealed record AdminChatThread(AdminChatHeader Room, IReadOnlyList<AdminChatMessage> Messages);

public interface IModerationRepository
{
    Task<PagedResult<AdminReview>> ReviewsAsync(string? status, string? search, int page, int pageSize, CancellationToken ct = default);
    Task<AdminReview> SetReviewDeletedAsync(long actorId, long reviewId, bool deleted, string? reason, CancellationToken ct = default);
    Task<PagedResult<AdminChatRoom>> ChatsAsync(string? filter, string? search, int page, int pageSize, CancellationToken ct = default);
    Task<AdminChatThread> ReadChatAsync(long actorId, long roomId, int limit, int offset, CancellationToken ct = default);
    Task<AdminChatMessage> PostChatAsync(long actorId, long roomId, string content, CancellationToken ct = default);
    Task<bool> ChatFileBelongsAsync(long roomId, string fileId, CancellationToken ct = default);
}

public sealed class ModerationRepository(IStoredProcedureExecutor db) : IModerationRepository
{
    private static AdminReview Review(DbDataReader r) => new(r.Long("id"), r.Int("rating"), r.StrOrNull("comment"), r.Utc("created_at"), r.StrOrNull("reply_text"), r.UtcOrNull("replied_at"), r.Bool("is_deleted"),
        r.UtcOrNull("deleted_at"), r.StrOrNull("delete_reason"), r.Long("reviewer_id"), r.Str("reviewer_name"), r.Long("professional_id"), r.Str("professional_name"), r.StrOrNull("deleted_by_name"));
    private static AdminChatRoom Room(DbDataReader r) => new(r.Long("id"), r.Long("user_a_id"), r.Str("user_a_name"), r.Str("user_a_email"), r.Long("user_b_id"), r.Str("user_b_name"), r.Str("user_b_email"),
        r.Utc("created_at"), r.Utc("last_message_at"), r.Int("message_count"), r.Int("deleted_messages"), r.Int("staff_messages"), r.UtcOrNull("deleted_by_a_at"), r.UtcOrNull("deleted_by_b_at"));
    private static AdminChatMessage Message(DbDataReader r) => new(r.Long("id"), r.Long("room_id"), r.Long("sender_id"), r.Str("sender_name"), r.Str("message_type"), r.StrOrNull("content"), r.DecOrNull("latitude"), r.DecOrNull("longitude"),
        r.StrOrNull("file_id"), r.Bool("is_read"), r.Bool("is_staff"), r.Utc("created_at"), r.UtcOrNull("deleted_at"), r.LongOrNull("deleted_by"), r.StrOrNull("deleted_by_name"));

    public async Task<PagedResult<AdminReview>> ReviewsAsync(string? status, string? search, int page, int pageSize, CancellationToken ct = default)
    {
        var (rows, totals) = await db.QueryTwoAsync("sp_admin_reviews_list", new ProcParams().Add("p_status", status).Add("p_search", search).Add("p_page", page).Add("p_page_size", pageSize), Review, r => r.Long("total"), ct);
        return new PagedResult<AdminReview>(rows, totals.Count > 0 ? totals[0] : 0, Math.Max(page, 1), Math.Clamp(pageSize, 1, 200));
    }

    public async Task<AdminReview> SetReviewDeletedAsync(long actorId, long reviewId, bool deleted, string? reason, CancellationToken ct = default) =>
        (await db.QuerySingleOrDefaultAsync("sp_admin_reviews_set_deleted", new ProcParams().Add("p_actor_id", actorId).Add("p_review_id", reviewId).Add("p_deleted", deleted).Add("p_reason", reason), Review, ct))!;

    public async Task<PagedResult<AdminChatRoom>> ChatsAsync(string? filter, string? search, int page, int pageSize, CancellationToken ct = default)
    {
        var (rows, totals) = await db.QueryTwoAsync("sp_admin_chats_list", new ProcParams().Add("p_filter", filter).Add("p_search", search).Add("p_page", page).Add("p_page_size", pageSize), Room, r => r.Long("total"), ct);
        return new PagedResult<AdminChatRoom>(rows, totals.Count > 0 ? totals[0] : 0, Math.Max(page, 1), Math.Clamp(pageSize, 1, 200));
    }

    public async Task<AdminChatThread> ReadChatAsync(long actorId, long roomId, int limit, int offset, CancellationToken ct = default)
    {
        var (header, messages) = await db.QueryTwoAsync("sp_admin_chat_messages", new ProcParams().Add("p_actor_id", actorId).Add("p_room_id", roomId).Add("p_limit", limit).Add("p_offset", offset),
            r => new AdminChatHeader(r.Long("id"), r.Long("user_a_id"), r.Str("user_a_name"), r.Long("user_b_id"), r.Str("user_b_name")), Message, ct);
        return new AdminChatThread(header[0], messages);
    }

    public async Task<AdminChatMessage> PostChatAsync(long actorId, long roomId, string content, CancellationToken ct = default) =>
        (await db.QuerySingleOrDefaultAsync("sp_admin_chat_post", new ProcParams().Add("p_actor_id", actorId).Add("p_room_id", roomId).Add("p_content", content), Message, ct))!;

    public async Task<bool> ChatFileBelongsAsync(long roomId, string fileId, CancellationToken ct = default) =>
        (await db.QueryAsync("sp_admin_chat_file", new ProcParams().Add("p_room_id", roomId).Add("p_file_id", fileId), r => r.Str("id"), ct)).Count > 0;
}
