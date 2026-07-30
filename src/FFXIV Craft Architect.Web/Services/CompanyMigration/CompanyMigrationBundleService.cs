using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.Web.Services.CompanyMigration;

public sealed class CompanyMigrationBundleValidationResult
{
    public CompanyMigrationExportBundle? Bundle { get; init; }
    public IReadOnlyList<CompanyMigrationSourceBlocker> Blockers { get; init; } =
        Array.Empty<CompanyMigrationSourceBlocker>();
    public bool IsValid => Bundle is not null && Blockers.Count == 0;
}

public sealed class CompanyMigrationBundleCombinationResult
{
    public CompanyMigrationExportBundle? Bundle { get; init; }
    public IReadOnlyList<CompanyMigrationExportBundle> SourceBundles { get; init; } =
        Array.Empty<CompanyMigrationExportBundle>();
    public IReadOnlyList<CompanyMigrationSourceBlocker> Blockers { get; init; } =
        Array.Empty<CompanyMigrationSourceBlocker>();
    public bool CanUse =>
        Bundle is not null &&
        Blockers.All(blocker => blocker.IsArchiveOnly);
}

public sealed class CompanyMigrationRecoveryArchive
{
    public const int CurrentFormatVersion = 1;
    public const string PackageKindValue =
        "ffxiv-craft-architect.company-migration-recovery";

    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public string PackageKind { get; init; } = PackageKindValue;
    public DateTime ExportedAtUtc { get; init; }
    public IReadOnlyList<CompanyMigrationExportBundle> SourceBundles { get; init; } =
        Array.Empty<CompanyMigrationExportBundle>();
    public ProfileHostBootstrapPayload DestinationBootstrap { get; init; } = new();
    public CompanyMigrationRecoveryRequestMetadata? PreflightRequest { get; init; }
    public ProfileHostMigrationPreflightResponse? PreflightResponse { get; init; }
    public CompanyMigrationRecoveryRequestMetadata? CommitRequest { get; init; }
    public ProfileHostMigrationCommitResponse? Receipt { get; init; }
}

public sealed class CompanyMigrationRecoveryRequestMetadata
{
    public string RequestKind { get; init; } = string.Empty;
    public Guid MigrationId { get; init; }
    public string? RequestHash { get; init; }
    public string? PreflightHash { get; init; }
    public IReadOnlyList<CompanyMigrationRecoveryObjectMetadata> Objects { get; init; } =
        Array.Empty<CompanyMigrationRecoveryObjectMetadata>();
    public IReadOnlyList<ProfileHostMigrationResolution> Resolutions { get; init; } =
        Array.Empty<ProfileHostMigrationResolution>();
    public IReadOnlyList<ProfileHostMigrationCanonicalMapping> Mappings { get; init; } =
        Array.Empty<ProfileHostMigrationCanonicalMapping>();
}

public sealed class CompanyMigrationRecoveryObjectMetadata
{
    public string Collection { get; init; } = string.Empty;
    public string ObjectId { get; init; } = string.Empty;
    public string PayloadContentHash { get; init; } = string.Empty;
}

public sealed class CompanyMigrationBundleService
{
    private const int MaximumBundleCharacters = 64 * 1024 * 1024;
    private const string RecoverySaveKey = "company-migration-recovery";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        MaxDepth = 128
    };

    private readonly BrowserFileExportService _fileExport;

    public CompanyMigrationBundleService(BrowserFileExportService fileExport)
    {
        _fileExport = fileExport ?? throw new ArgumentNullException(nameof(fileExport));
    }

    public CompanyMigrationBundleValidationResult ParseUploadedBundle(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Invalid("bundle_json_empty", "The migration bundle is empty.");
        }
        if (json.Length > MaximumBundleCharacters)
        {
            return Invalid(
                "bundle_too_large",
                $"The migration bundle exceeds the {MaximumBundleCharacters / (1024 * 1024)} MB validation limit.");
        }

        try
        {
            var bundle = JsonSerializer.Deserialize<CompanyMigrationExportBundle>(
                json,
                JsonOptions);
            return bundle is null
                ? Invalid("bundle_json_empty", "The migration bundle contains no document.")
                : ValidateBundle(bundle);
        }
        catch (JsonException exception)
        {
            return Invalid(
                "bundle_json_invalid",
                $"The migration bundle is not valid JSON: {exception.Message}");
        }
        catch (NotSupportedException exception)
        {
            return Invalid(
                "bundle_json_unsupported",
                $"The migration bundle uses unsupported JSON: {exception.Message}");
        }
    }

    public CompanyMigrationBundleValidationResult ValidateBundle(
        CompanyMigrationExportBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var blockers = new List<CompanyMigrationSourceBlocker>();
        if (!string.Equals(
                bundle.PackageKind,
                CompanyMigrationExportBundle.PackageKindValue,
                StringComparison.Ordinal))
        {
            Add(
                "invalid_package_kind",
                $"Expected package kind '{CompanyMigrationExportBundle.PackageKindValue}'.");
        }
        if (bundle.FormatVersion != CompanyMigrationExportBundle.CurrentFormatVersion)
        {
            Add(
                "unsupported_bundle_format",
                $"Migration bundle format v{bundle.FormatVersion} is unsupported; expected v{CompanyMigrationExportBundle.CurrentFormatVersion}.");
        }
        if (bundle.MigrationId == Guid.Empty)
        {
            Add("migration_id_missing", "The migration bundle has no migration ID.");
        }
        if (!IsSha256(bundle.ContentHash))
        {
            Add("content_hash_missing", "The migration bundle has no valid content hash.");
        }

        var manifest = bundle.Manifest;
        if (manifest is null)
        {
            Add("manifest_missing", "The migration bundle has no source manifest.");
            return new CompanyMigrationBundleValidationResult
            {
                Bundle = bundle,
                Blockers = blockers
            };
        }
        if (manifest.Source is null ||
            !IsSha256(manifest.Source.InstallationId))
        {
            Add(
                "installation_hash_missing",
                "The migration bundle has no valid source installation hash.");
        }
        if (!IsSha256(manifest.SourceContentHash))
        {
            Add(
                "source_hash_missing",
                "The migration bundle has no valid source content hash.");
        }

        var objects = bundle.Objects ?? Array.Empty<ProfileHostMigrationObjectInput>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in objects)
        {
            var collection = item?.Collection?.Trim() ?? string.Empty;
            var objectId = item?.ObjectId?.Trim() ?? string.Empty;
            if (collection.Length == 0 || objectId.Length == 0)
            {
                Add(
                    "object_identity_missing",
                    "Every migration object must have a collection and object ID.",
                    collection,
                    objectId);
                continue;
            }

            var identity = BuildObjectIdentity(collection, objectId);
            if (!identities.Add(identity))
            {
                Add(
                    ProfileHostMigrationBlockerCodes.DuplicateObjectIdentity,
                    $"Migration object '{collection}/{objectId}' appears more than once.",
                    collection,
                    objectId);
            }
            if (IsSecretBearingSetting(collection, objectId))
            {
                Add(
                    "secret_bearing_object",
                    $"Connection or secret setting '{objectId}' cannot be carried by a migration bundle.",
                    collection,
                    objectId);
            }

            try
            {
                using var payload = JsonDocument.Parse(item?.PayloadJson ?? string.Empty);
                if (payload.RootElement.ValueKind != JsonValueKind.Object)
                {
                    Add(
                        ProfileHostMigrationBlockerCodes.InvalidPayload,
                        $"Migration object '{collection}/{objectId}' must contain a JSON object payload.",
                        collection,
                        objectId);
                }
            }
            catch (JsonException)
            {
                Add(
                    ProfileHostMigrationBlockerCodes.InvalidPayload,
                    $"Migration object '{collection}/{objectId}' has invalid payload JSON.",
                    collection,
                    objectId);
            }
        }

        var records = manifest.Records ?? Array.Empty<CompanyMigrationSourceRecord>();
        foreach (var record in records)
        {
            var expected = CompanyMigrationInventoryBuilder.ComputePayloadContentHash(
                record.PayloadJson ?? string.Empty);
            if (!string.Equals(
                    record.ContentHash,
                    expected,
                    StringComparison.OrdinalIgnoreCase))
            {
                Add(
                    "source_record_hash_mismatch",
                    $"Preserved source record '{record.DatabaseName}/{record.StoreName}/{record.RecordId}' failed its content hash.",
                    record.TransferCollection,
                    record.RecordId);
            }
        }

        try
        {
            var integrity = CompanyMigrationInventoryBuilder.ComputeBundleIntegrity(bundle);
            if (!string.Equals(
                    manifest.SourceContentHash,
                    integrity.SourceContentHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                Add(
                    "source_hash_mismatch",
                    "The migration bundle source content hash does not match its preserved records.");
            }
            if (!string.Equals(
                    bundle.ContentHash,
                    integrity.ContentHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                Add(
                    "content_hash_mismatch",
                    "The migration bundle content hash does not match its source records and transfer objects.");
            }
            if (bundle.MigrationId != integrity.MigrationId)
            {
                Add(
                    "migration_id_mismatch",
                    "The migration ID does not match the bundle content.");
            }

            var declaredStoreHashes = manifest.StoreContentHashes ??
                new Dictionary<string, string>();
            if (declaredStoreHashes.Count != integrity.StoreContentHashes.Count ||
                integrity.StoreContentHashes.Any(expected =>
                    !declaredStoreHashes.TryGetValue(expected.Key, out var actual) ||
                    !string.Equals(actual, expected.Value, StringComparison.OrdinalIgnoreCase)))
            {
                Add(
                    "store_hash_mismatch",
                    "One or more preserved source store hashes do not match their records.");
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException)
        {
            Add(
                "bundle_integrity_unavailable",
                $"The migration bundle integrity could not be recomputed: {exception.Message}");
        }

        return new CompanyMigrationBundleValidationResult
        {
            Bundle = bundle,
            Blockers = blockers
        };

        void Add(
            string code,
            string message,
            string? collection = null,
            string? objectId = null)
        {
            blockers.Add(new CompanyMigrationSourceBlocker
            {
                Code = code,
                Message = message,
                Collection = string.IsNullOrWhiteSpace(collection) ? null : collection,
                ObjectId = string.IsNullOrWhiteSpace(objectId) ? null : objectId
            });
        }
    }

    public CompanyMigrationBundleCombinationResult CombineBundles(
        params CompanyMigrationExportBundle[] bundles) =>
        CombineBundles((IReadOnlyList<CompanyMigrationExportBundle>)bundles);

    public CompanyMigrationBundleCombinationResult CombineBundles(
        IReadOnlyList<CompanyMigrationExportBundle> bundles)
    {
        ArgumentNullException.ThrowIfNull(bundles);
        if (bundles.Count == 0)
        {
            return new CompanyMigrationBundleCombinationResult
            {
                Blockers =
                [
                    new CompanyMigrationSourceBlocker
                    {
                        Code = "bundle_sources_empty",
                        Message = "At least one migration bundle is required."
                    }
                ]
            };
        }

        var validationBlockers = bundles
            .SelectMany(bundle => ValidateBundle(bundle).Blockers)
            .ToArray();
        if (validationBlockers.Length > 0)
        {
            return new CompanyMigrationBundleCombinationResult
            {
                SourceBundles = bundles,
                Blockers = validationBlockers
            };
        }

        var orderedSources = bundles
            .OrderBy(bundle => bundle.ContentHash, StringComparer.Ordinal)
            .ToArray();
        var selectedObjects = new Dictionary<
            string,
            ProfileHostMigrationObjectInput>(StringComparer.Ordinal);
        var blockers = new List<CompanyMigrationSourceBlocker>();
        foreach (var source in orderedSources)
        {
            foreach (var item in source.Objects)
            {
                var identity = BuildObjectIdentity(item.Collection, item.ObjectId);
                if (!selectedObjects.TryGetValue(identity, out var existing))
                {
                    selectedObjects[identity] = CloneObject(item);
                    continue;
                }

                var existingHash =
                    CompanyMigrationInventoryBuilder.ComputeCanonicalPayloadContentHash(
                        existing.PayloadJson);
                var incomingHash =
                    CompanyMigrationInventoryBuilder.ComputeCanonicalPayloadContentHash(
                        item.PayloadJson);
                if (!string.Equals(
                        existingHash,
                        incomingHash,
                        StringComparison.Ordinal))
                {
                    blockers.Add(new CompanyMigrationSourceBlocker
                    {
                        Code = "divergent_bundle_object",
                        Message =
                            $"Migration object '{item.Collection}/{item.ObjectId}' differs between source bundles; explicit source selection is required.",
                        Collection = item.Collection,
                        ObjectId = item.ObjectId
                    });
                }
            }
        }

        if (blockers.Count > 0)
        {
            return new CompanyMigrationBundleCombinationResult
            {
                SourceBundles = orderedSources,
                Blockers = blockers
            };
        }

        var objects = selectedObjects.Values
            .OrderBy(item => item.Collection, StringComparer.Ordinal)
            .ThenBy(item => item.ObjectId, StringComparer.Ordinal)
            .ToArray();
        var records = orderedSources
            .SelectMany((source, sourceIndex) =>
                (source.Manifest.Records ?? Array.Empty<CompanyMigrationSourceRecord>())
                .Select(record => CloneRecord(record, sourceIndex)))
            .ToArray();
        var dangling = CompanyMigrationInventoryBuilder.FindDanglingReferences(objects);
        var sourceBlockers = orderedSources
            .SelectMany(source =>
                source.Manifest.Blockers ?? Array.Empty<CompanyMigrationSourceBlocker>())
            .Where(blocker =>
                !string.Equals(
                    blocker.Code,
                    "dangling_reference",
                    StringComparison.Ordinal))
            .Concat(dangling.Select(reference => new CompanyMigrationSourceBlocker
            {
                Code = "dangling_reference",
                Message =
                    $"{reference.Collection}/{reference.ObjectId} references missing {reference.ReferencedCollection}/{reference.ReferencedObjectId}.",
                Collection = reference.Collection,
                ObjectId = reference.ObjectId
            }))
            .DistinctBy(blocker =>
                $"{blocker.Code}\0{blocker.Collection}\0{blocker.ObjectId}\0{blocker.Message}")
            .ToArray();
        var installationId = HashLines(
            orderedSources.Select(source =>
                $"{source.Manifest.Source.InstallationId}/{source.ContentHash}"));
        var specializedStorage = JsonSerializer.SerializeToElement(
            new
            {
                combined = true,
                sources = orderedSources.Select(source => new
                {
                    source.MigrationId,
                    source.ContentHash,
                    source.Manifest.SourceContentHash,
                    source.Manifest.Source.InstallationId,
                    source.Manifest.Source.Origin
                }).ToArray()
            },
            JsonOptions);
        var sourceMetadata = new CompanyMigrationSourceMetadata
        {
            Origin = "combined migration bundles",
            InstallationId = installationId,
            CapturedAtUtc = orderedSources
                .Select(source => source.Manifest.Source.CapturedAtUtc)
                .DefaultIfEmpty(DateTime.MinValue)
                .Max(),
            IndexedDbModuleRevision = orderedSources
                .Select(source => source.Manifest.Source.IndexedDbModuleRevision)
                .DefaultIfEmpty()
                .Max(),
            PersonalDatabaseExists = orderedSources.Any(source =>
                source.Manifest.Source.PersonalDatabaseExists),
            PersonalDatabaseName = "multiple",
            CompanyDatabaseExists = orderedSources.Any(source =>
                source.Manifest.Source.CompanyDatabaseExists),
            CompanyDatabaseName = "multiple",
            LegacyDatabaseExists = orderedSources.Any(source =>
                source.Manifest.Source.LegacyDatabaseExists),
            LegacyDatabaseName = "multiple"
        };
        var initial = new CompanyMigrationExportBundle
        {
            ExportedAtUtc = DateTime.UtcNow,
            Objects = objects,
            Manifest = new CompanyMigrationLocalManifest
            {
                Source = sourceMetadata,
                SpecializedStorage = specializedStorage,
                Records = records,
                Companies = CompanyMigrationInventoryBuilder.SummarizeCompanies(objects),
                DanglingReferences = dangling,
                Blockers = sourceBlockers,
                Counts = CompanyMigrationInventoryBuilder.CreateCounts(
                    records,
                    objects,
                    dangling,
                    sourceBlockers)
            }
        };
        var integrity = CompanyMigrationInventoryBuilder.ComputeBundleIntegrity(initial);
        var combined = new CompanyMigrationExportBundle
        {
            ExportedAtUtc = initial.ExportedAtUtc,
            MigrationId = integrity.MigrationId,
            ContentHash = integrity.ContentHash,
            Objects = objects,
            Manifest = new CompanyMigrationLocalManifest
            {
                Source = sourceMetadata,
                SpecializedStorage = specializedStorage,
                Records = records,
                Companies = initial.Manifest.Companies,
                DanglingReferences = dangling,
                Blockers = sourceBlockers,
                Counts = initial.Manifest.Counts,
                StoreContentHashes = integrity.StoreContentHashes,
                SourceContentHash = integrity.SourceContentHash
            }
        };
        return new CompanyMigrationBundleCombinationResult
        {
            Bundle = combined,
            SourceBundles = orderedSources,
            Blockers = sourceBlockers
        };
    }

    public CompanyMigrationRecoveryArchive CreateRecoveryArchive(
        IReadOnlyList<CompanyMigrationExportBundle> sourceBundles,
        ProfileHostBootstrapPayload destinationBootstrap,
        ProfileHostMigrationPreflightRequest? preflightRequest = null,
        ProfileHostMigrationPreflightResponse? preflightResponse = null,
        ProfileHostMigrationCommitRequest? commitRequest = null,
        ProfileHostMigrationCommitResponse? receipt = null,
        DateTime? exportedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(sourceBundles);
        ArgumentNullException.ThrowIfNull(destinationBootstrap);
        if (sourceBundles.Count == 0)
        {
            throw new ArgumentException(
                "At least one source migration bundle is required.",
                nameof(sourceBundles));
        }

        var invalid = sourceBundles
            .SelectMany(source => ValidateBundle(source).Blockers)
            .ToArray();
        if (invalid.Length > 0)
        {
            throw new InvalidDataException(
                $"A source migration bundle is invalid: {invalid[0].Message}");
        }

        return new CompanyMigrationRecoveryArchive
        {
            ExportedAtUtc = exportedAtUtc ?? DateTime.UtcNow,
            SourceBundles = sourceBundles.ToArray(),
            DestinationBootstrap = SanitizeBootstrap(destinationBootstrap),
            PreflightRequest = preflightRequest is null
                ? null
                : CreateRequestMetadata(
                    "preflight",
                    preflightRequest.MigrationId,
                    null,
                    preflightResponse?.RequestHash,
                    preflightRequest.Objects,
                    preflightRequest.Resolutions,
                    preflightRequest.Mappings),
            PreflightResponse = preflightResponse,
            CommitRequest = commitRequest is null
                ? null
                : CreateRequestMetadata(
                    "commit",
                    commitRequest.MigrationId,
                    commitRequest.PreflightHash,
                    receipt?.RequestHash ?? preflightResponse?.RequestHash,
                    commitRequest.Objects,
                    commitRequest.Resolutions,
                    commitRequest.Mappings),
            Receipt = receipt
        };
    }

    public string SerializeRecoveryArchive(CompanyMigrationRecoveryArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        return JsonSerializer.Serialize(archive, JsonOptions);
    }

    public async Task<BrowserFileSaveResult> ExportRecoveryArchiveAsync(
        CompanyMigrationRecoveryArchive archive,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archive);
        var fileName =
            $"craft-architect-company-migration-recovery-{archive.ExportedAtUtc:yyyyMMdd-HHmmss}.json";
        return await _fileExport.SaveTextFileAsync(
            RecoverySaveKey,
            fileName,
            SerializeRecoveryArchive(archive),
            "application/json",
            cancellationToken);
    }

    private static CompanyMigrationBundleValidationResult Invalid(
        string code,
        string message) =>
        new()
        {
            Blockers =
            [
                new CompanyMigrationSourceBlocker
                {
                    Code = code,
                    Message = message
                }
            ]
        };

    private static ProfileHostMigrationObjectInput CloneObject(
        ProfileHostMigrationObjectInput item) =>
        new()
        {
            Collection = item.Collection,
            ObjectId = item.ObjectId,
            PayloadJson = item.PayloadJson
        };

    private static CompanyMigrationSourceRecord CloneRecord(
        CompanyMigrationSourceRecord record,
        int sourceIndex) =>
        new()
        {
            DatabaseRole = $"source-{sourceIndex + 1}:{record.DatabaseRole}",
            DatabaseName = record.DatabaseName,
            StoreName = record.StoreName,
            RecordId = record.RecordId,
            TransferCollection = record.TransferCollection,
            PayloadJson = record.PayloadJson,
            ContentHash = record.ContentHash,
            Supported = record.Supported,
            RequiredBySource = record.RequiredBySource
        };

    private static string BuildObjectIdentity(string collection, string objectId)
    {
        collection = collection.Trim();
        objectId = objectId.Trim();
        var normalizedId = Guid.TryParse(objectId, out var parsed) &&
                           parsed != Guid.Empty
            ? parsed.ToString("D")
            : objectId;
        return $"{collection}\0{normalizedId}";
    }

    private static bool IsSecretBearingSetting(string collection, string objectId)
    {
        if (!string.Equals(
                collection,
                ProfileSyncCollections.Settings,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (ProfileSyncSettingsKeys.ConnectionSettingKeys.Contains(objectId))
        {
            return true;
        }

        var normalized = objectId
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

    private static ProfileHostBootstrapPayload SanitizeBootstrap(
        ProfileHostBootstrapPayload bootstrap) =>
        new()
        {
            Objects = (bootstrap.Objects ?? Array.Empty<ProfileSyncObjectEnvelope>())
                .Where(item => !IsSecretBearingSetting(item.Collection, item.ObjectId))
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

    private static CompanyMigrationRecoveryRequestMetadata CreateRequestMetadata(
        string requestKind,
        Guid migrationId,
        string? preflightHash,
        string? requestHash,
        IReadOnlyList<ProfileHostMigrationObjectInput>? objects,
        IReadOnlyList<ProfileHostMigrationResolution>? resolutions,
        IReadOnlyList<ProfileHostMigrationCanonicalMapping>? mappings) =>
        new()
        {
            RequestKind = requestKind,
            MigrationId = migrationId,
            RequestHash = requestHash,
            PreflightHash = preflightHash,
            Objects = (objects ?? Array.Empty<ProfileHostMigrationObjectInput>())
                .OrderBy(item => item.Collection, StringComparer.Ordinal)
                .ThenBy(item => item.ObjectId, StringComparer.Ordinal)
                .Select(item => new CompanyMigrationRecoveryObjectMetadata
                {
                    Collection = item.Collection,
                    ObjectId = item.ObjectId,
                    PayloadContentHash =
                        CompanyMigrationInventoryBuilder.ComputePayloadContentHash(
                            item.PayloadJson)
                })
                .ToArray(),
            Resolutions = (resolutions ??
                           Array.Empty<ProfileHostMigrationResolution>())
                .Select(item => new ProfileHostMigrationResolution
                {
                    Collection = item.Collection,
                    ObjectId = item.ObjectId,
                    Resolution = item.Resolution
                })
                .ToArray(),
            Mappings = (mappings ??
                        Array.Empty<ProfileHostMigrationCanonicalMapping>())
                .Select(item => new ProfileHostMigrationCanonicalMapping
                {
                    Collection = item.Collection,
                    SourceObjectId = item.SourceObjectId,
                    TargetObjectId = item.TargetObjectId
                })
                .ToArray()
        };

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } &&
        value.All(character =>
            character is >= '0' and <= '9' ||
            character is >= 'a' and <= 'f' ||
            character is >= 'A' and <= 'F');

    private static string HashLines(IEnumerable<string> lines) =>
        Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(string.Join('\n', lines))))
            .ToLowerInvariant();
}
