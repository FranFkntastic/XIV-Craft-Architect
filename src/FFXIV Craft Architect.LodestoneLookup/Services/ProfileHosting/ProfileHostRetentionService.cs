using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

public sealed record ProfileHostRetentionSweepResult(
    int Scanned,
    int Purged,
    int SkippedUnarchived,
    int BackupFailures,
    int DeleteConflicts);

public sealed class ProfileHostRetentionService : BackgroundService
{
    private readonly ProfileHostOptions _options;
    private readonly SqliteProfileHostStore _store;
    private readonly ProfileArchiveBackupStore _backups;
    private readonly ILogger<ProfileHostRetentionService> _logger;

    public ProfileHostRetentionService(
        ProfileHostOptions options,
        SqliteProfileHostStore store,
        ProfileArchiveBackupStore backups,
        ILogger<ProfileHostRetentionService> logger)
    {
        _options = options;
        _store = store;
        _backups = backups;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || !_options.ArchiveRetentionEnabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.RetentionSweepInterval, stoppingToken);
                var result = await RunSweepAsync(stoppingToken);
                if (result.Purged > 0 || result.BackupFailures > 0 || result.DeleteConflicts > 0)
                {
                    _logger.LogInformation(
                        "Archive retention sweep: {Purged} purged, {Skipped} unarchived, {BackupFailures} backup failures, {Conflicts} delete conflicts out of {Scanned} candidates.",
                        result.Purged,
                        result.SkippedUnarchived,
                        result.BackupFailures,
                        result.DeleteConflicts,
                        result.Scanned);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Archive retention sweep failed.");
            }
        }
    }

    public async Task<ProfileHostRetentionSweepResult> RunSweepAsync(CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromDays(_options.ArchiveRetentionDays);
        var scanned = 0;
        var purged = 0;
        var skippedUnarchived = 0;
        var backupFailures = 0;
        var deleteConflicts = 0;
        foreach (var profileId in await _store.LoadActiveProfileIdsAsync(ct))
        {
            var candidates = await _store.LoadRetentionCandidatesAsync(profileId, cutoff, ct);
            foreach (var candidate in candidates)
            {
                ct.ThrowIfCancellationRequested();
                scanned++;
                if (TradeOrderArchiveSummaryCodec.TryCreate(
                        candidate.PayloadJson,
                        candidate.ObjectId) == null)
                {
                    skippedUnarchived++;
                    continue;
                }

                if (!await _backups.TryBackupAsync(profileId, candidate, ct))
                {
                    backupFailures++;
                    continue;
                }

                var deletion = await _store.DeleteObjectAsync(
                    profileId,
                    candidate.Collection,
                    candidate.ObjectId,
                    candidate.Revision,
                    ct);
                if (deletion.Conflict)
                {
                    deleteConflicts++;
                    continue;
                }

                purged++;
            }
        }

        return new ProfileHostRetentionSweepResult(
            scanned,
            purged,
            skippedUnarchived,
            backupFailures,
            deleteConflicts);
    }
}
