using System.Net.Mail;
using Sayserne.Api.Auth;
using Sayserne.Api.Data;
using Sayserne.Api.Email;
using Sayserne.Api.Platform;

namespace Sayserne.Api.Endpoints;

/// <summary>E-mail address verification, password reset, and each user's notification choices.</summary>
public static class AccountEmailEndpoints
{
    public sealed record TokenRequest(string? Token);
    public sealed record ForgotRequest(string? Email);
    public sealed record ResetRequest(string? Token, string? Password);
    public sealed record PreferenceRequest(string? Event, string? Channel, bool? Enabled);
    public sealed record TestEmailRequest(string? To);

    public static void MapAccountEmail(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth");   // each route below that takes a token or an address is rate limited individually

        // The link in the e-mail lands on a web page that posts the token here (never GET: a mail scanner opening the link must not verify anything).
        auth.MapPost("/verify-email", async (TokenRequest body, IEmailRepository emails) =>
        {
            if (!AccountEmails.LooksLikeToken(body.Token)) throw new AppException("INVALID_TOKEN", 400);
            await emails.VerifyEmailAsync(AccountEmails.Hash(body.Token!));
            return Results.Ok(new { verified = true });
        }).RequireRateLimiting("auth");

        auth.MapPost("/resend-verification", async (HttpContext ctx, IUserRepository users, AccountEmails mails) =>
        {
            var user = await users.GetByIdAsync(ctx.UserId()) ?? throw new AppException("UNAUTHENTICATED", 401);
            if (user.EmailVerified) return Results.Ok(new { alreadyVerified = true });
            await mails.SendVerificationAsync(user);   // TOO_MANY_EMAILS -> 429 after the hourly limit
            return Results.Json(new { sent = true }, statusCode: StatusCodes.Status202Accepted);
        }).RequireAuthorization().RequireRateLimiting("auth");

        // Always the same answer, whether or not the address has an account: nobody can use this to find out who is registered.
        auth.MapPost("/forgot-password", async (ForgotRequest body, IUserRepository users, AccountEmails mails) =>
        {
            var email = InputValidation.NormalizeEmail(body.Email);
            if (InputValidation.IsValidEmail(email))
            {
                var user = await users.GetByEmailAsync(email);
                if (user is { IsLocked: false })
                {
                    try { await mails.SendPasswordResetAsync(user); }
                    catch (AppException ex) when (ex.Code == "TOO_MANY_EMAILS") { /* silently limited */ }
                }
            }
            return Results.Json(new { accepted = true }, statusCode: StatusCodes.Status202Accepted);
        }).RequireRateLimiting("auth");

        auth.MapPost("/reset-password", async (ResetRequest body, IEmailRepository emails, IPasswordHasher hasher, ISettingsService settings) =>
        {
            if (!AccountEmails.LooksLikeToken(body.Token)) throw new AppException("INVALID_TOKEN", 400);
            var problem = InputValidation.PasswordProblem(body.Password ?? "", null, settings);
            if (problem is not null) throw new AppException("VALIDATION_FAILED", 400, fieldErrors: new Dictionary<string, string> { ["password"] = problem });
            await emails.ResetPasswordAsync(AccountEmails.Hash(body.Token!), hasher.Hash(body.Password!));
            return Results.Ok(new { reset = true });
        }).RequireRateLimiting("auth");

        var me = app.MapGroup("/api/me/notifications").RequireAuthorization();
        me.MapGet("", async (HttpContext ctx, IEmailRepository emails) => Results.Ok(await emails.PreferencesAsync(ctx.UserId())));
        me.MapPut("", async (HttpContext ctx, PreferenceRequest body, IEmailRepository emails) =>
        {
            if (string.IsNullOrWhiteSpace(body.Event) || string.IsNullOrWhiteSpace(body.Channel) || body.Enabled is null)
                throw new AppException("VALIDATION_FAILED", 400, fieldErrors: new Dictionary<string, string> { ["event"] = "Choose an event, a channel and on/off." });
            await emails.SetPreferenceAsync(ctx.UserId(), body.Event, body.Channel, body.Enabled.Value);
            return Results.Ok(await emails.PreferencesAsync(ctx.UserId()));
        });

        // ---- back office
        var admin = app.MapGroup("/api/admin/email");
        admin.MapGet("/outbox", async (IEmailRepository emails, string? status, int? page, int? pageSize) =>
            Results.Ok(await emails.OutboxAsync(status, page ?? 1, pageSize ?? 25))).RequirePermission("email.read");

        // Sends a real message straight away (not queued) so a wrong password or host is reported at once.
        admin.MapPost("/test", async (HttpContext ctx, TestEmailRequest body, IEmailRenderer renderer, IEmailSender sender, IPlatformRepository platform, ISettingsService settings) =>
        {
            if (settings.GetString("Email.Provider") != "smtp") throw new AppException("EMAIL_NOT_CONFIGURED", 409);
            MailAddress to;
            try { to = new MailAddress((body.To ?? "").Trim()); } catch (FormatException) { throw new AppException("VALIDATION_FAILED", 400, fieldErrors: new Dictionary<string, string> { ["to"] = "Enter a valid e-mail address." }); }
            var mail = await renderer.RenderAsync("test", settings.GetString("Site.DefaultLanguage"), new Dictionary<string, string>());
            try { await sender.SendAsync(new EmailMessage(to.Address, mail.Subject, mail.Text, mail.Html), ctx.RequestAborted); }
            catch (EmailException ex)
            {
                await platform.WriteAuditAsync(ctx.UserId(), "email.test.failed", "email", null, $"Test e-mail to {to.Address} failed", new { error = ex.Message }, ctx.Connection.RemoteIpAddress?.ToString());
                return Results.Json(new { error = "EMAIL_SEND_FAILED", detail = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
            await platform.WriteAuditAsync(ctx.UserId(), "email.test", "email", null, $"Sent a test e-mail to {to.Address}", null, ctx.Connection.RemoteIpAddress?.ToString());
            return Results.Ok(new { sent = true });
        }).RequirePermission("settings.manage");
    }
}
