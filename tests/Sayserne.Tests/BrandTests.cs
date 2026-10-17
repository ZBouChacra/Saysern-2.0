using System.Net;
using System.Text.Json;

namespace Sayserne.Tests;

public class BrandTests
{
    private static async Task<JsonElement> Public(ApiHost host) => (await host.NewClient().GetAsync("/api/config/public")).Body;

    [Fact]
    public async Task Colours_start_as_the_original_ones_accept_only_hex_values_and_can_be_reset()
    {
        await using var iso = await IsolatedHost.StartAsync();
        var (admin, _, _) = await iso.Host.CreateAdminAsync();
        var pub = await Public(iso.Host);
        Assert.Equal(("#2d6d5f", "#1f4d43", "#d4a757", ""), (pub.GetProperty("Brand.PrimaryColor").GetString(), pub.GetProperty("Brand.PrimaryDarkColor").GetString(), pub.GetProperty("Brand.AccentColor").GetString(), pub.GetProperty("Brand.LogoUrl").GetString()));
        foreach (var bad in new[] { "red", "#fff", "#12345g", "2d6d5f", "#2d6d5f;color:red", "url(x)", "" })
            Assert.Equal((HttpStatusCode.BadRequest, "SETTING_INVALID"), ((await admin.PutAsync("/api/admin/settings/Brand.PrimaryColor", new { value = bad })).Status, TestClient.Error((await admin.PutAsync("/api/admin/settings/Brand.PrimaryColor", new { value = bad })).Body)));
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/admin/settings/Brand.PrimaryColor", new { value = "#1A5FB4" })).Status);
        Assert.Equal("#1A5FB4", (await Public(iso.Host)).GetProperty("Brand.PrimaryColor").GetString());
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/admin/settings/Brand.PrimaryColor/reset")).Status);
        Assert.Equal("#2d6d5f", (await Public(iso.Host)).GetProperty("Brand.PrimaryColor").GetString());
        var (nobody, _, _) = await iso.Host.CreateStaffAsync("settings.read");
        Assert.Equal(HttpStatusCode.Forbidden, (await nobody.PutAsync("/api/admin/settings/Brand.AccentColor", new { value = "#112233" })).Status);   // changing the look needs settings.manage
    }

    [Fact]
    public async Task The_logo_is_uploaded_by_staff_only_must_be_a_logo_and_is_served_to_everyone()
    {
        await using var iso = await IsolatedHost.StartAsync();
        var (admin, _, _) = await iso.Host.CreateAdminAsync();
        var (user, _, _) = await iso.Host.RegisterAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.UploadAsync("/api/admin/brand/logo", TestMedia.SmallJpeg(), "l.jpg")).Item1);                       // an ordinary account is not staff
        Assert.Equal(HttpStatusCode.BadRequest, (await user.UploadAsync("/api/files?purpose=brand_logo", TestMedia.SmallJpeg(), "l.jpg")).Item1);                   // and cannot use the public upload for it
        var (reader, _, _) = await iso.Host.CreateStaffAsync("settings.read");
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.UploadAsync("/api/admin/brand/logo", TestMedia.SmallJpeg(), "l.jpg")).Item1);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.UploadAsync("/api/admin/brand/logo", new byte[] { 1, 2, 3, 4 }, "not-a-picture.jpg")).Item1);          // judged by its bytes, not its name

        var (st, up) = await admin.UploadAsync("/api/admin/brand/logo", TestMedia.SmallJpeg(), "logo.jpg");
        Assert.Equal(HttpStatusCode.Created, st);
        var id = up.GetProperty("id").GetString()!;
        Assert.Equal("SETTING_INVALID", TestClient.Error((await admin.PutAsync("/api/admin/settings/Brand.LogoFileId", new { value = new string('a', 32) })).Body));   // must be a real logo file
        var (_, ad) = await admin.UploadAsync("/api/admin/ads/images", TestMedia.SmallJpeg(), "ad.jpg");
        Assert.Equal("SETTING_INVALID", TestClient.Error((await admin.PutAsync("/api/admin/settings/Brand.LogoFileId", new { value = ad.GetProperty("id").GetString() })).Body));   // an ad picture is not a logo
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/admin/settings/Brand.LogoFileId", new { value = id })).Status);
        var pub = await Public(iso.Host);
        Assert.Equal($"/api/files/{id}", pub.GetProperty("Brand.LogoUrl").GetString());
        Assert.False(pub.TryGetProperty("Brand.LogoFileId", out _));                                                                                               // only the address is public
        var served = await iso.Host.NewClient().GetBytesAsync($"/api/files/{id}");                                                                                 // a visitor who is not signed in can load it
        Assert.Equal((HttpStatusCode.OK, "image/jpeg"), (served.Status, served.ContentHeaders.ContentType!.MediaType));
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/admin/settings/Brand.LogoFileId", new { value = "" })).Status);                                 // removing it
        Assert.Equal("", (await Public(iso.Host)).GetProperty("Brand.LogoUrl").GetString());
    }

    [Fact]
    public async Task The_address_that_is_told_about_contact_messages_must_look_like_an_address()
    {
        await using var iso = await IsolatedHost.StartAsync();
        var (admin, _, _) = await iso.Host.CreateAdminAsync();
        foreach (var bad in new[] { "not an address", "a@b", "two words@example.test" })
            Assert.Equal("SETTING_INVALID", TestClient.Error((await admin.PutAsync("/api/admin/settings/Contact.NotifyAddress", new { value = bad })).Body));
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/admin/settings/Contact.NotifyAddress", new { value = "staff@example.test" })).Status);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/admin/settings/Contact.NotifyAddress", new { value = "" })).Status);             // empty = nobody is told
    }
}
