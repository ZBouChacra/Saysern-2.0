using System.Buffers.Text;
using System.Text;
using Microsoft.Extensions.Options;
using Sayserne.Api;
using Sayserne.Api.Auth;
using Sayserne.Api.Data;
using Sayserne.Api.Platform;
using Sayserne.Migrator;

namespace Sayserne.Tests;

/// <summary>Settings with the shipped defaults, for tests that only need the rules (no database).</summary>
public sealed class FixedSettings(Dictionary<string, string>? values = null) : ISettingsService
{
    private readonly Dictionary<string, string> _v = new()
    {
        ["Auth.PasswordMinLength"] = "10", ["Auth.PasswordRequireLetter"] = "true", ["Auth.PasswordRequireDigit"] = "true",
    };
    public FixedSettings(string key, string value) : this(new Dictionary<string, string> { [key] = value }) { }
    public long Version => 1;
    public int GetInt(string key) => int.Parse(Get(key));
    public bool GetBool(string key) => Get(key) == "true";
    public string GetString(string key) => Get(key);
    public string GetSecret(string key) => Get(key);
    public Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;
    public IReadOnlyDictionary<string, object> PublicValues() => new Dictionary<string, object>();
    private string Get(string key) => values?.GetValueOrDefault(key) ?? _v[key];
}

public class PasswordHasherTests
{
    private readonly Pbkdf2PasswordHasher _hasher = new(2000);

    [Fact]
    public void Hash_then_verify_round_trips()
    {
        var hash = _hasher.Hash("correct-horse-9");
        Assert.True(_hasher.Verify("correct-horse-9", hash));
        Assert.False(_hasher.Verify("correct-horse-8", hash));
    }

    [Fact]
    public void Hash_is_salted_and_never_contains_the_password()
    {
        var a = _hasher.Hash("correct-horse-9");
        var b = _hasher.Hash("correct-horse-9");
        Assert.NotEqual(a, b);
        Assert.StartsWith("v1.2000.", a);
        Assert.False(a.Contains("correct-horse-9"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("plaintext")]
    [InlineData("v1.2000.AAAA")]
    [InlineData("v2.2000.AAAAAAAAAAAAAAAAAAAAAA==.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("v1.abc.AAAAAAAAAAAAAAAAAAAAAA==.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("v1.999999999.AAAAAAAAAAAAAAAAAAAAAA==.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("v1.2000.!!!notbase64!!!.!!!notbase64!!!")]
    public void Verify_rejects_malformed_or_hostile_stored_values(string stored)
    {
        Assert.False(_hasher.Verify("anything", stored));
    }
}

public class ValidationTests
{
    private static RegisterRequest Valid() => new("Person@Example.com", "correct-horse-9", "Pat", "Person");

    [Fact]
    public void Valid_registration_has_no_errors() => Assert.Equal(0, InputValidation.ValidateRegistration(Valid(), new FixedSettings()).Count);

    [Theory]
    [InlineData("short1")]
    [InlineData("onlyletterspassword")]
    [InlineData("1234567890123")]
    public void Weak_passwords_are_rejected(string password)
    {
        var errors = InputValidation.ValidateRegistration(Valid() with { Password = password }, new FixedSettings());
        Assert.True(errors.ContainsKey("password"));
    }

    [Fact]
    public void Password_must_not_equal_the_email()
    {
        var errors = InputValidation.ValidateRegistration(Valid() with { Email = "person.abc12@example.com", Password = "person.abc12@example.com" }, new FixedSettings());
        Assert.True(errors.ContainsKey("password"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    [InlineData("a@b")]
    [InlineData("a b@c.de")]
    public void Bad_emails_are_rejected(string email)
    {
        Assert.True(InputValidation.ValidateRegistration(Valid() with { Email = email }, new FixedSettings()).ContainsKey("email"));
    }

    [Fact]
    public void Missing_or_oversized_names_are_rejected()
    {
        var errors = InputValidation.ValidateRegistration(Valid() with { FirstName = " ", LastName = new string('x', 101) }, new FixedSettings());
        Assert.True(errors.ContainsKey("firstName"));
        Assert.True(errors.ContainsKey("lastName"));
    }

    [Fact]
    public void Email_is_normalised() => Assert.Equal("person@example.com", InputValidation.NormalizeEmail("  Person@Example.COM "));

    [Theory]
    [InlineData("/profile", "/profile")]
    [InlineData("/search?q=1", "/search?q=1")]
    [InlineData("https://evil.example", "/")]
    [InlineData("//evil.example", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("javascript:alert(1)", "/")]
    [InlineData(null, "/")]
    public void Return_urls_cannot_redirect_off_site(string? input, string expected) =>
        Assert.Equal(expected, InputValidation.SafeReturnUrl(input));
}

public class GoogleIdTokenValidatorTests
{
    private static async Task<(FakeGoogle Google, GoogleIdTokenValidator Validator, ManualClock Clock)> SetupAsync()
    {
        var google = await FakeGoogle.StartAsync();
        var clock = new ManualClock();
        var options = Options.Create(new GoogleOptions { ClientId = FakeGoogle.ClientId, ClientSecret = FakeGoogle.ClientSecret, JwksUri = google.BaseUrl + "/certs" });
        return (google, new GoogleIdTokenValidator(options, new SimpleHttpClientFactory(), clock), clock);
    }

    private static readonly FakeGoogle.Claims Ok = new("sub-123", "gina@example.com");

    [Fact]
    public async Task Accepts_a_correctly_signed_token()
    {
        var (g, v, _) = await SetupAsync();
        await using var _g = g;
        var id = await v.ValidateAsync(g.BuildToken(Ok, "nonce-1"), "nonce-1");
        Assert.Equal("sub-123", id.Subject);
        Assert.Equal("gina@example.com", id.Email);
        Assert.True(id.EmailVerified);
    }

    [Fact]
    public async Task Reports_unverified_email_without_throwing()
    {
        var (g, v, _) = await SetupAsync();
        await using var _g = g;
        var id = await v.ValidateAsync(g.BuildToken(Ok with { EmailVerified = false }, "n"), "n");
        Assert.False(id.EmailVerified);
    }

    private static async Task AssertRejectedAsync(Func<FakeGoogle, string> token, string nonce = "n", Action<ManualClock>? clock = null)
    {
        var (g, v, c) = await SetupAsync();
        await using var _g = g;
        clock?.Invoke(c);
        await Assert.ThrowsAsync<TokenValidationException>(() => v.ValidateAsync(token(g), nonce));
    }

    [Fact] public Task Rejects_bad_signature() => AssertRejectedAsync(g => g.BuildToken(Ok with { SignWithOtherKey = true }, "n"));
    [Fact] public Task Rejects_wrong_audience() => AssertRejectedAsync(g => g.BuildToken(Ok with { Audience = "someone-elses-client" }, "n"));
    [Fact] public Task Rejects_wrong_issuer() => AssertRejectedAsync(g => g.BuildToken(Ok with { Issuer = "https://evil.example" }, "n"));
    [Fact] public Task Rejects_expired_token() => AssertRejectedAsync(g => g.BuildToken(Ok with { Lifetime = TimeSpan.FromMinutes(-10) }, "n"));
    [Fact] public Task Rejects_nonce_mismatch() => AssertRejectedAsync(g => g.BuildToken(Ok with { NonceOverride = "attacker-nonce" }, "n"));
    [Fact] public Task Rejects_unknown_signing_key() => AssertRejectedAsync(g => g.BuildToken(Ok, "n", kid: "rotated-away"));
    [Fact] public Task Rejects_empty_expected_nonce() => AssertRejectedAsync(g => g.BuildToken(Ok, "n"), nonce: "");
    [Fact] public Task Rejects_garbage() => AssertRejectedAsync(_ => "not.a.jwt");
    [Fact] public Task Rejects_two_part_token() => AssertRejectedAsync(_ => "abc.def");

    [Fact]
    public Task Rejects_alg_none_downgrade() => AssertRejectedAsync(_ =>
    {
        string B(string s) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(s));
        var payload = $"{{\"iss\":\"https://accounts.google.com\",\"aud\":\"{FakeGoogle.ClientId}\",\"sub\":\"x\",\"email\":\"a@b.co\",\"nonce\":\"n\",\"exp\":{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()}}}";
        return B("{\"alg\":\"none\",\"kid\":\"" + FakeGoogle.Kid + "\"}") + "." + B(payload) + ".";
    });

    [Fact]
    public async Task Rejects_a_token_after_the_clock_passes_its_expiry()
    {
        var (g, v, clock) = await SetupAsync();
        await using var _g = g;
        var token = g.BuildToken(Ok with { Lifetime = TimeSpan.FromMinutes(5) }, "n");
        await v.ValidateAsync(token, "n");
        clock.Now = clock.Now.AddMinutes(30);
        await Assert.ThrowsAsync<TokenValidationException>(() => v.ValidateAsync(token, "n"));
    }
}

public class ScriptSplitterTests
{
    [Fact]
    public void Splits_plain_statements_and_drops_comments()
    {
        var parts = ScriptRunner.Split("-- header\nCREATE TABLE a (id INT);\n\n# note\nCREATE TABLE b (id INT);\n");
        Assert.Equal(2, parts.Count);
        Assert.StartsWith("CREATE TABLE a", parts[0]);
    }

    [Fact]
    public void Honours_DELIMITER_for_procedure_bodies()
    {
        var script = "DROP PROCEDURE IF EXISTS p;\nDELIMITER $$\nCREATE PROCEDURE p()\nBEGIN\n  SELECT 1;\n  SELECT 2;\nEND$$\nDELIMITER ;\nSELECT 3;\n";
        var parts = ScriptRunner.Split(script);
        Assert.Equal(3, parts.Count);
        Assert.True(parts[1].Contains("SELECT 1;") && parts[1].Contains("SELECT 2;"));
        Assert.True(parts[1].EndsWith("END"));
    }

    [Fact]
    public void Checksum_ignores_line_ending_style() =>
        Assert.Equal(ScriptRunner.Checksum("a\nb\n"), ScriptRunner.Checksum("a\r\nb\r\n"));

    [Fact]
    public void Checksum_changes_when_content_changes() =>
        Assert.NotEqual(ScriptRunner.Checksum("a"), ScriptRunner.Checksum("b"));
}

public class ProvisionerValidationTests
{
    private static ProvisionOptions Good() => new("sayserne", "migrator", "Strong-Pass-123", "appuser", "Other-Pass-456");

    [Theory]
    [InlineData("db`; DROP DATABASE x; --")]
    [InlineData("has space")]
    [InlineData("")]
    public async Task Rejects_hostile_database_names(string name) =>
        await Assert.ThrowsAsync<ArgumentException>(() => Provisioner.RunAsync("Server=127.0.0.1", TestEnv.DbDir, Good() with { DatabaseName = name }));

    [Theory]
    [InlineData("short")]
    [InlineData("has'quote-in-password-1")]
    [InlineData("back\\slash-password-12")]
    public async Task Rejects_unsafe_or_weak_passwords(string password) =>
        await Assert.ThrowsAsync<ArgumentException>(() => Provisioner.RunAsync("Server=127.0.0.1", TestEnv.DbDir, Good() with { AppPassword = password }));

    [Fact]
    public async Task Requires_distinct_migrator_and_app_users() =>
        await Assert.ThrowsAsync<ArgumentException>(() => Provisioner.RunAsync("Server=127.0.0.1", TestEnv.DbDir, Good() with { AppUser = "migrator" }));
}

public class ProcedureNameTests
{
    [Theory]
    [InlineData("sp_users_get_by_id")]
    [InlineData("sp_health_ping")]
    public void Accepts_well_formed_names(string name) => StoredProcedureExecutor.ValidateProcedureName(name);

    [Theory]
    [InlineData("users")]
    [InlineData("SELECT 1")]
    [InlineData("sp_x; DROP TABLE users")]
    [InlineData("sp_x(1)")]
    [InlineData("SP_UPPER")]
    [InlineData("")]
    [InlineData("db.sp_x")]
    public void Rejects_anything_that_is_not_a_procedure_identifier(string name) =>
        Assert.Throws<ArgumentException>(() => StoredProcedureExecutor.ValidateProcedureName(name));
}
