using System.Net;
using System.Text.Json;

namespace Sayserne.Tests;

public class DashboardTests
{
    private static async Task<JsonElement> Dash(TestClient c) { var (s, b) = await c.GetAsync("/api/admin/dashboard"); Assert.Equal(HttpStatusCode.OK, s); return b; }
    private static string[] Sections(JsonElement d) => d.EnumerateObject().Select(p => p.Name).OrderBy(x => x).ToArray();

    [Fact]
    public async Task Each_staff_member_sees_only_the_groups_their_role_allows_and_nobody_else_sees_anything()
    {
        await using var s = await AdTests.SceneHandle.StartAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Host.NewClient().GetAsync("/api/admin/dashboard")).Status);
        var (user, _, _) = await s.Host.RegisterAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.GetAsync("/api/admin/dashboard")).Status);                      // an ordinary account is not staff
        var (blank, _, _) = await s.Host.CreateStaffAsync("settings.read");
        Assert.Empty(Sections(await Dash(blank)));                                                                            // staff with nothing relevant: an empty page, not an error
        var (people, _, _) = await s.Host.CreateStaffAsync("users.read");
        Assert.Equal(new[] { "users" }, Sections(await Dash(people)));
        var (money, _, _) = await s.Host.CreateStaffAsync("payments.read", "audit.read", "email.read");
        Assert.Equal(new[] { "payments", "recentActivity", "system" }, Sections(await Dash(money)));
        var system = (await Dash(money)).GetProperty("system");
        Assert.False(system.TryGetProperty("failedJobs", out var fj) && fj.ValueKind == JsonValueKind.Number);               // inside "system" only what they may see: e-mails yes, jobs no
        Assert.True(system.GetProperty("failedEmails").GetInt64() >= 0);
        Assert.Equal(new[] { "ads", "contact", "payments", "premium", "recentActivity", "reviews", "system", "users" }, Sections(await Dash(s.Admin)).Where(x => x != "").OrderBy(x => x).ToArray());   // a super admin sees every group
    }

    [Fact]
    public async Task The_numbers_match_what_actually_happened()
    {
        await using var s = await AdTests.SceneHandle.StartAsync();
        var before = await Dash(s.Admin);
        var usersBefore = before.GetProperty("users").GetProperty("total").GetInt64(); var prosBefore = before.GetProperty("users").GetProperty("professionals").GetInt64();
        await s.Host.RegisterAsync(); await s.Host.RegisterAsync();
        var (pro, _, _) = await s.Host.RegisterProfessionalAsync("Dash", "Pro", "Dashtown", "LB");
        var listing = await s.Host.AddProfessionAsync(pro, extra: new() { ["city"] = "Dashtown", ["country"] = "LB" });
        var after = (await Dash(s.Admin)).GetProperty("users");
        Assert.Equal((usersBefore + 3, prosBefore + 1, 3L), (after.GetProperty("total").GetInt64(), after.GetProperty("professionals").GetInt64(), after.GetProperty("last7Days").GetInt64() - before.GetProperty("users").GetProperty("last7Days").GetInt64()));
        Assert.True(after.GetProperty("listings").GetInt64() >= 1);

        var form = s.Host.NewClient();
        await form.PostAsync("/api/contact", new { name = "A", email = "a@example.test", subject = "s", message = "m" });
        await form.PostAsync("/api/contact", new { name = "B", email = "b@example.test", subject = "s", message = "m" });
        await form.PostAsync("/api/contact", new { name = "Bot", email = "bot@example.test", subject = "s", message = "m", website = "x" });
        var contact = (await Dash(s.Admin)).GetProperty("contact");
        Assert.Equal((2L, 2L), (contact.GetProperty("unanswered").GetInt64(), contact.GetProperty("total").GetInt64()));    // spam is not counted

        var ad = await s.NewAd("Dash ad"); var (bs, batch) = await s.Book(ad, 0, 1);                                        // 2 days at the default 5.00
        Assert.Equal(HttpStatusCode.Created, bs);
        Assert.Equal(1L, (await Dash(s.Admin)).GetProperty("ads").GetProperty("pendingBatches").GetInt64());
        Assert.Equal(HttpStatusCode.OK, (await s.Admin.PostAsync($"/api/admin/ads/batches/{batch.GetProperty("id").GetInt64()}/status", new { status = "paid", billedTo = "Dash Co", method = "cash" })).Item1);
        var d = await Dash(s.Admin);
        Assert.Equal((1L, 0L), (d.GetProperty("ads").GetProperty("runningToday").GetInt64(), d.GetProperty("ads").GetProperty("pendingBatches").GetInt64()));
        var pay = d.GetProperty("payments");
        Assert.Equal((1L, 10m, 10m, "USD"), (pay.GetProperty("receiptsLast30Days").GetInt64(), pay.GetProperty("receivedLast30Days").GetDecimal(), pay.GetProperty("receivedThisMonth").GetDecimal(), pay.GetProperty("currency").GetString()));
        Assert.True(d.GetProperty("recentActivity").EnumerateArray().Any(a => a.GetProperty("action").GetString() == "payments.issue"));

        await ApiHost.MakePremiumAsync(s.Admin, listing);
        Assert.Equal(1L, (await Dash(s.Admin)).GetProperty("premium").GetProperty("activeToday").GetInt64());
        var total = (await Dash(s.Admin)).GetProperty("payments");
        Assert.Equal((2L, 80m), (total.GetProperty("receiptsLast30Days").GetInt64(), total.GetProperty("receivedLast30Days").GetDecimal()));   // the premium receipt (7 days at 10.00 = 70) is added to the ad's 10.00
    }
}

public class DashboardYearTests
{
    private static async Task<JsonElement> Year(TestClient c, string? year = null) { var (s, b) = await c.GetAsync("/api/admin/dashboard/year" + (year is null ? "" : "?year=" + year)); Assert.Equal(HttpStatusCode.OK, s); return b; }
    private static JsonElement Month(JsonElement y, int month) => y.GetProperty("months").EnumerateArray().Single(m => m.GetProperty("month").GetInt32() == month);

    [Fact]
    public async Task The_year_view_shows_twelve_months_with_only_the_columns_each_staff_member_may_see()
    {
        await using var s = await AdTests.SceneHandle.StartAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Host.NewClient().GetAsync("/api/admin/dashboard/year")).Status);
        var (user, _, _) = await s.Host.RegisterAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.GetAsync("/api/admin/dashboard/year")).Status);
        var (blank, _, _) = await s.Host.CreateStaffAsync("settings.read");
        var none = await Year(blank);
        Assert.Equal(12, none.GetProperty("months").GetArrayLength());
        Assert.Empty(none.GetProperty("columns").EnumerateArray());                                                          // nothing to show, but not an error
        var (money, _, _) = await s.Host.CreateStaffAsync("payments.read");
        Assert.Equal(new[] { "receipts", "received", "voided" }, (await Year(money)).GetProperty("columns").EnumerateArray().Select(c => c.GetString()!).ToArray());
        var (people, _, _) = await s.Host.CreateStaffAsync("users.read", "reviews.read", "contact.read");
        Assert.Equal(new[] { "newUsers", "appointments", "reviews", "contactMessages" }, (await Year(people)).GetProperty("columns").EnumerateArray().Select(c => c.GetString()!).ToArray());
        Assert.Equal(7, (await Year(s.Admin)).GetProperty("columns").GetArrayLength());                                      // a super admin sees all of them
    }

    [Fact]
    public async Task Counts_land_in_the_right_month_and_year_and_the_list_of_years_includes_every_year_with_data()
    {
        await using var s = await AdTests.SceneHandle.StartAsync();
        var thisYear = DateTime.UtcNow.Year; var thisMonth = DateTime.UtcNow.Month;
        var before = Month(await Year(s.Admin), thisMonth).GetProperty("newUsers").GetInt64();
        var (_, _, a) = await s.Host.RegisterAsync(); var (_, _, b) = await s.Host.RegisterAsync(); var (_, _, c) = await s.Host.RegisterAsync();
        var other = thisMonth == 3 ? 4 : 3;                                                                                   // a month that is not this one
        await s.Iso.Db.ExecAsync($"UPDATE users SET created_at = '2025-{other:00}-10 12:00:00' WHERE id = {a}");               // one person who joined in another month of 2025...
        await s.Iso.Db.ExecAsync($"UPDATE users SET created_at = '2025-{thisMonth:00}-10 12:00:00' WHERE id = {b}");           // ...and one in the SAME month of last year: it must not be counted this year
        Assert.Equal(before + 1, Month(await Year(s.Admin), thisMonth).GetProperty("newUsers").GetInt64());                  // only the third is this year
        var y2025 = await Year(s.Admin, "2025");
        Assert.Equal(1, Month(y2025, other).GetProperty("newUsers").GetInt64());
        Assert.Equal(1, Month(y2025, thisMonth).GetProperty("newUsers").GetInt64());
        Assert.Equal(0, Month(y2025, thisMonth == 12 ? 1 : thisMonth + 1).GetProperty("newUsers").GetInt64());

        var ad = await s.NewAd("Yearly"); var (st, batch) = await s.Book(ad, 0, 1);
        Assert.Equal(HttpStatusCode.Created, st);
        Assert.Equal(HttpStatusCode.OK, (await s.Admin.PostAsync($"/api/admin/ads/batches/{batch.GetProperty("id").GetInt64()}/status", new { status = "paid", billedTo = "Year Co", method = "cash" })).Item1);
        var now = Month(await Year(s.Admin), thisMonth);
        Assert.Equal((1L, 10m), (now.GetProperty("receipts").GetInt64(), now.GetProperty("received").GetDecimal()));
        await s.Iso.Db.ExecAsync("UPDATE payments SET received_on = '2025-06-15'");                                           // as if it had been paid last June
        var june = Month(await Year(s.Admin, "2025"), 6);
        Assert.Equal((1L, 10m), (june.GetProperty("receipts").GetInt64(), june.GetProperty("received").GetDecimal()));
        Assert.Equal(0, Month(await Year(s.Admin), thisMonth).GetProperty("receipts").GetInt64());                            // and no longer counts this year

        var years = (await Year(s.Admin)).GetProperty("years").EnumerateArray().Select(y => y.GetInt32()).ToList();
        Assert.Equal(years.OrderByDescending(y => y).ToList(), years);                                                        // newest first
        Assert.True(years.Contains(thisYear) && years.Contains(2025));
        _ = c;
    }

    [Theory]
    [InlineData("abc")] [InlineData("1999")] [InlineData("3000")] [InlineData("-5")] [InlineData("2026.5")] [InlineData("20 26")]
    public async Task A_year_that_is_not_a_sensible_year_is_refused(string year)
    {
        await using var iso = await IsolatedHost.StartAsync();
        var (admin, _, _) = await iso.Host.CreateAdminAsync();
        var (st, body) = await admin.GetAsync("/api/admin/dashboard/year?year=" + Uri.EscapeDataString(year));
        Assert.Equal(HttpStatusCode.BadRequest, st); Assert.True(body.GetProperty("errors").TryGetProperty("year", out _));
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/admin/dashboard/year.csv?year=" + Uri.EscapeDataString(year))).Status);
    }

    [Fact]
    public async Task The_spreadsheet_is_a_csv_with_a_byte_order_mark_twelve_rows_and_only_the_permitted_columns()
    {
        await using var s = await AdTests.SceneHandle.StartAsync();
        var ad = await s.NewAd("Csv"); var (_, batch) = await s.Book(ad, 0, 1);
        await s.Admin.PostAsync($"/api/admin/ads/batches/{batch.GetProperty("id").GetInt64()}/status", new { status = "paid", billedTo = "Csv Co", method = "cash" });
        await s.Iso.Db.ExecAsync("UPDATE payments SET received_on = '2025-06-15'");
        var res = await s.Admin.GetBytesAsync("/api/admin/dashboard/year.csv?year=2025");
        Assert.Equal(HttpStatusCode.OK, res.Status);
        Assert.Equal("text/csv", res.ContentHeaders.ContentType!.MediaType);
        Assert.Contains("sayserne-2025.csv", res.ContentHeaders.ContentDisposition!.ToString());
        var bytes = res.Bytes; Assert.True(bytes.Length > 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);        // Excel reads it as UTF-8
        var lines = System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(13, lines.Length);
        Assert.Equal("Year,Month,New users,Appointments requested,Reviews,Contact messages,Receipts issued,Amount received (USD),Receipts voided", lines[0]);
        var june = lines[6].Split(',');                                                                                        // the June row
        Assert.Equal(("2025", "June", "0", "0", "0", "0", "1", "0"), (june[0], june[1], june[2], june[3], june[4], june[5], june[6], june[8]));
        Assert.Equal(10m, decimal.Parse(june[7], System.Globalization.CultureInfo.InvariantCulture));
        var (money, _, _) = await s.Host.CreateStaffAsync("payments.read");
        var onlyMoney = System.Text.Encoding.UTF8.GetString((await money.GetBytesAsync("/api/admin/dashboard/year.csv?year=2025")).Bytes).Split("\r\n")[0].TrimStart('\uFEFF');
        Assert.Equal("Year,Month,Receipts issued,Amount received (USD),Receipts voided", onlyMoney);                              // no columns for areas they may not see
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Host.NewClient().GetBytesAsync("/api/admin/dashboard/year.csv")).Status);
    }
}
