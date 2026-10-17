using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Sayserne.Api.Data;
using Sayserne.Api.Jobs;
using Sayserne.Api.Platform;

namespace Sayserne.Api.Email;

// ------------------------------------------------------------------ sending

public sealed record EmailMessage(string To, string Subject, string Text, string Html);
public sealed class EmailException(string message, Exception? inner = null) : Exception(message, inner);

public interface IEmailSender { Task SendAsync(EmailMessage message, CancellationToken ct = default); }

/// <summary>Sends through the mail server configured in the back office (Settings -> Email). Nothing about the server is compiled in.</summary>
public sealed class SmtpEmailSender(ISettingsService settings) : IEmailSender
{
    /// <summary>Header values must be a single line: a CR or LF would let a name or subject inject extra headers.</summary>
    private static string OneLine(string s) => Regex.Replace(s, @"[\r\n]+", " ").Trim();

    public async Task SendAsync(EmailMessage m, CancellationToken ct = default)
    {
        var host = settings.GetString("Email.SmtpHost");
        if (host.Length == 0) throw new EmailException("The mail server host is not set (Settings -> Email).");
        MailAddress from, to;
        try
        {
            from = new MailAddress(settings.GetString("Email.FromAddress"), OneLine(settings.GetString("Email.FromName")), Encoding.UTF8);
            to = new MailAddress(m.To);
        }
        catch (FormatException ex) { throw new EmailException("An e-mail address is not valid.", ex); }

        using var client = new SmtpClient(host, settings.GetInt("Email.SmtpPort"))
        {
            EnableSsl = settings.GetString("Email.SmtpSecurity") == "starttls",
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Timeout = 30_000,
        };
        var user = settings.GetString("Email.SmtpUsername");
        if (user.Length > 0) client.Credentials = new NetworkCredential(user, settings.GetSecret("Email.SmtpPassword"));

        using var mail = new MailMessage { From = from, Subject = OneLine(m.Subject), SubjectEncoding = Encoding.UTF8, BodyEncoding = Encoding.UTF8, Body = m.Text, IsBodyHtml = false };
        mail.To.Add(to);
        mail.Headers.Add("Auto-Submitted", "auto-generated");
        mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(m.Html, Encoding.UTF8, "text/html"));
        try { await client.SendMailAsync(mail, ct); }
        catch (SmtpException ex) { throw new EmailException($"The mail server refused the message: {ex.Message}", ex); }
        catch (IOException ex) { throw new EmailException("The connection to the mail server was lost.", ex); }
    }
}

public interface ISecretProtector
{
    string Protect(string plain);
    string Unprotect(string protectedValue);
}

/// <summary>Encrypts secret settings (such as the SMTP password) with ASP.NET Data Protection before they are stored.</summary>
public sealed class SecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("Sayserne.Settings.v1");
    public string Protect(string plain) => _protector.Protect(plain);
    public string Unprotect(string protectedValue) => _protector.Unprotect(protectedValue);
}

// ------------------------------------------------------------------ templates

public sealed record RenderedEmail(string Subject, string Text, string Html);

public interface IEmailRenderer
{
    Task<RenderedEmail> RenderAsync(string eventCode, string language, IReadOnlyDictionary<string, string> data, CancellationToken ct = default);
}

/// <summary>
/// Builds a message from the texts kept in the database (Texts page: keys email.&lt;event&gt;.subject / .body, plus email.footer and email.signoff) in the
/// recipient's language, falling back to the default language. {{placeholders}} are replaced; in the HTML version every value is HTML-encoded.
/// </summary>
public sealed partial class EmailRenderer(IPlatformRepository platform, ISettingsService settings) : IEmailRenderer
{
    [GeneratedRegex(@"\{\{(\w+)\}\}")] private static partial Regex Placeholder();
    [GeneratedRegex(@"https?://[^\s<>""]+")] private static partial Regex Url();

    private static string Fill(string template, IReadOnlyDictionary<string, string> values, Func<string, string> encode) =>
        Placeholder().Replace(template, m => values.TryGetValue(m.Groups[1].Value, out var v) ? encode(v) : "");

    public async Task<RenderedEmail> RenderAsync(string eventCode, string language, IReadOnlyDictionary<string, string> data, CancellationToken ct = default)
    {
        var texts = await platform.TranslationsAsync(language, ct);
        string Text(string key) => texts.TryGetValue(key, out var v) ? v : throw new InvalidOperationException($"The text '{key}' is missing.");
        var baseUrl = settings.GetString("Site.PublicUrl").TrimEnd('/');
        var values = new Dictionary<string, string>(data) { ["site"] = settings.GetString("Site.Name"), ["settings_link"] = baseUrl + "/settings" };

        var subject = Regex.Replace(Fill(Text($"email.{eventCode}.subject"), values, s => s), @"[\r\n]+", " ").Trim();
        var body = Fill(Text($"email.{eventCode}.body"), values, s => s).Trim();
        var signoff = Fill(Text("email.signoff"), values, s => s);
        var footer = Fill(Text("email.footer"), values, s => s);

        var plain = $"{body}\n\n{signoff}\n\n-- \n{footer}\n";
        var rtl = (await platform.LookupValuesAsync("language", null, false, ct)).FirstOrDefault(l => l.Code == language)?.Metadata is { } meta && meta.TryGetProperty("dir", out var dir) && dir.GetString() == "rtl";

        string Html(string text) => Url().Replace(WebUtility.HtmlEncode(text), m => $"<a href=\"{m.Value}\">{m.Value}</a>");
        var paragraphs = string.Concat(body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(p => $"<p style=\"margin:0 0 14px\">{Html(p).Replace("\n", "<br>")}</p>"));
        var html = $"""
            <!doctype html><html lang="{WebUtility.HtmlEncode(language)}" dir="{(rtl ? "rtl" : "ltr")}"><body style="margin:0;background:#f4f6f5;font-family:Segoe UI,Arial,sans-serif;color:#1c2b27">
            <div style="max-width:560px;margin:0 auto;padding:24px"><div style="background:#fff;border-radius:12px;padding:24px;line-height:1.55">
            <div style="font:700 20px Georgia,serif;color:#1f6f5c;margin-bottom:16px">{WebUtility.HtmlEncode(values["site"])}</div>{paragraphs}
            <p style="margin:18px 0 0;color:#5b6b66">{WebUtility.HtmlEncode(signoff)}</p></div>
            <p style="font-size:12px;color:#75857f;margin:14px 6px">{Html(footer)}</p></div></body></html>
            """;
        return new RenderedEmail(subject, plain, html);
    }
}

// ------------------------------------------------------------------ data

public sealed record NotifyTarget(long UserId, string Email, string FirstName, string Language, bool Enabled, string? TimeZone = null);
public sealed record OutboxRow(long Id, long? UserId, string To, string EventCode, string Language, string Subject, string? BodyText, string? BodyHtml, string Status, int Attempts, string? LastError, DateTime CreatedAt, DateTime? SentAt);
public sealed record NotificationPreference(string Event, string Channel, bool Enabled);

public interface IEmailRepository
{
    Task CreateTokenAsync(long userId, string purpose, string tokenHash, int ttlMinutes, CancellationToken ct = default);
    Task<long> VerifyEmailAsync(string tokenHash, CancellationToken ct = default);
    Task<long> ReenableAccountAsync(string tokenHash, CancellationToken ct = default);
    Task<long> ResetPasswordAsync(string tokenHash, string passwordHash, CancellationToken ct = default);
    Task<NotifyTarget?> PrepareAsync(long userId, string eventCode, string channel, CancellationToken ct = default);
    Task<long> CreateOutboxAsync(long? userId, string to, string eventCode, string language, RenderedEmail mail, CancellationToken ct = default);
    Task<OutboxRow?> GetOutboxAsync(long id, CancellationToken ct = default);
    Task MarkOutboxAsync(long id, string status, string? error, CancellationToken ct = default);
    Task<PagedResult<OutboxRow>> OutboxAsync(string? status, int page, int pageSize, CancellationToken ct = default);
    Task<IReadOnlyList<NotificationPreference>> PreferencesAsync(long userId, CancellationToken ct = default);
    Task SetPreferenceAsync(long userId, string eventCode, string channel, bool enabled, CancellationToken ct = default);
    Task<int> RoomUnreadAsync(long roomId, long userId, CancellationToken ct = default);
}

public sealed class EmailRepository(IStoredProcedureExecutor db) : IEmailRepository
{
    private static OutboxRow Outbox(System.Data.Common.DbDataReader r, bool withBody) => new(r.Long("id"), r.LongOrNull("user_id"), r.Str("to_address"), r.Str("event_code"), r.Str("language"), r.Str("subject"),
        withBody ? r.StrOrNull("body_text") : null, withBody ? r.StrOrNull("body_html") : null, r.Str("status"), r.Int("attempts"), r.StrOrNull("last_error"), r.Utc("created_at"), r.UtcOrNull("sent_at"));

    public Task CreateTokenAsync(long userId, string purpose, string tokenHash, int ttlMinutes, CancellationToken ct = default) =>
        db.ExecuteAsync("sp_tokens_create", new ProcParams().Add("p_user_id", userId).Add("p_purpose", purpose).Add("p_token_hash", tokenHash).Add("p_ttl_minutes", ttlMinutes), ct);

    public async Task<long> VerifyEmailAsync(string tokenHash, CancellationToken ct = default) =>
        (await db.QueryAsync("sp_email_verify", new ProcParams().Add("p_token_hash", tokenHash), r => r.Long("user_id"), ct))[0];

    public async Task<long> ReenableAccountAsync(string tokenHash, CancellationToken ct = default) =>
        (await db.QueryAsync("sp_account_reenable", new ProcParams().Add("p_token_hash", tokenHash), r => r.Long("user_id"), ct))[0];

    public async Task<long> ResetPasswordAsync(string tokenHash, string passwordHash, CancellationToken ct = default) =>
        (await db.QueryAsync("sp_password_reset", new ProcParams().Add("p_token_hash", tokenHash).Add("p_password_hash", passwordHash), r => r.Long("user_id"), ct))[0];

    public Task<NotifyTarget?> PrepareAsync(long userId, string eventCode, string channel, CancellationToken ct = default) =>
        db.QuerySingleOrDefaultAsync("sp_notify_prepare", new ProcParams().Add("p_user_id", userId).Add("p_event", eventCode).Add("p_channel", channel),
            r => new NotifyTarget(r.Long("id"), r.Str("email"), r.Str("first_name"), r.Str("preferred_language"), r.Bool("enabled"), r.StrOrNull("time_zone")), ct);

    public async Task<long> CreateOutboxAsync(long? userId, string to, string eventCode, string language, RenderedEmail mail, CancellationToken ct = default) =>
        (await db.QueryAsync("sp_email_outbox_create", new ProcParams().Add("p_user_id", userId).Add("p_to", to).Add("p_event", eventCode).Add("p_language", language)
            .Add("p_subject", mail.Subject).Add("p_text", mail.Text).Add("p_html", mail.Html), r => r.Long("id"), ct))[0];

    public Task<OutboxRow?> GetOutboxAsync(long id, CancellationToken ct = default) =>
        db.QuerySingleOrDefaultAsync("sp_email_outbox_get", new ProcParams().Add("p_id", id), r => Outbox(r, true), ct);

    public Task MarkOutboxAsync(long id, string status, string? error, CancellationToken ct = default) =>
        db.ExecuteAsync("sp_email_outbox_mark", new ProcParams().Add("p_id", id).Add("p_status", status).Add("p_error", error), ct);

    public async Task<PagedResult<OutboxRow>> OutboxAsync(string? status, int page, int pageSize, CancellationToken ct = default)
    {
        var (rows, totals) = await db.QueryTwoAsync("sp_email_outbox_list", new ProcParams().Add("p_status", status).Add("p_page", page).Add("p_page_size", pageSize), r => Outbox(r, false), r => r.Long("total"), ct);
        return new PagedResult<OutboxRow>(rows, totals.Count > 0 ? totals[0] : 0, Math.Max(page, 1), Math.Clamp(pageSize, 1, 200));
    }

    public Task<IReadOnlyList<NotificationPreference>> PreferencesAsync(long userId, CancellationToken ct = default) =>
        db.QueryAsync("sp_notification_prefs_list", new ProcParams().Add("p_user_id", userId), r => new NotificationPreference(r.Str("event_code"), r.Str("channel"), r.Bool("enabled")), ct);

    public Task SetPreferenceAsync(long userId, string eventCode, string channel, bool enabled, CancellationToken ct = default) =>
        db.ExecuteAsync("sp_notification_prefs_set", new ProcParams().Add("p_user_id", userId).Add("p_event", eventCode).Add("p_channel", channel).Add("p_enabled", enabled), ct);

    public async Task<int> RoomUnreadAsync(long roomId, long userId, CancellationToken ct = default) =>
        (await db.QueryAsync("sp_chat_room_unread", new ProcParams().Add("p_room_id", roomId).Add("p_user_id", userId), r => r.Int("unread"), ct))[0];
}

// ------------------------------------------------------------------ notifying

public interface INotifier
{
    /// <summary>Queues an e-mail about an event if the user wants it (and e-mail is switched on). Never throws: a notification must not break the action that caused it.</summary>
    Task NotifyAsync(string eventCode, long userId, IReadOnlyDictionary<string, string> data, CancellationToken ct = default);

    /// <summary>Queues an e-mail to a plain address (not an account): a reply to a contact message, or a heads-up to staff. Returns false if e-mail is off or it could not be queued.</summary>
    Task<bool> NotifyAddressAsync(string eventCode, string toAddress, string language, IReadOnlyDictionary<string, string> data, CancellationToken ct = default);
}

public sealed class Notifier(IEmailRepository repo, IEmailRenderer renderer, IJobQueue jobs, ISettingsService settings, ILogger<Notifier> log) : INotifier
{
    public async Task NotifyAsync(string eventCode, long userId, IReadOnlyDictionary<string, string> data, CancellationToken ct = default)
    {
        if (settings.GetString("Email.Provider") == "off") return;
        try
        {
            var target = await repo.PrepareAsync(userId, eventCode, "email", ct);
            if (target is not { Enabled: true }) return;
            var values = EmailTimes.Localise(data, target.TimeZone, settings.GetString("Site.DefaultTimeZone"));   // each "x_utc" becomes "x" in THIS person's time zone
            values["name"] = target.FirstName;
            var mail = await renderer.RenderAsync(eventCode, target.Language, values, ct);
            var id = await repo.CreateOutboxAsync(userId, target.Email, eventCode, target.Language, mail, ct);
            await jobs.EnqueueAsync(EmailSendJobHandler.JobType, new { outboxId = id }, ct: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not queue the {Event} e-mail for user {UserId}.", eventCode, userId);
        }
    }

    public async Task<bool> NotifyAddressAsync(string eventCode, string toAddress, string language, IReadOnlyDictionary<string, string> data, CancellationToken ct = default)
    {
        if (settings.GetString("Email.Provider") == "off") return false;
        try
        {
            var mail = await renderer.RenderAsync(eventCode, language, data, ct);
            var id = await repo.CreateOutboxAsync(null, toAddress, eventCode, language, mail, ct);
            await jobs.EnqueueAsync(EmailSendJobHandler.JobType, new { outboxId = id }, ct: ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not queue the {Event} e-mail to {Address}.", eventCode, toAddress);
            return false;
        }
    }
}

/// <summary>Sends one queued e-mail. A failure keeps the message and is retried with growing delays; the last failure marks it failed.</summary>
public sealed class EmailSendJobHandler(IEmailRepository repo, IEmailSender sender, ISettingsService settings) : IJobHandler
{
    public const string JobType = "email.send";
    public string Type => JobType;

    public async Task HandleAsync(JobContext job, JsonElement payload, CancellationToken ct)
    {
        var id = payload.GetProperty("outboxId").GetInt64();
        var row = await repo.GetOutboxAsync(id, ct);
        // "failed" messages keep their text on purpose, so an admin retry of the job can send them after the mail server problem is fixed
        if (row is null || row.Status is not ("queued" or "failed") || row.BodyText is null || row.BodyHtml is null) return;
        if (settings.GetString("Email.Provider") != "smtp") { await repo.MarkOutboxAsync(id, "skipped", null, CancellationToken.None); return; }
        try
        {
            await sender.SendAsync(new EmailMessage(row.To, row.Subject, row.BodyText, row.BodyHtml), ct);
            await repo.MarkOutboxAsync(id, "sent", null, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await repo.MarkOutboxAsync(id, job.IsLastAttempt ? "failed" : "queued", ex.Message, CancellationToken.None);
            throw;
        }
    }
}

/// <summary>
/// "New message" e-mails are deliberately lazy: the e-mail is only sent if, after a short wait (Email.MessageDelayMinutes), the recipient still has
/// unread messages in that conversation. One wait window per conversation, so a chatty exchange sends one e-mail, not twenty.
/// </summary>
public sealed class MessageNotificationJobHandler(IEmailRepository repo, INotifier notifier) : IJobHandler
{
    public const string JobType = "email.new_message";
    public string Type => JobType;

    public async Task HandleAsync(JobContext job, JsonElement payload, CancellationToken ct)
    {
        var room = payload.GetProperty("roomId").GetInt64();
        var recipient = payload.GetProperty("recipientId").GetInt64();
        if (await repo.RoomUnreadAsync(room, recipient, ct) == 0) return;   // they read it already
        await notifier.NotifyAsync("new_message", recipient, new Dictionary<string, string> { ["sender"] = payload.GetProperty("sender").GetString() ?? "", ["link"] = payload.GetProperty("link").GetString() ?? "" }, ct);
    }
}

/// <summary>Verification and password-reset e-mails: a random single-use token, of which only the hash is stored.</summary>
public sealed class AccountEmails(IEmailRepository repo, INotifier notifier, ISettingsService settings)
{
    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
    public static bool LooksLikeToken(string? token) => token is { Length: >= 40 and <= 64 } && Regex.IsMatch(token, "^[A-Za-z0-9_-]+$");

    public string Link(string path) => settings.GetString("Site.PublicUrl").TrimEnd('/') + path;
    private bool Off => settings.GetString("Email.Provider") == "off";

    /// <exception cref="AppException">TOO_MANY_EMAILS when the per-hour limit is reached.</exception>
    public async Task SendVerificationAsync(UserRecord user, CancellationToken ct = default)
    {
        if (Off || user.EmailVerified) return;
        var hours = settings.GetInt("Email.VerifyTokenHours");
        var token = NewToken();
        await repo.CreateTokenAsync(user.Id, "verify_email", Hash(token), hours * 60, ct);
        await notifier.NotifyAsync("verify_email", user.Id, new Dictionary<string, string> { ["link"] = Link("/verify-email?token=" + token), ["hours"] = hours.ToString() }, ct);
    }

    /// <summary>The link that brings a disabled account back (the person has already proved the password, or signed in with Google).</summary>
    public async Task SendReenableAsync(UserRecord user, CancellationToken ct = default)
    {
        if (Off) return;
        var hours = settings.GetInt("Email.VerifyTokenHours");
        var token = NewToken();
        await repo.CreateTokenAsync(user.Id, "reenable_account", Hash(token), hours * 60, ct);
        await notifier.NotifyAsync("reenable_account", user.Id, new Dictionary<string, string> { ["link"] = Link("/reenable?token=" + token), ["hours"] = hours.ToString() }, ct);
    }

    /// <summary>"Your password was changed", with a link to reset it if that was not you.</summary>
    public Task SendPasswordChangedAsync(UserRecord user, CancellationToken ct = default) =>
        notifier.NotifyAsync("password_changed", user.Id, new Dictionary<string, string> { ["link"] = Link("/forgot-password") }, ct);

    public async Task SendPasswordResetAsync(UserRecord user, CancellationToken ct = default)
    {
        if (Off) return;
        var minutes = settings.GetInt("Email.ResetTokenMinutes");
        var token = NewToken();
        await repo.CreateTokenAsync(user.Id, "reset_password", Hash(token), minutes, ct);
        await notifier.NotifyAsync("password_reset", user.Id, new Dictionary<string, string> { ["link"] = Link("/reset-password?token=" + token), ["minutes"] = minutes.ToString() }, ct);
    }
}
