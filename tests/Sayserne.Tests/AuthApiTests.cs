using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Sayserne.Api;
using Sayserne.Api.Data;

namespace Sayserne.Tests;

public class AuthApiTests
{
    [Fact]
    public async Task Registration_creates_an_account_and_signs_the_user_in()
    {
        await using var host = await ApiHost.StartAsync();
        var (client, email, _) = await host.RegisterAsync();
        var (status, body) = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, status);
        var user = body.GetProperty("user");
        Assert.Equal(email, user.GetProperty("email").GetString());
        Assert.Equal("user", user.GetProperty("role").GetString());
        Assert.False(user.GetProperty("emailVerified").GetBoolean());
    }

    [Fact]
    public async Task Responses_never_contain_password_material()
    {
        await using var host = await ApiHost.StartAsync();
        var (client, _, _) = await host.RegisterAsync();
        var (_, body) = await client.GetAsync("/api/auth/me");
        var text = body.GetRawText().ToLowerInvariant();
        Assert.False(text.Contains("password"));
        Assert.False(text.Contains("hash"));
    }

    [Fact]
    public async Task Password_is_stored_as_a_salted_hash()
    {
        await using var host = await ApiHost.StartAsync();
        var (_, email, _) = await host.RegisterAsync();
        var stored = await new UserRepository((await TestEnv.GetDatabaseAsync()).Executor()).GetByEmailAsync(email);
        Assert.NotNull(stored);
        Assert.StartsWith("v1.", stored!.PasswordHash!);
        Assert.False(stored.PasswordHash!.Contains(ApiHost.StrongPassword));
    }

    [Fact]
    public async Task Duplicate_email_is_a_conflict_regardless_of_case()
    {
        await using var host = await ApiHost.StartAsync();
        var (_, email, _) = await host.RegisterAsync();
        var second = host.NewClient();
        var (status, body) = await second.PostAsync("/api/auth/register", new { email = email.ToUpperInvariant(), password = ApiHost.StrongPassword, firstName = "A", lastName = "B" });
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("DUPLICATE_EMAIL", TestClient.Error(body));
    }

    [Fact]
    public async Task Invalid_registration_returns_field_errors_and_creates_nothing()
    {
        await using var host = await ApiHost.StartAsync();
        var client = host.NewClient();
        var email = ApiHost.UniqueEmail();
        var (status, body) = await client.PostAsync("/api/auth/register", new { email, password = "short", firstName = "", lastName = "X" });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("VALIDATION_FAILED", TestClient.Error(body));
        Assert.True(body.GetProperty("errors").TryGetProperty("password", out _));
        Assert.True(body.GetProperty("errors").TryGetProperty("firstName", out _));
        Assert.Null(await new UserRepository((await TestEnv.GetDatabaseAsync()).Executor()).GetByEmailAsync(email));
    }

    [Fact]
    public async Task Login_succeeds_with_the_right_password()
    {
        await using var host = await ApiHost.StartAsync();
        var (_, email, _) = await host.RegisterAsync();
        var fresh = host.NewClient();
        var (status, _) = await fresh.PostAsync("/api/auth/login", new { email = email.ToUpperInvariant(), password = ApiHost.StrongPassword });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(HttpStatusCode.OK, (await fresh.GetAsync("/api/auth/me")).Status);
    }

    [Fact]
    public async Task Wrong_password_and_unknown_email_give_the_same_error()
    {
        await using var host = await ApiHost.StartAsync();
        var (_, email, _) = await host.RegisterAsync();
        var c = host.NewClient();
        var (s1, b1) = await c.PostAsync("/api/auth/login", new { email, password = "wrong-password-1" });
        var (s2, b2) = await c.PostAsync("/api/auth/login", new { email = ApiHost.UniqueEmail("nobody"), password = "wrong-password-1" });
        Assert.Equal(HttpStatusCode.Unauthorized, s1);
        Assert.Equal(HttpStatusCode.Unauthorized, s2);
        Assert.Equal(b1.GetRawText(), b2.GetRawText());
    }

    [Fact]
    public async Task Repeated_failures_lock_the_account_even_for_the_right_password()
    {
        await using var host = await ApiHost.StartAsync(new() { ["Auth:MaxFailedAttempts"] = "3" });
        var (_, email, _) = await host.RegisterAsync();
        var c = host.NewClient();
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsync("/api/auth/login", new { email, password = "wrong-password-1" })).Status);
        var (status, body) = await c.PostAsync("/api/auth/login", new { email, password = ApiHost.StrongPassword });
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("ACCOUNT_LOCKED", TestClient.Error(body));
    }

    [Fact]
    public async Task A_successful_login_resets_the_failure_counter()
    {
        await using var host = await ApiHost.StartAsync(new() { ["Auth:MaxFailedAttempts"] = "3" });
        var (_, email, _) = await host.RegisterAsync();
        var c = host.NewClient();
        for (var i = 0; i < 2; i++) await c.PostAsync("/api/auth/login", new { email, password = "wrong-password-1" });
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync("/api/auth/login", new { email, password = ApiHost.StrongPassword })).Status);
        for (var i = 0; i < 2; i++) await c.PostAsync("/api/auth/login", new { email, password = "wrong-password-1" });
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync("/api/auth/login", new { email, password = ApiHost.StrongPassword })).Status);
    }

    [Fact]
    public async Task Logout_ends_the_session()
    {
        await using var host = await ApiHost.StartAsync();
        var (client, _, _) = await host.RegisterAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/logout")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).Status);
    }

    [Fact]
    public async Task Me_requires_a_session()
    {
        await using var host = await ApiHost.StartAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.NewClient().GetAsync("/api/auth/me")).Status);
    }

    [Fact]
    public async Task State_changing_requests_without_origin_or_header_are_rejected_as_csrf()
    {
        await using var host = await ApiHost.StartAsync();
        var noHeader = host.NewClient(csrfHeader: false);
        var (status, body) = await noHeader.PostAsync("/api/auth/login", new { email = "a@b.co", password = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("CSRF_REJECTED", TestClient.Error(body));
    }

    [Fact]
    public async Task A_foreign_origin_is_rejected_even_with_the_header()
    {
        await using var host = await ApiHost.StartAsync();
        var c = host.NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login") { Content = JsonContent.Create(new { email = "a@b.co", password = "x" }) };
        request.Headers.Add("Origin", "https://evil.example");
        using var response = await c.Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_configured_origin_is_accepted()
    {
        await using var host = await ApiHost.StartAsync(new() { ["Web:AllowedOrigins:0"] = "https://app.example.test" });
        var c = host.NewClient(csrfHeader: false);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login") { Content = JsonContent.Create(new { email = "a@b.co", password = "x" }) };
        request.Headers.Add("Origin", "https://app.example.test");
        using var response = await c.Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);   // got past CSRF, then bad credentials
    }

    [Fact]
    public async Task Auth_endpoints_are_rate_limited()
    {
        await using var host = await ApiHost.StartAsync(new() { ["Auth:AuthRequestsPerMinute"] = "3" });
        var c = host.NewClient();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++) statuses.Add((await c.PostAsync("/api/auth/login", new { email = "a@b.co", password = "x" })).Status);
        Assert.True(statuses.Contains(HttpStatusCode.TooManyRequests), string.Join(",", statuses));
    }

    [Fact]
    public async Task Health_endpoint_reaches_the_database_through_a_procedure()
    {
        await using var host = await ApiHost.StartAsync();
        var (status, body) = await host.NewClient().GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("ok", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Security_headers_are_set()
    {
        await using var host = await ApiHost.StartAsync();
        using var response = await host.NewClient().Http.GetAsync("/api/health");
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
    }

    [Fact]
    public async Task Session_cookie_is_http_only()
    {
        await using var host = await ApiHost.StartAsync();
        var c = host.NewClient();
        using var response = await c.Http.PostAsJsonAsync("/api/auth/register", new { email = ApiHost.UniqueEmail(), password = ApiHost.StrongPassword, firstName = "A", lastName = "B" });
        var cookie = response.Headers.GetValues("Set-Cookie").First(v => v.StartsWith("sayserne.auth="));
        Assert.Contains("httponly", cookie.ToLowerInvariant());
        Assert.Contains("samesite=lax", cookie.ToLowerInvariant());
    }

    [Fact]
    public async Task Missing_connection_string_fails_fast_with_a_clear_message()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => AppFactory.Build([], b => b.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = "" }), "Development"));
        Assert.Contains("ConnectionStrings", ex.Message);
    }
}
