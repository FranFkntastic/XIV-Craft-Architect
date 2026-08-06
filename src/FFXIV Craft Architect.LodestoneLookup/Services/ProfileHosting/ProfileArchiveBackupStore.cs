using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

public sealed class ProfileArchiveBackupStore
{
    private static readonly JsonSerializerOptions JsonOptions =
        ProfileSyncJson.CreateOptions();
    private readonly ProfileHostOptions _options;
    private readonly ILogger<ProfileArchiveBackupStore> _logger;

    public ProfileArchiveBackupStore(
        ProfileHostOptions options,
        ILogger<ProfileArchiveBackupStore> logger)
    {
        _options = options;
        _logger = logger;
    }

    public string GetBackupFilePath(string profileId, DateTime updatedAtUtc) =>
        Path.Combine(
            _options.ArchiveBackupDirectory,
            Uri.EscapeDataString(profileId),
            $"archived-orders-{updatedAtUtc:yyyy-MM}.jsonl");

    public async Task<bool> TryBackupAsync(
        string profileId,
        ProfileSyncObjectEnvelope envelope,
        CancellationToken ct)
    {
        var record = JsonSerializer.Serialize(
            new
            {
                backedUpAtUtc = DateTime.UtcNow,
                profileId,
                envelope.Collection,
                envelope.ObjectId,
                envelope.Revision,
                envelope.UpdatedAtUtc,
                envelope.PayloadJson
            },
            JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(record + "\n");
        var path = GetBackupFilePath(profileId, envelope.UpdatedAtUtc);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            long offset;
            await using (var stream = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 8192,
                useAsync: true))
            {
                offset = stream.Length;
                await stream.WriteAsync(bytes, ct);
                stream.Flush(flushToDisk: true);
            }

            await using (var verify = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 8192,
                useAsync: true))
            {
                verify.Seek(offset, SeekOrigin.Begin);
                var readBack = new byte[bytes.Length];
                var filled = 0;
                while (filled < readBack.Length)
                {
                    var read = await verify.ReadAsync(
                        readBack.AsMemory(filled),
                        ct);
                    if (read == 0)
                    {
                        break;
                    }

                    filled += read;
                }

                if (filled != bytes.Length ||
                    !SHA256.HashData(readBack).SequenceEqual(SHA256.HashData(bytes)))
                {
                    _logger.LogError(
                        "Archive backup verification failed for {Collection}/{ObjectId} in profile {ProfileId}.",
                        envelope.Collection,
                        envelope.ObjectId,
                        profileId);
                    return false;
                }
            }

            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(
                exception,
                "Archive backup write failed for {Collection}/{ObjectId} in profile {ProfileId}.",
                envelope.Collection,
                envelope.ObjectId,
                profileId);
            return false;
        }
    }
}
