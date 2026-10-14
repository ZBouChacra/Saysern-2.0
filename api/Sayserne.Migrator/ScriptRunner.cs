using System.Data;
using System.Text;
using MySqlConnector;

namespace Sayserne.Migrator;

/// <summary>
/// The only place in the code base that executes SQL text, and only text loaded from the
/// versioned .sql files under db/ (migrations, bootstrap, provisioning). No SQL is authored in C#.
/// </summary>
public static class ScriptRunner
{
    public static async Task ExecuteAsync(MySqlConnection connection, string scriptText, CancellationToken ct)
    {
        foreach (var statement in Split(scriptText))
        {
            await using var cmd = new MySqlCommand { Connection = connection, CommandType = CommandType.Text, CommandText = statement };
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Splits a script into statements, honouring the mysql client's <c>DELIMITER</c> command
    /// (needed for stored-procedure bodies). A delimiter must be the last thing on its line.
    /// Blank lines and full-line comments between statements are dropped.
    /// </summary>
    public static IReadOnlyList<string> Split(string script)
    {
        var statements = new List<string>();
        var delimiter = ";";
        var buffer = new StringBuilder();

        foreach (var rawLine in script.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = rawLine.Trim();
            if (buffer.Length == 0)
            {
                if (trimmed.StartsWith("DELIMITER ", StringComparison.OrdinalIgnoreCase))
                {
                    delimiter = trimmed["DELIMITER ".Length..].Trim();
                    continue;
                }
                if (trimmed.Length == 0 || trimmed.StartsWith("--", StringComparison.Ordinal) || trimmed.StartsWith('#'))
                    continue;
            }

            buffer.Append(rawLine).Append('\n');
            if (trimmed.EndsWith(delimiter, StringComparison.Ordinal))
            {
                var text = buffer.ToString().TrimEnd();
                text = text[..^delimiter.Length].Trim();
                if (text.Length > 0) statements.Add(text);
                buffer.Clear();
            }
        }

        var rest = buffer.ToString().Trim();
        if (rest.Length > 0) statements.Add(rest);
        return statements;
    }

    /// <summary>SHA-256 over the text with normalised line endings, so Windows/Linux checkouts agree.</summary>
    public static string Checksum(string scriptText)
    {
        var normalised = scriptText.Replace("\r\n", "\n");
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(normalised));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
