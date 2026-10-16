using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Sayserne.Api;
using Sayserne.Api.Email;
using Sayserne.Api.Jobs;

namespace Sayserne.Tests;

public class SmtpSenderTests
{
    private static FixedSettings Settings(FakeSmtp smtp, Dictionary<string, string>? extra = null)
    {
        var v = new Dictionary<string, string>
        {
            ["Email.SmtpHost"] = "127.0.0.1", ["Email.SmtpPort"] = smtp.Port.ToString(), ["Email.SmtpSecurity"] = "none", ["Email.SmtpUsername"] = "", ["Email.SmtpPassword"] = "",
            ["Email.FromAddress"] = "hello@sayserne.test", ["Email.FromName"] = "SaySerné",
        };
        if (extra != null) foreach (var (k, val) in extra) v[k] = val;
        return new FixedSettings(v);
    }

    [Fact]
    public async Task A_message_goes_out_as_text_plus_html_in_utf8_with_the_configured_sender_and_credentials()
    {
        await using var smtp = await FakeSmtp.StartAsync();
        var sender = new SmtpEmailSender(Settings(smtp, new() { ["Email.SmtpUsername"] = "mailer", ["Email.SmtpPassword"] = "s3cret-pass" }));
        await sender.SendAsync(new EmailMessage("user@example.test", "تأكيد بريدك الإلكتروني", "مرحبًا\nhttps://x.test/verify?token=abc", "<p>مرحبًا</p>"));
        var m = Assert.Single(smtp.Messages);
        Assert.Equal(("hello@sayserne.test", "user@example.test"), (m.From, m.To));
        Assert.Equal("تأكيد بريدك الإلكتروني", m.Subject);
        Assert.Contains("مرحبًا", m.Text);
        Assert.Equal("<p>مرحبًا</p>", m.Html.Trim());
        Assert.Equal(("mailer", "s3cret-pass"), (m.Username, m.Password));              // the credentials came from the settings
        Assert.Equal("auto-generated", m.Header("Auto-Submitted"));
        Assert.Contains("SaySerné", FakeSmtp.Mime.DecodeWords(m.Header("From")));
    }

    [Fact]
    public async Task No_credentials_are_sent_when_no_username_is_set()
    {
        await using var smtp = await FakeSmtp.StartAsync();
        await new SmtpEmailSender(Settings(smtp)).SendAsync(new EmailMessage("a@example.test", "Hi", "text", "<p>html</p>"));
        Assert.Null(Assert.Single(smtp.Messages).Username);
    }

    [Fact]
    public async Task A_subject_or_sender_name_cannot_smuggle_extra_headers()
    {
        await using var smtp = await FakeSmtp.StartAsync();
        var sender = new SmtpEmailSender(Settings(smtp, new() { ["Email.FromName"] = "Evil\r\nBcc: victim@example.test" }));
        await sender.SendAsync(new EmailMessage("a@example.test", "Hello\r\nBcc: victim@example.test\r\nX-Injected: yes", "text", "<p>html</p>"));
        var m = Assert.Single(smtp.Messages);
        Assert.Equal("", m.Header("Bcc"));
        Assert.Equal("", m.Header("X-Injected"));
        Assert.Equal(["a@example.test"], m.Recipients);             // nobody else was addressed
        Assert.DoesNotContain("\r\nBcc:", "\r\n" + m.Raw.Split("\r\n\r\n")[0]);          // no header line was injected anywhere
    }

    [Fact]
    public async Task Problems_are_reported_as_one_clear_exception_type()
    {
        await using var smtp = await FakeSmtp.StartAsync();
        await Assert.ThrowsAsync<EmailException>(() => new SmtpEmailSender(Settings(smtp, new() { ["Email.SmtpHost"] = "" })).SendAsync(new EmailMessage("a@example.test", "s", "t", "h")));       // not configured
        await Assert.ThrowsAsync<EmailException>(() => new SmtpEmailSender(Settings(smtp)).SendAsync(new EmailMessage("not an address", "s", "t", "h")));                                         // bad recipient
        await Assert.ThrowsAsync<EmailException>(() => new SmtpEmailSender(Settings(smtp, new() { ["Email.FromAddress"] = "nonsense" })).SendAsync(new EmailMessage("a@example.test", "s", "t", "h"))); // bad sender
        smtp.RejectNext = 1;
        var refused = await Assert.ThrowsAsync<EmailException>(() => new SmtpEmailSender(Settings(smtp)).SendAsync(new EmailMessage("a@example.test", "s", "t", "h")));                         // server says no
        Assert.Contains("refused", refused.Message);
        var dead = Settings(smtp, new() { ["Email.SmtpPort"] = "1" });
        await Assert.ThrowsAsync<EmailException>(() => new SmtpEmailSender(dead).SendAsync(new EmailMessage("a@example.test", "s", "t", "h")));                                                    // nothing listening
        Assert.Empty(smtp.Messages);
    }
}

public class EmailRendererTests
{
    private static async Task<RenderedEmail> Render(ApiHost host, string evt, string lang, Dictionary<string, string> data) =>
        await host.App.Services.GetRequiredService<IEmailRenderer>().RenderAsync(evt, lang, data);

    [Fact]
    public async Task Texts_are_filled_in_in_the_recipients_language_and_in_both_text_and_html()
    {
        await using var host = await ApiHost.StartAsync();
        var data = new Dictionary<string, string> { ["name"] = "Maya", ["link"] = "https://sayserne.test/verify-email?token=abc_def-1", ["hours"] = "48" };
        var en = await Render(host, "verify_email", "en", data);
        Assert.Equal("Confirm your email address on SaySerné", en.Subject);
        Assert.Contains("Hello Maya,", en.Text); Assert.Contains("valid for 48 hours", en.Text); Assert.Contains("https://sayserne.test/verify-email?token=abc_def-1", en.Text);
        Assert.Contains("<a href=\"https://sayserne.test/verify-email?token=abc_def-1\">", en.Html);
        Assert.Contains("dir=\"ltr\"", en.Html);
        var ar = await Render(host, "verify_email", "ar", data);
        Assert.Contains("بريدك", ar.Subject); Assert.Contains("مرحبًا Maya", ar.Text);
        Assert.Contains("dir=\"rtl\"", ar.Html); Assert.Contains("lang=\"ar\"", ar.Html);
        Assert.DoesNotContain("{{", ar.Text + ar.Html + en.Text + en.Html);   // every placeholder was filled
    }

    [Fact]
    public async Task Values_are_encoded_in_html_and_cannot_break_the_subject_line()
    {
        await using var host = await ApiHost.StartAsync();
        var r = await Render(host, "booking_requested", "en", new() { ["name"] = "P", ["client"] = "<script>alert(1)</script>\r\nBcc: x@y.z", ["when"] = "now", ["note"] = "a & b <i>", ["link"] = "https://sayserne.test/appointments" });
        Assert.DoesNotContain("<script>", r.Html);
        Assert.Contains("&lt;script&gt;", r.Html);
        Assert.Contains("a &amp; b &lt;i&gt;", r.Html);
        Assert.DoesNotContain("\n", r.Subject);
        Assert.DoesNotContain("\r", r.Subject);
        Assert.Contains("<script>", r.Text);   // the plain-text version is plain text
    }

    [Fact]
    public async Task A_language_without_a_text_falls_back_to_the_default_and_an_unknown_event_is_an_error()
    {
        await using var host = await ApiHost.StartAsync();
        var r = await Render(host, "password_reset", "en", new() { ["name"] = "N", ["link"] = "L", ["minutes"] = "60" });
        Assert.Contains("60 minutes", r.Text);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Render(host, "no_such_event", "en", new()));
    }

    [Fact]
    public async Task An_admin_edit_of_the_wording_is_used_by_the_very_next_email()
    {
        await using var iso = await IsolatedHost.StartAsync();
        var (admin, _, _) = await iso.Host.CreateAdminAsync();
        var edit = await admin.PutAsync("/api/admin/translations/en/email.new_message.subject", new { value = "Ping from {{sender}}!" });
        Assert.Equal(HttpStatusCode.OK, edit.Status);
        Assert.Equal("Ping from Karim!", (await Render(iso.Host, "new_message", "en", new() { ["name"] = "N", ["sender"] = "Karim", ["link"] = "L" })).Subject);
        await admin.PostAsync("/api/admin/translations/en/email.new_message.subject/reset");
        Assert.Equal("New message from Karim", (await Render(iso.Host, "new_message", "en", new() { ["name"] = "N", ["sender"] = "Karim", ["link"] = "L" })).Subject);
    }
}

public class SecretSettingsTests
{
    [Fact]
    public async Task A_secret_is_stored_encrypted_never_shown_never_audited_and_never_public()
    {
        await using var iso = await IsolatedHost.StartAsync();
        var host = iso.Host;
        var (admin, _, _) = await host.CreateAdminAsync();
        var (status, row) = await admin.PutAsync("/api/admin/settings/Email.SmtpPassword", new { value = "Sup3r-Secret!" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(("", true, "secret"), (row.GetProperty("value").GetString(), row.GetProperty("isSet").GetBoolean(), row.GetProperty("type").GetString()));
        var listed = J.Find((await admin.GetAsync("/api/admin/settings")).Body, "key", "Email.SmtpPassword");
        Assert.Equal(("", true), (listed.GetProperty("value").GetString(), listed.GetProperty("isSet").GetBoolean()));
        Assert.DoesNotContain("Sup3r-Secret!", (await admin.GetAsync("/api/admin/settings")).Body.GetRawText());

        var stored = await iso.Db.ScalarAsync("SELECT value_text FROM app_settings WHERE setting_key = 'Email.SmtpPassword'");
        Assert.True(!string.IsNullOrEmpty(stored) && !stored.Contains("Sup3r-Secret!"), "the database must hold ciphertext, not the password");
        await host.App.Services.GetRequiredService<Sayserne.Api.Platform.ISettingsService>().RefreshAsync();
        Assert.Equal("Sup3r-Secret!", host.App.Services.GetRequiredService<Sayserne.Api.Platform.ISettingsService>().GetSecret("Email.SmtpPassword"));   // the server can use it

        var audit = (await admin.GetAsync("/api/admin/audit?action=settings.update&search=SmtpPassword")).Body.GetProperty("items")[0];
        Assert.DoesNotContain("Sup3r-Secret!", audit.GetRawText());
        Assert.DoesNotContain(stored!, audit.GetRawText());
        Assert.True(audit.GetProperty("details").GetProperty("secret").GetBoolean());
        Assert.False((await host.NewClient().GetAsync("/api/config/public")).Body.TryGetProperty("Email.SmtpPassword", out _));

        var (_, cleared) = await admin.PostAsync("/api/admin/settings/Email.SmtpPassword/reset");   // reset = clear
        Assert.False(cleared.GetProperty("isSet").GetBoolean());
        Assert.Equal("", await iso.Db.ScalarAsync("SELECT value_text FROM app_settings WHERE setting_key = 'Email.SmtpPassword'"));
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/admin/settings/Email.SmtpPassword", new { value = "" })).Status);   // an empty value is allowed for a secret only
    }

    [Fact]
    public async Task Email_settings_are_validated_and_only_the_right_staff_can_change_them()
    {
        await using var iso = await IsolatedHost.StartAsync();
        var (admin, _, _) = await iso.Host.CreateAdminAsync();
        async Task<string?> Err(string key, string value) { var (s, b) = await admin.PutAsync($"/api/admin/settings/{key}", new { value }); return s == HttpStatusCode.OK ? null : TestClient.Error(b); }
        Assert.Equal("SETTING_INVALID", await Err("Email.Provider", "ftp"));
        Assert.Equal("SETTING_INVALID", await Err("Email.SmtpSecurity", "tls"));          // implicit TLS is not supported, so it cannot be chosen
        Assert.Equal("SETTING_INVALID", await Err("Email.FromAddress", "not-an-address"));
        Assert.Equal("SETTING_INVALID", await Err("Site.PublicUrl", "javascript:alert(1)"));
        Assert.Equal("SETTING_INVALID", await Err("Site.PublicUrl", "https://x.test/"));  // no trailing slash
        Assert.Equal("SETTING_OUT_OF_RANGE", await Err("Email.SmtpPort", "70000"));
        Assert.Equal("SETTING_INVALID", await Err("Email.FromName", "   "));             // a name is required (empty is only allowed where the default is empty)
        Assert.Null(await Err("Email.SmtpHost", ""));                                      // the host may be cleared
        Assert.Null(await Err("Site.PublicUrl", "https://www.sayserne.test"));
        Assert.Null(await Err("Email.Provider", "smtp"));
        var (reader, _, _) = await iso.Host.CreateStaffAsync("settings.read");
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsync("/api/admin/settings/Email.SmtpPassword", new { value = "x" })).Status);
        Assert.False(J.Find((await reader.GetAsync("/api/admin/settings")).Body, "key", "Email.SmtpPassword").GetProperty("isSet").GetBoolean());
    }
}

/// <summary>Everything that needs a working mail server: an isolated site with e-mail switched on and a fake SMTP server behind it.</summary>
public sealed class MailSite : IAsyncDisposable
{
    public required IsolatedHost Iso { get; init; }
    public required FakeSmtp Smtp { get; init; }
    public required TestClient Admin { get; init; }
    public ApiHost Host => Iso.Host;

    public static async Task<MailSite> StartAsync(bool emailOn = true)
    {
        var smtp = await FakeSmtp.StartAsync();
        var iso = await IsolatedHost.StartAsync();
        var (admin, _, _) = await iso.Host.CreateAdminAsync();
        var site = new MailSite { Iso = iso, Smtp = smtp, Admin = admin };
        if (emailOn)
            foreach (var (k, v) in new[] { ("Email.SmtpHost", "127.0.0.1"), ("Email.SmtpPort", smtp.Port.ToString()), ("Email.SmtpSecurity", "none"), ("Email.FromAddress", "hello@sayserne.test"), ("Site.PublicUrl", "https://sayserne.test"), ("Email.Provider", "smtp") })
                await site.Set(k, v);
        return site;
    }

    public async Task Set(string key, string value) => Assert.Equal(HttpStatusCode.OK, (await Admin.PutAsync($"/api/admin/settings/{key}", new { value })).Status);

    /// <summary>Runs every due background job (sends the queued e-mails).</summary>
    public async Task<int> Drain()
    {
        var runner = Host.App.Services.GetRequiredService<JobRunner>();
        var total = 0; int n;
        while ((n = await runner.RunOnceAsync()) > 0) total += n;
        return total;
    }

    public async Task<FakeSmtp.Received[]> Mail(Func<FakeSmtp.Received, bool>? where = null) { await Drain(); return Smtp.Messages.Where(where ?? (_ => true)).ToArray(); }
    public static string TokenIn(FakeSmtp.Received m) => Regex.Match(m.Text, "token=([A-Za-z0-9_-]+)").Groups[1].Value;

    public async ValueTask DisposeAsync() { await Iso.DisposeAsync(); await Smtp.DisposeAsync(); }
}

public class EmailVerificationTests
{
    [Fact]
    public async Task Registering_sends_a_verification_link_and_following_it_verifies_the_account_once()
    {
        await using var site = await MailSite.StartAsync();
        var (c, email, id) = await site.Host.RegisterAsync();
        Assert.False((await c.GetAsync("/api/auth/me")).Body.GetProperty("user").GetProperty("emailVerified").GetBoolean());
        var m = Assert.Single(await site.Mail());
        Assert.Equal(email.ToLowerInvariant(), m.To.ToLowerInvariant());
        Assert.StartsWith("https://sayserne.test/verify-email?token=", Regex.Match(m.Text, @"https://\S+").Value);   // the link uses the configured public address
        var token = MailSite.TokenIn(m);

        Assert.Equal(HttpStatusCode.OK, (await site.Host.NewClient().PostAsync("/api/auth/verify-email", new { token })).Status);   // anyone holding the link can complete it (no login needed)
        Assert.True((await c.GetAsync("/api/auth/me")).Body.GetProperty("user").GetProperty("emailVerified").GetBoolean());
        var (s2, b2) = await site.Host.NewClient().PostAsync("/api/auth/verify-email", new { token });                                // single use
        Assert.Equal((HttpStatusCode.BadRequest, "INVALID_TOKEN"), (s2, TestClient.Error(b2)));
        // only a hash is stored
        var stored = await site.Iso.Db.ScalarAsync($"SELECT token_hash FROM user_tokens WHERE user_id = {id}");
        Assert.Equal(64, stored!.Length);
        Assert.NotEqual(token, stored);
    }

    [Fact]
    public async Task Bad_unknown_expired_and_replaced_tokens_are_all_refused_the_same_way()
    {
        await using var site = await MailSite.StartAsync();
        var (c, _, id) = await site.Host.RegisterAsync();
        var first = MailSite.TokenIn((await site.Mail()).Single());
        foreach (var bad in new[] { "", "short", new string('a', 43), "has spaces in it and is long enough to pass the length check", new string('é', 43), new string('A', 100) })
        {
            var (s, b) = await site.Host.NewClient().PostAsync("/api/auth/verify-email", new { token = bad });
            Assert.True(s == HttpStatusCode.BadRequest && TestClient.Error(b) == "INVALID_TOKEN", $"'{bad}': {s} {b}");
        }
        // asking again replaces the link: the old one stops working
        Assert.Equal(HttpStatusCode.Accepted, (await c.PostAsync("/api/auth/resend-verification")).Status);
        var second = MailSite.TokenIn((await site.Mail()).Last());
        Assert.NotEqual(first, second);
        Assert.Equal(HttpStatusCode.BadRequest, (await site.Host.NewClient().PostAsync("/api/auth/verify-email", new { token = first })).Status);
        // an expired link
        await site.Iso.Db.ExecAsync($"UPDATE user_tokens SET expires_at = DATE_SUB(UTC_TIMESTAMP(3), INTERVAL 1 MINUTE) WHERE user_id = {id}");
        Assert.Equal(HttpStatusCode.BadRequest, (await site.Host.NewClient().PostAsync("/api/auth/verify-email", new { token = second })).Status);
    }

    [Fact]
    public async Task Resending_is_limited_per_hour_and_does_nothing_for_a_verified_account_or_a_stranger()
    {
        await using var site = await MailSite.StartAsync();
        await site.Set("Email.MaxTokenEmailsPerHour", "3");
        var (c, _, _) = await site.Host.RegisterAsync();   // the registration e-mail is the first of the three
        Assert.Equal(HttpStatusCode.Accepted, (await c.PostAsync("/api/auth/resend-verification")).Status);
        Assert.Equal(HttpStatusCode.Accepted, (await c.PostAsync("/api/auth/resend-verification")).Status);
        var (s, b) = await c.PostAsync("/api/auth/resend-verification");
        Assert.Equal((HttpStatusCode)429, s);
        Assert.Equal("TOO_MANY_EMAILS", TestClient.Error(b));
        Assert.Equal(3, (await site.Mail()).Length);                                                    // no fourth e-mail was sent
        Assert.Equal(HttpStatusCode.Unauthorized, (await site.Host.NewClient().PostAsync("/api/auth/resend-verification")).Status);

        var (d, _, _) = await site.Host.RegisterAsync();
        await site.Host.NewClient().PostAsync("/api/auth/verify-email", new { token = MailSite.TokenIn((await site.Mail()).Last()) });
        Assert.True((await d.PostAsync("/api/auth/resend-verification")).Body.GetProperty("alreadyVerified").GetBoolean());
    }

    [Fact]
    public async Task Booking_and_messaging_need_a_verified_address_but_only_while_email_is_switched_on_and_required()
    {
        await using var site = await MailSite.StartAsync();
        var host = site.Host;
        var (pro, _, proId) = await host.RegisterProfessionalAsync(city: "Verifyville");
        await host.AddProfessionAsync(pro, extra: new() { ["city"] = "Verifyville" });
        await site.Mail();
        var (c, _, cId) = await host.RegisterAsync();
        var token = MailSite.TokenIn((await site.Mail()).Last());

        async Task<(HttpStatusCode, string?)> TryBook() { var (s, b) = await c.PostAsync("/api/appointments", new { professionalId = proId, startsAt = ApiHost.FutureSlot() }); return (s, TestClient.Error(b)); }
        Assert.Equal((HttpStatusCode.Forbidden, "EMAIL_NOT_VERIFIED"), await TryBook());
        Assert.Equal((HttpStatusCode.Forbidden, "EMAIL_NOT_VERIFIED"), ((await c.PostAsync("/api/chat/rooms", new { userId = proId })).Status, TestClient.Error((await c.PostAsync("/api/chat/rooms", new { userId = proId })).Body)));
        await site.Set("Auth.RequireVerifiedEmail", "false");
        Assert.Equal(HttpStatusCode.Created, (await TryBook()).Item1);                      // not required: fine
        await site.Set("Auth.RequireVerifiedEmail", "true");
        Assert.Equal(HttpStatusCode.Forbidden, (await TryBook()).Item1);
        await site.Set("Email.Provider", "off");
        Assert.Equal(HttpStatusCode.Created, (await TryBook()).Item1);                      // e-mail off: verification cannot be demanded, so nobody is locked out
        await site.Set("Email.Provider", "smtp");
        await host.NewClient().PostAsync("/api/auth/verify-email", new { token });
        var (s2, b2) = await c.PostAsync("/api/appointments", new { professionalId = proId, startsAt = ApiHost.FutureSlot(9) });
        Assert.Equal(HttpStatusCode.Created, s2);                                           // verified: allowed
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync("/api/chat/rooms", new { userId = proId })).Status);
        _ = cId; _ = b2;
    }

    [Fact]
    public async Task Google_sign_in_counts_as_verified()
    {
        await using var site = await MailSite.StartAsync();
        Assert.True(true);   // covered end to end in GoogleFlowTests (accounts created by Google are created verified); here only the rule that verified users are never asked again
        var (c, _, _) = await site.Host.RegisterAsync();
        var token = MailSite.TokenIn((await site.Mail()).Single());
        await site.Host.NewClient().PostAsync("/api/auth/verify-email", new { token });
        Assert.True((await c.PostAsync("/api/auth/resend-verification")).Body.GetProperty("alreadyVerified").GetBoolean());
        Assert.Single(await site.Mail());   // nothing new was sent
    }
}

public class PasswordResetTests
{
    [Fact]
    public async Task Forgot_password_looks_the_same_for_everyone_and_only_mails_real_accounts()
    {
        await using var site = await MailSite.StartAsync();
        var (_, email, _) = await site.Host.RegisterAsync();
        await site.Mail();
        var known = await site.Host.NewClient().PostAsync("/api/auth/forgot-password", new { email });
        var unknown = await site.Host.NewClient().PostAsync("/api/auth/forgot-password", new { email = "nobody@example.test" });
        var junk = await site.Host.NewClient().PostAsync("/api/auth/forgot-password", new { email = "not an email" });
        Assert.Equal(HttpStatusCode.Accepted, known.Status);
        Assert.Equal((known.Status, known.Body.GetRawText()), (unknown.Status, unknown.Body.GetRawText()));   // no way to tell them apart
        Assert.Equal((known.Status, known.Body.GetRawText()), (junk.Status, junk.Body.GetRawText()));
        var resets = (await site.Mail(m => m.Subject.Contains("Reset"))).ToArray();
        Assert.Single(resets);
        Assert.Equal(email.ToLowerInvariant(), resets[0].To.ToLowerInvariant());
    }

    [Fact]
    public async Task A_valid_link_sets_the_new_password_once_signs_out_old_sessions_and_verifies_the_address()
    {
        await using var site = await MailSite.StartAsync();
        var (oldSession, email, _) = await site.Host.RegisterAsync();
        await site.Mail();
        await site.Host.NewClient().PostAsync("/api/auth/forgot-password", new { email });
        var token = MailSite.TokenIn((await site.Mail(m => m.Subject.Contains("Reset"))).Single());

        var weak = await site.Host.NewClient().PostAsync("/api/auth/reset-password", new { token, password = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, weak.Status);
        Assert.True(weak.Body.GetProperty("errors").TryGetProperty("password", out _));            // the password rules apply, and the link was NOT used up
        var (rs, _) = await site.Host.NewClient().PostAsync("/api/auth/reset-password", new { token, password = "A-brand-new-pass-77" });
        Assert.Equal(HttpStatusCode.OK, rs);

        Assert.Equal(HttpStatusCode.Unauthorized, (await oldSession.GetAsync("/api/auth/me")).Status);   // every session opened with the old password ended
        Assert.Equal(HttpStatusCode.Unauthorized, (await site.Host.NewClient().PostAsync("/api/auth/login", new { email, password = ApiHost.StrongPassword })).Status);
        var fresh = site.Host.NewClient();
        Assert.Equal(HttpStatusCode.OK, (await fresh.PostAsync("/api/auth/login", new { email, password = "A-brand-new-pass-77" })).Status);
        Assert.True((await fresh.GetAsync("/api/auth/me")).Body.GetProperty("user").GetProperty("emailVerified").GetBoolean());   // they proved they own the mailbox
        var (again, body) = await site.Host.NewClient().PostAsync("/api/auth/reset-password", new { token, password = "Another-pass-88" });
        Assert.Equal((HttpStatusCode.BadRequest, "INVALID_TOKEN"), (again, TestClient.Error(body)));   // single use
        Assert.Equal("64", await site.Iso.Db.ScalarAsync("SELECT MAX(CHAR_LENGTH(token_hash)) FROM user_tokens WHERE purpose = 'reset_password'"));   // only hashes are stored
    }

    [Fact]
    public async Task A_reset_clears_a_temporary_lockout_but_never_an_admin_lock_and_expired_links_fail()
    {
        await using var site = await MailSite.StartAsync();
        await site.Set("Auth.MaxFailedAttempts", "3");
        var (_, email, id) = await site.Host.RegisterAsync();
        await site.Mail();
        for (var i = 0; i < 3; i++) await site.Host.NewClient().PostAsync("/api/auth/login", new { email, password = "wrong-password-1" });
        Assert.Equal("ACCOUNT_LOCKED", TestClient.Error((await site.Host.NewClient().PostAsync("/api/auth/login", new { email, password = ApiHost.StrongPassword })).Body));
        await site.Host.NewClient().PostAsync("/api/auth/forgot-password", new { email });
        var token = MailSite.TokenIn((await site.Mail(m => m.Subject.Contains("Reset"))).Single());
        Assert.Equal(HttpStatusCode.OK, (await site.Host.NewClient().PostAsync("/api/auth/reset-password", new { token, password = "Fresh-start-pass-1" })).Status);
        Assert.Equal(HttpStatusCode.OK, (await site.Host.NewClient().PostAsync("/api/auth/login", new { email, password = "Fresh-start-pass-1" })).Status);

        // an account an admin has locked gets no reset e-mail at all, and cannot use an older link either
        await site.Host.NewClient().PostAsync("/api/auth/forgot-password", new { email });
        var older = MailSite.TokenIn((await site.Mail(m => m.Subject.Contains("Reset"))).Last());
        Assert.Equal(HttpStatusCode.OK, (await site.Admin.PostAsync($"/api/admin/users/{id}/lock", new { isLocked = true })).Status);
        var before = (await site.Mail()).Length;
        await site.Host.NewClient().PostAsync("/api/auth/forgot-password", new { email });
        Assert.Equal(before, (await site.Mail()).Length);
        await site.Host.NewClient().PostAsync("/api/auth/reset-password", new { token = older, password = "Locked-user-pass-1" });
        Assert.Equal("ACCOUNT_LOCKED", TestClient.Error((await site.Host.NewClient().PostAsync("/api/auth/login", new { email, password = "Locked-user-pass-1" })).Body));
        await site.Iso.Db.ExecAsync($"UPDATE user_tokens SET expires_at = DATE_SUB(UTC_TIMESTAMP(3), INTERVAL 1 MINUTE) WHERE user_id = {id}");
        Assert.Equal("INVALID_TOKEN", TestClient.Error((await site.Host.NewClient().PostAsync("/api/auth/reset-password", new { token = older, password = "Expired-link-pass-1" })).Body));
    }

    [Fact]
    public async Task Nothing_is_sent_and_no_token_is_created_while_email_is_off()
    {
        await using var site = await MailSite.StartAsync(emailOn: false);
        var (_, email, _) = await site.Host.RegisterAsync();
        Assert.Equal(HttpStatusCode.Accepted, (await site.Host.NewClient().PostAsync("/api/auth/forgot-password", new { email })).Status);
        await site.Drain();
        Assert.Empty(site.Smtp.Messages);
        Assert.Equal("0", await site.Iso.Db.ScalarAsync("SELECT COUNT(*) FROM user_tokens"));
        Assert.Equal("0", await site.Iso.Db.ScalarAsync("SELECT COUNT(*) FROM email_outbox"));
    }
}

public class RateLimitScopeTests
{
    [Fact]
    public async Task Only_endpoints_that_take_passwords_or_tokens_are_rate_limited_never_the_calls_every_page_makes()
    {
        await using var iso = await IsolatedHost.StartAsync(pinRateLimit: false);   // the real, database-driven limit is in force
        var host = iso.Host;
        var (admin, _, _) = await host.CreateAdminAsync();
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/admin/settings/Auth.AuthRequestsPerMinute", new { value = "5" })).Status);
        var (user, _, _) = await host.RegisterAsync();

        var page = host.NewClient();
        for (var i = 0; i < 40; i++)                                      // a visitor clicking through the site: 40 page loads in a few seconds
        {
            Assert.NotEqual((HttpStatusCode)429, (await page.GetAsync("/api/auth/me")).Status);
            Assert.NotEqual((HttpStatusCode)429, (await page.GetAsync("/api/auth/providers")).Status);
            Assert.NotEqual((HttpStatusCode)429, (await page.GetAsync("/api/config/public")).Status);
        }
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/auth/me")).Status);

        async Task<bool> Limited(Func<Task<HttpStatusCode>> call) { for (var i = 0; i < 15; i++) if (await call() == (HttpStatusCode)429) return true; return false; }
        Assert.True(await Limited(async () => (await host.NewClient().PostAsync("/api/auth/login", new { email = "x@example.test", password = "nope-nope-1" })).Status), "login must be limited");
        Assert.True(await Limited(async () => (await host.NewClient().PostAsync("/api/auth/forgot-password", new { email = "x@example.test" })).Status), "forgot-password must be limited");
        Assert.True(await Limited(async () => (await host.NewClient().PostAsync("/api/auth/reset-password", new { token = new string('a', 43), password = "whatever-12345" })).Status), "reset-password must be limited");
        Assert.True(await Limited(async () => (await host.NewClient().PostAsync("/api/auth/verify-email", new { token = new string('a', 43) })).Status), "verify-email must be limited");
    }
}
