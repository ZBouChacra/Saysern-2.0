using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Sayserne.Api.Email;
using Sayserne.Api.Jobs;

namespace Sayserne.Tests;

public class AppointmentMaintenanceTests
{
    private sealed class Scene : IAsyncDisposable
    {
        public required MailSite Site { get; init; }
        public required TestClient Pro { get; init; } public required long ProId { get; init; } public required string ProEmail { get; init; }
        public required TestClient Client { get; init; } public required long ClientId { get; init; } public required string ClientEmail { get; init; }
        public required long Listing { get; init; }
        public ApiHost Host => Site.Host;

        public static async Task<Scene> StartAsync()
        {
            var site = await MailSite.StartAsync();
            await site.Set("Auth.RequireVerifiedEmail", "false");
            async Task Verify(string email)
            {
                var verify = (await site.Mail(m => m.To.Equals(email, StringComparison.OrdinalIgnoreCase) && m.Text.Contains("verify-email"))).Last();
                await site.Host.NewClient().PostAsync("/api/auth/verify-email", new { token = MailSite.TokenIn(verify) });
            }
            var (pro, proEmail, proId) = await site.Host.RegisterProfessionalAsync("Pia", "Zed", "Remindertown", "LB");
            var listing = await site.Host.AddProfessionAsync(pro, extra: new() { ["city"] = "Remindertown", ["country"] = "LB" });
            var (client, clientEmail, clientId) = await site.Host.RegisterAsync();
            await Verify(proEmail); await Verify(clientEmail);
            await site.Drain(); while (site.Smtp.Messages.TryDequeue(out _)) { }
            return new Scene { Site = site, Pro = pro, ProId = proId, ProEmail = proEmail, Client = client, ClientId = clientId, ClientEmail = clientEmail, Listing = listing };
        }

        /// <summary>A booking that starts in the future (so the API accepts it); then moved to the wanted moment, as if time had passed.</summary>
        public async Task<long> Booking(DateTime startUtc, string status, int offsetMinutes = 0)
        {
            var slot = ApiHost.FutureSlot().AddMinutes(offsetMinutes);
            var (st, b) = await Client.PostAsync("/api/appointments", new { professionalId = ProId, professionId = Listing, startsAt = slot, durationMinutes = 60 });
            Assert.Equal(HttpStatusCode.Created, st);
            var id = b.GetProperty("id").GetInt64();
            if (status == "approved") Assert.Equal(HttpStatusCode.OK, (await Pro.PostAsync($"/api/appointments/{id}/status", new { status = "approved" })).Status);
            if (status == "cancelled") Assert.Equal(HttpStatusCode.OK, (await Client.PostAsync($"/api/appointments/{id}/status", new { status = "cancelled" })).Status);
            await Site.Iso.Db.ExecAsync($"UPDATE appointments SET starts_at = '{startUtc:yyyy-MM-dd HH:mm:ss}', ends_at = '{startUtc.AddMinutes(60):yyyy-MM-dd HH:mm:ss}' WHERE id = {id}");
            return id;
        }

        /// <summary>One run of the maintenance job (each with its own key, as if it had been queued by different servers), then every queued e-mail.</summary>
        public async Task Run()
        {
            await Host.App.Services.GetRequiredService<IJobQueue>().EnqueueAsync(AppointmentMaintenanceJobHandler.JobType, null, dedupeKey: "test:" + Guid.NewGuid());
            await Site.Drain();
        }

        public async Task<string> Status(long id) => (await Site.Iso.Db.ScalarAsync($"SELECT status FROM appointments WHERE id = {id}"))!;
        public IReadOnlyList<(string To, string Subject, string Text)> Mails(string subjectPart) =>
            Site.Smtp.Messages.Where(m => m.Subject.Contains(subjectPart, StringComparison.OrdinalIgnoreCase)).Select(m => (m.To.ToLowerInvariant(), m.Subject, m.Text)).ToList();
        public async ValueTask DisposeAsync() => await Site.DisposeAsync();
    }

    [Fact]
    public async Task A_request_nobody_answered_is_cancelled_once_it_is_due_the_client_is_told_once_and_answered_or_future_ones_are_left_alone()
    {
        await using var s = await Scene.StartAsync();
        var now = DateTime.UtcNow;
        var future = await s.Booking(now.AddDays(3), "pending", 0);
        var overdue = await s.Booking(now.AddHours(-2), "pending", 120);
        var approvedPast = await s.Booking(now.AddHours(-3), "approved", 240);                       // approved and in the past: the professional will complete it, not us
        var cancelledPast = await s.Booking(now.AddHours(-4), "cancelled", 360);
        await s.Run();
        Assert.Equal(("pending", "cancelled", "approved", "cancelled"), (await s.Status(future), await s.Status(overdue), await s.Status(approvedPast), await s.Status(cancelledPast)));
        var told = s.Mails("was not answered");
        var mail = Assert.Single(told);
        Assert.Equal(s.ClientEmail.ToLowerInvariant(), mail.To);                                                          // the client, not the professional
        Assert.Contains("Pia Zed", mail.Text); Assert.Contains($"https://sayserne.test/professional/{s.ProId}", mail.Text);
        Assert.Matches(@"\(UTC[+-]\d{2}:\d{2}\)", mail.Text);                                                             // with the time in their zone
        Assert.Empty(s.Mails("Reminder:"));                                                                               // an appointment that has already started is not "reminded"
        await s.Run(); await s.Run();
        Assert.Single(s.Mails("was not answered"));                                                                       // later runs find nothing new

        await s.Site.Set("Booking.ExpirePending", "false");
        var another = await s.Booking(now.AddHours(-1), "pending", 480);
        await s.Run();
        Assert.Equal("pending", await s.Status(another));                                                                 // switched off: left as it was
    }

    [Fact]
    public async Task An_approved_appointment_reminds_both_people_once_in_their_own_time_zone_and_only_inside_the_window()
    {
        await using var s = await Scene.StartAsync();
        Assert.Equal(HttpStatusCode.OK, (await s.Client.PutAsync("/api/me/preferences", new { language = "en", timeZone = "Asia/Dubai" })).Status);
        Assert.Equal(HttpStatusCode.OK, (await s.Pro.PutAsync("/api/me/preferences", new { language = "en", timeZone = "America/New_York" })).Status);
        await s.Site.Drain(); while (s.Site.Smtp.Messages.TryDequeue(out _)) { }
        var start = DateTime.UtcNow.AddHours(5); start = new DateTime(start.Year, start.Month, start.Day, start.Hour, 0, 0, DateTimeKind.Utc);
        var soon = await s.Booking(start, "approved", 0);
        var later = await s.Booking(DateTime.UtcNow.AddHours(48), "approved", 120);
        var pending = await s.Booking(DateTime.UtcNow.AddHours(6), "pending", 240);
        var cancelled = await s.Booking(DateTime.UtcNow.AddHours(7), "cancelled", 360);
        await s.Run();
        var reminders = s.Mails("Reminder:");
        Assert.Equal(2, reminders.Count);                                                                                 // the one inside 24 hours, to both people
        var client = reminders.Single(m => m.To == s.ClientEmail.ToLowerInvariant()); var pro = reminders.Single(m => m.To == s.ProEmail.ToLowerInvariant());
        Assert.Contains(EmailTimes.Format(start, "Asia/Dubai", null), client.Text); Assert.Contains("Pia Zed", client.Text);   // the client sees the professional's name and THEIR time
        Assert.Contains(EmailTimes.Format(start, "America/New_York", null), pro.Text);                                    // the professional sees theirs
        Assert.NotEqual(client.Text.Split('\n').First(l => l.Contains("UTC")), pro.Text.Split('\n').First(l => l.Contains("UTC")));
        await s.Run(); await s.Run();
        Assert.Equal(2, s.Mails("Reminder:").Count);                                                                        // never repeated

        await s.Site.Set("Booking.ReminderHours", "72");
        await s.Run();
        Assert.Equal(4, s.Mails("Reminder:").Count);                                                                        // the window grew: the 48-hour one is now due
        Assert.Equal(0, s.Mails("Reminder:").Count(m => m.Text.Contains("pending")));
        _ = (soon, later, pending, cancelled);
    }

    [Fact]
    public async Task Reminders_can_be_switched_off_for_everyone_or_by_one_person_and_the_scheduler_queues_the_job_once_per_quarter_hour()
    {
        await using var s = await Scene.StartAsync();
        var start = DateTime.UtcNow.AddHours(4);
        await s.Booking(start, "approved", 0);
        await s.Site.Set("Booking.ReminderHours", "0");
        await s.Run();
        Assert.Empty(s.Mails("Reminder:"));                                                                                 // 0 = no reminders at all
        await s.Site.Set("Booking.ReminderHours", "24");
        Assert.Equal(HttpStatusCode.OK, (await s.Client.PutAsync("/api/me/notifications", new { @event = "booking_reminder", channel = "email", enabled = false })).Status);   // the client opts out
        await s.Run();
        var mails = s.Mails("Reminder:");
        Assert.Equal(new[] { s.ProEmail.ToLowerInvariant() }, mails.Select(m => m.To).ToArray());                          // only the professional is reminded

        var scheduler = s.Host.App.Services.GetRequiredService<DigestScheduler>();
        await scheduler.RunOnceAsync(); await scheduler.RunOnceAsync(); await scheduler.RunOnceAsync();
        Assert.Equal("1", await s.Site.Iso.Db.ScalarAsync($"SELECT COUNT(*) FROM jobs WHERE job_type = '{AppointmentMaintenanceJobHandler.JobType}' AND dedupe_key NOT LIKE 'test:%'"));
    }

    [Fact]
    public async Task Several_servers_running_it_at_the_same_moment_still_cancel_and_remind_each_person_exactly_once()
    {
        await using var s = await Scene.StartAsync();
        await s.Booking(DateTime.UtcNow.AddHours(-1), "pending", 0);
        await s.Booking(DateTime.UtcNow.AddHours(3), "approved", 120);
        var queue = s.Host.App.Services.GetRequiredService<IJobQueue>();
        for (var i = 0; i < 6; i++) await queue.EnqueueAsync(AppointmentMaintenanceJobHandler.JobType, null, dedupeKey: "race:" + i);
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => s.Site.Drain()));
        await s.Site.Drain();
        Assert.Single(s.Mails("was not answered"));
        Assert.Equal(2, s.Mails("Reminder:").Count);
    }
}
