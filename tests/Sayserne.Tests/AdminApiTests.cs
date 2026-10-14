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
    }

    [Fact]
    public async Task Regular_users_get_403_on_every_admin_route()
    {
        await using var host = await ApiHost.StartAsync();
        var (client, _, _) = await host.RegisterAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/users")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/admin/users/1/lock", new { isLocked = true })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/admin/users/1/role", new { role = "admin" })).Status);
    }

    [Fact]
    public async Task A_user_cannot_make_themselves_admin_by_calling_the_endpoint()
    {
        await using var host = await ApiHost.StartAsync();
        var (client, _, id) = await host.RegisterAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/api/admin/users/{id}/role", new { role = "admin" })).Status);
        Assert.Equal("user", (await client.GetAsync("/api/auth/me")).Body.GetProperty("user").GetProperty("role").GetString());
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
    public async Task Locking_a_user_ends_their_session_and_blocks_login_until_unlocked()
    {
        await using var host = await ApiHost.StartAsync();
        var (userClient, email, id) = await host.RegisterAsync();
        var (admin, _, _) = await host.CreateAdminAsync();

        var (lockStatus, _) = await admin.PostAsync($"/api/admin/users/{id}/lock", new { isLocked = true });
        Assert.Equal(HttpStatusCode.OK, lockStatus);
        Assert.Equal(HttpStatusCode.Unauthorized, (await userClient.GetAsync("/api/auth/me")).Status);   // existing session revoked
        var (s, b) = await host.NewClient().PostAsync("/api/auth/login", new { email, password = ApiHost.StrongPassword });
        Assert.Equal(HttpStatusCode.Forbidden, s);
        Assert.Equal("ACCOUNT_LOCKED", TestClient.Error(b));

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/admin/users/{id}/lock", new { isLocked = false })).Status);
        Assert.Equal(HttpStatusCode.OK, (await host.NewClient().PostAsync("/api/auth/login", new { email, password = ApiHost.StrongPassword })).Status);
    }

    [Fact]
    public async Task Promoting_a_user_gives_them_admin_access_after_they_sign_in_again()
    {
        await using var host = await ApiHost.StartAsync();
        var (userClient, email, id) = await host.RegisterAsync();
        var (admin, _, _) = await host.CreateAdminAsync();

        var (status, body) = await admin.PostAsync($"/api/admin/users/{id}/role", new { role = "admin" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("admin", body.GetProperty("user").GetProperty("role").GetString());

        Assert.Equal(HttpStatusCode.Unauthorized, (await userClient.GetAsync("/api/admin/users")).Status);   // stale role claim is not honoured
        var relogin = host.NewClient();
        await relogin.PostAsync("/api/auth/login", new { email, password = ApiHost.StrongPassword });
        Assert.Equal(HttpStatusCode.OK, (await relogin.GetAsync("/api/admin/users")).Status);
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
    public async Task Admins_cannot_lock_or_demote_themselves()
    {
        await using var host = await ApiHost.StartAsync();
        var (admin, _, id) = await host.CreateAdminAsync();
        var (s1, b1) = await admin.PostAsync($"/api/admin/users/{id}/lock", new { isLocked = true });
        var (s2, b2) = await admin.PostAsync($"/api/admin/users/{id}/role", new { role = "user" });
        Assert.Equal(HttpStatusCode.BadRequest, s1);
        Assert.Equal("CANNOT_MODIFY_SELF", TestClient.Error(b1));
        Assert.Equal(HttpStatusCode.BadRequest, s2);
        Assert.Equal("CANNOT_MODIFY_SELF", TestClient.Error(b2));
    }

    [Fact]
    public async Task Invalid_role_and_unknown_user_are_reported()
    {
        await using var host = await ApiHost.StartAsync();
        var (admin, _, _) = await host.CreateAdminAsync();
        var (_, victim, victimId) = await host.RegisterAsync();
        var (s1, b1) = await admin.PostAsync($"/api/admin/users/{victimId}/role", new { role = "superuser" });
        Assert.Equal(HttpStatusCode.BadRequest, s1);
        Assert.Equal("INVALID_ROLE", TestClient.Error(b1));
        var (s2, b2) = await admin.PostAsync("/api/admin/users/999999999/lock", new { isLocked = true });
        Assert.Equal(HttpStatusCode.NotFound, s2);
        Assert.Equal("USER_NOT_FOUND", TestClient.Error(b2));
    }
}
