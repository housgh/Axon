using System.Security.Cryptography;

namespace Axon.Server.Services;

/// <summary>
/// PBKDF2-HMACSHA256 password hashing for dashboard users. Users still configure
/// <see cref="DashboardUser.Password"/> in plain text (appsettings.json, environment variables,
/// a secrets manager) - that's unavoidable without adding a separate hash-generation step to
/// every consumer's setup - but the plaintext is hashed with a random per-user salt once at
/// startup (see <see cref="DependencyInjection.AddAuthentication"/>) and only the salted hash is
/// held afterward and compared against on each login attempt.
/// </summary>
internal static class PasswordHasher
{
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int Iterations = 210_000; // OWASP-recommended minimum for PBKDF2-HMAC-SHA256 as of 2023.

    /// <summary>Hashes a plaintext password, returning "{saltBase64}:{hashBase64}".</summary>
    public static string Hash(string plaintextPassword)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(plaintextPassword, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return $"{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    /// <summary>Verifies a plaintext password against a hash produced by <see cref="Hash"/>.</summary>
    public static bool Verify(string plaintextPassword, string storedHash)
    {
        var parts = storedHash.Split(':', 2);
        if (parts.Length != 2) return false;

        byte[] salt, expectedHash;
        try
        {
            salt = Convert.FromBase64String(parts[0]);
            expectedHash = Convert.FromBase64String(parts[1]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actualHash = Rfc2898DeriveBytes.Pbkdf2(plaintextPassword, salt, Iterations, HashAlgorithmName.SHA256, expectedHash.Length);
        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }
}
