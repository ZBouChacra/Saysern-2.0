using System.Globalization;
using System.Security.Cryptography;
using Sayserne.Api.Data;
using Sayserne.Api.Email;

namespace Sayserne.Api.Platform;

/// <summary>Replaces individual settings (tests use this to pin a value for one host without touching the shared database).</summary>
public sealed class SettingOverrides(IReadOnlyDictionary<string, string> values)
{
    public IReadOnlyDictionary<string, string> Values { get; } = values;
}

public interface ISettingsService
{
    int GetInt(string key);
    bool GetBool(string key);
    string GetString(string key);
    /// <summary>The decrypted value of a secret setting (for example the SMTP password).</summary>
    string GetSecret(string key);
    /// <summary>Changes whenever any value changes; lets caches (e.g. rate limiters) notice an admin edit.</summary>
    long Version { get; }
    Task RefreshAsync(CancellationToken ct = default);
    /// <summary>Settings flagged public, typed, for the web app.</summary>
    IReadOnlyDictionary<string, object> PublicValues();
}

/// <summary>
/// Runtime settings kept in the database and edited in the back office. Reads are served from an in-memory snapshot that is
/// refreshed after every admin change on this instance and periodically (see <see cref="SettingsRefresher"/>) for other instances.
/// A missing key is a deployment error and throws, never silently falls back to a hard-coded number.
/// </summary>
public sealed class SettingsService(IStoredProcedureExecutor db, SettingOverrides? overrides = null, ISecretProtector? secrets = null) : ISettingsService
{
    private sealed record Snapshot(IReadOnlyDictionary<string, SettingRow> Rows, long Version);
    private volatile Snapshot _snapshot = new(new Dictionary<string, SettingRow>(), 0);

    public long Version => _snapshot.Version;

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var rows = await db.QueryAsync("sp_settings_list", null, r => PlatformMappers.Setting(r, mask: false), ct);
        var map = rows.ToDictionary(r => r.Key, StringComparer.Ordinal);
        var changed = map.Count != _snapshot.Rows.Count || map.Any(kv => !_snapshot.Rows.TryGetValue(kv.Key, out var old) || old.Value != kv.Value.Value);
        if (changed) _snapshot = new Snapshot(map, _snapshot.Version + 1);
    }

    private string Raw(string key)
    {
        if (overrides?.Values.TryGetValue(key, out var forced) == true) return forced;
        if (_snapshot.Rows.TryGetValue(key, out var row)) return row.Value;
        throw new InvalidOperationException($"Setting '{key}' is not defined. Run the database migrations.");
    }

    public int GetInt(string key) => int.Parse(Raw(key), CultureInfo.InvariantCulture);
    public bool GetBool(string key) => string.Equals(Raw(key), "true", StringComparison.OrdinalIgnoreCase);
    public string GetString(string key) => Raw(key);

    public string GetSecret(string key)
    {
        if (overrides?.Values.TryGetValue(key, out var forced) == true) return forced;   // tests pin plain values
        var stored = Raw(key);
        if (stored.Length == 0) return "";
        try { return (secrets ?? throw new InvalidOperationException("No secret protector is configured.")).Unprotect(stored); }
        catch (CryptographicException ex) { throw new InvalidOperationException($"The stored secret '{key}' cannot be decrypted (the Data Protection keys changed). Enter it again in Settings.", ex); }
    }

    public IReadOnlyDictionary<string, object> PublicValues() => WithDerived(_snapshot.Rows.Values.Where(r => r.IsPublic).ToDictionary(r => r.Key, r => (object)(r.Type switch
    {
        "int" => (object)int.Parse(Raw(r.Key), CultureInfo.InvariantCulture),
        "decimal" => decimal.Parse(Raw(r.Key), CultureInfo.InvariantCulture),
        "bool" => string.Equals(Raw(r.Key), "true", StringComparison.OrdinalIgnoreCase),
        _ => Raw(r.Key),
    })));

    /// <summary>Facts the web app needs that are derived from several settings.</summary>
    private Dictionary<string, object> WithDerived(Dictionary<string, object> values)
    {
        // the logo is served by id; the public sees only its address ("" = no logo)
        values["Brand.LogoUrl"] = _snapshot.Rows.ContainsKey("Brand.LogoFileId") && GetString("Brand.LogoFileId") is { Length: > 0 } logo ? $"/api/files/{logo}" : "";
        values["Email.VerificationRequired"] = _snapshot.Rows.ContainsKey("Auth.RequireVerifiedEmail") && GetBool("Auth.RequireVerifiedEmail") && GetString("Email.Provider") != "off";
        return values;
    }
}

public sealed class SettingsRefresher(ISettingsService settings, IConfiguration config, ILogger<SettingsRefresher> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = Math.Max(config.GetValue<int?>("Settings:RefreshSeconds") ?? 15, 1);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await settings.RefreshAsync(stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Could not refresh settings; keeping the previous values."); }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }
}
