using System.Data;
using MySqlConnector;
using Sayserne.Migrator;

// Usage:
//   migrate                  env SAYSERNE_MIGRATOR_CONNECTION
//   provision                env SAYSERNE_ADMIN_CONNECTION, SAYSERNE_DB_NAME, SAYSERNE_MIGRATOR_USER/_PASSWORD,
//                            SAYSERNE_APP_USER/_PASSWORD, optional SAYSERNE_USER_HOST (default localhost)
//   promote-admin <email>    env SAYSERNE_MIGRATOR_CONNECTION
// Optional: --db-dir <path>

static string Env(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : throw new InvalidOperationException($"Environment variable {name} is not set.");

try
{
    var argv = args.ToList();
    string? dbDirArg = null;
    var idx = argv.IndexOf("--db-dir");
    if (idx >= 0 && idx + 1 < argv.Count) { dbDirArg = argv[idx + 1]; argv.RemoveRange(idx, 2); }
    var command = argv.FirstOrDefault();
    var dbDir = DbDirectory.Locate(dbDirArg);

    switch (command)
    {
        case "migrate":
        {
            var result = await new MigrationRunner(Env("SAYSERNE_MIGRATOR_CONNECTION"), dbDir, Console.WriteLine).RunAsync();
            Console.WriteLine($"Done. Applied {result.AppliedVersioned.Count} versioned and {result.AppliedRepeatable.Count} repeatable script(s); {result.Unchanged.Count} unchanged.");
            return 0;
        }
        case "provision":
        {
            await Provisioner.RunAsync(Env("SAYSERNE_ADMIN_CONNECTION"), dbDir, new ProvisionOptions(
                Env("SAYSERNE_DB_NAME"), Env("SAYSERNE_MIGRATOR_USER"), Env("SAYSERNE_MIGRATOR_PASSWORD"),
                Env("SAYSERNE_APP_USER"), Env("SAYSERNE_APP_PASSWORD"),
                Environment.GetEnvironmentVariable("SAYSERNE_USER_HOST") is { Length: > 0 } h ? h : "localhost"));
            Console.WriteLine("Database and accounts provisioned.");
            return 0;
        }
        case "deprovision":
        {
            await Provisioner.DeprovisionAsync(Env("SAYSERNE_ADMIN_CONNECTION"), dbDir, new ProvisionOptions(
                Env("SAYSERNE_DB_NAME"), Env("SAYSERNE_MIGRATOR_USER"), Env("SAYSERNE_MIGRATOR_PASSWORD"),
                Env("SAYSERNE_APP_USER"), Env("SAYSERNE_APP_PASSWORD"),
                Environment.GetEnvironmentVariable("SAYSERNE_USER_HOST") is { Length: > 0 } h ? h : "localhost"));
            Console.WriteLine("Database and accounts removed.");
            return 0;
        }
        case "promote-admin":
        {
            var email = argv.Count > 1 ? argv[1] : throw new ArgumentException("Usage: promote-admin <email>");
            await using var conn = new MySqlConnection(Env("SAYSERNE_MIGRATOR_CONNECTION"));
            await conn.OpenAsync();
            await using var cmd = new MySqlCommand("sp_admin_users_promote_by_email", conn) { CommandType = CommandType.StoredProcedure };
            cmd.Parameters.AddWithValue("p_email", email.Trim());
            await cmd.ExecuteNonQueryAsync();
            Console.WriteLine($"{email} is now an admin.");
            return 0;
        }
        default:
            Console.Error.WriteLine("Commands: migrate | provision | deprovision | promote-admin <email>   (optional: --db-dir <path>)");
            return 2;
    }
}
catch (MySqlException ex)
{
    Console.Error.WriteLine($"Database error: {ex.Message}");
    return 1;
}
catch (Exception ex) when (ex is MigrationException or ArgumentException or InvalidOperationException or DirectoryNotFoundException)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}
