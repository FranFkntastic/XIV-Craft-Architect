using System.Globalization;
using System.Text;
using Chaos.NaCl;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;

public sealed class DiscordRequestVerifier(DiscordCommissionOptions options)
{
    private static readonly TimeSpan MaximumRequestAge = TimeSpan.FromMinutes(5);

    public bool Verify(string timestamp, string signatureHex, ReadOnlySpan<byte> body)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var maximumSkew = (long)MaximumRequestAge.TotalSeconds;
        if (!options.CanVerifyInteractions ||
            !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var timestampSeconds) ||
            timestampSeconds < now - maximumSkew ||
            timestampSeconds > now + maximumSkew ||
            !TryDecodeHex(options.PublicKey, 32, out var publicKey) ||
            !TryDecodeHex(signatureHex, 64, out var signature))
        {
            return false;
        }

        var timestampBytes = Encoding.ASCII.GetBytes(timestamp);
        var signedBody = new byte[timestampBytes.Length + body.Length];
        timestampBytes.CopyTo(signedBody, 0);
        body.CopyTo(signedBody.AsSpan(timestampBytes.Length));
        return Ed25519.Verify(signature, signedBody, publicKey);
    }

    private static bool TryDecodeHex(string value, int byteCount, out byte[] bytes)
    {
        bytes = [];
        if (value.Length != byteCount * 2)
        {
            return false;
        }

        try
        {
            bytes = Convert.FromHexString(value);
            return bytes.Length == byteCount;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
