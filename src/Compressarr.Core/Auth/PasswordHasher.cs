using System.Security.Cryptography;
using System.Text;

namespace Compressarr.Core.Auth;

/// <summary>Passwords are kept only as PBKDF2-HMAC-SHA512 hashes (what Sonarr and Radarr use): 220,000 rounds, a random 16-byte salt per password, a 32-byte key, written
/// <c>pbkdf2-sha512$220000$&lt;salt&gt;$&lt;key&gt;</c> so the rounds can be raised later and the old hashes still verify (and are re-hashed at the next login).</summary>
public static class PasswordHasher
{
    public const int CurrentRounds = 220_000;
    private const string Prefix = "pbkdf2-sha512";
    private const int SaltBytes = 16;
    private const int KeyBytes = 32;

    public static string Hash(string password) => Hash(password, CurrentRounds);

    internal static string Hash(string password, int rounds)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, rounds, HashAlgorithmName.SHA512, KeyBytes);
        return $"{Prefix}${rounds}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
    }

    /// <summary>True when the password matches the stored hash (constant-time). A malformed hash never matches.</summary>
    public static bool Verify(string password, string? stored)
    {
        if (!TryParse(stored, out var rounds, out var salt, out var expected)) return false;
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, rounds, HashAlgorithmName.SHA512, expected.Length);
        return CryptographicOperations.FixedTimeEquals(key, expected);
    }

    /// <summary>True when the stored hash was made with fewer rounds than today's, so it should be replaced after a successful login.</summary>
    public static bool NeedsRehash(string? stored) => !TryParse(stored, out var rounds, out _, out _) || rounds < CurrentRounds;

    private static bool TryParse(string? stored, out int rounds, out byte[] salt, out byte[] key)
    {
        rounds = 0;
        salt = key = Array.Empty<byte>();
        var parts = stored?.Split('$');
        if (parts is not { Length: 4 } || parts[0] != Prefix || !int.TryParse(parts[1], out rounds) || rounds < 1) return false;

        try
        {
            salt = Convert.FromBase64String(parts[2]);
            key = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        return salt.Length > 0 && key.Length > 0;
    }
}
