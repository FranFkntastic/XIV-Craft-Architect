using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.Web.Services.CompanyMigration;

public sealed class CompanyMigrationWorkflowService
{
    private readonly CompanyMigrationInventoryExportService _inventory;
    private readonly CompanyMigrationBundleService _bundles;
    private readonly CompanyMigrationCheckpointStore _checkpoints;
    private readonly ProfileHostClient _profileHost;
    private readonly ProfileSyncService _profileSync;
    private readonly TradeOperationsPersistenceService _tradeOperations;

    private string? _verifiedHostUrl;
    private string? _verifiedAccessKey;
    private ProfileHostBootstrapPayload? _destinationBefore;
    private IReadOnlyList<CompanyMigrationExportBundle> _recoverySources =
        Array.Empty<CompanyMigrationExportBundle>();
    private string? _combinedSourceHash;

    public CompanyMigrationWorkflowService(
        CompanyMigrationInventoryExportService inventory,
        CompanyMigrationBundleService bundles,
        CompanyMigrationCheckpointStore checkpoints,
        ProfileHostClient profileHost,
        ProfileSyncService profileSync,
        TradeOperationsPersistenceService tradeOperations)
    {
        _inventory = inventory;
        _bundles = bundles;
        _checkpoints = checkpoints;
        _profileHost = profileHost;
        _profileSync = profileSync;
        _tradeOperations = tradeOperations;
    }

    public bool RememberAccessKey { get; private set; }
    public ProfileHostProfileResponse? VerifiedDestination { get; private set; }
    public ProfileHostMigrationPreflightResponse? LastPreflight { get; private set; }
    public ProfileHostMigrationCommitResponse? LastReceipt { get; private set; }
    public CompanyMigrationRecoveryCheckpoint? RecoveryCheckpoint { get; private set; }

    public bool HasPendingActivation =>
        RecoveryCheckpoint is
        {
            Stage: CompanyMigrationCheckpointStage.Committed,
            Receipt: not null
        };

    public bool HasPendingCommit =>
        RecoveryCheckpoint is
        {
            Stage: CompanyMigrationCheckpointStage.CommitSent,
            Receipt: null
        };

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        RecoveryCheckpoint = await _checkpoints.LoadAsync(ct);
        LastReceipt = RecoveryCheckpoint?.Receipt;
        LastPreflight = RecoveryCheckpoint?.Preflight;
    }

    public void SetRememberAccessKey(bool rememberAccessKey)
    {
        RememberAccessKey = rememberAccessKey;
    }

    public async Task<CompanyMigrationExportBundle> CaptureCurrentBrowserAsync(
        CancellationToken ct)
    {
        var bundle = await _inventory.CreateInventoryAsync(ct);
        _recoverySources = [bundle];
        _combinedSourceHash = bundle.ContentHash;
        return bundle;
    }

    public CompanyMigrationBundleValidationResult ParseUploadedBundle(string json) =>
        _bundles.ParseUploadedBundle(json);

    public CompanyMigrationBundleCombinationResult CombineSources(
        IReadOnlyList<CompanyMigrationExportBundle> sources)
    {
        var result = _bundles.CombineBundles(sources);
        if (result.CanUse && result.Bundle != null)
        {
            _recoverySources = result.SourceBundles;
            _combinedSourceHash = result.Bundle.ContentHash;
        }

        return result;
    }

    public async Task<ProfileHostProfileResponse> VerifyDestinationAsync(
        string hostUrl,
        string accessKey,
        CancellationToken ct)
    {
        hostUrl = NormalizeHostUrl(hostUrl);
        accessKey = RequireAccessKey(accessKey);
        if (_profileSync.PendingSaves.Count > 0 ||
            _profileSync.Conflicts.Count > 0)
        {
            throw new InvalidOperationException(
                "Finish or resolve the current hosted-profile sync queue before moving companies.");
        }

        var health = await _profileHost.GetHealthAsync(hostUrl, ct);
        if (!health.ProfileHostEnabled)
        {
            throw new InvalidOperationException(
                "Profile hosting is not enabled at that destination.");
        }

        var profile = await _profileHost.GetProfileAsync(hostUrl, accessKey, ct);
        if (!Guid.TryParse(profile.ProfileId, out var profileId) ||
            profileId == Guid.Empty)
        {
            throw new InvalidOperationException(
                "The destination returned an invalid hosted profile identity.");
        }

        _destinationBefore = SanitizeDestinationBootstrap(
            await _profileHost.ExportBootstrapAsync(
                hostUrl,
                accessKey,
                ct));
        _verifiedHostUrl = hostUrl;
        _verifiedAccessKey = accessKey;
        VerifiedDestination = profile;
        LastPreflight = null;
        return profile;
    }

    public async Task<ProfileHostMigrationPreflightResponse> PreflightAsync(
        string hostUrl,
        string accessKey,
        CompanyMigrationExportBundle source,
        IReadOnlyList<ProfileHostMigrationResolution> resolutions,
        IReadOnlyList<ProfileHostMigrationCanonicalMapping> mappings,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        await EnsureVerifiedDestinationAsync(hostUrl, accessKey, ct);
        EnsureSourceCanPreflight(source);
        var request = BuildPreflightRequest(source, resolutions, mappings);
        EnsureRecoverySources(source);
        var response = await _profileHost.PreflightMigrationAsync(
            _verifiedHostUrl!,
            _verifiedAccessKey!,
            request,
            ct);
        EnsureResponseMatchesRequest(response, request);
        LastPreflight = response;
        return response;
    }

    public async Task<ProfileHostMigrationCommitClientResult> CommitAsync(
        string hostUrl,
        string accessKey,
        CompanyMigrationExportBundle source,
        ProfileHostMigrationPreflightResponse preflight,
        IReadOnlyList<ProfileHostMigrationResolution> resolutions,
        IReadOnlyList<ProfileHostMigrationCanonicalMapping> mappings,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(preflight);
        await EnsureVerifiedDestinationAsync(hostUrl, accessKey, ct);
        EnsureSourceCanPreflight(source);
        if (!preflight.CanCommit)
        {
            throw new InvalidOperationException(
                "The latest destination preflight still has unresolved decisions.");
        }

        var preflightRequest = BuildPreflightRequest(source, resolutions, mappings);
        EnsureRecoverySources(source);
        var currentPreflight = await _profileHost.PreflightMigrationAsync(
            _verifiedHostUrl!,
            _verifiedAccessKey!,
            preflightRequest,
            ct);
        EnsureResponseMatchesRequest(currentPreflight, preflightRequest);
        if (!currentPreflight.CanCommit ||
            !string.Equals(
                currentPreflight.PreflightHash,
                preflight.PreflightHash,
                StringComparison.Ordinal))
        {
            LastPreflight = currentPreflight;
            return ProfileHostMigrationCommitClientResult.FromConflict(currentPreflight);
        }

        var request = new ProfileHostMigrationCommitRequest
        {
            MigrationId = preflightRequest.MigrationId,
            PreflightHash = currentPreflight.PreflightHash,
            Objects = preflightRequest.Objects,
            Resolutions = preflightRequest.Resolutions,
            Mappings = preflightRequest.Mappings
        };
        var activeCompanyId =
            (await _tradeOperations.GetOrCreateActiveCompanyProfileAsync()).Id;
        var checkpoint = new CompanyMigrationRecoveryCheckpoint
        {
            Stage = CompanyMigrationCheckpointStage.Prepared,
            HostUrl = _verifiedHostUrl!,
            ProfileId = VerifiedDestination!.ProfileId,
            ProfileName = VerifiedDestination.DisplayName,
            ActiveCompanyId = activeCompanyId.ToString("D"),
            Source = source,
            DestinationBefore = _destinationBefore ??
                                throw new InvalidOperationException(
                                    "The verified destination snapshot is unavailable."),
            Preflight = currentPreflight,
            Request = request
        };

        var archive = _bundles.CreateRecoveryArchive(
            _recoverySources,
            checkpoint.DestinationBefore,
            preflightRequest,
            currentPreflight,
            request);
        var saveResult = await _bundles.ExportRecoveryArchiveAsync(archive, ct);
        if (!saveResult.Completed)
        {
            throw new OperationCanceledException(
                "The recovery archive save was canceled. Nothing was written to the hosted profile.",
                ct);
        }

        await _checkpoints.SaveAsync(checkpoint, ct);
        checkpoint.Stage = CompanyMigrationCheckpointStage.CommitSent;
        await _checkpoints.SaveAsync(checkpoint, ct);
        RecoveryCheckpoint = checkpoint;

        var result = await _profileHost.CommitMigrationAsync(
            _verifiedHostUrl!,
            _verifiedAccessKey!,
            request,
            ct);
        if (!result.Succeeded)
        {
            checkpoint.Stage = CompanyMigrationCheckpointStage.Prepared;
            if (result.Conflict != null)
            {
                checkpoint.Preflight = result.Conflict;
                LastPreflight = result.Conflict;
            }

            await _checkpoints.SaveAsync(checkpoint, ct);
            return result;
        }

        var receipt = result.Commit!;
        ValidateReceipt(receipt, currentPreflight, request);
        checkpoint.Receipt = receipt;
        checkpoint.Stage = CompanyMigrationCheckpointStage.Committed;
        await _checkpoints.SaveAsync(checkpoint, ct);
        LastReceipt = receipt;

        await ActivateCommittedMigrationAsync(
            checkpoint,
            _verifiedAccessKey!,
            ct);
        return result;
    }

    public async Task RetryActivationAsync(
        string accessKey,
        bool rememberAccessKey,
        CancellationToken ct = default)
    {
        var checkpoint = RecoveryCheckpoint ?? await _checkpoints.LoadAsync(ct);
        if (checkpoint is not
            {
                Stage: CompanyMigrationCheckpointStage.Committed,
                Receipt: not null
            })
        {
            throw new InvalidOperationException(
                "There is no committed company migration waiting to be activated.");
        }

        RememberAccessKey = rememberAccessKey;
        accessKey = RequireAccessKey(accessKey);
        var profile = await _profileHost.GetProfileAsync(
            checkpoint.HostUrl,
            accessKey,
            ct);
        if (!string.Equals(
                profile.ProfileId,
                checkpoint.ProfileId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "That access key does not open the hosted profile recorded by the migration receipt.");
        }

        VerifiedDestination = profile;
        _verifiedHostUrl = checkpoint.HostUrl;
        _verifiedAccessKey = accessKey;
        RecoveryCheckpoint = checkpoint;
        await ActivateCommittedMigrationAsync(
            checkpoint,
            accessKey,
            ct);
    }

    public async Task<ProfileHostMigrationCommitClientResult> RetryCommitAsync(
        string accessKey,
        bool rememberAccessKey,
        CancellationToken ct = default)
    {
        var checkpoint = RecoveryCheckpoint ?? await _checkpoints.LoadAsync(ct);
        if (checkpoint is not
            {
                Stage: CompanyMigrationCheckpointStage.CommitSent,
                Receipt: null
            })
        {
            throw new InvalidOperationException(
                "There is no uncertain company migration commit waiting to be retried.");
        }

        RememberAccessKey = rememberAccessKey;
        accessKey = RequireAccessKey(accessKey);
        var profile = await _profileHost.GetProfileAsync(
            checkpoint.HostUrl,
            accessKey,
            ct);
        if (!string.Equals(
                profile.ProfileId,
                checkpoint.ProfileId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "That access key does not open the hosted profile recorded by the recovery checkpoint.");
        }

        VerifiedDestination = profile;
        _verifiedHostUrl = checkpoint.HostUrl;
        _verifiedAccessKey = accessKey;
        RecoveryCheckpoint = checkpoint;
        var result = await _profileHost.CommitMigrationAsync(
            checkpoint.HostUrl,
            accessKey,
            checkpoint.Request,
            ct);
        if (!result.Succeeded)
        {
            checkpoint.Stage = CompanyMigrationCheckpointStage.Prepared;
            if (result.Conflict != null)
            {
                checkpoint.Preflight = result.Conflict;
                LastPreflight = result.Conflict;
            }

            await _checkpoints.SaveAsync(checkpoint, ct);
            return result;
        }

        var receipt = result.Commit!;
        ValidateReceipt(receipt, checkpoint.Preflight, checkpoint.Request);
        checkpoint.Receipt = receipt;
        checkpoint.Stage = CompanyMigrationCheckpointStage.Committed;
        await _checkpoints.SaveAsync(checkpoint, ct);
        LastReceipt = receipt;
        await ActivateCommittedMigrationAsync(
            checkpoint,
            accessKey,
            ct);
        return result;
    }

    public async Task ClearCompletedCheckpointAsync(
        CancellationToken ct = default)
    {
        if (RecoveryCheckpoint is { Stage: not CompanyMigrationCheckpointStage.Activated })
        {
            throw new InvalidOperationException(
                "An unfinished company migration checkpoint cannot be discarded here.");
        }

        await _checkpoints.ClearAsync(ct);
        RecoveryCheckpoint = null;
    }

    private async Task ActivateCommittedMigrationAsync(
        CompanyMigrationRecoveryCheckpoint checkpoint,
        string accessKey,
        CancellationToken ct)
    {
        if (!Guid.TryParse(checkpoint.ActiveCompanyId, out var activeCompanyId) ||
            activeCompanyId == Guid.Empty)
        {
            throw new InvalidOperationException(
                "The recovery checkpoint does not identify the company that was active before cutover.");
        }

        await _profileSync.ActivateMigrationAsync(
            new HostedProfileConnectionSettings
            {
                HostUrl = checkpoint.HostUrl,
                AccessKey = accessKey,
                RememberAccessKey = RememberAccessKey,
                ConnectedProfileId = checkpoint.ProfileId,
                ConnectedProfileName = checkpoint.ProfileName
            },
            checkpoint.Receipt!.Mappings,
            checkpoint.Request.Resolutions,
            async callbackCt =>
            {
                var canonicalActiveCompanyId = checkpoint.Receipt.Mappings
                    .FirstOrDefault(mapping =>
                        mapping.Collection ==
                        ProfileSyncCollections.TradeCompanyProfiles &&
                        string.Equals(
                            mapping.SourceObjectId,
                            activeCompanyId.ToString("D"),
                            StringComparison.OrdinalIgnoreCase))
                    ?.TargetObjectId;
                var selectedId = Guid.TryParse(
                    canonicalActiveCompanyId,
                    out var mappedId)
                    ? mappedId
                    : activeCompanyId;
                var companies = await _tradeOperations.LoadCompanyProfilesAsync();
                callbackCt.ThrowIfCancellationRequested();
                var selected = companies.FirstOrDefault(company =>
                                   company.Id == selectedId) ??
                               companies.OrderBy(
                                       company => company.Name,
                                       StringComparer.OrdinalIgnoreCase)
                                   .ThenBy(company => company.Id)
                                   .FirstOrDefault() ??
                               throw new InvalidOperationException(
                                   "The hosted migration committed without an active company profile.");
                await _tradeOperations.SelectCompanyProfileAsync(selected.Id);
            },
            ct);

        checkpoint.Stage = CompanyMigrationCheckpointStage.Activated;
        await _checkpoints.SaveAsync(checkpoint, ct);
        LastReceipt = checkpoint.Receipt;
        await _checkpoints.ClearAsync(ct);
        RecoveryCheckpoint = null;
    }

    private async Task EnsureVerifiedDestinationAsync(
        string hostUrl,
        string accessKey,
        CancellationToken ct)
    {
        hostUrl = NormalizeHostUrl(hostUrl);
        accessKey = RequireAccessKey(accessKey);
        if (VerifiedDestination == null ||
            !string.Equals(_verifiedHostUrl, hostUrl, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(_verifiedAccessKey, accessKey, StringComparison.Ordinal))
        {
            await VerifyDestinationAsync(hostUrl, accessKey, ct);
        }
    }

    private static ProfileHostMigrationPreflightRequest BuildPreflightRequest(
        CompanyMigrationExportBundle source,
        IReadOnlyList<ProfileHostMigrationResolution> resolutions,
        IReadOnlyList<ProfileHostMigrationCanonicalMapping> mappings)
    {
        var objects = source.Objects
            .Where(item =>
                item.Collection !=
                ProfileHostMigrationCollections.TradeOrderCraftSnapshots)
            .OrderBy(item => item.Collection, StringComparer.Ordinal)
            .ThenBy(item => item.ObjectId, StringComparer.Ordinal)
            .ToArray();
        var identities = objects
            .Select(item => (item.Collection, item.ObjectId))
            .ToHashSet();
        return new ProfileHostMigrationPreflightRequest
        {
            MigrationId = source.MigrationId,
            Objects = objects,
            Resolutions = (resolutions ?? Array.Empty<ProfileHostMigrationResolution>())
                .Where(item => identities.Contains((item.Collection, item.ObjectId)))
                .OrderBy(item => item.Collection, StringComparer.Ordinal)
                .ThenBy(item => item.ObjectId, StringComparer.Ordinal)
                .ToArray(),
            Mappings = (mappings ?? Array.Empty<ProfileHostMigrationCanonicalMapping>())
                .Where(item => identities.Contains((item.Collection, item.SourceObjectId)))
                .OrderBy(item => item.Collection, StringComparer.Ordinal)
                .ThenBy(item => item.SourceObjectId, StringComparer.Ordinal)
                .ToArray()
        };
    }

    private static void EnsureSourceCanPreflight(
        CompanyMigrationExportBundle source)
    {
        if (source.PackageKind != CompanyMigrationExportBundle.PackageKindValue ||
            source.FormatVersion != CompanyMigrationExportBundle.CurrentFormatVersion)
        {
            throw new InvalidOperationException(
                "The selected company migration source uses an unsupported format.");
        }

        var hardBlockers = source.Manifest.Blockers
            .Where(blocker => !blocker.IsArchiveOnly)
            .ToArray();
        if (hardBlockers.Length > 0)
        {
            throw new InvalidOperationException(
                $"The browser inventory cannot move yet: {hardBlockers[0].Message}");
        }

        if (!source.Objects.Any(item =>
                item.Collection !=
                ProfileHostMigrationCollections.TradeOrderCraftSnapshots))
        {
            throw new InvalidOperationException(
                "No supported company records were found to move.");
        }
    }

    private void EnsureRecoverySources(CompanyMigrationExportBundle source)
    {
        if (!string.Equals(
                _combinedSourceHash,
                source.ContentHash,
                StringComparison.Ordinal) ||
            _recoverySources.Count == 0)
        {
            _recoverySources = [source];
            _combinedSourceHash = source.ContentHash;
        }
    }

    private static void EnsureResponseMatchesRequest(
        ProfileHostMigrationPreflightResponse response,
        ProfileHostMigrationPreflightRequest request)
    {
        if (response.MigrationId != request.MigrationId ||
            string.IsNullOrWhiteSpace(response.RequestHash) ||
            string.IsNullOrWhiteSpace(response.PreflightHash))
        {
            throw new InvalidOperationException(
                "The hosted destination returned an invalid migration preflight.");
        }
    }

    private static void ValidateReceipt(
        ProfileHostMigrationCommitResponse receipt,
        ProfileHostMigrationPreflightResponse preflight,
        ProfileHostMigrationCommitRequest request)
    {
        if (receipt.MigrationId != request.MigrationId ||
            !string.Equals(
                receipt.RequestHash,
                preflight.RequestHash,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(receipt.ReceiptHash) ||
            receipt.ServerRevision < 1)
        {
            throw new InvalidOperationException(
                "The hosted destination returned an invalid migration receipt. The exact request is saved for a safe retry.");
        }
    }

    private static string NormalizeHostUrl(string hostUrl)
    {
        if (string.IsNullOrWhiteSpace(hostUrl))
        {
            throw new InvalidOperationException(
                "A hosted backend URL is required.");
        }

        if (!Uri.TryCreate(hostUrl.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException(
                "The hosted backend URL must be an absolute HTTP or HTTPS address.");
        }

        return uri.ToString().TrimEnd('/');
    }

    private static string RequireAccessKey(string accessKey)
    {
        if (string.IsNullOrWhiteSpace(accessKey))
        {
            throw new InvalidOperationException(
                "A hosted profile access key is required.");
        }

        return accessKey.Trim();
    }

    private static ProfileHostBootstrapPayload SanitizeDestinationBootstrap(
        ProfileHostBootstrapPayload bootstrap)
    {
        return new ProfileHostBootstrapPayload
        {
            Objects = (bootstrap.Objects ?? Array.Empty<ProfileSyncObjectEnvelope>())
                .Where(item =>
                    item.Collection != ProfileSyncCollections.Settings ||
                    !IsSecretSetting(item.ObjectId))
                .Select(item => new ProfileSyncObjectEnvelope
                {
                    Collection = item.Collection,
                    ObjectId = item.ObjectId,
                    PayloadJson = item.PayloadJson,
                    Revision = item.Revision,
                    UpdatedAtUtc = item.UpdatedAtUtc,
                    Deleted = item.Deleted,
                    DeletedAtUtc = item.DeletedAtUtc
                })
                .ToArray()
        };
    }

    private static bool IsSecretSetting(string key)
    {
        if (ProfileSyncSettingsKeys.ConnectionSettingKeys.Contains(key))
        {
            return true;
        }

        var normalized = key
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(".", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        return normalized.Contains("apikey", StringComparison.Ordinal) ||
               normalized.Contains("accesskey", StringComparison.Ordinal) ||
               normalized.Contains("password", StringComparison.Ordinal) ||
               normalized.Contains("secret", StringComparison.Ordinal) ||
               normalized.Contains("credential", StringComparison.Ordinal) ||
               normalized.EndsWith("token", StringComparison.Ordinal);
    }
}
