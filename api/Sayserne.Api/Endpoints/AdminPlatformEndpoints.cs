using System.Text.Json;
using Sayserne.Api.Auth;
using Sayserne.Api.Data;
using Sayserne.Api.Jobs;
using Sayserne.Api.Platform;

namespace Sayserne.Api.Endpoints;

/// <summary>Back-office configuration: settings, lookups, translations, roles, catalog and the audit log.</summary>
public static class AdminPlatformEndpoints
{
    public sealed record SettingValueRequest(string? Value);
    public sealed record LookupTypeRequest(string? Name, string? Description, string? ParentType);
    public sealed record LookupValueRequest(string? ParentCode, int? SortOrder, bool? IsActive, JsonElement? Metadata, Dictionary<string, string>? Names);
    public sealed record TranslationRequest(string? Value);
    public sealed record RoleRequest(string? Name, string? Description, List<string>? Permissions);
    public sealed record CategoryRequest(string? Name, string? NameAr, string? Description, string? DescriptionAr, string? Icon, bool? IsBlocked);
    public sealed record ServiceRequest(long? CategoryId, string? Name, string? NameAr, string? Description, string? DescriptionAr, bool? IsBlocked);

    private static AppException Invalid(string field, string message) => new("VALIDATION_FAILED", 400, fieldErrors: new Dictionary<string, string> { [field] = message });

    public static void MapAdminPlatform(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin");

        // ---------------- settings
        g.MapGet("/settings", async (IPlatformRepository platform) => Results.Ok(await platform.SettingsAsync())).RequirePermission("settings.read");
        g.MapPut("/settings/{key}", async (HttpContext ctx, string key, SettingValueRequest body, IPlatformRepository platform, ISettingsService settings) =>
        {
            var row = await platform.SetSettingAsync(ctx.UserId(), key, body.Value ?? "");
            await settings.RefreshAsync();   // takes effect on this instance immediately, on others within the refresh interval
            return Results.Ok(row);
        }).RequirePermission("settings.manage");
        g.MapPost("/settings/{key}/reset", async (HttpContext ctx, string key, IPlatformRepository platform, ISettingsService settings) =>
        {
            var row = await platform.ResetSettingAsync(ctx.UserId(), key);
            await settings.RefreshAsync();
            return Results.Ok(row);
        }).RequirePermission("settings.manage");

        // ---------------- lookups
        g.MapGet("/lookups/types", async (IPlatformRepository platform) => Results.Ok(await platform.LookupTypesAsync())).RequirePermission("lookups.manage");
        g.MapPut("/lookups/types/{code}", async (HttpContext ctx, string code, LookupTypeRequest body, IPlatformRepository platform) =>
            Results.Ok(await platform.SaveLookupTypeAsync(ctx.UserId(), code, body.Name ?? "", body.Description, body.ParentType))).RequirePermission("lookups.manage");
        g.MapGet("/lookups/{type}/values", async (string type, IPlatformRepository platform, string? parent, bool? includeInactive) =>
            Results.Ok(await platform.LookupValuesAsync(type, parent, includeInactive ?? true))).RequirePermission("lookups.manage");
        g.MapPut("/lookups/{type}/values/{code}", async (HttpContext ctx, string type, string code, LookupValueRequest body, IPlatformRepository platform) =>
            Results.Ok(await platform.SaveLookupValueAsync(ctx.UserId(), type, code, body.ParentCode, body.SortOrder, body.IsActive, body.Metadata, body.Names ?? new Dictionary<string, string>())))
            .RequirePermission("lookups.manage");

        // ---------------- translations
        g.MapGet("/translations", async (IPlatformRepository platform) => Results.Ok(await platform.AllTranslationsAsync())).RequirePermission("translations.manage");
        g.MapPut("/translations/{language}/{key}", async (HttpContext ctx, string language, string key, TranslationRequest body, IPlatformRepository platform) =>
            Results.Ok(await platform.SaveTranslationAsync(ctx.UserId(), language, key, body.Value ?? ""))).RequirePermission("translations.manage");
        g.MapPost("/translations/{language}/{key}/reset", async (HttpContext ctx, string language, string key, IPlatformRepository platform) =>
            Results.Ok(await platform.ResetTranslationAsync(ctx.UserId(), language, key))).RequirePermission("translations.manage");

        // ---------------- roles & permissions
        g.MapGet("/permissions", async (IPlatformRepository platform) => Results.Ok(await platform.PermissionsAsync())).RequirePermission("roles.manage");
        g.MapGet("/roles", async (IPlatformRepository platform) => Results.Ok(await platform.RolesAsync())).RequirePermission("roles.manage");
        g.MapPost("/roles", async (HttpContext ctx, RoleRequest body, IPlatformRepository platform) =>
            Results.Json(await platform.SaveRoleAsync(ctx.UserId(), null, body.Name ?? "", body.Description, body.Permissions ?? []), statusCode: StatusCodes.Status201Created))
            .RequirePermission("roles.manage");
        g.MapPut("/roles/{id:long}", async (HttpContext ctx, long id, RoleRequest body, IPlatformRepository platform) =>
            Results.Ok(await platform.SaveRoleAsync(ctx.UserId(), id, body.Name ?? "", body.Description, body.Permissions ?? []))).RequirePermission("roles.manage");
        g.MapDelete("/roles/{id:long}", async (HttpContext ctx, long id, IPlatformRepository platform) =>
        {
            await platform.DeleteRoleAsync(ctx.UserId(), id);
            return Results.NoContent();
        }).RequirePermission("roles.manage");

        // ---------------- catalog (all entries, including blocked ones)
        g.MapGet("/catalog/categories", async (ICatalogRepository catalog) => Results.Ok(await catalog.CategoriesAsync(onlyActive: false))).RequirePermission("catalog.manage");
        g.MapGet("/catalog/services", async (ICatalogRepository catalog, long? categoryId) => Results.Ok(await catalog.ServicesAsync(categoryId, onlyActive: false))).RequirePermission("catalog.manage");
        g.MapPost("/catalog/categories", async (HttpContext ctx, CategoryRequest b, IPlatformRepository platform) =>
            Results.Json(await platform.SaveCategoryAsync(ctx.UserId(), null, b.Name ?? "", b.NameAr, b.Description, b.DescriptionAr, b.Icon, b.IsBlocked), statusCode: StatusCodes.Status201Created))
            .RequirePermission("catalog.manage");
        g.MapPut("/catalog/categories/{id:long}", async (HttpContext ctx, long id, CategoryRequest b, IPlatformRepository platform) =>
            Results.Ok(await platform.SaveCategoryAsync(ctx.UserId(), id, b.Name ?? "", b.NameAr, b.Description, b.DescriptionAr, b.Icon, b.IsBlocked))).RequirePermission("catalog.manage");
        g.MapPost("/catalog/services", async (HttpContext ctx, ServiceRequest b, IPlatformRepository platform) =>
        {
            if (b.CategoryId is null or <= 0) throw Invalid("categoryId", "Choose a category.");
            return Results.Json(await platform.SaveServiceAsync(ctx.UserId(), null, b.CategoryId.Value, b.Name ?? "", b.NameAr, b.Description, b.DescriptionAr, b.IsBlocked), statusCode: StatusCodes.Status201Created);
        }).RequirePermission("catalog.manage");
        g.MapPut("/catalog/services/{id:long}", async (HttpContext ctx, long id, ServiceRequest b, IPlatformRepository platform) =>
        {
            if (b.CategoryId is null or <= 0) throw Invalid("categoryId", "Choose a category.");
            return Results.Ok(await platform.SaveServiceAsync(ctx.UserId(), id, b.CategoryId.Value, b.Name ?? "", b.NameAr, b.Description, b.DescriptionAr, b.IsBlocked));
        }).RequirePermission("catalog.manage");

        // ---------------- background jobs
        g.MapGet("/jobs", async (IJobQueue jobs, string? status, string? type, int? page, int? pageSize) =>
            Results.Ok(await jobs.ListAsync(status, type, page ?? 1, pageSize ?? 25))).RequirePermission("jobs.read");
        g.MapGet("/jobs/stats", async (IJobQueue jobs) => Results.Ok(await jobs.StatsAsync())).RequirePermission("jobs.read");
        g.MapPost("/jobs/{id:long}/retry", async (HttpContext ctx, long id, IJobQueue jobs) => Results.Ok(await jobs.RetryAsync(ctx.UserId(), id))).RequirePermission("jobs.manage");

        // ---------------- audit log (read-only: there is no way to change or delete an entry through the API)
        g.MapGet("/audit", async (IPlatformRepository platform, DateTimeOffset? from, DateTimeOffset? to, long? actorId, string? action, string? entityType, string? search, int? page, int? pageSize) =>
            Results.Ok(await platform.AuditAsync(new AuditFilter(from?.UtcDateTime, to?.UtcDateTime, actorId, action, entityType, search, page ?? 1, pageSize ?? 50)))).RequirePermission("audit.read");
    }
}
