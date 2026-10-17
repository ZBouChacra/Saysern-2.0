using System.Net;
using System.Text.Json;

namespace Sayserne.Tests;

public class UiTextsTests
{
    private static string SeedSql() => File.ReadAllText(Path.Combine(TestEnv.DbDir, "migrations", "R__ui_texts.sql"));

    [Fact]
    public async Task Every_screen_phrase_is_served_in_English_and_Arabic_with_the_right_direction()
    {
        await using var iso = await IsolatedHost.StartAsync();
        var c = iso.Host.NewClient();
        var en = (await c.GetAsync("/api/translations/en")).Body; var ar = (await c.GetAsync("/api/translations/ar")).Body;
        Assert.Equal("Sign in", en.GetProperty("ui:Sign in").GetString());
        Assert.Equal("تسجيل الدخول", ar.GetProperty("ui:Sign in").GetString());
        Assert.Equal("أهلًا، {firstName}", ar.GetProperty("ui:Welcome, {firstName}").GetString());                      // placeholders travel with the text
        var phrases = en.EnumerateObject().Count(p => p.Name.StartsWith("ui:")); var arabic = ar.EnumerateObject().Count(p => p.Name.StartsWith("ui:"));
        Assert.True(phrases > 600 && phrases == arabic, $"{phrases} English, {arabic} Arabic");                         // none is left untranslated
        Assert.True(en.EnumerateObject().Any(p => p.Name.StartsWith("ui:") && p.Name.Contains("…#")));                   // the long help texts are there too, under their short keys
        var langs = (await c.GetAsync("/api/lookups/language")).Body.EnumerateArray().ToDictionary(l => l.GetProperty("code").GetString()!, l => l);
        Assert.Equal("rtl", langs["ar"].GetProperty("metadata").GetProperty("dir").GetString());                         // the site switches to right-to-left for Arabic
        Assert.Equal("العربية", langs["ar"].GetProperty("names").GetProperty("ar").GetString());
    }

    [Fact]
    public async Task Running_the_seed_again_refreshes_untouched_texts_but_never_overwrites_what_an_admin_changed()
    {
        await using var iso = await IsolatedHost.StartAsync();
        var (admin, _, _) = await iso.Host.CreateAdminAsync();
        var edited = Uri.EscapeDataString("ui:Save");
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync($"/api/admin/translations/ar/{edited}", new { value = "احفظ (صياغتي)" })).Status);   // an admin rewords one text
        await iso.Db.ExecAsync("UPDATE translations SET value = 'old wording', default_value = 'old wording' WHERE language = 'ar' AND translation_key = 'ui:Cancel'");   // an untouched one from an older release
        await iso.Db.ExecAsync(SeedSql());                                                                                 // the next release's seed runs
        var ar = (await iso.Host.NewClient().GetAsync("/api/translations/ar")).Body;
        Assert.Equal("احفظ (صياغتي)", ar.GetProperty("ui:Save").GetString());                                              // the admin's wording is kept
        Assert.Equal("إلغاء", ar.GetProperty("ui:Cancel").GetString());                                                    // the untouched one was refreshed
        Assert.Equal("حفظ", await iso.Db.ScalarAsync("SELECT default_value FROM translations WHERE language = 'ar' AND translation_key = 'ui:Save'"));   // and the original is still there to reset to
    }
}
