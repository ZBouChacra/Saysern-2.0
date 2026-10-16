using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Sayserne.Api.Jobs;

namespace Sayserne.Tests;

public class DigestTests
{
    private sealed record Scene(MailSite Site, TestClient Client, string ClientEmail, long ClientId, string City, DigestScheduler Scheduler);

    private static async Task Verify(MailSite site, string email)
    {
        var mail = (await site.Mail(m => m.To.Equals(email, StringComparison.OrdinalIgnoreCase) && m.Text.Contains("verify-email"))).Last();
        await site.Host.NewClient().PostAsync("/api/auth/verify-email", new { token = MailSite.TokenIn(mail) });
    }

    private static async Task<Scene> SceneAsync(bool verified = true)
    {
        var site = await MailSite.StartAsync();
        var (client, email, id) = await site.Host.RegisterAsync();
        if (verified) await Verify(site, email);
        await site.Drain();                                           // let the registration e-mail go out, THEN forget it
        while (site.Smtp.Messages.TryDequeue(out _)) { }
        return new Scene(site, client, email, id, "Digestville" + Guid.NewGuid().ToString("N")[..6], site.Host.App.Services.GetRequiredService<DigestScheduler>());
    }

    private static async Task<long> NewProfessional(Scene s, string first, bool old = false)
    {
        var (pro, _, id) = await s.Site.Host.RegisterProfessionalAsync(first, "Digest", city: s.City);
        await s.Site.Host.AddProfessionAsync(pro, extra: new() { ["city"] = s.City });
        if (old) await s.Site.Iso.Db.ExecAsync($"UPDATE professions SET created_at = DATE_SUB(UTC_TIMESTAMP(3), INTERVAL 10 DAY) WHERE user_id = {id}");
        return id;
    }

    private static async Task<long> NewAlert(Scene s, string frequency = "daily", string? city = null, int ageDays = 2)
    {
        var (status, body) = await s.Client.PostAsync("/api/alerts", new { name = "Digest search", searchCriteria = new { city = city ?? s.City }, frequency });
        Assert.Equal(HttpStatusCode.Created, status);
        var id = body.GetProperty("id").GetInt64();
        await s.Site.Iso.Db.ExecAsync($"UPDATE alerts SET created_at = DATE_SUB(UTC_TIMESTAMP(3), INTERVAL {ageDays} DAY) WHERE id = {id}");
        return id;
    }

    private static async Task<FakeSmtp.Received[]> DigestsFor(Scene s)
    {
        await s.Scheduler.RunOnceAsync();
        return (await s.Site.Mail(m => m.To.Equals(s.ClientEmail, StringComparison.OrdinalIgnoreCase))).ToArray();
    }

    [Fact]
    public async Task A_due_search_emails_only_the_professionals_who_joined_since_and_then_starts_over()
    {
        var s = await SceneAsync();
        await using var _ = s.Site;
        await NewProfessional(s, "Olga", old: true);                         // matches the search, but joined long before it was saved
        var alert = await NewAlert(s);                                       // saved two days ago...
        var nadia = await NewProfessional(s, "Nadia"); var omar = await NewProfessional(s, "Omar"); // ...so these two are new
        var mail = Assert.Single(await DigestsFor(s));
        Assert.Contains("Digest search", mail.Subject);
        Assert.Contains("2 new professionals", mail.Text);
        Assert.Contains("Nadia Digest", mail.Text); Assert.Contains("Omar Digest", mail.Text);
        Assert.DoesNotContain("Olga", mail.Text);
        Assert.Contains("https://sayserne.test/alerts", mail.Text);
        Assert.NotNull(await s.Site.Iso.Db.ScalarAsync($"SELECT last_digest_at FROM alerts WHERE id = {alert}"));

        Assert.Equal(0, await s.Scheduler.RunOnceAsync());                   // not due again for a day
        Assert.Single(await s.Site.Mail(m => m.To.Equals(s.ClientEmail, StringComparison.OrdinalIgnoreCase)));

        // a day later: the last digest went out 25 hours ago, and Nadia and Omar joined 36 hours ago, i.e. AFTER the search was saved (2 days ago) but BEFORE that digest
        await s.Site.Iso.Db.ExecAsync($"UPDATE alerts SET last_digest_at = DATE_SUB(UTC_TIMESTAMP(3), INTERVAL 25 HOUR) WHERE id = {alert}");
        await s.Site.Iso.Db.ExecAsync("DELETE FROM jobs WHERE job_type = 'alerts.digest'");   // the next day-sized period (the real clock has not moved in a test)
        await s.Site.Iso.Db.ExecAsync($"UPDATE professions SET created_at = DATE_SUB(UTC_TIMESTAMP(3), INTERVAL 36 HOUR) WHERE user_id IN ({nadia}, {omar})");
        await NewProfessional(s, "Pascal");
        var second = (await DigestsFor(s)).Last();
        Assert.Contains("1 new professionals", second.Text);                  // only Pascal: the earlier two were already reported
        Assert.Contains("Pascal Digest", second.Text);
        Assert.DoesNotContain("Nadia", second.Text);
    }

    [Fact]
    public async Task When_nothing_new_has_arrived_no_email_is_sent_but_the_search_is_still_moved_forward()
    {
        var s = await SceneAsync();
        await using var _ = s.Site;
        await NewProfessional(s, "Olga", old: true);
        var alert = await NewAlert(s);
        Assert.Empty(await DigestsFor(s));
        Assert.NotNull(await s.Site.Iso.Db.ScalarAsync($"SELECT last_digest_at FROM alerts WHERE id = {alert}"));
        Assert.Equal("done", await s.Site.Iso.Db.ScalarAsync("SELECT status FROM jobs WHERE job_type = 'alerts.digest'"));
    }

    [Fact]
    public async Task A_search_is_due_only_after_its_frequency_and_one_job_is_queued_per_period_however_often_the_scheduler_runs()
    {
        var s = await SceneAsync();
        await using var _ = s.Site;
        var daily = await NewAlert(s, "daily", ageDays: 2);
        var weekly = await NewAlert(s, "weekly", ageDays: 2);
        var monthly = await NewAlert(s, "monthly", ageDays: 2);
        Assert.Equal(1, await s.Scheduler.RunOnceAsync());                    // only the daily one
        Assert.Equal(1, await s.Scheduler.RunOnceAsync());                    // still due (its job has not run yet) ...
        Assert.Equal("1", await s.Site.Iso.Db.ScalarAsync("SELECT COUNT(*) FROM jobs WHERE job_type = 'alerts.digest'"));   // ...but never queued twice
        await s.Site.Drain();
        await s.Site.Iso.Db.ExecAsync($"UPDATE alerts SET created_at = DATE_SUB(UTC_TIMESTAMP(3), INTERVAL 8 DAY) WHERE id = {weekly}");
        Assert.Equal(1, await s.Scheduler.RunOnceAsync());                    // now the weekly one; the monthly one still waits
        await s.Site.Iso.Db.ExecAsync($"UPDATE alerts SET created_at = DATE_SUB(UTC_TIMESTAMP(3), INTERVAL 31 DAY) WHERE id = {monthly}");
        await s.Site.Drain();
        Assert.Equal(1, await s.Scheduler.RunOnceAsync());
        Assert.True(daily > 0);
    }

    [Fact]
    public async Task An_admin_can_add_a_new_frequency_and_switch_one_off_without_a_deployment()
    {
        var s = await SceneAsync();
        await using var _ = s.Site;
        var code = "every_3_days";
        Assert.Equal(HttpStatusCode.OK, (await s.Site.Admin.PutAsync($"/api/admin/lookups/alert_frequency/values/{code}", new { sortOrder = 9, metadata = new { days = 3 }, names = new Dictionary<string, string> { ["en"] = "Every 3 days" } })).Status);
        var custom = await NewAlert(s, code, ageDays: 2);
        Assert.Equal(0, await s.Scheduler.RunOnceAsync());                    // 2 days is not yet 3
        await s.Site.Iso.Db.ExecAsync($"UPDATE alerts SET created_at = DATE_SUB(UTC_TIMESTAMP(3), INTERVAL 4 DAY) WHERE id = {custom}");
        Assert.Equal(1, await s.Scheduler.RunOnceAsync());
        await s.Site.Drain();
        await s.Site.Iso.Db.ExecAsync($"UPDATE alerts SET last_digest_at = DATE_SUB(UTC_TIMESTAMP(3), INTERVAL 9 DAY) WHERE id = {custom}");
        await s.Site.Admin.PutAsync($"/api/admin/lookups/alert_frequency/values/{code}", new { isActive = false, metadata = new { days = 3 }, names = new Dictionary<string, string> { ["en"] = "Every 3 days" } });
        Assert.Equal(0, await s.Scheduler.RunOnceAsync());                    // a frequency that is switched off is never due
    }

    [Fact]
    public async Task Opt_outs_unverified_addresses_and_other_criteria_are_respected()
    {
        var s = await SceneAsync();
        await using var _ = s.Site;
        await NewAlert(s, city: "Nowhere-" + s.City);                         // a search that matches nobody
        await NewProfessional(s, "Quentin");
        Assert.Empty(await DigestsFor(s));

        var other = await NewAlert(s);
        await s.Client.PutAsync("/api/me/notifications", new { @event = "alert_digest", channel = "email", enabled = false });
        await s.Site.Iso.Db.ExecAsync($"UPDATE alerts SET last_digest_at = DATE_SUB(UTC_TIMESTAMP(3), INTERVAL 2 DAY) WHERE id = {other}");
        await s.Site.Iso.Db.ExecAsync("DELETE FROM jobs WHERE job_type = 'alerts.digest'");
        Assert.Empty(await DigestsFor(s));                                    // opted out: nothing is sent...
        Assert.NotNull(await s.Site.Iso.Db.ScalarAsync($"SELECT last_digest_at FROM alerts WHERE id = {other} AND last_digest_at > DATE_SUB(UTC_TIMESTAMP(3), INTERVAL 1 HOUR)"));   // ...and the search still moves on

        var u = await SceneAsync(verified: false);
        await using var __ = u.Site;
        await NewProfessional(u, "Una");
        await NewAlert(u);
        await NewProfessional(u, "Uri");
        Assert.Empty(await DigestsFor(u));                                    // an unverified address never receives digests
    }

    [Fact]
    public async Task The_list_is_capped_by_a_setting_but_the_count_is_honest_and_the_language_is_the_recipients()
    {
        var s = await SceneAsync();
        await using var _ = s.Site;
        await s.Site.Set("Alerts.DigestMaxListings", "1");
        await s.Client.PutAsync("/api/me/profile", new { firstName = "A", lastName = "B", profileType = "customer", preferredLanguage = "ar" });
        await NewAlert(s);
        for (var i = 0; i < 3; i++) await NewProfessional(s, "Cap" + (char)('a' + i));
        var mail = Assert.Single(await DigestsFor(s));
        Assert.Contains("3", mail.Text);
        Assert.Equal(1, mail.Text.Split('\n').Count(l => l.StartsWith("• ")));
        Assert.Contains("محترفون جدد", mail.Subject);
    }
}
