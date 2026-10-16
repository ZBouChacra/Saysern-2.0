using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Sayserne.Api.Jobs;

namespace Sayserne.Tests;

public class FileApiTests
{
    private static Task<(HttpStatusCode Status, System.Text.Json.JsonElement Body)> Upload(TestClient c, byte[] bytes, string purpose = "profile_photo", long? roomId = null, string name = "photo.jpg") =>
        c.UploadAsync($"/api/files?purpose={purpose}" + (roomId is null ? "" : $"&roomId={roomId}"), bytes, name);

    private static async Task<long> RoomAsync(TestClient a, long otherId) => (await a.PostAsync("/api/chat/rooms", new { userId = otherId })).Body.GetProperty("id").GetInt64();
    private static string[] StoredFiles(ApiHost h) => Directory.Exists(h.StoragePath) ? Directory.GetFiles(h.StoragePath, "*", SearchOption.AllDirectories) : [];
    private static async Task Set(TestClient admin, string key, string value) => Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync($"/api/admin/settings/{key}", new { value })).Status);
    private static bool IsJpeg(byte[] b) => b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;

    /// <summary>Runs every due background job (compression runs here, in the test, exactly as the server's runner would).</summary>
    private static async Task<int> RunJobs(ApiHost host)
    {
        var runner = host.App.Services.GetRequiredService<JobRunner>();
        var total = 0; int n;
        while ((n = await runner.RunOnceAsync()) > 0) total += n;
        return total;
    }

    [Fact]
    public async Task A_photo_is_shrunk_stored_and_served_publicly_with_safe_headers_and_without_leaving_the_raw_upload()
    {
        await using var host = await ApiHost.StartAsync();
        var (c, _, _) = await host.RegisterAsync();
        var original = TestMedia.BigJpeg();
        var (status, body) = await Upload(c, original);
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal("ready", body.GetProperty("status").GetString());
        Assert.Equal("image", body.GetProperty("kind").GetString());
        Assert.True(body.GetProperty("width").GetInt32() <= 1600 && body.GetProperty("height").GetInt32() <= 1600);
        Assert.True(body.GetProperty("bytes").GetInt64() < original.Length / 5);
        var url = body.GetProperty("url").GetString()!;
        Assert.Matches("^/api/files/[0-9a-f]{32}$", url);

        var (gs, bytes, headers, content) = await host.NewClient().GetBytesAsync(url);   // anonymous: profile photos are public
        Assert.Equal(HttpStatusCode.OK, gs);
        Assert.True(IsJpeg(bytes));
        Assert.Equal("image/jpeg", content.ContentType!.MediaType);
        Assert.Equal("nosniff", headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("sandbox", headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("public", headers.CacheControl!.ToString());
        Assert.NotNull(headers.ETag);

        var files = StoredFiles(host);
        Assert.Single(files);                                            // only the processed file...
        Assert.DoesNotContain("incoming", files[0]);                     // ...the raw upload is gone
        Assert.Equal(bytes.Length, new FileInfo(files[0]).Length);
    }

    [Fact]
    public async Task Png_and_gif_become_jpeg_and_small_images_are_not_enlarged()
    {
        await using var host = await ApiHost.StartAsync();
        var (c, _, _) = await host.RegisterAsync();
        foreach (var (bytes, name, w, h) in new[] { (TestMedia.Png(), "a.png", 800, 600), (TestMedia.Gif(), "a.gif", 200, 100), (TestMedia.SmallJpeg(), "a.jpg", 300, 200) })
        {
            var (status, body) = await Upload(c, bytes, name: name);
            Assert.Equal(HttpStatusCode.Created, status);
            Assert.Equal((w, h), (body.GetProperty("width").GetInt32(), body.GetProperty("height").GetInt32()));
            Assert.True(IsJpeg((await host.NewClient().GetBytesAsync(body.GetProperty("url").GetString()!)).Bytes));
        }
    }

    [Fact]
    public async Task Anything_that_is_not_a_supported_image_or_video_is_refused_whatever_its_name_and_nothing_is_stored()
    {
        await using var host = await ApiHost.StartAsync();
        var (c, _, _) = await host.RegisterAsync();
        foreach (var (name, bytes) in new (string, byte[])[]
        {
            ("evil.jpg", Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>")),
            ("page.png", Encoding.UTF8.GetBytes("<!doctype html><script>alert(document.cookie)</script>")),
            ("doc.jpg", Encoding.ASCII.GetBytes("%PDF-1.7 fake pdf")), ("archive.jpg", [0x50, 0x4B, 0x03, 0x04, 1, 2, 3, 4, 5, 6, 7, 8]),
            ("run.jpg", [0x4D, 0x5A, 0x90, 0, 3, 0, 0, 0, 4, 0, 0, 0]), ("note.jpg", Encoding.UTF8.GetBytes("plain text pretending to be a photo")),
        })
        {
            var (status, body) = await Upload(c, bytes, name: name);
            Assert.True(status == HttpStatusCode.BadRequest && TestClient.Error(body) == "UNSUPPORTED_FILE", $"{name}: {status} {body}");
        }
        Assert.Empty(StoredFiles(host));
    }

    [Fact]
    public async Task A_file_with_a_valid_header_but_garbage_inside_is_refused_and_cleaned_up()
    {
        await using var host = await ApiHost.StartAsync();
        var (c, _, _) = await host.RegisterAsync();
        var (status, body) = await Upload(c, [0xFF, 0xD8, 0xFF, 0xE0, .. System.Security.Cryptography.RandomNumberGenerator.GetBytes(4000)]);
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("UNSUPPORTED_FILE", TestClient.Error(body));
        Assert.Empty(StoredFiles(host));                 // the raw upload was removed again
        var (s2, b2) = await Upload(c, TestMedia.Png(12000, 12000), name: "bomb.png");   // a 138-byte file claiming 144 megapixels
        Assert.Equal((HttpStatusCode.BadRequest, "IMAGE_TOO_LARGE"), (s2, TestClient.Error(b2)));
        Assert.Empty(StoredFiles(host));
    }

    [Fact]
    public async Task Uploads_are_validated_before_any_work_is_done()
    {
        await using var host = await ApiHost.StartAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Upload(host.NewClient(), TestMedia.SmallJpeg())).Status);
        var (c, _, _) = await host.RegisterAsync();
        var (_, _, otherId) = await host.RegisterAsync();
        var (_, strangerRoomOwner, _) = await host.RegisterAsync();
        Assert.Equal("VALIDATION_FAILED", TestClient.Error((await Upload(c, TestMedia.SmallJpeg(), purpose: "ad_image")).Body));         // ads are uploaded by staff, not here
        Assert.Equal("VALIDATION_FAILED", TestClient.Error((await Upload(c, TestMedia.SmallJpeg(), purpose: "whatever")).Body));
        Assert.Equal("VALIDATION_FAILED", TestClient.Error((await c.c_PostJson("/api/files?purpose=profile_photo")).Body));              // not multipart
        Assert.Equal("VALIDATION_FAILED", TestClient.Error((await Upload(c, [], name: "empty.jpg")).Body));
        Assert.Equal("VALIDATION_FAILED", TestClient.Error((await Upload(c, TestMedia.SmallJpeg(), purpose: "chat_media")).Body));      // a conversation is required
        Assert.Equal(HttpStatusCode.NotFound, (await Upload(c, TestMedia.SmallJpeg(), purpose: "chat_media", roomId: 999999)).Status);  // not a room I am in
        var (_, _, thirdId) = await host.RegisterAsync();
        var (a, _, _) = await host.RegisterAsync(); var (_, _, bId) = await host.RegisterAsync();
        var room = await RoomAsync(a, bId);
        Assert.Equal(HttpStatusCode.NotFound, (await Upload(c, TestMedia.SmallJpeg(), purpose: "chat_media", roomId: room)).Status);    // someone else's room
        var (vs, vb) = await Upload(c, TestMedia.Mp4(3, 320, 180), purpose: "profile_photo", name: "clip.mp4");                          // a video as a profile photo
        Assert.Equal((HttpStatusCode.BadRequest, "UNSUPPORTED_FILE"), (vs, TestClient.Error(vb)));
        _ = otherId; _ = strangerRoomOwner; _ = thirdId;
    }

    [Fact]
    public async Task Oversized_uploads_are_refused_while_streaming_without_being_stored()
    {
        await using var iso = await IsolatedHost.StartAsync();
        var (admin, _, _) = await iso.Host.CreateAdminAsync();
        await Set(admin, "Media.MaxUploadMB", "5");
        var (c, _, _) = await iso.Host.RegisterAsync();
        var huge = new byte[6 * 1024 * 1024]; huge[0] = 0xFF; huge[1] = 0xD8; huge[2] = 0xFF;
        var (status, body) = await Upload(c, huge);
        Assert.Equal((HttpStatusCode.RequestEntityTooLarge, "FILE_TOO_LARGE"), (status, TestClient.Error(body)));
        Assert.Empty(StoredFiles(iso.Host));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Upload(c, TestMedia.BigJpeg())).Status);   // the 9 MB photo is over the 5 MB UPLOAD cap, even though it would compress well
        Assert.Equal(HttpStatusCode.Created, (await Upload(c, TestMedia.Png())).Status);                       // a normal file is unaffected
    }

    [Fact]
    public async Task The_image_limits_are_settings_an_admin_can_change()
    {
        await using var iso = await IsolatedHost.StartAsync();
        var (admin, _, _) = await iso.Host.CreateAdminAsync();
        await Set(admin, "Media.MaxImageMB", "1"); await Set(admin, "Media.ImageMaxDimension", "320");
        var (c, _, _) = await iso.Host.RegisterAsync();
        var (status, body) = await Upload(c, TestMedia.BigJpeg());
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.True(body.GetProperty("width").GetInt32() <= 320 && body.GetProperty("height").GetInt32() <= 320);
        Assert.True(body.GetProperty("bytes").GetInt64() <= 1 << 20);
        await Set(admin, "Media.ImageMaxDimension", "1000");
        Assert.True((await Upload(c, TestMedia.BigJpeg())).Body.GetProperty("width").GetInt32() is > 320 and <= 1000);
    }

    [Fact]
    public async Task Chat_media_is_visible_only_to_the_two_people_in_the_conversation()
    {
        await using var host = await ApiHost.StartAsync();
        var (a, _, aId) = await host.RegisterAsync(); var (b, _, bId) = await host.RegisterAsync(); var (stranger, _, _) = await host.RegisterAsync();
        var room = await RoomAsync(a, bId);
        var (status, body) = await Upload(a, TestMedia.SmallJpeg(), purpose: "chat_media", roomId: room);
        Assert.Equal(HttpStatusCode.Created, status);
        var url = body.GetProperty("url").GetString()!; var id = body.GetProperty("id").GetString()!;

        Assert.Equal(HttpStatusCode.OK, (await a.GetBytesAsync(url)).Status);
        Assert.Equal(HttpStatusCode.OK, (await b.GetBytesAsync(url)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetBytesAsync(url)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await host.NewClient().GetBytesAsync(url)).Status);   // not found, not forbidden: ids cannot be probed
        var (_, _, h, _) = await b.GetBytesAsync(url);
        Assert.Contains("private", h.CacheControl!.ToString());                                       // never cached by shared proxies

        Assert.Equal("ready", (await a.GetAsync($"/api/files/{id}/status")).Body.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/files/{id}/status")).Status);      // status is for the uploader only
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.NewClient().GetAsync($"/api/files/{id}/status")).Status);
        _ = aId;
    }

    [Fact]
    public async Task An_image_message_references_the_senders_own_finished_file_for_that_conversation_only()
    {
        await using var host = await ApiHost.StartAsync();
        var (a, _, _) = await host.RegisterAsync(); var (b, _, bId) = await host.RegisterAsync(); var (_, _, cId) = await host.RegisterAsync();
        var room = await RoomAsync(a, bId); var otherRoom = await RoomAsync(a, cId);
        var mine = (await Upload(a, TestMedia.SmallJpeg(), purpose: "chat_media", roomId: room)).Body.GetProperty("id").GetString()!;
        var forOtherRoom = (await Upload(a, TestMedia.SmallJpeg(), purpose: "chat_media", roomId: otherRoom)).Body.GetProperty("id").GetString()!;
        var bsFile = (await Upload(b, TestMedia.SmallJpeg(), purpose: "chat_media", roomId: room)).Body.GetProperty("id").GetString()!;
        var aPhoto = (await Upload(a, TestMedia.SmallJpeg(), purpose: "profile_photo")).Body.GetProperty("id").GetString()!;

        var (s, sent) = await a.PostAsync($"/api/chat/rooms/{room}/messages", new { type = "image", fileId = mine, content = "the invoice" });
        Assert.Equal(HttpStatusCode.Created, s);
        Assert.Equal((mine, "image", "the invoice"), (sent.GetProperty("fileId").GetString(), sent.GetProperty("messageType").GetString(), sent.GetProperty("content").GetString()));
        var listed = (await b.GetAsync($"/api/chat/rooms/{room}/messages")).Body[0];
        Assert.Equal(mine, listed.GetProperty("fileId").GetString());
        Assert.Equal(HttpStatusCode.OK, (await b.GetBytesAsync($"/api/files/{mine}")).Status);

        foreach (var (name, payload) in new (string, object)[]
        {
            ("file of another conversation", new { type = "image", fileId = forOtherRoom }), ("someone else's file", new { type = "image", fileId = bsFile }),
            ("a profile photo", new { type = "image", fileId = aPhoto }), ("an unknown file", new { type = "image", fileId = new string('0', 32) }),
            ("an image sent as a video", new { type = "video", fileId = mine }),
        })
        {
            var (st, bd) = await a.PostAsync($"/api/chat/rooms/{room}/messages", payload);
            Assert.True(st == HttpStatusCode.NotFound && TestClient.Error(bd) == "FILE_NOT_FOUND", $"{name}: {st} {bd}");
        }
        Assert.Equal("VALIDATION_FAILED", TestClient.Error((await a.PostAsync($"/api/chat/rooms/{room}/messages", new { type = "image" })).Body));
        Assert.Equal("VALIDATION_FAILED", TestClient.Error((await a.PostAsync($"/api/chat/rooms/{room}/messages", new { type = "image", fileId = "not-hex" })).Body));
    }

    [Fact]
    public async Task A_profile_photo_can_be_one_of_your_own_uploads_but_never_someone_elses_or_the_wrong_kind()
    {
        await using var host = await ApiHost.StartAsync();
        var (me, _, _) = await host.RegisterAsync(); var (other, _, _) = await host.RegisterAsync();
        var photo = (await Upload(me, TestMedia.SmallJpeg(), purpose: "profile_photo")).Body.GetProperty("url").GetString()!;
        var banner = (await Upload(me, TestMedia.SmallJpeg(), purpose: "banner_photo")).Body.GetProperty("url").GetString()!;
        var othersPhoto = (await Upload(other, TestMedia.SmallJpeg(), purpose: "profile_photo")).Body.GetProperty("url").GetString()!;
        Task<(HttpStatusCode Status, System.Text.Json.JsonElement Body)> Save(string? profile, string? bannerUrl = null) =>
            me.PutAsync("/api/me/profile", new { firstName = "A", lastName = "B", profileType = "customer", profilePhoto = profile, bannerPhoto = bannerUrl });

        var (ok, saved) = await Save(photo, banner);
        Assert.Equal(HttpStatusCode.OK, ok);
        Assert.Equal((photo, banner), (saved.GetProperty("profilePhoto").GetString(), saved.GetProperty("bannerPhoto").GetString()));
        Assert.Equal(HttpStatusCode.OK, (await Save("https://example.com/me.jpg")).Status);          // external links keep working
        foreach (var (name, value) in new[] { ("someone else's file", othersPhoto), ("a banner used as a photo", banner), ("an unknown file", "/api/files/" + new string('a', 32)) })
        {
            var (st, bd) = await Save(value);
            Assert.True(st == HttpStatusCode.NotFound && TestClient.Error(bd) == "FILE_NOT_FOUND", $"{name}: {st} {bd}");
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await Save("/api/files/not-a-real-id")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Save("javascript:alert(1)")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Save(photo, photo)).Status);                       // a profile photo is not a banner
    }

    [Fact]
    public async Task A_video_is_accepted_at_once_compressed_in_the_background_and_streamable_when_ready()
    {
        await using var iso = await IsolatedHost.StartAsync();
        var host = iso.Host;
        var (admin, _, _) = await host.CreateAdminAsync();
        await Set(admin, "Media.MaxVideoMB", "1");
        var source = TestMedia.Mp4(seconds: 10, width: 960, height: 540);
        Assert.True(source.Length > 3 << 20, $"the source ({source.Length} bytes) must be well over the 1 MB limit");

        var (a, _, _) = await host.RegisterAsync(); var (b, _, bId) = await host.RegisterAsync();
        var room = await RoomAsync(a, bId);
        var (status, body) = await Upload(a, source, purpose: "chat_media", roomId: room, name: "clip.mp4");
        Assert.Equal(HttpStatusCode.Accepted, status);
        var id = body.GetProperty("id").GetString()!; var url = body.GetProperty("url").GetString()!;
        Assert.Equal(("processing", "video"), (body.GetProperty("status").GetString(), body.GetProperty("kind").GetString()));
        Assert.Equal(HttpStatusCode.NotFound, (await a.GetBytesAsync(url)).Status);                                                         // nothing to serve yet
        var (ns, nb) = await a.PostAsync($"/api/chat/rooms/{room}/messages", new { type = "video", fileId = id });
        Assert.Equal((HttpStatusCode.Conflict, "FILE_NOT_READY"), (ns, TestClient.Error(nb)));

        Assert.Equal(1, await RunJobs(host));
        var st = (await a.GetAsync($"/api/files/{id}/status")).Body;
        Assert.Equal("ready", st.GetProperty("status").GetString());
        var (gs, bytes, headers, content) = await b.GetBytesAsync(url);
        Assert.Equal(HttpStatusCode.OK, gs);
        Assert.True(bytes.Length <= 1 << 20, $"{bytes.Length} bytes");                   // 3+ MB source compressed under the 1 MB limit
        Assert.Equal("video/mp4", content.ContentType!.MediaType);
        Assert.Equal("ftyp", Encoding.ASCII.GetString(bytes[4..8]));
        Assert.DoesNotContain("SECRET-TITLE", Encoding.Latin1.GetString(bytes));
        Assert.Equal("bytes", headers.AcceptRanges.Single());
        var (rs, part, _, rc) = await b.GetBytesAsync(url, range: "bytes=0-99");           // players seek with Range requests
        Assert.Equal(HttpStatusCode.PartialContent, rs);
        Assert.Equal(100, part.Length);
        Assert.Equal(bytes.Length, rc.ContentRange!.Length);

        var (ms, msg) = await a.PostAsync($"/api/chat/rooms/{room}/messages", new { type = "video", fileId = id });
        Assert.Equal(HttpStatusCode.Created, ms);
        Assert.Equal("video", msg.GetProperty("messageType").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await host.NewClient().GetBytesAsync(url)).Status);
        Assert.Empty(StoredFiles(host).Where(f => f.Contains("incoming")));                  // the raw upload was removed
    }

    [Fact]
    public async Task A_video_that_cannot_be_accepted_fails_with_a_reason_the_uploader_can_read_and_is_not_retried()
    {
        await using var iso = await IsolatedHost.StartAsync();
        var host = iso.Host;
        var (admin, _, _) = await host.CreateAdminAsync();
        await Set(admin, "Media.VideoMaxSeconds", "5");
        var (a, _, _) = await host.RegisterAsync(); var (_, _, bId) = await host.RegisterAsync();
        var room = await RoomAsync(a, bId);
        var (_, body) = await Upload(a, TestMedia.Mp4(seconds: 8, width: 320, height: 180), purpose: "chat_media", roomId: room, name: "long.mp4");
        var id = body.GetProperty("id").GetString()!;
        await RunJobs(host);
        var st = (await a.GetAsync($"/api/files/{id}/status")).Body;
        Assert.Equal(("failed", "VIDEO_TOO_LONG"), (st.GetProperty("status").GetString(), st.GetProperty("failureCode").GetString()));
        Assert.Empty(StoredFiles(host));                                                     // nothing is kept for a refused video
        var (_, jobs) = await (await host.CreateAdminAsync()).Client.GetAsync("/api/admin/jobs?status=failed");
        var job = jobs.GetProperty("items").EnumerateArray().Single(j => j.GetProperty("type").GetString() == "media.process");
        Assert.Equal((1, "VIDEO_TOO_LONG"), (job.GetProperty("attempts").GetInt32(), job.GetProperty("lastError").GetString()));   // permanent: tried once
        var (ms, mb) = await a.PostAsync($"/api/chat/rooms/{room}/messages", new { type = "video", fileId = id });
        Assert.Equal((HttpStatusCode.Conflict, "FILE_NOT_READY"), (ms, TestClient.Error(mb)));
    }

    [Fact]
    public async Task Video_can_be_switched_off_to_save_space()
    {
        await using var iso = await IsolatedHost.StartAsync();
        var (admin, _, _) = await iso.Host.CreateAdminAsync();
        var (a, _, _) = await iso.Host.RegisterAsync(); var (_, _, bId) = await iso.Host.RegisterAsync();
        var room = await RoomAsync(a, bId);
        Assert.True((await iso.Host.NewClient().GetAsync("/api/config/public")).Body.GetProperty("Media.VideoEnabled").GetBoolean());
        await Set(admin, "Media.VideoEnabled", "false");
        Assert.False((await iso.Host.NewClient().GetAsync("/api/config/public")).Body.GetProperty("Media.VideoEnabled").GetBoolean());   // the web app hides the video button
        var (status, body) = await Upload(a, TestMedia.Mp4(3, 320, 180), purpose: "chat_media", roomId: room, name: "clip.mp4");
        Assert.Equal((HttpStatusCode.BadRequest, "VIDEO_DISABLED"), (status, TestClient.Error(body)));
        Assert.Equal(HttpStatusCode.Created, (await Upload(a, TestMedia.SmallJpeg(), purpose: "chat_media", roomId: room)).Status);   // images still work
        Assert.Empty(StoredFiles(iso.Host).Where(f => f.Contains("incoming")));
    }

    [Fact]
    public async Task New_uploads_follow_the_storage_setting_while_old_files_stay_where_they_are()
    {
        await using var fake = await FakeS3.StartAsync();
        await using var iso = await IsolatedHost.StartAsync(overrides: fake.Config());
        var host = iso.Host;
        var (admin, _, _) = await host.CreateAdminAsync();
        var (c, _, _) = await host.RegisterAsync();

        var onDisk = (await Upload(c, TestMedia.SmallJpeg())).Body.GetProperty("url").GetString()!;
        Assert.Empty(fake.Objects);

        await Set(admin, "Storage.Provider", "s3");
        var s3Upload = await Upload(c, TestMedia.SmallJpeg());
        var onS3 = s3Upload.Body.GetProperty("url").GetString()!;
        Assert.Equal(HttpStatusCode.Created, s3Upload.Status);
        Assert.Single(fake.Objects);                                                      // only the processed file; the raw upload was removed from S3 too
        Assert.StartsWith("files/", fake.Objects.Keys.Single());
        Assert.Equal(1, StoredFiles(host).Length);                                        // the earlier disk file, nothing new
        Assert.True(IsJpeg((await host.NewClient().GetBytesAsync(onS3)).Bytes));          // served from S3

        await Set(admin, "Storage.Provider", "disk");                                      // the owner decides to go back (or to a bigger server) later
        Assert.Equal(HttpStatusCode.Created, (await Upload(c, TestMedia.SmallJpeg())).Status);
        Assert.Equal(2, StoredFiles(host).Length);
        Assert.True(IsJpeg((await host.NewClient().GetBytesAsync(onS3)).Bytes));          // the S3 file is still served
        Assert.True(IsJpeg((await host.NewClient().GetBytesAsync(onDisk)).Bytes));
        Assert.Equal(0, fake.Rejected);                                                    // every request was correctly signed

        fake.FailEverything = true;                                                       // an object-store outage is a clean 404, never a 500
        Assert.Equal(HttpStatusCode.NotFound, (await host.NewClient().GetBytesAsync(onS3)).Status);
        Assert.Equal(HttpStatusCode.OK, (await host.NewClient().GetBytesAsync(onDisk)).Status);
        await Set(admin, "Storage.Provider", "s3");
        var (status, body) = await Upload(c, TestMedia.SmallJpeg());
        Assert.True(status is HttpStatusCode.InternalServerError or HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway || (int)status >= 500, $"{status} {body}");
    }

    [Fact]
    public async Task Choosing_s3_without_configuring_it_gives_a_clear_error_and_unknown_providers_cannot_be_set()
    {
        await using var iso = await IsolatedHost.StartAsync();
        var (admin, _, _) = await iso.Host.CreateAdminAsync();
        var (c, _, _) = await iso.Host.RegisterAsync();
        var (bad, badBody) = await admin.PutAsync("/api/admin/settings/Storage.Provider", new { value = "ftp" });
        Assert.Equal((HttpStatusCode.BadRequest, "INVALID_STORAGE_PROVIDER"), (bad, TestClient.Error(badBody)));
        await Set(admin, "Storage.Provider", "s3");                                         // allowed (it is a real provider) but not configured on this server
        var (status, body) = await Upload(c, TestMedia.SmallJpeg());
        Assert.Equal((HttpStatusCode.ServiceUnavailable, "STORAGE_UNAVAILABLE"), (status, TestClient.Error(body)));
        Assert.Empty(StoredFiles(iso.Host));
    }

    [Fact]
    public async Task Malformed_or_unknown_file_ids_are_a_plain_404()
    {
        await using var host = await ApiHost.StartAsync();
        var c = host.NewClient();
        foreach (var id in new[] { "zzz", new string('0', 32), new string('G', 32), "../../etc/passwd", new string('a', 33) })
            Assert.Equal(HttpStatusCode.NotFound, (await c.GetBytesAsync("/api/files/" + id)).Status);
        Assert.True((await c.GetBytesAsync("/api/files/%00")).Status is HttpStatusCode.NotFound or HttpStatusCode.BadRequest);   // a null byte is refused by the server itself
    }
}
