using System.Security.Cryptography;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

public sealed class ProfileAccessKeyHasher
{
    private const int SaltBytes = 16;
    private const int KeyBytes = 32;
    private const int Iterations = 210_000;

    public CreatedProfileAccessKey CreateAccessKey()
    {
        var keyBytes = RandomNumberGenerator.GetBytes(32);
        var plaintext = "cap_" + Convert.ToBase64String(keyBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return new CreatedProfileAccessKey(plaintext, Hash(plaintext));
    }

    public string Hash(string plaintextKey)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(plaintextKey, salt, Iterations, HashAlgorithmName.SHA256, KeyBytes);
        return $"pbkdf2-sha256:{Iterations}:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    public bool Verify(string plaintextKey, string storedHash)
    {
        if (string.IsNullOrEmpty(plaintextKey) || string.IsNullOrEmpty(storedHash))
        {
            return false;
        }

        var parts = storedHash.Split(':');
        if (parts.Length != 4 ||
            parts[0] != "pbkdf2-sha256" ||
            !int.TryParse(parts[1], out var iterations) ||
            iterations != Iterations)
        {
            return false;
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (salt.Length != SaltBytes || expected.Length != KeyBytes)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            plaintextKey,
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

public sealed record CreatedProfileAccessKey(string PlaintextKey, string StoredHash);
