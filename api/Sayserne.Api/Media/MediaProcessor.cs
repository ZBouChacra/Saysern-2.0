using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Sayserne.Api.Media;

public sealed class MediaOptions
{
    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";
    /// <summary>A single ffmpeg/ffprobe run is killed after this long.</summary>
    public int ProcessTimeoutSeconds { get; set; } = 600;
    public string? TempPath { get; set; }
}

/// <summary>A refusal that is the file's fault (not a glitch): the upload is marked failed with this code and is not retried.</summary>
public sealed class MediaException(string code) : Exception(code) { public string Code { get; } = code; }

public sealed record MediaLimits(long MaxImageBytes, long MaxVideoBytes, int ImageMaxDimension, int VideoMaxHeight, int VideoMaxSeconds);
public sealed record ProcessedMedia(string Path, string ContentType, string Extension, int Width, int Height, int? DurationMs, long Bytes);
public sealed record ProbeResult(string FormatName, double DurationSeconds, bool HasVideo, bool HasAudio, int Width, int Height);

public interface IMediaProcessor
{
    Task<ProcessedMedia> ProcessImageAsync(string input, string outputDirectory, MediaLimits limits, CancellationToken ct = default);
    Task<ProcessedMedia> ProcessVideoAsync(string input, string outputDirectory, MediaLimits limits, CancellationToken ct = default);
}

/// <summary>
/// Normalises uploads with ffmpeg: every image becomes a metadata-free JPEG no larger than the configured pixels and bytes; every video becomes
/// H.264/AAC MP4 (fast-start) at most the configured height, length and bytes. Uploads are never trusted: the type is checked by magic bytes
/// first, then probed, and ffmpeg is run without a shell, without network protocols, without stdin and under a timeout.
/// </summary>
public sealed class MediaProcessor(IOptions<MediaOptions> options) : IMediaProcessor
{
    private const long MaxPixels = 100_000_000;       // refuses decompression bombs before decoding
    // ffmpeg can also open SVG, PDF, playlists and other formats that may reference local files or the network. Only these plain raster
    // containers are ever decoded, whatever the upload claims to be (the upload endpoint also refuses them by their first bytes).
    private static readonly HashSet<string> RasterImageFormats = new(StringComparer.Ordinal) { "image2", "jpeg_pipe", "png_pipe", "webp_pipe", "gif" };
    private MediaOptions Cfg => options.Value;

    private async Task<(int Exit, string Out, string Err)> RunAsync(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = new Process { StartInfo = psi };
        try { process.Start(); }
        catch (Win32Exception ex) { throw new InvalidOperationException($"'{exe}' could not be started. Is ffmpeg installed on this server?", ex); }
        process.StandardInput.Close();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(Cfg.ProcessTimeoutSeconds, 1)));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ }
            throw new MediaException("MEDIA_TIMEOUT");
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ }
            throw;
        }
    }

    public async Task<ProbeResult> ProbeAsync(string path, CancellationToken ct)
    {
        var (exit, output, _) = await RunAsync(Cfg.FfprobePath, ["-v", "error", "-hide_banner", "-protocol_whitelist", "file", "-print_format", "json", "-show_format", "-show_streams", path], ct);
        if (exit != 0) throw new MediaException("UNSUPPORTED_FILE");
        try
        {
            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;
            var format = root.TryGetProperty("format", out var f) ? f : default;
            var streams = root.TryGetProperty("streams", out var s) ? s.EnumerateArray().ToList() : [];
            var video = streams.FirstOrDefault(x => x.TryGetProperty("codec_type", out var t) && t.GetString() == "video");
            var hasVideo = video.ValueKind == JsonValueKind.Object;
            var width = hasVideo && video.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
            var height = hasVideo && video.TryGetProperty("height", out var hgt) ? hgt.GetInt32() : 0;
            double duration = 0;
            if (format.ValueKind == JsonValueKind.Object && format.TryGetProperty("duration", out var d)) double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
            return new ProbeResult(format.ValueKind == JsonValueKind.Object && format.TryGetProperty("format_name", out var fn) ? fn.GetString() ?? "" : "", duration, hasVideo,
                streams.Any(x => x.TryGetProperty("codec_type", out var t) && t.GetString() == "audio"), width, height);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException) { throw new MediaException("UNSUPPORTED_FILE"); }
    }

    private async Task Ffmpeg(IEnumerable<string> args, CancellationToken ct)
    {
        var all = new List<string> { "-nostdin", "-v", "error", "-y", "-protocol_whitelist", "file" };
        all.AddRange(args);
        var (exit, _, _) = await RunAsync(Cfg.FfmpegPath, all, ct);
        if (exit != 0) throw new MediaException("UNSUPPORTED_FILE");
    }

    public async Task<ProcessedMedia> ProcessImageAsync(string input, string outputDirectory, MediaLimits limits, CancellationToken ct = default)
    {
        var probe = await ProbeAsync(input, ct);
        if (!probe.HasVideo || probe.Width < 1 || probe.Height < 1 || !probe.FormatName.Split(',').All(RasterImageFormats.Contains)) throw new MediaException("UNSUPPORTED_FILE");
        if ((long)probe.Width * probe.Height > MaxPixels) throw new MediaException("IMAGE_TOO_LARGE");

        var output = Path.Combine(outputDirectory, "image-" + Guid.NewGuid().ToString("N") + ".jpg");
        var dimension = limits.ImageMaxDimension;
        // first make it fit the pixel limit, then walk down the quality ladder (and, if it still does not fit, the size) until it fits the byte limit
        for (var round = 0; round < 4; round++, dimension = (int)(dimension * 0.75))
            foreach (var quality in new[] { 4, 8, 13, 20, 28 })
            {
                await Ffmpeg(["-i", input, "-map_metadata", "-1", "-vf", $"scale=w=min({dimension}\\,iw):h=min({dimension}\\,ih):force_original_aspect_ratio=decrease:flags=lanczos", "-frames:v", "1", "-q:v", quality.ToString(CultureInfo.InvariantCulture), output], ct);
                var bytes = new FileInfo(output).Length;
                if (bytes <= limits.MaxImageBytes)
                {
                    var result = await ProbeAsync(output, ct);
                    return new ProcessedMedia(output, "image/jpeg", "jpg", result.Width, result.Height, null, bytes);
                }
            }
        if (File.Exists(output)) File.Delete(output);
        throw new MediaException("FILE_TOO_LARGE");
    }

    public async Task<ProcessedMedia> ProcessVideoAsync(string input, string outputDirectory, MediaLimits limits, CancellationToken ct = default)
    {
        var probe = await ProbeAsync(input, ct);
        var container = probe.FormatName;
        if (!probe.HasVideo || probe.Width < 1 || probe.Height < 1 || !(container.Contains("mp4") || container.Contains("mov") || container.Contains("matroska") || container.Contains("webm")))
            throw new MediaException("UNSUPPORTED_FILE");
        if ((long)probe.Width * probe.Height > MaxPixels) throw new MediaException("IMAGE_TOO_LARGE");
        if (probe.DurationSeconds < 0.2) throw new MediaException("UNSUPPORTED_FILE");
        if (probe.DurationSeconds > limits.VideoMaxSeconds) throw new MediaException("VIDEO_TOO_LONG");

        // aim a little under the byte limit: (bytes * 8 / seconds) = total bitrate, minus the audio share
        var audioKbps = probe.HasAudio ? 64 : 0;
        var targetKbps = (limits.MaxVideoBytes * 0.9 * 8 / 1000 / Math.Max(probe.DurationSeconds, 1)) - audioKbps;
        targetKbps = Math.Min(targetKbps, 4000);
        var output = Path.Combine(outputDirectory, "video-" + Guid.NewGuid().ToString("N") + ".mp4");
        for (var attempt = 0; attempt < 3; attempt++, targetKbps *= 0.65)
        {
            if (targetKbps < 60) break;
            var k = ((int)targetKbps).ToString(CultureInfo.InvariantCulture);
            await Ffmpeg(["-i", input, "-map", "0:v:0", "-map", "0:a:0?", "-map_metadata", "-1", "-sn", "-dn",
                "-vf", $"scale=-2:trunc(min({limits.VideoMaxHeight}\\,ih)/2)*2", "-c:v", "libx264", "-preset", "veryfast", "-crf", "26", "-maxrate", k + "k", "-bufsize", ((int)(targetKbps * 2)).ToString(CultureInfo.InvariantCulture) + "k",
                "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "64k", "-ac", "2", "-movflags", "+faststart", output], ct);
            var bytes = new FileInfo(output).Length;
            if (bytes <= limits.MaxVideoBytes)
            {
                var result = await ProbeAsync(output, ct);
                return new ProcessedMedia(output, "video/mp4", "mp4", result.Width, result.Height, (int)(result.DurationSeconds * 1000), bytes);
            }
        }
        if (File.Exists(output)) File.Delete(output);
        throw new MediaException("FILE_TOO_LARGE");
    }
}
