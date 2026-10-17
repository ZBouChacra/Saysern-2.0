using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Sayserne.Api.Email;

namespace Sayserne.Tests;

public class TimeZoneTests
{
    private static readonly DateTime Summer = new(2026, 7, 15, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Winter = new(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("Asia/Dubai", "2026-07-15 14:00 (UTC+04:00)")]
    [InlineData("Asia/Kolkata", "2026-07-15 15:30 (UTC+05:30)")]       // a half-hour zone
    [InlineData("Europe/Paris", "2026-07-15 12:00 (UTC+02:00)")]       // summer time is taken into account
    [InlineData("America/New_York", "2026-07-15 06:00 (UTC-04:00)")]   // a negative offset
    [InlineData("UTC", "2026-07-15 10:00 (UTC+00:00)")]
    public void A_moment_is_shown_in_the_zone_with_its_offset_in_summer(string zone, string expected) => Assert.Equal(expected, EmailTimes.Format(Summer, zone, "Asia/Beirut"));

    [Theory]
    [InlineData("Europe/Paris", "2026-01-15 11:00 (UTC+01:00)")]       // ...and winter time
    [InlineData("America/New_York", "2026-01-15 05:00 (UTC-05:00)")]
    public void A_moment_is_shown_in_the_zone_with_its_offset_in_winter(string zone, string expected) => Assert.Equal(expected, EmailTimes.Format(Winter, zone, "Asia/Beirut"));

    [Fact]
    public void Without_a_zone_or_with_an_unknown_one_the_sites_default_is_used_and_if_that_fails_UTC()
    {
        var beirut = EmailTimes.Format(Summer, "Asia/Beirut", null);
        Assert.Equal(beirut, EmailTimes.Format(Summer, null, "Asia/Beirut"));
        Assert.Equal(beirut, EmailTimes.Format(Summer, "", "Asia/Beirut"));
        Assert.Equal(beirut, EmailTimes.Format(Summer, "Mars/Olympus", "Asia/Beirut"));                                   // an unknown name never stops an e-mail
        Assert.Equal("2026-07-15 10:00 (UTC+00:00)", EmailTimes.Format(Summer, "Mars/Olympus", "Also/Unknown"));
        Assert.Equal("2026-07-15 10:00 (UTC+00:00)", EmailTimes.Format(Summer, null, null));
    }

    [Fact]
    public void Every_x_utc_value_becomes_an_x_text_and_everything_else_is_left_alone()
    {
        var values = EmailTimes.Localise(new Dictionary<string, string> { ["when_utc"] = "2026-07-15T10:00:00.0000000Z", ["note"] = "hello", ["bad_utc"] = "not a date" }, "Asia/Dubai", "Asia/Beirut");
        Assert.Equal("2026-07-15 14:00 (UTC+04:00)", values["when"]);
        Assert.Equal("hello", values["note"]);
        Assert.False(values.ContainsKey("bad"));                                                                          // an unreadable value is simply not converted
    }

    [Fact]
    public async Task The_same_moment_reaches_two_people_in_their_own_zones_and_the_default_applies_to_whoever_chose_none()
    {
        await using var site = await MailSite.StartAsync();
        await site.Set("Auth.RequireVerifiedEmail", "false");
        async Task<(TestClient, string, long)> Verified()
        {
            var (c, email, id) = await site.Host.RegisterAsync();
            var verify = (await site.Mail(m => m.To.Equals(email, StringComparison.OrdinalIgnoreCase) && m.Text.Contains("verify-email"))).Last();
            await site.Host.NewClient().PostAsync("/api/auth/verify-email", new { token = MailSite.TokenIn(verify) });
            return (c, email, id);
        }
        var (dubai, dubaiEmail, dubaiId) = await Verified(); var (paris, parisEmail, parisId) = await Verified(); var (nobody, nobodyEmail, nobodyId) = await Verified();
        Assert.Equal(HttpStatusCode.OK, (await dubai.PutAsync("/api/me/preferences", new { language = "en", timeZone = "Asia/Dubai" })).Status);
        Assert.Equal(HttpStatusCode.OK, (await paris.PutAsync("/api/me/preferences", new { language = "en", timeZone = "Europe/Paris" })).Status);
        await site.Drain(); while (site.Smtp.Messages.TryDequeue(out _)) { }
        var notifier = site.Host.App.Services.GetRequiredService<INotifier>();
        var data = new Dictionary<string, string> { ["professional"] = "Dr Pro", ["when_utc"] = "2026-07-15T10:00:00.0000000Z", ["link"] = "https://sayserne.test/appointments" };
        foreach (var id in new[] { dubaiId, parisId, nobodyId }) await notifier.NotifyAsync("booking_approved", id, data);

        string TextFor(string email) => Assert.Single(site.Smtp.Messages.Where(m => m.To.Equals(email, StringComparison.OrdinalIgnoreCase) && m.Text.Contains("Dr Pro"))).Text;   // the approval e-mail
        await site.Drain();
        Assert.Contains("2026-07-15 14:00 (UTC+04:00)", TextFor(dubaiEmail));
        Assert.Contains("2026-07-15 12:00 (UTC+02:00)", TextFor(parisEmail));
        Assert.Contains(EmailTimes.Format(Summer, "Asia/Beirut", null), TextFor(nobodyEmail));                              // the site's default (Asia/Beirut)
    }
}
