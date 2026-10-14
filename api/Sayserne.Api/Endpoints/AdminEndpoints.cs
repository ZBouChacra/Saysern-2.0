using Sayserne.Api.Auth;
using Sayserne.Api.Data;

namespace Sayserne.Api.Endpoints;

public static class AdminEndpoints
{
    public const string PolicyName = "AdminOnly";
    public sealed record SetLockedRequest(bool IsLocked);
    public sealed record SetRoleRequest(string? Role);

    public static void MapAdmin(this IEndpointRouteBuilder app)
    {
        // Every route below requires an authenticated session whose current role is "admin".
        var g = app.MapGroup("/api/admin").RequireAuthorization(PolicyName);

        g.MapGet("/users", async (IAdminUserRepository admin, string? search, int? page, int? pageSize) =>
        {
            var result = await admin.ListAsync(search, page ?? 1, pageSize ?? 20);
            return Results.Ok(new PagedResult<UserDto>(result.Items.Select(u => u.ToDto()).ToList(), result.Total, result.Page, result.PageSize));
        });

        g.MapPost("/users/{id:long}/lock", async (HttpContext ctx, long id, SetLockedRequest body, IAdminUserRepository admin) =>
        {
            if (AuthSession.CurrentUserId(ctx.User) == id) throw new AppException("CANNOT_MODIFY_SELF", 400);
            return Results.Ok(new { user = (await admin.SetLockedAsync(id, body.IsLocked)).ToDto() });
        });

        g.MapPost("/users/{id:long}/role", async (HttpContext ctx, long id, SetRoleRequest body, IAdminUserRepository admin) =>
        {
            if (AuthSession.CurrentUserId(ctx.User) == id) throw new AppException("CANNOT_MODIFY_SELF", 400);
            if (body.Role is not ("user" or "admin")) throw new AppException("INVALID_ROLE", 400);
            return Results.Ok(new { user = (await admin.SetRoleAsync(id, body.Role)).ToDto() });
        });
    }
}
