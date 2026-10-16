using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sayserne.Api;
using Sayserne.Api.Media;

namespace Sayserne.Tests;

/// <summary>Real media made with the real ffmpeg (the same tool the server uses), cached so each file is generated once per test run.</summary>
internal static class TestMedia
{
    private static readonly string Dir = Path.Combine(Path.GetTempPath(), "sayserne-testmedia-" + Environment.ProcessId);
    private static readonly ConcurrentDictionary<string, byte[]> Cache = new();

    public static byte[] Make(string name, params string[] args) => Cache.GetOrAdd(name, _ =>
    {
        Directory.CreateDirectory(Dir);
        var path = Path.Combine(Dir, name);
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
        foreach (var a in new[] { "-nostdin", "-v", "error", "-y" }.Concat(args).Concat([path])) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"ffmpeg could not make {name}: {err}");
        return File.ReadAllBytes(path);
    });

    public static byte[] BigJpeg() => Make("big.jpg", "-f", "lavfi", "-i", "testsrc2=size=4000x3000:rate=1,noise=alls=40:allf=t", "-frames:v", "1", "-q:v", "2");
    public static byte[] SmallJpeg() => Make("small.jpg", "-f", "lavfi", "-i", "testsrc2=size=300x200:rate=1", "-frames:v", "1");
    public static byte[] Png() => Make("pic.png", "-f", "lavfi", "-i", "testsrc2=size=800x600:rate=1", "-frames:v", "1");
    public static byte[] Gif() => Make("pic.gif", "-f", "lavfi", "-i", "testsrc2=size=200x100:rate=5", "-t", "1");
    /// <summary>A noisy clip (does not compress well), with a metadata title that must not survive processing.</summary>
    public static byte[] Mp4(int seconds = 5, int width = 640, int height = 360, bool audio = true) =>
        audio ? Make($"clip-{seconds}-{width}x{height}-a.mp4", "-f", "lavfi", "-i", $"testsrc2=size={width}x{height}:rate=25,noise=alls=25:allf=t", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100",
                  "-t", seconds.ToString(), "-c:v", "libx264", "-crf", "16", "-pix_fmt", "yuv420p", "-c:a", "aac", "-metadata", "title=SECRET-TITLE")
              : Make($"clip-{seconds}-{width}x{height}.mp4", "-f", "lavfi", "-i", $"testsrc2=size={width}x{height}:rate=25,noise=alls=25:allf=t", "-t", seconds.ToString(),
                  "-c:v", "libx264", "-crf", "16", "-pix_fmt", "yuv420p", "-metadata", "title=SECRET-TITLE");
    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data) { crc ^= b; for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1; }
        return ~crc;
    }

    /// <summary>A 138-byte PNG that CLAIMS to be width x height pixels (a decompression-bomb probe): header only, no real pixel data.</summary>
    public static byte[] Png(int width, int height)
    {
        static byte[] Chunk(string type, byte[] data)
        {
            var t = Encoding.ASCII.GetBytes(type);
            var all = t.Concat(data).ToArray();
            return [.. BitConverter.GetBytes(data.Length).Reverse(), .. all, .. BitConverter.GetBytes(Crc32(all)).Reverse()];
        }
        byte[] oneScanline;
        using (var compressed = new MemoryStream())
        {
            using (var z = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true)) z.Write(new byte[1 + width * 3]);
            oneScanline = compressed.ToArray();
        }
        return [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. Chunk("IHDR", [.. BitConverter.GetBytes(width).Reverse(), .. BitConverter.GetBytes(height).Reverse(), 8, 2, 0, 0, 0]), .. Chunk("IDAT", oneScanline), .. Chunk("IEND", [])];
    }
}

/// <summary>An in-process S3-compatible server that independently checks every request's signature and payload hash.</summary>
public sealed class FakeS3 : IAsyncDisposable
{
    public const string Access = "AKIATESTTESTTEST", Secret = "test-secret-key/with+chars", Bucket = "sayserne-test", Region = "eu-test-1";
    private WebApplication _app = null!;
    public ConcurrentDictionary<string, byte[]> Objects { get; } = new();
    public int Rejected;
    public bool FailEverything;
    public string BaseUrl { get; private set; } = "";

    public static async Task<FakeS3> StartAsync()
    {
        var s3 = new FakeS3();
        var b = WebApplication.CreateBuilder();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        b.Logging.ClearProviders();
        s3._app = b.Build();
        s3._app.Run(s3.HandleAsync);
        await s3._app.StartAsync();
        s3.BaseUrl = s3._app.Urls.First();
        return s3;
    }

    private async Task HandleAsync(HttpContext ctx)
    {
        if (FailEverything) { ctx.Response.StatusCode = 500; return; }
        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms);
        var body = ms.ToArray();
        var auth = ctx.Request.Headers.Authorization.ToString();
        var m = Regex.Match(auth, @"^AWS4-HMAC-SHA256 Credential=([^/]+)/(\d{8})/([^/]+)/s3/aws4_request, SignedHeaders=([^,]+), Signature=([0-9a-f]{64})$");
        var amzDate = ctx.Request.Headers["x-amz-date"].ToString();
        var claimedHash = ctx.Request.Headers["x-amz-content-sha256"].ToString();
        var valid = m.Success && m.Groups[1].Value == Access && m.Groups[3].Value == Region && claimedHash == SigV4.Sha256Hex(body)
                    && Regex.IsMatch(amzDate, @"^\d{8}T\d{6}Z$") && amzDate[..8] == m.Groups[2].Value && m.Groups[4].Value == "host;x-amz-content-sha256;x-amz-date";
        if (valid)
        {
            var headers = new Dictionary<string, string> { ["host"] = ctx.Request.Host.Value!, ["x-amz-content-sha256"] = claimedHash, ["x-amz-date"] = amzDate };
            var (_, expected) = SigV4.Authorize(ctx.Request.Method, SigV4.EncodePath(ctx.Request.Path.Value!), "", headers, claimedHash, Access, Secret, Region, "s3", amzDate);
            valid = expected == m.Groups[5].Value;
        }
        if (!valid) { Interlocked.Increment(ref Rejected); ctx.Response.StatusCode = 403; return; }

        var path = ctx.Request.Path.Value!;
        var prefix = "/" + Bucket + "/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) { ctx.Response.StatusCode = 404; return; }
        var key = path[prefix.Length..];
        switch (ctx.Request.Method)
        {
            case "PUT": Objects[key] = body; ctx.Response.StatusCode = 200; break;
            case "GET":
                if (Objects.TryGetValue(key, out var found)) { ctx.Response.StatusCode = 200; await ctx.Response.Body.WriteAsync(found); }
                else ctx.Response.StatusCode = 404;
                break;
            case "DELETE": Objects.TryRemove(key, out _); ctx.Response.StatusCode = 204; break;
            default: ctx.Response.StatusCode = 405; break;
        }
    }

    public Dictionary<string, string?> Config() => new()
    {
        ["Storage:S3:Endpoint"] = BaseUrl, ["Storage:S3:Bucket"] = Bucket, ["Storage:S3:Region"] = Region, ["Storage:S3:AccessKey"] = Access, ["Storage:S3:SecretKey"] = Secret,
    };

    public async ValueTask DisposeAsync() { await _app.StopAsync(); await _app.DisposeAsync(); }
}

public class MediaSnifferTests
{
    private static byte[] Pad(params byte[] head) => [.. head, .. new byte[MediaSniffer.HeaderBytes]];

    [Fact]
    public void Recognises_the_supported_types_by_their_first_bytes()
    {
        Assert.Equal(new Sniffed(MediaKind.Image, "jpg"), MediaSniffer.Detect(Pad(0xFF, 0xD8, 0xFF, 0xE0)));
        Assert.Equal(new Sniffed(MediaKind.Image, "png"), MediaSniffer.Detect(Pad(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)));
        Assert.Equal(new Sniffed(MediaKind.Image, "gif"), MediaSniffer.Detect(Encoding.ASCII.GetBytes("GIF89a......")));
        Assert.Equal(new Sniffed(MediaKind.Image, "gif"), MediaSniffer.Detect(Encoding.ASCII.GetBytes("GIF87a......")));
        Assert.Equal(new Sniffed(MediaKind.Image, "webp"), MediaSniffer.Detect([.. "RIFF"u8.ToArray(), 1, 2, 3, 4, .. "WEBPVP8 "u8.ToArray()]));
        Assert.Equal(new Sniffed(MediaKind.Video, "mp4"), MediaSniffer.Detect([0, 0, 0, 0x20, .. "ftypisom"u8.ToArray()]));
        Assert.Equal(new Sniffed(MediaKind.Video, "webm"), MediaSniffer.Detect(Pad(0x1A, 0x45, 0xDF, 0xA3)));
        Assert.Equal(MediaKind.Image, MediaSniffer.Detect(TestMedia.BigJpeg().AsSpan(0, MediaSniffer.HeaderBytes))!.Kind);
        Assert.Equal(MediaKind.Video, MediaSniffer.Detect(TestMedia.Mp4().AsSpan(0, MediaSniffer.HeaderBytes))!.Kind);
    }

    [Fact]
    public void Refuses_everything_else_whatever_the_file_is_called()
    {
        foreach (var (name, bytes) in new (string, byte[])[]
        {
            ("svg", Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>")),
            ("html", Encoding.UTF8.GetBytes("<!doctype html><script>alert(1)</script>")),
            ("pdf", Encoding.ASCII.GetBytes("%PDF-1.7 ......")), ("zip", [0x50, 0x4B, 0x03, 0x04, 0, 0, 0, 0, 0, 0, 0, 0]),
            ("exe", [0x4D, 0x5A, 0x90, 0, 3, 0, 0, 0, 4, 0, 0, 0]), ("text", Encoding.UTF8.GetBytes("just some text, named photo.jpg")),
            ("empty", []), ("short", [0xFF, 0xD8]), ("riff-but-wav", [.. "RIFF"u8.ToArray(), 1, 2, 3, 4, .. "WAVEfmt "u8.ToArray()]),
        })
            Assert.True(MediaSniffer.Detect(bytes) is null, $"{name} must be refused");
    }
}

public class SigV4Tests
{
    // Amazon's published S3 examples (docs: "Signature Calculations for the Authorization Header: Transferring Payload in a Single Chunk")
    private const string AwsAccess = "AKIAIOSFODNN7EXAMPLE", AwsSecret = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";

    [Fact]
    public void Matches_Amazons_published_signature_for_GET_object_with_a_range()
    {
        var headers = new Dictionary<string, string>
        {
            ["host"] = "examplebucket.s3.amazonaws.com", ["range"] = "bytes=0-9",
            ["x-amz-content-sha256"] = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", ["x-amz-date"] = "20130524T000000Z",
        };
        var (auth, signature) = SigV4.Authorize("GET", "/test.txt", "", headers, headers["x-amz-content-sha256"], AwsAccess, AwsSecret, "us-east-1", "s3", "20130524T000000Z");
        Assert.Equal("f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41", signature);
        Assert.Equal("AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request, SignedHeaders=host;range;x-amz-content-sha256;x-amz-date, Signature=" + signature, auth);
    }

    [Fact]
    public void Matches_Amazons_published_signature_for_PUT_object_with_a_special_character_in_the_key()
    {
        var payloadHash = SigV4.Sha256Hex(Encoding.UTF8.GetBytes("Welcome to Amazon S3."));
        Assert.Equal("44ce7dd67c959e0d3524ffac1771dfbba87d2b6b4b4e99e42034a8b803f8b072", payloadHash);
        var headers = new Dictionary<string, string>
        {
            ["date"] = "Fri, 24 May 2013 00:00:00 GMT", ["host"] = "examplebucket.s3.amazonaws.com", ["x-amz-content-sha256"] = payloadHash,
            ["x-amz-date"] = "20130524T000000Z", ["x-amz-storage-class"] = "REDUCED_REDUNDANCY",
        };
        var (_, signature) = SigV4.Authorize("PUT", SigV4.EncodePath("/test$file.text"), "", headers, payloadHash, AwsAccess, AwsSecret, "us-east-1", "s3", "20130524T000000Z");
        Assert.Equal("98ad721746da40c64f1a55b78f14c238d841ea1380cd77a1b5971af0ece108bd", signature);
    }

    [Fact]
    public void Path_encoding_keeps_slashes_and_encodes_everything_unsafe()
    {
        Assert.Equal("/files/2026/10/abc-1_2.3~x.jpg", SigV4.EncodePath("/files/2026/10/abc-1_2.3~x.jpg"));
        Assert.Equal("/a%20b/%24%26%2B%2C%3A%3B%3D%3F%40", SigV4.EncodePath("/a b/$&+,:;=?@"));
        Assert.Equal("/%C3%A9", SigV4.EncodePath("/é"));
    }
}

public class StorageTests
{
    private static (DiskStorage Disk, string Root) NewDisk()
    {
        var root = Path.Combine(Path.GetTempPath(), "sayserne-disk-" + Guid.NewGuid().ToString("N"));
        return (new DiskStorage(Options.Create(new StorageOptions { DiskPath = root }), new TestHostEnvironment()), root);
    }
    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test"; public string ApplicationName { get; set; } = "t"; public string ContentRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
    private static async Task<byte[]> Read(IFileStorage s, string key) { await using var st = await s.OpenReadAsync(key); using var ms = new MemoryStream(); await st.CopyToAsync(ms); return ms.ToArray(); }

    [Fact]
    public async Task Disk_storage_round_trips_overwrites_and_deletes_without_leaving_temp_files()
    {
        var (disk, root) = NewDisk();
        try
        {
            var key = StorageKeys.Final("a".PadRight(32, 'b'), "jpg", new DateTime(2026, 10, 16));
            Assert.Equal("files/2026/10/" + "a".PadRight(32, 'b') + ".jpg", key);
            await disk.PutAsync(key, new MemoryStream([1, 2, 3]), "image/jpeg");
            Assert.Equal(new byte[] { 1, 2, 3 }, await Read(disk, key));
            await disk.PutAsync(key, new MemoryStream([9, 9]), "image/jpeg");
            Assert.Equal(new byte[] { 9, 9 }, await Read(disk, key));
            Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories));
            await disk.DeleteAsync(key); await disk.DeleteAsync(key);   // deleting twice is fine
            await Assert.ThrowsAsync<StorageException>(() => disk.OpenReadAsync(key));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Keys_that_could_escape_the_storage_folder_are_refused()
    {
        var (disk, root) = NewDisk();
        try
        {
            foreach (var bad in new[] { "../outside.jpg", "files/../../etc/passwd", "/etc/passwd", "C:/Windows/x.jpg", "files//double.jpg", "Files/Upper.jpg", "files/ space.jpg", "a", "", "files/x\0.jpg", "..", new string('a', 400) })
            {
                await Assert.ThrowsAsync<ArgumentException>(() => disk.PutAsync(bad, new MemoryStream([1]), "x"));
                await Assert.ThrowsAsync<ArgumentException>(() => disk.OpenReadAsync(bad));
                await Assert.ThrowsAsync<ArgumentException>(() => disk.DeleteAsync(bad));
            }
            Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), "outside.jpg")));
            Assert.False(Directory.Exists(root));   // nothing was even created
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static S3Storage NewS3(FakeS3 fake, string? secret = null) => new(Options.Create(new StorageOptions { S3 = new S3Options
        { Endpoint = fake.BaseUrl, Bucket = FakeS3.Bucket, Region = FakeS3.Region, AccessKey = FakeS3.Access, SecretKey = secret ?? FakeS3.Secret } }), TimeProvider.System);

    [Fact]
    public async Task S3_storage_signs_every_request_correctly_and_round_trips_bytes()
    {
        await using var fake = await FakeS3.StartAsync();
        using var s3 = NewS3(fake);
        var data = RandomNumberGenerator.GetBytes(300_000);
        await s3.PutAsync("files/2026/10/object.mp4", new MemoryStream(data), "video/mp4");
        Assert.Equal(data, fake.Objects["files/2026/10/object.mp4"]);
        Assert.Equal(data, await Read(s3, "files/2026/10/object.mp4"));
        await s3.DeleteAsync("files/2026/10/object.mp4"); await s3.DeleteAsync("files/2026/10/object.mp4");
        await Assert.ThrowsAsync<StorageException>(() => s3.OpenReadAsync("files/2026/10/object.mp4"));
        Assert.Equal(0, fake.Rejected);
    }

    [Fact]
    public async Task S3_storage_with_the_wrong_secret_or_a_broken_server_fails_cleanly()
    {
        await using var fake = await FakeS3.StartAsync();
        using var wrong = NewS3(fake, secret: "not-the-secret");
        await Assert.ThrowsAsync<StorageException>(() => wrong.PutAsync("files/a1.jpg", new MemoryStream([1]), "image/jpeg"));
        Assert.True(fake.Rejected >= 1);
        using var good = NewS3(fake);
        fake.FailEverything = true;
        await Assert.ThrowsAsync<StorageException>(() => good.PutAsync("files/a1.jpg", new MemoryStream([1]), "image/jpeg"));
        using var unreachable = new S3Storage(Options.Create(new StorageOptions { S3 = new S3Options { Endpoint = "http://127.0.0.1:1", Bucket = "b", AccessKey = "a", SecretKey = "s" } }), TimeProvider.System);
        await Assert.ThrowsAsync<StorageException>(() => unreachable.PutAsync("files/a1.jpg", new MemoryStream([1]), "image/jpeg"));
        using var unconfigured = new S3Storage(Options.Create(new StorageOptions()), TimeProvider.System);
        await Assert.ThrowsAsync<StorageException>(() => unconfigured.PutAsync("files/a1.jpg", new MemoryStream([1]), "image/jpeg"));
    }
}

public class MediaProcessorTests
{
    private static MediaProcessor New(int timeoutSeconds = 120) => new(Options.Create(new MediaOptions { ProcessTimeoutSeconds = timeoutSeconds }));
    private static MediaLimits Limits(long image = 5L << 20, long video = 25L << 20, int dim = 1600, int height = 720, int seconds = 120) => new(image, video, dim, height, seconds);
    private static string Work(byte[] input, string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sayserne-mp-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name), input); return dir;
    }

    [Fact]
    public async Task A_huge_photo_is_shrunk_to_the_pixel_and_byte_limits_as_a_metadata_free_jpeg()
    {
        var dir = Work(TestMedia.BigJpeg(), "in.jpg");
        var r = await New().ProcessImageAsync(Path.Combine(dir, "in.jpg"), dir, Limits(image: 1L << 20, dim: 1200));
        Assert.True(r.Bytes <= 1L << 20, $"{r.Bytes} bytes");
        Assert.Equal("image/jpeg", r.ContentType);
        Assert.True(r.Width <= 1200 && r.Height <= 1200 && r.Width == 1200 && r.Height == 900, $"{r.Width}x{r.Height}");   // aspect ratio kept
        Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF }, File.ReadAllBytes(r.Path)[..3]);
        Assert.True(TestMedia.BigJpeg().Length > 5_000_000);   // the input really was big
    }

    [Fact]
    public async Task A_small_image_is_never_enlarged_and_png_and_gif_become_jpeg()
    {
        var p = New();
        foreach (var (bytes, name, w, h) in new[] { (TestMedia.SmallJpeg(), "s.jpg", 300, 200), (TestMedia.Png(), "s.png", 800, 600), (TestMedia.Gif(), "s.gif", 200, 100) })
        {
            var dir = Work(bytes, name);
            var r = await p.ProcessImageAsync(Path.Combine(dir, name), dir, Limits());
            Assert.Equal((w, h), (r.Width, r.Height));
            Assert.Equal("jpg", r.Extension);
            Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF }, File.ReadAllBytes(r.Path)[..3]);
        }
    }

    [Fact]
    public async Task Files_that_are_not_really_images_or_cannot_be_made_small_enough_are_refused_with_a_reason()
    {
        var p = New();
        async Task<string> Code(byte[] input, string name, MediaLimits limits)
        {
            var dir = Work(input, name);
            return (await Assert.ThrowsAsync<MediaException>(() => p.ProcessImageAsync(Path.Combine(dir, name), dir, limits))).Code;
        }
        Assert.Equal("UNSUPPORTED_FILE", await Code([0xFF, 0xD8, 0xFF, 0xE0, .. RandomNumberGenerator.GetBytes(2000)], "junk.jpg", Limits()));   // JPEG header, garbage body
        Assert.Equal("UNSUPPORTED_FILE", await Code(Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"100\"/>"), "x.svg", Limits()));   // ffmpeg CAN render SVG; the processor must still refuse it
        Assert.Equal("FILE_TOO_LARGE", await Code(TestMedia.BigJpeg(), "tiny-budget.jpg", Limits(image: 800)));                                    // cannot fit 800 bytes at any quality
        Assert.Equal("IMAGE_TOO_LARGE", await Code(TestMedia.Png(12000, 12000), "bomb.png", Limits()));                                              // 144 megapixels claimed by a 60-byte file
        Assert.Equal("UNSUPPORTED_FILE", await Code(TestMedia.Png(20000, 20000), "bomb2.png", Limits()));
    }

    [Fact]
    public async Task A_video_is_compressed_to_the_byte_limit_scaled_down_and_stripped_of_metadata()
    {
        var input = TestMedia.Mp4(seconds: 5, width: 640, height: 360);
        Assert.True(input.Length > 400_000, "the source must be bigger than the limit for this test to mean anything");
        var dir = Work(input, "in.mp4");
        var p = New();
        var r = await p.ProcessVideoAsync(Path.Combine(dir, "in.mp4"), dir, Limits(video: 250_000, height: 240));
        Assert.True(r.Bytes <= 250_000, $"{r.Bytes} bytes");
        Assert.Equal("video/mp4", r.ContentType);
        Assert.Equal(240, r.Height);
        Assert.InRange(r.DurationMs!.Value, 4500, 5500);
        var probe = await p.ProbeAsync(r.Path, default);
        Assert.True(probe.HasVideo && probe.HasAudio);
        Assert.DoesNotContain("SECRET-TITLE", Encoding.Latin1.GetString(File.ReadAllBytes(r.Path)));
        Assert.Equal("ftyp", Encoding.ASCII.GetString(File.ReadAllBytes(r.Path)[4..8]));
    }

    [Fact]
    public async Task A_video_without_audio_works_and_one_already_small_is_not_enlarged()
    {
        var dir = Work(TestMedia.Mp4(seconds: 3, width: 320, height: 180, audio: false), "in.mp4");
        var p = New();
        var r = await p.ProcessVideoAsync(Path.Combine(dir, "in.mp4"), dir, Limits(video: 10L << 20, height: 720));
        Assert.Equal(180, r.Height);                                  // below the limit: not scaled up
        Assert.False((await p.ProbeAsync(r.Path, default)).HasAudio);
    }

    [Fact]
    public async Task Videos_that_are_too_long_unsupported_or_impossible_to_shrink_are_refused()
    {
        var p = New();
        var dir = Work(TestMedia.Mp4(seconds: 8, width: 320, height: 180), "long.mp4");
        Assert.Equal("VIDEO_TOO_LONG", (await Assert.ThrowsAsync<MediaException>(() => p.ProcessVideoAsync(Path.Combine(dir, "long.mp4"), dir, Limits(seconds: 5)))).Code);
        Assert.Equal("FILE_TOO_LARGE", (await Assert.ThrowsAsync<MediaException>(() => p.ProcessVideoAsync(Path.Combine(dir, "long.mp4"), dir, Limits(video: 2_000)))).Code);
        var junk = Work([0, 0, 0, 0x20, .. "ftypisom"u8.ToArray(), .. RandomNumberGenerator.GetBytes(3000)], "junk.mp4");
        Assert.Equal("UNSUPPORTED_FILE", (await Assert.ThrowsAsync<MediaException>(() => p.ProcessVideoAsync(Path.Combine(junk, "junk.mp4"), junk, Limits()))).Code);
        var image = Work(TestMedia.SmallJpeg(), "pic.jpg");   // an image offered as a video
        Assert.Equal("UNSUPPORTED_FILE", (await Assert.ThrowsAsync<MediaException>(() => p.ProcessVideoAsync(Path.Combine(image, "pic.jpg"), image, Limits()))).Code);
    }

    [Fact]
    public async Task A_runaway_process_is_killed_after_the_timeout_and_a_missing_ffmpeg_is_reported_plainly()
    {
        var dir = Work(TestMedia.Mp4(seconds: 5, width: 640, height: 360), "in.mp4");
        var slow = New(timeoutSeconds: 1);
        // the encode of a noisy 5-second clip takes longer than one second on any machine; the probe is quick
        var ex = await Assert.ThrowsAsync<MediaException>(() => slow.ProcessVideoAsync(Path.Combine(dir, "in.mp4"), dir, Limits(video: 200_000, height: 360)));
        Assert.Equal("MEDIA_TIMEOUT", ex.Code);
        var missing = new MediaProcessor(Options.Create(new MediaOptions { FfprobePath = "/nonexistent/ffprobe" }));
        var inv = await Assert.ThrowsAsync<InvalidOperationException>(() => missing.ProcessImageAsync(Path.Combine(dir, "in.mp4"), dir, Limits()));
        Assert.Contains("ffmpeg", inv.Message);
    }
}
