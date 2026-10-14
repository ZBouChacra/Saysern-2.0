using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Sayserne.Api.Auth;

public sealed class TokenValidationException(string reason) : Exception(reason);

public sealed record GoogleIdentity(string Subject, string Email, bool EmailVerified, string? GivenName, string? FamilyName, string? Name);

public interface IGoogleIdTokenValidator
{
    /// <summary>Verifies signature, issuer, audience, expiry and nonce. Throws <see cref="TokenValidationException"/> on any failure.</summary>
    Task<GoogleIdentity> ValidateAsync(string idToken, string expectedNonce, CancellationToken ct = default);
}

/// <summary>
/// Validates a Google OpenID Connect ID token on the server: RS256 only, signature checked against
/// Google's published JWKS, then iss / aud / exp / nonce. Nothing from the browser is trusted.
/// </summary>
public sealed class GoogleIdTokenValidator(IOptions<GoogleOptions> options, IHttpClientFactory httpFactory, TimeProvider clock) : IGoogleIdTokenValidator
{
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan KeyCacheLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan MinRefreshInterval = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private Dictionary<string, RSAParameters> _keys = new();
    private DateTimeOffset _fetchedAt = DateTimeOffset.MinValue;

    public async Task<GoogleIdentity> ValidateAsync(string idToken, string expectedNonce, CancellationToken ct = default)
    {
        var o = options.Value;
        var parts = (idToken ?? string.Empty).Split('.');
        if (parts.Length != 3) throw new TokenValidationException("malformed token");

        JsonElement header, payload;
        byte[] signature;
        try
        {
            header = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[0])).RootElement;
            payload = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1])).RootElement;
            signature = Base64Url.DecodeFromChars(parts[2]);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException)
        {
            throw new TokenValidationException("malformed token");
        }

        if (Str(header, "alg") != "RS256") throw new TokenValidationException("unsupported algorithm");
        var kid = Str(header, "kid");
        if (string.IsNullOrEmpty(kid)) throw new TokenValidationException("missing key id");

        var key = await GetKeyAsync(kid, ct) ?? throw new TokenValidationException("unknown signing key");
        using (var rsa = RSA.Create())
        {
            rsa.ImportParameters(key);
            var signedData = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
            if (!rsa.VerifyData(signedData, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                throw new TokenValidationException("bad signature");
        }

        if (!o.Issuers.Contains(Str(payload, "iss"))) throw new TokenValidationException("wrong issuer");
        if (!AudienceMatches(payload, o.ClientId)) throw new TokenValidationException("wrong audience");

        if (!payload.TryGetProperty("exp", out var exp) || exp.ValueKind != JsonValueKind.Number || !exp.TryGetInt64(out var expSeconds))
            throw new TokenValidationException("missing expiry");
        if (DateTimeOffset.FromUnixTimeSeconds(expSeconds) + ClockSkew < clock.GetUtcNow()) throw new TokenValidationException("token expired");

        var nonce = Str(payload, "nonce");
        if (string.IsNullOrEmpty(expectedNonce) || string.IsNullOrEmpty(nonce)
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(nonce), Encoding.UTF8.GetBytes(expectedNonce)))
            throw new TokenValidationException("nonce mismatch");

        var sub = Str(payload, "sub");
        var email = Str(payload, "email");
        if (string.IsNullOrEmpty(sub) || sub.Length > 255) throw new TokenValidationException("missing subject");
        if (string.IsNullOrEmpty(email) || email.Length > 254) throw new TokenValidationException("missing email");

        var verified = payload.TryGetProperty("email_verified", out var ev)
            && (ev.ValueKind == JsonValueKind.True || (ev.ValueKind == JsonValueKind.String && ev.GetString() == "true"));

        return new GoogleIdentity(sub, email, verified, Str(payload, "given_name"), Str(payload, "family_name"), Str(payload, "name"));
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool AudienceMatches(JsonElement payload, string clientId)
    {
        if (!payload.TryGetProperty("aud", out var aud) || string.IsNullOrEmpty(clientId)) return false;
        return aud.ValueKind switch
        {
            JsonValueKind.String => aud.GetString() == clientId,
            JsonValueKind.Array => aud.EnumerateArray().Any(a => a.ValueKind == JsonValueKind.String && a.GetString() == clientId),
            _ => false,
        };
    }

    private async Task<RSAParameters?> GetKeyAsync(string kid, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var now = clock.GetUtcNow();
            var stale = now - _fetchedAt > KeyCacheLifetime;
            var canRefresh = now - _fetchedAt > MinRefreshInterval;
            if (_keys.TryGetValue(kid, out var cached) && !stale) return cached;
            if (stale || canRefresh) await RefreshAsync(ct);   // unknown kid => keys may have rotated
            return _keys.TryGetValue(kid, out var key) ? key : null;
        }
        finally { _lock.Release(); }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        var http = httpFactory.CreateClient("google");
        using var response = await http.GetAsync(options.Value.JwksUri, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var keys = new Dictionary<string, RSAParameters>();
        foreach (var k in doc.RootElement.GetProperty("keys").EnumerateArray())
        {
            if (Str(k, "kty") != "RSA" || Str(k, "kid") is not { Length: > 0 } id) continue;
            keys[id] = new RSAParameters { Modulus = Base64Url.DecodeFromChars(Str(k, "n")!), Exponent = Base64Url.DecodeFromChars(Str(k, "e")!) };
        }
        _keys = keys;
        _fetchedAt = clock.GetUtcNow();
    }
}
