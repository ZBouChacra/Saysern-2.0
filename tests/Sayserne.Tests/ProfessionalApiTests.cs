using System.Net;
using System.Text.Json;

namespace Sayserne.Tests;

public class CatalogTests
{
    [Fact]
    public async Task Categories_and_services_are_public_and_seeded()
    {
        await using var host = await ApiHost.StartAsync();
        var c = host.NewClient();   // anonymous
        var (s1, cats) = await c.GetAsync("/api/catalog/categories");
        Assert.Equal(HttpStatusCode.OK, s1);
        Assert.True(cats.GetArrayLength() >= 7);
        var legal = cats.EnumerateArray().First(x => x.GetProperty("name").GetString() == "Legal");
        var (s2, services) = await c.GetAsync($"/api/catalog/services?categoryId={legal.GetProperty("id").GetInt64()}");
        Assert.Equal(HttpStatusCode.OK, s2);
        Assert.True(services.EnumerateArray().Any(x => x.GetProperty("name").GetString() == "Corporate Law"));
        Assert.True(services.EnumerateArray().All(x => x.GetProperty("categoryId").GetInt64() == legal.GetProperty("id").GetInt64()));
    }

    [Fact]
    public async Task An_unknown_category_has_no_services()
    {
        await using var host = await ApiHost.StartAsync();
        var (status, services) = await host.NewClient().GetAsync("/api/catalog/services?categoryId=99999999");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, services.GetArrayLength());
    }
}

public class ProfileApiTests
{
    [Fact]
    public async Task Profile_endpoints_require_a_session()
    {
        await using var host = await ApiHost.StartAsync();
        var c = host.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/me/profile")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PutAsync("/api/me/profile", ApiHost.ProfileBody())).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/me/professions")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsync("/api/me/professions", new { })).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PutAsync("/api/me/availability", new { slots = new object[0] })).Status);
    }

    [Fact]
    public async Task The_owner_can_read_and_update_their_full_profile()
    {
        await using var host = await ApiHost.StartAsync();
        var (c, email, _) = await host.RegisterAsync();
        var (status, updated) = await c.PutAsync("/api/me/profile", ApiHost.ProfileBody("Rima", "Haddad", "customer", "Beirut", "Lebanon", "female", "1990-05-17", "Lebanese", "+961 71 123 456", "Hello"));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Rima", ApiHost.Str(updated, "firstName"));
        Assert.Equal("1990-05-17", ApiHost.Str(updated, "dateOfBirth"));
        var (_, mine) = await c.GetAsync("/api/me/profile");
        Assert.Equal(email, ApiHost.Str(mine, "email"));
        Assert.Equal("+961 71 123 456", ApiHost.Str(mine, "phone"));
    }

    [Fact]
    public async Task Profile_update_replaces_fields_so_optional_values_can_be_cleared()
    {
        await using var host = await ApiHost.StartAsync();
        var (c, _, _) = await host.RegisterAsync();
        await c.PutAsync("/api/me/profile", ApiHost.ProfileBody("A", "B", "customer", "Beirut", bio: "first bio"));
        var (_, after) = await c.PutAsync("/api/me/profile", ApiHost.ProfileBody("A", "B", "customer"));
        Assert.False(ApiHost.Has(after, "bio"));
        Assert.False(ApiHost.Has(after, "city"));
    }

    [Fact]
    public async Task Invalid_profile_values_are_rejected_field_by_field()
    {
        await using var host = await ApiHost.StartAsync();
        var (c, _, _) = await host.RegisterAsync();
        var (status, body) = await c.PutAsync("/api/me/profile", new
        {
            firstName = "", lastName = new string('x', 101), phone = "abc", sex = "other", dateOfBirth = "17/05/1990",
            profilePhoto = "javascript:alert(1)", bannerPhoto = "ftp://x/y", preferredLanguage = "fr", profileType = "wizard", bio = new string('b', 2001),
        });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("VALIDATION_FAILED", TestClient.Error(body));
        var errors = body.GetProperty("errors");
        foreach (var field in new[] { "firstName", "lastName", "phone", "sex", "dateOfBirth", "profilePhoto", "bannerPhoto", "preferredLanguage", "profileType", "bio" })
            Assert.True(errors.TryGetProperty(field, out _), $"expected an error for {field}");
    }

    [Fact]
    public async Task A_future_date_of_birth_is_rejected()
    {
        await using var host = await ApiHost.StartAsync();
        var (c, _, _) = await host.RegisterAsync();
        var (status, body) = await c.PutAsync("/api/me/profile", ApiHost.ProfileBody(dob: DateTime.UtcNow.AddDays(2).ToString("yyyy-MM-dd")));
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.True(body.GetProperty("errors").TryGetProperty("dateOfBirth", out _));
    }

    [Fact]
    public async Task The_public_profile_never_exposes_email_phone_or_date_of_birth()
    {
        await using var host = await ApiHost.StartAsync();
        var (c, email, id) = await host.RegisterAsync();
        await c.PutAsync("/api/me/profile", ApiHost.ProfileBody("Rami", "Karam", "professional", "Beirut", "Lebanon", "male", "1985-03-02", "Lebanese", "+961 3 999 888", "Lawyer"));
        var (status, body) = await host.NewClient().GetAsync($"/api/professionals/{id}");   // anonymous
        Assert.Equal(HttpStatusCode.OK, status);
        var text = body.GetRawText();
        Assert.False(text.Contains(email), "email leaked");
        Assert.False(text.Contains("961 3 999 888"), "phone leaked");
        Assert.False(text.Contains("1985-03-02"), "date of birth leaked");
        Assert.False(text.Contains("passwordHash"));
        var profile = body.GetProperty("profile");
        Assert.Equal("Rami", ApiHost.Str(profile, "firstName"));
        Assert.True(profile.GetProperty("age").GetInt32() >= 40);   // age is shown instead
    }

    [Fact]
    public async Task Customers_locked_users_and_unknown_ids_have_no_public_profile()
    {
        await using var host = await ApiHost.StartAsync();
        var (_, _, customerId) = await host.RegisterAsync();
        var (_, _, proId) = await host.RegisterProfessionalAsync();
        var (admin, _, _) = await host.CreateAdminAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await host.NewClient().GetAsync($"/api/professionals/{customerId}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await host.NewClient().GetAsync("/api/professionals/99999999")).Status);
        Assert.Equal(HttpStatusCode.OK, (await host.NewClient().GetAsync($"/api/professionals/{proId}")).Status);
        await admin.PostAsync($"/api/admin/users/{proId}/lock", new { isLocked = true });
        Assert.Equal(HttpStatusCode.NotFound, (await host.NewClient().GetAsync($"/api/professionals/{proId}")).Status);
    }

    [Fact]
    public async Task A_professional_with_live_listings_cannot_switch_back_to_customer()
    {
        await using var host = await ApiHost.StartAsync();
        var (c, _, _) = await host.RegisterProfessionalAsync();
        var professionId = await host.AddProfessionAsync(c);
        var (status, body) = await c.PutAsync("/api/me/profile", ApiHost.ProfileBody("Pat", "Pro", "customer"));
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("HIDE_PROFESSIONS_FIRST", TestClient.Error(body));
        await c.PutAsync($"/api/me/professions/{professionId}", new { isHidden = true });
        Assert.Equal(HttpStatusCode.OK, (await c.PutAsync("/api/me/profile", ApiHost.ProfileBody("Pat", "Pro", "customer"))).Status);
    }
}

public class ProfessionApiTests
{
    [Fact]
    public async Task Only_professionals_can_add_services()
    {
        await using var host = await ApiHost.StartAsync();
        var (customer, _, _) = await host.RegisterAsync();
        var (categoryId, serviceId) = await host.ServiceAsync();
        var (status, body) = await customer.PostAsync("/api/me/professions", new { categoryId, serviceId });
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("NOT_A_PROFESSIONAL", TestClient.Error(body));
    }

    [Fact]
    public async Task A_service_must_belong_to_the_chosen_category()
    {
        await using var host = await ApiHost.StartAsync();
        var (pro, _, _) = await host.RegisterProfessionalAsync();
        var (legalCategory, _) = await host.ServiceAsync("Corporate Law");
        var (_, physio) = await host.ServiceAsync("Physiotherapy");
        var (status, body) = await pro.PostAsync("/api/me/professions", new { categoryId = legalCategory, serviceId = physio });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("INVALID_SERVICE", TestClient.Error(body));
        Assert.Equal(HttpStatusCode.BadRequest, (await pro.PostAsync("/api/me/professions", new { categoryId = 999999, serviceId = 999999 })).Status);
    }

    [Fact]
    public async Task The_same_service_cannot_be_added_twice()
    {
        await using var host = await ApiHost.StartAsync();
        var (pro, _, _) = await host.RegisterProfessionalAsync();
        await host.AddProfessionAsync(pro);
        var (categoryId, serviceId) = await host.ServiceAsync();
        var (status, body) = await pro.PostAsync("/api/me/professions", new { categoryId, serviceId });
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("DUPLICATE_PROFESSION", TestClient.Error(body));
    }

    [Fact]
    public async Task Bad_listing_values_are_rejected()
    {
        await using var host = await ApiHost.StartAsync();
        var (pro, _, _) = await host.RegisterProfessionalAsync();
        var (categoryId, serviceId) = await host.ServiceAsync();
        var (status, body) = await pro.PostAsync("/api/me/professions", new
        {
            categoryId, serviceId, costPerHour = -5, yearsOfExperience = 200, website = "not a url", teamSize = -1, hasTeam = true,
            geographicAreas = Enumerable.Range(0, 31).Select(i => "Area" + i).ToArray(),
        });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        var errors = body.GetProperty("errors");
        foreach (var field in new[] { "costPerHour", "yearsOfExperience", "website", "teamSize", "geographicAreas" })
            Assert.True(errors.TryGetProperty(field, out _), $"expected an error for {field}");
    }

    [Fact]
    public async Task Listings_are_created_listed_updated_and_deleted_by_their_owner()
    {
        await using var host = await ApiHost.StartAsync();
        var (pro, _, _) = await host.RegisterProfessionalAsync();
        var id = await host.AddProfessionAsync(pro, extra: new() { ["geographicAreas"] = new[] { "Beirut", "beirut", " Jounieh " } });

        var (_, mine) = await pro.GetAsync("/api/me/professions");
        Assert.Equal(1, mine.GetArrayLength());
        Assert.Equal(2, mine[0].GetProperty("geographicAreas").GetArrayLength());   // duplicates (case-insensitive) and blanks removed

        var (s, updated) = await pro.PutAsync($"/api/me/professions/{id}", new { costPerHour = 99.5, yearsOfExperience = 9, city = "Byblos", hasOffice = true, officeAddress = "Main St", isHidden = false });
        Assert.Equal(HttpStatusCode.OK, s);
        Assert.Equal(99.5m, updated.GetProperty("costPerHour").GetDecimal());
        Assert.Equal("Byblos", ApiHost.Str(updated, "city"));

        Assert.Equal(HttpStatusCode.NoContent, (await pro.DeleteAsync($"/api/me/professions/{id}")).Status);
        Assert.Equal(0, (await pro.GetAsync("/api/me/professions")).Body.GetArrayLength());
    }

    [Fact]
    public async Task Other_users_cannot_change_or_delete_someone_elses_listing()
    {
        await using var host = await ApiHost.StartAsync();
        var (owner, _, _) = await host.RegisterProfessionalAsync();
        var (intruder, _, _) = await host.RegisterProfessionalAsync();
        var id = await host.AddProfessionAsync(owner);
        var (s1, b1) = await intruder.PutAsync($"/api/me/professions/{id}", new { costPerHour = 1 });
        var (s2, _) = await intruder.DeleteAsync($"/api/me/professions/{id}");
        Assert.Equal(HttpStatusCode.NotFound, s1);
        Assert.Equal("PROFESSION_NOT_FOUND", TestClient.Error(b1));
        Assert.Equal(HttpStatusCode.NotFound, s2);
        Assert.Equal(50m, (await owner.GetAsync("/api/me/professions")).Body[0].GetProperty("costPerHour").GetDecimal());   // untouched
    }

    [Fact]
    public async Task Hidden_listings_disappear_from_search_and_the_public_profile()
    {
        await using var host = await ApiHost.StartAsync();
        var city = "Hide" + Guid.NewGuid().ToString("N")[..8];
        var (pro, _, proId) = await host.RegisterProfessionalAsync();
        var id = await host.AddProfessionAsync(pro, extra: new() { ["city"] = city });
        var anon = host.NewClient();
        Assert.Equal(1, (await anon.GetAsync($"/api/professionals/search?city={city}")).Body.GetProperty("total").GetInt32());
        await pro.PutAsync($"/api/me/professions/{id}", new { city, isHidden = true });
        Assert.Equal(0, (await anon.GetAsync($"/api/professionals/search?city={city}")).Body.GetProperty("total").GetInt32());
        Assert.Equal(0, (await anon.GetAsync($"/api/professionals/{proId}")).Body.GetProperty("professions").GetArrayLength());
    }
}

public class AvailabilityApiTests
{
    private static object Slot(int day, string start, string end) => new { dayOfWeek = day, startTime = start, endTime = end };

    [Fact]
    public async Task Availability_is_replaced_as_a_set_and_can_be_cleared()
    {
        await using var host = await ApiHost.StartAsync();
        var (pro, _, _) = await host.RegisterProfessionalAsync();
        var professionId = await host.AddProfessionAsync(pro);
        var (s1, set1) = await pro.PutAsync("/api/me/availability", new { professionId, slots = new[] { Slot(1, "09:00", "12:00"), Slot(1, "14:00", "17:00"), Slot(3, "10:00", "16:00") } });
        Assert.Equal(HttpStatusCode.OK, s1);
        Assert.Equal(3, set1.GetArrayLength());
        Assert.Equal("09:00", set1[0].GetProperty("startTime").GetString());

        var (_, set2) = await pro.PutAsync("/api/me/availability", new { professionId, slots = new[] { Slot(5, "08:00", "09:30") } });
        Assert.Equal(1, set2.GetArrayLength());
        var (_, cleared) = await pro.PutAsync("/api/me/availability", new { professionId, slots = new object[0] });
        Assert.Equal(0, cleared.GetArrayLength());
    }

    [Fact]
    public async Task Invalid_time_windows_are_rejected()
    {
        await using var host = await ApiHost.StartAsync();
        var (pro, _, _) = await host.RegisterProfessionalAsync();
        foreach (var bad in new[]
        {
            Slot(7, "09:00", "10:00"),       // no such day
            Slot(1, "10:00", "09:00"),       // backwards
            Slot(1, "10:00", "10:00"),       // empty
            Slot(1, "9:00", "10:00"),        // not HH:mm
            Slot(1, "09:00", "25:00"),       // not a time
        })
        {
            var (status, body) = await pro.PutAsync("/api/me/availability", new { slots = new[] { bad } });
            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Equal("VALIDATION_FAILED", TestClient.Error(body));
        }
    }

    [Fact]
    public async Task Overlapping_windows_on_the_same_day_are_rejected()
    {
        await using var host = await ApiHost.StartAsync();
        var (pro, _, _) = await host.RegisterProfessionalAsync();
        var (status, _) = await pro.PutAsync("/api/me/availability", new { slots = new[] { Slot(2, "09:00", "12:00"), Slot(2, "11:00", "13:00") } });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(HttpStatusCode.OK, (await pro.PutAsync("/api/me/availability", new { slots = new[] { Slot(2, "09:00", "12:00"), Slot(2, "12:00", "13:00"), Slot(3, "11:00", "13:00") } })).Status);
    }

    [Fact]
    public async Task Availability_can_only_be_attached_to_your_own_listing()
    {
        await using var host = await ApiHost.StartAsync();
        var (owner, _, _) = await host.RegisterProfessionalAsync();
        var (intruder, _, _) = await host.RegisterProfessionalAsync();
        var professionId = await host.AddProfessionAsync(owner);
        var (status, body) = await intruder.PutAsync("/api/me/availability", new { professionId, slots = new[] { Slot(1, "09:00", "10:00") } });
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("PROFESSION_NOT_FOUND", TestClient.Error(body));
    }

    [Fact]
    public async Task Setting_one_listings_availability_leaves_the_others_alone()
    {
        await using var host = await ApiHost.StartAsync();
        var (pro, _, _) = await host.RegisterProfessionalAsync();
        var a = await host.AddProfessionAsync(pro, "Corporate Law");
        var b = await host.AddProfessionAsync(pro, "Family Law");
        await pro.PutAsync("/api/me/availability", new { professionId = a, slots = new[] { Slot(1, "09:00", "10:00") } });
        await pro.PutAsync("/api/me/availability", new { professionId = b, slots = new[] { Slot(2, "09:00", "10:00") } });
        await pro.PutAsync("/api/me/availability", new { professionId = a, slots = new object[0] });
        var (_, all) = await pro.GetAsync("/api/me/availability");
        Assert.Equal(1, all.GetArrayLength());
        Assert.Equal(b, all[0].GetProperty("professionId").GetInt64());
    }

    [Fact]
    public async Task The_public_profile_shows_availability_only_for_visible_listings()
    {
        await using var host = await ApiHost.StartAsync();
        var (pro, _, proId) = await host.RegisterProfessionalAsync();
        var shown = await host.AddProfessionAsync(pro, "Corporate Law");
        var hidden = await host.AddProfessionAsync(pro, "Family Law");
        await pro.PutAsync("/api/me/availability", new { professionId = shown, slots = new[] { Slot(1, "09:00", "10:00") } });
        await pro.PutAsync("/api/me/availability", new { professionId = hidden, slots = new[] { Slot(2, "09:00", "10:00") } });
        await pro.PutAsync($"/api/me/professions/{hidden}", new { isHidden = true });
        var (_, profile) = await host.NewClient().GetAsync($"/api/professionals/{proId}");
        Assert.Equal(1, profile.GetProperty("professions").GetArrayLength());
        Assert.Equal(1, profile.GetProperty("availability").GetArrayLength());
    }
}

public class SearchApiTests
{
    private static async Task<JsonElement> SearchAsync(ApiHost host, string query) => (await host.NewClient().GetAsync("/api/professionals/search?" + query)).Body;
    private static int Total(JsonElement r) => r.GetProperty("total").GetInt32();

    [Fact]
    public async Task Search_is_public_and_filters_by_category_service_city_and_name()
    {
        await using var host = await ApiHost.StartAsync();
        var tag = "Srch" + Guid.NewGuid().ToString("N")[..8];
        var (law, _, lawId) = await host.RegisterProfessionalAsync("Lara", "Lawson", tag, "Lebanon");
        var (health, _, _) = await host.RegisterProfessionalAsync("Hugo", "Healey", tag, "Lebanon");
        await host.AddProfessionAsync(law, "Corporate Law", new() { ["city"] = tag, ["country"] = "Lebanon" });
        await host.AddProfessionAsync(health, "Physiotherapy", new() { ["city"] = tag, ["country"] = "Lebanon" });
        var (legalCat, legalSvc) = await host.ServiceAsync("Corporate Law");

        Assert.Equal(2, Total(await SearchAsync(host, $"city={tag}")));
        Assert.Equal(1, Total(await SearchAsync(host, $"city={tag}&categoryId={legalCat}")));
        Assert.Equal(1, Total(await SearchAsync(host, $"city={tag}&serviceId={legalSvc}")));
        Assert.Equal(1, Total(await SearchAsync(host, $"city={tag}&firstName=ugo")));        // partial, case-insensitive
        Assert.Equal(1, Total(await SearchAsync(host, $"city={tag}&lastName=awso")));
        Assert.True(Total(await SearchAsync(host, $"city={tag[..7]}&country=Lebanon")) >= 2);
        var result = await SearchAsync(host, $"city={tag}&categoryId={legalCat}");
        Assert.Equal(lawId, result.GetProperty("items")[0].GetProperty("userId").GetInt64());
    }

    [Fact]
    public async Task Wildcard_characters_in_search_text_are_matched_literally()
    {
        await using var host = await ApiHost.StartAsync();
        var (pro, _, _) = await host.RegisterProfessionalAsync("Wild", "Card", "Plainville");
        await host.AddProfessionAsync(pro, extra: new() { ["city"] = "Plainville" });
        Assert.Equal(0, Total(await SearchAsync(host, "city=%25")));         // '%' must not mean "everything"
        Assert.Equal(0, Total(await SearchAsync(host, "city=Plain_ille")));  // '_' must not mean "any character"
        Assert.Equal(1, Total(await SearchAsync(host, "city=Plainville&firstName=Wild")));
    }

    [Fact]
    public async Task Search_filters_by_cost_experience_team_office_sex_age_and_nationality()
    {
        await using var host = await ApiHost.StartAsync();
        var tag = "Flt" + Guid.NewGuid().ToString("N")[..8];
        var (a, _, _) = await host.RegisterProfessionalAsync("Ann", "A", tag, sex: "female", dob: DateTime.UtcNow.AddYears(-30).AddDays(-10).ToString("yyyy-MM-dd"), nationality: "Lebanese");
        var (b, _, _) = await host.RegisterProfessionalAsync("Bob", "B", tag, sex: "male", dob: DateTime.UtcNow.AddYears(-50).AddDays(-10).ToString("yyyy-MM-dd"), nationality: "French");
        await host.AddProfessionAsync(a, extra: new() { ["city"] = tag, ["costPerHour"] = 20, ["yearsOfExperience"] = 2, ["hasTeam"] = false, ["hasOffice"] = false });
        await host.AddProfessionAsync(b, extra: new() { ["city"] = tag, ["costPerHour"] = 80, ["yearsOfExperience"] = 20, ["hasTeam"] = true, ["teamSize"] = 4, ["hasOffice"] = true, ["officeAddress"] = "X" });

        Assert.Equal(1, Total(await SearchAsync(host, $"city={tag}&minCost=50")));
        Assert.Equal(1, Total(await SearchAsync(host, $"city={tag}&maxCost=50")));
        Assert.Equal(1, Total(await SearchAsync(host, $"city={tag}&minExperience=10")));
        Assert.Equal(1, Total(await SearchAsync(host, $"city={tag}&maxExperience=10")));
        Assert.Equal(1, Total(await SearchAsync(host, $"city={tag}&hasTeam=true")));
        Assert.Equal(1, Total(await SearchAsync(host, $"city={tag}&hasTeam=false")));
        Assert.Equal(1, Total(await SearchAsync(host, $"city={tag}&hasOffice=true")));
        Assert.Equal(2, Total(await SearchAsync(host, $"city={tag}&hasOffice=false")));      // false/absent = no filter, as in the original
        Assert.Equal(1, Total(await SearchAsync(host, $"city={tag}&sex=female")));
        Assert.Equal(1, Total(await SearchAsync(host, $"city={tag}&minAge=40")));
        Assert.Equal(1, Total(await SearchAsync(host, $"city={tag}&maxAge=40")));
        Assert.Equal(1, Total(await SearchAsync(host, $"city={tag}&nationality=French")));
    }

    [Fact]
    public async Task Search_results_are_paged_and_report_the_total()
    {
        await using var host = await ApiHost.StartAsync();
        var tag = "Pg" + Guid.NewGuid().ToString("N")[..8];
        for (var i = 0; i < 5; i++)
        {
            var (p, _, _) = await host.RegisterProfessionalAsync($"P{i}", "Paged", tag);
            await host.AddProfessionAsync(p, extra: new() { ["city"] = tag });
        }
        var page1 = await SearchAsync(host, $"city={tag}&pageSize=2&page=1");
        var page3 = await SearchAsync(host, $"city={tag}&pageSize=2&page=3");
        Assert.Equal(5, Total(page1));
        Assert.Equal(2, page1.GetProperty("items").GetArrayLength());
        Assert.Equal(1, page3.GetProperty("items").GetArrayLength());
        Assert.Equal(3, page3.GetProperty("page").GetInt32());
    }

    [Fact]
    public async Task Premium_then_starred_listings_rank_first()
    {
        await using var host = await ApiHost.StartAsync();
        var tag = "Rk" + Guid.NewGuid().ToString("N")[..8];
        var (admin, _, _) = await host.CreateAdminAsync();
        var ids = new List<long>();
        for (var i = 0; i < 3; i++)
        {
            var (p, _, id) = await host.RegisterProfessionalAsync($"R{i}", "Rank", tag);
            await host.AddProfessionAsync(p, extra: new() { ["city"] = tag, ["yearsOfExperience"] = 30 - i });
            ids.Add(id);
        }
        // ids[2] is premium, ids[1] starred, ids[0] neither (even though it has the most experience)
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/admin/users/{ids[2]}/flags", new { isPremium = true, isStarred = false })).Status);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/admin/users/{ids[1]}/flags", new { isPremium = false, isStarred = true })).Status);
        var items = (await SearchAsync(host, $"city={tag}")).GetProperty("items");
        Assert.Equal(new[] { ids[2], ids[1], ids[0] }, items.EnumerateArray().Select(x => x.GetProperty("userId").GetInt64()).ToArray());
        Assert.True(items[0].GetProperty("isPremium").GetBoolean());
    }

    [Fact]
    public async Task Locked_professionals_are_not_listed()
    {
        await using var host = await ApiHost.StartAsync();
        var tag = "Lk" + Guid.NewGuid().ToString("N")[..8];
        var (pro, _, id) = await host.RegisterProfessionalAsync("Lock", "Me", tag);
        await host.AddProfessionAsync(pro, extra: new() { ["city"] = tag });
        var (admin, _, _) = await host.CreateAdminAsync();
        Assert.Equal(1, Total(await SearchAsync(host, $"city={tag}")));
        await admin.PostAsync($"/api/admin/users/{id}/lock", new { isLocked = true });
        Assert.Equal(0, Total(await SearchAsync(host, $"city={tag}")));
    }

    [Fact]
    public async Task Results_expose_age_but_not_contact_details_or_birth_dates()
    {
        await using var host = await ApiHost.StartAsync();
        var tag = "Pv" + Guid.NewGuid().ToString("N")[..8];
        var (pro, email, _) = await host.RegisterProfessionalAsync("Priv", "Acy", tag, dob: "1980-01-01");
        await pro.PutAsync("/api/me/profile", ApiHost.ProfileBody("Priv", "Acy", "professional", tag, phone: "+961 70 111 222", dob: "1980-01-01"));
        await host.AddProfessionAsync(pro, extra: new() { ["city"] = tag });
        var text = (await SearchAsync(host, $"city={tag}")).GetRawText();
        Assert.False(text.Contains(email) || text.Contains("111 222") || text.Contains("1980-01-01") || text.Contains("dateOfBirth"));
        Assert.True(text.Contains("\"age\""));
    }

    [Fact]
    public async Task Top_professionals_returns_the_best_ranked_listings()
    {
        await using var host = await ApiHost.StartAsync();
        var (pro, _, _) = await host.RegisterProfessionalAsync();
        await host.AddProfessionAsync(pro);
        var (status, top) = await host.NewClient().GetAsync("/api/professionals/top?limit=3");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(top.GetArrayLength() is >= 1 and <= 3);
    }

    [Fact]
    public async Task Nonsensical_search_filters_are_rejected()
    {
        await using var host = await ApiHost.StartAsync();
        foreach (var query in new[] { "minAge=50&maxAge=20", "minCost=100&maxCost=10", "pageSize=500", "minStars=9", "sex=banana", "page=0" })
        {
            var (status, body) = await host.NewClient().GetAsync("/api/professionals/search?" + query);
            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Equal("VALIDATION_FAILED", TestClient.Error(body));
        }
    }
}
