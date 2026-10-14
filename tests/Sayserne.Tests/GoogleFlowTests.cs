using System.Net;
using Microsoft.AspNetCore.WebUtilities;
using Sayserne.Api.Data;

namespace Sayserne.Tests;

public class GoogleFlowTests
{
    private static FakeGoogle.Claims NewGoogleUser(string? email = null, bool verified = true) =>
        new($"sub-{Guid.NewGuid():N}", email ?? ApiHost.UniqueEmail("g"), verified);

    private static string Location(HttpResponseMessage r) => r.Headers.Location?.OriginalString ?? "";

    [Fact]
    public async Task Providers_reports_whether_google_is_configured()
    {
        await using var off = await ApiHost.StartAsync();
        await using var on = await ApiHost.StartAsync(withGoogle: true);
        Assert.False((await off.NewClient().GetAsync("/api/auth/providers")).Body.GetProperty("google").GetBoolean());
        Assert.True((await on.NewClient().GetAsync("/api/auth/providers")).Body.GetProperty("google").GetBoolean());
    }

    [Fact]
    public async Task Start_returns_503_when_google_is_not_configured()
    {
        await using var host = await ApiHost.StartAsync();
        using var response = await host.NewClient().Http.GetAsync("/api/auth/google/start");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Start_redirects_to_google_with_state_nonce_and_pkce()
    {
        await using var host = await ApiHost.StartAsync(withGoogle: true);
        var c = host.NewClient();
        using var response = await c.Http.GetAsync("/api/auth/google/start");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.StartsWith(host.Google!.BaseUrl + "/auth", location.AbsoluteUri);
        var q = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("code", q["response_type"].ToString());
        Assert.Equal(FakeGoogle.ClientId, q["client_id"].ToString());
        Assert.Equal("S256", q["code_challenge_method"].ToString());
        Assert.Contains("openid", q["scope"].ToString());
        Assert.Contains("email", q["scope"].ToString());
        Assert.True(q["state"].ToString().Length >= 32 && q["nonce"].ToString().Length >= 32 && q["code_challenge"].ToString().Length >= 32);
        Assert.Equal("http://localhost:5173/api/auth/google/callback", q["redirect_uri"].ToString());

        var flowCookie = response.Headers.GetValues("Set-Cookie").First(v => v.StartsWith("sayserne.g.flow="));
        Assert.Contains("httponly", flowCookie.ToLowerInvariant());
        Assert.False(flowCookie.Contains(q["state"].ToString()), "state must not be readable in the cookie");
    }

    [Fact]
    public async Task A_new_google_user_gets_an_account_and_a_session()
    {
        await using var host = await ApiHost.StartAsync(withGoogle: true);
        var c = host.NewClient();
        var claims = NewGoogleUser();
        using var callback = await host.Google!.SignInAsync(c, claims);
        Assert.Equal("/", Location(callback));
        var (status, body) = await c.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, status);
        var user = body.GetProperty("user");
        Assert.Equal(claims.Email.ToLowerInvariant(), user.GetProperty("email").GetString());
        Assert.True(user.GetProperty("emailVerified").GetBoolean());
        Assert.Equal("Gina", user.GetProperty("firstName").GetString());
    }

    [Fact]
    public async Task A_returning_google_user_signs_into_the_same_account()
    {
        await using var host = await ApiHost.StartAsync(withGoogle: true);
        var claims = NewGoogleUser();
        var first = host.NewClient();
        await host.Google!.SignInAsync(first, claims);
        var id1 = (await first.GetAsync("/api/auth/me")).Body.GetProperty("user").GetProperty("id").GetInt64();

        var second = host.NewClient();
        await host.Google.SignInAsync(second, claims);
        var id2 = (await second.GetAsync("/api/auth/me")).Body.GetProperty("user").GetProperty("id").GetInt64();
        Assert.Equal(id1, id2);
    }

    [Fact]
    public async Task Return_url_is_honoured_only_for_local_paths()
    {
        await using var host = await ApiHost.StartAsync(withGoogle: true);
        using var local = await host.Google!.SignInAsync(host.NewClient(), NewGoogleUser(), "/api/auth/google/start?returnUrl=%2Fprofile");
        Assert.Equal("/profile", Location(local));
        using var evil = await host.Google.SignInAsync(host.NewClient(), NewGoogleUser(), "/api/auth/google/start?returnUrl=https%3A%2F%2Fevil.example");
        Assert.Equal("/", Location(evil));
    }

    [Fact]
    public async Task A_state_mismatch_is_rejected_and_creates_no_session()
    {
        await using var host = await ApiHost.StartAsync(withGoogle: true);
        var c = host.NewClient();
        using var callback = await host.Google!.SignInAsync(c, NewGoogleUser(), stateOverride: "attacker-chosen-state");
        Assert.Equal("/login?error=google_state", Location(callback));
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/auth/me")).Status);
    }

    [Fact]
    public async Task A_callback_without_a_started_flow_is_rejected()
    {
        await using var host = await ApiHost.StartAsync(withGoogle: true);
        using var response = await host.NewClient().Http.GetAsync("/api/auth/google/callback?code=abc&state=xyz");
        Assert.Equal("/login?error=google_state", Location(response));
    }

    [Fact]
    public async Task The_flow_cookie_is_single_use()
    {
        await using var host = await ApiHost.StartAsync(withGoogle: true);
        var c = host.NewClient();
        using var start = await c.Http.GetAsync("/api/auth/google/start");
        var q = QueryHelpers.ParseQuery(start.Headers.Location!.Query);
        var code1 = host.Google!.IssueCode(NewGoogleUser(), q["nonce"]!, q["code_challenge"]!);
        using var first = await c.Http.GetAsync($"/api/auth/google/callback?code={code1}&state={q["state"]}");
        Assert.Equal("/", Location(first));

        var anon = host.NewClient();   // attacker replays the same callback URL in a fresh browser
        using var replay = await anon.Http.GetAsync($"/api/auth/google/callback?code={code1}&state={q["state"]}");
        Assert.Equal("/login?error=google_state", Location(replay));
    }

    [Fact]
    public async Task User_denial_at_google_is_reported()
    {
        await using var host = await ApiHost.StartAsync(withGoogle: true);
        var c = host.NewClient();
        using var start = await c.Http.GetAsync("/api/auth/google/start");
        var state = QueryHelpers.ParseQuery(start.Headers.Location!.Query)["state"];
        using var callback = await c.Http.GetAsync($"/api/auth/google/callback?error=access_denied&state={state}");
        Assert.Equal("/login?error=google_denied", Location(callback));
    }

    [Fact]
    public async Task Tokens_with_a_wrong_nonce_audience_issuer_signature_or_expiry_are_rejected()
    {
        await using var host = await ApiHost.StartAsync(withGoogle: true);
        var bad = new[]
        {
            NewGoogleUser() with { NonceOverride = "attacker-nonce" },
            NewGoogleUser() with { Audience = "another-app" },
            NewGoogleUser() with { Issuer = "https://evil.example" },
            NewGoogleUser() with { SignWithOtherKey = true },
            NewGoogleUser() with { Lifetime = TimeSpan.FromMinutes(-10) },
        };
        foreach (var claims in bad)
        {
            var c = host.NewClient();
            using var callback = await host.Google!.SignInAsync(c, claims);
            Assert.Equal("/login?error=google_token", Location(callback));
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/auth/me")).Status);
        }
    }

    [Fact]
    public async Task An_unverified_google_email_is_refused_and_creates_no_account()
    {
        await using var host = await ApiHost.StartAsync(withGoogle: true);
        var claims = NewGoogleUser(verified: false);
        var c = host.NewClient();
        using var callback = await host.Google!.SignInAsync(c, claims);
        Assert.Equal("/login?error=google_email_unverified", Location(callback));
        Assert.Null(await new UserRepository((await TestEnv.GetDatabaseAsync()).Executor()).GetByEmailAsync(claims.Email.ToLowerInvariant()));
    }

    [Fact]
    public async Task A_replayed_authorization_code_is_refused()
    {
        await using var host = await ApiHost.StartAsync(withGoogle: true);
        var c = host.NewClient();
        using var start = await c.Http.GetAsync("/api/auth/google/start");
        var q = QueryHelpers.ParseQuery(start.Headers.Location!.Query);
        // code was never issued by (fake) Google, so the token endpoint refuses it
        using var callback = await c.Http.GetAsync($"/api/auth/google/callback?code=forged&state={q["state"]}");
        Assert.Equal("/login?error=google_exchange", Location(callback));
    }

    [Fact]
    public async Task A_google_login_with_a_registered_email_is_not_merged_and_needs_the_password()
    {
        await using var host = await ApiHost.StartAsync(withGoogle: true);
        var (_, email, localId) = await host.RegisterAsync();
        var claims = NewGoogleUser(email);

        var c = host.NewClient();
        using var callback = await host.Google!.SignInAsync(c, claims);
        Assert.Equal("/link-google", Location(callback));
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/auth/me")).Status);   // no session: nothing was merged

        var (badStatus, bad) = await c.PostAsync("/api/auth/google/link-pending", new { password = "wrong-password-1" });
        Assert.Equal(HttpStatusCode.Unauthorized, badStatus);
        Assert.Equal("INVALID_CREDENTIALS", TestClient.Error(bad));
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/auth/me")).Status);

        var (okStatus, ok) = await c.PostAsync("/api/auth/google/link-pending", new { password = ApiHost.StrongPassword });
        Assert.Equal(HttpStatusCode.OK, okStatus);
        Assert.Equal(localId, ok.GetProperty("user").GetProperty("id").GetInt64());
        Assert.True(ok.GetProperty("user").GetProperty("emailVerified").GetBoolean());

        var later = host.NewClient();   // from now on Google alone signs in to that same account
        using var again = await host.Google.SignInAsync(later, claims);
        Assert.Equal("/", Location(again));
        Assert.Equal(localId, (await later.GetAsync("/api/auth/me")).Body.GetProperty("user").GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task Link_pending_without_a_pending_link_is_refused()
    {
        await using var host = await ApiHost.StartAsync(withGoogle: true);
        var (status, body) = await host.NewClient().PostAsync("/api/auth/google/link-pending", new { password = ApiHost.StrongPassword });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("LINK_EXPIRED", TestClient.Error(body));
    }

    [Fact]
    public async Task Guessing_the_password_during_linking_locks_the_account()
    {
        await using var host = await ApiHost.StartAsync(withGoogle: true, overrides: new() { ["Auth:MaxFailedAttempts"] = "3" });
        var (_, email, _) = await host.RegisterAsync();
        var c = host.NewClient();
        await host.Google!.SignInAsync(c, NewGoogleUser(email));
        for (var i = 0; i < 3; i++) await c.PostAsync("/api/auth/google/link-pending", new { password = "wrong-password-1" });
        var (status, body) = await c.PostAsync("/api/auth/google/link-pending", new { password = ApiHost.StrongPassword });
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("ACCOUNT_LOCKED", TestClient.Error(body));
    }

    [Fact]
    public async Task A_signed_in_user_can_link_google_explicitly_and_then_use_it()
    {
        await using var host = await ApiHost.StartAsync(withGoogle: true);
        var (client, _, id) = await host.RegisterAsync();
        var claims = NewGoogleUser();   // different email than the account: allowed because the user is signed in and Google verified it
        using var callback = await host.Google!.SignInAsync(client, claims, "/api/auth/google/start?mode=link&returnUrl=%2Fsettings");
        Assert.Equal("/settings", Location(callback));

        var other = host.NewClient();
        using var viaGoogle = await host.Google.SignInAsync(other, claims);
        Assert.Equal("/", Location(viaGoogle));
        Assert.Equal(id, (await other.GetAsync("/api/auth/me")).Body.GetProperty("user").GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task Link_mode_requires_a_signed_in_user()
    {
        await using var host = await ApiHost.StartAsync(withGoogle: true);
        using var response = await host.NewClient().Http.GetAsync("/api/auth/google/start?mode=link");
        Assert.Equal("/login?error=login_required", Location(response));
    }

    [Fact]
    public async Task A_google_identity_cannot_be_attached_to_two_accounts()
    {
        await using var host = await ApiHost.StartAsync(withGoogle: true);
        var claims = NewGoogleUser();
        await host.Google!.SignInAsync(host.NewClient(), claims);   // identity now belongs to its own new account

        var (client, _, _) = await host.RegisterAsync();
        using var callback = await host.Google.SignInAsync(client, claims, "/api/auth/google/start?mode=link");
        Assert.Contains("google=already_linked", Location(callback));
    }

    [Fact]
    public async Task A_locked_account_cannot_sign_in_with_google()
    {
        await using var host = await ApiHost.StartAsync(withGoogle: true);
        var claims = NewGoogleUser();
        var first = host.NewClient();
        await host.Google!.SignInAsync(first, claims);
        var id = (await first.GetAsync("/api/auth/me")).Body.GetProperty("user").GetProperty("id").GetInt64();
        var (admin, _, _) = await host.CreateAdminAsync();
        await admin.PostAsync($"/api/admin/users/{id}/lock", new { isLocked = true });

        var c = host.NewClient();
        using var callback = await host.Google.SignInAsync(c, claims);
        Assert.Equal("/login?error=account_locked", Location(callback));
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/auth/me")).Status);
    }
}
