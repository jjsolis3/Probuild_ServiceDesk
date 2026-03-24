using System.Security.Cryptography;
using System.Text;

namespace ServiceDesk.Core.Services;

/// <summary>
/// PBKDF2-SHA256 password hashing. No external packages required.
/// Stored format: "v1:{saltBase64}:{hashBase64}"
/// </summary>
public static class PasswordService
{
    private const int SaltSize = 16;        // 128-bit salt
    private const int KeySize = 32;         // 256-bit output
    private const int Iterations = 100_000;
    private static readonly HashAlgorithmName Algorithm = HashAlgorithmName.SHA256;

    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt, Iterations, Algorithm, KeySize);
        return $"v1:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    public static bool VerifyPassword(string password, string? storedHash)
    {
        if (string.IsNullOrEmpty(storedHash)) return false;
        var parts = storedHash.Split(':');
        if (parts.Length != 3 || parts[0] != "v1") return false;

        byte[] salt, expectedHash;
        try
        {
            salt = Convert.FromBase64String(parts[1]);
            expectedHash = Convert.FromBase64String(parts[2]);
        }
        catch { return false; }

        var actualHash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt, Iterations, Algorithm, KeySize);

        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }
}
