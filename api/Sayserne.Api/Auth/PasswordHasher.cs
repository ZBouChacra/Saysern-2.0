using System.Security.Cryptography;

namespace Sayserne.Api.Auth;

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string stored);
}

/// <summary>PBKDF2-HMAC-SHA256 with a random 128-bit salt. Stored as v1.{iterations}.{salt}.{hash} (base64).</summary>
public sealed class Pbkdf2PasswordHasher(int iterations = 600_000) : IPasswordHasher
{
    private const int SaltSize = 16;
    private const int KeySize = 32;
    private const int MinAcceptedIterations = 1_000;       // floor lets tests run fast; production default is 600k
    private const int MaxAcceptedIterations = 5_000_000;   // refuse absurd values from a tampered row (CPU DoS)

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, KeySize);
        return $"v1.{iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(key)}";
    }

    public bool Verify(string password, string stored)
    {
        var parts = stored.Split('.');
        if (parts.Length != 4 || parts[0] != "v1" || !int.TryParse(parts[1], out var iter)
            || iter is < MinAcceptedIterations or > MaxAcceptedIterations)
            return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            if (salt.Length < 8 || expected.Length != KeySize) return false;
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iter, HashAlgorithmName.SHA256, KeySize);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
