using System.Data.Common;
using System.Text.Json;
using Sayserne.Api.Data;

namespace Sayserne.Api.Platform;

/// <summary>A setting. For a secret the value is never exposed: Value is empty and IsSet says whether one is stored.</summary>
public sealed record SettingRow(string Key, string Group, string Type, string Value, string Default, decimal? Min, decimal? Max, string Description, bool IsPublic, DateTime UpdatedAt, long? UpdatedBy, bool IsSet = false);
public sealed record LookupType(string Code, string Name, string? Description, string? ParentType, bool IsSystem, int ValueCount);
public sealed record LookupValue(long Id, string Type, string Code, string? ParentCode, int SortOrder, bool IsActive, JsonElement? Metadata, IReadOnlyDictionary<string, string> Names);
public sealed record TranslationRow(string Language, string Key, string Value, string DefaultValue, DateTime UpdatedAt);
public sealed record AuditRow(long Id, DateTime OccurredAt, long? ActorId, string? ActorName, string Action, string EntityType, string? EntityId, string Summary, JsonElement? Details, string? Ip);
public sealed record AuditFilter(DateTime? From, DateTime? To, long? ActorId, string? Action, string? EntityType, string? Search, int Page, int PageSize);
public sealed record PermissionRow(string Code, string Group, string Description);
public sealed record RoleRow(long Id, string Name, string? Description, int UserCount, IReadOnlyList<string> Permissions);
public sealed record RoleRef(long Id, string Name);
public sealed record StaffInfo(long UserId, bool IsSuper, int RoleCount, IReadOnlyList<string> Permissions) { public bool IsStaff => IsSuper || RoleCount > 0; }
public sealed record TwoFactorState(string SecretProtected, bool IsConfirmed, long? LastUsedStep);

/// <summary>A user row for the back office: the account plus its roles and whether two-factor is set up.</summary>
public sealed record AdminUserRow(UserRecord User, string? RoleNames, bool HasTwoFactor);
public sealed record AdminUserDto(
    long Id, string Email, bool EmailVerified, string FirstName, string LastName, string Role, string ProfileType, bool IsLocked, DateTime CreatedAtUtc,
    bool IsPremium, bool IsStarred, string? RoleNames, bool HasTwoFactor, bool IsDisabled = false)
{
    public static AdminUserDto From(AdminUserRow r) => new(r.User.Id, r.User.Email, r.User.EmailVerified, r.User.FirstName, r.User.LastName, r.User.Role,
        r.User.ProfileType, r.User.IsLocked, r.User.CreatedAtUtc, r.User.IsPremium, r.User.IsStarred, r.RoleNames, r.HasTwoFactor, r.User.IsDisabled);
}

internal static class PlatformMappers
{
    public static JsonElement? Json(DbDataReader r, string name)
    {
        var s = r.StrOrNull(name);
        return s is null ? null : JsonDocument.Parse(s).RootElement.Clone();
    }

    public static SettingRow Setting(DbDataReader r, bool mask = true)
    {
        var secret = r.Str("value_type") == "secret";
        return new SettingRow(r.Str("setting_key"), r.Str("group_name"), r.Str("value_type"), secret && mask ? "" : r.Str("value_text"), secret ? "" : r.Str("default_text"),
            r.DecOrNull("min_value"), r.DecOrNull("max_value"), r.Str("description"), r.Bool("is_public"), r.Utc("updated_at"), r.LongOrNull("updated_by"), secret && r.Str("value_text").Length > 0);
    }
    public static LookupType LookupType(DbDataReader r) => new(r.Str("code"), r.Str("name"), r.StrOrNull("description"), r.StrOrNull("parent_type"), r.Bool("is_system"), r.Int("value_count"));
    public static TranslationRow Translation(DbDataReader r) => new(r.Str("language"), r.Str("translation_key"), r.Str("value"), r.Str("default_value"), r.Utc("updated_at"));
    public static AuditRow Audit(DbDataReader r) => new(r.Long("id"), r.Utc("occurred_at"), r.LongOrNull("actor_id"), r.StrOrNull("actor_name"), r.Str("action"), r.Str("entity_type"),
        r.StrOrNull("entity_id"), r.Str("summary"), Json(r, "details"), r.StrOrNull("ip"));
    public static PermissionRow Permission(DbDataReader r) => new(r.Str("code"), r.Str("group_name"), r.Str("description"));
    public static AdminUserRow AdminUser(DbDataReader r) => new(Mappers.User(r), r.StrOrNull("role_names"), r.Bool("has_two_factor"));
}
