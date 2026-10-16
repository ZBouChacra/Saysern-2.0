using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Sayserne.Api.Auth;
using Sayserne.Api.Data;
using Sayserne.Api.Platform;

namespace Sayserne.Api.Endpoints;

/// <summary>
/// Back-office sign-in: password, then an authenticator code (or a one-time recovery code). Admin accounts that have not set up
/// two-factor yet are forced to enroll before a session is issued. Google sign-in is deliberately not available here.
/// </summary>
public static class AdminAuthEndpoints
{
    public sealed record AdminLoginRequest(string? Email, string? Password);
    public sealed record CodeRequest(string? Code, string? RecoveryCode);

    private static string? Ip(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString();

    public static void MapAdminAuth(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/auth").RequireRateLimiting("auth");
        g.MapPost("/login", Login);
        g.MapPost("/2fa/enroll", Enroll);
        g.MapPost("/2fa/confirm", Confirm);
        g.MapPost("/verify", Verify);
        g.MapPost("/logout", (Delegate)Logout).RequireAuthorization(PermissionExtensions.StaffPolicy);
        g.MapGet("/me", Me).RequireAuthorization(PermissionExtensions.StaffPolicy);
        g.MapPost("/recovery-codes/regenerate", Regenerate).RequireAuthorization(PermissionExtensions.StaffPolicy);
    }

    private static async Task<IResult> Login(HttpContext ctx, AdminLoginRequest request, IUserRepository users, IPlatformRepository platform, ITwoFactorRepository twoFactor,
        IPasswordHasher hasher, DummyPasswordHash dummy, ISettingsService settings, ProtectedCookies cookies, TimeProvider clock)
    {
        var email = InputValidation.NormalizeEmail(request.Email);
        var password = request.Password ?? "";
        if (email.Length == 0 || password.Length == 0 || password.Length > 1024) throw new AppException("INVALID_CREDENTIALS");

        var user = await users.GetByEmailAsync(email);
        if (user is null) { hasher.Verify(password, dummy.Value); throw new AppException("INVALID_CREDENTIALS"); }
        if (user.IsLocked || user.IsTemporarilyLocked(clock.GetUtcNow().UtcDateTime)) throw new AppException("ACCOUNT_LOCKED");

        var staff = await platform.StaffAsync(user.Id);
        if (user.PasswordHash is null || !hasher.Verify(password, user.PasswordHash) || staff is null || !staff.IsStaff)
        {
            // customers and unknown accounts get the same answer as a wrong password; real admins' failures count towards the stricter admin lockout
            if (staff is { IsStaff: true })
            {
                await users.RecordLoginFailureAsync(user.Id, settings.GetInt("Auth.AdminMaxFailedAttempts"), settings.GetInt("Auth.AdminLockoutMinutes"));
                await platform.WriteAuditAsync(user.Id, "admin.login.failure", "user", user.Id.ToString(), "Wrong password at admin sign-in", null, Ip(ctx));
            }
            throw new AppException("INVALID_CREDENTIALS");
        }

        cookies.Write(ctx, AdminAuth.PendingCookie, new PendingAdmin(user.Id), TimeSpan.FromMinutes(5), AdminAuth.CookiePath);
        var state = await twoFactor.GetAsync(user.Id);
        return Results.Ok(new { status = state is { IsConfirmed: true } ? "totp_required" : "enrollment_required" });
    }

    private static async Task<(UserRecord User, StaffInfo Staff)> PendingAsync(HttpContext ctx, ProtectedCookies cookies, IUserRepository users, IPlatformRepository platform, TimeProvider clock)
    {
        var pending = cookies.Read<PendingAdmin>(ctx, AdminAuth.PendingCookie) ?? throw new AppException("PENDING_EXPIRED", 401);
        var user = await users.GetByIdAsync(pending.UserId);
        var staff = user is null ? null : await platform.StaffAsync(user.Id);
        if (user is null || staff is not { IsStaff: true } || user.IsLocked || user.IsTemporarilyLocked(clock.GetUtcNow().UtcDateTime)) throw new AppException("PENDING_EXPIRED", 401);
        return (user, staff);
    }

    private static async Task<IResult> Enroll(HttpContext ctx, ProtectedCookies cookies, IUserRepository users, IPlatformRepository platform, ITwoFactorRepository twoFactor,
        IDataProtectionProvider dp, ISettingsService settings, TimeProvider clock)
    {
        var (user, _) = await PendingAsync(ctx, cookies, users, platform, clock);
        var secret = Totp.NewSecret();
        await twoFactor.BeginAsync(user.Id, dp.CreateProtector("Sayserne.TwoFactor.v1").Protect(secret));   // TWO_FACTOR_ALREADY_ENROLLED -> 409
        return Results.Ok(new { secret, otpauthUri = Totp.ProvisioningUri(settings.GetString("Site.Name"), user.Email, secret) });
    }

    private static async Task<long> CheckCodeAsync(UserRecord user, string? code, ITwoFactorRepository twoFactor, IDataProtectionProvider dp, TimeProvider clock, bool requireConfirmed)
    {
        var state = await twoFactor.GetAsync(user.Id);
        if (state is null || (requireConfirmed && !state.IsConfirmed)) throw new AppException("TWO_FACTOR_NOT_STARTED", 409);
        var secret = dp.CreateProtector("Sayserne.TwoFactor.v1").Unprotect(state.SecretProtected);
        return Totp.Verify(secret, code, clock.GetUtcNow().ToUnixTimeSeconds(), state.LastUsedStep) ?? throw new AppException("INVALID_TWO_FACTOR_CODE", 401);
    }

    private static async Task<AppException> FailAsync(HttpContext ctx, UserRecord user, IUserRepository users, IPlatformRepository platform, ISettingsService settings, string summary)
    {
        await users.RecordLoginFailureAsync(user.Id, settings.GetInt("Auth.AdminMaxFailedAttempts"), settings.GetInt("Auth.AdminLockoutMinutes"));
        await platform.WriteAuditAsync(user.Id, "admin.login.failure", "user", user.Id.ToString(), summary, null, Ip(ctx));
        return new AppException("INVALID_TWO_FACTOR_CODE", 401);
    }

    private static async Task<IResult> Confirm(HttpContext ctx, CodeRequest request, ProtectedCookies cookies, IUserRepository users, IPlatformRepository platform,
        ITwoFactorRepository twoFactor, IDataProtectionProvider dp, ISettingsService settings, TimeProvider clock)
    {
        var (user, staff) = await PendingAsync(ctx, cookies, users, platform, clock);
        long step;
        try { step = await CheckCodeAsync(user, request.Code, twoFactor, dp, clock, requireConfirmed: false); }
        catch (AppException ex) when (ex.Code == "INVALID_TWO_FACTOR_CODE") { throw await FailAsync(ctx, user, users, platform, settings, "Wrong code while enrolling two-factor"); }

        var codes = RecoveryCodes.Generate();
        await twoFactor.ConfirmAsync(user.Id, step, codes.Select(RecoveryCodes.Hash).ToList());
        await users.RecordLoginSuccessAsync(user.Id);
        cookies.Delete(ctx, AdminAuth.PendingCookie, AdminAuth.CookiePath);
        await AdminAuth.SignInAsync(ctx, user, staff);
        await platform.WriteAuditAsync(user.Id, "admin.login.success", "user", user.Id.ToString(), "Signed in to the back office (first sign-in, two-factor enrolled)", null, Ip(ctx));
        return Results.Ok(new { recoveryCodes = codes, session = SessionInfo(user, staff, settings, codes.Count) });
    }

    private static async Task<IResult> Verify(HttpContext ctx, CodeRequest request, ProtectedCookies cookies, IUserRepository users, IPlatformRepository platform,
        ITwoFactorRepository twoFactor, IDataProtectionProvider dp, ISettingsService settings, TimeProvider clock)
    {
        var (user, staff) = await PendingAsync(ctx, cookies, users, platform, clock);
        if (!string.IsNullOrWhiteSpace(request.RecoveryCode))
        {
            try { await twoFactor.UseRecoveryAsync(user.Id, RecoveryCodes.Hash(request.RecoveryCode)); }   // single use; audited by the procedure
            catch (AppException ex) when (ex.Code == "INVALID_RECOVERY_CODE") { throw await FailAsync(ctx, user, users, platform, settings, "Wrong recovery code at admin sign-in"); }
        }
        else
        {
            long step;
            try { step = await CheckCodeAsync(user, request.Code, twoFactor, dp, clock, requireConfirmed: true); }
            catch (AppException ex) when (ex.Code == "INVALID_TWO_FACTOR_CODE") { throw await FailAsync(ctx, user, users, platform, settings, "Wrong authenticator code at admin sign-in"); }
            try { await twoFactor.UseStepAsync(user.Id, step); }
            catch (AppException ex) when (ex.Code == "TOTP_REPLAY") { throw await FailAsync(ctx, user, users, platform, settings, "Authenticator code reused at admin sign-in"); }
        }
        await users.RecordLoginSuccessAsync(user.Id);
        cookies.Delete(ctx, AdminAuth.PendingCookie, AdminAuth.CookiePath);
        await AdminAuth.SignInAsync(ctx, user, staff);
        await platform.WriteAuditAsync(user.Id, "admin.login.success", "user", user.Id.ToString(), "Signed in to the back office", null, Ip(ctx));
        return Results.Ok(new { session = SessionInfo(user, staff, settings, await twoFactor.RecoveryCountAsync(user.Id)) });
    }

    private static object SessionInfo(UserRecord user, StaffInfo staff, ISettingsService settings, int recoveryCodesRemaining) => new
    {
        user = user.ToDto(), isSuper = staff.IsSuper, permissions = staff.Permissions, recoveryCodesRemaining, idleMinutes = settings.GetInt("Auth.AdminIdleMinutes"),
    };

    private static async Task<IResult> Me(HttpContext ctx, IUserRepository users, IPlatformRepository platform, ITwoFactorRepository twoFactor, ISettingsService settings)
    {
        var id = ctx.UserId();
        var user = await users.GetByIdAsync(id) ?? throw new AppException("UNAUTHENTICATED", 401);
        var staff = await platform.StaffAsync(id) ?? throw new AppException("UNAUTHENTICATED", 401);
        return Results.Ok(SessionInfo(user, staff, settings, await twoFactor.RecoveryCountAsync(id)));
    }

    private static async Task<IResult> Logout(HttpContext ctx, IPlatformRepository platform)
    {
        await platform.WriteAuditAsync(ctx.UserId(), "admin.logout", "user", ctx.UserId().ToString(), "Signed out of the back office", null, Ip(ctx));
        await ctx.SignOutAsync(AdminAuth.Scheme);
        return Results.NoContent();
    }

    /// <summary>Issues a fresh set of recovery codes (the old ones stop working). Requires a current authenticator code.</summary>
    private static async Task<IResult> Regenerate(HttpContext ctx, CodeRequest request, IUserRepository users, ITwoFactorRepository twoFactor, IDataProtectionProvider dp, TimeProvider clock)
    {
        var user = await users.GetByIdAsync(ctx.UserId()) ?? throw new AppException("UNAUTHENTICATED", 401);
        var step = await CheckCodeAsync(user, request.Code, twoFactor, dp, clock, requireConfirmed: true);
        await twoFactor.UseStepAsync(user.Id, step);
        var codes = RecoveryCodes.Generate();
        await twoFactor.ReplaceRecoveryAsync(user.Id, codes.Select(RecoveryCodes.Hash).ToList());
        return Results.Ok(new { recoveryCodes = codes });
    }
}
