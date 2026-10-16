using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Data;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Sayserne.Api;
using Sayserne.Api.Data;
using Sayserne.Api.Auth;
using Sayserne.Api.Platform;
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
        // The API account gets a small pool (many hosts run side by side); the migrator account is used for one-off calls, so no pooling at all.
        string Conn(string user, string pw, bool pooled) => new MySqlConnectionStringBuilder
        {
            Server = server.Server, Port = server.Port, Database = options.DatabaseName, UserID = user, Password = pw,
            AllowUserVariables = false, Pooling = pooled, MaximumPoolSize = pooled ? 6u : 100u,
        }.ConnectionString;

        var db = new TestDatabase { Options = options, AppConnection = Conn(options.AppUser, options.AppPassword, pooled: true), MigratorConnection = Conn(options.MigratorUser, options.MigratorPassword, pooled: false) };
        All.Add(db);
        await Provisioner.RunAsync(TestEnv.AdminConnection, dbDir ?? TestEnv.DbDir, options);
        if (migrate) await new MigrationRunner(db.MigratorConnection, dbDir ?? TestEnv.DbDir).RunAsync();
        return db;
    }

    public async Task DropAsync()
    {
        lock (_dataSourceLock) { _dataSource?.Dispose(); _dataSource = null; }
        MySqlConnection.ClearAllPools();   // idle pooled connections of this database would otherwise linger and count against max_connections
        await Provisioner.DeprovisionAsync(TestEnv.AdminConnection, TestEnv.DbDir, Options);
    }

    /// <summary>Promotes a user to admin through the same stored procedure the migrator CLI uses.</summary>
    public async Task PromoteToAdminAsync(string email)
    {
        await using var conn = new MySqlConnection(MigratorConnection);
        await conn.OpenAsync();
        await using var cmd = new MySqlCommand("sp_admin_users_promote_by_email", conn) { CommandType = CommandType.StoredProcedure };
        cmd.Parameters.AddWithValue("p_email", email);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Moves an appointment into the past (the API refuses to create past bookings), so completion can be tested.</summary>
    public async Task BackdateAppointmentAsync(long appointmentId)
    {
        var sql = (await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "backdate_appointment.sql"))).Replace("{{ID}}", appointmentId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await using var conn = new MySqlConnection(MigratorConnection);
        await conn.OpenAsync();
        await ScriptRunner.ExecuteAsync(conn, sql, default);
    }

    /// <summary>Test-only time travel for the job queue (the app account cannot edit tables, by design).</summary>
    public async Task RunFixtureAsync(string name, long id = 0)
    {
        var sql = (await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", name))).Replace("{{ID}}", id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await using var conn = new MySqlConnection(MigratorConnection);
        await conn.OpenAsync();
        await ScriptRunner.ExecuteAsync(conn, sql, default);
    }

    /// <summary>Seconds from now until the job's next run (negative = already due).</summary>
    public async Task<double> JobRunInAsync(long id)
    {
        var sql = (await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "jobs_read_run_at.sql"))).Replace("{{ID}}", id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await using var conn = new MySqlConnection(MigratorConnection);
        await conn.OpenAsync();
        await using var cmd = new MySqlCommand(sql, conn);
        return Convert.ToDouble(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Test-only: one value from the database, read with the owner account (the app account cannot read tables, by design).</summary>
    public async Task<string?> ScalarAsync(string sql)
    {
        await using var conn = new MySqlConnection(MigratorConnection);
        await conn.OpenAsync();
        await using var cmd = new MySqlCommand(sql, conn);
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task ExecAsync(string sql)
    {
        await using var conn = new MySqlConnection(MigratorConnection);
        await conn.OpenAsync();
        await using var cmd = new MySqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private MySqlDataSource? _dataSource;
    private readonly object _dataSourceLock = new();
    /// <summary>One shared connection pool per test database (creating a pool per call exhausts MySQL's connection limit).</summary>
    public StoredProcedureExecutor Executor()
    {
        lock (_dataSourceLock) _dataSource ??= new MySqlDataSource(AppConnection);
        return new StoredProcedureExecutor(_dataSource);
    }
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

    public Task<(HttpStatusCode Status, JsonElement Body)> c_PostJson(string path) => PostAsync(path, new { file = "not multipart" });

    public async Task<(HttpStatusCode Status, JsonElement Body)> PutAsync(string path, object? body = null)
    {
        using var response = await Http.PutAsJsonAsync(path, body ?? new { });
        return await ReadAsync(response);
    }

    public async Task<(HttpStatusCode Status, JsonElement Body)> DeleteAsync(string path)
    {
        using var response = await Http.DeleteAsync(path);
        return await ReadAsync(response);
    }

    public async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(string path)
    {
        using var response = await Http.GetAsync(path);
        return await ReadAsync(response);
    }

    /// <summary>Uploads one file as multipart/form-data (field name "file").</summary>
    public async Task<(HttpStatusCode Status, JsonElement Body)> UploadAsync(string path, byte[] bytes, string fileName = "photo.jpg")
    {
        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        form.Add(part, "file", fileName);
        using var response = await Http.PostAsync(path, form);
        return await ReadAsync(response);
    }

    public async Task<(HttpStatusCode Status, byte[] Bytes, HttpResponseHeaders Headers, HttpContentHeaders ContentHeaders)> GetBytesAsync(string path, string? range = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (range != null) request.Headers.TryAddWithoutValidation("Range", range);
        using var response = await Http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsByteArrayAsync(), response.Headers, response.Content.Headers);
    }

    public static async Task<(HttpStatusCode, JsonElement)> ReadAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    public static string? Str(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static string? Error(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty("error", out var e) ? e.GetString() : null;
}

/// <summary>Runs the real API in-process on a random port, wired to the throw-away database.</summary>
public sealed class ApiHost : IAsyncDisposable
{
    public required WebApplication App { get; init; }
    public FakeGoogle? Google { get; init; }
    public required TestDatabase Database { get; init; }
    public Uri BaseUri => new(App.Urls.First());

    public static async Task<ApiHost> StartAsync(Dictionary<string, string?>? overrides = null, bool withGoogle = false, TestDatabase? database = null, TimeProvider? clock = null, bool pinRateLimit = true, Action<IServiceCollection>? services = null)
    {
        var db = database ?? await TestEnv.GetDatabaseAsync();
        var google = withGoogle ? await FakeGoogle.StartAsync() : null;
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = db.AppConnection,
            ["Auth:PasswordIterations"] = "2000",          // fast hashing for tests only (deploy-time setting)
            ["Settings:RefreshSeconds"] = "3600",           // tests refresh explicitly
            ["Jobs:Enabled"] = "false",                     // tests run due jobs themselves (JobRunner.RunOnceAsync)
            ["Jobs:SchedulerEnabled"] = "false",            // ...and the digest scheduler (DigestScheduler.RunOnceAsync)
            ["Storage:DiskPath"] = Path.Combine(Path.GetTempPath(), "sayserne-test-storage-" + Guid.NewGuid().ToString("N")),
        };
        // "Auth:Xyz" keys given by tests pin the runtime setting "Auth.Xyz" for this host only (other tests share the database, so it is never written there)
        var pinned = new Dictionary<string, string>();
        if (pinRateLimit) pinned["Auth.AuthRequestsPerMinute"] = "100000";
        if (google != null)
        {
            settings["Google:ClientId"] = FakeGoogle.ClientId;
            settings["Google:ClientSecret"] = FakeGoogle.ClientSecret;
            settings["Google:RedirectUri"] = "http://localhost:5173/api/auth/google/callback";
            settings["Google:AuthorizationEndpoint"] = google.BaseUrl + "/auth";
            settings["Google:TokenEndpoint"] = google.BaseUrl + "/token";
            settings["Google:JwksUri"] = google.BaseUrl + "/certs";
        }
        if (overrides != null)
            foreach (var (k, v) in overrides)
            {
                if (k.StartsWith("Auth:", StringComparison.Ordinal) && k != "Auth:PasswordIterations") pinned["Auth." + k["Auth:".Length..]] = v!;
                else settings[k] = v;
            }

        var app = AppFactory.Build([], b =>
        {
            b.Configuration.AddInMemoryCollection(settings);
            b.Services.AddSingleton(new SettingOverrides(pinned));
            if (clock != null) b.Services.AddSingleton(clock);
            services?.Invoke(b.Services);
            b.WebHost.UseUrls("http://127.0.0.1:0");
            b.Logging.ClearProviders();
        }, "Development");
        await app.StartAsync();
        return new ApiHost { App = app, Google = google, Database = db, StoragePath = settings["Storage:DiskPath"]! };
    }

    public TestClient NewClient(bool csrfHeader = true) => new(BaseUri, csrfHeader);

    public required string StoragePath { get; init; }

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
        if (Google != null) await Google.DisposeAsync();
        try { if (Directory.Exists(StoragePath)) Directory.Delete(StoragePath, recursive: true); } catch (IOException) { /* temp folder */ }
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

    private static int _slot;
    /// <summary>A unique future instant (so tests never collide on the same professional's calendar).</summary>
    public static DateTimeOffset FutureSlot(int hour = 10) =>
        new(DateTime.UtcNow.Date.AddDays(30 + Interlocked.Increment(ref _slot)).AddHours(hour), TimeSpan.Zero);

    public static object ProfileBody(string first = "Pat", string last = "Pro", string type = "professional", string? city = null, string? country = null,
        string? sex = null, string? dob = null, string? nationality = null, string? phone = null, string? bio = null) =>
        new { firstName = first, lastName = last, profileType = type, city, country, sex, dateOfBirth = dob, nationality, phone, bio, preferredLanguage = "en" };

    public async Task<(TestClient Client, string Email, long Id)> RegisterProfessionalAsync(string first = "Pat", string last = "Pro", string? city = null,
        string? country = null, string? sex = null, string? dob = null, string? nationality = null)
    {
        var (c, email, id) = await RegisterAsync();
        var (status, body) = await c.PutAsync("/api/me/profile", ProfileBody(first, last, "professional", city, country, sex, dob, nationality));
        Assert.Equal(HttpStatusCode.OK, status);
        return (c, email, id);
    }

    public async Task<(long CategoryId, long ServiceId)> ServiceAsync(string name = "Corporate Law")
    {
        var (_, services) = await NewClient().GetAsync("/api/catalog/services");
        foreach (var s in services.EnumerateArray())
            if (s.GetProperty("name").GetString() == name) return (s.GetProperty("categoryId").GetInt64(), s.GetProperty("id").GetInt64());
        throw new InvalidOperationException($"seed service '{name}' not found");
    }

    public async Task<long> AddProfessionAsync(TestClient pro, string serviceName = "Corporate Law", Dictionary<string, object?>? extra = null)
    {
        var (categoryId, serviceId) = await ServiceAsync(serviceName);
        var body = new Dictionary<string, object?> { ["categoryId"] = categoryId, ["serviceId"] = serviceId, ["costPerHour"] = 50, ["yearsOfExperience"] = 5 };
        if (extra != null) foreach (var (k, v) in extra) body[k] = v;
        var (status, created) = await pro.PostAsync("/api/me/professions", body);
        Assert.Equal(HttpStatusCode.Created, status);
        return created.GetProperty("id").GetInt64();
    }

    /// <summary>Client books, professional approves, appointment is moved into the past, professional completes it.</summary>
    public async Task<long> CompletedAppointmentAsync(TestClient client, TestClient pro, long proId, long? professionId = null)
    {
        var (s1, booked) = await client.PostAsync("/api/appointments", new { professionalId = proId, professionId, startsAt = FutureSlot(), durationMinutes = 60 });
        Assert.Equal(HttpStatusCode.Created, s1);
        var id = booked.GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.OK, (await pro.PostAsync($"/api/appointments/{id}/status", new { status = "approved" })).Status);
        await Database.BackdateAppointmentAsync(id);
        Assert.Equal(HttpStatusCode.OK, (await pro.PostAsync($"/api/appointments/{id}/status", new { status = "completed" })).Status);
        return id;
    }

    public static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    public static bool Has(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, AdminSecrets> Secrets = new();
    private sealed class AdminSecrets { public string Secret = ""; public long LastStep; public List<string> Recovery = []; }

    /// <summary>Signs in to the back office with password + authenticator code (enrolling on the first sign-in). Returns the admin session.</summary>
    public async Task<(TestClient Client, long Id)> AdminSignInAsync(string email, string password = StrongPassword)
    {
        var client = NewClient();
        var (status, body) = await client.PostAsync("/api/admin/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, status);
        var known = Secrets.GetOrAdd(email, _ => new AdminSecrets());
        if (TestClient.Str(body, "status") == "enrollment_required")
        {
            var (es, enroll) = await client.PostAsync("/api/admin/auth/2fa/enroll");
            Assert.Equal(HttpStatusCode.OK, es);
            known.Secret = enroll.GetProperty("secret").GetString()!;
            var step = Totp.StepAt(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            var (cs, confirm) = await client.PostAsync("/api/admin/auth/2fa/confirm", new { code = Totp.Code(known.Secret, step) });
            Assert.Equal(HttpStatusCode.OK, cs);
            known.LastStep = step;
            known.Recovery = confirm.GetProperty("recoveryCodes").EnumerateArray().Select(c => c.GetString()!).ToList();
        }
        else
        {
            // each 30-second step can be used once: take the next unused step inside the accepted window, else fall back to a recovery code
            var now = Totp.StepAt(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            var next = Math.Max(known.LastStep + 1, now - 1);
            object payload;
            if (next <= now + 1) { known.LastStep = next; payload = new { code = Totp.Code(known.Secret, next) }; }
            else { var code = known.Recovery[0]; known.Recovery.RemoveAt(0); payload = new { recoveryCode = code }; }
            var (vs, _) = await client.PostAsync("/api/admin/auth/verify", payload);
            Assert.Equal(HttpStatusCode.OK, vs);
        }
        var (_, me) = await client.GetAsync("/api/admin/auth/me");
        return (client, me.GetProperty("user").GetProperty("id").GetInt64());
    }

    public static string SecretOf(string email) => Secrets[email].Secret;
    public static IReadOnlyList<string> RecoveryCodesOf(string email) => Secrets[email].Recovery;

    /// <summary>Registers a user, makes them a super admin, and signs in to the back office (enrolling two-factor).</summary>
    public async Task<(TestClient Client, string Email, long Id)> CreateAdminAsync()
    {
        var (_, email, id) = await RegisterAsync();
        await Database.PromoteToAdminAsync(email);
        var (client, _) = await AdminSignInAsync(email);
        return (client, email, id);
    }

    /// <summary>A staff member who is NOT a super admin: holds a role with exactly the given permissions.</summary>
    public async Task<(TestClient Client, string Email, long Id)> CreateStaffAsync(params string[] permissions)
    {
        var (admin, _, _) = await CreateAdminAsync();
        var (rs, role) = await admin.PostAsync("/api/admin/roles", new { name = "Role " + Guid.NewGuid().ToString("N")[..10], permissions });
        Assert.Equal(HttpStatusCode.Created, rs);
        var (_, email, id) = await RegisterAsync();
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync($"/api/admin/users/{id}/roles", new { roleIds = new[] { role.GetProperty("id").GetInt64() } })).Status);
        var (client, _) = await AdminSignInAsync(email);
        return (client, email, id);
    }
}

/// <summary>A host with its own database, for tests that change shared configuration (settings, lookups, translations). Removed on dispose.</summary>
public sealed class IsolatedHost : IAsyncDisposable
{
    public required ApiHost Host { get; init; }
    public required TestDatabase Db { get; init; }

    public static async Task<IsolatedHost> StartAsync(Dictionary<string, string?>? overrides = null, bool withGoogle = false, TimeProvider? clock = null, bool pinRateLimit = true, Action<IServiceCollection>? services = null)
    {
        var db = await TestDatabase.CreateAsync();
        return new IsolatedHost { Db = db, Host = await ApiHost.StartAsync(overrides, withGoogle, db, clock, pinRateLimit, services) };
    }

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        await Db.DropAsync();
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
