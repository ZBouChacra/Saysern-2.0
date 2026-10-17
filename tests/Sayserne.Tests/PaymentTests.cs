using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sayserne.Api;
using Sayserne.Api.Ads;
using Sayserne.Api.Payments;

namespace Sayserne.Tests;

public class PaymentTests
{
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);
    private static string Day(int offset = 0) => Today.AddDays(offset).ToString("yyyy-MM-dd");

    private sealed class Shop : IAsyncDisposable
    {
        public required AdTests.SceneHandle Scene { get; init; }
        public IsolatedHost Iso => Scene.Iso;
        public TestClient Admin => Scene.Admin;
        public ApiHost Host => Scene.Host;
        public static async Task<Shop> StartAsync() { var scene = await AdTests.SceneHandle.StartAsync(); await scene.Setting("Ads.DefaultMaxAds", "100"); return new Shop { Scene = scene }; }

        public async Task<long> AdBatch(string title = "Spring sale", int from = 0, int to = 4, decimal? fee = 6m)
        {
            var ad = await Scene.NewAd(title, fee: fee);
            var (s, b) = await Scene.Book(ad, from, to); Assert.Equal(HttpStatusCode.Created, s);
            return b.GetProperty("id").GetInt64();
        }
        public Task<(HttpStatusCode, JsonElement)> PayAd(long batch, object? billing = null) =>
            Admin.PostAsync($"/api/admin/ads/batches/{batch}/status", billing ?? new { status = "paid", billedTo = "Acme Co", method = "cash" });
        public async Task<JsonElement> Receipt(long id) { var (s, b) = await Admin.GetAsync($"/api/admin/payments/{id}"); Assert.Equal(HttpStatusCode.OK, s); return b; }
        public async Task<JsonElement> Receipts(string query = "") => (await Admin.GetAsync("/api/admin/payments?pageSize=100" + query)).Body;
        public async Task<long> PayAdOk(long batch, object? billing = null) { var (s, b) = await PayAd(batch, billing); Assert.True(s == HttpStatusCode.OK, $"pay: {s} {b}"); return (await Receipts()).GetProperty("items")[0].GetProperty("id").GetInt64(); }
        public async Task<string> NextCounter() => (await Iso.Db.ScalarAsync("SELECT next_value FROM billing_counters WHERE name = 'receipt'"))!;
        public async ValueTask DisposeAsync() => await Scene.DisposeAsync();
    }

    [Fact]
    public async Task Only_staff_with_the_right_permission_can_see_or_correct_receipts()
    {
        await using var s = await Shop.StartAsync();
        var receipt = await s.PayAdOk(await s.AdBatch());
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Host.NewClient().GetAsync("/api/admin/payments")).Status);
        var (nobody, _, _) = await s.Host.CreateStaffAsync("users.read");
        foreach (var path in new[] { "/api/admin/payments", "/api/admin/payments/summary", $"/api/admin/payments/{receipt}" }) Assert.Equal(HttpStatusCode.Forbidden, (await nobody.GetAsync(path)).Status);
        var (reader, _, _) = await s.Host.CreateStaffAsync("payments.read");
        foreach (var path in new[] { "/api/admin/payments", "/api/admin/payments/summary", $"/api/admin/payments/{receipt}" }) Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync(path)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsync($"/api/admin/payments/{receipt}/billing", new { billedTo = "Someone else" })).Status);
        var (manager, _, _) = await s.Host.CreateStaffAsync("payments.manage");
        Assert.Equal(HttpStatusCode.OK, (await manager.PutAsync($"/api/admin/payments/{receipt}/billing", new { billedTo = "Acme Holdings" })).Status);
        var (adsOnly, _, _) = await s.Host.CreateStaffAsync("ads.manage", "ads.read");                                        // someone who may book ads can take payment...
        var next = await s.AdBatch("Another", 10, 12);
        Assert.Equal(HttpStatusCode.OK, (await adsOnly.PostAsync($"/api/admin/ads/batches/{next}/status", new { status = "paid", billedTo = "Zed", method = "cash" })).Item1);
        Assert.Equal(HttpStatusCode.Forbidden, (await adsOnly.GetAsync("/api/admin/payments")).Status);                       // ...but cannot browse the receipts
        var (ordinary, _, _) = await s.Host.RegisterAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await ordinary.GetAsync("/api/admin/payments")).Status);
    }

    [Fact]
    public async Task Marking_a_batch_paid_needs_valid_billing_details_and_a_refused_payment_changes_nothing()
    {
        await using var s = await Shop.StartAsync();
        var batch = await s.AdBatch();
        async Task<(HttpStatusCode, string?)> Try(object body) { var (st, b) = await s.PayAd(batch, body); return (st, TestClient.Error(b)); }
        Assert.Equal((HttpStatusCode.BadRequest, "VALIDATION_FAILED"), await Try(new { status = "paid", method = "cash" }));                              // who paid?
        Assert.Equal((HttpStatusCode.BadRequest, "VALIDATION_FAILED"), await Try(new { status = "paid", billedTo = "   ", method = "cash" }));
        Assert.Equal((HttpStatusCode.BadRequest, "VALIDATION_FAILED"), await Try(new { status = "paid", billedTo = new string('x', 201), method = "cash" }));
        Assert.Equal((HttpStatusCode.BadRequest, "VALIDATION_FAILED"), await Try(new { status = "paid", billedTo = "Acme" }));                           // how?
        Assert.Equal((HttpStatusCode.BadRequest, "INVALID_PAYMENT_METHOD"), await Try(new { status = "paid", billedTo = "Acme", method = "bitcoin" }));
        Assert.Equal((HttpStatusCode.BadRequest, "INVALID_DATES"), await Try(new { status = "paid", billedTo = "Acme", method = "cash", receivedOn = Day(2) }));   // not received in the future
        Assert.Equal("pending", (await s.Admin.GetAsync($"/api/admin/ads/batches?adId={(await s.Admin.GetAsync("/api/admin/ads")).Body[0].GetProperty("id").GetInt64()}")).Body[0].GetProperty("status").GetString());
        Assert.Equal("0", await s.Iso.Db.ScalarAsync("SELECT COUNT(*) FROM payments"));
        Assert.Equal("1", await s.NextCounter());                                                                                                          // no receipt number was used
        Assert.Equal(HttpStatusCode.OK, (await s.Admin.PostAsync($"/api/admin/ads/batches/{batch}/status", new { status = "cancelled" })).Item1);          // cancelling needs no billing details
        Assert.Equal("0", await s.Iso.Db.ScalarAsync("SELECT COUNT(*) FROM payments"));
    }

    [Fact]
    public async Task The_database_refuses_a_bad_receipt_even_if_the_API_check_were_bypassed()
    {
        await using var s = await Shop.StartAsync();
        var batch = await s.AdBatch();
        var ads = s.Host.App.Services.GetRequiredService<IAdRepository>();     // straight to the data layer: no endpoint validation in front
        async Task<string> Code(BillingInput b) => (await Assert.ThrowsAsync<AppException>(() => ads.SetBatchStatusAsync(1, batch, "paid", b))).Code;
        Assert.Equal("BILLING_INVALID", await Code(new BillingInput("   ", null, "cash", null, null, null)));
        var tooLongRefused = false;
        try { await ads.SetBatchStatusAsync(1, batch, "paid", new BillingInput(new string('x', 201), null, "cash", null, null, null)); } catch (Exception) { tooLongRefused = true; }   // too long: MySQL itself refuses
        Assert.True(tooLongRefused);
        Assert.Equal("INVALID_PAYMENT_METHOD", await Code(new BillingInput("Acme", null, "bitcoin", null, null, null)));
        Assert.Equal("INVALID_PAYMENT_METHOD", await Code(new BillingInput("Acme", null, null, null, null, null)));
        Assert.Equal("INVALID_DATES", await Code(new BillingInput("Acme", null, "cash", null, Today.AddDays(3), null)));
        Assert.Equal("0", await s.Iso.Db.ScalarAsync("SELECT COUNT(*) FROM payments"));
        Assert.Equal("1", await s.NextCounter());
        Assert.Equal("pending", (await ads.BatchesAsync(null, null)).Single().Status);                                  // refused payments change nothing
    }

    [Fact]
    public async Task A_receipt_is_a_snapshot_with_the_batch_the_money_the_payer_and_how_it_was_paid()
    {
        await using var s = await Shop.StartAsync();
        var batch = await s.AdBatch("Spring sale", 0, 4, 6m);                                                          // 5 days at 6.00
        var id = await s.PayAdOk(batch, new { status = "paid", billedTo = "  Acme Co  ", billedDetails = "Hamra Street, Beirut\nVAT 123", method = "bank_transfer", reference = "TT-4411", receivedOn = Day(-1), notes = "Paid in two parts" });
        var r = await s.Receipt(id);
        Assert.Equal(("SAY-000001", "ad", "issued", "USD"), (r.GetProperty("receiptNumber").GetString(), r.GetProperty("kind").GetString(), r.GetProperty("status").GetString(), r.GetProperty("currency").GetString()));
        Assert.Equal((5, 6m, 30m, 0m, 0m, 30m), (r.GetProperty("quantity").GetInt32(), r.GetProperty("unitPrice").GetDecimal(), r.GetProperty("subtotal").GetDecimal(), r.GetProperty("vatPercent").GetDecimal(), r.GetProperty("vatAmount").GetDecimal(), r.GetProperty("total").GetDecimal()));
        Assert.Contains("Spring sale", r.GetProperty("description").GetString()); Assert.Contains("Home page banner", r.GetProperty("description").GetString()); Assert.Contains(Day(0), r.GetProperty("description").GetString());
        Assert.Equal(("Acme Co", "Hamra Street, Beirut\nVAT 123", "Bank transfer", "TT-4411", Day(-1), "Paid in two parts"),
            (r.GetProperty("billedTo").GetString(), r.GetProperty("billedDetails").GetString(), r.GetProperty("methodName").GetString(), r.GetProperty("reference").GetString(), r.GetProperty("receivedOn").GetString(), r.GetProperty("notes").GetString()));
        Assert.Equal("SaySerné", r.GetProperty("companyName").GetString());                                            // the company details come from Settings

        // nothing that changes later alters what was issued
        await s.Scene.Setting("Billing.VatPercent", "20"); await s.Scene.Setting("Billing.ReceiptPrefix", "INV/"); await s.Scene.Setting("Billing.CompanyName", "New Name Ltd");
        await s.Admin.PutAsync("/api/admin/ads/pricing", new { position = "home_banner", dailyFee = 99m });
        var again = await s.Receipt(id);
        Assert.Equal(("SAY-000001", 0m, 30m), (again.GetProperty("receiptNumber").GetString(), again.GetProperty("vatPercent").GetDecimal(), again.GetProperty("total").GetDecimal()));
        Assert.Equal("New Name Ltd", again.GetProperty("companyName").GetString());                                    // (only the printed letterhead follows the current setting)
        var next = await s.PayAdOk(await s.AdBatch("Next", 20, 21, 5m));
        var second = await s.Receipt(next);
        Assert.Equal(("INV/000002", 20m, 2m, 12m), (second.GetProperty("receiptNumber").GetString(), second.GetProperty("vatPercent").GetDecimal(), second.GetProperty("vatAmount").GetDecimal(), second.GetProperty("total").GetDecimal()));
        Assert.Equal(HttpStatusCode.NotFound, (await s.Admin.GetAsync("/api/admin/payments/99999999")).Status);
    }

    [Fact]
    public async Task VAT_is_a_setting_recorded_on_each_receipt_and_rounded_to_cents()
    {
        await using var s = await Shop.StartAsync();
        await s.Scene.Setting("Billing.VatPercent", "11.5");
        var batch = await s.AdBatch("Rounding", 0, 0, 33.33m);                                                         // one day at 33.33
        var r = await s.Receipt(await s.PayAdOk(batch));
        Assert.Equal((33.33m, 3.83m, 37.16m, 11.5m), (r.GetProperty("subtotal").GetDecimal(), r.GetProperty("vatAmount").GetDecimal(), r.GetProperty("total").GetDecimal(), r.GetProperty("vatPercent").GetDecimal()));
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Admin.PutAsync("/api/admin/settings/Billing.VatPercent", new { value = "101" })).Status);   // a rate is a percentage
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Admin.PutAsync("/api/admin/settings/Billing.ReceiptDigits", new { value = "2" })).Status);
    }

    [Fact]
    public async Task Receipt_numbers_are_consecutive_follow_the_settings_and_a_failed_payment_never_uses_one()
    {
        await using var s = await Shop.StartAsync();
        await s.Scene.Setting("Billing.ReceiptDigits", "4");
        var numbers = new List<string>();
        for (var i = 0; i < 3; i++) numbers.Add((await s.Receipt(await s.PayAdOk(await s.AdBatch("Ad " + i, i * 10, i * 10 + 1)))).GetProperty("receiptNumber").GetString()!);
        Assert.Equal("SAY-0001,SAY-0002,SAY-0003", string.Join(",", numbers));

        // a premium payment that cannot get its days is refused AFTER the receipt was prepared: the whole thing is rolled back, so no number is burned
        await s.Admin.PutAsync("/api/admin/settings/Premium.HorizonDays", new { value = "30" });
        Assert.Equal(HttpStatusCode.OK, (await s.Admin.PutAsync("/api/admin/premium/pricing", new { countryCode = "LB", maxPremium = 1 })).Status);
        long Listing(string first) => 0;
        async Task<long> Pro(string first) { var (p, _, _) = await s.Host.RegisterProfessionalAsync(first, "Pay", "Beirut", "LB"); return await s.Host.AddProfessionAsync(p, extra: new() { ["city"] = "Beirut", ["country"] = "LB" }); }
        async Task<long> Book(long listing, int days) { var (st, b) = await s.Admin.PostAsync("/api/admin/premium/batches", new { professionId = listing, from = Day(0), to = Day(days - 1) }); Assert.Equal(HttpStatusCode.Created, st); return b.GetProperty("id").GetInt64(); }
        var first = await Book(await Pro("Ann"), 60); var second = await Book(await Pro("Ben"), 10);
        Assert.Equal(HttpStatusCode.OK, (await s.Admin.PostAsync($"/api/admin/premium/batches/{first}/status", new { status = "paid", billedTo = "Ann", method = "cash" })).Item1);      // takes SAY-0004
        var (st2, body) = await s.Admin.PostAsync($"/api/admin/premium/batches/{second}/status", new { status = "paid", billedTo = "Ben", method = "cash" });
        Assert.Equal((HttpStatusCode.Conflict, "PREMIUM_NO_CAPACITY"), (st2, TestClient.Error(body)));
        Assert.Equal("5", await s.NextCounter());
        Assert.Equal("4", await s.Iso.Db.ScalarAsync("SELECT COUNT(*) FROM payments"));
        Assert.Equal("pending", (await s.Admin.GetAsync("/api/admin/premium/batches")).Body.EnumerateArray().Single(b => b.GetProperty("id").GetInt64() == second).GetProperty("status").GetString());
        Assert.Equal("1-2-3-4", await s.Iso.Db.ScalarAsync("SELECT GROUP_CONCAT(seq ORDER BY seq SEPARATOR '-') FROM payments"));                                                       // no gap
        _ = Listing("x");
    }

    [Fact]
    public async Task Many_payments_at_the_same_moment_get_consecutive_numbers_with_no_duplicates_and_no_gaps()
    {
        await using var s = await Shop.StartAsync();
        var batches = new List<long>();
        for (var i = 0; i < 16; i++) batches.Add(await s.AdBatch("Race " + i, i * 3, i * 3 + 1));
        var gate = new TaskCompletionSource();
        var tasks = batches.Select(b => Task.Run(async () => { await gate.Task; return (await s.PayAd(b)).Item1; })).ToList();
        await Task.Delay(150); gate.SetResult();
        foreach (var code in await Task.WhenAll(tasks)) Assert.Equal(HttpStatusCode.OK, code);
        Assert.Equal(string.Join("-", Enumerable.Range(1, 16)), await s.Iso.Db.ScalarAsync("SELECT GROUP_CONCAT(seq ORDER BY seq SEPARATOR '-') FROM payments"));
        Assert.Equal("16", await s.Iso.Db.ScalarAsync("SELECT COUNT(DISTINCT receipt_number) FROM payments"));
        Assert.Equal("17", await s.NextCounter());
    }

    [Fact]
    public async Task A_batch_has_one_receipt_and_cancelling_a_paid_batch_voids_it_but_keeps_its_number()
    {
        await using var s = await Shop.StartAsync();
        var batch = await s.AdBatch();
        var id = await s.PayAdOk(batch);
        JsonElement BatchRow() => s.Admin.GetAsync("/api/admin/ads/batches").GetAwaiter().GetResult().Body.EnumerateArray().Single(b => b.GetProperty("id").GetInt64() == batch);
        Assert.Equal("SAY-000001", BatchRow().GetProperty("receiptNumber").GetString());                                                                  // the batch list shows its receipt
        Assert.Equal((HttpStatusCode.Conflict, "INVALID_TRANSITION"), ((await s.PayAd(batch)).Item1, TestClient.Error((await s.PayAd(batch)).Item2)));   // paid twice is refused...
        Assert.Equal("1", await s.Iso.Db.ScalarAsync("SELECT COUNT(*) FROM payments"));                                                                  // ...so there is never a second receipt
        Assert.Equal(HttpStatusCode.OK, (await s.Admin.PostAsync($"/api/admin/ads/batches/{batch}/status", new { status = "cancelled" })).Item1);
        var r = await s.Receipt(id);
        Assert.Equal(("void", "SAY-000001"), (r.GetProperty("status").GetString(), r.GetProperty("receiptNumber").GetString()));
        Assert.False(BatchRow().TryGetProperty("receiptNumber", out var shown) && shown.ValueKind == JsonValueKind.String);                           // a void receipt is no longer shown on the batch
        Assert.False(string.IsNullOrWhiteSpace(r.GetProperty("voidReason").GetString())); Assert.NotNull(r.GetProperty("voidedAt").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await s.PayAd(batch)).Item1);                                                                              // a cancelled batch cannot be paid again
        var fresh = await s.PayAdOk(await s.AdBatch("Fresh", 10, 11));
        Assert.Equal("SAY-000002", (await s.Receipt(fresh)).GetProperty("receiptNumber").GetString());                                                    // the voided number is never reused
        var pendingOnly = await s.AdBatch("Never paid", 20, 21);
        Assert.Equal(HttpStatusCode.OK, (await s.Admin.PostAsync($"/api/admin/ads/batches/{pendingOnly}/status", new { status = "cancelled" })).Item1);
        Assert.Equal("2", await s.Iso.Db.ScalarAsync("SELECT COUNT(*) FROM payments"));                                                                  // cancelling an unpaid batch creates nothing
        var actions = (await s.Admin.GetAsync("/api/admin/audit?pageSize=100")).Body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("action").GetString()).ToHashSet();
        foreach (var expected in new[] { "payments.issue", "payments.void" }) Assert.True(actions.Contains(expected), $"missing {expected}");
    }

    [Fact]
    public async Task Only_who_a_receipt_is_made_out_to_can_be_corrected_never_the_money_or_the_number_and_it_is_audited()
    {
        await using var s = await Shop.StartAsync();
        var id = await s.PayAdOk(await s.AdBatch(), new { status = "paid", billedTo = "Acme Cp", method = "cash", notes = "first" });
        var before = await s.Receipt(id);
        Assert.Equal("VALIDATION_FAILED", TestClient.Error((await s.Admin.PutAsync($"/api/admin/payments/{id}/billing", new { billedTo = "  " })).Body));
        Assert.Equal("VALIDATION_FAILED", TestClient.Error((await s.Admin.PutAsync($"/api/admin/payments/{id}/billing", new { billedTo = new string('x', 201) })).Body));
        Assert.Equal(HttpStatusCode.NotFound, (await s.Admin.PutAsync("/api/admin/payments/99999999/billing", new { billedTo = "X" })).Status);
        var (st, after) = await s.Admin.PutAsync($"/api/admin/payments/{id}/billing", new { billedTo = "Acme Corp", billedDetails = "Tax id 55", notes = "corrected" });
        Assert.Equal(HttpStatusCode.OK, st);
        Assert.Equal(("Acme Corp", "Tax id 55", "corrected"), (after.GetProperty("billedTo").GetString(), after.GetProperty("billedDetails").GetString(), after.GetProperty("notes").GetString()));
        foreach (var field in new[] { "receiptNumber", "subtotal", "vatAmount", "total", "quantity", "unitPrice", "method", "receivedOn", "description", "issuedAt", "status" })
            Assert.Equal(before.GetProperty(field).ToString(), after.GetProperty(field).ToString());                       // everything else is untouched
        var audit = (await s.Admin.GetAsync("/api/admin/audit?action=payments.billing")).Body.GetProperty("items")[0];
        Assert.Equal(("Acme Cp", "Acme Corp"), (audit.GetProperty("details").GetProperty("old").GetString(), audit.GetProperty("details").GetProperty("new").GetString()));
    }

    [Fact]
    public async Task The_list_filters_by_status_kind_date_and_text_newest_first_with_paging_and_the_summary_excludes_voids()
    {
        await using var s = await Shop.StartAsync();
        var a = await s.AdBatch("Alpha ad", 0, 1, 10m); var b = await s.AdBatch("Beta ad", 5, 6, 20m); var c = await s.AdBatch("Gamma ad", 10, 11, 30m);
        await s.PayAdOk(a, new { status = "paid", billedTo = "Alice Ltd", method = "cash", receivedOn = Day(-10), reference = "REF-A" });
        await s.PayAdOk(b, new { status = "paid", billedTo = "Bob Trading", method = "bank_transfer", receivedOn = Day(-2) });
        await s.PayAdOk(c, new { status = "paid", billedTo = "Carol & Sons", method = "cash", receivedOn = Day(0) });
        var (p, _, _) = await s.Host.RegisterProfessionalAsync("Prem", "Ium", "Beirut", "LB");
        var listing = await s.Host.AddProfessionAsync(p, extra: new() { ["city"] = "Beirut", ["country"] = "LB" });
        var pb = (await s.Admin.PostAsync("/api/admin/premium/batches", new { professionId = listing, from = Day(0), to = Day(2) })).Item2.GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.OK, (await s.Admin.PostAsync($"/api/admin/premium/batches/{pb}/status", new { status = "paid", billedTo = "Prem Ium", method = "card_external", receivedOn = Day(0) })).Item1);
        await s.Admin.PostAsync($"/api/admin/ads/batches/{b}/status", new { status = "cancelled" });                       // Bob's is voided

        async Task<string[]> Numbers(string q) => (await s.Receipts(q)).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("receiptNumber").GetString()!).ToArray();
        Assert.Equal(new[] { "SAY-000004", "SAY-000003", "SAY-000002", "SAY-000001" }, await Numbers(""));                   // newest first
        Assert.Equal(new[] { "SAY-000002" }, await Numbers("&status=void"));
        Assert.Equal(new[] { "SAY-000004" }, await Numbers("&kind=premium"));
        Assert.Equal(new[] { "SAY-000003", "SAY-000001" }, await Numbers("&kind=ad&status=issued"));
        Assert.Equal(new[] { "SAY-000003", "SAY-000002", "SAY-000004" }.OrderBy(x => x).ToArray(), (await Numbers($"&from={Day(-2)}")).OrderBy(x => x).ToArray());   // by the date the money was received
        Assert.Equal(new[] { "SAY-000001" }, await Numbers($"&to={Day(-5)}"));
        Assert.Equal(new[] { "SAY-000003" }, await Numbers("&search=Carol"));                                                 // by who paid (even with an & in the name)
        Assert.Equal(new[] { "SAY-000001" }, await Numbers("&search=REF-A"));                                                 // by reference
        Assert.Equal(new[] { "SAY-000004" }, await Numbers("&search=Premium%20listing"));                                     // by what was bought
        Assert.Equal(new[] { "SAY-000002" }, await Numbers("&search=000002"));                                                // by number
        Assert.Empty(await Numbers("&search=nothing-like-this"));
        Assert.Empty(await Numbers("&search=%25"));                                                                           // a wildcard is just text
        var page = (await s.Admin.GetAsync("/api/admin/payments?pageSize=2&page=2")).Body;
        Assert.Equal((4, 2), (page.GetProperty("total").GetInt32(), page.GetProperty("items").GetArrayLength()));

        var summary = (await s.Admin.GetAsync("/api/admin/payments/summary")).Body;
        Assert.Equal(1, summary.GetProperty("voided").GetInt64());
        Assert.Equal(110m, summary.GetProperty("total").GetDecimal());                                                        // Alpha 2 x 10 + Gamma 2 x 30 + premium 3 x 10 (default fee); Bob's voided 40 is excluded
        var rows = summary.GetProperty("rows").EnumerateArray().ToDictionary(r => $"{r.GetProperty("kind").GetString()}/{r.GetProperty("method").GetString()}", r => r.GetProperty("total").GetDecimal());
        Assert.Equal(new Dictionary<string, decimal> { ["ad/cash"] = 80m, ["premium/card_external"] = 30m }, rows);
        var recent = (await s.Admin.GetAsync($"/api/admin/payments/summary?from={Day(-3)}")).Body;
        Assert.Equal(90m, recent.GetProperty("total").GetDecimal());                                                         // Gamma 60 + premium 30 (Alpha was received 10 days ago, Bob's is void)
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Admin.GetAsync($"/api/admin/payments?search={new string('x', 101)}")).Status);
        var premium = await s.Receipt((await s.Receipts("&kind=premium")).GetProperty("items")[0].GetProperty("id").GetInt64());
        Assert.Equal((3, 10m), (premium.GetProperty("quantity").GetInt32(), premium.GetProperty("unitPrice").GetDecimal()));   // a premium receipt: days x fee per day
        Assert.StartsWith("Premium listing:", premium.GetProperty("description").GetString());
    }
}
