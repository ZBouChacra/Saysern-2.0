namespace Sayserne.Api.Data;

// Repositories translate method calls into stored-procedure calls. No SQL text lives here.

public interface IUserRepository
{
    Task<UserRecord> CreateLocalAsync(string email, string passwordHash, string firstName, string lastName, CancellationToken ct = default);
    Task<UserRecord?> GetByEmailAsync(string email, CancellationToken ct = default);
    Task<UserRecord?> GetByIdAsync(long id, CancellationToken ct = default);
    Task RecordLoginFailureAsync(long id, int maxAttempts, int lockMinutes, CancellationToken ct = default);
    Task RecordLoginSuccessAsync(long id, CancellationToken ct = default);
    Task PingAsync(CancellationToken ct = default);
}

public interface IIdentityRepository
{
    Task<UserRecord?> FindUserAsync(string provider, string subject, CancellationToken ct = default);
    Task<UserRecord> CreateUserAsync(string provider, string subject, string email, bool emailVerified, string firstName, string lastName, CancellationToken ct = default);
    Task<UserRecord> LinkAsync(long userId, string provider, string subject, string email, bool emailVerified, CancellationToken ct = default);
}

public interface IAdminUserRepository
{
    Task<PagedResult<UserRecord>> ListAsync(string? search, int page, int pageSize, CancellationToken ct = default);
    Task<UserRecord> SetLockedAsync(long userId, bool isLocked, CancellationToken ct = default);
    Task<UserRecord> SetRoleAsync(long userId, string role, CancellationToken ct = default);
    Task<UserRecord> SetFlagsAsync(long userId, bool isPremium, bool isStarred, CancellationToken ct = default);
}

public sealed class UserRepository(IStoredProcedureExecutor db) : IUserRepository
{
    public async Task<UserRecord> CreateLocalAsync(string email, string passwordHash, string firstName, string lastName, CancellationToken ct = default) =>
        (await db.QuerySingleOrDefaultAsync("sp_users_create_local",
            new ProcParams().Add("p_email", email).Add("p_password_hash", passwordHash).Add("p_first_name", firstName).Add("p_last_name", lastName),
            Mappers.User, ct))!;

    public Task<UserRecord?> GetByEmailAsync(string email, CancellationToken ct = default) =>
        db.QuerySingleOrDefaultAsync("sp_users_get_by_email", new ProcParams().Add("p_email", email), Mappers.User, ct);

    public Task<UserRecord?> GetByIdAsync(long id, CancellationToken ct = default) =>
        db.QuerySingleOrDefaultAsync("sp_users_get_by_id", new ProcParams().Add("p_id", id), Mappers.User, ct);

    public Task RecordLoginFailureAsync(long id, int maxAttempts, int lockMinutes, CancellationToken ct = default) =>
        db.ExecuteAsync("sp_users_record_login_failure",
            new ProcParams().Add("p_id", id).Add("p_max_attempts", maxAttempts).Add("p_lock_minutes", lockMinutes), ct);

    public Task RecordLoginSuccessAsync(long id, CancellationToken ct = default) =>
        db.ExecuteAsync("sp_users_record_login_success", new ProcParams().Add("p_id", id), ct);

    public Task PingAsync(CancellationToken ct = default) => db.ExecuteAsync("sp_health_ping", null, ct);
}

public sealed class IdentityRepository(IStoredProcedureExecutor db) : IIdentityRepository
{
    public Task<UserRecord?> FindUserAsync(string provider, string subject, CancellationToken ct = default) =>
        db.QuerySingleOrDefaultAsync("sp_identities_find_user",
            new ProcParams().Add("p_provider", provider).Add("p_subject", subject), Mappers.User, ct);

    public async Task<UserRecord> CreateUserAsync(string provider, string subject, string email, bool emailVerified, string firstName, string lastName, CancellationToken ct = default) =>
        (await db.QuerySingleOrDefaultAsync("sp_identities_create_user",
            new ProcParams().Add("p_provider", provider).Add("p_subject", subject).Add("p_email", email)
                .Add("p_email_verified", emailVerified).Add("p_first_name", firstName).Add("p_last_name", lastName),
            Mappers.User, ct))!;

    public async Task<UserRecord> LinkAsync(long userId, string provider, string subject, string email, bool emailVerified, CancellationToken ct = default) =>
        (await db.QuerySingleOrDefaultAsync("sp_identities_link",
            new ProcParams().Add("p_user_id", userId).Add("p_provider", provider).Add("p_subject", subject)
                .Add("p_email", email).Add("p_email_verified", emailVerified),
            Mappers.User, ct))!;
}

public sealed class AdminUserRepository(IStoredProcedureExecutor db) : IAdminUserRepository
{
    public async Task<PagedResult<UserRecord>> ListAsync(string? search, int page, int pageSize, CancellationToken ct = default)
    {
        var (rows, totals) = await db.QueryTwoAsync("sp_admin_users_list",
            new ProcParams().Add("p_search", search).Add("p_page", page).Add("p_page_size", pageSize),
            Mappers.User, r => r.Long("total"), ct);
        return new PagedResult<UserRecord>(rows, totals.Count > 0 ? totals[0] : 0, Math.Max(page, 1), Math.Clamp(pageSize, 1, 100));
    }

    public async Task<UserRecord> SetLockedAsync(long userId, bool isLocked, CancellationToken ct = default) =>
        (await db.QuerySingleOrDefaultAsync("sp_admin_users_set_locked",
            new ProcParams().Add("p_user_id", userId).Add("p_is_locked", isLocked), Mappers.User, ct))!;

    public async Task<UserRecord> SetRoleAsync(long userId, string role, CancellationToken ct = default) =>
        (await db.QuerySingleOrDefaultAsync("sp_admin_users_set_role",
            new ProcParams().Add("p_user_id", userId).Add("p_role", role), Mappers.User, ct))!;

    public async Task<UserRecord> SetFlagsAsync(long userId, bool isPremium, bool isStarred, CancellationToken ct = default) =>
        (await db.QuerySingleOrDefaultAsync("sp_admin_users_set_flags",
            new ProcParams().Add("p_user_id", userId).Add("p_is_premium", isPremium).Add("p_is_starred", isStarred), Mappers.User, ct))!;
}
