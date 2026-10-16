using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Sayserne.Api.Data;
using Sayserne.Api.Platform;

namespace Sayserne.Api.Auth;

/// <summary>Deploy-time authentication settings. Everything an admin may tune (lockout, session length, rate limits, password rules) lives in the database settings instead.</summary>
public sealed class AuthOptions
{
    /// <summary>PBKDF2 cost. Deploy-time on purpose: it must match the hardware, and old hashes record their own cost.</summary>
    public int PasswordIterations { get; set; } = 600_000;
}

public static class AuthSession
{
    public const string RoleAdmin = "admin";
    public const string PasswordClaim = "pwd";

    /// <summary>A short, non-reversible marker of the password a session was opened with. Changing the password (reset) therefore ends every older session.</summary>
    public static string PasswordFingerprint(UserRecord user) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(user.PasswordHash ?? "")))[..16];

    public static Task SignInAsync(HttpContext ctx, UserRecord user)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Name, $"{user.FirstName} {user.LastName}".Trim()),
            new Claim(ClaimTypes.Role, user.Role),
            new Claim(PasswordClaim, PasswordFingerprint(user)),
        ], CookieAuthenticationDefaults.AuthenticationScheme);
        var settings = ctx.RequestServices.GetRequiredService<ISettingsService>();
        var clock = ctx.RequestServices.GetRequiredService<TimeProvider>();
        return ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = false, ExpiresUtc = clock.GetUtcNow().AddHours(settings.GetInt("Auth.SessionHours")) });
    }

    public static long? CurrentUserId(ClaimsPrincipal principal) =>
        long.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}

public static class HttpContextUserExtensions
{
    /// <summary>The signed-in user's id. Only call from endpoints that require authorization.</summary>
    public static long UserId(this HttpContext ctx) =>
        AuthSession.CurrentUserId(ctx.User) ?? throw new AppException("UNAUTHENTICATED", StatusCodes.Status401Unauthorized);
}
