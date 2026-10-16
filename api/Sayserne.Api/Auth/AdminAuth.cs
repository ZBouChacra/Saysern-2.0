using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Sayserne.Api.Data;
using Sayserne.Api.Platform;

namespace Sayserne.Api.Auth;

public sealed record PendingAdmin(long UserId);

/// <summary>
/// The back office has its own cookie scheme ("Admin"): a separate, SameSite=Strict cookie that only exists after password + a second factor.
/// A normal website session can never reach /api/admin, and an admin session is never accepted by the public API.
/// </summary>
public static class AdminAuth
{
    public const string Scheme = "Admin";
    public const string PendingCookie = "sayserne.admin.pending";
    public const string CookiePath = "/api/admin/auth";
    public const string PermissionClaim = "perm";
    public const string SuperClaim = "super";
    public const string AuthTimeClaim = "auth_time";
    public const string LastActiveClaim = "last_active";

    public static ClaimsPrincipal Principal(UserRecord u, StaffInfo staff, long authTime, long lastActive)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, u.Id.ToString()), new(ClaimTypes.Email, u.Email), new(ClaimTypes.Name, $"{u.FirstName} {u.LastName}".Trim()),
            new(AuthTimeClaim, authTime.ToString()), new(LastActiveClaim, lastActive.ToString()), new("amr", "pwd,mfa"),
        };
        if (staff.IsSuper) claims.Add(new Claim(SuperClaim, "1"));
        claims.AddRange(staff.Permissions.Select(p => new Claim(PermissionClaim, p)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme));
    }

    public static async Task SignInAsync(HttpContext ctx, UserRecord user, StaffInfo staff)
    {
        var clock = ctx.RequestServices.GetRequiredService<TimeProvider>();
        var settings = ctx.RequestServices.GetRequiredService<ISettingsService>();
        var now = clock.GetUtcNow();
        await ctx.SignInAsync(Scheme, Principal(user, staff, now.ToUnixTimeSeconds(), now.ToUnixTimeSeconds()),
            new AuthenticationProperties { IsPersistent = false, ExpiresUtc = now.AddHours(settings.GetInt("Auth.AdminMaxSessionHours")) });
    }

    /// <summary>Runs on every request: ends idle/over-long sessions, re-reads the account and its permissions (so revocations are immediate).</summary>
    public static async Task ValidateAsync(CookieValidatePrincipalContext c)
    {
        var sp = c.HttpContext.RequestServices;
        var clock = sp.GetRequiredService<TimeProvider>();
        var settings = sp.GetRequiredService<ISettingsService>();
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        long Claim(string type) => long.TryParse(c.Principal!.FindFirstValue(type), out var v) ? v : 0;

        var id = AuthSession.CurrentUserId(c.Principal!);
        var user = id is null ? null : await sp.GetRequiredService<IUserRepository>().GetByIdAsync(id.Value);
        var staff = id is null ? null : await sp.GetRequiredService<IPlatformRepository>().StaffAsync(id.Value);
        var idleOver = now - Claim(LastActiveClaim) > settings.GetInt("Auth.AdminIdleMinutes") * 60L;
        var tooOld = now - Claim(AuthTimeClaim) > settings.GetInt("Auth.AdminMaxSessionHours") * 3600L;
        if (user is null || staff is null || !staff.IsStaff || user.IsLocked || user.IsTemporarilyLocked(clock.GetUtcNow().UtcDateTime) || idleOver || tooOld)
        {
            c.RejectPrincipal();
            await c.HttpContext.SignOutAsync(Scheme);
            return;
        }
        var previousLastActive = Claim(LastActiveClaim);   // read BEFORE the principal is replaced
        var authTime = Claim(AuthTimeClaim);
        var lastActive = now - previousLastActive > 60 ? now : previousLastActive;
        c.ReplacePrincipal(Principal(user, staff, authTime, lastActive));
        if (lastActive != previousLastActive) c.ShouldRenew = true;   // re-issue the cookie so the idle clock moves forward
    }

    public static void ConfigureCookie(CookieAuthenticationOptions o, bool development)
    {
        o.Cookie.Name = "sayserne.admin";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = development ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        o.SlidingExpiration = false;
        o.ExpireTimeSpan = TimeSpan.FromHours(24);   // upper bound; the real limits are the admin settings checked on every request
        o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
        o.Events.OnValidatePrincipal = ValidateAsync;
    }
}

// ---- permission-based authorization: "perm:<code>" policies are created on demand from the permission codes in the database ----

public sealed class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}

public sealed class PermissionHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (context.User.HasClaim(AdminAuth.PermissionClaim, requirement.Permission)) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

public sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options) : DefaultAuthorizationPolicyProvider(options)
{
    public const string Prefix = "perm:";
    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName) =>
        policyName.StartsWith(Prefix, StringComparison.Ordinal)
            ? new AuthorizationPolicyBuilder(AdminAuth.Scheme).RequireAuthenticatedUser().AddRequirements(new PermissionRequirement(policyName[Prefix.Length..])).Build()
            : await base.GetPolicyAsync(policyName);
}

public static class PermissionExtensions
{
    public const string StaffPolicy = "staff";
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission) where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(PermissionPolicyProvider.Prefix + permission);
}
