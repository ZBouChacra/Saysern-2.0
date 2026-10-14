namespace Sayserne.Migrator;

/// <summary>Finds the repository's <c>db/</c> folder (bootstrap, migrations, setup scripts).</summary>
public static class DbDirectory
{
    public static string Locate(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var full = Path.GetFullPath(explicitPath);
            if (!Directory.Exists(Path.Combine(full, "migrations")))
                throw new DirectoryNotFoundException($"'{full}' does not contain a 'migrations' folder.");
            return full;
        }

        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "db");
                if (Directory.Exists(Path.Combine(candidate, "migrations"))) return candidate;
            }
        }
        throw new DirectoryNotFoundException("Could not find the 'db' folder. Run from inside the repository or pass --db-dir.");
    }
}
