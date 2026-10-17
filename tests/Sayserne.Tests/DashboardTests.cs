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
