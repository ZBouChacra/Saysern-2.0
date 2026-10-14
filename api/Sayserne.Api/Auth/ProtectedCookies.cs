using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Sayserne.Api.Auth;

/// <summary>
/// Short-lived, encrypted + tamper-proof cookies (ASP.NET Data Protection) used to carry OAuth state
/// (state / nonce / PKCE verifier) and the "pending account link" between requests.
/// </summary>
public sealed class ProtectedCookies(IDataProtectionProvider provider, TimeProvider clock)
{
    private readonly IDataProtector _protector = provider.CreateProtector("Sayserne.ProtectedCookies.v1");
    private sealed record Envelope<T>(T Payload, long ExpiresAtUnix);

    public void Write<T>(HttpContext ctx, string name, T payload, TimeSpan lifetime, string path)
    {
        var envelope = new Envelope<T>(payload, clock.GetUtcNow().Add(lifetime).ToUnixTimeSeconds());
        ctx.Response.Cookies.Append(name, _protector.Protect(JsonSerializer.Serialize(envelope)), new CookieOptions
        {
            HttpOnly = true,
            Secure = ctx.Request.IsHttps,
            SameSite = SameSiteMode.Lax,   // the OAuth callback is a cross-site top-level GET, so Strict would drop it
            Path = path,
            MaxAge = lifetime,
            IsEssential = true,
        });
    }

    public T? Read<T>(HttpContext ctx, string name) where T : class
    {
        if (!ctx.Request.Cookies.TryGetValue(name, out var raw) || string.IsNullOrEmpty(raw)) return null;
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope<T>>(_protector.Unprotect(raw));
            return envelope is null || envelope.ExpiresAtUnix < clock.GetUtcNow().ToUnixTimeSeconds() ? null : envelope.Payload;
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException)
        {
            return null;
        }
    }

    public void Delete(HttpContext ctx, string name, string path) =>
        ctx.Response.Cookies.Delete(name, new CookieOptions { Path = path, HttpOnly = true, Secure = ctx.Request.IsHttps, SameSite = SameSiteMode.Lax });
}
