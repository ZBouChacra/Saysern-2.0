using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Sayserne.Api.Auth;

public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b; bits += 8;
            while (bits >= 5) { sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static byte[] Decode(string text)
    {
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in text.Trim().TrimEnd('=').ToUpperInvariant())
        {
            var v = Alphabet.IndexOf(c);
            if (v < 0) throw new FormatException("Invalid base32 character.");
            buffer = (buffer << 5) | v; bits += 5;
            if (bits >= 8) { bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF)); bits -= 8; }
        }
        return [.. bytes];
    }
}

/// <summary>RFC 6238 time-based one-time passwords (HMAC-SHA1, 30-second steps, 6 digits), as used by Google Authenticator, Microsoft Authenticator, Authy, 1Password...</summary>
public static class Totp
{
    public const int StepSeconds = 30;
    private const int Digits = 6;

    public static string NewSecret() => Base32.Encode(RandomNumberGenerator.GetBytes(20));

    public static long StepAt(long unixSeconds) => unixSeconds / StepSeconds;

    public static string Code(string base32Secret, long step)
    {
        Span<byte> message = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(message, step);
        var hash = HMACSHA1.HashData(Base32.Decode(base32Secret), message);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Accepts the current step and one either side (clock drift). Returns the matching step, never one already used.</summary>
    public static long? Verify(string base32Secret, string? code, long unixSeconds, long? lastUsedStep)
    {
        var candidate = new string((code ?? "").Where(char.IsDigit).ToArray());
        if (candidate.Length != Digits) return null;
        var now = StepAt(unixSeconds);
        for (var delta = -1; delta <= 1; delta++)
        {
            var step = now + delta;
            if (lastUsedStep is { } used && step <= used) continue;
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(base32Secret, step)), Encoding.ASCII.GetBytes(candidate))) return step;
        }
        return null;
    }

    public static string ProvisioningUri(string issuer, string account, string base32Secret) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}?secret={base32Secret}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";
}

/// <summary>Ten single-use codes shown once at enrollment. Only their SHA-256 hashes are stored.</summary>
public static class RecoveryCodes
{
    private const string Alphabet = "abcdefghjkmnpqrstuvwxyz23456789";   // no look-alikes (i, l, o, 0, 1)

    public static IReadOnlyList<string> Generate(int count = 10) =>
        Enumerable.Range(0, count).Select(_ =>
        {
            var chars = RandomNumberGenerator.GetBytes(10).Select(b => Alphabet[b % Alphabet.Length]).ToArray();
            return new string(chars, 0, 5) + "-" + new string(chars, 5, 5);
        }).ToList();

    public static string Hash(string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(code)))).ToLowerInvariant();

    public static string Normalize(string code) => new string((code ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
}
