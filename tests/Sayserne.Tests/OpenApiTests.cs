using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sayserne.Tests;

public class OpenApiTests
{
    private static async Task<(IsolatedHost Iso, string Text, JsonElement Doc)> Fetch()
    {
        var iso = await IsolatedHost.StartAsync(new Dictionary<string, string?> { ["OpenApi:Enabled"] = "true" });
        var res = await iso.Host.NewClient().GetBytesAsync("/api/openapi.json");
        Assert.Equal(HttpStatusCode.OK, res.Status);
        var text = System.Text.Encoding.UTF8.GetString(res.Bytes);
        return (iso, text, JsonDocument.Parse(text).RootElement);
    }

    [Fact]
    public async Task The_description_is_off_unless_it_is_switched_on()
    {
        await using var iso = await IsolatedHost.StartAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await iso.Host.NewClient().GetAsync("/api/openapi.json")).Status);
    }

    [Fact]
    public async Task It_describes_the_public_api_with_parameters_bodies_and_who_may_call_it_and_leaves_the_back_office_out()
    {
        var (iso, _, doc) = await Fetch(); await using var _i = iso;
        Assert.Equal("3.0.3", doc.GetProperty("openapi").GetString());
        var paths = doc.GetProperty("paths");
        var all = paths.EnumerateObject().Select(p => p.Name).ToList();
        Assert.True(all.Count > 45, $"{all.Count} paths");   // every public route
        Assert.False(all.Any(p => p.Length > 1 && p.EndsWith('/')));
        Assert.False(all.Any(p => p.StartsWith("/api/admin", StringComparison.Ordinal) || p == "/api/openapi.json"));   // the back office is not part of the public description

        var login = paths.GetProperty("/api/auth/login").GetProperty("post");
        var fields = login.GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("schema");
        Assert.Equal("#/components/schemas/LoginRequest", fields.GetProperty("$ref").GetString());
        var props = doc.GetProperty("components").GetProperty("schemas").GetProperty("LoginRequest").GetProperty("properties");
        Assert.True(props.TryGetProperty("email", out _) && props.TryGetProperty("password", out _));
        Assert.False(login.TryGetProperty("security", out _));                                                              // signing in needs no session
        Assert.Equal("auth", login.GetProperty("x-rate-limit-policy").GetString());                                          // ...but is rate limited

        var profile = paths.GetProperty("/api/professionals/{userId}").GetProperty("get");
        var userId = profile.GetProperty("parameters").EnumerateArray().Single(p => p.GetProperty("name").GetString() == "userId");
        Assert.Equal(("path", true, "integer"), (userId.GetProperty("in").GetString(), userId.GetProperty("required").GetBoolean(), userId.GetProperty("schema").GetProperty("type").GetString()));
        Assert.False(profile.TryGetProperty("security", out _));                                                             // a public page

        var book = paths.GetProperty("/api/appointments").GetProperty("post");
        Assert.Equal("sessionCookie", book.GetProperty("security")[0].EnumerateObject().Single().Name);                      // booking needs a signed-in person
        Assert.True(doc.GetProperty("components").GetProperty("schemas").GetProperty("BookingInput").GetProperty("properties").TryGetProperty("durationMinutes", out _));

        var upload = paths.GetProperty("/api/files").GetProperty("post");
        Assert.True(upload.GetProperty("requestBody").GetProperty("content").TryGetProperty("multipart/form-data", out _));
        Assert.Equal(new[] { "purpose", "roomId" }, upload.GetProperty("parameters").EnumerateArray().Select(p => p.GetProperty("name").GetString()!).ToArray());

        var search = paths.GetProperty("/api/professionals/search").GetProperty("get");
        var filters = search.GetProperty("parameters").EnumerateArray().ToDictionary(p => p.GetProperty("name").GetString()!, p => p);
        Assert.True(filters.Count > 15 && filters.Values.All(p => p.GetProperty("in").GetString() == "query" && !p.GetProperty("required").GetBoolean()));   // every filter is an optional query parameter
        Assert.True(filters.ContainsKey("city") && filters.ContainsKey("minStars") && filters.ContainsKey("visitorCity"));
        Assert.Equal("number", filters["minStars"].GetProperty("schema").GetProperty("type").GetString());
        Assert.True(doc.GetProperty("info").GetProperty("description").GetString()!.Contains("X-Requested-With"));            // the cross-site rule a native app must know
    }

    [Fact]
    public async Task The_document_is_consistent_every_reference_resolves_every_path_parameter_is_declared_and_ids_are_unique()
    {
        var (iso, text, doc) = await Fetch(); await using var _i = iso;
        var schemas = doc.GetProperty("components").GetProperty("schemas");
        foreach (Match m in Regex.Matches(text, "\"#/components/schemas/([A-Za-z0-9_]+)\""))
            Assert.True(schemas.TryGetProperty(m.Groups[1].Value, out _), "dangling reference " + m.Value);
        var ids = new List<string>();
        foreach (var path in doc.GetProperty("paths").EnumerateObject())
            foreach (var op in path.Value.EnumerateObject())
            {
                ids.Add(op.Value.GetProperty("operationId").GetString()!);
                Assert.True(op.Value.TryGetProperty("responses", out var r) && r.TryGetProperty("default", out _), path.Name);
                var declared = op.Value.TryGetProperty("parameters", out var ps) ? ps.EnumerateArray().Where(p => p.GetProperty("in").GetString() == "path").Select(p => p.GetProperty("name").GetString()).ToHashSet() : new HashSet<string?>();
                foreach (Match m in Regex.Matches(path.Name, @"\{(\w+)\}")) Assert.True(declared.Contains(m.Groups[1].Value), $"{op.Name} {path.Name} does not declare {m.Groups[1].Value}");
            }
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.False(text.Contains("EqualityContract"));
    }

    [Fact]
    public async Task The_committed_description_matches_the_code_run_with_SAYSERNE_UPDATE_OPENAPI_equals_1_to_regenerate_it()
    {
        var (iso, text, _) = await Fetch(); await using var _i = iso;
        var file = Path.GetFullPath(Path.Combine(TestEnv.DbDir, "..", "docs", "openapi.json"));
        if (Environment.GetEnvironmentVariable("SAYSERNE_UPDATE_OPENAPI") == "1") { File.WriteAllText(file, text); return; }
        Assert.True(File.Exists(file), "docs/openapi.json is missing: run the tests once with SAYSERNE_UPDATE_OPENAPI=1");
        Assert.True(File.ReadAllText(file).Replace("\r\n", "\n") == text, "The API changed but docs/openapi.json was not regenerated: run the tests with SAYSERNE_UPDATE_OPENAPI=1 and commit the result.");
    }
}
