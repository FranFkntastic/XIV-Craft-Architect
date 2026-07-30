using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services.CompanyMigration;

public sealed class CompanyMigrationExportBundle
{
    public const int CurrentFormatVersion = 1;
    public const string PackageKindValue =
        "ffxiv-craft-architect.company-migration-inventory";

    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public string PackageKind { get; init; } = PackageKindValue;
    public DateTime ExportedAtUtc { get; init; }
    public Guid MigrationId { get; init; }
    public string ContentHash { get; init; } = string.Empty;
    public CompanyMigrationLocalManifest Manifest { get; init; } = new();
    public IReadOnlyList<ProfileHostMigrationObjectInput> Objects { get; init; } =
        Array.Empty<ProfileHostMigrationObjectInput>();
}

public sealed class CompanyMigrationLocalManifest
{
    public CompanyMigrationSourceMetadata Source { get; init; } = new();
    public JsonElement SpecializedStorage { get; init; }
    public IReadOnlyList<CompanyMigrationSourceRecord> Records { get; init; } =
        Array.Empty<CompanyMigrationSourceRecord>();
    public IReadOnlyList<CompanyMigrationCompanySummary> Companies { get; init; } =
        Array.Empty<CompanyMigrationCompanySummary>();
    public IReadOnlyList<CompanyMigrationDanglingReference> DanglingReferences { get; init; } =
        Array.Empty<CompanyMigrationDanglingReference>();
    public IReadOnlyList<CompanyMigrationSourceBlocker> Blockers { get; init; } =
        Array.Empty<CompanyMigrationSourceBlocker>();
    public IReadOnlyDictionary<string, int> Counts { get; init; } =
        new Dictionary<string, int>();
    public IReadOnlyDictionary<string, string> StoreContentHashes { get; init; } =
        new Dictionary<string, string>();
    public string SourceContentHash { get; init; } = string.Empty;
    public bool CanPreflight => Blockers.All(blocker => blocker.IsArchiveOnly);
    public bool RequiresRecoveryArchive => Blockers.Any(blocker => blocker.IsArchiveOnly);
}

public sealed class CompanyMigrationSourceMetadata
{
    public string? Origin { get; init; }
    public string InstallationId { get; init; } = string.Empty;
    public DateTime CapturedAtUtc { get; init; }
    public int IndexedDbModuleRevision { get; init; }
    public bool PersonalDatabaseExists { get; init; }
    public string PersonalDatabaseName { get; init; } = string.Empty;
    public int? PersonalSchemaVersion { get; init; }
    public bool CompanyDatabaseExists { get; init; }
    public string CompanyDatabaseName { get; init; } = string.Empty;
    public int? CompanySchemaVersion { get; init; }
    public bool LegacyDatabaseExists { get; init; }
    public string LegacyDatabaseName { get; init; } = string.Empty;
    public int? LegacySchemaVersion { get; init; }
}

public sealed class CompanyMigrationSourceRecord
{
    public string DatabaseRole { get; init; } = string.Empty;
    public string DatabaseName { get; init; } = string.Empty;
    public string StoreName { get; init; } = string.Empty;
    public string RecordId { get; init; } = string.Empty;
    public string? TransferCollection { get; init; }
    public string PayloadJson { get; init; } = "{}";
    public string ContentHash { get; init; } = string.Empty;
    public bool Supported { get; init; }
    public bool RequiredBySource { get; init; }
}

public sealed class CompanyMigrationCompanySummary
{
    public string CompanyId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public int CrafterCount { get; init; }
    public int OrderCount { get; init; }
    public int PayrollDraftCount { get; init; }
    public int OrderCraftSnapshotCount { get; init; }
    public int LinkedPlanReferenceCount { get; init; }
    public string ContentHash { get; init; } = string.Empty;
}

public sealed class CompanyMigrationDanglingReference
{
    public string Collection { get; init; } = string.Empty;
    public string ObjectId { get; init; } = string.Empty;
    public string ReferencedCollection { get; init; } = string.Empty;
    public string ReferencedObjectId { get; init; } = string.Empty;
}

public sealed class CompanyMigrationSourceBlocker
{
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string? DatabaseRole { get; init; }
    public string? StoreName { get; init; }
    public string? Collection { get; init; }
    public string? ObjectId { get; init; }

    public bool IsArchiveOnly =>
        Code is ProfileHostMigrationBlockerCodes.UnsupportedOrderCraftSnapshot or
            "unsupported_company_store" or
            "unsupported_legacy_store" or
            "linked_plan_candidate_unreadable" ||
        Code == "divergent_source_copy" &&
        Collection != ProfileSyncCollections.Plans;
}

public sealed class CompanyMigrationBundleIntegrity
{
    public string SourceContentHash { get; init; } = string.Empty;
    public string TransferContentHash { get; init; } = string.Empty;
    public string ContentHash { get; init; } = string.Empty;
    public Guid MigrationId { get; init; }
    public IReadOnlyDictionary<string, string> RecordContentHashes { get; init; } =
        new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> StoreContentHashes { get; init; } =
        new Dictionary<string, string>();
}

internal sealed class BrowserCompanyMigrationSource
{
    public int FormatVersion { get; set; }
    public DateTime CapturedAtUtc { get; set; }
    public string? Origin { get; set; }
    public int ModuleRevision { get; set; }
    public JsonElement SpecializedStorage { get; set; }
    public BrowserCompanyDatabase Company { get; set; } = new();
    public BrowserPersonalDatabase Personal { get; set; } = new();
    public IReadOnlyList<BrowserLinkedPlan> LinkedPlans { get; set; } =
        Array.Empty<BrowserLinkedPlan>();
    public BrowserLegacyDatabase Legacy { get; set; } = new();
}

internal class BrowserCompanyDatabase
{
    public string DatabaseName { get; set; } = string.Empty;
    public bool Exists { get; set; }
    public int? SchemaVersion { get; set; }
    public IReadOnlyList<JsonElement> CompanyProfiles { get; set; } = [];
    public IReadOnlyList<JsonElement> Crafters { get; set; } = [];
    public IReadOnlyList<JsonElement> Orders { get; set; } = [];
    public IReadOnlyList<JsonElement> OrderCraftSnapshots { get; set; } = [];
    public IReadOnlyList<JsonElement> PayrollDrafts { get; set; } = [];
    public IReadOnlyList<BrowserUnsupportedStore> UnsupportedStores { get; set; } = [];
}

internal sealed class BrowserPersonalDatabase
{
    public string DatabaseName { get; set; } = string.Empty;
    public bool Exists { get; set; }
    public int? SchemaVersion { get; set; }
    public IReadOnlyList<JsonElement> LinkedPlans { get; set; } = [];
    public IReadOnlyList<JsonElement> LinkedPlanComponents { get; set; } = [];
}

internal sealed class BrowserLegacyDatabase
{
    public string DatabaseName { get; set; } = string.Empty;
    public bool Exists { get; set; }
    public int? SchemaVersion { get; set; }
    public IReadOnlyList<JsonElement> CompanyProfiles { get; set; } = [];
    public IReadOnlyList<JsonElement> Crafters { get; set; } = [];
    public IReadOnlyList<JsonElement> Orders { get; set; } = [];
    public IReadOnlyList<JsonElement> OrderCraftSnapshots { get; set; } = [];
    public IReadOnlyList<JsonElement> PayrollDrafts { get; set; } = [];
    public IReadOnlyList<JsonElement> LinkedPlans { get; set; } = [];
    public IReadOnlyList<JsonElement> LinkedPlanComponents { get; set; } = [];
    public IReadOnlyList<BrowserUnsupportedStore> UnsupportedStores { get; set; } = [];
}

internal sealed class BrowserLinkedPlan
{
    public string DatabaseRole { get; set; } = string.Empty;
    public string PlanId { get; set; } = string.Empty;
    public bool RequiredBySource { get; set; }
    public JsonElement Payload { get; set; }
    public string? Error { get; set; }
}

internal sealed class BrowserUnsupportedStore
{
    public string StoreName { get; set; } = string.Empty;
    public IReadOnlyList<JsonElement> Records { get; set; } = [];
}

public static class CompanyMigrationInventoryBuilder
{
    private const string PlansStore = "plans";
    private const string PlanComponentsStore = "planComponents";
    private const string CompanyProfilesStore = "tradeCompanyProfiles";
    private const string CraftersStore = "tradeCrafters";
    private const string OrdersStore = "tradeOrders";
    private const string OrderCraftSnapshotsStore = "tradeOrderCraftSnapshots";
    private const string PayrollDraftsStore = "tradePayrollDrafts";

    internal static CompanyMigrationExportBundle Build(
        BrowserCompanyMigrationSource source,
        DateTime exportedAtUtc)
    {
        var blockers = new List<CompanyMigrationSourceBlocker>();
        var records = Flatten(source, blockers);
        var objects = SelectTransferObjects(records, blockers);
        var dangling = FindDanglingReferences(objects);
        blockers.AddRange(dangling.Select(reference => new CompanyMigrationSourceBlocker
        {
            Code = "dangling_reference",
            Message =
                $"{reference.Collection}/{reference.ObjectId} references missing {reference.ReferencedCollection}/{reference.ReferencedObjectId}.",
            Collection = reference.Collection,
            ObjectId = reference.ObjectId
        }));

        if (source.FormatVersion != 2)
        {
            blockers.Add(new CompanyMigrationSourceBlocker
            {
                Code = "unsupported_inventory_format",
                Message = $"Browser inventory format v{source.FormatVersion} is unsupported."
            });
        }
        if (string.IsNullOrWhiteSpace(source.Origin))
        {
            blockers.Add(new CompanyMigrationSourceBlocker
            {
                Code = "source_origin_missing",
                Message = "Browser origin metadata is missing."
            });
        }
        var markerFingerprint = ReadMarkerFingerprint(source.SpecializedStorage, blockers);
        var installationId = HashLines(
        [
            "ffxiv-craft-architect-browser-installation/v1",
            source.Origin ?? "<missing-origin>",
            source.Company.DatabaseName,
            markerFingerprint
        ]);
        var integrity = ComputeIntegrity(
            CompanyMigrationExportBundle.PackageKindValue,
            installationId,
            records,
            objects);
        var companies = SummarizeCompanies(objects);
        var counts = CreateCounts(records, objects, dangling, blockers);

        return new CompanyMigrationExportBundle
        {
            ExportedAtUtc = exportedAtUtc,
            MigrationId = integrity.MigrationId,
            ContentHash = integrity.ContentHash,
            Objects = objects,
            Manifest = new CompanyMigrationLocalManifest
            {
                Source = new CompanyMigrationSourceMetadata
                {
                    Origin = source.Origin,
                    InstallationId = installationId,
                    CapturedAtUtc = source.CapturedAtUtc,
                    IndexedDbModuleRevision = source.ModuleRevision,
                    PersonalDatabaseExists = source.Personal.Exists,
                    PersonalDatabaseName = source.Personal.DatabaseName,
                    PersonalSchemaVersion = source.Personal.SchemaVersion,
                    CompanyDatabaseExists = source.Company.Exists,
                    CompanyDatabaseName = source.Company.DatabaseName,
                    CompanySchemaVersion = source.Company.SchemaVersion,
                    LegacyDatabaseExists = source.Legacy.Exists,
                    LegacyDatabaseName = source.Legacy.DatabaseName,
                    LegacySchemaVersion = source.Legacy.SchemaVersion
                },
                SpecializedStorage = source.SpecializedStorage.Clone(),
                Records = records,
                Companies = companies,
                DanglingReferences = dangling,
                Blockers = blockers,
                Counts = counts,
                StoreContentHashes = integrity.StoreContentHashes,
                SourceContentHash = integrity.SourceContentHash
            }
        };
    }

    public static CompanyMigrationBundleIntegrity ComputeBundleIntegrity(
        CompanyMigrationExportBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var manifest = bundle.Manifest ??
            throw new ArgumentException("The migration bundle has no manifest.", nameof(bundle));
        return ComputeIntegrity(
            bundle.PackageKind,
            manifest.Source?.InstallationId ?? string.Empty,
            manifest.Records ?? Array.Empty<CompanyMigrationSourceRecord>(),
            bundle.Objects ?? Array.Empty<ProfileHostMigrationObjectInput>());
    }

    public static string ComputePayloadContentHash(string payloadJson)
    {
        ArgumentNullException.ThrowIfNull(payloadJson);
        return HashText(payloadJson);
    }

    public static string ComputeCanonicalPayloadContentHash(string payloadJson)
    {
        ArgumentNullException.ThrowIfNull(payloadJson);
        using var document = JsonDocument.Parse(payloadJson);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonicalJson(writer, document.RootElement);
        }
        return HashText(Encoding.UTF8.GetString(stream.ToArray()));
    }

    public static IReadOnlyList<CompanyMigrationDanglingReference> FindDanglingReferences(
        IReadOnlyList<ProfileHostMigrationObjectInput> objects)
    {
        ArgumentNullException.ThrowIfNull(objects);
        return FindDanglingReferencesCore(objects);
    }

    public static IReadOnlyList<CompanyMigrationCompanySummary> SummarizeCompanies(
        IReadOnlyList<ProfileHostMigrationObjectInput> objects)
    {
        ArgumentNullException.ThrowIfNull(objects);
        return SummarizeCompaniesCore(objects);
    }

    public static IReadOnlyDictionary<string, int> CreateCounts(
        IReadOnlyCollection<CompanyMigrationSourceRecord> records,
        IReadOnlyCollection<ProfileHostMigrationObjectInput> objects,
        IReadOnlyCollection<CompanyMigrationDanglingReference> dangling,
        IReadOnlyCollection<CompanyMigrationSourceBlocker> blockers)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(dangling);
        ArgumentNullException.ThrowIfNull(blockers);
        return new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["companies"] = objects.Count(item =>
                item.Collection == ProfileSyncCollections.TradeCompanyProfiles),
            ["crafters"] = objects.Count(item =>
                item.Collection == ProfileSyncCollections.TradeCrafters),
            ["orders"] = objects.Count(item =>
                item.Collection == ProfileSyncCollections.TradeOrders),
            ["payrollDrafts"] = objects.Count(item =>
                item.Collection == ProfileSyncCollections.TradePayrollDrafts),
            ["linkedPlans"] = objects.Count(item =>
                item.Collection == ProfileSyncCollections.Plans),
            ["orderCraftSnapshots"] = objects.Count(item =>
                item.Collection == ProfileHostMigrationCollections.TradeOrderCraftSnapshots),
            ["sourceRecords"] = records.Count,
            ["unsupportedRecords"] = records.Count(record => !record.Supported),
            ["danglingReferences"] = dangling.Count,
            ["blockers"] = blockers.Count
        };
    }

    private static CompanyMigrationBundleIntegrity ComputeIntegrity(
        string packageKind,
        string installationId,
        IReadOnlyCollection<CompanyMigrationSourceRecord> records,
        IReadOnlyCollection<ProfileHostMigrationObjectInput> objects)
    {
        var indexedRecords = records
            .Select((record, index) => new
            {
                Record = record,
                IntegrityKey = $"{BuildRecordIntegrityKey(record)}\0{index}",
                ContentHash = HashText(record.PayloadJson ?? string.Empty)
            })
            .ToArray();
        var recordHashes = indexedRecords.ToDictionary(
            item => item.IntegrityKey,
            item => item.ContentHash,
            StringComparer.Ordinal);
        var sourceHash = HashLines(indexedRecords
            .OrderBy(item => item.Record.DatabaseName, StringComparer.Ordinal)
            .ThenBy(item => item.Record.StoreName, StringComparer.Ordinal)
            .ThenBy(item => item.Record.RecordId, StringComparer.Ordinal)
            .ThenBy(item => item.IntegrityKey, StringComparer.Ordinal)
            .Select(item =>
                $"{item.Record.DatabaseName}/{item.Record.StoreName}/{item.Record.RecordId}/{item.ContentHash}"));
        var transferHash = HashLines(objects
            .OrderBy(item => item.Collection, StringComparer.Ordinal)
            .ThenBy(item => item.ObjectId, StringComparer.Ordinal)
            .Select(item =>
                $"{item.Collection}/{item.ObjectId}/{HashText(item.PayloadJson ?? string.Empty)}"));
        var contentHash = HashLines(
        [
            packageKind,
            installationId,
            sourceHash,
            transferHash
        ]);
        var storeHashes = indexedRecords
            .GroupBy(
                item => $"{item.Record.DatabaseName}/{item.Record.StoreName}",
                StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => HashLines(group
                    .OrderBy(item => item.Record.RecordId, StringComparer.Ordinal)
                    .ThenBy(item => item.IntegrityKey, StringComparer.Ordinal)
                    .Select(item =>
                        $"{item.Record.RecordId}/{item.ContentHash}")),
                StringComparer.Ordinal);
        return new CompanyMigrationBundleIntegrity
        {
            SourceContentHash = sourceHash,
            TransferContentHash = transferHash,
            ContentHash = contentHash,
            MigrationId = DeterministicGuid(contentHash),
            RecordContentHashes = recordHashes,
            StoreContentHashes = storeHashes
        };
    }

    private static string BuildRecordIntegrityKey(CompanyMigrationSourceRecord record) =>
        $"{record.DatabaseRole}\0{record.DatabaseName}\0{record.StoreName}\0{record.RecordId}";

    private static List<CompanyMigrationSourceRecord> Flatten(
        BrowserCompanyMigrationSource source,
        ICollection<CompanyMigrationSourceBlocker> blockers)
    {
        var records = new List<CompanyMigrationSourceRecord>();
        Add(records, "company", source.Company.DatabaseName, CompanyProfilesStore, ProfileSyncCollections.TradeCompanyProfiles, source.Company.CompanyProfiles);
        Add(records, "company", source.Company.DatabaseName, CraftersStore, ProfileSyncCollections.TradeCrafters, source.Company.Crafters);
        Add(records, "company", source.Company.DatabaseName, OrdersStore, ProfileSyncCollections.TradeOrders, source.Company.Orders);
        Add(records, "company", source.Company.DatabaseName, PayrollDraftsStore, ProfileSyncCollections.TradePayrollDrafts, source.Company.PayrollDrafts);
        Add(records, "company", source.Company.DatabaseName, OrderCraftSnapshotsStore, ProfileHostMigrationCollections.TradeOrderCraftSnapshots, source.Company.OrderCraftSnapshots);
        Add(records, "legacy", source.Legacy.DatabaseName, CompanyProfilesStore, ProfileSyncCollections.TradeCompanyProfiles, source.Legacy.CompanyProfiles);
        Add(records, "legacy", source.Legacy.DatabaseName, CraftersStore, ProfileSyncCollections.TradeCrafters, source.Legacy.Crafters);
        Add(records, "legacy", source.Legacy.DatabaseName, OrdersStore, ProfileSyncCollections.TradeOrders, source.Legacy.Orders);
        Add(records, "legacy", source.Legacy.DatabaseName, PayrollDraftsStore, ProfileSyncCollections.TradePayrollDrafts, source.Legacy.PayrollDrafts);
        Add(records, "legacy", source.Legacy.DatabaseName, OrderCraftSnapshotsStore, ProfileHostMigrationCollections.TradeOrderCraftSnapshots, source.Legacy.OrderCraftSnapshots);
        Add(records, "personal", source.Personal.DatabaseName, PlansStore, null, source.Personal.LinkedPlans);
        Add(records, "personal", source.Personal.DatabaseName, PlanComponentsStore, null, source.Personal.LinkedPlanComponents);
        Add(records, "legacy", source.Legacy.DatabaseName, PlansStore, null, source.Legacy.LinkedPlans);
        Add(records, "legacy", source.Legacy.DatabaseName, PlanComponentsStore, null, source.Legacy.LinkedPlanComponents);
        foreach (var snapshot in records.Where(record =>
                     record.TransferCollection ==
                     ProfileHostMigrationCollections.TradeOrderCraftSnapshots))
        {
            blockers.Add(new CompanyMigrationSourceBlocker
            {
                Code = ProfileHostMigrationBlockerCodes.UnsupportedOrderCraftSnapshot,
                Message =
                    $"Order craft snapshot '{snapshot.RecordId}' is preserved but unsupported by hosted migration.",
                DatabaseRole = snapshot.DatabaseRole,
                StoreName = snapshot.StoreName,
                Collection = snapshot.TransferCollection,
                ObjectId = snapshot.RecordId
            });
        }

        foreach (var plan in source.LinkedPlans)
        {
            if (plan.Payload.ValueKind is JsonValueKind.Object)
            {
                var databaseName = plan.DatabaseRole == "legacy"
                    ? source.Legacy.DatabaseName
                    : source.Personal.DatabaseName;
                Add(
                    records,
                    plan.DatabaseRole,
                    databaseName,
                    $"{PlansStore}.materialized",
                    ProfileSyncCollections.Plans,
                    [plan.Payload],
                    plan.PlanId,
                    requiredBySource: plan.RequiredBySource);
            }
            else
            {
                blockers.Add(new CompanyMigrationSourceBlocker
                {
                    Code = plan.RequiredBySource
                        ? "linked_plan_unavailable"
                        : "linked_plan_candidate_unreadable",
                    Message = string.IsNullOrWhiteSpace(plan.Error)
                        ? $"Linked saved plan '{plan.PlanId}' is missing from the {plan.DatabaseRole} source."
                        : $"Linked saved plan '{plan.PlanId}' from the {plan.DatabaseRole} source could not be materialized: {plan.Error}",
                    DatabaseRole = plan.DatabaseRole,
                    Collection = ProfileSyncCollections.Plans,
                    ObjectId = plan.PlanId
                });
            }
        }
        foreach (var store in source.Company.UnsupportedStores)
        {
            Add(records, "company", source.Company.DatabaseName, store.StoreName, null, store.Records, supported: false);
            blockers.Add(new CompanyMigrationSourceBlocker
            {
                Code = "unsupported_company_store",
                Message =
                    $"Unsupported company store '{store.StoreName}' has {store.Records.Count} preserved records.",
                DatabaseRole = "company",
                StoreName = store.StoreName
            });
        }
        foreach (var store in source.Legacy.UnsupportedStores)
        {
            Add(records, "legacy", source.Legacy.DatabaseName, store.StoreName, null, store.Records, supported: false);
            blockers.Add(new CompanyMigrationSourceBlocker
            {
                Code = "unsupported_legacy_store",
                Message =
                    $"Unsupported legacy store '{store.StoreName}' has {store.Records.Count} preserved records.",
                DatabaseRole = "legacy",
                StoreName = store.StoreName
            });
        }
        return records;
    }

    private static void Add(
        ICollection<CompanyMigrationSourceRecord> target,
        string role,
        string database,
        string store,
        string? collection,
        IEnumerable<JsonElement> payloads,
        string? forcedId = null,
        bool supported = true,
        bool requiredBySource = false)
    {
        foreach (var payload in payloads)
        {
            var payloadJson = payload.GetRawText();
            var hash = HashText(payloadJson);
            target.Add(new CompanyMigrationSourceRecord
            {
                DatabaseRole = role,
                DatabaseName = database,
                StoreName = store,
                RecordId = forcedId ?? GetString(payload, "id") ?? $"missing-id:{hash}",
                TransferCollection = collection,
                PayloadJson = payloadJson,
                ContentHash = hash,
                Supported = supported,
                RequiredBySource = requiredBySource
            });
        }
    }

    private static IReadOnlyList<ProfileHostMigrationObjectInput> SelectTransferObjects(
        IEnumerable<CompanyMigrationSourceRecord> records,
        ICollection<CompanyMigrationSourceBlocker> blockers)
    {
        var result = new List<ProfileHostMigrationObjectInput>();
        var blockedPlanIds = blockers
            .Where(blocker =>
                blocker.Collection == ProfileSyncCollections.Plans &&
                blocker.ObjectId != null)
            .Select(blocker => blocker.ObjectId!)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var group in records
                     .Where(record => record.Supported && record.TransferCollection != null)
                     .GroupBy(record => (
                         Collection: record.TransferCollection!,
                         RecordId: NormalizeId(
                             record.TransferCollection!,
                             record.RecordId))))
        {
            if (group.Key.Collection == ProfileSyncCollections.Plans &&
                blockedPlanIds.Contains(group.Key.RecordId))
            {
                continue;
            }
            var ordered = group
                .OrderBy(record => record.DatabaseRole == "legacy" ? 1 : 0)
                .ThenBy(record => record.DatabaseName, StringComparer.Ordinal)
                .ToArray();
            if (group.Key.RecordId.StartsWith("missing-id:", StringComparison.Ordinal))
            {
                blockers.Add(new CompanyMigrationSourceBlocker
                {
                    Code = "missing_record_id",
                    Message = $"Store '{ordered[0].StoreName}' contains a record without an ID.",
                    DatabaseRole = ordered[0].DatabaseRole,
                    StoreName = ordered[0].StoreName,
                    Collection = group.Key.Item1
                });
                continue;
            }
            if (ordered.Select(record => record.ContentHash).Distinct().Count() > 1)
            {
                blockers.Add(new CompanyMigrationSourceBlocker
                {
                    Code = "divergent_source_copy",
                    Message = group.Key.Collection == ProfileSyncCollections.Plans
                        ? $"{group.Key.Collection}/{group.Key.RecordId} differs between personal and legacy source databases; explicit source selection is required and every copy remains in the manifest."
                        : $"{group.Key.Collection}/{group.Key.RecordId} differs between preserved source databases; the specialized copy is selected and every copy remains in the manifest.",
                    Collection = group.Key.Item1,
                    ObjectId = group.Key.RecordId
                });
                if (group.Key.Collection == ProfileSyncCollections.Plans)
                {
                    continue;
                }
            }
            var selected = group.Key.Collection == ProfileSyncCollections.Plans
                ? ordered.FirstOrDefault(record =>
                      record.RequiredBySource &&
                      record.DatabaseRole == "legacy") ??
                  ordered.FirstOrDefault(record => record.RequiredBySource) ??
                  ordered[0]
                : ordered[0];
            result.Add(new ProfileHostMigrationObjectInput
            {
                Collection = group.Key.Item1,
                ObjectId = group.Key.RecordId,
                PayloadJson = selected.PayloadJson
            });
        }
        return result
            .OrderBy(item => item.Collection, StringComparer.Ordinal)
            .ThenBy(item => item.ObjectId, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<CompanyMigrationDanglingReference> FindDanglingReferencesCore(
        IReadOnlyList<ProfileHostMigrationObjectInput> objects)
    {
        var identities = objects
            .Select(item => (item.Collection, item.ObjectId))
            .ToHashSet();
        var references = new List<CompanyMigrationDanglingReference>();
        foreach (var item in objects)
        {
            using var document = JsonDocument.Parse(item.PayloadJson);
            var payload = document.RootElement;
            if (item.Collection is ProfileSyncCollections.TradeCrafters or
                ProfileSyncCollections.TradeOrders or
                ProfileSyncCollections.TradePayrollDrafts ||
                item.Collection == ProfileHostMigrationCollections.TradeOrderCraftSnapshots)
            {
                AddIfMissing(ProfileSyncCollections.TradeCompanyProfiles, NormalizeGuid(GetString(payload, "companyProfileId")));
            }
            if (item.Collection == ProfileSyncCollections.TradeOrders)
            {
                AddIfMissing(ProfileSyncCollections.TradeCrafters, NormalizeGuid(GetString(payload, "assignedCrafterId")));
                AddIfMissing(ProfileSyncCollections.TradePayrollDrafts, GetString(payload, "payrollDraftId"));
                AddIfMissing(ProfileSyncCollections.Plans, GetString(payload, "craftPlanId"));
            }
            if (item.Collection == ProfileSyncCollections.TradePayrollDrafts)
            {
                AddIfMissing(ProfileSyncCollections.TradeOrders, NormalizeGuid(GetString(payload, "orderId")));
                AddIfMissing(ProfileSyncCollections.TradeCrafters, NormalizeGuid(GetString(payload, "assignedCrafterId")));
            }
            if (item.Collection == ProfileHostMigrationCollections.TradeOrderCraftSnapshots)
            {
                AddIfMissing(ProfileSyncCollections.TradeOrders, NormalizeGuid(GetString(payload, "orderId")));
            }

            void AddIfMissing(string collection, string? objectId)
            {
                if (!string.IsNullOrWhiteSpace(objectId) &&
                    !identities.Contains((collection, objectId)))
                {
                    references.Add(new CompanyMigrationDanglingReference
                    {
                        Collection = item.Collection,
                        ObjectId = item.ObjectId,
                        ReferencedCollection = collection,
                        ReferencedObjectId = objectId
                    });
                }
            }
        }
        return references
            .DistinctBy(reference =>
                $"{reference.Collection}\0{reference.ObjectId}\0{reference.ReferencedCollection}\0{reference.ReferencedObjectId}")
            .OrderBy(reference => reference.Collection, StringComparer.Ordinal)
            .ThenBy(reference => reference.ObjectId, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<CompanyMigrationCompanySummary> SummarizeCompaniesCore(
        IReadOnlyList<ProfileHostMigrationObjectInput> objects)
    {
        var companyObjects = objects
            .Where(item => item.Collection == ProfileSyncCollections.TradeCompanyProfiles)
            .ToDictionary(item => item.ObjectId, StringComparer.Ordinal);
        var ownership = objects.ToDictionary(
            item => (item.Collection, item.ObjectId),
            item =>
            {
                using var document = JsonDocument.Parse(item.PayloadJson);
                return NormalizeGuid(GetString(document.RootElement, "companyProfileId"));
            });
        return companyObjects.Select(pair =>
        {
            using var document = JsonDocument.Parse(pair.Value.PayloadJson);
            var owned = ownership
                .Where(item => item.Value == pair.Key)
                .Select(item => item.Key)
                .ToArray();
            var planReferences = objects
                .Where(item => item.Collection == ProfileSyncCollections.TradeOrders &&
                               ownership[(item.Collection, item.ObjectId)] == pair.Key)
                .Sum(item =>
                {
                    using var order = JsonDocument.Parse(item.PayloadJson);
                    return (string.IsNullOrWhiteSpace(GetString(order.RootElement, "craftPlanId")) ? 0 : 1) +
                           (TryGet(order.RootElement, "sourceSnapshot", out var snapshot) &&
                            !string.IsNullOrWhiteSpace(GetString(snapshot, "sourcePlanId")) ? 1 : 0);
                });
            return new CompanyMigrationCompanySummary
            {
                CompanyId = pair.Key,
                Name = GetString(document.RootElement, "name") ?? string.Empty,
                CrafterCount = owned.Count(identity =>
                    identity.Collection == ProfileSyncCollections.TradeCrafters),
                OrderCount = owned.Count(identity =>
                    identity.Collection == ProfileSyncCollections.TradeOrders),
                PayrollDraftCount = owned.Count(identity =>
                    identity.Collection == ProfileSyncCollections.TradePayrollDrafts),
                OrderCraftSnapshotCount = owned.Count(identity =>
                    identity.Collection == ProfileHostMigrationCollections.TradeOrderCraftSnapshots),
                LinkedPlanReferenceCount = planReferences,
                ContentHash = HashLines(objects
                    .Where(item => item.ObjectId == pair.Key ||
                                   ownership[(item.Collection, item.ObjectId)] == pair.Key)
                    .Select(item =>
                        $"{item.Collection}/{item.ObjectId}/{HashText(item.PayloadJson)}"))
            };
        }).OrderBy(company => company.CompanyId, StringComparer.Ordinal).ToArray();
    }

    private static string ReadMarkerFingerprint(
        JsonElement diagnostics,
        ICollection<CompanyMigrationSourceBlocker> blockers)
    {
        if (!TryGet(diagnostics, "migrations", out var migrations))
        {
            blockers.Add(new CompanyMigrationSourceBlocker
            {
                Code = "migration_markers_missing",
                Message = "Specialized storage migration markers are unavailable."
            });
            return "<missing-markers>";
        }
        var parts = new List<string>();
        foreach (var domain in new[] { "personal", "company" })
        {
            if (!TryGet(migrations, domain, out var marker) ||
                marker.ValueKind == JsonValueKind.Null)
            {
                blockers.Add(new CompanyMigrationSourceBlocker
                {
                    Code = "migration_marker_missing",
                    Message = $"Specialized {domain} storage has no migration marker.",
                    DatabaseRole = domain,
                    StoreName = "storageMetadata"
                });
                parts.Add($"{domain}:<missing>");
            }
            else
            {
                parts.Add($"{domain}:{HashText(marker.GetRawText())}");
            }
        }
        return string.Join('|', parts);
    }

    private static string NormalizeId(string collection, string objectId) =>
        collection is ProfileSyncCollections.TradeCompanyProfiles or
            ProfileSyncCollections.TradeCrafters or
            ProfileSyncCollections.TradeOrders
            ? NormalizeGuid(objectId) ?? objectId
            : objectId;

    private static string? NormalizeGuid(string? value) =>
        Guid.TryParse(value, out var parsed) && parsed != Guid.Empty
            ? parsed.ToString("D")
            : null;

    private static string? GetString(JsonElement element, string property) =>
        TryGet(element, property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryGet(JsonElement element, string property, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var candidate in element.EnumerateObject())
            {
                if (string.Equals(candidate.Name, property, StringComparison.OrdinalIgnoreCase))
                {
                    value = candidate.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static string HashLines(IEnumerable<string> lines) =>
        HashText(string.Join('\n', lines));

    private static void WriteCanonicalJson(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element
                             .EnumerateObject()
                             .OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonicalJson(writer, item);
                }
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static Guid DeterministicGuid(string hash)
    {
        var bytes = Convert.FromHexString(hash)[..16];
        bytes[7] = (byte)((bytes[7] & 0x0f) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes);
    }
}
