using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Sayserne.Api.Email;
using Sayserne.Api.Jobs;

namespace Sayserne.Tests;

public class NotificationTests
{
    private sealed record Scene(MailSite Site, TestClient Pro, string ProEmail, long ProId, TestClient Client, string ClientEmail, long ClientId);

    private static async Task Verify(MailSite site, string email)
    {
        var mail = (await site.Mail(m => m.To.Equals(email, StringComparison.OrdinalIgnoreCase) && m.Text.Contains("verify-email"))).Last();
        Assert.Equal(HttpStatusCode.OK, (await site.Host.NewClient().PostAsync("/api/auth/verify-email", new { token = MailSite.TokenIn(mail) })).Status);
    }
    /// <summary>Lets everything queued so far go out, THEN forgets it, so the next assertion sees only what the next action causes.</summary>
    private static async Task ClearMail(MailSite site) { await site.Drain(); while (site.Smtp.Messages.TryDequeue(out _)) { } }

    /// <summary>A professional and a client, e-mail on, nothing sent yet. Both verified unless told otherwise.</summary>
    private static async Task<Scene> SceneAsync(string proLanguage = "en", bool verifyPro = true)
    {
        var site = await MailSite.StartAsync();
        var (pro, proEmail, proId) = await site.Host.RegisterProfessionalAsync("Pia", "Pro", city: "Notifyville");
        await site.Host.AddProfessionAsync(pro, extra: new() { ["city"] = "Notifyville" });
        await pro.PutAsync("/api/me/profile", new { firstName = "Pia", lastName = "Pro", profileType = "professional", preferredLanguage = proLanguage });
        var (client, clientEmail, clientId) = await site.Host.RegisterAsync();
        if (verifyPro) await Verify(site, proEmail);
        await Verify(site, clientEmail);
        await ClearMail(site);
        return new Scene(site, pro, proEmail, proId, client, clientEmail, clientId);
    }

    private static async Task<FakeSmtp.Received[]> NewMailFor(MailSite site, string email) =>
        (await site.Mail(m => m.To.Equals(email, StringComparison.OrdinalIgnoreCase))).ToArray();

    [Fact]
    public async Task Each_booking_event_emails_the_right_person_in_their_own_language_with_a_link()
    {
        var s = await SceneAsync(proLanguage: "ar");
        await using var _ = s.Site;
        var (_, booked) = await s.Client.PostAsync("/api/appointments", new { professionalId = s.ProId, startsAt = ApiHost.FutureSlot(), durationMinutes = 60, description = "Contract review" });
        var id = booked.GetProperty("id").GetInt64();

        var toPro = Assert.Single(await NewMailFor(s.Site, s.ProEmail));                          // 1. the professional hears about the request (in Arabic)
        Assert.Contains("طلب موعد جديد", toPro.Subject);
        Assert.Contains("Contract review", toPro.Text); Assert.Contains("https://sayserne.test/appointments", toPro.Text);
        Assert.Empty(await NewMailFor(s.Site, s.ClientEmail));                                     // the client is not told about their own action
        await ClearMail(s.Site);

        await s.Pro.PostAsync($"/api/appointments/{id}/status", new { status = "approved" });       // 2. the client hears it was approved (in English)
        var approved = Assert.Single(await NewMailFor(s.Site, s.ClientEmail));
        Assert.Equal("Your appointment with Pia Pro is confirmed", approved.Subject);
        Assert.Matches(@"\d{4}-\d{2}-\d{2} \d{2}:\d{2} UTC", approved.Text);
        Assert.Empty(await NewMailFor(s.Site, s.ProEmail)); await ClearMail(s.Site);

        await s.Client.PostAsync($"/api/appointments/{id}/status", new { status = "cancelled" });   // 3. the client cancels: the PROFESSIONAL is told
        Assert.Contains("تم إلغاء موعد", Assert.Single(await NewMailFor(s.Site, s.ProEmail)).Subject);
        Assert.Empty(await NewMailFor(s.Site, s.ClientEmail)); await ClearMail(s.Site);

        var (_, second) = await s.Client.PostAsync("/api/appointments", new { professionalId = s.ProId, startsAt = ApiHost.FutureSlot(), durationMinutes = 60 }); await ClearMail(s.Site);
        await s.Pro.PostAsync($"/api/appointments/{second.GetProperty("id").GetInt64()}/status", new { status = "cancelled" });   // 4. the professional cancels: the CLIENT is told
        Assert.Equal("An appointment was cancelled", Assert.Single(await NewMailFor(s.Site, s.ClientEmail)).Subject);
        Assert.Empty(await NewMailFor(s.Site, s.ProEmail)); await ClearMail(s.Site);

        var done = await s.Site.Host.CompletedAppointmentAsync(s.Client, s.Pro, s.ProId); await ClearMail(s.Site);
        await s.Client.PostAsync("/api/reviews", new { appointmentId = done, rating = 5, comment = "Great" });   // 5. a review: the professional is told
        var review = Assert.Single(await NewMailFor(s.Site, s.ProEmail));
        Assert.Contains("5/5", review.Subject); Assert.Contains($"https://sayserne.test/professional/{s.ProId}", review.Text);
    }

    [Fact]
    public async Task Completing_an_appointment_invites_the_client_to_review()
    {
        var s = await SceneAsync();
        await using var _ = s.Site;
        var (_, booked) = await s.Client.PostAsync("/api/appointments", new { professionalId = s.ProId, startsAt = ApiHost.FutureSlot(), durationMinutes = 60 });
        var id = booked.GetProperty("id").GetInt64();
        await s.Pro.PostAsync($"/api/appointments/{id}/status", new { status = "approved" });
        await s.Site.Host.Database.BackdateAppointmentAsync(id);
        await ClearMail(s.Site);
        await s.Pro.PostAsync($"/api/appointments/{id}/status", new { status = "completed" });
        var m = Assert.Single(await NewMailFor(s.Site, s.ClientEmail));
        Assert.Equal("How was your appointment with Pia Pro?", m.Subject);
        Assert.Contains($"https://sayserne.test/professional/{s.ProId}", m.Text);
    }

    [Fact]
    public async Task Addresses_that_are_unverified_or_locked_only_ever_get_the_mandatory_emails()
    {
        var s = await SceneAsync(verifyPro: false);
        await using var _ = s.Site;
        await s.Client.PostAsync("/api/appointments", new { professionalId = s.ProId, startsAt = ApiHost.FutureSlot(), durationMinutes = 60 });
        Assert.Empty(await NewMailFor(s.Site, s.ProEmail));                                        // an unverified address cannot be used to spam someone else's inbox
        await s.Pro.PostAsync("/api/auth/resend-verification");                                   // (the first link was cleared with the set-up mail)
        await Verify(s.Site, s.ProEmail); await ClearMail(s.Site);
        await s.Client.PostAsync("/api/appointments", new { professionalId = s.ProId, startsAt = ApiHost.FutureSlot(), durationMinutes = 60 });
        Assert.Single(await NewMailFor(s.Site, s.ProEmail));                                       // verified: now they are told

        await ClearMail(s.Site);
        await s.Site.Admin.PostAsync($"/api/admin/users/{s.ProId}/lock", new { isLocked = true });
        await s.Client.PostAsync("/api/appointments", new { professionalId = s.ProId, startsAt = ApiHost.FutureSlot(), durationMinutes = 60 });
        Assert.Empty(await NewMailFor(s.Site, s.ProEmail));
    }

    [Fact]
    public async Task Users_choose_which_emails_they_get_and_the_mandatory_ones_cannot_be_switched_off()
    {
        var s = await SceneAsync();
        await using var _ = s.Site;
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Site.Host.NewClient().GetAsync("/api/me/notifications")).Status);
        Assert.Equal(0, (await s.Pro.GetAsync("/api/me/notifications")).Body.GetArrayLength());       // defaults: everything on

        var (ok, prefs) = await s.Pro.PutAsync("/api/me/notifications", new { @event = "booking_requested", channel = "email", enabled = false });
        Assert.Equal(HttpStatusCode.OK, ok);
        Assert.Equal(("booking_requested", "email", false), (prefs[0].GetProperty("event").GetString(), prefs[0].GetProperty("channel").GetString(), prefs[0].GetProperty("enabled").GetBoolean()));
        await s.Client.PostAsync("/api/appointments", new { professionalId = s.ProId, startsAt = ApiHost.FutureSlot(), durationMinutes = 60 });
        Assert.Empty(await NewMailFor(s.Site, s.ProEmail));                                        // switched off: nothing
        Assert.Equal(0, (await s.Client.GetAsync("/api/me/notifications")).Body.GetArrayLength());   // other people's choices are their own

        await s.Pro.PutAsync("/api/me/notifications", new { @event = "booking_requested", channel = "email", enabled = true });
        await s.Client.PostAsync("/api/appointments", new { professionalId = s.ProId, startsAt = ApiHost.FutureSlot(), durationMinutes = 60 });
        Assert.Single(await NewMailFor(s.Site, s.ProEmail));                                       // back on

        foreach (var (name, body) in new (string, object)[]
        {
            ("a mandatory event", new { @event = "verify_email", channel = "email", enabled = false }), ("password reset", new { @event = "password_reset", channel = "email", enabled = false }),
            ("an unknown event", new { @event = "world_peace", channel = "email", enabled = false }), ("an unknown channel", new { @event = "new_message", channel = "pigeon", enabled = false }),
            ("a channel that is switched off (not integrated yet)", new { @event = "new_message", channel = "sms", enabled = true }), ("whatsapp", new { @event = "new_message", channel = "whatsapp", enabled = true }),
        })
        {
            var (st, bd) = await s.Pro.PutAsync("/api/me/notifications", body);
            Assert.True(st == HttpStatusCode.BadRequest && TestClient.Error(bd) == "NOTIFICATION_NOT_ALLOWED", $"{name}: {st} {bd}");
        }
        Assert.Equal("VALIDATION_FAILED", TestClient.Error((await s.Pro.PutAsync("/api/me/notifications", new { channel = "email" })).Body));
        // the channels offered to users are exactly the ones an admin has switched on
        var channels = (await s.Site.Host.NewClient().GetAsync("/api/lookups/notification_channel")).Body;
        Assert.Equal(["email"], channels.EnumerateArray().Select(c => c.GetProperty("code").GetString()).ToArray());
    }

    [Fact]
    public async Task A_new_message_email_waits_is_sent_once_per_conversation_window_and_only_if_still_unread()
    {
        var s = await SceneAsync();
        await using var _ = s.Site;
        var room = (await s.Client.PostAsync("/api/chat/rooms", new { userId = s.ProId })).Body.GetProperty("id").GetInt64();
        for (var i = 0; i < 3; i++) await s.Client.PostAsync($"/api/chat/rooms/{room}/messages", new { content = $"hello {i}" });
        Assert.Equal("1", await s.Site.Iso.Db.ScalarAsync("SELECT COUNT(*) FROM jobs WHERE job_type = 'email.new_message'"));    // three messages, one waiting e-mail
        Assert.Empty(await s.Site.Mail());                                                                                      // and it is not due yet
        Assert.True(await s.Site.Iso.Db.JobRunInAsync(long.Parse((await s.Site.Iso.Db.ScalarAsync("SELECT id FROM jobs WHERE job_type = 'email.new_message'"))!)) > 8 * 60);   // about ten minutes away

        await s.Site.Iso.Db.RunFixtureAsync("jobs_make_due.sql");
        var m = Assert.Single(await s.Site.Mail());
        Assert.Equal(s.ProEmail.ToLowerInvariant(), m.To.ToLowerInvariant());
        Assert.Equal("New message from " + (await s.Client.GetAsync("/api/auth/me")).Body.GetProperty("user").GetProperty("firstName").GetString() + " " + (await s.Client.GetAsync("/api/auth/me")).Body.GetProperty("user").GetProperty("lastName").GetString(), m.Subject);
        Assert.Contains($"https://sayserne.test/chats/{room}", m.Text);
        Assert.DoesNotContain("hello", m.Text);                                                                                 // the message itself is never put in an e-mail
    }

    [Fact]
    public async Task No_message_email_is_sent_if_the_recipient_read_the_conversation_in_the_meantime()
    {
        var s = await SceneAsync();
        await using var _ = s.Site;
        var room = (await s.Client.PostAsync("/api/chat/rooms", new { userId = s.ProId })).Body.GetProperty("id").GetInt64();
        await s.Client.PostAsync($"/api/chat/rooms/{room}/messages", new { content = "are you there?" });
        await s.Pro.GetAsync($"/api/chat/rooms/{room}/messages");                                      // opening the conversation marks it read
        await s.Site.Iso.Db.RunFixtureAsync("jobs_make_due.sql");
        Assert.Empty(await s.Site.Mail());
        Assert.Equal("done", await s.Site.Iso.Db.ScalarAsync("SELECT status FROM jobs WHERE job_type = 'email.new_message'"));   // the job ran and correctly decided to send nothing
    }

    [Fact]
    public async Task While_email_is_off_nothing_is_queued_for_any_event()
    {
        await using var site = await MailSite.StartAsync(emailOn: false);
        var (pro, _, proId) = await site.Host.RegisterProfessionalAsync(city: "Quietville");
        await site.Host.AddProfessionAsync(pro, extra: new() { ["city"] = "Quietville" });
        var (client, _, _) = await site.Host.RegisterAsync();
        await client.PostAsync("/api/appointments", new { professionalId = proId, startsAt = ApiHost.FutureSlot(), durationMinutes = 60 });
        var room = (await client.PostAsync("/api/chat/rooms", new { userId = proId })).Body.GetProperty("id").GetInt64();
        await client.PostAsync($"/api/chat/rooms/{room}/messages", new { content = "hi" });
        await site.Drain();
        Assert.Equal(("0", "0"), (await site.Iso.Db.ScalarAsync("SELECT COUNT(*) FROM email_outbox"), await site.Iso.Db.ScalarAsync("SELECT COUNT(*) FROM jobs WHERE job_type LIKE 'email.%'")));
    }
}

public class EmailDeliveryTests
{
    [Fact]
    public async Task A_refused_message_is_retried_with_growing_delays_then_sent_once_and_its_text_removed()
    {
        await using var site = await MailSite.StartAsync();
        site.Smtp.RejectNext = 2;
        var (_, email, _) = await site.Host.RegisterAsync();
        Assert.Empty(await site.Mail());
        var row = async () => (await site.Host.App.Services.GetRequiredService<IEmailRepository>().OutboxAsync(null, 1, 10)).Items.Single();
        var first = await row();
        Assert.Equal(("queued", 1), (first.Status, first.Attempts));
        Assert.Contains("refused", first.LastError);

        await site.Iso.Db.RunFixtureAsync("jobs_make_due.sql");
        Assert.Empty(await site.Mail());
        Assert.Equal(2, (await row()).Attempts);
        await site.Iso.Db.RunFixtureAsync("jobs_make_due.sql");
        var sent = Assert.Single(await site.Mail());                                                     // third try works
        Assert.Equal(email.ToLowerInvariant(), sent.To.ToLowerInvariant());
        var done = await row();
        Assert.Equal("sent", done.Status);
        Assert.NotNull(done.SentAt);
        var full = await site.Host.App.Services.GetRequiredService<IEmailRepository>().GetOutboxAsync(done.Id);
        Assert.True(full!.BodyText is null && full.BodyHtml is null, "once sent, the message (which holds a sign-in style link) is wiped");
        Assert.Single(await site.Mail());                                                                 // and it is never sent again
    }

    [Fact]
    public async Task A_message_that_keeps_failing_is_marked_failed_and_an_admin_retry_sends_it_once_the_server_is_fixed()
    {
        await using var site = await MailSite.StartAsync();
        await site.Set("Jobs.MaxAttempts", "2");
        site.Smtp.RejectNext = 100;
        var (_, email, _) = await site.Host.RegisterAsync();
        await site.Drain();
        await site.Iso.Db.RunFixtureAsync("jobs_make_due.sql"); await site.Drain();
        var repo = site.Host.App.Services.GetRequiredService<IEmailRepository>();
        var failed = (await repo.OutboxAsync("failed", 1, 10)).Items.Single();
        Assert.Equal(2, failed.Attempts);

        var (_, jobs) = await site.Admin.GetAsync("/api/admin/jobs?status=failed&type=email.send");
        var job = jobs.GetProperty("items").EnumerateArray().Single();
        site.Smtp.RejectNext = 0;                                                                         // the mail server is fixed
        Assert.Equal(HttpStatusCode.OK, (await site.Admin.PostAsync($"/api/admin/jobs/{job.GetProperty("id").GetInt64()}/retry")).Status);
        var sent = Assert.Single(await site.Mail());
        Assert.Equal(email.ToLowerInvariant(), sent.To.ToLowerInvariant());
        Assert.Equal("sent", (await repo.OutboxAsync(null, 1, 10)).Items.Single().Status);
    }

    [Fact]
    public async Task Switching_email_off_while_messages_wait_discards_them_instead_of_sending()
    {
        await using var site = await MailSite.StartAsync();
        var (_, _, _) = await site.Host.RegisterAsync();
        await site.Set("Email.Provider", "off");
        await site.Drain();
        Assert.Empty(site.Smtp.Messages);
        var row = (await site.Host.App.Services.GetRequiredService<IEmailRepository>().OutboxAsync(null, 1, 10)).Items.Single();
        Assert.Equal("skipped", row.Status);
        Assert.Null((await site.Host.App.Services.GetRequiredService<IEmailRepository>().GetOutboxAsync(row.Id))!.BodyText);
    }

    [Fact]
    public async Task Staff_can_see_what_was_sent_but_never_its_content_and_can_send_a_test_message()
    {
        await using var site = await MailSite.StartAsync(emailOn: false);
        Assert.Equal(HttpStatusCode.Unauthorized, (await site.Host.NewClient().GetAsync("/api/admin/email/outbox")).Status);
        var (nobody, _, _) = await site.Host.CreateStaffAsync("users.read");
        Assert.Equal(HttpStatusCode.Forbidden, (await nobody.GetAsync("/api/admin/email/outbox")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await nobody.PostAsync("/api/admin/email/test", new { to = "a@example.test" })).Status);

        var (s0, b0) = await site.Admin.PostAsync("/api/admin/email/test", new { to = "a@example.test" });                      // e-mail still off
        Assert.Equal((HttpStatusCode.Conflict, "EMAIL_NOT_CONFIGURED"), (s0, TestClient.Error(b0)));
        foreach (var (k, v) in new[] { ("Email.SmtpHost", "127.0.0.1"), ("Email.SmtpPort", "1"), ("Email.SmtpSecurity", "none"), ("Site.PublicUrl", "https://sayserne.test"), ("Email.Provider", "smtp") }) await site.Set(k, v);
        Assert.Equal("VALIDATION_FAILED", TestClient.Error((await site.Admin.PostAsync("/api/admin/email/test", new { to = "not an address" })).Body));
        var (s1, b1) = await site.Admin.PostAsync("/api/admin/email/test", new { to = "a@example.test" });                      // wrong port: the reason comes back at once
        Assert.Equal((HttpStatusCode.BadGateway, "EMAIL_SEND_FAILED"), (s1, TestClient.Error(b1)));
        Assert.False(string.IsNullOrWhiteSpace(b1.GetProperty("detail").GetString()));

        await site.Set("Email.SmtpPort", site.Smtp.Port.ToString());
        var (s2, _) = await site.Admin.PostAsync("/api/admin/email/test", new { to = "a@example.test" });
        Assert.Equal(HttpStatusCode.OK, s2);
        var test = Assert.Single(site.Smtp.Messages);
        Assert.Equal("Test email from SaySerné", test.Subject);

        var (_, reg, _) = await site.Host.RegisterAsync(); await site.Drain();
        var (reader, _, _) = await site.Host.CreateStaffAsync("email.read");
        var (ls, outbox) = await reader.GetAsync("/api/admin/email/outbox?status=sent");
        Assert.Equal(HttpStatusCode.OK, ls);
        var item = outbox.GetProperty("items")[0];
        Assert.Equal(("verify_email", "sent"), (item.GetProperty("eventCode").GetString(), item.GetProperty("status").GetString()));
        Assert.Contains(reg.ToLowerInvariant(), item.GetProperty("to").GetString()!.ToLowerInvariant());
        Assert.DoesNotContain("token=", outbox.GetRawText());                                                                    // never the content (and so never a link)
        Assert.False(item.TryGetProperty("bodyText", out _) || item.TryGetProperty("bodyHtml", out _));
        var audit = (await site.Admin.GetAsync("/api/admin/audit?action=email.test&pageSize=5")).Body;
        Assert.True(audit.GetProperty("total").GetInt32() >= 1);
        Assert.True((await site.Admin.GetAsync("/api/admin/audit?action=email.test.failed")).Body.GetProperty("total").GetInt32() >= 1);
    }
}
