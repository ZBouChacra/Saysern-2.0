using System.Net;
using System.Text.Json;

namespace Sayserne.Tests;

public class AdminApiTests
{
    [Fact]
    public async Task Anonymous_callers_get_401_on_every_admin_route()
    {
        await using var host = await ApiHost.StartAsync();
        var c = host.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/admin/users")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsync("/api/admin/users/1/lock", new { isLocked = true })).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsync("/api/admin/users/1/role", new { role = "admin" })).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/admin/settings")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/admin/audit")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/admin/auth/me")).Status);
    }

    [Fact]
    public async Task An_ordinary_website_session_cannot_reach_the_back_office()
    {
        await using var host = await ApiHost.StartAsync();
        var (client, _, id) = await host.RegisterAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/users")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync($"/api/admin/users/{id}/role", new { role = "admin" })).Status);
        Assert.Equal("user", (await client.GetAsync("/api/auth/me")).Body.GetProperty("user").GetProperty("role").GetString());
    }

    [Fact]
    public async Task A_super_admin_who_signs_in_to_the_website_gets_no_back_office_powers_there()
    {
        await using var host = await ApiHost.StartAsync();
        var (_, email, _) = await host.CreateAdminAsync();
        var site = host.NewClient();   // password only, as a customer would
        Assert.Equal(HttpStatusCode.OK, (await site.PostAsync("/api/auth/login", new { email, password = ApiHost.StrongPassword })).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await site.GetAsync("/api/admin/users")).Status);
    }

    [Fact]
    public async Task Admins_can_list_search_and_page_users_without_seeing_password_data()
    {
        await using var host = await ApiHost.StartAsync();
        var tag = Guid.NewGuid().ToString("N")[..10];
        for (var i = 0; i < 3; i++) await host.RegisterAsync(ApiHost.UniqueEmail($"zz{tag}"));
        var (admin, _, _) = await host.CreateAdminAsync();

        var (status, body) = await admin.GetAsync($"/api/admin/users?search=zz{tag}&page=1&pageSize=2");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(3, body.GetProperty("total").GetInt32());
        Assert.Equal(2, body.GetProperty("items").GetArrayLength());
        Assert.False(body.GetRawText().ToLowerInvariant().Contains("password"));

        var (_, page2) = await admin.GetAsync($"/api/admin/users?search=zz{tag}&page=2&pageSize=2");
        Assert.Equal(1, page2.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Search_text_is_matched_literally()
    {
        await using var host = await ApiHost.StartAsync();
        var (admin, _, _) = await host.CreateAdminAsync();
        Assert.Equal(0, (await admin.GetAsync("/api/admin/users?search=%25%25%25")).Body.GetProperty("total").GetInt32());   // '%' is not "everything"
    }

    [Fact]
    public async Task Locking_a_user_ends_their_session_and_blocks_login_until_unlocked()
    {
        await using var host = await ApiHost.StartAsync();
        var (userClient, email, id) = await host.RegisterAsync();
        var (admin, _, _) = await host.CreateAdminAsync();

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/admin/users/{id}/lock", new { isLocked = true })).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await userClient.GetAsync("/api/auth/me")).Status);   // existing session revoked
        var (s, b) = await host.NewClient().PostAsync("/api/auth/login", new { email, password = ApiHost.StrongPassword });
        Assert.Equal(HttpStatusCode.Forbidden, s);
        Assert.Equal("ACCOUNT_LOCKED", TestClient.Error(b));

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/admin/users/{id}/lock", new { isLocked = false })).Status);
        Assert.Equal(HttpStatusCode.OK, (await host.NewClient().PostAsync("/api/auth/login", new { email, password = ApiHost.StrongPassword })).Status);
    }

    [Fact]
    public async Task Making_a_user_a_super_admin_lets_them_into_the_back_office_but_only_after_two_factor_enrollment()
    {
        await using var host = await ApiHost.StartAsync();
        var (_, email, id) = await host.RegisterAsync();
        var (admin, _, _) = await host.CreateAdminAsync();

        var (status, body) = await admin.PostAsync($"/api/admin/users/{id}/role", new { role = "admin" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("admin", body.GetProperty("user").GetProperty("role").GetString());

        var fresh = host.NewClient();
        var (_, login) = await fresh.PostAsync("/api/admin/auth/login", new { email, password = ApiHost.StrongPassword });
        Assert.Equal("enrollment_required", TestClient.Str(login, "status"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await fresh.GetAsync("/api/admin/users")).Status);   // password alone is not a session
        var (second, _) = await host.AdminSignInAsync(email);
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/api/admin/users")).Status);
    }

    [Fact]
    public async Task Demoting_an_admin_takes_effect_immediately()
    {
        await using var host = await ApiHost.StartAsync();
        var (other, _, otherId) = await host.CreateAdminAsync();
        var (admin, _, _) = await host.CreateAdminAsync();
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/api/admin/users")).Status);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/admin/users/{otherId}/role", new { role = "user" })).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync("/api/admin/users")).Status);
    }

    [Fact]
    public async Task Admins_cannot_lock_demote_or_re_role_themselves()
    {
        await using var host = await ApiHost.StartAsync();
        var (admin, _, id) = await host.CreateAdminAsync();
        foreach (var (path, body) in new (string, object)[]
        {
            ($"/api/admin/users/{id}/lock", new { isLocked = true }),
            ($"/api/admin/users/{id}/role", new { role = "user" }),
            ($"/api/admin/users/{id}/roles", new { roleIds = new long[0] }),
            ($"/api/admin/users/{id}/two-factor/reset", new { }),
        })
        {
            var (s, b) = path.EndsWith("/roles") ? await admin.PutAsync(path, body) : await admin.PostAsync(path, body);
            Assert.Equal(HttpStatusCode.BadRequest, s);
            Assert.Equal("CANNOT_MODIFY_SELF", TestClient.Error(b));
        }
    }

    [Fact]
    public async Task Invalid_role_and_unknown_user_are_reported()
    {
        await using var host = await ApiHost.StartAsync();
        var (admin, _, _) = await host.CreateAdminAsync();
        var (_, _, victimId) = await host.RegisterAsync();
        var (s1, b1) = await admin.PostAsync($"/api/admin/users/{victimId}/role", new { role = "superuser" });
        Assert.Equal(HttpStatusCode.BadRequest, s1);
        Assert.Equal("INVALID_ROLE", TestClient.Error(b1));
        var (s2, b2) = await admin.PostAsync("/api/admin/users/999999999/lock", new { isLocked = true });
        Assert.Equal(HttpStatusCode.NotFound, s2);
        Assert.Equal("USER_NOT_FOUND", TestClient.Error(b2));
    }
}

public class AdminFlagsApiTests
{
    [Fact]
    public async Task Only_staff_with_the_users_manage_permission_can_set_listing_flags()
    {
        await using var host = await ApiHost.StartAsync();
        var (user, _, id) = await host.RegisterAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.NewClient().PostAsync($"/api/admin/users/{id}/flags", new { isStarred = true })).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.PostAsync($"/api/admin/users/{id}/flags", new { isStarred = true })).Status);
        var (reader, _, _) = await host.CreateStaffAsync("users.read");
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync($"/api/admin/users/{id}/flags", new { isStarred = true })).Status);
        var (manager, _, _) = await host.CreateStaffAsync("users.manage");
        var (status, body) = await manager.PostAsync($"/api/admin/users/{id}/flags", new { isStarred = true });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.GetProperty("user").GetProperty("isStarred").GetBoolean());
        Assert.False(body.GetProperty("user").GetProperty("isPremium").GetBoolean());   // premium is not a flag: only a paid premium batch makes a listing premium
        Assert.Equal(HttpStatusCode.NotFound, (await manager.PostAsync("/api/admin/users/99999999/flags", new { isStarred = true })).Status);
    }
}
