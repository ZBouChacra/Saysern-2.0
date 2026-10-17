using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sayserne.Api.Jobs;

namespace Sayserne.Tests;

public class AdTests
{
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);
    private static string Day(int offset = 0) => Today.AddDays(offset).ToString("yyyy-MM-dd");

    public sealed class SceneHandle : IAsyncDisposable
    {
        public static async Task<SceneHandle> StartAsync() { var iso = await IsolatedHost.StartAsync(); var (admin, _, _) = await iso.Host.CreateAdminAsync(); return new SceneHandle { Iso = iso, Admin = admin }; }
        public required IsolatedHost Iso { get; init; }
        public required TestClient Admin { get; init; }
        public ApiHost Host => Iso.Host;
        public async Task<string> Image() { var (s, b) = await Admin.UploadAsync("/api/admin/ads/images", TestMedia.SmallJpeg(), "ad.jpg"); Assert.Equal(HttpStatusCode.Created, s); return b.GetProperty("id").GetString()!; }
        public async Task<long> NewAd(string title = "Ad", string position = "home_banner", string? country = null, string? city = null, string link = "https://example.test/landing", decimal? fee = null, string? image = null)
        {
            var (s, b) = await Admin.PostAsync("/api/admin/ads", new { title, position, imageFileId = image ?? await Image(), linkUrl = link, countryCode = country, city, feeOverride = fee });
            Assert.True(s == HttpStatusCode.Created, $"create ad: {s} {b}");
            return b.GetProperty("id").GetInt64();
        }
        public async Task<(HttpStatusCode, JsonElement)> Book(long ad, int from = 0, int to = 6) => await Admin.PostAsync($"/api/admin/ads/{ad}/batches", new { from = Day(from), to = Day(to) });
        public async Task<long> BookPaid(long ad, int from = 0, int to = 6)
        {
            var (s, b) = await Book(ad, from, to); Assert.Equal(HttpStatusCode.Created, s);
            var id = b.GetProperty("id").GetInt64();
            Assert.Equal(HttpStatusCode.OK, (await Admin.PostAsync($"/api/admin/ads/batches/{id}/status", new { status = "paid", billedTo = "Test customer", method = "cash" })).Item1);
            return id;
        }
        public async Task Setting(string key, string value) => Assert.Equal(HttpStatusCode.OK, (await Admin.PutAsync($"/api/admin/settings/{key}", new { value })).Status);
        public async Task Price(string position, string? country, string? city, decimal? fee, int? max) =>
            Assert.Equal(HttpStatusCode.OK, (await Admin.PutAsync("/api/admin/ads/pricing", new { position, countryCode = country, city, dailyFee = fee, maxAds = max })).Item1);
        public async Task<JsonElement> Served(string position, string? country = null, string? city = null, TestClient? who = null)
        {
            var q = $"/api/ads?position={position}" + (country is null ? "" : $"&country={country}") + (city is null ? "" : $"&city={Uri.EscapeDataString(city)}");
            return (await (who ?? Host.NewClient()).GetAsync(q)).Body;
        }
        public static string[] Titles(JsonElement served) => served.EnumerateArray().Select(a => a.GetProperty("title").GetString()!).ToArray();
        public async ValueTask DisposeAsync() => await Iso.DisposeAsync();
    }

    private static Task<SceneHandle> StartAsync() => SceneHandle.StartAsync();

    [Fact]
    public async Task Only_staff_with_the_right_permission_can_see_or_manage_ads_and_the_public_cannot_upload_ad_images()
    {
        await using var s = await StartAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Host.NewClient().GetAsync("/api/admin/ads")).Status);
        var (nobody, _, _) = await s.Host.CreateStaffAsync("users.read");
        Assert.Equal(HttpStatusCode.Forbidden, (await nobody.GetAsync("/api/admin/ads")).Status);
        var (reader, _, _) = await s.Host.CreateStaffAsync("ads.read");
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/admin/ads")).Status);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/admin/ads/stats")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync("/api/admin/ads", new { title = "x" })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsync("/api/admin/ads/pricing", new { position = "sidebar", dailyFee = 1 })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.UploadAsync("/api/admin/ads/images", TestMedia.SmallJpeg())).Status);
        var (manager, _, _) = await s.Host.CreateStaffAsync("ads.manage");
        Assert.Equal(HttpStatusCode.Created, (await manager.UploadAsync("/api/admin/ads/images", TestMedia.SmallJpeg())).Status);
        var (user, _, _) = await s.Host.RegisterAsync();
        Assert.Equal("VALIDATION_FAILED", TestClient.Error((await user.UploadAsync("/api/files?purpose=ad_image", TestMedia.SmallJpeg())).Body));   // not through the public upload
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.UploadAsync("/api/admin/ads/images", TestMedia.SmallJpeg())).Status);
    }

    [Fact]
    public async Task Ad_input_is_validated_and_the_picture_must_be_an_ad_image()
    {
        await using var s = await StartAsync();
        var image = await s.Image();
        var (pro, _, _) = await s.Host.RegisterAsync();
        var photo = (await pro.UploadAsync("/api/files?purpose=profile_photo", TestMedia.SmallJpeg())).Body.GetProperty("id").GetString()!;
        async Task<(HttpStatusCode, string?)> Try(object body) { var (st, b) = await s.Admin.PostAsync("/api/admin/ads", body); return (st, TestClient.Error(b)); }
        Assert.Equal((HttpStatusCode.BadRequest, "VALIDATION_FAILED"), await Try(new { position = "home_banner", imageFileId = image, linkUrl = "https://x.test" }));      // no title
        Assert.Equal((HttpStatusCode.BadRequest, "VALIDATION_FAILED"), await Try(new { title = "t", position = "home_banner", linkUrl = "https://x.test" }));              // no picture
        Assert.Equal((HttpStatusCode.BadRequest, "VALIDATION_FAILED"), await Try(new { title = new string('x', 151), position = "home_banner", imageFileId = image, linkUrl = "https://x.test" }));
        Assert.Equal((HttpStatusCode.BadRequest, "INVALID_AD_POSITION"), await Try(new { title = "t", position = "billboard", imageFileId = image, linkUrl = "https://x.test" }));
        foreach (var bad in new[] { "javascript:alert(1)", "ftp://x.test/a", "//x.test", "https://has space.test", "data:text/html,hi", "x.test" })
            Assert.Equal((HttpStatusCode.BadRequest, "INVALID_LINK"), await Try(new { title = "t", position = "home_banner", imageFileId = image, linkUrl = bad }));
        Assert.Equal((HttpStatusCode.NotFound, "FILE_NOT_FOUND"), await Try(new { title = "t", position = "home_banner", imageFileId = photo, linkUrl = "https://x.test" }));             // someone's profile photo is not an ad image
        Assert.Equal((HttpStatusCode.NotFound, "FILE_NOT_FOUND"), await Try(new { title = "t", position = "home_banner", imageFileId = new string('0', 32), linkUrl = "https://x.test" }));
        Assert.Equal((HttpStatusCode.BadRequest, "INVALID_COUNTRY"), await Try(new { title = "t", position = "home_banner", imageFileId = image, linkUrl = "https://x.test", countryCode = "ZZ" }));
        Assert.Equal((HttpStatusCode.BadRequest, "INVALID_COUNTRY"), await Try(new { title = "t", position = "home_banner", imageFileId = image, linkUrl = "https://x.test", city = "Beirut" }));   // a city needs its country
        Assert.Equal((HttpStatusCode.BadRequest, "INVALID_PRICING"), await Try(new { title = "t", position = "home_banner", imageFileId = image, linkUrl = "https://x.test", feeOverride = -1 }));
        var id = await s.NewAd("Valid", image: image);
        var (us, updated) = await s.Admin.PutAsync($"/api/admin/ads/{id}", new { title = "Renamed", position = "sidebar", imageFileId = image, linkUrl = "http://x.test/y", countryCode = "LB", city = "Beirut", isActive = false });
        Assert.Equal(HttpStatusCode.OK, us);
        Assert.Equal(("Renamed", "sidebar", "LB", false), (updated.GetProperty("title").GetString(), updated.GetProperty("position").GetString(), updated.GetProperty("countryCode").GetString(), updated.GetProperty("isActive").GetBoolean()));
        Assert.Equal(HttpStatusCode.NotFound, (await s.Admin.PutAsync("/api/admin/ads/999999", new { title = "t", position = "sidebar", imageFileId = image, linkUrl = "https://x.test" })).Item1);
    }

    [Fact]
    public async Task Prices_and_limits_fall_back_from_city_to_country_to_the_position_to_the_settings_each_value_on_its_own()
    {
        await using var s = await StartAsync();
        async Task<(decimal, int)> Resolve(string pos, string? country = null, string? city = null)
        {
            var b = (await s.Admin.GetAsync($"/api/admin/ads/pricing/resolve?position={pos}" + (country is null ? "" : $"&country={country}") + (city is null ? "" : $"&city={city}"))).Body;
            return (b.GetProperty("dailyFee").GetDecimal(), b.GetProperty("maxAds").GetInt32());
        }
        Assert.Equal((5.00m, 5), await Resolve("home_banner", "LB", "Beirut"));          // the defaults in Settings
        await s.Price("home_banner", null, null, 8m, null);                                  // everywhere: fee only
        await s.Price("home_banner", "LB", null, 6m, null);                                  // Lebanon: a cheaper fee
        await s.Price("home_banner", "LB", "Beirut", null, 2);                               // Beirut: only a limit (fee is inherited from Lebanon)
        Assert.Equal((6m, 2), await Resolve("home_banner", "LB", "Beirut"));
        Assert.Equal((6m, 2), await Resolve("home_banner", "LB", "beirut"));                 // city names are not case sensitive
        Assert.Equal((6m, 5), await Resolve("home_banner", "LB", "Jounieh"));                // another Lebanese city: country fee, default limit
        Assert.Equal((6m, 5), await Resolve("home_banner", "LB"));
        Assert.Equal((8m, 5), await Resolve("home_banner", "FR", "Paris"));                  // anywhere else: the position's own price
        Assert.Equal((8m, 5), await Resolve("home_banner"));
        Assert.Equal((5m, 5), await Resolve("sidebar", "LB", "Beirut"));                      // another position: untouched
        await s.Price("home_banner", "LB", "Beirut", 3.5m, 4);                               // saving the same place again replaces it
        Assert.Equal((3.5m, 4), await Resolve("home_banner", "LB", "Beirut"));
        Assert.Equal(1, (await s.Admin.GetAsync("/api/admin/ads/pricing")).Body.EnumerateArray().Count(p => p.TryGetProperty("city", out var c) && c.GetString() == "Beirut"));
        await s.Setting("Ads.DefaultDailyFee", "7.25");
        Assert.Equal(7.25m, (await Resolve("search_banner")).Item1);

        var rows = (await s.Admin.GetAsync("/api/admin/ads/pricing")).Body;
        var cityRow = rows.EnumerateArray().Single(p => p.TryGetProperty("city", out var c) && c.GetString() == "Beirut").GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.NoContent, (await s.Admin.DeleteAsync($"/api/admin/ads/pricing/{cityRow}")).Status);
        Assert.Equal((6m, 5), await Resolve("home_banner", "LB", "Beirut"));                 // back to Lebanon's
        Assert.Equal(HttpStatusCode.NotFound, (await s.Admin.DeleteAsync($"/api/admin/ads/pricing/{cityRow}")).Status);

        async Task<string?> Err(object body) => TestClient.Error((await s.Admin.PutAsync("/api/admin/ads/pricing", body)).Body);
        Assert.Equal("INVALID_PRICING", await Err(new { position = "home_banner" }));                                   // nothing to set
        Assert.Equal("INVALID_PRICING", await Err(new { position = "home_banner", dailyFee = -1 }));
        Assert.Equal("INVALID_PRICING", await Err(new { position = "home_banner", maxAds = 0 }));
        Assert.Equal("INVALID_AD_POSITION", await Err(new { position = "nope", dailyFee = 1 }));
        Assert.Equal("INVALID_COUNTRY", await Err(new { position = "home_banner", countryCode = "ZZ", dailyFee = 1 }));
        Assert.Equal("INVALID_COUNTRY", await Err(new { position = "home_banner", city = "Beirut", dailyFee = 1 }));
    }

    [Fact]
    public async Task A_batch_fixes_its_fee_when_created_and_a_per_ad_fee_overrides_the_price()
    {
        await using var s = await StartAsync();
        await s.Price("home_banner", "LB", null, 6m, null);
        var ad = await s.NewAd("Priced", country: "LB");
        var (st, batch) = await s.Book(ad, 0, 4);                                            // 5 days
        Assert.Equal(HttpStatusCode.Created, st);
        Assert.Equal((6m, 30m, "pending"), (batch.GetProperty("feePerDay").GetDecimal(), batch.GetProperty("totalFee").GetDecimal(), batch.GetProperty("status").GetString()));
        await s.Price("home_banner", "LB", null, 100m, null);                                // a later price rise...
        var listed = (await s.Admin.GetAsync($"/api/admin/ads/batches?adId={ad}")).Body[0];
        Assert.Equal(6m, listed.GetProperty("feePerDay").GetDecimal());                      // ...does not touch the existing batch
        var special = await s.NewAd("Special deal", country: "LB", fee: 2.5m);
        Assert.Equal((2.5m, 5.0m), ((await s.Book(special, 0, 1)).Item2.GetProperty("feePerDay").GetDecimal(), (await s.Book(special, 10, 11)).Item2.GetProperty("totalFee").GetDecimal()));
        Assert.Equal(1m, (await s.Book(await s.NewAd("One day", country: "LB", fee: 1m), 0, 0)).Item2.GetProperty("totalFee").GetDecimal());   // one-day batch
    }

    [Fact]
    public async Task Batch_dates_are_calendar_days_that_must_make_sense_and_an_inactive_ad_cannot_be_booked()
    {
        await using var s = await StartAsync();
        var ad = await s.NewAd();
        async Task<string?> Err(object body) { var (st, b) = await s.Admin.PostAsync($"/api/admin/ads/{ad}/batches", body); return st == HttpStatusCode.Created ? null : TestClient.Error(b); }
        Assert.Equal("INVALID_DATES", await Err(new { from = Day(3), to = Day(2) }));         // ends before it starts
        Assert.Equal("INVALID_DATES", await Err(new { from = Day(-1), to = Day(3) }));        // starts in the past
        Assert.Equal("INVALID_DATES", await Err(new { from = Day(0), to = Day(400) }));       // longer than a year
        Assert.Equal("VALIDATION_FAILED", await Err(new { from = Day(0) }));
        Assert.Equal(HttpStatusCode.NotFound, (await s.Admin.PostAsync("/api/admin/ads/999999/batches", new { from = Day(0), to = Day(1) })).Item1);
        Assert.Null(await Err(new { from = Day(0), to = Day(365) }));                         // exactly a year is fine
        await s.Admin.PutAsync($"/api/admin/ads/{ad}", new { title = "Ad", position = "home_banner", imageFileId = await s.Image(), linkUrl = "https://x.test", isActive = false });
        Assert.Equal((HttpStatusCode.Conflict, "AD_INACTIVE"), ((await s.Book(ad)).Item1, TestClient.Error((await s.Book(ad)).Item2)));
    }

    [Fact]
    public async Task Capacity_is_a_hard_stop_per_day_position_and_overlapping_place_and_cancelling_frees_it()
    {
        await using var s = await StartAsync();
        await s.Price("home_banner", null, null, null, 2);                                    // two ads per day, anywhere
        var a = await s.NewAd("A"); var b = await s.NewAd("B"); var c = await s.NewAd("C");
        var batchA = await s.Book(a, 0, 6); Assert.Equal(HttpStatusCode.Created, batchA.Item1);
        Assert.Equal(HttpStatusCode.Created, (await s.Book(b, 3, 9)).Item1);
        var (st, body) = await s.Book(c, 5, 5);                                               // day 5 already has A and B
        Assert.Equal((HttpStatusCode.Conflict, "AD_CAPACITY_EXCEEDED"), (st, TestClient.Error(body)));
        Assert.Equal(HttpStatusCode.Created, (await s.Book(c, 10, 12)).Item1);                // other days are fine
        Assert.Equal(HttpStatusCode.Created, (await s.Book(c, 0, 2)).Item1);                  // days where only A runs
        var preview = (await s.Admin.GetAsync($"/api/admin/ads/{c}/capacity?from={Day(2)}&to={Day(6)}")).Body;
        Assert.Equal(new[] { 1, 2, 2, 2, 2 }, preview.EnumerateArray().Select(d => d.GetProperty("booked").GetInt32()).ToArray());   // others booked per day (day 2: A only... C itself is excluded)
        Assert.True(preview.EnumerateArray().All(d => d.GetProperty("maxAds").GetInt32() == 2));

        var other = await s.NewAd("Elsewhere", position: "sidebar");
        Assert.Equal(HttpStatusCode.Created, (await s.Book(other, 5, 5)).Item1);              // another position is independent

        var batchId = batchA.Item2.GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.OK, (await s.Admin.PostAsync($"/api/admin/ads/batches/{batchId}/status", new { status = "cancelled" })).Item1);
        var d = await s.NewAd("D");
        Assert.Equal(HttpStatusCode.Created, (await s.Book(d, 5, 5)).Item1);                  // A's days are free again
        await s.Admin.PutAsync($"/api/admin/ads/{b}", new { title = "B", position = "home_banner", imageFileId = await s.Image(), linkUrl = "https://x.test", isActive = false });
        Assert.Equal(HttpStatusCode.Created, (await s.Book(await s.NewAd("E"), 5, 5)).Item1);   // switching an ad off frees its days too
    }

    [Fact]
    public async Task Capacity_counts_only_ads_whose_places_overlap_everywhere_covers_all_a_country_covers_its_cities()
    {
        await using var s = await StartAsync();
        await s.Price("home_banner", null, null, null, 1);                                    // one ad per day wherever places overlap
        var lebanon = await s.NewAd("Lebanon", country: "LB"); var france = await s.NewAd("France", country: "FR");
        Assert.Equal(HttpStatusCode.Created, (await s.Book(lebanon)).Item1);
        Assert.Equal(HttpStatusCode.Created, (await s.Book(france)).Item1);                   // two countries never compete
        var beirut = await s.NewAd("Beirut", country: "LB", city: "Beirut");
        Assert.Equal((HttpStatusCode.Conflict, "AD_CAPACITY_EXCEEDED"), ((await s.Book(beirut)).Item1, TestClient.Error((await s.Book(beirut)).Item2)));   // Lebanon covers Beirut
        var everywhere = await s.NewAd("Everywhere");
        Assert.Equal(HttpStatusCode.Conflict, (await s.Book(everywhere)).Item1);              // a worldwide ad overlaps everything
        var paris = await s.NewAd("Paris", country: "FR", city: "Paris");
        Assert.Equal(HttpStatusCode.Conflict, (await s.Book(paris)).Item1);
        // the preview an admin sees must agree with the real check: Beirut is affected by the Lebanon ad only, Paris by the France ad only
        async Task<int> BookedToday(long ad) => (await s.Admin.GetAsync($"/api/admin/ads/{ad}/capacity?from={Day(0)}&to={Day(0)}")).Body[0].GetProperty("booked").GetInt32();
        Assert.Equal(1, await BookedToday(beirut));
        Assert.Equal(1, await BookedToday(paris));
        Assert.Equal(2, await BookedToday(everywhere));        // a worldwide ad would collide with both
        Assert.Equal(0, await BookedToday(await s.NewAd("Japan", country: "JP")));

        await s.Price("home_banner", "LB", "Beirut", null, 2); await s.Price("home_banner", "LB", "Jounieh", null, 1);
        var jounieh = await s.NewAd("Jounieh", country: "LB", city: "Jounieh");
        Assert.Equal(HttpStatusCode.Conflict, (await s.Book(jounieh)).Item1);                 // the Lebanon ad still counts against Jounieh (limit 1)
        await s.Price("home_banner", "LB", "Jounieh", null, 2);
        Assert.Equal(HttpStatusCode.Created, (await s.Book(jounieh)).Item1);                  // limit 2: the Lebanon ad + this one
        Assert.Equal(HttpStatusCode.Created, (await s.Book(beirut)).Item1);                   // Beirut and Jounieh are different cities: they do not compete
    }

    [Fact]
    public async Task Many_admins_booking_at_the_same_moment_can_never_exceed_the_limit()
    {
        await using var s = await StartAsync();
        await s.Price("home_banner", null, null, null, 3);
        var ads = new List<long>();
        for (var i = 0; i < 10; i++) ads.Add(await s.NewAd("Race " + i));
        var gate = new TaskCompletionSource();
        var attempts = ads.Select(id => Task.Run(async () => { await gate.Task; return (await s.Book(id, 1, 3)).Item1; })).ToList();
        await Task.Delay(150); gate.SetResult();
        var results = await Task.WhenAll(attempts);
        Assert.Equal(3, results.Count(r => r == HttpStatusCode.Created));                      // exactly the limit, never more, whatever the timing
        Assert.Equal(7, results.Count(r => r == HttpStatusCode.Conflict));
        Assert.Equal("3", await s.Iso.Db.ScalarAsync("SELECT COUNT(*) FROM ad_batches WHERE status = 'pending'"));
    }

    [Fact]
    public async Task A_batch_moves_from_pending_to_paid_or_cancelled_and_nothing_else()
    {
        await using var s = await StartAsync();
        var ad = await s.NewAd();
        var id = (await s.Book(ad)).Item2.GetProperty("id").GetInt64();
        async Task<(HttpStatusCode, string?)> Move(long batch, string status) { var (st, b) = await s.Admin.PostAsync($"/api/admin/ads/batches/{batch}/status", new { status, billedTo = "Test customer", method = "cash" }); return (st, TestClient.Error(b)); }
        Assert.Equal((HttpStatusCode.BadRequest, "INVALID_STATUS"), await Move(id, "pending"));
        Assert.Equal((HttpStatusCode.BadRequest, "INVALID_STATUS"), await Move(id, "refunded"));
        Assert.Equal((HttpStatusCode.NotFound, "BATCH_NOT_FOUND"), await Move(999999, "paid"));
        var (ok, paid) = await s.Admin.PostAsync($"/api/admin/ads/batches/{id}/status", new { status = "paid", billedTo = "Test customer", method = "cash" });
        Assert.Equal(HttpStatusCode.OK, ok);
        Assert.NotNull(paid.GetProperty("paidAt").GetString());
        Assert.Equal((HttpStatusCode.Conflict, "INVALID_TRANSITION"), await Move(id, "paid"));            // already paid
        Assert.Equal(HttpStatusCode.OK, (await Move(id, "cancelled")).Item1);                              // a paid batch can still be cancelled
        Assert.Equal((HttpStatusCode.Conflict, "INVALID_TRANSITION"), await Move(id, "paid"));            // but cancelled is final
        Assert.Equal((HttpStatusCode.Conflict, "INVALID_TRANSITION"), await Move(id, "cancelled"));
        Assert.Equal(2, (await s.Admin.GetAsync("/api/admin/audit?entityType=ad_batch&pageSize=20")).Body.GetProperty("items").GetArrayLength() - 1);   // create + paid + cancelled
    }

    [Fact]
    public async Task Visitors_get_only_ads_that_are_paid_running_today_active_and_meant_for_their_place_most_specific_first()
    {
        await using var s = await StartAsync();
        await s.Setting("Ads.DefaultMaxAds", "50");
        var everywhere = await s.NewAd("Everywhere"); var lebanon = await s.NewAd("Lebanon", country: "LB"); var beirut = await s.NewAd("Beirut", country: "LB", city: "Beirut");
        var france = await s.NewAd("France", country: "FR"); var pending = await s.NewAd("Not paid"); var future = await s.NewAd("Starts tomorrow"); var off = await s.NewAd("Switched off");
        var expired = await s.NewAd("Ended yesterday"); var sidebar = await s.NewAd("Sidebar", position: "sidebar");
        foreach (var id in new[] { everywhere, lebanon, beirut, france, off, sidebar }) await s.BookPaid(id);
        await s.Book(pending);                                                               // reserved, not paid: must not run
        await s.BookPaid(future, 1, 5);                                                      // paid, but not today
        var gone = await s.BookPaid(expired, 0, 2);
        await s.Iso.Db.ExecAsync($"UPDATE ad_batches SET date_from = DATE_SUB(UTC_DATE(), INTERVAL 4 DAY), date_to = DATE_SUB(UTC_DATE(), INTERVAL 1 DAY) WHERE id = {gone}");
        await s.Admin.PutAsync($"/api/admin/ads/{off}", new { title = "Switched off", position = "home_banner", imageFileId = await s.Image(), linkUrl = "https://x.test", isActive = false });

        // the admin list says which ads are running today: exactly those with a paid batch covering today
        var listed = (await s.Admin.GetAsync("/api/admin/ads")).Body.EnumerateArray().ToDictionary(a => a.GetProperty("title").GetString()!, a => a.GetProperty("runningToday").GetBoolean());
        Assert.True(listed["Everywhere"] && listed["Lebanon"] && listed["Beirut"] && listed["France"] && listed["Sidebar"], string.Join(", ", listed));
        Assert.False(listed["Not paid"] || listed["Starts tomorrow"] || listed["Ended yesterday"], string.Join(", ", listed));

        Assert.Equal(new[] { "Beirut", "Lebanon", "Everywhere" }, SceneHandle.Titles(await s.Served("home_banner", "LB", "Beirut")));      // city first, then country, then global
        Assert.Equal(new[] { "Beirut", "Lebanon", "Everywhere" }, SceneHandle.Titles(await s.Served("home_banner", "LB", "BEIRUT")));
        Assert.Equal(new[] { "Lebanon", "Everywhere" }, SceneHandle.Titles(await s.Served("home_banner", "LB", "Jounieh")));
        Assert.Equal(new[] { "Lebanon", "Everywhere" }, SceneHandle.Titles(await s.Served("home_banner", "LB")));
        Assert.Equal(new[] { "France", "Everywhere" }, SceneHandle.Titles(await s.Served("home_banner", "FR", "Paris")));
        Assert.Equal(new[] { "Everywhere" }, SceneHandle.Titles(await s.Served("home_banner")));                                           // no known place: worldwide ads only
        Assert.Equal(new[] { "Everywhere" }, SceneHandle.Titles(await s.Served("home_banner", "JP", "Tokyo")));                            // nobody sees ads for other countries
        Assert.Equal(new[] { "Sidebar" }, SceneHandle.Titles(await s.Served("sidebar", "LB")));
        Assert.Empty(SceneHandle.Titles(await s.Served("search_banner", "LB")));
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Host.NewClient().GetAsync("/api/ads")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Host.NewClient().GetAsync("/api/ads?position=BAD%20POSITION")).Status);

        await s.Setting("Ads.MaxPerSlot", "2");
        Assert.Equal(new[] { "Beirut", "Lebanon" }, SceneHandle.Titles(await s.Served("home_banner", "LB", "Beirut")));                    // capped by the setting
        var first = (await s.Served("home_banner", "LB", "Beirut"))[0];
        Assert.Matches("^/api/files/[0-9a-f]{32}$", first.GetProperty("imageUrl").GetString());
        Assert.Matches("^/api/ads/\\d+/click$", first.GetProperty("clickUrl").GetString());
        var picture = await s.Host.NewClient().GetBytesAsync(first.GetProperty("imageUrl").GetString()!);                              // the picture is public
        Assert.Equal((HttpStatusCode.OK, "image/jpeg"), (picture.Status, picture.ContentHeaders.ContentType!.MediaType));
        Assert.Equal(5, (await s.Host.NewClient().GetAsync("/api/config/public")).Body.GetProperty("Ads.RotationSeconds").GetInt32());   // the rotation time is a public setting
    }
}

public class AdTrackingTests
{
    private static async Task<(AdTests.SceneHandle S, long Ad)> RunningAd(string link = "https://example.test/landing")
    {
        var s = await AdTests.SceneHandle.StartAsync();
        var ad = await s.NewAd("Tracked", link: link);
        await s.BookPaid(ad);
        return (s, ad);
    }

    private static async Task<HttpResponseMessage> Raw(TestClient c, HttpMethod method, string path) { using var req = new HttpRequestMessage(method, path); return await c.Http.SendAsync(req); }

    [Fact]
    public async Task Impressions_are_counted_in_total_and_once_per_visitor_per_day_using_an_anonymous_cookie()
    {
        var (s, ad) = await RunningAd();
        await using var scene = s;
        var a = s.Host.NewClient(); var b = s.Host.NewClient();
        using var first = await Raw(a, HttpMethod.Post, $"/api/ads/{ad}/impression");
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        var cookie = first.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("sayserne.sid=")).ToLowerInvariant();
        Assert.Contains("httponly", cookie); Assert.Contains("samesite=lax", cookie);
        using var again = await Raw(a, HttpMethod.Post, $"/api/ads/{ad}/impression");
        Assert.False(again.Headers.TryGetValues("Set-Cookie", out _));                    // the visitor id is created once, not on every call
        await Raw(a, HttpMethod.Post, $"/api/ads/{ad}/impression");
        await Raw(b, HttpMethod.Post, $"/api/ads/{ad}/impression");

        var stat = (await s.Admin.GetAsync("/api/admin/ads/stats")).Body.EnumerateArray().Single(x => x.GetProperty("adId").GetInt64() == ad);
        Assert.Equal((4L, 2L, 0L), (stat.GetProperty("impressions").GetInt64(), stat.GetProperty("uniqueImpressions").GetInt64(), stat.GetProperty("clicks").GetInt64()));
        var daily = (await s.Admin.GetAsync($"/api/admin/ads/{ad}/stats")).Body;
        Assert.Equal(1, daily.GetArrayLength());
        Assert.Equal((4L, 2L), (daily[0].GetProperty("impressions").GetInt64(), daily[0].GetProperty("uniqueImpressions").GetInt64()));
        Assert.Equal(DateTime.UtcNow.ToString("yyyy-MM-dd"), daily[0].GetProperty("day").GetString());
    }

    [Fact]
    public async Task A_click_is_counted_then_the_visitor_is_sent_to_the_saved_link_and_nowhere_else()
    {
        var (s, ad) = await RunningAd("https://example.test/landing?from=sayserne");
        await using var scene = s;
        var a = s.Host.NewClient();
        using var r1 = await Raw(a, HttpMethod.Get, $"/api/ads/{ad}/click");
        Assert.Equal(HttpStatusCode.Redirect, r1.StatusCode);
        Assert.Equal("https://example.test/landing?from=sayserne", r1.Headers.Location!.ToString());
        Assert.Equal("no-referrer", r1.Headers.GetValues("Referrer-Policy").Single());
        using var r2 = await Raw(a, HttpMethod.Get, $"/api/ads/{ad}/click?to=https://evil.test");      // a parameter cannot redirect somewhere else
        Assert.Equal("https://example.test/landing?from=sayserne", r2.Headers.Location!.ToString());
        await Raw(s.Host.NewClient(), HttpMethod.Get, $"/api/ads/{ad}/click");
        var stat = (await s.Admin.GetAsync("/api/admin/ads/stats")).Body.EnumerateArray().Single(x => x.GetProperty("adId").GetInt64() == ad);
        Assert.Equal((3L, 2L), (stat.GetProperty("clicks").GetInt64(), stat.GetProperty("uniqueClicks").GetInt64()));
        Assert.Equal(0L, stat.GetProperty("impressions").GetInt64());                                     // a click is not an impression
    }

    [Fact]
    public async Task Ads_that_are_not_running_cannot_be_tracked_or_clicked_through()
    {
        await using var s = await AdTests.SceneHandle.StartAsync();
        var reserved = await s.NewAd("Reserved only"); await s.Book(reserved);                            // not paid
        var off = await s.NewAd("Off"); await s.BookPaid(off);
        await s.Admin.PutAsync($"/api/admin/ads/{off}", new { title = "Off", position = "home_banner", imageFileId = await s.Image(), linkUrl = "https://x.test", isActive = false });
        var c = s.Host.NewClient();
        foreach (var id in new long[] { reserved, off, 999999 })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await Raw(c, HttpMethod.Post, $"/api/ads/{id}/impression")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await Raw(c, HttpMethod.Get, $"/api/ads/{id}/click")).StatusCode);   // never redirects for an ad that is not running
        }
        Assert.Equal("0", await s.Iso.Db.ScalarAsync("SELECT COUNT(*) FROM ad_events"));
    }

    [Fact]
    public async Task Tracking_stores_no_address_only_a_hash_of_a_random_visitor_id()
    {
        var (s, ad) = await RunningAd();
        await using var scene = s;
        var a = s.Host.NewClient();
        using var r = await Raw(a, HttpMethod.Post, $"/api/ads/{ad}/impression");
        var cookieValue = System.Text.RegularExpressions.Regex.Match(r.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("sayserne.sid=")), "sayserne.sid=([^;]+)").Groups[1].Value;
        var columns = (await s.Iso.Db.ScalarAsync("SELECT GROUP_CONCAT(column_name) FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name IN ('ad_events','ad_unique_seen','ad_stats_daily')"))!.ToLowerInvariant();
        Assert.DoesNotContain("ip", columns.Split(',').Select(c => c.Trim()));                      // there is no address column anywhere
        Assert.DoesNotContain("user", columns);
        var stored = await s.Iso.Db.ScalarAsync("SELECT session_hash FROM ad_events LIMIT 1");
        Assert.Matches("^[0-9a-f]{16}$", stored!);
        Assert.NotEqual(cookieValue, stored);                                                        // the cookie value itself is not stored
    }

    [Fact]
    public async Task Reports_are_rate_limited_per_address_but_showing_ads_never_is()
    {
        var (s, ad) = await RunningAd();
        await using var scene = s;
        await s.Setting("Ads.EventsPerMinute", "10");
        var c = s.Host.NewClient();
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 25; i++) codes.Add((await Raw(c, HttpMethod.Post, $"/api/ads/{ad}/impression")).StatusCode);
        Assert.True(codes.Contains((HttpStatusCode)429), string.Join(",", codes));
        Assert.True(codes.Count(x => x == HttpStatusCode.NoContent) is >= 8 and <= 12, string.Join(",", codes));
        for (var i = 0; i < 40; i++) Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/ads?position=home_banner")).Status);   // serving is not limited
    }

    [Fact]
    public async Task A_report_from_another_website_is_refused()
    {
        var (s, ad) = await RunningAd();
        await using var scene = s;
        var foreign = s.Host.NewClient(csrfHeader: false);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/ads/{ad}/impression");
        req.Headers.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await foreign.Http.SendAsync(req)).StatusCode);
        Assert.Equal("0", await s.Iso.Db.ScalarAsync("SELECT COUNT(*) FROM ad_events"));
    }

    [Fact]
    public async Task The_daily_cleanup_removes_old_raw_events_but_never_the_totals()
    {
        var (s, ad) = await RunningAd();
        await using var scene = s;
        await Raw(s.Host.NewClient(), HttpMethod.Post, $"/api/ads/{ad}/impression");
        await s.Iso.Db.ExecAsync($"INSERT INTO ad_events (ad_id, kind, session_hash, occurred_at) VALUES ({ad}, 'impression', 'aaaaaaaaaaaaaaaa', DATE_SUB(UTC_TIMESTAMP(3), INTERVAL 100 DAY)), ({ad}, 'click', 'bbbbbbbbbbbbbbbb', DATE_SUB(UTC_TIMESTAMP(3), INTERVAL 30 DAY))");
        await s.Iso.Db.ExecAsync($"INSERT INTO ad_unique_seen (ad_id, day, kind, session_hash) VALUES ({ad}, DATE_SUB(UTC_DATE(), INTERVAL 5 DAY), 'impression', 'aaaaaaaaaaaaaaaa')");
        var scheduler = s.Host.App.Services.GetRequiredService<DigestScheduler>(); var runner = s.Host.App.Services.GetRequiredService<JobRunner>();
        async Task Cleanup() { await scheduler.RunOnceAsync(); while (await runner.RunOnceAsync() > 0) { } }

        await Cleanup();
        Assert.Equal("2", await s.Iso.Db.ScalarAsync("SELECT COUNT(*) FROM ad_events"));                                  // 90 days kept: the 100-day-old one is gone
        Assert.Equal("0", await s.Iso.Db.ScalarAsync("SELECT COUNT(*) FROM ad_unique_seen WHERE day < DATE_SUB(UTC_DATE(), INTERVAL 2 DAY)"));
        await Cleanup();                                                                                                    // the job runs once per day however often the scheduler ticks
        Assert.Equal("1", await s.Iso.Db.ScalarAsync("SELECT COUNT(*) FROM jobs WHERE job_type = 'ads.purge'"));

        await s.Setting("Ads.EventRetentionDays", "7");
        await s.Iso.Db.ExecAsync("DELETE FROM jobs WHERE job_type = 'ads.purge'");                                         // (the next day)
        await Cleanup();
        Assert.Equal("1", await s.Iso.Db.ScalarAsync("SELECT COUNT(*) FROM ad_events"));                                  // only today's remains
        var stat = (await s.Admin.GetAsync("/api/admin/ads/stats")).Body.EnumerateArray().Single(x => x.GetProperty("adId").GetInt64() == ad);
        Assert.Equal(1L, stat.GetProperty("impressions").GetInt64());                                                       // the totals are untouched
    }

    [Fact]
    public async Task Statistics_can_be_filtered_by_date_and_every_change_to_ads_is_audited()
    {
        var (s, ad) = await RunningAd();
        await using var scene = s;
        await Raw(s.Host.NewClient(), HttpMethod.Post, $"/api/ads/{ad}/impression");
        var yesterday = (DateTime.UtcNow.Date.AddDays(-1)).ToString("yyyy-MM-dd");
        var none = (await s.Admin.GetAsync($"/api/admin/ads/stats?from={yesterday}&to={yesterday}")).Body.EnumerateArray().Single();
        Assert.Equal(0L, none.GetProperty("impressions").GetInt64());                                   // an ad with no events still appears, with zeros
        Assert.Equal(1L, (await s.Admin.GetAsync($"/api/admin/ads/stats?from={DateTime.UtcNow:yyyy-MM-dd}")).Body.EnumerateArray().Single().GetProperty("impressions").GetInt64());
        Assert.Equal(0, (await s.Admin.GetAsync($"/api/admin/ads/{ad}/stats?from={yesterday}&to={yesterday}")).Body.GetArrayLength());

        await s.Price("sidebar", "LB", null, 3m, 2);
        var actions = (await s.Admin.GetAsync("/api/admin/audit?pageSize=200")).Body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("action").GetString()).ToHashSet();
        foreach (var expected in new[] { "ads.create", "ads.batch.create", "ads.batch.paid", "ads.pricing.save" })
            Assert.True(actions.Contains(expected), $"missing audit action {expected}; have {string.Join(", ", actions)}");
    }
}
