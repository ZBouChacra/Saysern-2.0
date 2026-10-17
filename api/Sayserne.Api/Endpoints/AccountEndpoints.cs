using Microsoft.AspNetCore.Authentication;
using Sayserne.Api.Auth;
using Sayserne.Api.Data;
using Sayserne.Api.Email;
using Sayserne.Api.Platform;

namespace Sayserne.Api.Endpoints;

/// <summary>A person's own account: language and time zone, password, disabling the account, and getting it back by e-mail confirmation.</summary>
public static class AccountEndpoints
{
    public sealed record PreferencesInput(string? Language, string? TimeZone);
    public sealed record PasswordInput(string? CurrentPassword, string? NewPassword);
    public sealed record DisableInput(string? Password, string? ConfirmEmail);
    public sealed record ReenableRequest(string? Email, string? Password);
    public sealed record ReenableConfirm(string? Token);

    private static AppException Invalid(string field, string message) => new("VALIDATION_FAILED", 400, fieldErrors: new Dictionary<string, string> { [field] = message });

    public static void MapAccount(this IEndpointRouteBuilder app)
    {
        var me = app.MapGroup("/api/me").RequireAuthorization();

        me.MapPut("/preferences", async (HttpContext ctx, PreferencesInput b, IUserRepository users) =>
        {
            var language = (b.Language ?? "").Trim();
            if (language.Length is 0 or > 10) throw new AppException("INVALID_LANGUAGE", 400);
            var user = await users.UpdatePreferencesAsync(ctx.UserId(), language, string.IsNullOrWhiteSpace(b.TimeZone) ? null : b.TimeZone.Trim());
            return Results.Ok(new { user = user.ToDto() });
        });

        // Changing the password ends every other session (their cookies carry a fingerprint of the old password); this one is renewed so the person stays signed in.
        me.MapPost("/password", async (HttpContext ctx, PasswordInput b, IUserRepository users, IPasswordHasher hasher, ISettingsService settings, AccountEmails mails, ILogger<AccountEmails> log) =>
        {
            var user = await users.GetByIdAsync(ctx.UserId()) ?? throw new AppException("UNAUTHENTICATED", 401);
            if (user.PasswordHash is null) throw new AppException("PASSWORD_NOT_SET", 400);        // signed up with Google: use "forgot password" once to choose one
            if (string.IsNullOrEmpty(b.CurrentPassword) || b.CurrentPassword.Length > 1024 || !hasher.Verify(b.CurrentPassword, user.PasswordHash))
            {
                await users.RecordLoginFailureAsync(user.Id, settings.GetInt("Auth.MaxFailedAttempts"), settings.GetInt("Auth.LockoutMinutes"));   // guessing the current password counts like guessing at sign-in
                throw new AppException("WRONG_PASSWORD", 400, fieldErrors: new Dictionary<string, string> { ["currentPassword"] = "That password is not correct." });
            }
            var problem = InputValidation.PasswordProblem(b.NewPassword ?? "", user.Email, settings);
            if (problem is not null) throw Invalid("newPassword", problem);
            if (b.NewPassword == b.CurrentPassword) throw Invalid("newPassword", "Choose a password you have not used just now.");
            var updated = await users.ChangePasswordAsync(user.Id, hasher.Hash(b.NewPassword!));
            await AuthSession.SignInAsync(ctx, updated);
            try { await mails.SendPasswordChangedAsync(updated); } catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Could not send the password-changed e-mail to user {UserId}.", updated.Id); }
            return Results.Ok(new { user = updated.ToDto() });
        }).RequireRateLimiting("auth");

        // Disabling needs the password (or, for a Google account, the person's e-mail address typed again). The account is hidden and signed out, never deleted.
        me.MapPost("/disable", async (HttpContext ctx, DisableInput b, IUserRepository users, IPasswordHasher hasher, ISettingsService settings) =>
        {
            var user = await users.GetByIdAsync(ctx.UserId()) ?? throw new AppException("UNAUTHENTICATED", 401);
            if (user.PasswordHash is not null)
            {
                if (string.IsNullOrEmpty(b.Password) || b.Password.Length > 1024 || !hasher.Verify(b.Password, user.PasswordHash))
                {
                    await users.RecordLoginFailureAsync(user.Id, settings.GetInt("Auth.MaxFailedAttempts"), settings.GetInt("Auth.LockoutMinutes"));
                    throw new AppException("WRONG_PASSWORD", 400, fieldErrors: new Dictionary<string, string> { ["password"] = "That password is not correct." });
                }
            }
            else if (!string.Equals(InputValidation.NormalizeEmail(b.ConfirmEmail), user.Email, StringComparison.OrdinalIgnoreCase)) throw Invalid("confirmEmail", "Type your email address to confirm.");
            await users.SetDisabledAsync(user.Id, true);
            await ctx.SignOutAsync();
            return Results.NoContent();
        }).RequireRateLimiting("auth");

        var auth = app.MapGroup("/api/auth/reenable");
        // Always the same answer. A link is sent only if the account is disabled, not locked by staff, and the password is right (a wrong one counts as a failed sign-in).
        auth.MapPost("/request", async (ReenableRequest b, IUserRepository users, IPasswordHasher hasher, ISettingsService settings, AccountEmails mails, TimeProvider clock, DummyPasswordHash dummy) =>
        {
            var email = InputValidation.NormalizeEmail(b.Email);
            var password = b.Password ?? "";
            var user = InputValidation.IsValidEmail(email) ? await users.GetByEmailAsync(email) : null;
            if (user is null || user.PasswordHash is null) { hasher.Verify(password.Length is > 0 and <= 1024 ? password : "x", dummy.Value); return Accepted(); }   // (similar timing)
            if (user.IsLocked || user.IsTemporarilyLocked(clock.GetUtcNow().UtcDateTime)) return Accepted();
            if (password.Length is 0 or > 1024 || !hasher.Verify(password, user.PasswordHash))
            {
                await users.RecordLoginFailureAsync(user.Id, settings.GetInt("Auth.MaxFailedAttempts"), settings.GetInt("Auth.LockoutMinutes"));
                return Accepted();
            }
            if (user.IsDisabled)
                try { await mails.SendReenableAsync(user); } catch (AppException ex) when (ex.Code == "TOO_MANY_EMAILS") { /* silently limited */ }
            return Accepted();
        }).RequireRateLimiting("auth");

        auth.MapPost("/confirm", async (ReenableConfirm b, IEmailRepository emails) =>
        {
            if (!AccountEmails.LooksLikeToken(b.Token)) throw new AppException("INVALID_TOKEN", 400);
            await emails.ReenableAccountAsync(AccountEmails.Hash(b.Token!));
            return Results.Ok(new { reenabled = true });
        }).RequireRateLimiting("auth");
    }

    private static IResult Accepted() => Results.Json(new { accepted = true }, statusCode: StatusCodes.Status202Accepted);
}
