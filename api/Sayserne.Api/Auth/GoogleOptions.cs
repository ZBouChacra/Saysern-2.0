namespace Sayserne.Api.Auth;

/// <summary>Bound from the "Google" configuration section. Secrets come from env vars / user-secrets, never from git.</summary>
public sealed class GoogleOptions
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    /// <summary>Must exactly match an "Authorized redirect URI" in the Google console, e.g. http://localhost:5173/api/auth/google/callback</summary>
    public string? RedirectUri { get; set; }
    public string AuthorizationEndpoint { get; set; } = "https://accounts.google.com/o/oauth2/v2/auth";
    public string TokenEndpoint { get; set; } = "https://oauth2.googleapis.com/token";
    public string JwksUri { get; set; } = "https://www.googleapis.com/oauth2/v3/certs";
    public string[] Issuers { get; set; } = ["https://accounts.google.com", "accounts.google.com"];

    public bool Enabled => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}
