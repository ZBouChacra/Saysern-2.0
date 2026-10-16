using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Sayserne.Api.Auth;
using Sayserne.Api.Data;
using Sayserne.Api.Platform;

namespace Sayserne.Api.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/health", async (IUserRepository users) =>
        {
            await users.PingAsync();
            return Results.Ok(new { status = "ok" });
        });

        var g = app.MapGroup("/api/auth").RequireRateLimiting("auth");
        g.MapGet("/providers", (IOptions<GoogleOptions> google) => Results.Ok(new { google = google.Value.Enabled, passwordRegistration = true }));
        g.MapPost("/register", Register);
        g.MapPost("/login", Login);
        g.MapPost("/logout", (Delegate)Logout);   // cast: Logout(HttpContext) would otherwise bind as a raw RequestDelegate and drop its result
        g.MapGet("/me", Me).RequireAuthorization();
    }

    private static async Task<IResult> Register(HttpContext ctx, RegisterRequest request, IUserRepository users, IPasswordHasher hasher, ISettingsService settings)
    {
        var errors = InputValidation.ValidateRegistration(request, settings);
        if (errors.Count > 0) throw new AppException("VALIDATION_FAILED", 400, fieldErrors: errors);

        var user = await users.CreateLocalAsync(
            InputValidation.NormalizeEmail(request.Email), hasher.Hash(request.Password!),
            request.FirstName!.Trim(), request.LastName!.Trim());   // DUPLICATE_EMAIL -> 409 via AppException
        await AuthSession.SignInAsync(ctx, user);
        return Results.Json(new { user = user.ToDto() }, statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> Login(
        HttpContext ctx, LoginRequest request, IUserRepository users, IPasswordHasher hasher,
        ISettingsService settings, TimeProvider clock, DummyPasswordHash dummy)
    {
        var email = InputValidation.NormalizeEmail(request.Email);
        var password = request.Password ?? string.Empty;
        if (email.Length == 0 || password.Length == 0 || password.Length > 1024)
            throw new AppException("INVALID_CREDENTIALS");

        var user = await users.GetByEmailAsync(email);
        if (user is null)
        {
            hasher.Verify(password, dummy.Value);   // keep timing similar for unknown emails
            throw new AppException("INVALID_CREDENTIALS");
        }
        if (user.IsLocked || user.IsTemporarilyLocked(clock.GetUtcNow().UtcDateTime))
            throw new AppException("ACCOUNT_LOCKED");

        if (user.PasswordHash is null || !hasher.Verify(password, user.PasswordHash))
        {
            await users.RecordLoginFailureAsync(user.Id, settings.GetInt("Auth.MaxFailedAttempts"), settings.GetInt("Auth.LockoutMinutes"));
            throw new AppException("INVALID_CREDENTIALS");
        }

        await users.RecordLoginSuccessAsync(user.Id);
        await AuthSession.SignInAsync(ctx, user);
        return Results.Ok(new { user = user.ToDto() });
    }

    private static async Task<IResult> Logout(HttpContext ctx)
    {
        await ctx.SignOutAsync();
        return Results.NoContent();
    }

    private static async Task<IResult> Me(HttpContext ctx, IUserRepository users)
    {
        var id = AuthSession.CurrentUserId(ctx.User);
        var user = id is null ? null : await users.GetByIdAsync(id.Value);
        return user is null ? Results.Unauthorized() : Results.Ok(new { user = user.ToDto() });
    }
}

/// <summary>A throw-away hash verified for unknown emails so login timing doesn't reveal which emails exist.</summary>
public sealed class DummyPasswordHash(IPasswordHasher hasher)
{
    public string Value { get; } = hasher.Hash("not-a-real-password-" + Guid.NewGuid());
}
