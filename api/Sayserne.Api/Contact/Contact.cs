using System.Data.Common;
using Sayserne.Api.Data;

namespace Sayserne.Api.Contact;

public sealed record ContactRow(long Id, long? UserId, string Name, string Email, string Language, string? Topic, string Subject, string Preview, string Status, DateTime CreatedAt, DateTime? RepliedAt);
public sealed record ContactMessage(long Id, long? UserId, string Name, string Email, string Language, string? Topic, string? TopicName, string Subject, string Body, string Status, string? ReplyBody,
    DateTime? RepliedAt, string? RepliedByName, string? InternalNote, DateTime CreatedAt);

public interface IContactRepository
{
    Task<(long Id, string Status)> CreateAsync(long? userId, string name, string email, string language, string? topic, string subject, string body, bool spam, CancellationToken ct = default);
    Task<PagedResult<ContactRow>> ListAsync(string? status, string? search, int page, int pageSize, CancellationToken ct = default);
    Task<ContactMessage?> GetAsync(long id, CancellationToken ct = default);
    Task<ContactMessage> ReplyAsync(long actorId, long id, string reply, CancellationToken ct = default);
    Task<ContactMessage> SetStatusAsync(long actorId, long id, string status, string? note, CancellationToken ct = default);
}

public sealed class ContactRepository(IStoredProcedureExecutor db) : IContactRepository
{
    private static ContactMessage Full(DbDataReader r) => new(r.Long("id"), r.LongOrNull("user_id"), r.Str("name"), r.Str("email"), r.Str("language"), r.StrOrNull("topic"), r.StrOrNull("topic_name"), r.Str("subject"), r.Str("body"),
        r.Str("status"), r.StrOrNull("reply_body"), r.UtcOrNull("replied_at"), r.StrOrNull("replied_by_name"), r.StrOrNull("internal_note"), r.Utc("created_at"));

    public async Task<(long Id, string Status)> CreateAsync(long? userId, string name, string email, string language, string? topic, string subject, string body, bool spam, CancellationToken ct = default) =>
        (await db.QueryAsync("sp_contact_create", new ProcParams().Add("p_user_id", userId).Add("p_name", name).Add("p_email", email).Add("p_language", language).Add("p_topic", topic)
            .Add("p_subject", subject).Add("p_body", body).Add("p_spam", spam), r => (r.Long("id"), r.Str("status")), ct))[0];

    public async Task<PagedResult<ContactRow>> ListAsync(string? status, string? search, int page, int pageSize, CancellationToken ct = default)
    {
        var (rows, totals) = await db.QueryTwoAsync("sp_contact_list", new ProcParams().Add("p_status", status).Add("p_search", search).Add("p_page", page).Add("p_page_size", pageSize),
            r => new ContactRow(r.Long("id"), r.LongOrNull("user_id"), r.Str("name"), r.Str("email"), r.Str("language"), r.StrOrNull("topic"), r.Str("subject"), r.Str("preview"), r.Str("status"), r.Utc("created_at"), r.UtcOrNull("replied_at")),
            r => r.Long("total"), ct);
        return new PagedResult<ContactRow>(rows, totals.Count > 0 ? totals[0] : 0, Math.Max(page, 1), Math.Clamp(pageSize, 1, 200));
    }

    public Task<ContactMessage?> GetAsync(long id, CancellationToken ct = default) => db.QuerySingleOrDefaultAsync("sp_contact_get", new ProcParams().Add("p_id", id), Full, ct);

    public async Task<ContactMessage> ReplyAsync(long actorId, long id, string reply, CancellationToken ct = default) =>
        (await db.QuerySingleOrDefaultAsync("sp_contact_reply", new ProcParams().Add("p_actor_id", actorId).Add("p_id", id).Add("p_reply", reply), Full, ct))!;

    public async Task<ContactMessage> SetStatusAsync(long actorId, long id, string status, string? note, CancellationToken ct = default) =>
        (await db.QuerySingleOrDefaultAsync("sp_contact_set_status", new ProcParams().Add("p_actor_id", actorId).Add("p_id", id).Add("p_status", status).Add("p_note", note), Full, ct))!;
}
