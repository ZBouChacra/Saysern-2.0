using System.Net;
using Sayserne.Api.Data;

namespace Sayserne.Tests;

public class AvailabilityRuleTests
{
    private static AvailabilitySlot Slot(int day, string from, string to, long? profession = null) => new(profession, day, from, to);
    // Monday 2026-07-13 and Tuesday 2026-07-14 (Asia/Dubai is UTC+4 all year; Europe/Paris is UTC+2 in July, UTC+1 in January)
    private static DateTime Utc(int day, int hour, int minute = 0) => new(2026, 7, day, hour, minute, 0, DateTimeKind.Utc);
    private static readonly AvailabilitySlot[] DubaiMonday = { Slot(1, "09:00:00", "17:00:00") };

    [Theory]
    [InlineData(13, 5, 0, 60, true)]     // 09:00-10:00 Dubai: starts exactly when the window opens
    [InlineData(13, 12, 0, 60, true)]    // 16:00-17:00: ends exactly when it closes
    [InlineData(13, 12, 1, 60, false)]   // one minute past closing
    [InlineData(13, 4, 59, 60, false)]   // one minute before opening
    [InlineData(13, 8, 0, 480, false)]   // too long for the window
    [InlineData(14, 8, 0, 60, false)]    // Tuesday: no window
    [InlineData(13, 20, 30, 60, false)]  // 00:30 Tuesday in Dubai: the wrong day (and across no window)
    public void A_booking_must_fit_entirely_inside_a_window_in_the_professionals_zone(int day, int hour, int minute, int duration, bool expected) =>
        Assert.Equal(expected, AvailabilityRules.Allows(DubaiMonday, null, Utc(day, hour, minute), duration, "Asia/Dubai", "Asia/Beirut"));

    [Fact]
    public void The_same_instant_can_be_inside_in_one_zone_and_outside_in_another()
    {
        var moment = Utc(13, 6);     // 10:00 in Dubai, 08:00 in Paris
        Assert.True(AvailabilityRules.Allows(DubaiMonday, null, moment, 60, "Asia/Dubai", null));
        Assert.False(AvailabilityRules.Allows(DubaiMonday, null, moment, 60, "Europe/Paris", null));
        Assert.True(AvailabilityRules.Allows(DubaiMonday, null, Utc(13, 7), 60, "Europe/Paris", null));      // 09:00 in Paris
        // winter time moves Paris by an hour: 08:00Z is 09:00 in January but 10:00 in July
        Assert.True(AvailabilityRules.Allows(DubaiMonday, null, new DateTime(2026, 1, 12, 8, 0, 0, DateTimeKind.Utc), 60, "Europe/Paris", null));    // Monday 12 January, 09:00 Paris
        Assert.False(AvailabilityRules.Allows(DubaiMonday, null, new DateTime(2026, 1, 12, 7, 0, 0, DateTimeKind.Utc), 60, "Europe/Paris", null));   // 08:00 Paris
    }

    [Fact]
    public void Several_windows_a_day_and_windows_for_one_listing_only()
    {
        var slots = new[] { Slot(1, "09:00:00", "12:00:00"), Slot(1, "14:00:00", "17:00:00"), Slot(1, "18:00:00", "20:00:00", profession: 7) };
        Assert.True(AvailabilityRules.Allows(slots, 7, Utc(13, 5), 60, "Asia/Dubai", null));              // morning window
        Assert.False(AvailabilityRules.Allows(slots, 7, Utc(13, 9), 60, "Asia/Dubai", null));             // 13:00 lunch break
        Assert.True(AvailabilityRules.Allows(slots, 7, Utc(13, 10), 60, "Asia/Dubai", null));             // afternoon window
        Assert.True(AvailabilityRules.Allows(slots, 7, Utc(13, 14), 60, "Asia/Dubai", null));             // evening window: listing 7 only
        Assert.False(AvailabilityRules.Allows(slots, 8, Utc(13, 14), 60, "Asia/Dubai", null));            // another listing does not get it
        Assert.False(AvailabilityRules.Allows(slots, null, Utc(13, 14), 60, "Asia/Dubai", null));
    }

    [Fact]
    public void Nothing_set_means_always_open_and_a_booking_never_crosses_midnight_inside_a_window()
    {
        Assert.True(AvailabilityRules.Allows(Array.Empty<AvailabilitySlot>(), null, Utc(13, 3), 600, "Asia/Dubai", null));
        // hours that belong to listing 7 do not restrict listing 8: it has none of its own, so it is open
        Assert.True(AvailabilityRules.Allows(new[] { Slot(1, "09:00:00", "17:00:00", profession: 7) }, 8, Utc(13, 3), 60, "Asia/Dubai", null));
        var late = new[] { Slot(1, "20:00:00", "23:59:00") };
        Assert.False(AvailabilityRules.Allows(late, null, Utc(13, 19, 30), 60, "Asia/Dubai", null));      // 23:30 to 00:30
        Assert.True(AvailabilityRules.Allows(late, null, Utc(13, 17), 60, "Asia/Dubai", null));           // 21:00-22:00
    }

    [Fact]
    public void An_unknown_zone_falls_back_to_the_default_then_to_UTC()
    {
        Assert.True(AvailabilityRules.Allows(DubaiMonday, null, Utc(13, 5), 60, "Mars/Olympus", "Asia/Dubai"));
        Assert.True(AvailabilityRules.Allows(DubaiMonday, null, Utc(13, 10), 60, null, null));            // UTC: 10:00
        Assert.False(AvailabilityRules.Allows(DubaiMonday, null, Utc(13, 18), 60, null, null));           // 18:00 UTC
    }
}

public class AvailabilityBookingTests
{
    private static object Slot(int day, string from, string to) => new { dayOfWeek = day, startTime = from, endTime = to };
    private static object Book(long pro, DateTime startUtc, long? professionId = null, int minutes = 60) => new { professionalId = pro, professionId, startsAt = new DateTimeOffset(startUtc, TimeSpan.Zero), durationMinutes = minutes };

    /// <summary>The next Monday that is at least ten days away, at the given hour (UTC).</summary>
    private static DateTime NextMonday(int hourUtc, int addWeeks = 0) { var d = DateTime.UtcNow.Date.AddDays(10); while (d.DayOfWeek != DayOfWeek.Monday) d = d.AddDays(1); return d.AddDays(7 * addWeeks).AddHours(hourUtc); }

    [Fact]
    public async Task A_booking_outside_the_professionals_hours_is_refused_with_the_reason_and_the_setting_can_switch_it_off()
    {
        await using var iso = await IsolatedHost.StartAsync();
        var (admin, _, _) = await iso.Host.CreateAdminAsync();
        var (pro, _, proId) = await iso.Host.RegisterProfessionalAsync("Hana", "Hours", "Hourstown", "AE");
        var listing = await iso.Host.AddProfessionAsync(pro, extra: new() { ["city"] = "Hourstown", ["country"] = "AE" });
        var (client, _, _) = await iso.Host.RegisterAsync();

        // no hours set yet: always open
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsync("/api/appointments", Book(proId, NextMonday(23), listing))).Status);

        Assert.Equal(HttpStatusCode.OK, (await pro.PutAsync("/api/me/availability", new { slots = new[] { Slot(1, "09:00", "17:00") } })).Status);
        Assert.Equal(HttpStatusCode.OK, (await pro.PutAsync("/api/me/preferences", new { language = "en", timeZone = "Asia/Dubai" })).Status);   // Monday 09:00-17:00 in Dubai = 05:00-13:00 UTC
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsync("/api/appointments", Book(proId, NextMonday(5), listing))).Status);          // 09:00 Dubai
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsync("/api/appointments", Book(proId, NextMonday(11), listing))).Status);         // 15:00 Dubai
        var (st, body) = await client.PostAsync("/api/appointments", Book(proId, NextMonday(14), listing));                                       // 18:00 Dubai
        Assert.Equal((HttpStatusCode.Conflict, "OUTSIDE_AVAILABILITY"), (st, TestClient.Error(body)));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync("/api/appointments", Book(proId, NextMonday(5).AddDays(1), listing))).Status);   // Tuesday
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync("/api/appointments", Book(proId, NextMonday(12), listing, 120))).Status);     // 16:00-18:00 runs past closing
        // it is judged in the PROFESSIONAL's zone, whoever asks
        var (client2, _, _) = await iso.Host.RegisterAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await client2.PostAsync("/api/appointments", Book(proId, NextMonday(14), listing))).Status);

        // the refusal is not a validation error and nothing was booked
        Assert.Equal("3", await iso.Db.ScalarAsync($"SELECT COUNT(*) FROM appointments WHERE professional_id = {proId}"));

        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/admin/settings/Booking.EnforceAvailability", new { value = "false" })).Status);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsync("/api/appointments", Book(proId, NextMonday(14), listing))).Status);          // switched off: advisory again
        Assert.False((await iso.Host.NewClient().GetAsync("/api/config/public")).Body.GetProperty("Booking.EnforceAvailability").GetBoolean());
    }
}
