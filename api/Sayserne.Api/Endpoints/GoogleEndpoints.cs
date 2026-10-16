using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Sayserne.Api.Auth;
using Sayserne.Api.Data;
using Sayserne.Api.Platform;

namespace Sayserne.Api.Endpoints;

/// <summary>
/// Google sign-in: OAuth 2.0 authorization-code flow + PKCE + OpenID Connect ID-token validation.
/// Identity decisions are made only from the server-side validated token, never from browser-supplied data.
///
/// Account linking rules:
///  - Known Google identity            -> sign in.
///  - Unknown identity, email unused   -> create a new passwordless account (email must be Google-verified).
///  - Unknown identity, email in use   -> NEVER auto-merge. The user must prove ownership by entering that
///                                        account's password (POST /link-pending) before the identity is attached.
///  - Signed-in user using ?mode=link  -> attach the verified Google identity to the current account.
/// </summary>
public static class GoogleEndpoints
{
    private const string Provider = "google";
    private const string CookiePath = "/api/auth/google";
    private const string FlowCookie = "sayserne.g.flow";
    private const string PendingCookie = "sayserne.g.pending";
    private static readonly TimeSpan FlowLifetime = TimeSpan.FromMinutes(10);

    private sealed record FlowState(string State, string Nonce, string Verifier, string ReturnUrl, string Mode, long? LinkUserId);
    private sealed record PendingLink(long UserId, string Subject, string Email, bool EmailVerified);
    private sealed record TokenResponse(string? IdToken);
    public sealed record LinkPendingRequest(string? Password);

    public static void MapGoogle(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/auth/google").RequireRateLimiting("auth");
        g.MapGet("/start", Start);
        g.MapGet("/callback", Callback);
        g.MapPost("/link-pending", LinkPending);
    }

    private static IResult Fail(string code) => Results.Redirect($"/login?error={Uri.EscapeDataString(code)}");

    private static IResult Start(HttpContext ctx, IOptions<GoogleOptions> options, ProtectedCookies cookies, string? returnUrl, string? mode)
    {
        var o = options.Value;
        if (!o.Enabled) return Results.Json(new { error = "GOOGLE_NOT_CONFIGURED" }, statusCode: StatusCodes.Status503ServiceUnavailable);

        var linking = string.Equals(mode, "link", StringComparison.OrdinalIgnoreCase);
        long? linkUserId = null;
        if (linking)
        {
            linkUserId = ctx.User.Identity?.IsAuthenticated == true ? AuthSession.CurrentUserId(ctx.User) : null;
            if (linkUserId is null) return Fail("login_required");
        }

        var state = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var nonce = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(48));
        var challenge = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        cookies.Write(ctx, FlowCookie,
            new FlowState(state, nonce, verifier, InputValidation.SafeReturnUrl(returnUrl), linking ? "link" : "signin", linkUserId),
            FlowLifetime, CookiePath);

        var url = QueryHelpers.AddQueryString(o.AuthorizationEndpoint, new Dictionary<string, string?>
        {
            ["client_id"] = o.ClientId,
            ["redirect_uri"] = RedirectUri(ctx, o),
            ["response_type"] = "code",
            ["scope"] = "openid email profile",
            ["state"] = state,
            ["nonce"] = nonce,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["prompt"] = "select_account",
        });
        return Results.Redirect(url);
    }

    private static async Task<IResult> Callback(
        HttpContext ctx, IOptions<GoogleOptions> options, ProtectedCookies cookies, IGoogleIdTokenValidator validator,
        IHttpClientFactory httpFactory, IUserRepository users, IIdentityRepository identities, TimeProvider clock,
        ILoggerFactory loggerFactory, string? code, string? state, string? error)
    {
        var log = loggerFactory.CreateLogger("Sayserne.Google");
        var o = options.Value;
        var flow = cookies.Read<FlowState>(ctx, FlowCookie);
        cookies.Delete(ctx, FlowCookie, CookiePath);   // single use

        if (!o.Enabled || flow is null) return Fail("google_state");
        if (!string.IsNullOrEmpty(error)) return Fail("google_denied");
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state)
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(flow.State)))
            return Fail("google_state");

        GoogleIdentity identity;
        try
        {
            var idToken = await ExchangeCodeAsync(httpFactory, o, RedirectUri(ctx, o), code, flow.Verifier, ctx.RequestAborted);
            identity = await validator.ValidateAsync(idToken, flow.Nonce, ctx.RequestAborted);
        }
        catch (TokenValidationException ex)
        {
            log.LogWarning("Google ID token rejected: {Reason}", ex.Message);
            return Fail("google_token");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            log.LogWarning(ex, "Google code exchange failed");
            return Fail("google_exchange");
        }

        if (!identity.EmailVerified) return Fail("google_email_unverified");
        var email = InputValidation.NormalizeEmail(identity.Email);
        var now = clock.GetUtcNow().UtcDateTime;

        // --- explicit linking from a signed-in session ---
        if (flow.Mode == "link")
        {
            var currentId = ctx.User.Identity?.IsAuthenticated == true ? AuthSession.CurrentUserId(ctx.User) : null;
            if (currentId is null || currentId != flow.LinkUserId) return Fail("google_state");
            try { await identities.LinkAsync(currentId.Value, Provider, identity.Subject, email, true); }
            catch (AppException ex) when (ex.Code is "IDENTITY_ALREADY_LINKED") { return Results.Redirect(flow.ReturnUrl + "?google=already_linked"); }
            return Results.Redirect(flow.ReturnUrl);
        }

        // --- returning Google user ---
        var existing = await identities.FindUserAsync(Provider, identity.Subject);
        if (existing != null)
        {
            if (existing.IsLocked || existing.IsTemporarilyLocked(now)) return Fail("account_locked");
            await users.RecordLoginSuccessAsync(existing.Id);
            await AuthSession.SignInAsync(ctx, existing);
            return Results.Redirect(flow.ReturnUrl);
        }

        // --- same email already registered: require proof of ownership, do not merge ---
        var byEmail = await users.GetByEmailAsync(email);
        if (byEmail != null)
        {
            cookies.Write(ctx, PendingCookie, new PendingLink(byEmail.Id, identity.Subject, email, true), FlowLifetime, CookiePath);
            return Results.Redirect("/link-google");
        }

        // --- brand-new user ---
        var (first, last) = SplitName(identity, email);
        try
        {
            var created = await identities.CreateUserAsync(Provider, identity.Subject, email, true, first, last);
            await users.RecordLoginSuccessAsync(created.Id);
            await AuthSession.SignInAsync(ctx, created);
            return Results.Redirect(flow.ReturnUrl);
        }
        catch (AppException ex) when (ex.Code is "DUPLICATE_ACCOUNT")
        {
            return Fail("google_conflict");   // lost a race with another sign-up; user can simply retry
        }
    }

    private static async Task<IResult> LinkPending(
        HttpContext ctx, LinkPendingRequest request, ProtectedCookies cookies, IUserRepository users,
        IIdentityRepository identities, IPasswordHasher hasher, ISettingsService settings, TimeProvider clock)
    {
        var pending = cookies.Read<PendingLink>(ctx, PendingCookie) ?? throw new AppException("LINK_EXPIRED", 400);
        var user = await users.GetByIdAsync(pending.UserId) ?? throw new AppException("LINK_EXPIRED", 400);
        if (user.IsLocked || user.IsTemporarilyLocked(clock.GetUtcNow().UtcDateTime)) throw new AppException("ACCOUNT_LOCKED");
        if (user.PasswordHash is null) throw new AppException("LINK_NOT_POSSIBLE", 400);

        var password = request.Password ?? string.Empty;
        if (password.Length == 0 || password.Length > 1024 || !hasher.Verify(password, user.PasswordHash))
        {
            await users.RecordLoginFailureAsync(user.Id, settings.GetInt("Auth.MaxFailedAttempts"), settings.GetInt("Auth.LockoutMinutes"));
            throw new AppException("INVALID_CREDENTIALS");
        }

        var linked = await identities.LinkAsync(user.Id, Provider, pending.Subject, pending.Email, pending.EmailVerified);
        cookies.Delete(ctx, PendingCookie, CookiePath);
        await users.RecordLoginSuccessAsync(user.Id);
        await AuthSession.SignInAsync(ctx, linked);
        return Results.Ok(new { user = linked.ToDto() });
    }

    private static string RedirectUri(HttpContext ctx, GoogleOptions o) =>
        !string.IsNullOrWhiteSpace(o.RedirectUri) ? o.RedirectUri! : $"{ctx.Request.Scheme}://{ctx.Request.Host}{CookiePath}/callback";

    private static async Task<string> ExchangeCodeAsync(IHttpClientFactory factory, GoogleOptions o, string redirectUri, string code, string verifier, CancellationToken ct)
    {
        var http = factory.CreateClient("google");
        using var response = await http.PostAsync(o.TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = o.ClientId,
            ["client_secret"] = o.ClientSecret,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code",
            ["code_verifier"] = verifier,
        }), ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var idToken = doc.RootElement.TryGetProperty("id_token", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        return string.IsNullOrEmpty(idToken) ? throw new TokenValidationException("no id_token in response") : idToken;
    }

    private static (string First, string Last) SplitName(GoogleIdentity id, string email)
    {
        var first = id.GivenName?.Trim();
        var last = id.FamilyName?.Trim();
        if (string.IsNullOrEmpty(first) && !string.IsNullOrWhiteSpace(id.Name))
        {
            var parts = id.Name.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            first = parts[0];
            last ??= parts.Length > 1 ? parts[1] : null;
        }
        first = string.IsNullOrEmpty(first) ? email.Split('@')[0] : first;
        last ??= "";
        return (first.Length > 100 ? first[..100] : first, last.Length > 100 ? last[..100] : last);
    }
}
