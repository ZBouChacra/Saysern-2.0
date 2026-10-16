using System.Text.RegularExpressions;

namespace Sayserne.Tests;

/// <summary>
/// Architecture guard: application code may only call stored procedures.
/// Fails the build if SQL text, direct command execution, an ORM or a query builder sneaks into api/ or tests/.
/// SQL belongs in db/ (migrations and setup scripts) and nowhere else.
/// </summary>
public class SqlGuardTests
{
    private static readonly Regex SqlText = new(
        @"\b(select\s+(distinct\s+)?[\w\*\.,`\s\(\)]{1,200}?\s+from\s+[\w`\[(]|insert\s+into|update\s+\w+\s+set|delete\s+from|create\s+(table|procedure|database|user|index)|drop\s+(table|procedure|database|user)|alter\s+table|truncate\s+table|grant\s+\w+\s+on)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] ForbiddenEverywhere =
        ["FromSql", "ExecuteSql", "SqlQuery", "DbContext", "EntityFramework", "Dapper", "MySqlScript", "MySqlBulk", "SqlBuilder"];

    private static readonly Regex ForbiddenPackages = new(
        @"PackageReference\s+Include=""[^""]*(EntityFramework|Pomelo|Dapper|linq2db|LinqToDB|SqlKata|NHibernate|OrmLite|RepoDb)[^""]*""", RegexOptions.IgnoreCase);

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "api")) && Directory.Exists(Path.Combine(dir.FullName, "db"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root not found");
    }

    private static IEnumerable<string> CSharpFiles(params string[] folders) =>
        folders.SelectMany(f => Directory.EnumerateFiles(Path.Combine(RepoRoot(), f), "*.cs", SearchOption.AllDirectories))
               .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
               .Where(p => Path.GetFileName(p) != "SqlGuardTests.cs");

    /// <summary>Removes // and /* */ comments but leaves string/char literals intact, so SQL cannot hide behind a fake comment.</summary>
    public static string StripComments(string code)
    {
        var sb = new System.Text.StringBuilder(code.Length);
        for (var i = 0; i < code.Length; i++)
        {
            var c = code[i];
            var next = i + 1 < code.Length ? code[i + 1] : '\0';
            if (c == '/' && next == '/') { while (i < code.Length && code[i] != '\n') i++; sb.Append('\n'); continue; }
            if (c == '/' && next == '*') { i += 2; while (i + 1 < code.Length && !(code[i] == '*' && code[i + 1] == '/')) i++; i++; sb.Append(' '); continue; }
            if (c == '"' && i + 2 < code.Length && code[i + 1] == '"' && code[i + 2] == '"')            // raw string literal """..."""
            {
                var end = code.IndexOf("\"\"\"", i + 3, StringComparison.Ordinal);
                var stop = end < 0 ? code.Length : end + 3;
                sb.Append(code, i, stop - i); i = stop - 1; continue;
            }
            if (c == '"' || c == '\'')
            {
                var verbatim = c == '"' && i > 0 && (code[i - 1] == '@' || (i > 1 && code[i - 1] == '$' && code[i - 2] == '@') || (i > 1 && code[i - 1] == '@' && code[i - 2] == '$'));
                var j = i + 1;
                while (j < code.Length)
                {
                    if (verbatim) { if (code[j] == '"') { if (j + 1 < code.Length && code[j + 1] == '"') { j += 2; continue; } break; } }
                    else if (code[j] == '\\') { j += 2; continue; }
                    else if (code[j] == c || code[j] == '\n') break;
                    j++;
                }
                var stop = Math.Min(j + 1, code.Length);
                sb.Append(code, i, stop - i); i = stop - 1; continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    public static List<string> FindViolations(string code, bool checkSqlText = true)
    {
        var text = StripComments(code);
        var found = new List<string>();
        if (checkSqlText) foreach (Match m in SqlText.Matches(text)) found.Add($"SQL text: '{m.Value}'");
        foreach (var token in ForbiddenEverywhere) if (text.Contains(token, StringComparison.Ordinal)) found.Add($"forbidden API: {token}");
        return found;
    }

    [Fact]
    public void The_guard_actually_detects_violations()
    {
        Assert.True(FindViolations("var q = \"SELECT * FROM users WHERE id = \" + id;").Count > 0);
        Assert.True(FindViolations("cmd.CommandText = \"INSERT INTO users VALUES (1)\";").Count > 0);
        Assert.True(FindViolations("ctx.Users.FromSqlRaw(x)").Count > 0);
        Assert.True(FindViolations("class C : DbContext {}").Count > 0);
        Assert.Equal(0, FindViolations("db.ExecuteAsync(\"sp_users_get_by_id\", args); // select the user from the cache").Count);
        // C# that merely looks like SQL (LINQ Select ... From) must not be flagged
        Assert.Equal(0, FindViolations("var rows = result.Items.Select(AdminUserDto.From).ToList(); var x = items.Select(r => r.From);").Count);
        Assert.Equal(0, FindViolations("var u = \"https://accounts.google.com\"; /* delete from the list */").Count);
        // SQL hidden after something that merely looks like a comment inside a string must still be caught
        Assert.True(FindViolations("var q = \"a//b\" + \"SELECT * FROM users\";").Count > 0);
        Assert.True(FindViolations("var q = @\"SELECT id FROM users\"; // harmless").Count > 0);
        Assert.True(FindViolations("var q = \"\"\"\n INSERT INTO users VALUES (1)\n \"\"\";").Count > 0);
    }

    [Fact]
    public void No_sql_text_or_orm_apis_in_application_code()
    {
        var problems = new List<string>();
        foreach (var file in CSharpFiles("api"))
            foreach (var v in FindViolations(File.ReadAllText(file)))
                problems.Add($"{Path.GetRelativePath(RepoRoot(), file)}: {v}");
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void Tests_use_no_orm_or_direct_query_apis_and_keep_sql_in_fixture_files()
    {
        // Test code may contain SQL-looking strings only as DATA (injection payloads, synthetic inputs for the
        // script splitter). It must never use ORM / raw-query APIs, and real SQL statements live in fixtures/*.sql.
        var problems = new List<string>();
        foreach (var file in CSharpFiles("tests"))
            foreach (var v in FindViolations(File.ReadAllText(file), checkSqlText: false))
                problems.Add($"{Path.GetRelativePath(RepoRoot(), file)}: {v}");
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void Only_the_executor_and_the_migrator_script_runner_touch_commands()
    {
        var problems = new List<string>();
        foreach (var file in CSharpFiles("api"))
        {
            var name = Path.GetFileName(file);
            var code = StripComments(File.ReadAllText(file));
            var inApi = file.Contains($"Sayserne.Api{Path.DirectorySeparatorChar}");
            if (code.Contains("CommandType.Text") && name != "ScriptRunner.cs") problems.Add($"{name}: CommandType.Text");
            if (code.Contains("CommandText") && name != "ScriptRunner.cs") problems.Add($"{name}: CommandText");
            if (inApi && Regex.IsMatch(code, @"\bMySqlCommand\b") && name != "StoredProcedureExecutor.cs") problems.Add($"{name}: MySqlCommand outside the executor");
            if (inApi && Regex.IsMatch(code, @"\bMySqlConnection\b") && name != "StoredProcedureExecutor.cs") problems.Add($"{name}: raw MySqlConnection in the API project");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void Every_database_call_goes_through_the_executor_and_names_a_deployed_procedure()
    {
        var referenced = new HashSet<string>();
        foreach (var file in CSharpFiles("api"))
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"""(sp_[a-z0-9_]+)"""))
                referenced.Add(m.Groups[1].Value);

        var defined = new HashSet<string>();
        foreach (var sql in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "db"), "*.sql", SearchOption.AllDirectories))
            foreach (Match m in Regex.Matches(File.ReadAllText(sql), @"CREATE\s+PROCEDURE\s+(sp_[a-z0-9_]+)", RegexOptions.IgnoreCase))
                defined.Add(m.Groups[1].Value);

        Assert.True(referenced.Count > 0, "expected the API to reference stored procedures");
        var missing = referenced.Where(r => !defined.Contains(r)).ToList();
        Assert.True(missing.Count == 0, "procedures referenced in C# but not defined in db/: " + string.Join(", ", missing));
    }

    [Fact]
    public void No_orm_or_query_builder_packages_are_referenced()
    {
        var problems = Directory.EnumerateFiles(RepoRoot(), "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(p => ForbiddenPackages.IsMatch(File.ReadAllText(p)))
            .Select(p => Path.GetRelativePath(RepoRoot(), p)).ToList();
        Assert.True(problems.Count == 0, "ORM/query-builder package referenced in: " + string.Join(", ", problems));
    }
}
