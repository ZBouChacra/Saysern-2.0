using System.Text.RegularExpressions;
using MySqlConnector;

namespace Sayserne.Migrator;

public sealed record ProvisionOptions(
    string DatabaseName,
    string MigratorUser,
    string MigratorPassword,
    string AppUser,
    string AppPassword,
    string UserHost = "localhost");

/// <summary>
/// Creates the database plus two accounts from db/setup/provision.sql:
/// a migrator (schema owner) and an app account that can only EXECUTE stored procedures.
/// Values are validated against strict allow-lists before being substituted into the SQL file.
/// </summary>
public static class Provisioner
{
    private static readonly Regex Identifier = new(@"^[A-Za-z0-9_]{1,32}$", RegexOptions.Compiled);
    private static readonly Regex Host = new(@"^[A-Za-z0-9_.%\-]{1,60}$", RegexOptions.Compiled);
    private static readonly Regex Password = new(@"^[A-Za-z0-9._~!@#%^*+=:,\-]{12,64}$", RegexOptions.Compiled);

    public static Task RunAsync(string adminConnectionString, string dbDirectory, ProvisionOptions o, CancellationToken ct = default) =>
        ApplyTemplateAsync("provision.sql", adminConnectionString, dbDirectory, o, ct);

    /// <summary>Drops the database and both accounts (db/setup/deprovision.sql). Destructive.</summary>
    public static Task DeprovisionAsync(string adminConnectionString, string dbDirectory, ProvisionOptions o, CancellationToken ct = default) =>
        ApplyTemplateAsync("deprovision.sql", adminConnectionString, dbDirectory, o, ct);

    private static async Task ApplyTemplateAsync(string templateFile, string adminConnectionString, string dbDirectory, ProvisionOptions o, CancellationToken ct)
    {
        Require(Identifier, o.DatabaseName, "database name (letters, digits, underscore; max 32)");
        Require(Identifier, o.MigratorUser, "migrator user");
        Require(Identifier, o.AppUser, "app user");
        if (string.Equals(o.MigratorUser, o.AppUser, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The migrator and app accounts must be different users.");
        Require(Host, o.UserHost, "user host");
        Require(Password, o.MigratorPassword, "migrator password (12-64 chars; letters, digits and . _ ~ ! @ # % ^ * + = : , -)");
        Require(Password, o.AppPassword, "app password (12-64 chars; letters, digits and . _ ~ ! @ # % ^ * + = : , -)");

        var text = await File.ReadAllTextAsync(Path.Combine(dbDirectory, "setup", templateFile), ct).ConfigureAwait(false);
        text = text.Replace("{{DB_NAME}}", o.DatabaseName)
                   .Replace("{{MIGRATOR_USER}}", o.MigratorUser).Replace("{{MIGRATOR_PASSWORD}}", o.MigratorPassword)
                   .Replace("{{APP_USER}}", o.AppUser).Replace("{{APP_PASSWORD}}", o.AppPassword)
                   .Replace("{{USER_HOST}}", o.UserHost);

        await using var conn = new MySqlConnection(adminConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await ScriptRunner.ExecuteAsync(conn, text, ct).ConfigureAwait(false);
    }

    private static void Require(Regex pattern, string value, string what)
    {
        if (!pattern.IsMatch(value ?? string.Empty)) throw new ArgumentException($"Invalid {what}.");
    }
}
