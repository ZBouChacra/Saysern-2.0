using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Sayserne.Api.Auth;
using Sayserne.Api.Platform;

namespace Sayserne.Tests;

public class TwoFactorApiTests
{
    private static long NowStep(TimeProvider? clock = null) => Totp.StepAt((clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds());

    /// <summary>A registered user promoted to super admin who has not signed in to the back office yet.</summary>
    private static async Task<(string Email, long Id)> NewAdminAsync(ApiHost host)
    {
        var (_, email, id) = await host.RegisterAsync();
        await host.Database.PromoteToAdminAsync(email);
        return (email, id);
    }

    private static async Task<(string Secret, IReadOnlyList<string> Recovery, long Step)> EnrollAsync(TestClient c, string email, TimeProvider? clock = null)
    {
        var (_, login) = await c.PostAsync("/api/admin/auth/login", new { email, password = ApiHost.StrongPassword });
        Assert.Equal("enrollment_required", TestClient.Str(login, "status"));
        var (es, enroll) = await c.PostAsync("/api/admin/auth/2fa/enroll");
        Assert.Equal(HttpStatusCode.OK, es);
        var secret = enroll.GetProperty("secret").GetString()!;
        var step = NowStep(clock);
        var (cs, confirm) = await c.PostAsync("/api/admin/auth/2fa/confirm", new { code = Totp.Code(secret, step) });
        Assert.Equal(HttpStatusCode.OK, cs);
        return (secret, confirm.GetProperty("recoveryCodes").EnumerateArray().Select(x => x.GetString()!).ToList(), step);
    }

    [Fact]
    public async Task A_password_alone_never_opens_a_session()
    {
        await using var host = await ApiHost.StartAsync();
        var (email, _) = await NewAdminAsync(host);
        var c = host.NewClient();
        var (status, body) = await c.PostAsync("/api/admin/auth/login", new { email, password = ApiHost.StrongPassword });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("enrollment_required", TestClient.Str(body, "status"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/admin/users")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/admin/auth/me")).Status);
    }

    [Fact]
    public async Task Wrong_passwords_customers_unknown_emails_and_google_only_accounts_all_get_the_same_answer()
    {
        await using var iso = await IsolatedHost.StartAsync(withGoogle: true);
        var host = iso.Host;
        var (email, _) = await NewAdminAsync(host);
        var (_, customerEmail, _) = await host.RegisterAsync();
        var google = host.NewClient();
        var gEmail = ApiHost.UniqueEmail("g");
        await host.Google!.SignInAsync(google, new FakeGoogle.Claims("sub-" + Guid.NewGuid().ToString("N"), gEmail));   // a passwordless account
        await host.Database.PromoteToAdminAsync(gEmail.ToLowerInvariant());                                           // ...even if it were an admin

        var answers = new List<string>();
        foreach (var (e, p) in new[] { (email, "wrong-password-1"), (customerEmail, ApiHost.StrongPassword), ("nobody@example.test", "whatever-123"), (gEmail, "anything-12345") })
        {
            var (status, body) = await host.NewClient().PostAsync("/api/admin/auth/login", new { email = e, password = p });
            Assert.Equal(HttpStatusCode.Unauthorized, status);
            answers.Add(body.GetRawText());
        }
        Assert.Equal(1, answers.Distinct().Count());
    }

    [Fact]
    public async Task Enrollment_requires_a_correct_code_and_hands_out_ten_single_use_recovery_codes()
    {
        await using var host = await ApiHost.StartAsync();
        var (email, _) = await NewAdminAsync(host);
        var c = host.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsync("/api/admin/auth/2fa/enroll")).Status);   // no pending sign-in yet
        Assert.Equal("PENDING_EXPIRED", TestClient.Error((await c.PostAsync("/api/admin/auth/2fa/confirm", new { code = "123456" })).Body));

        await c.PostAsync("/api/admin/auth/login", new { email, password = ApiHost.StrongPassword });
        var (_, enroll) = await c.PostAsync("/api/admin/auth/2fa/enroll");
        var secret = enroll.GetProperty("secret").GetString()!;
        Assert.Matches(new Regex("^[A-Z2-7]{32}$"), secret);
        var uri = enroll.GetProperty("otpauthUri").GetString()!;
        Assert.StartsWith("otpauth://totp/", uri);
        Assert.Contains(Uri.EscapeDataString(email), uri);
        Assert.Contains("secret=" + secret, uri);
        Assert.Contains("issuer=", uri);

        var (badStatus, bad) = await c.PostAsync("/api/admin/auth/2fa/confirm", new { code = "000000" });
        Assert.Equal(HttpStatusCode.Unauthorized, badStatus);
        Assert.Equal("INVALID_TWO_FACTOR_CODE", TestClient.Error(bad));
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/admin/users")).Status);   // still no session

        var (okStatus, ok) = await c.PostAsync("/api/admin/auth/2fa/confirm", new { code = Totp.Code(secret, NowStep()) });
        Assert.Equal(HttpStatusCode.OK, okStatus);
        var codes = ok.GetProperty("recoveryCodes").EnumerateArray().Select(x => x.GetString()!).ToList();
        Assert.Equal(10, codes.Count);
        Assert.Equal(10, codes.Distinct().Count());
        Assert.True(codes.All(x => Regex.IsMatch(x, "^[a-z2-9]{5}-[a-z2-9]{5}$")));
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/admin/users")).Status);
        Assert.Equal(10, (await c.GetAsync("/api/admin/auth/me")).Body.GetProperty("recoveryCodesRemaining").GetInt32());
    }

    [Fact]
    public async Task A_later_sign_in_needs_a_valid_code_and_a_code_cannot_be_used_twice()
    {
        await using var host = await ApiHost.StartAsync();
        var (email, _) = await NewAdminAsync(host);
        var (secret, _, usedStep) = await EnrollAsync(host.NewClient(), email);

        var c = host.NewClient();
        var (_, login) = await c.PostAsync("/api/admin/auth/login", new { email, password = ApiHost.StrongPassword });
        Assert.Equal("totp_required", TestClient.Str(login, "status"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsync("/api/admin/auth/verify", new { code = "000000" })).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsync("/api/admin/auth/verify", new { code = Totp.Code(secret, usedStep) })).Status);   // already used when enrolling: replay refused
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/admin/users")).Status);
        var (ok, _) = await c.PostAsync("/api/admin/auth/verify", new { code = Totp.Code(secret, usedStep + 1) });                                  // the next step is fine
        Assert.Equal(HttpStatusCode.OK, ok);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/admin/users")).Status);
        // ...and that step cannot be replayed either
        var again = host.NewClient();
        await again.PostAsync("/api/admin/auth/login", new { email, password = ApiHost.StrongPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, (await again.PostAsync("/api/admin/auth/verify", new { code = Totp.Code(secret, usedStep + 1) })).Status);
    }

    [Fact]
    public async Task Recovery_codes_work_once_each()
    {
        await using var host = await ApiHost.StartAsync();
        var (email, _) = await NewAdminAsync(host);
        var (_, recovery, _) = await EnrollAsync(host.NewClient(), email);

        var first = host.NewClient();
        await first.PostAsync("/api/admin/auth/login", new { email, password = ApiHost.StrongPassword });
        var (s, body) = await first.PostAsync("/api/admin/auth/verify", new { recoveryCode = recovery[0].ToUpperInvariant() });   // case and the dash are forgiven
        Assert.Equal(HttpStatusCode.OK, s);
        Assert.Equal(9, body.GetProperty("session").GetProperty("recoveryCodesRemaining").GetInt32());

        var second = host.NewClient();
        await second.PostAsync("/api/admin/auth/login", new { email, password = ApiHost.StrongPassword });
        var (s2, b2) = await second.PostAsync("/api/admin/auth/verify", new { recoveryCode = recovery[0] });
        Assert.Equal(HttpStatusCode.Unauthorized, s2);
        Assert.Equal("INVALID_TWO_FACTOR_CODE", TestClient.Error(b2));
        Assert.Equal(HttpStatusCode.OK, (await second.PostAsync("/api/admin/auth/verify", new { recoveryCode = recovery[1].Replace("-", "") })).Status);
    }

    [Fact]
    public async Task Too_many_wrong_codes_lock_the_admin_account_even_for_the_right_code()
    {
        await using var iso = await IsolatedHost.StartAsync();
        var host = iso.Host;
        var (email, id) = await NewAdminAsync(host);
        var (secret, _, step) = await EnrollAsync(host.NewClient(), email);

        var c = host.NewClient();
        await c.PostAsync("/api/admin/auth/login", new { email, password = ApiHost.StrongPassword });
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsync("/api/admin/auth/verify", new { code = "000000" })).Status);   // Auth.AdminMaxFailedAttempts = 3
        var (status, body) = await c.PostAsync("/api/admin/auth/verify", new { code = Totp.Code(secret, step + 1) });
        Assert.Equal(HttpStatusCode.Unauthorized, status);   // the pending sign-in is dead...
        var (ls, lb) = await host.NewClient().PostAsync("/api/admin/auth/login", new { email, password = ApiHost.StrongPassword });
        Assert.Equal(HttpStatusCode.Forbidden, ls);           // ...and so is the account for a while
        Assert.Equal("ACCOUNT_LOCKED", TestClient.Error(lb));
        var (other, _, _) = await host.CreateAdminAsync();
        var failures = (await other.GetAsync($"/api/admin/audit?action=admin.login.failure&actorId={id}")).Body.GetProperty("total").GetInt32();
        Assert.True(failures >= 3, $"expected the failures to be audited, found {failures}");
    }

    [Fact]
    public async Task A_forged_pending_sign_in_cookie_is_ignored()
    {
        await using var host = await ApiHost.StartAsync();
        var c = host.NewClient();
        c.Http.DefaultRequestHeaders.Add("Cookie", "sayserne.admin.pending=CfDJ8-not-a-real-protected-value");
        var (s1, b1) = await c.PostAsync("/api/admin/auth/verify", new { code = "123456" });
        Assert.Equal(HttpStatusCode.Unauthorized, s1);
        Assert.Equal("PENDING_EXPIRED", TestClient.Error(b1));
        Assert.Equal("PENDING_EXPIRED", TestClient.Error((await c.PostAsync("/api/admin/auth/2fa/enroll")).Body));
    }

    [Fact]
    public async Task Enrollment_cannot_be_started_again_once_confirmed()
    {
        await using var host = await ApiHost.StartAsync();
        var (email, _) = await NewAdminAsync(host);
        await EnrollAsync(host.NewClient(), email);
        var c = host.NewClient();
        await c.PostAsync("/api/admin/auth/login", new { email, password = ApiHost.StrongPassword });
        var (status, body) = await c.PostAsync("/api/admin/auth/2fa/enroll");   // an attacker with only the password cannot swap in their own authenticator
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("TWO_FACTOR_ALREADY_ENROLLED", TestClient.Error(body));
    }

    [Fact]
    public async Task Another_super_admin_can_reset_a_lost_device_but_nobody_else_can()
    {
        await using var host = await ApiHost.StartAsync();
        var (aEmail, aId) = await NewAdminAsync(host);
        var (oldSecret, oldRecovery, oldStep) = await EnrollAsync(host.NewClient(), aEmail);
        var (b, _, _) = await host.CreateAdminAsync();
        var (manager, _, _) = await host.CreateStaffAsync("users.manage");

        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsync($"/api/admin/users/{aId}/two-factor/reset")).Status);   // needs admins.manage
        Assert.Equal(HttpStatusCode.NoContent, (await b.PostAsync($"/api/admin/users/{aId}/two-factor/reset")).Status);

        var c = host.NewClient();
        var (_, login) = await c.PostAsync("/api/admin/auth/login", new { email = aEmail, password = ApiHost.StrongPassword });
        Assert.Equal("enrollment_required", TestClient.Str(login, "status"));
        var (_, enroll) = await c.PostAsync("/api/admin/auth/2fa/enroll");
        Assert.NotEqual(oldSecret, enroll.GetProperty("secret").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsync("/api/admin/auth/2fa/confirm", new { code = Totp.Code(oldSecret, oldStep + 1) })).Status);   // the lost device's codes are useless
        var (_, audit) = await b.GetAsync($"/api/admin/audit?action=admin.2fa.reset&entityType=user");
        Assert.True(audit.GetProperty("total").GetInt32() >= 1);
        Assert.False(string.IsNullOrEmpty(oldRecovery[0]));
    }

    [Fact]
    public async Task Regenerating_recovery_codes_needs_a_current_code_and_retires_the_old_set()
    {
        await using var host = await ApiHost.StartAsync();
        var (email, _) = await NewAdminAsync(host);
        var enrolling = host.NewClient();
        var (secret, oldCodes, step) = await EnrollAsync(enrolling, email);   // the enrolling client is signed in

        Assert.Equal(HttpStatusCode.Unauthorized, (await enrolling.PostAsync("/api/admin/auth/recovery-codes/regenerate", new { code = "000000" })).Status);
        var (status, body) = await enrolling.PostAsync("/api/admin/auth/recovery-codes/regenerate", new { code = Totp.Code(secret, step + 1) });
        Assert.Equal(HttpStatusCode.OK, status);
        var fresh = body.GetProperty("recoveryCodes").EnumerateArray().Select(x => x.GetString()!).ToList();
        Assert.Equal(10, fresh.Count);

        var c = host.NewClient();
        await c.PostAsync("/api/admin/auth/login", new { email, password = ApiHost.StrongPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsync("/api/admin/auth/verify", new { recoveryCode = oldCodes[0] })).Status);   // retired
        var d = host.NewClient();
        await d.PostAsync("/api/admin/auth/login", new { email, password = ApiHost.StrongPassword });
        Assert.Equal(HttpStatusCode.OK, (await d.PostAsync("/api/admin/auth/verify", new { recoveryCode = fresh[0] })).Status);
    }

    [Fact]
    public async Task The_admin_cookie_is_http_only_strict_and_separate_from_the_website_session()
    {
        await using var host = await ApiHost.StartAsync();
        var (email, _) = await NewAdminAsync(host);
        var c = host.NewClient();
        await c.PostAsync("/api/admin/auth/login", new { email, password = ApiHost.StrongPassword });
        var (_, enroll) = await c.PostAsync("/api/admin/auth/2fa/enroll");
        using var response = await c.Http.PostAsJsonAsync("/api/admin/auth/2fa/confirm", new { code = Totp.Code(enroll.GetProperty("secret").GetString()!, NowStep()) });
        var cookie = response.Headers.GetValues("Set-Cookie").First(v => v.StartsWith("sayserne.admin="));
        Assert.Contains("httponly", cookie.ToLowerInvariant());
        Assert.Contains("samesite=strict", cookie.ToLowerInvariant());

        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/auth/me")).Status);          // an admin session is not a website session
        var (site, _, _) = await host.RegisterAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await site.GetAsync("/api/admin/auth/me")).Status);   // and vice versa
    }

    [Fact]
    public async Task Locking_an_admin_ends_their_session_at_once()
    {
        await using var host = await ApiHost.StartAsync();
        var (victim, _, victimId) = await host.CreateAdminAsync();
        var (admin, _, _) = await host.CreateAdminAsync();
        Assert.Equal(HttpStatusCode.OK, (await victim.GetAsync("/api/admin/users")).Status);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/admin/users/{victimId}/lock", new { isLocked = true })).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await victim.GetAsync("/api/admin/users")).Status);
    }

    [Fact]
    public async Task An_idle_admin_session_ends_and_activity_keeps_it_alive_but_not_forever()
    {
        var clock = new ManualClock();
        await using var iso = await IsolatedHost.StartAsync(clock: clock);
        var host = iso.Host;
        var (email, _) = await NewAdminAsync(host);
        var c = host.NewClient();
        var (secret, _, _) = await EnrollAsync(c, email, clock);   // signs in at "now"

        async Task<HttpStatusCode> Ping(TestClient who) => (await who.GetAsync("/api/admin/users")).Status;
        Assert.True(await Ping(c) == HttpStatusCode.OK, "right after sign-in");
        clock.Now = clock.Now.AddMinutes(25);                                                    // the idle limit is 30
        Assert.True(await Ping(c) == HttpStatusCode.OK, "25 minutes idle must still be fine");   // and this request counts as activity
        clock.Now = clock.Now.AddMinutes(25);
        Assert.True(await Ping(c) == HttpStatusCode.OK, "50 minutes after sign-in, but never idle for 30");
        clock.Now = clock.Now.AddMinutes(31);
        Assert.True(await Ping(c) == HttpStatusCode.Unauthorized, "31 minutes idle must end the session");

        // a session that stays active still has an absolute limit (Auth.AdminMaxSessionHours = 12)
        var again = host.NewClient();
        await again.PostAsync("/api/admin/auth/login", new { email, password = ApiHost.StrongPassword });
        var (verifyStatus, verifyBody) = await again.PostAsync("/api/admin/auth/verify", new { code = Totp.Code(secret, NowStep(clock)) });
        Assert.True(verifyStatus == HttpStatusCode.OK, $"second sign-in after the clock moved: {verifyStatus} {verifyBody}");
        var endedAfterMinutes = -1;
        for (var minutes = 20; minutes <= 20 * 45; minutes += 20)
        {
            clock.Now = clock.Now.AddMinutes(20);
            if ((await again.GetAsync("/api/admin/users")).Status != HttpStatusCode.OK) { endedAfterMinutes = minutes; break; }
        }
        Assert.True(endedAfterMinutes > 0, "an always-active session must still end");
        Assert.True(endedAfterMinutes is > 11 * 60 and <= 12 * 60 + 20, $"ended after {endedAfterMinutes} minutes; expected about 12 hours");
    }
}

public class RolesApiTests
{
    private static string Name() => "Role " + Guid.NewGuid().ToString("N")[..10];

    [Fact]
    public async Task Permissions_and_the_starter_roles_are_listed_for_role_managers_only()
    {
        await using var host = await ApiHost.StartAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.NewClient().GetAsync("/api/admin/roles")).Status);
        var (reader, _, _) = await host.CreateStaffAsync("users.read");
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/admin/roles")).Status);
        var (manager, _, _) = await host.CreateStaffAsync("roles.manage");
        var (_, perms) = await manager.GetAsync("/api/admin/permissions");
        Assert.True(J.Any(perms, "code", "settings.manage") && J.Any(perms, "code", "audit.read"));
        var (_, roles) = await manager.GetAsync("/api/admin/roles");
        Assert.Equal("audit.read,users.read", string.Join(",", J.Find(roles, "name", "Support").GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).Order()));
    }

    [Fact]
    public async Task Roles_can_be_created_renamed_repermissioned_and_deleted_when_unused()
    {
        await using var host = await ApiHost.StartAsync();
        var (admin, _, _) = await host.CreateAdminAsync();
        var name = Name();
        var (cs, created) = await admin.PostAsync("/api/admin/roles", new { name, description = "d", permissions = new[] { "users.read", "audit.read" } });
        Assert.Equal(HttpStatusCode.Created, cs);
        var id = created.GetProperty("id").GetInt64();
        Assert.Equal(2, created.GetProperty("permissions").GetArrayLength());

        var (us, updated) = await admin.PutAsync($"/api/admin/roles/{id}", new { name = name + " v2", description = "d2", permissions = new[] { "catalog.manage" } });
        Assert.Equal(HttpStatusCode.OK, us);
        Assert.Equal(name + " v2", updated.GetProperty("name").GetString());
        Assert.Equal("catalog.manage", updated.GetProperty("permissions")[0].GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/admin/roles/{id}")).Status);
        Assert.False(J.Any((await admin.GetAsync("/api/admin/roles")).Body, "name", name + " v2"));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/api/admin/roles/{id}")).Status);
    }

    [Fact]
    public async Task Role_input_is_validated()
    {
        await using var host = await ApiHost.StartAsync();
        var (admin, _, _) = await host.CreateAdminAsync();
        var name = Name();
        await admin.PostAsync("/api/admin/roles", new { name, permissions = new string[0] });
        var (s1, b1) = await admin.PostAsync("/api/admin/roles", new { name = name.ToUpperInvariant(), permissions = new string[0] });   // names are unique, case-insensitively
        Assert.Equal(HttpStatusCode.Conflict, s1);
        Assert.Equal("DUPLICATE_ROLE", TestClient.Error(b1));
        Assert.Equal("INVALID_PERMISSION", TestClient.Error((await admin.PostAsync("/api/admin/roles", new { name = Name(), permissions = new[] { "users.read", "world.domination" } })).Body));
        Assert.Equal("INVALID_NAMES", TestClient.Error((await admin.PostAsync("/api/admin/roles", new { name = "  ", permissions = new string[0] })).Body));
        Assert.Equal("ROLE_NOT_FOUND", TestClient.Error((await admin.PutAsync("/api/admin/roles/999999999", new { name = Name(), permissions = new string[0] })).Body));
        Assert.Equal("ROLE_NOT_FOUND", TestClient.Error((await admin.PutAsync("/api/admin/users/1/roles", new { roleIds = new[] { 999999999L } })).Body));
    }

    [Fact]
    public async Task A_role_in_use_cannot_be_deleted()
    {
        await using var host = await ApiHost.StartAsync();
        var (admin, _, _) = await host.CreateAdminAsync();
        var (_, role) = await admin.PostAsync("/api/admin/roles", new { name = Name(), permissions = new[] { "users.read" } });
        var roleId = role.GetProperty("id").GetInt64();
        var (_, _, userId) = await host.RegisterAsync();
        await admin.PutAsync($"/api/admin/users/{userId}/roles", new { roleIds = new[] { roleId } });
        var (status, body) = await admin.DeleteAsync($"/api/admin/roles/{roleId}");
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("ROLE_IN_USE", TestClient.Error(body));
        await admin.PutAsync($"/api/admin/users/{userId}/roles", new { roleIds = new long[0] });
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/admin/roles/{roleId}")).Status);
    }

    [Fact]
    public async Task Staff_can_do_exactly_what_their_role_allows()
    {
        await using var host = await ApiHost.StartAsync();
        var (reader, _, _) = await host.CreateStaffAsync("users.read");
        var (_, _, targetId) = await host.RegisterAsync();
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/admin/users")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync($"/api/admin/users/{targetId}/lock", new { isLocked = true })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync($"/api/admin/users/{targetId}/role", new { role = "admin" })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/admin/settings")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/admin/audit")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsync("/api/admin/translations/en/error.SLOT_TAKEN", new { value = "x" })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync("/api/admin/catalog/categories", new { name = "x" })).Status);

        var me = (await reader.GetAsync("/api/admin/auth/me")).Body;
        Assert.False(me.GetProperty("isSuper").GetBoolean());
        Assert.Equal(new[] { "users.read" }, me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!).ToArray());
    }

    [Fact]
    public async Task A_super_admin_holds_every_permission()
    {
        await using var host = await ApiHost.StartAsync();
        var (admin, _, _) = await host.CreateAdminAsync();
        var me = (await admin.GetAsync("/api/admin/auth/me")).Body;
        Assert.True(me.GetProperty("isSuper").GetBoolean());
        var all = (await admin.GetAsync("/api/admin/permissions")).Body.EnumerateArray().Select(p => p.GetProperty("code").GetString()!).Order().ToArray();
        Assert.Equal(all, me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!).Order().ToArray());
    }

    [Fact]
    public async Task Changing_a_role_or_removing_it_takes_effect_on_open_sessions_immediately()
    {
        await using var host = await ApiHost.StartAsync();
        var (admin, _, _) = await host.CreateAdminAsync();
        var (_, role) = await admin.PostAsync("/api/admin/roles", new { name = Name(), permissions = new[] { "users.read", "audit.read" } });
        var roleId = role.GetProperty("id").GetInt64();
        var (_, email, userId) = await host.RegisterAsync();
        await admin.PutAsync($"/api/admin/users/{userId}/roles", new { roleIds = new[] { roleId } });
        var (staff, _) = await host.AdminSignInAsync(email);

        Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync("/api/admin/audit")).Status);
        await admin.PutAsync($"/api/admin/roles/{roleId}", new { name = role.GetProperty("name").GetString(), permissions = new[] { "users.read" } });   // audit.read revoked
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.GetAsync("/api/admin/audit")).Status);
        Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync("/api/admin/users")).Status);
        await admin.PutAsync($"/api/admin/users/{userId}/roles", new { roleIds = new long[0] });                                                          // no role left: no back office
        Assert.Equal(HttpStatusCode.Unauthorized, (await staff.GetAsync("/api/admin/users")).Status);
    }

    [Fact]
    public async Task Customers_and_role_less_users_cannot_enter_the_back_office()
    {
        await using var host = await ApiHost.StartAsync();
        var (_, email, _) = await host.RegisterAsync();
        var (status, body) = await host.NewClient().PostAsync("/api/admin/auth/login", new { email, password = ApiHost.StrongPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal("INVALID_CREDENTIALS", TestClient.Error(body));
    }

    [Fact]
    public async Task Only_staff_who_manage_roles_can_assign_them_and_the_list_shows_role_names()
    {
        await using var host = await ApiHost.StartAsync();
        var (reader, _, _) = await host.CreateStaffAsync("users.read");
        var (admin, _, _) = await host.CreateAdminAsync();
        var (_, _, userId) = await host.RegisterAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsync($"/api/admin/users/{userId}/roles", new { roleIds = new long[0] })).Status);
        var (_, role) = await admin.PostAsync("/api/admin/roles", new { name = Name(), permissions = new string[0] });
        var (s, assigned) = await admin.PutAsync($"/api/admin/users/{userId}/roles", new { roleIds = new[] { role.GetProperty("id").GetInt64() } });
        Assert.Equal(HttpStatusCode.OK, s);
        Assert.Equal(role.GetProperty("name").GetString(), assigned.GetProperty("roles")[0].GetProperty("name").GetString());
        var listed = (await reader.GetAsync($"/api/admin/users?search={Uri.EscapeDataString("")}&pageSize=100")).Body;
        _ = listed;
    }
}

public class AuditApiTests
{
    [Fact]
    public async Task Back_office_changes_are_recorded_with_who_what_and_when()
    {
        await using var iso = await IsolatedHost.StartAsync();
        var host = iso.Host;
        var (admin, adminEmail, adminId) = await host.CreateAdminAsync();
        var (_, _, userId) = await host.RegisterAsync();
        var (_, role) = await admin.PostAsync("/api/admin/roles", new { name = "Audited", permissions = new[] { "users.read" } });

        await admin.PostAsync($"/api/admin/users/{userId}/lock", new { isLocked = true });
        await admin.PostAsync($"/api/admin/users/{userId}/lock", new { isLocked = false });
        await admin.PostAsync($"/api/admin/users/{userId}/flags", new { isStarred = true });
        await admin.PutAsync($"/api/admin/users/{userId}/roles", new { roleIds = new[] { role.GetProperty("id").GetInt64() } });
        await admin.PutAsync($"/api/admin/lookups/sex/values/male", new { names = new Dictionary<string, string> { ["en"] = "Male" } });
        await admin.PostAsync("/api/admin/catalog/categories", new { name = "Audit cat" });
        await admin.PutAsync("/api/admin/settings/Chat.MaxMessageLength", new { value = "300" });

        var (_, all) = await admin.GetAsync("/api/admin/audit?pageSize=200");
        var actions = all.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("action").GetString()!).ToHashSet();
        foreach (var expected in new[] { "users.lock", "users.unlock", "users.flags", "users.roles", "lookups.value.save", "catalog.category.create", "settings.update", "roles.create", "admin.login.success", "admin.2fa.enrolled" })
            Assert.True(actions.Contains(expected), $"missing audit action {expected}; have {string.Join(", ", actions)}");

        var lockEntry = (await admin.GetAsync("/api/admin/audit?action=users.lock")).Body.GetProperty("items")[0];
        Assert.Equal(adminId, lockEntry.GetProperty("actorId").GetInt64());
        Assert.Contains(adminEmail, lockEntry.GetProperty("actorName").GetString());
        Assert.Equal("user", lockEntry.GetProperty("entityType").GetString());
        Assert.Equal(userId.ToString(), lockEntry.GetProperty("entityId").GetString());
        Assert.True(DateTime.UtcNow - lockEntry.GetProperty("occurredAt").GetDateTime().ToUniversalTime() < TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task A_failed_change_leaves_no_audit_trace_of_a_change_that_did_not_happen()
    {
        await using var iso = await IsolatedHost.StartAsync();
        var (admin, _, _) = await iso.Host.CreateAdminAsync();
        await admin.PutAsync("/api/admin/settings/Auth.PasswordMinLength", new { value = "3" });   // refused
        await admin.PostAsync("/api/admin/users/999999999/lock", new { isLocked = true });         // refused
        Assert.Equal(0, (await admin.GetAsync("/api/admin/audit?action=settings.update")).Body.GetProperty("total").GetInt32());
        Assert.Equal(0, (await admin.GetAsync("/api/admin/audit?action=users.lock")).Body.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task The_audit_log_can_be_filtered_searched_and_paged()
    {
        await using var iso = await IsolatedHost.StartAsync();
        var (admin, _, adminId) = await iso.Host.CreateAdminAsync();
        var (other, _, otherId) = await iso.Host.CreateAdminAsync();
        for (var i = 0; i < 5; i++) await admin.PutAsync("/api/admin/settings/Chat.MaxMessageLength", new { value = (500 + i).ToString() });
        await other.PutAsync("/api/admin/settings/Alerts.MaxPerUser", new { value = "7" });

        var byActor = (await admin.GetAsync($"/api/admin/audit?actorId={otherId}&action=settings.update")).Body;
        Assert.Equal(1, byActor.GetProperty("total").GetInt32());
        var paged = (await admin.GetAsync($"/api/admin/audit?actorId={adminId}&action=settings.update&pageSize=2&page=3")).Body;
        Assert.Equal(5, paged.GetProperty("total").GetInt32());
        Assert.Equal(1, paged.GetProperty("items").GetArrayLength());
        Assert.Equal(1, (await admin.GetAsync("/api/admin/audit?search=Alerts.MaxPerUser")).Body.GetProperty("total").GetInt32());
        Assert.Equal(0, (await admin.GetAsync("/api/admin/audit?search=%25%25")).Body.GetProperty("total").GetInt32());   // literal search, not a wildcard
        Assert.True((await admin.GetAsync($"/api/admin/audit?entityType=setting&from={Uri.EscapeDataString(DateTime.UtcNow.AddMinutes(-10).ToString("o"))}")).Body.GetProperty("total").GetInt32() >= 6);
        Assert.Equal(0, (await admin.GetAsync($"/api/admin/audit?entityType=setting&to={Uri.EscapeDataString(DateTime.UtcNow.AddMinutes(-10).ToString("o"))}")).Body.GetProperty("total").GetInt32());
        var newestFirst = (await admin.GetAsync("/api/admin/audit?pageSize=50")).Body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("occurredAt").GetDateTime()).ToList();
        Assert.Equal(newestFirst.OrderByDescending(d => d).ToList(), newestFirst);
    }

    [Fact]
    public async Task Only_staff_with_audit_read_can_see_it_and_nobody_can_change_it_through_the_api()
    {
        await using var host = await ApiHost.StartAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.NewClient().GetAsync("/api/admin/audit")).Status);
        var (reader, _, _) = await host.CreateStaffAsync("audit.read");
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/admin/audit")).Status);
        var (admin, _, _) = await host.CreateAdminAsync();
        foreach (var method in new[] { "POST", "PUT", "DELETE", "PATCH" })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), "/api/admin/audit/1") { Content = JsonContent.Create(new { summary = "tampered" }) };
            using var response = await admin.Http.SendAsync(request);
            Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, $"{method} -> {response.StatusCode}");
        }
    }

    [Fact]
    public async Task Sign_in_events_are_recorded_including_failures_and_recovery_code_use()
    {
        await using var iso = await IsolatedHost.StartAsync();
        var host = iso.Host;
        var (_, email, id) = await host.CreateAdminAsync();
        var c = host.NewClient();
        await c.PostAsync("/api/admin/auth/login", new { email, password = "wrong-password-1" });
        await c.PostAsync("/api/admin/auth/login", new { email, password = ApiHost.StrongPassword });
        await c.PostAsync("/api/admin/auth/verify", new { recoveryCode = ApiHost.RecoveryCodesOf(email)[0] });
        var (admin, _, _) = await host.CreateAdminAsync();
        var actions = (await admin.GetAsync($"/api/admin/audit?actorId={id}&pageSize=100")).Body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("action").GetString()).ToList();
        Assert.True(actions.Contains("admin.login.failure") && actions.Contains("admin.login.success") && actions.Contains("admin.recovery_code.used") && actions.Contains("admin.2fa.enrolled"), string.Join(", ", actions));
    }
}

public class CatalogAdminApiTests
{
    private static string Name() => "Cat " + Guid.NewGuid().ToString("N")[..10];

    [Fact]
    public async Task Catalog_management_needs_the_catalog_permission()
    {
        await using var host = await ApiHost.StartAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.NewClient().PostAsync("/api/admin/catalog/categories", new { name = "x" })).Status);
        var (other, _, _) = await host.CreateStaffAsync("users.read");
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsync("/api/admin/catalog/categories", new { name = "x" })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync("/api/admin/catalog/categories")).Status);
        var (content, _, _) = await host.CreateStaffAsync("catalog.manage");
        Assert.Equal(HttpStatusCode.OK, (await content.GetAsync("/api/admin/catalog/categories")).Status);
    }

    [Fact]
    public async Task A_new_category_and_service_flow_through_to_the_public_catalog_and_to_professionals()
    {
        await using var host = await ApiHost.StartAsync();
        var (admin, _, _) = await host.CreateAdminAsync();
        var name = Name();
        var (cs, category) = await admin.PostAsync("/api/admin/catalog/categories", new { name, nameAr = "فئة", description = "d", icon = "star" });
        Assert.Equal(HttpStatusCode.Created, cs);
        var categoryId = category.GetProperty("id").GetInt64();
        var serviceName = Name();
        var (ss, service) = await admin.PostAsync("/api/admin/catalog/services", new { categoryId, name = serviceName, nameAr = "خدمة" });
        Assert.Equal(HttpStatusCode.Created, ss);
        var serviceId = service.GetProperty("id").GetInt64();

        var c = host.NewClient();
        Assert.True(J.Any((await c.GetAsync("/api/catalog/categories")).Body, "name", name));
        Assert.Equal(serviceName, (await c.GetAsync($"/api/catalog/services?categoryId={categoryId}")).Body[0].GetProperty("name").GetString());

        var (pro, _, _) = await host.RegisterProfessionalAsync(city: "Catalogville");
        Assert.Equal(HttpStatusCode.Created, (await pro.PostAsync("/api/me/professions", new { categoryId, serviceId, city = "Catalogville" })).Status);
        Assert.Equal(1, (await c.GetAsync($"/api/professionals/search?serviceId={serviceId}")).Body.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Blocking_a_category_or_service_hides_it_and_stops_new_listings()
    {
        await using var host = await ApiHost.StartAsync();
        var (admin, _, _) = await host.CreateAdminAsync();
        var (_, category) = await admin.PostAsync("/api/admin/catalog/categories", new { name = Name() });
        var categoryId = category.GetProperty("id").GetInt64();
        var (_, service) = await admin.PostAsync("/api/admin/catalog/services", new { categoryId, name = Name() });
        var serviceId = service.GetProperty("id").GetInt64();
        var (pro, _, _) = await host.RegisterProfessionalAsync();

        await admin.PutAsync($"/api/admin/catalog/services/{serviceId}", new { categoryId, name = service.GetProperty("name").GetString(), isBlocked = true });
        Assert.Equal(0, (await host.NewClient().GetAsync($"/api/catalog/services?categoryId={categoryId}")).Body.GetArrayLength());
        Assert.Equal(1, (await admin.GetAsync($"/api/admin/catalog/services?categoryId={categoryId}")).Body.GetArrayLength());   // admins still see it
        Assert.Equal("INVALID_SERVICE", TestClient.Error((await pro.PostAsync("/api/me/professions", new { categoryId, serviceId })).Body));

        await admin.PutAsync($"/api/admin/catalog/services/{serviceId}", new { categoryId, name = service.GetProperty("name").GetString(), isBlocked = false });
        await admin.PutAsync($"/api/admin/catalog/categories/{categoryId}", new { name = category.GetProperty("name").GetString(), isBlocked = true });
        Assert.False((await host.NewClient().GetAsync("/api/catalog/categories")).Body.EnumerateArray().Any(x => x.GetProperty("id").GetInt64() == categoryId));
        Assert.Equal("INVALID_SERVICE", TestClient.Error((await pro.PostAsync("/api/me/professions", new { categoryId, serviceId })).Body));
    }

    [Fact]
    public async Task Catalog_input_is_validated()
    {
        await using var host = await ApiHost.StartAsync();
        var (admin, _, _) = await host.CreateAdminAsync();
        var name = Name();
        var (_, category) = await admin.PostAsync("/api/admin/catalog/categories", new { name });
        var categoryId = category.GetProperty("id").GetInt64();
        var (s1, b1) = await admin.PostAsync("/api/admin/catalog/categories", new { name = name.ToUpperInvariant() });
        Assert.Equal(HttpStatusCode.Conflict, s1);
        Assert.Equal("DUPLICATE_NAME", TestClient.Error(b1));
        Assert.Equal("INVALID_NAMES", TestClient.Error((await admin.PostAsync("/api/admin/catalog/categories", new { name = " " })).Body));
        Assert.Equal("CATEGORY_NOT_FOUND", TestClient.Error((await admin.PutAsync("/api/admin/catalog/categories/999999999", new { name = Name() })).Body));
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/admin/catalog/services", new { name = Name() })).Status);          // no category
        Assert.Equal("CATEGORY_NOT_FOUND", TestClient.Error((await admin.PostAsync("/api/admin/catalog/services", new { categoryId = 999999999, name = Name() })).Body));
        var serviceName = Name();
        await admin.PostAsync("/api/admin/catalog/services", new { categoryId, name = serviceName });
        Assert.Equal("DUPLICATE_NAME", TestClient.Error((await admin.PostAsync("/api/admin/catalog/services", new { categoryId, name = serviceName })).Body));
        var (_, other) = await admin.PostAsync("/api/admin/catalog/categories", new { name = Name() });
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsync("/api/admin/catalog/services", new { categoryId = other.GetProperty("id").GetInt64(), name = serviceName })).Status);   // same name, other category: fine
        Assert.Equal("SERVICE_NOT_FOUND", TestClient.Error((await admin.PutAsync("/api/admin/catalog/services/999999999", new { categoryId, name = Name() })).Body));
    }
}
