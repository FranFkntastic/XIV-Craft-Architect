namespace FFXIV_Craft_Architect.Core.Models;

public static class ProfileHostMigrationCollections
{
    public const string TradeOrderCraftSnapshots = "tradeOrderCraftSnapshots";
}

public static class ProfileHostMigrationBlockerCodes
{
    public const string InvalidMigrationId = "invalid_migration_id";
    public const string EmptyMigration = "empty_migration";
    public const string DuplicateObjectIdentity = "duplicate_object_identity";
    public const string DuplicateAuthoritativeIdentity = "duplicate_authoritative_identity";
    public const string InvalidCanonicalMapping = "invalid_canonical_mapping";
    public const string CanonicalTargetConflict = "canonical_target_conflict";
    public const string UnsupportedCollection = "unsupported_collection";
    public const string UnsupportedOrderCraftSnapshot = "unsupported_order_craft_snapshot";
    public const string InvalidPayload = "invalid_payload";
    public const string ObjectIdentityMismatch = "object_identity_mismatch";
    public const string ResolutionRequired = "resolution_required";
    public const string InvalidResolution = "invalid_resolution";
    public const string UnexpectedResolution = "unexpected_resolution";
    public const string MissingCompany = "missing_company";
    public const string MissingCrafter = "missing_crafter";
    public const string MissingOrder = "missing_order";
    public const string MissingPayrollDraft = "missing_payroll_draft";
    public const string MissingPlan = "missing_plan";
    public const string CompanyReferenceMismatch = "company_reference_mismatch";
    public const string OrderReferenceMismatch = "order_reference_mismatch";
    public const string PlanReferenceMismatch = "plan_reference_mismatch";
    public const string DuplicatePayrollLink = "duplicate_payroll_link";
    public const string PublishedOrderRemapRequiresReissue =
        "published_order_remap_requires_reissue";
    public const string PreflightChanged = "preflight_changed";
    public const string MigrationIdConflict = "migration_id_conflict";
}

public enum ProfileHostMigrationObjectDisposition
{
    Insert,
    Identical,
    SameIdDifferentContent,
    AuthoritativeTombstone
}

public enum ProfileHostMigrationConflictResolution
{
    KeepAuthoritative,
    UseIncoming,
    ResurrectIncoming,
    KeepBothAsCopy
}

public sealed class ProfileHostMigrationObjectInput
{
    public string Collection { get; set; } = string.Empty;
    public string ObjectId { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
}

public sealed class ProfileHostMigrationResolution
{
    public string Collection { get; set; } = string.Empty;
    public string ObjectId { get; set; } = string.Empty;
    public ProfileHostMigrationConflictResolution Resolution { get; set; }
}

public sealed class ProfileHostMigrationCanonicalMapping
{
    public string Collection { get; set; } = string.Empty;
    public string SourceObjectId { get; set; } = string.Empty;
    public string TargetObjectId { get; set; } = string.Empty;
}

public sealed class ProfileHostMigrationPreflightRequest
{
    public Guid MigrationId { get; set; }
    public IReadOnlyList<ProfileHostMigrationObjectInput> Objects { get; set; } =
        Array.Empty<ProfileHostMigrationObjectInput>();
    public IReadOnlyList<ProfileHostMigrationResolution> Resolutions { get; set; } =
        Array.Empty<ProfileHostMigrationResolution>();
    public IReadOnlyList<ProfileHostMigrationCanonicalMapping> Mappings { get; set; } =
        Array.Empty<ProfileHostMigrationCanonicalMapping>();
}

public sealed class ProfileHostMigrationCommitRequest
{
    public Guid MigrationId { get; set; }
    public string PreflightHash { get; set; } = string.Empty;
    public IReadOnlyList<ProfileHostMigrationObjectInput> Objects { get; set; } =
        Array.Empty<ProfileHostMigrationObjectInput>();
    public IReadOnlyList<ProfileHostMigrationResolution> Resolutions { get; set; } =
        Array.Empty<ProfileHostMigrationResolution>();
    public IReadOnlyList<ProfileHostMigrationCanonicalMapping> Mappings { get; set; } =
        Array.Empty<ProfileHostMigrationCanonicalMapping>();
}

public sealed class ProfileHostMigrationObjectAssessment
{
    public string Collection { get; set; } = string.Empty;
    public string ObjectId { get; set; } = string.Empty;
    public string CanonicalObjectId { get; set; } = string.Empty;
    public ProfileHostMigrationObjectDisposition Disposition { get; set; }
    public ProfileHostMigrationConflictResolution? Resolution { get; set; }
    public long? AuthoritativeRevision { get; set; }
    public bool AuthoritativeDeleted { get; set; }
    public DateTime? AuthoritativeDeletedAtUtc { get; set; }
    public bool RetiresAuthoritativeSource { get; set; }
    public long? AuthoritativeSourceRevision { get; set; }
    public bool AuthoritativeSourceDeleted { get; set; }
    public DateTime? AuthoritativeSourceDeletedAtUtc { get; set; }
    public string? AuthoritativeSourceContentHash { get; set; }
    public string IncomingContentHash { get; set; } = string.Empty;
    public string? AuthoritativeContentHash { get; set; }
}

public sealed class ProfileHostMigrationBlocker
{
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? Collection { get; set; }
    public string? ObjectId { get; set; }
    public string? ReferencedCollection { get; set; }
    public string? ReferencedObjectId { get; set; }
}

public sealed class ProfileHostMigrationPreflightResponse
{
    public Guid MigrationId { get; set; }
    public string RequestHash { get; set; } = string.Empty;
    public string PreflightHash { get; set; } = string.Empty;
    public bool CanCommit { get; set; }
    public IReadOnlyList<ProfileHostMigrationObjectAssessment> Objects { get; set; } =
        Array.Empty<ProfileHostMigrationObjectAssessment>();
    public IReadOnlyList<ProfileHostMigrationCanonicalMapping> Mappings { get; set; } =
        Array.Empty<ProfileHostMigrationCanonicalMapping>();
    public IReadOnlyList<ProfileHostMigrationBlocker> Blockers { get; set; } =
        Array.Empty<ProfileHostMigrationBlocker>();
}

public sealed class ProfileHostMigrationAuthoritativeObject
{
    public string Collection { get; set; } = string.Empty;
    public string SourceObjectId { get; set; } = string.Empty;
    public string ObjectId { get; set; } = string.Empty;
    public long Revision { get; set; }
    public bool Deleted { get; set; }
}

public sealed class ProfileHostMigrationCommitResponse
{
    public Guid MigrationId { get; set; }
    public string RequestHash { get; set; } = string.Empty;
    public string ReceiptHash { get; set; } = string.Empty;
    public long ServerRevision { get; set; }
    public IReadOnlyList<ProfileHostMigrationAuthoritativeObject> Objects { get; set; } =
        Array.Empty<ProfileHostMigrationAuthoritativeObject>();
    public IReadOnlyList<ProfileHostMigrationCanonicalMapping> Mappings { get; set; } =
        Array.Empty<ProfileHostMigrationCanonicalMapping>();
    public IReadOnlyList<ProfileHostMigrationRetiredSource> RetiredSources { get; set; } =
        Array.Empty<ProfileHostMigrationRetiredSource>();
}

public sealed class ProfileHostMigrationRetiredSource
{
    public string Collection { get; set; } = string.Empty;
    public string ObjectId { get; set; } = string.Empty;
    public long Revision { get; set; }
    public bool Deleted { get; set; }
}
