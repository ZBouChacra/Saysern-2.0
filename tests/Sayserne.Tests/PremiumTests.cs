using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sayserne.Api.Jobs;

namespace Sayserne.Tests;

public class PremiumTests
{
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);
    private static string Day(int offset = 0) => Today.AddDays(offset).ToString("yyyy-MM-dd");

    private sealed class Scene : IAsyncDisposable
    {
        public required IsolatedHost Iso { get; init; }
        public required TestClient Admin { get; init; }
        public ApiHost Host => Iso.Host;
        public static async Task<Scene> StartAsync() { var iso = await IsolatedHost.StartAsync(); var (admin, _, _) = await iso.Host.CreateAdminAsync(); return new Scene { Iso = iso, Admin = admin }; }

        /// <summary>A professional with one listing in a place. Returns the user id and the listing (profession) id.</summary>
        public async Task<(long User, long Listing)> Listing(string first, string country = "LB", string? city = "Beirut", int years = 5, string? serviceName = null)
        {
            var (p, _, id) = await Host.RegisterProfessionalAsync(first, "Premium", city, country);
            var extra = new Dictionary<string, object?> { ["city"] = city, ["country"] = country, ["yearsOfExperience"] = years };
            return (id, serviceName is null ? await Host.AddProfessionAsync(p, extra: extra) : await Host.AddProfessionAsync(p, serviceName, extra));
        }
        public async Task<(HttpStatusCode, JsonElement)> Book(long listing, int from = 0, int days = 5) =>
            await Admin.PostAsync("/api/admin/premium/batches", new { professionId = listing, from = Day(from), to = Day(from + days - 1) });
        public async Task<long> BookId(long listing, int from = 0, int days = 5) { var (s, b) = await Book(listing, from, days); Assert.True(s == HttpStatusCode.Created, $"{s} {b}"); return b.GetProperty("id").GetInt64(); }
        public async Task<(HttpStatusCode, JsonElement)> SetStatus(long batch, string status) => await Admin.PostAsync($"/api/admin/premium/batches/{batch}/status", new { status, billedTo = "Test customer", method = "cash" });   // paying records a payment (a receipt), so it needs who paid and how
        public async Task<long> Pay(long listing, int from = 0, int days = 5) { var id = await BookId(listing, from, days); var (s, b) = await SetStatus(id, "paid"); Assert.True(s == HttpStatusCode.OK, $"pay: {s} {b}"); return id; }
        public async Task Limit(string country, int? max, string? city = null, decimal? fee = null) =>
            Assert.Equal(HttpStatusCode.OK, (await Admin.PutAsync("/api/admin/premium/pricing", new { countryCode = country, city, maxPremium = max, dailyFee = fee })).Status);
        public async Task<JsonElement> Batch(long id) => (await Admin.GetAsync("/api/admin/premium/batches")).Body.EnumerateArray().Single(b => b.GetProperty("id").GetInt64() == id);
        /// <summary>(first day held, last day held, days held) of a batch, from the days actually allocated.</summary>
        public async Task<(string? First, string? Last, int Days)> Held(long batch)
        {
            var b = await Batch(batch);
            var first = await Iso.Db.ScalarAsync($"SELECT DATE_FORMAT(MIN(day), '%Y-%m-%d') FROM premium_days WHERE batch_id = {batch}");
            return (first, b.TryGetProperty("effectiveTo", out var e) ? e.GetString() : null, b.GetProperty("daysAssigned").GetInt32());
        }
        public async Task<bool> PremiumToday(long listing) => (await Host.NewClient().GetAsync("/api/professionals/search?pageSize=50")).Body.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("professionId").GetInt64() == listing).GetProperty("isPremium").GetBoolean();
        public async ValueTask DisposeAsync() => await Iso.DisposeAsync();
    }

    [Fact]
    public async Task Only_staff_with_the_right_permission_can_see_or_manage_premium()
    {
        await using var s = await Scene.StartAsync();
        var (user, _, _) = await s.Host.RegisterProfessionalAsync("Pia", "Premium", "Beirut", "LB");                  // a professional, with a client session of their own
        var listing = await s.Host.AddProfessionAsync(user, extra: new() { ["city"] = "Beirut", ["country"] = "LB" });
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Host.NewClient().GetAsync("/api/admin/premium/batches")).Status);
        var (nobody, _, _) = await s.Host.CreateStaffAsync("users.read");
        Assert.Equal(HttpStatusCode.Forbidden, (await nobody.GetAsync("/api/admin/premium/batches")).Status);
        var (reader, _, _) = await s.Host.CreateStaffAsync("premium.read");
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/admin/premium/batches")).Status);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/admin/premium/pricing")).Status);
        var found = (await reader.GetAsync("/api/admin/premium/find?search=Pia")).Body;
        Assert.Equal(listing, found[0].GetProperty("professionId").GetInt64());
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync("/api/admin/premium/batches", new { professionId = listing, from = Day(0), to = Day(2) })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsync("/api/admin/premium/pricing", new { countryCode = "LB", maxPremium = 3 })).Status);
        var (manager, _, _) = await s.Host.CreateStaffAsync("premium.manage");
        Assert.Equal(HttpStatusCode.Created, (await manager.PostAsync("/api/admin/premium/batches", new { professionId = listing, from = Day(0), to = Day(2) })).Status);
        var (ordinary, _, _) = await s.Host.RegisterAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await ordinary.GetAsync("/api/admin/premium/batches")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.PostAsync("/api/admin/premium/batches", new { professionId = listing, from = Day(0), to = Day(2) })).Status);   // the professional cannot buy their own premium
    }

    [Fact]
    public async Task A_batch_needs_a_listing_with_a_country_sensible_dates_and_no_overlap_and_fixes_its_fee()
    {
        await using var s = await Scene.StartAsync();
        var (_, listing) = await s.Listing("Una");
        async Task<string?> Err(object body) { var (st, b) = await s.Admin.PostAsync("/api/admin/premium/batches", body); return st == HttpStatusCode.Created ? null : TestClient.Error(b); }
        Assert.Equal("INVALID_DATES", await Err(new { professionId = listing, from = Day(3), to = Day(2) }));
        Assert.Equal("INVALID_DATES", await Err(new { professionId = listing, from = Day(-1), to = Day(3) }));
        Assert.Equal("INVALID_DATES", await Err(new { professionId = listing, from = Day(0), to = Day(400) }));
        Assert.Equal("VALIDATION_FAILED", await Err(new { from = Day(0), to = Day(2) }));
        Assert.Equal("VALIDATION_FAILED", await Err(new { professionId = listing, from = Day(0) }));
        Assert.Equal("PROFESSION_NOT_FOUND", await Err(new { professionId = 99999999, from = Day(0), to = Day(2) }));
        var (_, noCountry) = await s.Listing("Nia", country: null!, city: null);
        Assert.Equal("PREMIUM_NEEDS_COUNTRY", await Err(new { professionId = noCountry, from = Day(0), to = Day(2) }));

        await s.Limit("LB", null, fee: 6m); await s.Limit("LB", null, "Beirut", fee: 8m);
        var batch = await s.BookId(listing, 0, 4);
        var row = await s.Batch(batch);
        Assert.Equal((8m, 32m, "pending", 4), (row.GetProperty("feePerDay").GetDecimal(), row.GetProperty("totalFee").GetDecimal(), row.GetProperty("status").GetString(), row.GetProperty("entitledDays").GetInt32()));   // city price beats country price
        Assert.Equal("Una Premium", row.GetProperty("professionalName").GetString());
        await s.Limit("LB", null, "Beirut", fee: 100m);
        Assert.Equal(8m, (await s.Batch(batch)).GetProperty("feePerDay").GetDecimal());                                // a later price change never alters a booked batch
        Assert.Equal("PREMIUM_OVERLAP", await Err(new { professionId = listing, from = Day(2), to = Day(6) }));        // the same listing cannot be booked twice for the same days
        Assert.Null(await Err(new { professionId = listing, from = Day(4), to = Day(8) }));                             // the day after is fine
        var (_, other) = await s.Listing("Ola", city: "Jounieh");
        Assert.Equal(6m, (await s.Batch(await s.BookId(other, 0, 1))).GetProperty("feePerDay").GetDecimal());          // another city of Lebanon: the country price
        var (_, france) = await s.Listing("Fay", "FR", "Paris");
        Assert.Equal(10m, (await s.Batch(await s.BookId(france, 0, 1))).GetProperty("feePerDay").GetDecimal());        // no price for France: the default from Settings
    }

    [Fact]
    public async Task Only_a_paid_batch_makes_a_listing_premium_and_only_on_its_days()
    {
        await using var s = await Scene.StartAsync();
        var (user, listing) = await s.Listing("Pam");
        var pending = await s.BookId(listing, 0, 3);
        Assert.False(await s.PremiumToday(listing));                                                                   // reserved is not premium
        var (_, bystander) = await s.Listing("By");
        await s.Pay(bystander);                                                                                         // ...not even when the country is recalculated for someone else's payment
        await s.Limit("LB", 9);                                                                                         // ...or for a changed limit
        Assert.False(await s.PremiumToday(listing));
        Assert.True(await s.PremiumToday(bystander));
        Assert.Equal(HttpStatusCode.OK, (await s.SetStatus(pending, "paid")).Item1);
        Assert.True(await s.PremiumToday(listing));
        var profile = (await s.Host.NewClient().GetAsync($"/api/professionals/{user}")).Body.GetProperty("profile");
        Assert.True(profile.GetProperty("isPremium").GetBoolean());                                                     // the public profile agrees
        Assert.Equal(HttpStatusCode.OK, (await s.SetStatus(pending, "cancelled")).Item1);
        Assert.False(await s.PremiumToday(listing));                                                                   // cancelled: gone at once

        var (_, later) = await s.Listing("Lee");
        await s.Pay(later, 3, 4);                                                                                       // paid, but it starts in three days
        Assert.False(await s.PremiumToday(later));
        var (_, past) = await s.Listing("Pat");
        var old = await s.Pay(past, 0, 3);
        await s.Iso.Db.ExecAsync($"UPDATE premium_days SET day = DATE_SUB(day, INTERVAL 10 DAY) WHERE batch_id = {old}");   // its days are now in the past
        Assert.False(await s.PremiumToday(past));
        Assert.Equal(HttpStatusCode.Conflict, (await s.SetStatus(old, "paid")).Item1);                                // paid is final
    }

    [Fact]
    public async Task A_batch_moves_from_pending_to_paid_or_cancelled_and_nothing_else()
    {
        await using var s = await Scene.StartAsync();
        var (_, listing) = await s.Listing("Sam");
        var id = await s.BookId(listing);
        async Task<(HttpStatusCode, string?)> Move(long b, string st) { var (code, body) = await s.SetStatus(b, st); return (code, TestClient.Error(body)); }
        Assert.Equal((HttpStatusCode.BadRequest, "INVALID_STATUS"), await Move(id, "pending"));
        Assert.Equal((HttpStatusCode.BadRequest, "INVALID_STATUS"), await Move(id, "refunded"));
        Assert.Equal((HttpStatusCode.NotFound, "BATCH_NOT_FOUND"), await Move(999999, "paid"));
        Assert.Equal(HttpStatusCode.OK, (await Move(id, "paid")).Item1);
        Assert.Equal((HttpStatusCode.Conflict, "INVALID_TRANSITION"), await Move(id, "paid"));
        Assert.Equal(HttpStatusCode.OK, (await Move(id, "cancelled")).Item1);                                          // a paid batch can still be cancelled
        Assert.Equal((HttpStatusCode.Conflict, "INVALID_TRANSITION"), await Move(id, "paid"));                        // cancelled is final
        Assert.Equal((HttpStatusCode.Conflict, "INVALID_TRANSITION"), await Move(id, "cancelled"));
    }

    [Fact]
    public async Task First_paid_keeps_its_days_and_later_batches_are_pushed_back_by_exactly_the_days_they_lost()
    {
        await using var s = await Scene.StartAsync();
        await s.Limit("LB", 2);                                                                                         // two premium listings per day in Lebanon
        var (_, a) = await s.Listing("Ann"); var (_, b) = await s.Listing("Ben"); var (_, c) = await s.Listing("Cal");
        // booked in the order c, a, b but PAID in the order a, b, c: payment order decides
        var bc = await s.BookId(c, 0, 5); var ba = await s.BookId(a, 0, 5); var bb = await s.BookId(b, 0, 5);
        await s.SetStatus(ba, "paid"); await s.SetStatus(bb, "paid");
        Assert.Equal((Day(0), Day(4), 5), await s.Held(ba));
        Assert.Equal((Day(0), Day(4), 5), await s.Held(bb));
        await s.SetStatus(bc, "paid");                                                                                  // the third finds days 0-4 full ...
        Assert.Equal((Day(5), Day(9), 5), await s.Held(bc));                                                            // ... and receives its 5 paid days straight after
        Assert.Equal((true, true, false), (await s.PremiumToday(a), await s.PremiumToday(b), await s.PremiumToday(c)));
        Assert.Equal((Day(0), Day(4), 5), await s.Held(ba));                                                            // the earlier payers are untouched

        await s.Limit("LB", 1);                                                                                         // the limit is lowered: the later payers lose days...
        Assert.Equal((Day(0), Day(4), 5), await s.Held(ba));                                                            // ...the first payer keeps them
        Assert.Equal((Day(5), Day(9), 5), await s.Held(bb));                                                            // ...and each is compensated: still exactly 5 days
        Assert.Equal((Day(10), Day(14), 5), await s.Held(bc));
        Assert.Equal((true, false, false), (await s.PremiumToday(a), await s.PremiumToday(b), await s.PremiumToday(c)));
        Assert.Equal(15, int.Parse((await s.Iso.Db.ScalarAsync("SELECT COUNT(*) FROM premium_days"))!));                // nobody lost days they paid for

        await s.Limit("LB", 3);                                                                                         // raised again: everyone holds their original days
        foreach (var batch in new[] { ba, bb, bc }) Assert.Equal((Day(0), Day(4), 5), await s.Held(batch));
        await s.Limit("LB", 1);
        Assert.Equal(HttpStatusCode.OK, (await s.SetStatus(ba, "cancelled")).Item1);                                    // the first payer cancels: the others move up
        Assert.Equal((Day(0), Day(4), 5), await s.Held(bb));
        Assert.Equal((Day(5), Day(9), 5), await s.Held(bc));
    }

    [Fact]
    public async Task Days_that_are_already_past_are_never_rewritten_when_a_limit_changes()
    {
        await using var s = await Scene.StartAsync();
        await s.Limit("LB", 5);
        var (_, a) = await s.Listing("Ann"); var (_, b) = await s.Listing("Ben");
        await s.Pay(a, 0, 6); var bb = await s.Pay(b, 0, 6);
        // pretend Ben's batch started two days ago: his first two days are history (yesterday and the day before)
        await s.Iso.Db.ExecAsync($"DELETE FROM premium_days WHERE batch_id = {bb} AND day IN (UTC_DATE(), DATE_ADD(UTC_DATE(), INTERVAL 1 DAY))");
        await s.Iso.Db.ExecAsync($"INSERT INTO premium_days (batch_id, day, profession_id) SELECT {bb}, DATE_SUB(UTC_DATE(), INTERVAL n DAY), profession_id FROM (SELECT 1 n UNION SELECT 2) x JOIN premium_batches pb ON pb.id = {bb}");
        await s.Iso.Db.ExecAsync($"UPDATE premium_batches SET date_from = DATE_SUB(UTC_DATE(), INTERVAL 2 DAY) WHERE id = {bb}");
        await s.Limit("LB", 1);                                                                                         // would not have allowed Ben those days
        Assert.Equal("2", await s.Iso.Db.ScalarAsync($"SELECT COUNT(*) FROM premium_days WHERE batch_id = {bb} AND day < UTC_DATE()"));   // history is kept
        Assert.Equal(6, (await s.Batch(bb)).GetProperty("daysAssigned").GetInt32());                                   // 2 past + 4 more, still exactly the 6 paid for
        Assert.Equal("4", await s.Iso.Db.ScalarAsync($"SELECT COUNT(*) FROM premium_days WHERE batch_id = {bb} AND day >= UTC_DATE()"));
    }

    [Fact]
    public async Task A_batch_that_cannot_be_given_all_its_days_within_the_horizon_cannot_be_marked_paid()
    {
        await using var s = await Scene.StartAsync();
        Assert.Equal(HttpStatusCode.OK, (await s.Admin.PutAsync("/api/admin/settings/Premium.HorizonDays", new { value = "30" })).Status);
        await s.Limit("LB", 1);
        var (_, a) = await s.Listing("Ann"); var (_, b) = await s.Listing("Ben");
        await s.Pay(a, 0, 60);                                                                                          // Ann holds every day for the next 60
        var bb = await s.BookId(b, 0, 10);
        var (status, body) = await s.SetStatus(bb, "paid");
        Assert.Equal((HttpStatusCode.Conflict, "PREMIUM_NO_CAPACITY"), (status, TestClient.Error(body)));
        Assert.Equal("pending", (await s.Batch(bb)).GetProperty("status").GetString());                                // nothing changed: still pending, no days taken
        Assert.Equal("0", await s.Iso.Db.ScalarAsync($"SELECT COUNT(*) FROM premium_days WHERE batch_id = {bb}"));
        Assert.False(await s.PremiumToday(b));
    }

    [Fact]
    public async Task A_city_batch_counts_against_its_country_and_a_city_can_have_a_limit_of_its_own_countries_are_independent()
    {
        await using var s = await Scene.StartAsync();
        await s.Limit("LB", 2);
        var (_, beirut1) = await s.Listing("B1", city: "Beirut"); var (_, jounieh) = await s.Listing("J1", city: "Jounieh"); var (_, beirut2) = await s.Listing("B2", city: "Beirut");
        var (_, paris) = await s.Listing("P1", "FR", "Paris");
        var b1 = await s.Pay(beirut1); var j1 = await s.Pay(jounieh); var b2 = await s.Pay(beirut2); var p1 = await s.Pay(paris);
        Assert.Equal((Day(0), Day(4), 5), await s.Held(b1)); Assert.Equal((Day(0), Day(4), 5), await s.Held(j1));      // Lebanon's two slots: a Beirut and a Jounieh listing
        Assert.Equal((Day(5), Day(9), 5), await s.Held(b2));                                                            // the third Lebanese listing waits, wherever in Lebanon it is
        Assert.Equal((Day(0), Day(4), 5), await s.Held(p1));                                                            // France has its own limit

        await s.Limit("LB", 5); await s.Limit("LB", 1, "Beirut");                                                       // Lebanon allows 5, but Beirut only 1
        Assert.Equal((Day(0), Day(4), 5), await s.Held(b1));
        Assert.Equal((Day(5), Day(9), 5), await s.Held(b2));                                                            // the second Beirut listing is pushed by the city limit
        Assert.Equal((Day(0), Day(4), 5), await s.Held(j1));                                                            // Jounieh is not affected by Beirut's limit
        var preview = (await s.Admin.GetAsync($"/api/admin/premium/capacity?country=LB&city=Beirut&from={Day(0)}&to={Day(1)}")).Body[0];
        Assert.Equal((2, 5, 1, 1), (preview.GetProperty("used").GetInt32(), preview.GetProperty("maxPremium").GetInt32(), preview.GetProperty("cityUsed").GetInt32(), preview.GetProperty("maxCity").GetInt32()));
        var resolved = (await s.Admin.GetAsync("/api/admin/premium/pricing/resolve?country=LB&city=Beirut")).Body;
        Assert.Equal((5, 1), (resolved.GetProperty("maxPerCountry").GetInt32(), resolved.GetProperty("maxPerCity").GetInt32()));
    }

    [Fact]
    public async Task Prices_and_limits_are_validated_audited_and_removable()
    {
        await using var s = await Scene.StartAsync();
        async Task<string?> Err(object body) => TestClient.Error((await s.Admin.PutAsync("/api/admin/premium/pricing", body)).Body);
        Assert.Equal("INVALID_PRICING", await Err(new { countryCode = "LB" }));
        Assert.Equal("INVALID_PRICING", await Err(new { countryCode = "LB", dailyFee = -1 }));
        Assert.Equal("INVALID_PRICING", await Err(new { countryCode = "LB", maxPremium = 0 }));
        Assert.Equal("INVALID_COUNTRY", await Err(new { countryCode = "ZZ", dailyFee = 1 }));
        Assert.Equal("VALIDATION_FAILED", await Err(new { dailyFee = 1 }));
        await s.Limit("LB", 3, fee: 9m);
        await s.Limit("LB", 3, fee: 7m);                                                                                // saving again replaces
        var rows = (await s.Admin.GetAsync("/api/admin/premium/pricing")).Body;
        Assert.Equal(1, rows.GetArrayLength());
        Assert.Equal(7m, rows[0].GetProperty("dailyFee").GetDecimal());
        Assert.Equal(HttpStatusCode.NoContent, (await s.Admin.DeleteAsync($"/api/admin/premium/pricing/{rows[0].GetProperty("id").GetInt64()}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Admin.DeleteAsync($"/api/admin/premium/pricing/{rows[0].GetProperty("id").GetInt64()}")).Status);
        Assert.Equal(10m, (await s.Admin.GetAsync("/api/admin/premium/pricing/resolve?country=LB")).Body.GetProperty("dailyFee").GetDecimal());   // back to the default
        var (_, l) = await s.Listing("Audit");
        await s.Pay(l);
        var actions = (await s.Admin.GetAsync("/api/admin/audit?pageSize=200")).Body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("action").GetString()).ToHashSet();
        foreach (var expected in new[] { "premium.pricing.save", "premium.pricing.delete", "premium.batch.create", "premium.batch.paid" }) Assert.True(actions.Contains(expected), $"missing {expected}");
    }

    [Fact]
    public async Task Many_batches_paid_at_the_same_moment_never_exceed_the_limit_and_each_still_gets_its_days()
    {
        await using var s = await Scene.StartAsync();
        await s.Limit("LB", 3);
        var batches = new List<long>();
        for (var i = 0; i < 8; i++) { var (_, l) = await s.Listing("Race" + i, city: "City" + (i % 3)); batches.Add(await s.BookId(l, 0, 4)); }
        var gate = new TaskCompletionSource();
        var results = await Task.WhenAll(batches.Select(id => Task.Run(async () => { await gate.Task; return (await s.SetStatus(id, "paid")).Item1; })).ToList().Select(async t => { gate.TrySetResult(); return await t; }));
        foreach (var r in results) Assert.Equal(HttpStatusCode.OK, r);
        Assert.Equal(0, int.Parse((await s.Iso.Db.ScalarAsync("SELECT COUNT(*) FROM (SELECT day FROM premium_days GROUP BY day HAVING COUNT(*) > 3) x"))!));   // never more than 3 on any day
        foreach (var id in batches) Assert.Equal(4, (await s.Batch(id)).GetProperty("daysAssigned").GetInt32());                                                   // and everyone got all 4 paid days
    }

    [Fact]
    public async Task The_daily_job_repairs_the_allocation_and_runs_once_a_day()
    {
        await using var s = await Scene.StartAsync();
        var (_, l) = await s.Listing("Heal");
        var id = await s.Pay(l);
        await s.Iso.Db.ExecAsync($"DELETE FROM premium_days WHERE batch_id = {id}");                                   // something went wrong
        Assert.False(await s.PremiumToday(l));
        var scheduler = s.Host.App.Services.GetRequiredService<DigestScheduler>(); var runner = s.Host.App.Services.GetRequiredService<JobRunner>();
        await scheduler.RunOnceAsync(); await scheduler.RunOnceAsync();
        while (await runner.RunOnceAsync() > 0) { }
        Assert.True(await s.PremiumToday(l));
        Assert.Equal("1", await s.Iso.Db.ScalarAsync("SELECT COUNT(*) FROM jobs WHERE job_type = 'premium.reallocate'"));
    }

    [Fact]
    public async Task Search_ranks_premium_then_featured_then_the_rest_and_inside_each_group_the_visitors_city_then_country_then_elsewhere()
    {
        await using var s = await Scene.StartAsync();
        // identical in rating and experience except for where they are, so that only the place decides the order inside a group
        var (uPb, pb) = await s.Listing("PremBeirut"); var (uPj, pj) = await s.Listing("PremJounieh", city: "Jounieh"); var (uPf, pf) = await s.Listing("PremParis", "FR", "Paris");
        var (uSb, sb) = await s.Listing("StarBeirut"); var (uSj, sj) = await s.Listing("StarJounieh", city: "Jounieh");
        var (_, nb) = await s.Listing("PlainBeirut"); var (_, nj) = await s.Listing("PlainJounieh", city: "Jounieh"); var (_, nf) = await s.Listing("PlainParis", "FR", "Paris");
        foreach (var l in new[] { pb, pj, pf }) await s.Pay(l);
        foreach (var u in new[] { uSb, uSj }) await s.Admin.PostAsync($"/api/admin/users/{u}/flags", new { isStarred = true });
        async Task<string[]> Order(string query) => (await s.Host.NewClient().GetAsync("/api/professionals/search?pageSize=50" + query)).Body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("firstName").GetString()!).ToArray();

        Assert.Equal(new[] { "PremBeirut", "PremJounieh", "PremParis", "StarBeirut", "StarJounieh", "PlainBeirut", "PlainJounieh", "PlainParis" }, await Order("&visitorCountry=LB&visitorCity=Beirut"));
        Assert.Equal(new[] { "PremJounieh", "PremBeirut", "PremParis", "StarJounieh", "StarBeirut", "PlainJounieh", "PlainBeirut", "PlainParis" }, await Order("&visitorCountry=LB&visitorCity=jounieh"));   // another city: its own listings first (any letter case)
        Assert.Equal(new[] { "PremParis", "PremBeirut", "PremJounieh", "StarBeirut", "StarJounieh", "PlainParis", "PlainBeirut", "PlainJounieh" }, await Order("&visitorCountry=FR&visitorCity=Paris"));
        var country = await Order("&visitorCountry=LB");                                                                // country only: the whole country before the rest
        Assert.Equal(new[] { "PremParis" }, country.Where(n => n == "PremParis").ToArray());
        Assert.True(Array.IndexOf(country, "PlainBeirut") < Array.IndexOf(country, "PlainParis") && Array.IndexOf(country, "PlainJounieh") < Array.IndexOf(country, "PlainParis"));
        Assert.True(Array.IndexOf(country, "PremBeirut") < Array.IndexOf(country, "StarBeirut") && Array.IndexOf(country, "PremParis") < Array.IndexOf(country, "StarBeirut"));   // premium still beats everything
        var none = await Order("");                                                                                     // no place: the place plays no part
        Assert.Equal(8, none.Length);
        Assert.Equal(8, (await Order("&visitorCountry=JP&visitorCity=Tokyo")).Length);                                   // a visitor place never hides anyone
        // a saved search is about what to find, never about where the visitor was
        var (alice, _, _) = await s.Host.RegisterAsync();
        var (st, alert) = await alice.PostAsync("/api/alerts", new { name = "x", searchCriteria = new { city = "Beirut", visitorCountry = "LB", visitorCity = "Beirut" }, frequency = "daily" });
        Assert.Equal(HttpStatusCode.Created, st);
        Assert.False(alert.GetProperty("searchCriteria").TryGetProperty("visitorCountry", out var vc) && vc.ValueKind != JsonValueKind.Null);
        _ = (uPb, uPj, uPf, nb, nj, nf);
    }
}
