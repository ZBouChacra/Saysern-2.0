using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Data;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Sayserne.Api;
using Sayserne.Api.Data;
using Sayserne.Migrator;

namespace Sayserne.Tests;

/// <summary>
/// Integration tests need a MySQL 8 server and an administrator account that may create databases/users:
///   SAYSERNE_TEST_ADMIN_CONNECTION="Server=127.0.0.1;Port=3306;User ID=root;Password=..."
/// Each test run provisions its own throw-away database (sayserne_test_*) and removes it afterwards.
/// </summary>
public static class TestEnv
{
    public static string AdminConnection =>
        Environment.GetEnvironmentVariable("SAYSERNE_TEST_ADMIN_CONNECTION") is { Length: > 0 } v ? v
            : throw new InvalidOperationException("Set SAYSERNE_TEST_ADMIN_CONNECTION to run the database/API tests (see README).");

    public static string UserHost => Environment.GetEnvironmentVariable("SAYSERNE_TEST_USER_HOST") is { Length: > 0 } v ? v : "localhost";
    public static string DbDir { get; } = DbDirectory.Locate();

    private static readonly Lazy<Task<TestDatabase>> Shared = new(() => TestDatabase.CreateAsync());
    public static Task<TestDatabase> GetDatabaseAsync() => Shared.Value;
}

public sealed class TestDatabase
{
    private static readonly ConcurrentBag<TestDatabase> All = [];
    static TestDatabase() => AppDomain.CurrentDomain.ProcessExit += (_, _) =>
    {
        foreach (var db in All)
            try { db.DropAsync().GetAwaiter().GetResult(); } catch { /* best effort */ }
    };

    public required ProvisionOptions Options { get; init; }
    public required string AppConnection { get; init; }
    public required string MigratorConnection { get; init; }
    public string Name => Options.DatabaseName;

    public static async Task<TestDatabase> CreateAsync(string? dbDir = null, bool migrate = true)
    {
        var id = Guid.NewGuid().ToString("N");
        var options = new ProvisionOptions($"sayserne_test_{id[..10]}", $"m_{id[..8]}", $"Mp-{id[8..24]}", $"a_{id[..8]}", $"Ap-{id[12..28]}", TestEnv.UserHost);
        var server = new MySqlConnectionStringBuilder(TestEnv.AdminConnection);
        string Conn(string user, string pw) => new MySqlConnectionStringBuilder
        {
            Server = server.Server, Port = server.Port, Database = options.DatabaseName, UserID = user, Password = pw,
            AllowUserVariables = false,
        }.ConnectionString;

        var db = new TestDatabase { Options = options, AppConnection = Conn(options.AppUser, options.AppPassword), MigratorConnection = Conn(options.MigratorUser, options.MigratorPassword) };
        All.Add(db);
        await Provisioner.RunAsync(TestEnv.AdminConnection, dbDir ?? TestEnv.DbDir, options);
        if (migrate) await new MigrationRunner(db.MigratorConnection, dbDir ?? TestEnv.DbDir).RunAsync();
        return db;
    }

    public Task DropAsync() => Provisioner.DeprovisionAsync(TestEnv.AdminConnection, TestEnv.DbDir, Options);

    /// <summary>Promotes a user to admin through the same stored procedure the migrator CLI uses.</summary>
    public async Task PromoteToAdminAsync(string email)
    {
        await using var conn = new MySqlConnection(MigratorConnection);
        await conn.OpenAsync();
        await using var cmd = new MySqlCommand("sp_admin_users_promote_by_email", conn) { CommandType = CommandType.StoredProcedure };
        cmd.Parameters.AddWithValue("p_email", email);
        await cmd.ExecuteNonQueryAsync();
    }

    public StoredProcedureExecutor Executor() => new(new MySqlDataSource(AppConnection));
}

public sealed class ManualClock(DateTimeOffset? start = null) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start ?? DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class SimpleHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}

/// <summary>A browser-like client: keeps cookies, never follows redirects, sends the CSRF header.</summary>
public sealed class TestClient
{
    public HttpClient Http { get; }
    public TestClient(Uri baseUri, bool csrfHeader = true)
    {
        Http = new HttpClient(new HttpClientHandler { UseCookies = true, AllowAutoRedirect = false, CookieContainer = new CookieContainer() }) { BaseAddress = baseUri };
        if (csrfHeader) Http.DefaultRequestHeaders.Add("X-Requested-With", "tests");
    }

    public async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(string path, object? body = null)
    {
        using var response = await Http.PostAsJsonAsync(path, body ?? new { });
        return await ReadAsync(response);
    }

    public async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(string path)
    {
        using var response = await Http.GetAsync(path);
        return await ReadAsync(response);
    }

    public static async Task<(HttpStatusCode, JsonElement)> ReadAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    public static string? Error(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty("error", out var e) ? e.GetString() : null;
}

/// <summary>Runs the real API in-process on a random port, wired to the throw-away database.</summary>
public sealed class ApiHost : IAsyncDisposable
{
    public required WebApplication App { get; init; }
    public FakeGoogle? Google { get; init; }
    public Uri BaseUri => new(App.Urls.First());

    public static async Task<ApiHost> StartAsync(Dictionary<string, string?>? overrides = null, bool withGoogle = false, TestDatabase? database = null)
    {
        var db = database ?? await TestEnv.GetDatabaseAsync();
        var google = withGoogle ? await FakeGoogle.StartAsync() : null;
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = db.AppConnection,
            ["Auth:PasswordIterations"] = "2000",          // fast hashing for tests only
            ["Auth:AuthRequestsPerMinute"] = "100000",
        };
        if (google != null)
        {
            settings["Google:ClientId"] = FakeGoogle.ClientId;
            settings["Google:ClientSecret"] = FakeGoogle.ClientSecret;
            settings["Google:RedirectUri"] = "http://localhost:5173/api/auth/google/callback";
            settings["Google:AuthorizationEndpoint"] = google.BaseUrl + "/auth";
            settings["Google:TokenEndpoint"] = google.BaseUrl + "/token";
            settings["Google:JwksUri"] = google.BaseUrl + "/certs";
        }
        if (overrides != null) foreach (var (k, v) in overrides) settings[k] = v;

        var app = AppFactory.Build([], b =>
        {
            b.Configuration.AddInMemoryCollection(settings);
            b.WebHost.UseUrls("http://127.0.0.1:0");
            b.Logging.ClearProviders();
        }, "Development");
        await app.StartAsync();
        return new ApiHost { App = app, Google = google };
    }

    public TestClient NewClient(bool csrfHeader = true) => new(BaseUri, csrfHeader);

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
        if (Google != null) await Google.DisposeAsync();
    }

    public static string UniqueEmail(string prefix = "user") => $"{prefix}.{Guid.NewGuid():N}@example.test";
    public const string StrongPassword = "correct-horse-9";

    public async Task<(TestClient Client, string Email, long Id)> RegisterAsync(string? email = null, string password = StrongPassword)
    {
        var client = NewClient();
        email ??= UniqueEmail();
        var (status, body) = await client.PostAsync("/api/auth/register", new { email, password, firstName = "Test", lastName = "User" });
        Assert.Equal(HttpStatusCode.Created, status);
        return (client, email, body.GetProperty("user").GetProperty("id").GetInt64());
    }

    /// <summary>Registers a user, promotes it to admin, and signs in again so the session carries the admin role.</summary>
    public async Task<(TestClient Client, string Email, long Id)> CreateAdminAsync()
    {
        var (_, email, id) = await RegisterAsync();
        await (await TestEnv.GetDatabaseAsync()).PromoteToAdminAsync(email);
        var client = NewClient();
        var (status, _) = await client.PostAsync("/api/auth/login", new { email, password = StrongPassword });
        Assert.Equal(HttpStatusCode.OK, status);
        return (client, email, id);
    }
}

/// <summary>Stands in for accounts.google.com: serves a JWKS and a token endpoint that enforces client secret and PKCE.</summary>
public sealed class FakeGoogle : IAsyncDisposable
{
    public const string ClientId = "test-client-id.apps.googleusercontent.com";
    public const string ClientSecret = "test-client-secret";
    public const string Kid = "test-key-1";

    public sealed record Claims(
        string Sub, string Email, bool EmailVerified = true, string? Given = "Gina", string? Family = "Google",
        string? NonceOverride = null, string? Audience = null, string? Issuer = null, TimeSpan? Lifetime = null, bool SignWithOtherKey = false);

    public RSA Key { get; } = RSA.Create(2048);
    public RSA OtherKey { get; } = RSA.Create(2048);
    public string BaseUrl { get; private set; } = "";
    private WebApplication _app = null!;
    private readonly ConcurrentDictionary<string, (string IdToken, string Challenge)> _codes = new();

    public static async Task<FakeGoogle> StartAsync()
    {
        var g = new FakeGoogle();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        g._app = builder.Build();
        g._app.MapGet("/certs", () =>
        {
            var p = g.Key.ExportParameters(false);
            return Results.Json(new { keys = new[] { new { kty = "RSA", alg = "RS256", use = "sig", kid = Kid, n = Base64Url.EncodeToString(p.Modulus!), e = Base64Url.EncodeToString(p.Exponent!) } } });
        });
        g._app.MapPost("/token", async (HttpContext ctx) =>
        {
            var f = await ctx.Request.ReadFormAsync();
            if (f["client_id"] != ClientId || f["client_secret"] != ClientSecret || f["grant_type"] != "authorization_code")
                return Results.Json(new { error = "invalid_client" }, statusCode: 401);
            if (!g._codes.TryRemove(f["code"].ToString(), out var entry))   // codes are single-use, like Google's
                return Results.Json(new { error = "invalid_grant" }, statusCode: 400);
            var expected = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(f["code_verifier"].ToString())));
            if (expected != entry.Challenge)
                return Results.Json(new { error = "invalid_grant", detail = "PKCE mismatch" }, statusCode: 400);
            return Results.Json(new { access_token = "unused", id_token = entry.IdToken, token_type = "Bearer" });
        });
        await g._app.StartAsync();
        g.BaseUrl = g._app.Urls.First();
        return g;
    }

    public string IssueCode(Claims claims, string nonce, string codeChallenge)
    {
        var code = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
        _codes[code] = (BuildToken(claims, nonce), codeChallenge);
        return code;
    }

    public string BuildToken(Claims c, string nonce, string? kid = Kid)
    {
        var now = DateTimeOffset.UtcNow;
        var header = JsonSerializer.Serialize(new { alg = "RS256", kid, typ = "JWT" });
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["iss"] = c.Issuer ?? "https://accounts.google.com",
            ["aud"] = c.Audience ?? ClientId,
            ["sub"] = c.Sub,
            ["email"] = c.Email,
            ["email_verified"] = c.EmailVerified,
            ["given_name"] = c.Given,
            ["family_name"] = c.Family,
            ["nonce"] = c.NonceOverride ?? nonce,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = now.Add(c.Lifetime ?? TimeSpan.FromHours(1)).ToUnixTimeSeconds(),
        });
        var signingInput = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(header)) + "." + Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload));
        var signature = (c.SignWithOtherKey ? OtherKey : Key).SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return signingInput + "." + Base64Url.EncodeToString(signature);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Key.Dispose();
        OtherKey.Dispose();
    }

    /// <summary>Drives a complete browser-style sign-in: start -> (fake consent) -> callback. Returns the callback response.</summary>
    public async Task<HttpResponseMessage> SignInAsync(TestClient client, Claims claims, string startPath = "/api/auth/google/start", string? stateOverride = null)
    {
        using var start = await client.Http.GetAsync(startPath);
        var q = QueryHelpers.ParseQuery(start.Headers.Location!.Query);
        var code = IssueCode(claims, q["nonce"]!, q["code_challenge"]!);
        return await client.Http.GetAsync($"/api/auth/google/callback?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(stateOverride ?? q["state"]!)}");
    }
}
