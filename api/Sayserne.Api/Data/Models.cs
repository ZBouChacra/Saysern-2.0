using System.Data.Common;

namespace Sayserne.Api.Data;

/// <summary>Row as returned by the user procedures. Contains the password hash: never serialise this type.</summary>
public sealed record UserRecord(
    long Id, string Email, bool EmailVerified, string? PasswordHash, string FirstName, string LastName,
    string Role, string ProfileType, bool IsLocked, int FailedLoginAttempts, DateTime? LockedUntilUtc, DateTime CreatedAtUtc,
    bool IsPremium = false, bool IsStarred = false, bool IsDisabled = false, string? TimeZone = null, string? PreferredLanguage = null)
{
    public UserDto ToDto() => new(Id, Email, EmailVerified, FirstName, LastName, Role, ProfileType, IsLocked, CreatedAtUtc, IsPremium, IsStarred, IsDisabled, TimeZone, PreferredLanguage);
    public bool IsTemporarilyLocked(DateTime nowUtc) => LockedUntilUtc is { } until && until > nowUtc;
}

/// <summary>What the API returns about a user.</summary>
public sealed record UserDto(
    long Id, string Email, bool EmailVerified, string FirstName, string LastName,
    string Role, string ProfileType, bool IsLocked, DateTime CreatedAtUtc, bool IsPremium = false, bool IsStarred = false, bool IsDisabled = false, string? TimeZone = null, string? PreferredLanguage = null);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, long Total, int Page, int PageSize);

internal static class Mappers
{
    private static int O(DbDataReader r, string name) => r.GetOrdinal(name);
    public static long Long(this DbDataReader r, string name) => Convert.ToInt64(r.GetValue(O(r, name)));
    public static string Str(this DbDataReader r, string name) => r.GetString(O(r, name));
    public static string? StrOrNull(this DbDataReader r, string name) => r.IsDBNull(O(r, name)) ? null : r.GetString(O(r, name));
    public static bool Bool(this DbDataReader r, string name) => Convert.ToInt64(r.GetValue(O(r, name))) != 0;
    public static DateTime Utc(this DbDataReader r, string name) => DateTime.SpecifyKind(r.GetDateTime(O(r, name)), DateTimeKind.Utc);
    public static DateTime? UtcOrNull(this DbDataReader r, string name) => r.IsDBNull(O(r, name)) ? null : r.Utc(name);
    public static int Int(this DbDataReader r, string name) => Convert.ToInt32(r.GetValue(O(r, name)));
    public static int? IntOrNull(this DbDataReader r, string name) => r.IsDBNull(O(r, name)) ? null : r.Int(name);
    public static long? LongOrNull(this DbDataReader r, string name) => r.IsDBNull(O(r, name)) ? null : r.Long(name);
    public static decimal Dec(this DbDataReader r, string name) => Convert.ToDecimal(r.GetValue(O(r, name)));
    public static decimal? DecOrNull(this DbDataReader r, string name) => r.IsDBNull(O(r, name)) ? null : r.Dec(name);
    public static DateOnly? DateOrNull(this DbDataReader r, string name) => r.IsDBNull(O(r, name)) ? null : DateOnly.FromDateTime(r.GetDateTime(O(r, name)));

    public static UserRecord User(DbDataReader r) => new(
        r.Long("id"), r.Str("email"), r.Bool("email_verified"), r.StrOrNull("password_hash"),
        r.Str("first_name"), r.Str("last_name"), r.Str("role"), r.Str("profile_type"),
        r.Bool("is_locked"), (int)r.Long("failed_login_attempts"), r.UtcOrNull("locked_until"), r.Utc("created_at"),
        r.Bool("is_premium"), r.Bool("is_starred"),
        Has(r, "is_disabled") && r.Bool("is_disabled"), Has(r, "time_zone") ? r.StrOrNull("time_zone") : null, Has(r, "preferred_language") ? r.StrOrNull("preferred_language") : null);

    /// <summary>Not every procedure that returns a user row returns every column: the newer ones are optional.</summary>
    private static bool Has(DbDataReader r, string column)
    {
        for (var i = 0; i < r.FieldCount; i++) if (string.Equals(r.GetName(i), column, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
