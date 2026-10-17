using Sayserne.Api.Auth;
using Sayserne.Api.Data;
using Sayserne.Api.Infrastructure;
using Sayserne.Api.Platform;

namespace Sayserne.Api.Endpoints;

/// <summary>Back-office user management. Each route names the one permission it needs; roles (data) decide who holds it.</summary>
public static class AdminEndpoints
{
    public sealed record SetLockedRequest(bool IsLocked);
    public sealed record SetRoleRequest(string? Role);
    public sealed record UserRolesRequest(List<long>? RoleIds);

    public static void MapAdmin(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin");

        g.MapGet("/users", async (IAdminUserRepository admin, string? search, int? page, int? pageSize) =>
        {
            var result = await admin.ListAsync(search, page ?? 1, pageSize ?? 20);
            return Results.Ok(new PagedResult<AdminUserDto>(result.Items.Select(AdminUserDto.From).ToList(), result.Total, result.Page, result.PageSize));
        }).RequirePermission("users.read");

        // Everything about one person, for support: account, profile (with the private details), listings with their weekly hours, recent appointments and a few counts.
        // It shows personal data, so every opening is written to the audit log.
        g.MapGet("/users/{id:long}", async (HttpContext ctx, long id, IUserRepository users, IProfessionalRepository profiles, IBookingRepository bookings, IStoredProcedureExecutor db, IPlatformRepository platform) =>
        {
            var user = await users.GetByIdAsync(id) ?? throw new AppException("USER_NOT_FOUND", 404);
            var counts = (await db.QueryAsync("sp_admin_user_counts", new ProcParams().Add("p_user_id", id), r => new
            {
                appointmentsAsClient = r.Long("appointments_as_client"), appointmentsAsProfessional = r.Long("appointments_as_professional"), reviewsWritten = r.Long("reviews_written"),
                reviewsReceived = r.Long("reviews_received"), reviewsHidden = r.Long("reviews_hidden"), conversations = r.Long("conversations"), premiumBatches = r.Long("premium_batches"),
            }, ctx.RequestAborted))[0];
            await platform.WriteAuditAsync(ctx.UserId(), "users.view", "user", id.ToString(), $"Opened the details of {user.Email}", null, ctx.Connection.RemoteIpAddress?.ToString(), ctx.RequestAborted);
            return Results.Ok(new
            {
                user = user.ToDto(),
                profile = await profiles.GetPrivateProfileAsync(id, ctx.RequestAborted),
                professions = await profiles.ListProfessionsAsync(id, false, ctx.RequestAborted),
                availability = await profiles.GetAvailabilityAsync(id, ctx.RequestAborted),
                appointments = (await bookings.ListAppointmentsAsync(id, ctx.RequestAborted)).Take(25).ToList(),
                counts,
            });
        }).RequirePermission("users.read");

        g.MapPost("/users/{id:long}/lock", async (HttpContext ctx, long id, SetLockedRequest body, IAdminUserRepository admin) =>
        {
            if (ctx.UserId() == id) throw new AppException("CANNOT_MODIFY_SELF", 400);
            return Results.Ok(new { user = (await admin.SetLockedAsync(ctx.UserId(), id, body.IsLocked)).ToDto() });
        }).RequirePermission("users.manage");

        g.MapPost("/users/{id:long}/flags", async (HttpContext ctx, long id, FlagsInput body, IAdminUserRepository admin) =>
            Results.Ok(new { user = (await admin.SetFlagsAsync(ctx.UserId(), id, body.IsStarred)).ToDto() })).RequirePermission("users.manage");

        // the super-admin flag and two-factor resets are for super admins only (permission admins.manage)
        g.MapPost("/users/{id:long}/role", async (HttpContext ctx, long id, SetRoleRequest body, IAdminUserRepository admin) =>
        {
            if (ctx.UserId() == id) throw new AppException("CANNOT_MODIFY_SELF", 400);
            if (body.Role is not ("user" or "admin")) throw new AppException("INVALID_ROLE", 400);
            return Results.Ok(new { user = (await admin.SetRoleAsync(ctx.UserId(), id, body.Role)).ToDto() });
        }).RequirePermission("admins.manage");

        g.MapPost("/users/{id:long}/two-factor/reset", async (HttpContext ctx, long id, ITwoFactorRepository twoFactor) =>
        {
            if (ctx.UserId() == id) throw new AppException("CANNOT_MODIFY_SELF", 400);
            await twoFactor.ResetAsync(ctx.UserId(), id);
            return Results.NoContent();
        }).RequirePermission("admins.manage");

        g.MapPut("/users/{id:long}/roles", async (HttpContext ctx, long id, UserRolesRequest body, IPlatformRepository platform) =>
        {
            if (ctx.UserId() == id) throw new AppException("CANNOT_MODIFY_SELF", 400);
            return Results.Ok(new { roles = await platform.SetUserRolesAsync(ctx.UserId(), id, body.RoleIds ?? []) });
        }).RequirePermission("roles.manage");
    }
}
