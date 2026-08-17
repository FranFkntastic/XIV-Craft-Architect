using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

public sealed record ProfileHostDeepArchiveSweepResult(
    int Scanned,
    int DeepArchived,
    int SkippedUnarchived,
    int Conflicts);

public sealed class ProfileHostDeepArchiveService : BackgroundService
{
    private readonly ProfileHostOptions _options;
    private readonly SqliteProfileHostStore _store;
    private readonly ILogger<ProfileHostDeepArchiveService> _logger;

    public ProfileHostDeepArchiveService(
        ProfileHostOptions options,
        SqliteProfileHostStore store,
        ILogger<ProfileHostDeepArchiveService> logger)
    {
        _options = options;
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || !_options.DeepArchiveEnabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await RunSweepAsync(stoppingToken);
                if (result.DeepArchived > 0 || result.Conflicts > 0)
                {
                    _logger.LogInformation(
                        "Deep archive sweep: {Archived} archived, {Skipped} unarchived, {Conflicts} conflicts out of {Scanned} candidates.",
                        result.DeepArchived,
                        result.SkippedUnarchived,
                        result.Conflicts,
                        result.Scanned);
                }
                await Task.Delay(_options.DeepArchiveSweepInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Deep archive sweep failed.");
                await Task.Delay(_options.DeepArchiveSweepInterval, stoppingToken);
            }
        }
    }

    public async Task<ProfileHostDeepArchiveSweepResult> RunSweepAsync(CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromDays(_options.DeepArchiveAfterDays);
        var scanned = 0;
        var deepArchived = 0;
        var skippedUnarchived = 0;
        var conflicts = 0;
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

                if (await _store.MoveOrderToDeepArchiveAsync(profileId, candidate, ct))
                {
                    deepArchived++;
                }
                else
                {
                    conflicts++;
                }
            }
        }

        return new ProfileHostDeepArchiveSweepResult(
            scanned,
            deepArchived,
            skippedUnarchived,
            conflicts);
    }
}
