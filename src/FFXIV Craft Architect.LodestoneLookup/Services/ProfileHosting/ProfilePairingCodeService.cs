using System.Security.Cryptography;
using System.Text;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

public sealed class ProfilePairingCodeService
{
    public CreatedProfilePairingCode Create()
    {
        var bytes = RandomNumberGenerator.GetBytes(24);
        var plaintext = "pair_" + Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return new CreatedProfilePairingCode(plaintext, Hash(plaintext));
    }

    public string Hash(string plaintext) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext)));
}

public sealed record CreatedProfilePairingCode(string Plaintext, string TokenHash);
