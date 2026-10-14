using System.Data;
using System.Text.RegularExpressions;
using MySqlConnector;

namespace Sayserne.Migrator;

public sealed class MigrationException(string message) : Exception(message);

public sealed record MigrationResult(
    IReadOnlyList<string> AppliedVersioned,
    IReadOnlyList<string> AppliedRepeatable,
    IReadOnlyList<string> Unchanged);

/// <summary>
/// Applies db/bootstrap (idempotent), then db/migrations:
///   V###__name.sql  versioned, applied once, in numeric order; a changed checksum after the fact is an error.
///   R__name.sql     repeatable (stored procedures), re-applied whenever the file's checksum changes.
/// The journal is read and written exclusively through stored procedures.
/// </summary>
public sealed class MigrationRunner(string connectionString, string dbDirectory, Action<string>? log = null)
{
    private static readonly Regex VersionedName = new(@"^V(\d+)__([A-Za-z0-9_\-]+)\.sql$", RegexOptions.Compiled);
    private static readonly Regex RepeatableName = new(@"^R__([A-Za-z0-9_\-]+)\.sql$", RegexOptions.Compiled);

    public async Task<MigrationResult> RunAsync(CancellationToken ct = default)
    {
        var versioned = new List<(int Version, string Name, string Path)>();
        var repeatable = new List<(string Name, string Path)>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(dbDirectory, "migrations"), "*.sql"))
        {
            var fileName = Path.GetFileName(file);
            var v = VersionedName.Match(fileName);
            var r = RepeatableName.Match(fileName);
            if (v.Success) versioned.Add((int.Parse(v.Groups[1].Value), fileName, file));
            else if (r.Success) repeatable.Add((fileName, file));
            else throw new MigrationException($"Unrecognised migration file name '{fileName}'. Use V###__name.sql or R__name.sql.");
        }
        var dup = versioned.GroupBy(x => x.Version).FirstOrDefault(g => g.Count() > 1);
        if (dup != null) throw new MigrationException($"Duplicate migration version {dup.Key}.");
        versioned.Sort((a, b) => a.Version.CompareTo(b.Version));
        repeatable.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        var bootstrapDir = Path.Combine(dbDirectory, "bootstrap");
        if (Directory.Exists(bootstrapDir))
            foreach (var file in Directory.EnumerateFiles(bootstrapDir, "*.sql").Order(StringComparer.Ordinal))
                await ScriptRunner.ExecuteAsync(conn, await File.ReadAllTextAsync(file, ct), ct).ConfigureAwait(false);

        var acquired = await CallScalarAsync(conn, "sp_schema_migrations_lock", ct).ConfigureAwait(false);
        if (acquired != 1) throw new MigrationException("Another migration run holds the lock; try again shortly.");
        try
        {
            var applied = await LoadJournalAsync(conn, ct).ConfigureAwait(false);
            var appliedV = new List<string>();
            var appliedR = new List<string>();
            var unchanged = new List<string>();

            foreach (var (version, name, path) in versioned)
            {
                var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
                var checksum = ScriptRunner.Checksum(text);
                if (applied.TryGetValue($"versioned:{name}", out var existing))
                {
                    if (!string.Equals(existing, checksum, StringComparison.Ordinal))
                        throw new MigrationException($"Versioned migration {name} was modified after it was applied. Add a new migration instead.");
                    unchanged.Add(name);
                    continue;
                }
                log?.Invoke($"Applying {name}");
                await ScriptRunner.ExecuteAsync(conn, text, ct).ConfigureAwait(false);
                await RecordAsync(conn, "versioned", version.ToString(), name, checksum, ct).ConfigureAwait(false);
                appliedV.Add(name);
            }

            foreach (var (name, path) in repeatable)
            {
                var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
                var checksum = ScriptRunner.Checksum(text);
                if (applied.TryGetValue($"repeatable:{name}", out var existing) && existing == checksum)
                {
                    unchanged.Add(name);
                    continue;
                }
                log?.Invoke($"Applying {name}");
                await ScriptRunner.ExecuteAsync(conn, text, ct).ConfigureAwait(false);
                await RecordAsync(conn, "repeatable", null, name, checksum, ct).ConfigureAwait(false);
                appliedR.Add(name);
            }
            return new MigrationResult(appliedV, appliedR, unchanged);
        }
        finally
        {
            await CallScalarAsync(conn, "sp_schema_migrations_unlock", CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task<long> CallScalarAsync(MySqlConnection conn, string procedure, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand(procedure, conn) { CommandType = CommandType.StoredProcedure };
        var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is null or DBNull ? 0 : Convert.ToInt64(value);
    }

    private static async Task<Dictionary<string, string>> LoadJournalAsync(MySqlConnection conn, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var cmd = new MySqlCommand("sp_schema_migrations_list", conn) { CommandType = CommandType.StoredProcedure };
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            map[$"{reader.GetString(0)}:{reader.GetString(1)}"] = reader.GetString(2);
        return map;
    }

    private static async Task RecordAsync(MySqlConnection conn, string kind, string? version, string name, string checksum, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand("sp_schema_migrations_record", conn) { CommandType = CommandType.StoredProcedure };
        cmd.Parameters.AddWithValue("p_kind", kind);
        cmd.Parameters.AddWithValue("p_version", (object?)version ?? DBNull.Value);
        cmd.Parameters.AddWithValue("p_name", name);
        cmd.Parameters.AddWithValue("p_checksum", checksum);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
