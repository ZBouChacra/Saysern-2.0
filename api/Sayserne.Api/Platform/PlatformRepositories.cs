using System.Text.Json;
using Sayserne.Api.Data;

namespace Sayserne.Api.Platform;

// Repositories only translate method calls into stored-procedure calls. No SQL text here.

public interface IPlatformRepository
{
    Task<IReadOnlyList<SettingRow>> SettingsAsync(CancellationToken ct = default);
    Task<SettingRow> SetSettingAsync(long actorId, string key, string value, CancellationToken ct = default);
    Task<SettingRow> ResetSettingAsync(long actorId, string key, CancellationToken ct = default);

    Task<IReadOnlyList<LookupType>> LookupTypesAsync(CancellationToken ct = default);
    Task<LookupType> SaveLookupTypeAsync(long actorId, string code, string name, string? description, string? parentType, CancellationToken ct = default);
    Task<IReadOnlyList<LookupValue>> LookupValuesAsync(string type, string? parentCode, bool includeInactive, CancellationToken ct = default);
    Task<LookupValue> SaveLookupValueAsync(long actorId, string type, string code, string? parentCode, int? sortOrder, bool? isActive, JsonElement? metadata,
        IReadOnlyDictionary<string, string> names, CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, string>> TranslationsAsync(string language, CancellationToken ct = default);
    Task<IReadOnlyList<TranslationRow>> AllTranslationsAsync(CancellationToken ct = default);
    Task<TranslationRow> SaveTranslationAsync(long actorId, string language, string key, string value, CancellationToken ct = default);
    Task<TranslationRow> ResetTranslationAsync(long actorId, string language, string key, CancellationToken ct = default);

    Task<PagedResult<AuditRow>> AuditAsync(AuditFilter filter, CancellationToken ct = default);
    Task WriteAuditAsync(long? actorId, string action, string entityType, string? entityId, string summary, object? details, string? ip, CancellationToken ct = default);

    Task<IReadOnlyList<PermissionRow>> PermissionsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<RoleRow>> RolesAsync(long? id = null, CancellationToken ct = default);
    Task<RoleRow> SaveRoleAsync(long actorId, long? id, string name, string? description, IReadOnlyList<string> permissions, CancellationToken ct = default);
    Task DeleteRoleAsync(long actorId, long id, CancellationToken ct = default);
    Task<IReadOnlyList<RoleRef>> SetUserRolesAsync(long actorId, long userId, IReadOnlyList<long> roleIds, CancellationToken ct = default);
    Task<StaffInfo?> StaffAsync(long userId, CancellationToken ct = default);

    Task<Category> SaveCategoryAsync(long actorId, long? id, string name, string? nameAr, string? description, string? descriptionAr, string? icon, bool? isBlocked, CancellationToken ct = default);
    Task<Service> SaveServiceAsync(long actorId, long? id, long categoryId, string name, string? nameAr, string? description, string? descriptionAr, bool? isBlocked, CancellationToken ct = default);
}

public interface ITwoFactorRepository
{
    Task<TwoFactorState?> GetAsync(long userId, CancellationToken ct = default);
    Task BeginAsync(long userId, string secretProtected, CancellationToken ct = default);
    Task ConfirmAsync(long userId, long step, IReadOnlyList<string> recoveryHashes, CancellationToken ct = default);
    Task UseStepAsync(long userId, long step, CancellationToken ct = default);
    Task UseRecoveryAsync(long userId, string hash, CancellationToken ct = default);
    Task ReplaceRecoveryAsync(long userId, IReadOnlyList<string> hashes, CancellationToken ct = default);
    Task<int> RecoveryCountAsync(long userId, CancellationToken ct = default);
    Task ResetAsync(long actorId, long userId, CancellationToken ct = default);
}

public sealed class PlatformRepository(IStoredProcedureExecutor db) : IPlatformRepository
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<IReadOnlyList<SettingRow>> SettingsAsync(CancellationToken ct = default) => db.QueryAsync("sp_settings_list", null, r => PlatformMappers.Setting(r), ct);

    public async Task<SettingRow> SetSettingAsync(long actorId, string key, string value, CancellationToken ct = default) =>
        (await db.QuerySingleOrDefaultAsync("sp_settings_set", new ProcParams().Add("p_actor_id", actorId).Add("p_key", key).Add("p_value", value), r => PlatformMappers.Setting(r), ct))!;

    public async Task<SettingRow> ResetSettingAsync(long actorId, string key, CancellationToken ct = default) =>
        (await db.QuerySingleOrDefaultAsync("sp_settings_reset", new ProcParams().Add("p_actor_id", actorId).Add("p_key", key), r => PlatformMappers.Setting(r), ct))!;

    public Task<IReadOnlyList<LookupType>> LookupTypesAsync(CancellationToken ct = default) => db.QueryAsync("sp_lookups_types_list", null, PlatformMappers.LookupType, ct);

    public async Task<LookupType> SaveLookupTypeAsync(long actorId, string code, string name, string? description, string? parentType, CancellationToken ct = default) =>
        (await db.QuerySingleOrDefaultAsync("sp_lookups_types_save",
            new ProcParams().Add("p_actor_id", actorId).Add("p_code", code).Add("p_name", name).Add("p_description", description).Add("p_parent_type", parentType),
            PlatformMappers.LookupType, ct))!;

    private sealed record LookupRow(long Id, string Type, string Code, string? Parent, int Sort, bool Active, JsonElement? Meta);
    private sealed record NameRow(long ValueId, string Language, string Name);

    public async Task<IReadOnlyList<LookupValue>> LookupValuesAsync(string type, string? parentCode, bool includeInactive, CancellationToken ct = default) =>
        await ReadLookupsAsync(new ProcParams().Add("p_type", type).Add("p_parent_code", parentCode).Add("p_code", null).Add("p_include_inactive", includeInactive), "sp_lookups_values_list", ct);

    private async Task<IReadOnlyList<LookupValue>> ReadLookupsAsync(ProcParams args, string procedure, CancellationToken ct)
    {
        var (rows, names) = await db.QueryTwoAsync(procedure, args,
            r => new LookupRow(r.Long("id"), r.Str("type_code"), r.Str("code"), r.StrOrNull("parent_code"), r.Int("sort_order"), r.Bool("is_active"), PlatformMappers.Json(r, "metadata")),
            r => new NameRow(r.Long("value_id"), r.Str("language"), r.Str("name")), ct);
        var byValue = names.GroupBy(n => n.ValueId).ToDictionary(g => g.Key, g => (IReadOnlyDictionary<string, string>)g.ToDictionary(n => n.Language, n => n.Name));
        return rows.Select(x => new LookupValue(x.Id, x.Type, x.Code, x.Parent, x.Sort, x.Active, x.Meta, byValue.GetValueOrDefault(x.Id) ?? new Dictionary<string, string>())).ToList();
    }

    public async Task<LookupValue> SaveLookupValueAsync(long actorId, string type, string code, string? parentCode, int? sortOrder, bool? isActive, JsonElement? metadata,
        IReadOnlyDictionary<string, string> names, CancellationToken ct = default)
    {
        var list = await ReadLookupsAsync(new ProcParams().Add("p_actor_id", actorId).Add("p_type", type).Add("p_code", code).Add("p_parent_code", parentCode)
            .Add("p_sort_order", sortOrder).Add("p_is_active", isActive).Add("p_metadata", metadata is { ValueKind: not JsonValueKind.Null } m ? m.GetRawText() : null)
            .Add("p_names", JsonSerializer.Serialize(names.Select(n => new { language = n.Key, name = n.Value }), Json)), "sp_lookups_values_save", ct);
        return list[0];
    }

    public async Task<IReadOnlyDictionary<string, string>> TranslationsAsync(string language, CancellationToken ct = default) =>
        (await db.QueryAsync("sp_translations_get", new ProcParams().Add("p_language", language), r => (Key: r.Str("translation_key"), Value: r.Str("value")), ct))
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);

    public Task<IReadOnlyList<TranslationRow>> AllTranslationsAsync(CancellationToken ct = default) => db.QueryAsync("sp_translations_all", null, PlatformMappers.Translation, ct);

    public async Task<TranslationRow> SaveTranslationAsync(long actorId, string language, string key, string value, CancellationToken ct = default) =>
        (await db.QuerySingleOrDefaultAsync("sp_translations_save", new ProcParams().Add("p_actor_id", actorId).Add("p_language", language).Add("p_key", key).Add("p_value", value),
            PlatformMappers.Translation, ct))!;

    public async Task<TranslationRow> ResetTranslationAsync(long actorId, string language, string key, CancellationToken ct = default) =>
        (await db.QuerySingleOrDefaultAsync("sp_translations_reset", new ProcParams().Add("p_actor_id", actorId).Add("p_language", language).Add("p_key", key),
            PlatformMappers.Translation, ct))!;

    public async Task<PagedResult<AuditRow>> AuditAsync(AuditFilter f, CancellationToken ct = default)
    {
        var (rows, totals) = await db.QueryTwoAsync("sp_audit_list",
            new ProcParams().Add("p_from", f.From).Add("p_to", f.To).Add("p_actor_id", f.ActorId).Add("p_action", f.Action).Add("p_entity_type", f.EntityType)
                .Add("p_search", f.Search).Add("p_page", f.Page).Add("p_page_size", f.PageSize),
            PlatformMappers.Audit, r => r.Long("total"), ct);
        return new PagedResult<AuditRow>(rows, totals.Count > 0 ? totals[0] : 0, Math.Max(f.Page, 1), Math.Clamp(f.PageSize, 1, 200));
    }

    public Task WriteAuditAsync(long? actorId, string action, string entityType, string? entityId, string summary, object? details, string? ip, CancellationToken ct = default) =>
        db.ExecuteAsync("sp_audit_write", new ProcParams().Add("p_actor_id", actorId).Add("p_action", action).Add("p_entity_type", entityType).Add("p_entity_id", entityId)
            .Add("p_summary", summary).Add("p_details", details is null ? null : JsonSerializer.Serialize(details, Json)).Add("p_ip", ip), ct);

    public Task<IReadOnlyList<PermissionRow>> PermissionsAsync(CancellationToken ct = default) => db.QueryAsync("sp_permissions_list", null, PlatformMappers.Permission, ct);

    private sealed record RoleHead(long Id, string Name, string? Description, int UserCount);
    private sealed record RolePerm(long RoleId, string Code);

    public async Task<IReadOnlyList<RoleRow>> RolesAsync(long? id = null, CancellationToken ct = default)
    {
        var (roles, perms) = await db.QueryTwoAsync("sp_roles_list", new ProcParams().Add("p_id", id),
            r => new RoleHead(r.Long("id"), r.Str("name"), r.StrOrNull("description"), r.Int("user_count")), r => new RolePerm(r.Long("role_id"), r.Str("permission_code")), ct);
        var by = perms.GroupBy(p => p.RoleId).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(p => p.Code).ToList());
        return roles.Select(x => new RoleRow(x.Id, x.Name, x.Description, x.UserCount, by.GetValueOrDefault(x.Id) ?? [])).ToList();
    }

    public async Task<RoleRow> SaveRoleAsync(long actorId, long? id, string name, string? description, IReadOnlyList<string> permissions, CancellationToken ct = default)
    {
        var (roles, perms) = await db.QueryTwoAsync("sp_roles_save",
            new ProcParams().Add("p_actor_id", actorId).Add("p_id", id).Add("p_name", name).Add("p_description", description).Add("p_permissions", JsonSerializer.Serialize(permissions)),
            r => new RoleHead(r.Long("id"), r.Str("name"), r.StrOrNull("description"), r.Int("user_count")), r => new RolePerm(r.Long("role_id"), r.Str("permission_code")), ct);
        return new RoleRow(roles[0].Id, roles[0].Name, roles[0].Description, roles[0].UserCount, perms.Select(p => p.Code).ToList());
    }

    public Task DeleteRoleAsync(long actorId, long id, CancellationToken ct = default) =>
        db.ExecuteAsync("sp_roles_delete", new ProcParams().Add("p_actor_id", actorId).Add("p_id", id), ct);

    public Task<IReadOnlyList<RoleRef>> SetUserRolesAsync(long actorId, long userId, IReadOnlyList<long> roleIds, CancellationToken ct = default) =>
        db.QueryAsync("sp_user_roles_set", new ProcParams().Add("p_actor_id", actorId).Add("p_user_id", userId).Add("p_role_ids", JsonSerializer.Serialize(roleIds)),
            r => new RoleRef(r.Long("id"), r.Str("name")), ct);

    private sealed record StaffHead(long UserId, bool IsSuper, int RoleCount);
    public async Task<StaffInfo?> StaffAsync(long userId, CancellationToken ct = default)
    {
        var (head, perms) = await db.QueryTwoAsync("sp_staff_get", new ProcParams().Add("p_user_id", userId),
            r => new StaffHead(r.Long("id"), r.Bool("is_super"), r.Int("role_count")), r => r.Str("permission_code"), ct);
        return head.Count == 0 ? null : new StaffInfo(head[0].UserId, head[0].IsSuper, head[0].RoleCount, perms);
    }

    public async Task<Category> SaveCategoryAsync(long actorId, long? id, string name, string? nameAr, string? description, string? descriptionAr, string? icon, bool? isBlocked, CancellationToken ct = default) =>
        (await db.QuerySingleOrDefaultAsync("sp_catalog_categories_save",
            new ProcParams().Add("p_actor_id", actorId).Add("p_id", id).Add("p_name", name).Add("p_name_ar", nameAr).Add("p_description", description)
                .Add("p_description_ar", descriptionAr).Add("p_icon", icon).Add("p_is_blocked", isBlocked), MarketplaceMappers.Category, ct))!;

    public async Task<Service> SaveServiceAsync(long actorId, long? id, long categoryId, string name, string? nameAr, string? description, string? descriptionAr, bool? isBlocked, CancellationToken ct = default) =>
        (await db.QuerySingleOrDefaultAsync("sp_catalog_services_save",
            new ProcParams().Add("p_actor_id", actorId).Add("p_id", id).Add("p_category_id", categoryId).Add("p_name", name).Add("p_name_ar", nameAr)
                .Add("p_description", description).Add("p_description_ar", descriptionAr).Add("p_is_blocked", isBlocked), MarketplaceMappers.Service, ct))!;
}

public sealed class TwoFactorRepository(IStoredProcedureExecutor db) : ITwoFactorRepository
{
    public Task<TwoFactorState?> GetAsync(long userId, CancellationToken ct = default) =>
        db.QuerySingleOrDefaultAsync("sp_twofactor_get", new ProcParams().Add("p_user_id", userId),
            r => new TwoFactorState(r.Str("secret_protected"), r.Bool("is_confirmed"), r.LongOrNull("last_used_step")), ct);

    public Task BeginAsync(long userId, string secretProtected, CancellationToken ct = default) =>
        db.ExecuteAsync("sp_twofactor_begin", new ProcParams().Add("p_user_id", userId).Add("p_secret_protected", secretProtected), ct);

    public Task ConfirmAsync(long userId, long step, IReadOnlyList<string> recoveryHashes, CancellationToken ct = default) =>
        db.ExecuteAsync("sp_twofactor_confirm", new ProcParams().Add("p_user_id", userId).Add("p_step", step).Add("p_hashes", JsonSerializer.Serialize(recoveryHashes)), ct);

    public Task UseStepAsync(long userId, long step, CancellationToken ct = default) =>
        db.ExecuteAsync("sp_twofactor_use_step", new ProcParams().Add("p_user_id", userId).Add("p_step", step), ct);

    public Task UseRecoveryAsync(long userId, string hash, CancellationToken ct = default) =>
        db.ExecuteAsync("sp_twofactor_use_recovery", new ProcParams().Add("p_user_id", userId).Add("p_hash", hash), ct);

    public Task ReplaceRecoveryAsync(long userId, IReadOnlyList<string> hashes, CancellationToken ct = default) =>
        db.ExecuteAsync("sp_twofactor_replace_recovery", new ProcParams().Add("p_user_id", userId).Add("p_hashes", JsonSerializer.Serialize(hashes)), ct);

    public async Task<int> RecoveryCountAsync(long userId, CancellationToken ct = default)
    {
        var rows = await db.QueryAsync("sp_twofactor_recovery_count", new ProcParams().Add("p_user_id", userId), r => r.Int("remaining"), ct);
        return rows.Count > 0 ? rows[0] : 0;
    }

    public Task ResetAsync(long actorId, long userId, CancellationToken ct = default) =>
        db.ExecuteAsync("sp_twofactor_reset", new ProcParams().Add("p_actor_id", actorId).Add("p_user_id", userId), ct);
}
