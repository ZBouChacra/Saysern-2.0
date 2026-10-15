using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Sayserne.Api.Data;

namespace Sayserne.Api.Auth;

public sealed class AuthOptions
{
    public int PasswordIterations { get; set; } = 600_000;
    public int MaxFailedAttempts { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
    public int SessionHours { get; set; } = 8;
    public int AuthRequestsPerMinute { get; set; } = 30;
    public int WritesPerMinute { get; set; } = 120;
}

public static class AuthSession
{
    public const string RoleAdmin = "admin";

    public static Task SignInAsync(HttpContext ctx, UserRecord user)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Name, $"{user.FirstName} {user.LastName}".Trim()),
            new Claim(ClaimTypes.Role, user.Role),
        ], CookieAuthenticationDefaults.AuthenticationScheme);
        return ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
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
