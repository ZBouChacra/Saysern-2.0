namespace Sayserne.Api.Media;

public enum MediaKind { Image, Video }
public sealed record Sniffed(MediaKind Kind, string Extension);

/// <summary>
/// Decides what an upload really is from its first bytes, never from the file name or the browser's claim. Only a short allow-list passes
/// (JPEG, PNG, GIF, WebP images; MP4/MOV and WebM/Matroska video). SVG, HTML, PDF, archives and everything else are refused.
/// </summary>
public static class MediaSniffer
{
    public const int HeaderBytes = 16;

    public static Sniffed? Detect(ReadOnlySpan<byte> h)
    {
        if (h.Length >= 3 && h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF) return new(MediaKind.Image, "jpg");
        if (h.Length >= 8 && h[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return new(MediaKind.Image, "png");
        if (h.Length >= 6 && (h[..6].SequenceEqual("GIF87a"u8) || h[..6].SequenceEqual("GIF89a"u8))) return new(MediaKind.Image, "gif");
        if (h.Length >= 12 && h[..4].SequenceEqual("RIFF"u8) && h.Slice(8, 4).SequenceEqual("WEBP"u8)) return new(MediaKind.Image, "webp");
        if (h.Length >= 12 && h.Slice(4, 4).SequenceEqual("ftyp"u8)) return new(MediaKind.Video, "mp4");
        if (h.Length >= 4 && h[0] == 0x1A && h[1] == 0x45 && h[2] == 0xDF && h[3] == 0xA3) return new(MediaKind.Video, "webm");
        return null;
    }
}
