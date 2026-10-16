using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Sayserne.Api.Auth;
using Sayserne.Api.Platform;
using Sayserne.Migrator;

namespace Sayserne.Tests;

internal static class J
{
    public static JsonElement Find(JsonElement array, string property, string value) =>
        array.EnumerateArray().First(e => e.TryGetProperty(property, out var v) && v.GetString() == value);
    public static bool Any(JsonElement array, string property, string value) =>
        array.EnumerateArray().Any(e => e.TryGetProperty(property, out var v) && v.GetString() == value);
}

public class SettingsApiTests
{
    private static async Task<(HttpStatusCode, JsonElement)> SetAsync(TestClient admin, string key, string value) => await admin.PutAsync($"/api/admin/settings/{key}", new { value });

    [Fact]
    public async Task Settings_need_the_right_permission()
    {
        await using var host = await ApiHost.StartAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.NewClient().GetAsync("/api/admin/settings")).Status);
        var (none, _, _) = await host.CreateStaffAsync("users.read");
        Assert.Equal(HttpStatusCode.Forbidden, (await none.GetAsync("/api/admin/settings")).Status);
        var (reader, _, _) = await host.CreateStaffAsync("settings.read");
        var (status, list) = await reader.GetAsync("/api/admin/settings");
        Assert.Equal(HttpStatusCode.OK, status);
        var minLength = J.Find(list, "key", "Auth.PasswordMinLength");
        Assert.Equal(8m, minLength.GetProperty("min").GetDecimal());
        Assert.Equal("10", minLength.GetProperty("default").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await SetAsync(reader, "Auth.PasswordMinLength", "12")).Item1);   // read is not manage
    }

    [Fact]
    public async Task Values_are_checked_against_their_type_and_limits()
    {
        await using var iso = await IsolatedHost.StartAsync();
        var (admin, _, _) = await iso.Host.CreateAdminAsync();
        foreach (var (key, value, code) in new[]
        {
            ("Auth.PasswordMinLength", "7", "SETTING_OUT_OF_RANGE"),          // below the security floor of 8
            ("Auth.PasswordMinLength", "129", "SETTING_OUT_OF_RANGE"),
            ("Auth.PasswordMinLength", "ten", "SETTING_INVALID"),
            ("Auth.PasswordMinLength", "1.5", "SETTING_INVALID"),
            ("Auth.PasswordRequireDigit", "maybe", "SETTING_INVALID"),
            ("Auth.PasswordMinLength", "   ", "SETTING_INVALID"),
            ("Auth.NoSuchSetting", "1", "SETTING_NOT_FOUND"),
            ("Site.DefaultLanguage", "zz", "INVALID_LANGUAGE"),
            ("Booking.MinDurationMinutes", "120", "SETTING_INCONSISTENT"),     // would exceed the maximum (480 is fine, 120 min vs max 480 -> use bigger below)
        })
        {
            if (key == "Booking.MinDurationMinutes") continue;   // covered separately below
            var (status, body) = await SetAsync(admin, key, value);
            Assert.True(status is HttpStatusCode.BadRequest or HttpStatusCode.NotFound, $"{key}={value} -> {status}");
            Assert.Equal(code, TestClient.Error(body));
        }
        // the booking window must stay coherent
        await SetAsync(admin, "Booking.MaxDurationMinutes", "60");
        Assert.Equal("SETTING_INCONSISTENT", TestClient.Error((await SetAsync(admin, "Booking.MinDurationMinutes", "90")).Item2));
        Assert.Equal(HttpStatusCode.OK, (await SetAsync(admin, "Booking.MinDurationMinutes", "40")).Item1);
        Assert.Equal("SETTING_INCONSISTENT", TestClient.Error((await SetAsync(admin, "Booking.MaxDurationMinutes", "30")).Item2));   // 30 is in range but below the minimum (40)
        Assert.Equal("SETTING_INCONSISTENT", TestClient.Error((await SetAsync(admin, "Search.DefaultPageSize", "100")).Item2));
        Assert.Equal(HttpStatusCode.OK, (await SetAsync(admin, "Auth.PasswordRequireDigit", "FALSE")).Item1);   // normalised to lower case
        Assert.Equal("false", J.Find((await admin.GetAsync("/api/admin/settings")).Body, "key", "Auth.PasswordRequireDigit").GetProperty("value").GetString());
    }

    [Fact]
    public async Task The_password_policy_comes_from_settings_and_is_published_to_the_web_app()
    {
        await using var iso = await IsolatedHost.StartAsync();
        var host = iso.Host;
        var (admin, _, _) = await host.CreateAdminAsync();
        Assert.Equal(10, (await host.NewClient().GetAsync("/api/config/public")).Body.GetProperty("Auth.PasswordMinLength").GetInt32());

        Assert.Equal(HttpStatusCode.OK, (await SetAsync(admin, "Auth.PasswordMinLength", "14")).Item1);
        Assert.Equal(14, (await host.NewClient().GetAsync("/api/config/public")).Body.GetProperty("Auth.PasswordMinLength").GetInt32());
        var (s1, b1) = await host.NewClient().PostAsync("/api/auth/register", new { email = ApiHost.UniqueEmail(), password = "correct-horse-9", firstName = "A", lastName = "B" });   // 15 chars: fine
        Assert.Equal(HttpStatusCode.Created, s1);
        var (s2, b2) = await host.NewClient().PostAsync("/api/auth/register", new { email = ApiHost.UniqueEmail(), password = "shortpass-12", firstName = "A", lastName = "B" });   // 12 chars: now too short
        Assert.Equal(HttpStatusCode.BadRequest, s2);
        Assert.Contains("14", b2.GetProperty("errors").GetProperty("password").GetString());

        await SetAsync(admin, "Auth.PasswordRequireDigit", "false");
        Assert.Equal(HttpStatusCode.Created, (await host.NewClient().PostAsync("/api/auth/register", new { email = ApiHost.UniqueEmail(), password = "only-letters-in-here", firstName = "A", lastName = "B" })).Status);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/admin/settings/Auth.PasswordMinLength/reset")).Status);
        Assert.Equal(10, (await host.NewClient().GetAsync("/api/config/public")).Body.GetProperty("Auth.PasswordMinLength").GetInt32());
    }

    [Fact]
    public async Task Public_config_exposes_only_public_settings_with_real_types()
    {
        await using var host = await ApiHost.StartAsync();
        var (status, cfg) = await host.NewClient().GetAsync("/api/config/public");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(JsonValueKind.Number, cfg.GetProperty("Auth.PasswordMinLength").ValueKind);
        Assert.Equal(JsonValueKind.True, cfg.GetProperty("Auth.PasswordRequireLetter").ValueKind);
        Assert.Equal("en", cfg.GetProperty("Site.DefaultLanguage").GetString());
        foreach (var secret in new[] { "Auth.MaxFailedAttempts", "Auth.LockoutMinutes", "Auth.AdminIdleMinutes", "Auth.WritesPerMinute", "Auth.SessionHours" })
            Assert.False(cfg.TryGetProperty(secret, out _), $"{secret} must not be public");
        using var response = await host.NewClient().Http.GetAsync("/api/config/public");
        Assert.Contains("max-age", response.Headers.CacheControl?.ToString() ?? "");
    }

    [Fact]
    public async Task Lockout_session_and_rate_limit_settings_take_effect_without_a_restart()
    {
        await using var iso = await IsolatedHost.StartAsync(pinRateLimit: false);   // the database value must be the one in force
        var host = iso.Host;
        var (admin, _, _) = await host.CreateAdminAsync();
        await SetAsync(admin, "Auth.MaxFailedAttempts", "3");
        var (_, email, _) = await host.RegisterAsync();
        var c = host.NewClient();
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsync("/api/auth/login", new { email, password = "wrong-password-1" })).Status);
        Assert.Equal("ACCOUNT_LOCKED", TestClient.Error((await c.PostAsync("/api/auth/login", new { email, password = ApiHost.StrongPassword })).Body));

        await SetAsync(admin, "Auth.AuthRequestsPerMinute", "6");
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 10; i++) statuses.Add((await host.NewClient().PostAsync("/api/auth/login", new { email = "nobody@example.test", password = "x" })).Status);
        Assert.True(statuses.Contains(HttpStatusCode.TooManyRequests), string.Join(",", statuses));
    }

    [Fact]
    public async Task Booking_chat_alert_and_search_limits_come_from_settings()
    {
        await using var iso = await IsolatedHost.StartAsync();
        var host = iso.Host;
        var (admin, _, _) = await host.CreateAdminAsync();
        foreach (var (key, value) in new[] { ("Booking.MaxDurationMinutes", "60"), ("Chat.MaxMessageLength", "100"), ("Alerts.MaxPerUser", "2"), ("Search.DefaultPageSize", "5"), ("Search.MaxPageSize", "10") })
        {
            var (setStatus, setBody) = await SetAsync(admin, key, value);
            Assert.True(setStatus == HttpStatusCode.OK, $"setting {key}={value} -> {setStatus} {setBody}");
        }

        var (pro, _, proId) = await host.RegisterProfessionalAsync(city: "Limitville");
        await host.AddProfessionAsync(pro, extra: new() { ["city"] = "Limitville" });
        var (client, _, _) = await host.RegisterAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/appointments", new { professionalId = proId, startsAt = ApiHost.FutureSlot(), durationMinutes = 90 })).Status);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsync("/api/appointments", new { professionalId = proId, startsAt = ApiHost.FutureSlot(), durationMinutes = 60 })).Status);

        var (_, _, other) = await host.RegisterAsync();
        var room = (await client.PostAsync("/api/chat/rooms", new { userId = other })).Body.GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/api/chat/rooms/{room}/messages", new { content = new string('x', 101) })).Status);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsync($"/api/chat/rooms/{room}/messages", new { content = new string('x', 100) })).Status);

        for (var i = 0; i < 2; i++) Assert.Equal(HttpStatusCode.Created, (await client.PostAsync("/api/alerts", new { name = $"a{i}", searchCriteria = new { } })).Status);
        Assert.Equal("ALERT_LIMIT", TestClient.Error((await client.PostAsync("/api/alerts", new { name = "third", searchCriteria = new { } })).Body));

        var search = (await host.NewClient().GetAsync("/api/professionals/search")).Body;
        Assert.Equal(5, search.GetProperty("pageSize").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await host.NewClient().GetAsync("/api/professionals/search?pageSize=11")).Status);
        Assert.Equal(HttpStatusCode.OK, (await host.NewClient().GetAsync("/api/professionals/search?pageSize=10")).Status);
    }

    [Fact]
    public async Task A_setting_changed_on_one_instance_reaches_another_after_its_refresh()
    {
        await using var iso = await IsolatedHost.StartAsync();
        await using var other = await ApiHost.StartAsync(database: iso.Db);   // a second API process on the same database
        var (admin, _, _) = await iso.Host.CreateAdminAsync();
        Assert.Equal(HttpStatusCode.OK, (await SetAsync(admin, "Auth.PasswordMinLength", "20")).Item1);
        Assert.Equal(10, (await other.NewClient().GetAsync("/api/config/public")).Body.GetProperty("Auth.PasswordMinLength").GetInt32());   // not yet: stale until its next refresh
        await other.App.Services.GetRequiredService<ISettingsService>().RefreshAsync();
        Assert.Equal(20, (await other.NewClient().GetAsync("/api/config/public")).Body.GetProperty("Auth.PasswordMinLength").GetInt32());
    }

    [Fact]
    public async Task Every_settings_change_is_audited_with_the_old_and_new_value()
    {
        await using var iso = await IsolatedHost.StartAsync();
        var (admin, _, adminId) = await iso.Host.CreateAdminAsync();
        await SetAsync(admin, "Chat.MaxMessageLength", "500");
        await admin.PostAsync("/api/admin/settings/Chat.MaxMessageLength/reset");
        var (_, audit) = await admin.GetAsync("/api/admin/audit?action=settings.update");
        var items = audit.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        var change = items.EnumerateArray().First(i => i.GetProperty("details").GetProperty("new").GetString() == "500");
        Assert.Equal("4000", change.GetProperty("details").GetProperty("old").GetString());
        Assert.Equal(adminId, change.GetProperty("actorId").GetInt64());
        Assert.Equal("Chat.MaxMessageLength", change.GetProperty("entityId").GetString());
    }
}

public class LookupApiTests
{
    private static string Code() => "t" + Guid.NewGuid().ToString("N")[..10];

    [Fact]
    public async Task The_geography_from_the_original_app_is_seeded_with_english_and_arabic_names()
    {
        await using var host = await ApiHost.StartAsync();
        var c = host.NewClient();
        var (s, countries) = await c.GetAsync("/api/lookups/country");
        Assert.Equal(HttpStatusCode.OK, s);
        Assert.True(countries.GetArrayLength() >= 171);
        var lebanon = J.Find(countries, "code", "LB");
        Assert.Equal("Lebanon", lebanon.GetProperty("names").GetProperty("en").GetString());
        Assert.Equal("لبنان", lebanon.GetProperty("names").GetProperty("ar").GetString());
        Assert.Equal("+961", lebanon.GetProperty("metadata").GetProperty("phoneCode").GetString());
        Assert.True(countries.EnumerateArray().All(v => v.GetProperty("names").TryGetProperty("en", out _) && v.GetProperty("names").TryGetProperty("ar", out _)));

        var (_, cities) = await c.GetAsync("/api/lookups/city?parent=LB");
        Assert.True(cities.GetArrayLength() >= 15);
        Assert.True(cities.EnumerateArray().All(v => v.GetProperty("parentCode").GetString() == "LB"));
        Assert.Equal("بيروت", J.Find(cities, "code", "LB-beirut").GetProperty("names").GetProperty("ar").GetString());
        Assert.Equal(74, (await c.GetAsync("/api/lookups/nationality")).Body.GetArrayLength());
        Assert.Equal("لبناني", J.Find((await c.GetAsync("/api/lookups/nationality")).Body, "code", "lebanese").GetProperty("names").GetProperty("ar").GetString());
    }

    [Fact]
    public async Task The_small_lookups_replace_the_lists_the_ui_used_to_hard_code()
    {
        await using var host = await ApiHost.StartAsync();
        var c = host.NewClient();
        foreach (var (type, expected) in new[] { ("sex", 2), ("language", 2), ("alert_frequency", 3), ("weekday", 7), ("duration_option", 6), ("currency", 1), ("appointment_status", 4), ("message_type", 2), ("profile_type", 2) })
            Assert.Equal(expected, (await c.GetAsync($"/api/lookups/{type}")).Body.GetArrayLength());
        var days = (await c.GetAsync("/api/lookups/weekday")).Body;
        Assert.Equal(new[] { "0", "1", "2", "3", "4", "5", "6" }, days.EnumerateArray().Select(d => d.GetProperty("code").GetString()!).ToArray());   // ordered
        Assert.Equal("Sunday", days[0].GetProperty("names").GetProperty("en").GetString());
        Assert.Equal("rtl", J.Find((await c.GetAsync("/api/lookups/language")).Body, "code", "ar").GetProperty("metadata").GetProperty("dir").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/lookups/no_such_type")).Status);
        using var response = await c.Http.GetAsync("/api/lookups/sex");
        Assert.Contains("max-age", response.Headers.CacheControl?.ToString() ?? "");
    }

    [Fact]
    public async Task Admins_add_edit_and_deactivate_values_and_the_public_list_follows()
    {
        await using var host = await ApiHost.StartAsync();
        var (admin, _, _) = await host.CreateAdminAsync();
        var type = Code();
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync($"/api/admin/lookups/types/{type}", new { name = "Test type", description = "created by a test" })).Status);

        var code = Code();
        var (s1, v1) = await admin.PutAsync($"/api/admin/lookups/{type}/values/{code}", new { sortOrder = 5, isActive = true, metadata = new { color = "teal" }, names = new Dictionary<string, string> { ["en"] = "First", ["ar"] = "الأول" } });
        Assert.Equal(HttpStatusCode.OK, s1);
        Assert.Equal("teal", v1.GetProperty("metadata").GetProperty("color").GetString());

        var c = host.NewClient();
        Assert.Equal(1, (await c.GetAsync($"/api/lookups/{type}")).Body.GetArrayLength());
        await admin.PutAsync($"/api/admin/lookups/{type}/values/{code}", new { sortOrder = 1, isActive = true, names = new Dictionary<string, string> { ["en"] = "Renamed" } });
        var after = (await c.GetAsync($"/api/lookups/{type}")).Body[0];
        Assert.Equal("Renamed", after.GetProperty("names").GetProperty("en").GetString());
        Assert.Equal("الأول", after.GetProperty("names").GetProperty("ar").GetString());   // languages not sent are left alone

        await admin.PutAsync($"/api/admin/lookups/{type}/values/{code}", new { isActive = false, names = new Dictionary<string, string> { ["en"] = "Renamed" } });
        Assert.Equal(0, (await c.GetAsync($"/api/lookups/{type}")).Body.GetArrayLength());                                  // hidden from the public
        var adminView = (await admin.GetAsync($"/api/admin/lookups/{type}/values")).Body;
        Assert.Equal(1, adminView.GetArrayLength());                                                                         // but never deleted
        Assert.False(adminView[0].GetProperty("isActive").GetBoolean());
        await admin.PutAsync($"/api/admin/lookups/{type}/values/{code}", new { isActive = true, names = new Dictionary<string, string> { ["en"] = "Renamed" } });
        Assert.Equal(1, (await c.GetAsync($"/api/lookups/{type}")).Body.GetArrayLength());
        var (_, types) = await admin.GetAsync("/api/admin/lookups/types");
        Assert.Equal(1, J.Find(types, "code", type).GetProperty("valueCount").GetInt32());
    }

    [Fact]
    public async Task Lookup_changes_need_the_lookups_permission()
    {
        await using var host = await ApiHost.StartAsync();
        var anon = host.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/admin/lookups/types")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PutAsync("/api/admin/lookups/sex/values/x", new { names = new Dictionary<string, string> { ["en"] = "X" } })).Status);
        var (other, _, _) = await host.CreateStaffAsync("users.read");
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync("/api/admin/lookups/types")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PutAsync("/api/admin/lookups/sex/values/x", new { names = new Dictionary<string, string> { ["en"] = "X" } })).Status);
        var (content, _, _) = await host.CreateStaffAsync("lookups.manage");
        Assert.Equal(HttpStatusCode.OK, (await content.GetAsync("/api/admin/lookups/types")).Status);
    }

    [Fact]
    public async Task Bad_lookup_input_is_rejected()
    {
        await using var host = await ApiHost.StartAsync();
        var (admin, _, _) = await host.CreateAdminAsync();
        var en = new Dictionary<string, string> { ["en"] = "Ok" };
        async Task<string?> Err(string path, object body) => TestClient.Error((await admin.PutAsync(path, body)).Body);
        Assert.Equal("LOOKUP_TYPE_NOT_FOUND", await Err("/api/admin/lookups/nope/values/x", new { names = en }));
        Assert.Equal("INVALID_CODE", await Err("/api/admin/lookups/sex/values/bad%20code", new { names = en }));
        Assert.Equal("INVALID_NAMES", await Err($"/api/admin/lookups/sex/values/{Code()}", new { names = new Dictionary<string, string> { ["ar"] = "فقط" } }));          // English is required
        Assert.Equal("INVALID_NAMES", await Err($"/api/admin/lookups/sex/values/{Code()}", new { names = new Dictionary<string, string> { ["en"] = "X", ["zz"] = "Y" } }));  // unknown language
        Assert.Equal("INVALID_NAMES", await Err($"/api/admin/lookups/sex/values/{Code()}", new { names = new Dictionary<string, string> { ["en"] = "  " } }));
        Assert.Equal("LOOKUP_PARENT_REQUIRED", await Err($"/api/admin/lookups/city/values/{Code()}", new { names = en }));                                              // a city needs a country
        Assert.Equal("LOOKUP_PARENT_REQUIRED", await Err($"/api/admin/lookups/city/values/{Code()}", new { parentCode = "ZZ-none", names = en }));
        Assert.Equal("LOOKUP_TYPE_NOT_FOUND", await Err("/api/admin/lookups/types/" + Code(), new { name = "x", parentType = "nope" }));
        Assert.Equal("INVALID_CODE", await Err("/api/admin/lookups/types/Bad-Code", new { name = "x" }));
    }

    [Fact]
    public async Task A_new_city_appears_under_its_country_and_the_default_language_cannot_be_switched_off()
    {
        await using var iso = await IsolatedHost.StartAsync();
        var (admin, _, _) = await iso.Host.CreateAdminAsync();
        var code = "LB-" + Code();
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync($"/api/admin/lookups/city/values/{code}", new { parentCode = "LB", names = new Dictionary<string, string> { ["en"] = "Newtown", ["ar"] = "نيوتاون" } })).Status);
        Assert.True(J.Any((await iso.Host.NewClient().GetAsync("/api/lookups/city?parent=LB")).Body, "code", code));
        Assert.False(J.Any((await iso.Host.NewClient().GetAsync("/api/lookups/city?parent=FR")).Body, "code", code));

        var (status, body) = await admin.PutAsync("/api/admin/lookups/language/values/en", new { isActive = false, names = new Dictionary<string, string> { ["en"] = "English" } });
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("DEFAULT_LANGUAGE_REQUIRED", TestClient.Error(body));
    }

    [Fact]
    public async Task Profile_values_are_validated_against_the_lookups()
    {
        await using var host = await ApiHost.StartAsync();
        var (c, _, _) = await host.RegisterAsync();
        async Task<(HttpStatusCode, JsonElement)> Save(string? sex = "male", string? nat = "lebanese", string? country = "LB", string language = "en") =>
            await c.PutAsync("/api/me/profile", new { firstName = "A", lastName = "B", profileType = "customer", sex, nationality = nat, country, preferredLanguage = language });
        Assert.Equal(HttpStatusCode.OK, (await Save()).Item1);
        Assert.Equal("INVALID_SEX", TestClient.Error((await Save(sex: "other")).Item2));
        Assert.Equal("INVALID_NATIONALITY", TestClient.Error((await Save(nat: "klingon")).Item2));
        Assert.Equal("INVALID_COUNTRY", TestClient.Error((await Save(country: "Lebanon")).Item2));   // names are not codes
        Assert.Equal("INVALID_LANGUAGE", TestClient.Error((await Save(language: "fr")).Item2));
        Assert.Equal(HttpStatusCode.OK, (await Save(sex: null, nat: null, country: null, language: "ar")).Item1);   // optional fields may be empty
        // the listing's country is validated too
        var (pro, _, _) = await host.RegisterProfessionalAsync();
        var (categoryId, serviceId) = await host.ServiceAsync();
        Assert.Equal("INVALID_COUNTRY", TestClient.Error((await pro.PostAsync("/api/me/professions", new { categoryId, serviceId, country = "Nowhere" })).Body));
        Assert.Equal(HttpStatusCode.Created, (await pro.PostAsync("/api/me/professions", new { categoryId, serviceId, country = "LB", officeCountry = "FR" })).Status);
    }

    [Fact]
    public async Task A_country_an_admin_adds_becomes_valid_and_one_they_deactivate_stops_being_accepted()
    {
        await using var iso = await IsolatedHost.StartAsync();
        var host = iso.Host;
        var (admin, _, _) = await host.CreateAdminAsync();
        var (user, _, _) = await host.RegisterAsync();
        var code = "X" + Code()[..6];
        object Body(string country) => new { firstName = "A", lastName = "B", profileType = "customer", country };
        Assert.Equal("INVALID_COUNTRY", TestClient.Error((await user.PutAsync("/api/me/profile", Body(code))).Body));
        await admin.PutAsync($"/api/admin/lookups/country/values/{code}", new { names = new Dictionary<string, string> { ["en"] = "Newland" } });
        Assert.Equal(HttpStatusCode.OK, (await user.PutAsync("/api/me/profile", Body(code))).Status);
        await admin.PutAsync($"/api/admin/lookups/country/values/{code}", new { isActive = false, names = new Dictionary<string, string> { ["en"] = "Newland" } });
        Assert.Equal("INVALID_COUNTRY", TestClient.Error((await user.PutAsync("/api/me/profile", Body(code))).Body));
    }

    [Fact]
    public async Task Saved_search_frequencies_come_from_the_lookup()
    {
        await using var iso = await IsolatedHost.StartAsync();
        var host = iso.Host;
        var (admin, _, _) = await host.CreateAdminAsync();
        var (c, _, _) = await host.RegisterAsync();
        Assert.Equal("INVALID_FREQUENCY", TestClient.Error((await c.PostAsync("/api/alerts", new { name = "x", searchCriteria = new { }, frequency = "hourly" })).Body));
        var code = "freq_" + Code();
        await admin.PutAsync($"/api/admin/lookups/alert_frequency/values/{code}", new { names = new Dictionary<string, string> { ["en"] = "Custom" } });
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsync("/api/alerts", new { name = "y", searchCriteria = new { }, frequency = code })).Status);
    }
}

public class TranslationApiTests
{
    [Fact]
    public async Task The_error_texts_are_served_in_english_and_arabic()
    {
        await using var host = await ApiHost.StartAsync();
        var c = host.NewClient();
        var (s, en) = await c.GetAsync("/api/translations/en");
        Assert.Equal(HttpStatusCode.OK, s);
        Assert.Equal("That time is no longer available. Please choose another.", en.GetProperty("error.SLOT_TAKEN").GetString());
        var ar = (await c.GetAsync("/api/translations/ar")).Body;
        Assert.Equal(en.EnumerateObject().Count(), ar.EnumerateObject().Count());
        Assert.NotEqual(en.GetProperty("error.INVALID_CREDENTIALS").GetString(), ar.GetProperty("error.INVALID_CREDENTIALS").GetString());
        Assert.Equal("error.account_locked", en.EnumerateObject().First(p => p.Name == "error.account_locked").Name);   // keys are case-sensitive: both spellings exist
        Assert.True(en.TryGetProperty("error.ACCOUNT_LOCKED", out _));
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/translations/zz")).Status);
    }

    [Fact]
    public async Task A_language_with_no_text_falls_back_to_the_default_language()
    {
        await using var iso = await IsolatedHost.StartAsync();
        var (admin, _, _) = await iso.Host.CreateAdminAsync();
        await admin.PutAsync("/api/admin/lookups/language/values/fr", new { names = new Dictionary<string, string> { ["en"] = "French" }, metadata = new { dir = "ltr" } });
        await admin.PutAsync("/api/admin/translations/fr/error.SLOT_TAKEN", new { value = "Ce créneau n'est plus disponible." });
        var fr = (await iso.Host.NewClient().GetAsync("/api/translations/fr")).Body;
        Assert.Equal("Ce créneau n'est plus disponible.", fr.GetProperty("error.SLOT_TAKEN").GetString());
        Assert.Equal("Please sign in.", fr.GetProperty("error.UNAUTHENTICATED").GetString());   // not translated yet -> default language
    }

    [Fact]
    public async Task Admins_edit_and_reset_texts_and_every_change_is_audited()
    {
        await using var iso = await IsolatedHost.StartAsync();
        var host = iso.Host;
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.NewClient().GetAsync("/api/admin/translations")).Status);
        var (other, _, _) = await host.CreateStaffAsync("users.read");
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PutAsync("/api/admin/translations/en/error.SLOT_TAKEN", new { value = "x" })).Status);

        var (admin, _, _) = await host.CreateAdminAsync();
        var (s, saved) = await admin.PutAsync("/api/admin/translations/en/error.SLOT_TAKEN", new { value = "  Sorry, taken.  " });
        Assert.Equal(HttpStatusCode.OK, s);
        Assert.Equal("Sorry, taken.", saved.GetProperty("value").GetString());
        Assert.Equal("That time is no longer available. Please choose another.", saved.GetProperty("defaultValue").GetString());
        Assert.Equal("Sorry, taken.", (await host.NewClient().GetAsync("/api/translations/en")).Body.GetProperty("error.SLOT_TAKEN").GetString());

        var (_, reset) = await admin.PostAsync("/api/admin/translations/en/error.SLOT_TAKEN/reset");
        Assert.Equal("That time is no longer available. Please choose another.", reset.GetProperty("value").GetString());
        var (_, audit) = await admin.GetAsync("/api/admin/audit?action=translations.save");
        Assert.Equal(2, audit.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Bad_translation_edits_are_rejected()
    {
        await using var host = await ApiHost.StartAsync();
        var (admin, _, _) = await host.CreateAdminAsync();
        async Task<string?> Err(string lang, string key, string value) => TestClient.Error((await admin.PutAsync($"/api/admin/translations/{lang}/{key}", new { value })).Body);
        Assert.Equal("TRANSLATION_NOT_FOUND", await Err("en", "error.NO_SUCH_KEY", "x"));
        Assert.Equal("INVALID_LANGUAGE", await Err("zz", "error.SLOT_TAKEN", "x"));
        Assert.Equal("INVALID_TEXT", await Err("en", "error.SLOT_TAKEN", "   "));
        Assert.Equal("INVALID_TEXT", await Err("en", "error.SLOT_TAKEN", new string('x', 4001)));
        Assert.Equal("TRANSLATION_NOT_FOUND", TestClient.Error((await admin.PostAsync("/api/admin/translations/en/error.NO_SUCH_KEY/reset")).Body));
    }
}

public class SeedDataTests
{
    [Fact]
    public async Task The_database_ships_the_expected_reference_data()
    {
        var db = await TestEnv.GetDatabaseAsync();
        var repo = new PlatformRepository(db.Executor());
        var countries = await repo.LookupValuesAsync("country", null, includeInactive: true);
        Assert.Equal(171, countries.Count);
        Assert.Equal(171, countries.Select(c => c.Code).Distinct().Count());
        Assert.Equal(524, (await repo.LookupValuesAsync("city", null, includeInactive: true)).Count);
        Assert.Equal(74, (await repo.LookupValuesAsync("nationality", null, includeInactive: true)).Count);
        Assert.True(countries.All(c => c.Names.ContainsKey("en") && c.Names.ContainsKey("ar") && c.Metadata?.GetProperty("iso2").GetString() == c.Code));
        Assert.Equal(119, countries.Count(c => c.Metadata!.Value.TryGetProperty("phoneCode", out _)));   // the original's phone-code list
        Assert.True((await repo.SettingsAsync()).Count >= 22);
        Assert.True((await repo.PermissionsAsync()).Count >= 10);
        Assert.Equal(new[] { "Content", "Operations", "Support" }, (await repo.RolesAsync()).Select(r => r.Name).Where(n => n is "Content" or "Operations" or "Support").Order().ToArray());
    }
}

public class PlatformDatabaseSecurityTests
{
    [Theory]
    [InlineData("direct_settings_update.sql")]
    [InlineData("direct_audit_delete.sql")]
    [InlineData("direct_audit_update.sql")]
    [InlineData("direct_audit_select.sql")]
    [InlineData("direct_roles_insert.sql")]
    public async Task The_app_account_cannot_touch_platform_tables_directly(string fixture)
    {
        var db = await TestEnv.GetDatabaseAsync();
        await using var conn = new MySqlConnection(db.AppConnection);
        await conn.OpenAsync();
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", fixture));
        var ex = await Assert.ThrowsAsync<MySqlException>(async () => await ScriptRunner.ExecuteAsync(conn, sql, default));
        Assert.Equal(MySqlErrorCode.TableAccessDenied, ex.ErrorCode);
    }
}
