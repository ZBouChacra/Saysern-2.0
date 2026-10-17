using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sayserne.Api.Email;

namespace Sayserne.Tests;

public class AccountTests
{
    private static async Task<(TestClient Client, string Email, long Id)> User(MailSite site) { var (c, e, i) = await site.Host.RegisterAsync(); return (c, e, i); }
    private static async Task<JsonElement> Me(TestClient c) => (await c.GetAsync("/api/auth/me")).Body.GetProperty("user");
    private static async Task<(HttpStatusCode, string?)> Login(ApiHost host, string email, string password) { var (s, b) = await host.NewClient().PostAsync("/api/auth/login", new { email, password }); return (s, s == HttpStatusCode.OK ? null : TestClient.Error(b)); }

    [Fact]
    public async Task A_person_can_choose_their_language_and_time_zone_from_the_lists_an_admin_maintains()
    {
        await using var site = await MailSite.StartAsync(emailOn: false);
        var (c, _, _) = await User(site);
        Assert.Equal(HttpStatusCode.Unauthorized, (await site.Host.NewClient().PutAsync("/api/me/preferences", new { language = "ar" })).Status);
        Assert.Equal("INVALID_LANGUAGE", TestClient.Error((await c.PutAsync("/api/me/preferences", new { language = "xx" })).Body));
        Assert.Equal("INVALID_TIME_ZONE", TestClient.Error((await c.PutAsync("/api/me/preferences", new { language = "en", timeZone = "Mars/Olympus" })).Body));
        var (st, body) = await c.PutAsync("/api/me/preferences", new { language = "ar", timeZone = "Asia/Dubai" });
        Assert.Equal(HttpStatusCode.OK, st);
        Assert.Equal(("ar", "Asia/Dubai"), (body.GetProperty("user").GetProperty("preferredLanguage").GetString(), body.GetProperty("user").GetProperty("timeZone").GetString()));
        var me = await Me(c);
        Assert.Equal(("ar", "Asia/Dubai"), (me.GetProperty("preferredLanguage").GetString(), me.GetProperty("timeZone").GetString()));      // remembered
        var (_, cleared) = await c.PutAsync("/api/me/preferences", new { language = "en", timeZone = "" });
        Assert.False(cleared.GetProperty("user").TryGetProperty("timeZone", out var tz) && tz.ValueKind == JsonValueKind.String);        // back to the site's default
        var zones = (await site.Host.NewClient().GetAsync("/api/lookups/time_zone")).Body.EnumerateArray().Select(z => z.GetProperty("code").GetString()).ToList();
        Assert.True(zones.Contains("Asia/Beirut") && zones.Contains("UTC") && zones.Count > 20);
        Assert.Equal("Asia/Beirut", (await site.Host.NewClient().GetAsync("/api/config/public")).Body.GetProperty("Site.DefaultTimeZone").GetString());
    }

    [Fact]
    public async Task Changing_the_password_needs_the_current_one_follows_the_rules_ends_other_sessions_and_warns_by_e_mail()
    {
        await using var site = await MailSite.StartAsync();
        var (c, email, id) = await User(site);
        var other = site.Host.NewClient(); await other.PostAsync("/api/auth/login", new { email, password = ApiHost.StrongPassword });   // a second device
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/api/auth/me")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await site.Host.NewClient().PostAsync("/api/me/password", new { currentPassword = "x", newPassword = "y" })).Status);

        var (ws, wrong) = await c.PostAsync("/api/me/password", new { currentPassword = "not-my-password", newPassword = "A-new-password-77" });
        Assert.Equal((HttpStatusCode.BadRequest, "WRONG_PASSWORD"), (ws, TestClient.Error(wrong)));
        Assert.True(wrong.GetProperty("errors").TryGetProperty("currentPassword", out _));
        var (weak, wb) = await c.PostAsync("/api/me/password", new { currentPassword = ApiHost.StrongPassword, newPassword = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, weak); Assert.True(wb.GetProperty("errors").TryGetProperty("newPassword", out _));
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/api/me/password", new { currentPassword = ApiHost.StrongPassword, newPassword = ApiHost.StrongPassword })).Status);   // not the same one again

        await site.Drain(); while (site.Smtp.Messages.TryDequeue(out _)) { }
        var (ok, _) = await c.PostAsync("/api/me/password", new { currentPassword = ApiHost.StrongPassword, newPassword = "A-new-password-77" });
        Assert.Equal(HttpStatusCode.OK, ok);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/auth/me")).Status);                                       // this device stays signed in...
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync("/api/auth/me")).Status);                          // ...every other one is signed out
        Assert.Equal((HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS"), await Login(site.Host, email, ApiHost.StrongPassword));
        Assert.Equal(HttpStatusCode.OK, (await Login(site.Host, email, "A-new-password-77")).Item1);
        var mail = Assert.Single(await site.Mail(m => m.Subject.Contains("password was changed")));                         // even an unverified address gets this one (it is a security warning)
        Assert.Equal(email.ToLowerInvariant(), mail.To.ToLowerInvariant()); Assert.Contains("https://sayserne.test/forgot-password", mail.Text);
        _ = id;
    }

    [Fact]
    public async Task Guessing_the_current_password_counts_like_guessing_at_sign_in_and_a_Google_only_account_cannot_use_this_form()
    {
        await using var site = await MailSite.StartAsync(emailOn: false);
        await site.Set("Auth.MaxFailedAttempts", "3");
        var (c, email, id) = await User(site);
        for (var i = 0; i < 3; i++) await c.PostAsync("/api/me/password", new { currentPassword = "wrong-guess-" + i, newPassword = "A-new-password-77" });
        Assert.Equal((HttpStatusCode.Forbidden, "ACCOUNT_LOCKED"), await Login(site.Host, email, ApiHost.StrongPassword));
        _ = id;
    }

    [Fact]
    public async Task Disabling_needs_the_password_signs_out_hides_the_person_and_still_lets_them_sign_in_only_to_be_told_so()
    {
        await using var site = await MailSite.StartAsync(emailOn: false);
        var (pro, email, proId) = await site.Host.RegisterProfessionalAsync("Dina", "Disabled", "Disableville", "LB");
        await site.Host.AddProfessionAsync(pro, extra: new() { ["city"] = "Disableville", ["country"] = "LB" });
        var search = async () => (await site.Host.NewClient().GetAsync("/api/professionals/search?city=Disableville")).Body.GetProperty("total").GetInt32();
        Assert.Equal(1, await search());
        var (wrongStatus, wrong) = await pro.PostAsync("/api/me/disable", new { password = "not-my-password" });
        Assert.Equal((HttpStatusCode.BadRequest, "WRONG_PASSWORD"), (wrongStatus, TestClient.Error(wrong)));
        Assert.Equal(1, await search());                                                                                   // nothing happened
        Assert.Equal(HttpStatusCode.NoContent, (await pro.PostAsync("/api/me/disable", new { password = ApiHost.StrongPassword })).Status);

        Assert.Equal(HttpStatusCode.Unauthorized, (await pro.GetAsync("/api/auth/me")).Status);                            // signed out at once
        Assert.Equal((HttpStatusCode.Forbidden, "ACCOUNT_DISABLED"), await Login(site.Host, email, ApiHost.StrongPassword));   // the right password: told it is disabled
        Assert.Equal((HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS"), await Login(site.Host, email, "not-my-password"));  // a wrong one learns nothing
        Assert.Equal(0, await search());                                                                                    // hidden from search...
        Assert.Equal(HttpStatusCode.NotFound, (await site.Host.NewClient().GetAsync($"/api/professionals/{proId}")).Status);   // ...and the page is gone
        var (client, _, _) = await User(site);
        Assert.NotEqual(HttpStatusCode.Created, (await client.PostAsync("/api/chat/rooms", new { userId = proId })).Status);      // nobody can start a chat with them
        Assert.Equal("1", await site.Iso.Db.ScalarAsync($"SELECT COUNT(*) FROM users WHERE id = {proId}"));                // nothing was deleted
        var admin = (await site.Admin.GetAsync($"/api/admin/users?search={Uri.EscapeDataString(email)}")).Body.GetProperty("items")[0];
        Assert.True(admin.GetProperty("isDisabled").GetBoolean());                                                          // staff can see it
    }

    [Fact]
    public async Task A_disabled_account_comes_back_only_with_the_password_and_a_confirmation_link_by_e_mail()
    {
        await using var site = await MailSite.StartAsync();
        var (pro, email, proId) = await site.Host.RegisterProfessionalAsync("Rami", "Returns", "Returnville", "LB");
        await site.Host.AddProfessionAsync(pro, extra: new() { ["city"] = "Returnville", ["country"] = "LB" });
        await pro.PostAsync("/api/me/disable", new { password = ApiHost.StrongPassword });
        await site.Drain(); while (site.Smtp.Messages.TryDequeue(out _)) { }
        async Task<(HttpStatusCode, JsonElement)> Ask(string e, string pw) => await site.Host.NewClient().PostAsync("/api/auth/reenable/request", new { email = e, password = pw });

        var answers = new[] { await Ask("nobody@example.test", "x"), await Ask(email, "not-my-password"), await Ask("not an address", "x") };   // all look the same
        foreach (var (s, b) in answers) Assert.Equal((HttpStatusCode.Accepted, "{\"accepted\":true}"), (s, b.GetRawText()));
        Assert.Empty(await site.Mail());                                                                                    // no link for a wrong password or a stranger
        var good = await Ask(email, ApiHost.StrongPassword);
        Assert.Equal(HttpStatusCode.Accepted, good.Item1);
        var mail = Assert.Single(await site.Mail());
        Assert.Equal("Re-enable your SaySerné account", mail.Subject);
        Assert.Equal(email.ToLowerInvariant(), mail.To.ToLowerInvariant());
        var token = System.Text.RegularExpressions.Regex.Match(mail.Text, "reenable\\?token=([A-Za-z0-9_-]+)").Groups[1].Value;
        Assert.True(token.Length > 20, "the e-mail carries the re-enable link");

        Assert.Equal((HttpStatusCode.Forbidden, "ACCOUNT_DISABLED"), await Login(site.Host, email, ApiHost.StrongPassword));   // not yet: the link has not been opened
        Assert.Equal("INVALID_TOKEN", TestClient.Error((await site.Host.NewClient().PostAsync("/api/auth/reenable/confirm", new { token = "short" })).Body));
        Assert.Equal(HttpStatusCode.OK, (await site.Host.NewClient().PostAsync("/api/auth/reenable/confirm", new { token })).Status);
        Assert.Equal("INVALID_TOKEN", TestClient.Error((await site.Host.NewClient().PostAsync("/api/auth/reenable/confirm", new { token })).Body));   // single use
        Assert.Equal(HttpStatusCode.OK, (await Login(site.Host, email, ApiHost.StrongPassword)).Item1);                    // now they can sign in...
        Assert.Equal(1, (await site.Host.NewClient().GetAsync("/api/professionals/search?city=Returnville")).Body.GetProperty("total").GetInt32());   // ...and are visible again
        Assert.Equal(HttpStatusCode.OK, (await site.Host.NewClient().GetAsync($"/api/professionals/{proId}")).Status);
        var actions = (await site.Admin.GetAsync("/api/admin/audit?pageSize=100")).Body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("action").GetString()).ToList();
        Assert.True(actions.Contains("account.disable") && actions.Contains("account.reenable"));                           // both are on record
    }

    [Fact]
    public async Task An_account_locked_by_staff_can_never_be_switched_back_on_by_its_owner_and_links_expire()
    {
        await using var site = await MailSite.StartAsync();
        var (c, email, id) = await User(site);
        await c.PostAsync("/api/me/disable", new { password = ApiHost.StrongPassword });
        await site.Drain(); while (site.Smtp.Messages.TryDequeue(out _)) { }
        await site.Host.NewClient().PostAsync("/api/auth/reenable/request", new { email, password = ApiHost.StrongPassword });
        var token = System.Text.RegularExpressions.Regex.Match((await site.Mail()).Single().Text, "reenable\\?token=([A-Za-z0-9_-]+)").Groups[1].Value;
        Assert.Equal(HttpStatusCode.OK, (await site.Admin.PostAsync($"/api/admin/users/{id}/lock", new { isLocked = true })).Status);   // staff lock it after the link was sent
        var (st, body) = await site.Host.NewClient().PostAsync("/api/auth/reenable/confirm", new { token });
        Assert.Equal((HttpStatusCode.Forbidden, "ACCOUNT_LOCKED"), (st, TestClient.Error(body)));
        Assert.Equal("1", await site.Iso.Db.ScalarAsync($"SELECT CAST(is_disabled AS UNSIGNED) FROM users WHERE id = {id}"));   // still disabled
        await site.Drain(); while (site.Smtp.Messages.TryDequeue(out _)) { }
        await site.Host.NewClient().PostAsync("/api/auth/reenable/request", new { email, password = ApiHost.StrongPassword });
        Assert.Empty(await site.Mail());                                                                                    // a locked account gets no link at all

        var (d, dEmail, did) = await User(site);
        await d.PostAsync("/api/me/disable", new { password = ApiHost.StrongPassword });
        await site.Host.NewClient().PostAsync("/api/auth/reenable/request", new { email = dEmail, password = ApiHost.StrongPassword });
        var t2 = System.Text.RegularExpressions.Regex.Match((await site.Mail(m => m.Subject.Contains("Re-enable"))).Single().Text, "reenable\\?token=([A-Za-z0-9_-]+)").Groups[1].Value;
        await site.Iso.Db.ExecAsync($"UPDATE user_tokens SET expires_at = DATE_SUB(UTC_TIMESTAMP(3), INTERVAL 1 MINUTE) WHERE user_id = {did}");
        Assert.Equal("INVALID_TOKEN", TestClient.Error((await site.Host.NewClient().PostAsync("/api/auth/reenable/confirm", new { token = t2 })).Body));
    }

    [Fact]
    public async Task A_disabled_person_only_receives_the_mandatory_e_mails_and_a_wrong_password_in_the_request_counts_as_a_failed_sign_in()
    {
        await using var site = await MailSite.StartAsync();
        await site.Set("Auth.MaxFailedAttempts", "3");
        var (c, email, id) = await User(site);
        var mails = await site.Mail(); var verify = mails.Last(m => m.Text.Contains("verify-email"));                          // verify the address so ordinary notifications would normally arrive
        await site.Host.NewClient().PostAsync("/api/auth/verify-email", new { token = MailSite.TokenIn(verify) });
        var notifier = site.Host.App.Services.GetRequiredService<INotifier>();
        await site.Drain(); while (site.Smtp.Messages.TryDequeue(out _)) { }
        await notifier.NotifyAsync("new_message", id, new Dictionary<string, string> { ["sender"] = "Sam", ["link"] = "x" });
        Assert.Single(await site.Mail(m => m.Subject.Contains("New message")));                                                // an ordinary notification reaches an active account
        while (site.Smtp.Messages.TryDequeue(out _)) { }
        await c.PostAsync("/api/me/disable", new { password = ApiHost.StrongPassword });
        await notifier.NotifyAsync("new_message", id, new Dictionary<string, string> { ["sender"] = "Sam", ["link"] = "x" });
        Assert.Empty(await site.Mail(m => m.Subject.Contains("New message")));                                                 // ...but not a disabled one
        await notifier.NotifyAsync("password_changed", id, new Dictionary<string, string> { ["link"] = "x" });
        Assert.Single(await site.Mail(m => m.Subject.Contains("password was changed")));                                       // a security e-mail still does
        for (var i = 0; i < 3; i++) await site.Host.NewClient().PostAsync("/api/auth/reenable/request", new { email, password = "wrong-guess-" + i });
        Assert.Equal((HttpStatusCode.Forbidden, "ACCOUNT_LOCKED"), await Login(site.Host, email, ApiHost.StrongPassword));   // guessing here locks the account like guessing at sign-in
    }

    private static async Task<(IsolatedHost Iso, FakeSmtp Smtp, TestClient Admin)> GoogleSiteAsync()
    {
        var smtp = await FakeSmtp.StartAsync();
        var iso = await IsolatedHost.StartAsync(withGoogle: true);
        var (admin, _, _) = await iso.Host.CreateAdminAsync();
        foreach (var (k, v) in new[] { ("Email.SmtpHost", "127.0.0.1"), ("Email.SmtpPort", smtp.Port.ToString()), ("Email.SmtpSecurity", "none"), ("Site.PublicUrl", "https://sayserne.test"), ("Email.Provider", "smtp") })
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync($"/api/admin/settings/{k}", new { value = v })).Status);
        return (iso, smtp, admin);
    }

    [Fact]
    public async Task A_google_account_cannot_use_the_password_form_confirms_disabling_with_its_address_and_returns_through_google_plus_an_e_mail_link()
    {
        var (iso, smtp, _) = await GoogleSiteAsync();
        await using var _i = iso; await using var _s = smtp;
        var claims = new FakeGoogle.Claims($"sub-{Guid.NewGuid():N}", ApiHost.UniqueEmail("g"), true);
        var g = iso.Host.NewClient();
        await iso.Host.Google!.SignInAsync(g, claims);
        Assert.Equal("PASSWORD_NOT_SET", TestClient.Error((await g.PostAsync("/api/me/password", new { currentPassword = "anything", newPassword = "A-new-password-77" })).Body));
        Assert.Equal("VALIDATION_FAILED", TestClient.Error((await g.PostAsync("/api/me/disable", new { confirmEmail = "someone@else.test" })).Body));      // a Google account types its address instead of a password
        Assert.Equal(HttpStatusCode.NoContent, (await g.PostAsync("/api/me/disable", new { confirmEmail = claims.Email })).Status);

        using var again = await iso.Host.Google.SignInAsync(iso.Host.NewClient(), claims);                                      // signing in with Google again...
        Assert.StartsWith("/login?error=account_disabled", again.Headers.Location?.OriginalString ?? "");                       // ...is refused with the reason
        var runner = iso.Host.App.Services.GetRequiredService<Sayserne.Api.Jobs.JobRunner>();
        while (await runner.RunOnceAsync() > 0) { }
        var mail = Assert.Single(smtp.Messages.Where(m => m.Subject.Contains("Re-enable")));                                    // ...and an e-mail link is sent to confirm
        var token = System.Text.RegularExpressions.Regex.Match(mail.Text, "reenable\\?token=([A-Za-z0-9_-]+)").Groups[1].Value;
        Assert.Equal(HttpStatusCode.OK, (await iso.Host.NewClient().PostAsync("/api/auth/reenable/confirm", new { token })).Status);
        var back = iso.Host.NewClient();
        using var ok = await iso.Host.Google.SignInAsync(back, claims);
        Assert.Equal("/", ok.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await back.GetAsync("/api/auth/me")).Status);                                           // back in
    }
}
