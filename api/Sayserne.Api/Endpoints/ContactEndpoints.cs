using System.Net.Mail;
using Sayserne.Api.Auth;
using Sayserne.Api.Contact;
using Sayserne.Api.Data;
using Sayserne.Api.Email;
using Sayserne.Api.Platform;

namespace Sayserne.Api.Endpoints;

/// <summary>The public contact form, and the staff inbox where messages are read and answered by e-mail.</summary>
public static class ContactEndpoints
{
    public const string Limiter = "contact";

    public sealed record ContactInput(string? Name, string? Email, string? Topic, string? Subject, string? Message, string? Language, string? Website);
    public sealed record ReplyInput(string? Body);
    public sealed record StatusInput(string? Status, string? Note);

    private static AppException Invalid(string field, string message) => new("VALIDATION_FAILED", 400, fieldErrors: new Dictionary<string, string> { [field] = message });

    public static void MapContact(this IEndpointRouteBuilder app)
    {
        // Anyone may write. A hidden field that real people never fill in catches simple bots: those messages are kept but marked spam, and the sender is
        // told "received" like everyone else (so the bot learns nothing). Each address may send only a few per hour.
        app.MapPost("/api/contact", async (HttpContext ctx, ContactInput b, IContactRepository contact, INotifier notifier, IPlatformRepository platform, ISettingsService settings, AccountEmails links) =>
        {
            var name = (b.Name ?? "").Trim(); var email = (b.Email ?? "").Trim(); var subject = (b.Subject ?? "").Trim(); var message = (b.Message ?? "").Trim();
            if (name.Length is 0 or > 100) throw Invalid("name", "Please enter your name.");
            if (!InputValidation.IsValidEmail(email) || email.Length > 255) throw Invalid("email", "Please enter a valid email address.");
            if (subject.Length is 0 or > 150) throw Invalid("subject", "Please enter a subject (up to 150 characters).");
            var max = settings.GetInt("Contact.MaxMessageLength");
            if (message.Length == 0 || message.Length > max) throw Invalid("message", $"Please write your message (up to {max} characters).");
            var languages = (await platform.LookupValuesAsync("language", null, false, ctx.RequestAborted)).Select(l => l.Code).ToHashSet();
            var language = b.Language is { } l && languages.Contains(l) ? l : settings.GetString("Site.DefaultLanguage");
            var spam = !string.IsNullOrWhiteSpace(b.Website);
            var (id, _) = await contact.CreateAsync(AuthSession.CurrentUserId(ctx.User), name, email, language, string.IsNullOrWhiteSpace(b.Topic) ? null : b.Topic.Trim(), subject, message, spam, ctx.RequestAborted);
            if (!spam && settings.GetString("Contact.NotifyAddress") is { Length: > 0 } staff)
                await notifier.NotifyAddressAsync("contact_received", staff, settings.GetString("Site.DefaultLanguage"),
                    new Dictionary<string, string> { ["name"] = name, ["from"] = email, ["subject"] = subject, ["message"] = message, ["link"] = links.Link("/") }, ctx.RequestAborted);
            return Results.Json(new { received = true }, statusCode: StatusCodes.Status202Accepted);
        }).RequireRateLimiting(Limiter);

        var g = app.MapGroup("/api/admin/contact");
        g.MapGet("", async (IContactRepository contact, string? status, string? search, int? page, int? pageSize) =>
            Results.Ok(await contact.ListAsync(Trim(status, 12), Trim(search, 100), page ?? 1, pageSize ?? 25))).RequirePermission("contact.read");
        g.MapGet("/{id:long}", async (long id, IContactRepository contact) => Results.Ok(await contact.GetAsync(id) ?? throw new AppException("CONTACT_NOT_FOUND", 404))).RequirePermission("contact.read");

        // The answer is e-mailed to the address the person typed, in the language they wrote in; it is recorded only once it has been queued.
        g.MapPost("/{id:long}/reply", async (HttpContext ctx, long id, ReplyInput b, IContactRepository contact, INotifier notifier, ISettingsService settings) =>
        {
            var reply = (b.Body ?? "").Trim();
            if (reply.Length == 0) throw new AppException("REPLY_REQUIRED", 400);
            if (settings.GetString("Email.Provider") == "off") throw new AppException("EMAIL_NOT_CONFIGURED", 409);
            var original = await contact.GetAsync(id) ?? throw new AppException("CONTACT_NOT_FOUND", 404);
            var queued = await notifier.NotifyAddressAsync("contact_reply", original.Email, original.Language,
                new Dictionary<string, string> { ["name"] = original.Name, ["subject"] = original.Subject, ["reply"] = reply, ["message"] = original.Body }, ctx.RequestAborted);
            if (!queued) throw new AppException("EMAIL_SEND_FAILED", 502);
            return Results.Ok(await contact.ReplyAsync(ctx.UserId(), id, reply));
        }).RequirePermission("contact.manage");

        g.MapPost("/{id:long}/status", async (HttpContext ctx, long id, StatusInput b, IContactRepository contact) =>
            Results.Ok(await contact.SetStatusAsync(ctx.UserId(), id, (b.Status ?? "").Trim().ToLowerInvariant(), b.Note is { Length: > 1000 } ? throw Invalid("note", "At most 1000 characters.") : b.Note))).RequirePermission("contact.manage");
    }

    private static string? Trim(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length > max ? throw Invalid("filter", $"At most {max} characters.") : s.Trim();
}
