namespace FFXIV_Craft_Architect.Core.Models;

public sealed record CommissionBriefOutput(
    int ItemId,
    string Name,
    int Quantity,
    bool MustBeHq);

public sealed record CommissionBriefMaterial(
    int ItemId,
    string Name,
    int Quantity,
    bool RequiresHq,
    decimal UnitCost = 0,
    decimal TotalCost = 0);

public sealed record CommissionBriefPayment(
    string ContractLabel,
    decimal MaterialReimbursement,
    decimal MaterialBonus,
    decimal CraftLabor,
    decimal Total,
    decimal MaterialAdjustmentPercent = 0,
    int CraftSynthCount = 0,
    decimal GilPerSynth = 0);

public sealed record CommissionBriefEvidence(
    string CostBasis,
    string MarketScope,
    string Location,
    DateTime CapturedAtUtc);

public sealed class CommissionBriefDocument
{
    public bool IsTestFixture { get; set; }
    public string CompanyName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string StatusLabel { get; set; } = "Open for assignment";
    public string AssignmentLabel { get; set; } = "Contact operator";
    public string Reference { get; set; } = string.Empty;
    public string Contact { get; set; } = string.Empty;
    public string DeliveryInstructions { get; set; } = string.Empty;
    public IReadOnlyList<CommissionBriefOutput> Outputs { get; set; } = Array.Empty<CommissionBriefOutput>();
    public IReadOnlyList<CommissionBriefMaterial> CrafterMaterials { get; set; } = Array.Empty<CommissionBriefMaterial>();
    public IReadOnlyList<CommissionBriefMaterial> CompanyMaterials { get; set; } = Array.Empty<CommissionBriefMaterial>();
    public CommissionBriefPayment Payment { get; set; } = new("Commission", 0, 0, 0, 0);
    public CommissionBriefEvidence Evidence { get; set; } = new(
        "Selected acquisition sources",
        "Current market scope",
        "Unspecified",
        DateTime.UtcNow);
}

public sealed class CommissionBriefCreateRequest
{
    public CommissionBriefDocument Brief { get; set; } = new();
    public TradeCompanyPublicationOwnership? Ownership { get; set; }
}

public sealed class CompanyCommissionBriefCreateRequest
{
    public Guid OrderId { get; set; }
    public CompanyRecordRevision OrderRevision { get; set; }
    public CommissionBriefDocument Brief { get; set; } = new();
    public string IdempotencyKey { get; set; } = string.Empty;
}

public sealed class CommissionBriefCreateResponse
{
    public string PublicId { get; set; } = string.Empty;
    public string PublicUrl { get; set; } = string.Empty;
    public string? ClaimUrl { get; set; }
    public string EditorToken { get; set; } = string.Empty;
    public int Version { get; set; }
    public DateTime PublishedAtUtc { get; set; }
    public TradeCompanyRecordEnvelope? OrderRecord { get; set; }
}

public sealed class CommissionBriefLinkResponse
{
    public string PublicId { get; set; } = string.Empty;
    public string PublicUrl { get; set; } = string.Empty;
    public int Version { get; set; }
    public DateTime PublishedAtUtc { get; set; }
}

public sealed class PublishedCommissionBrief
{
    public string PublicId { get; set; } = string.Empty;
    public int Version { get; set; }
    public DateTime PublishedAtUtc { get; set; }
    public CommissionBriefDocument Brief { get; set; } = new();
    public TradeCompanyPublicationOwnership? Ownership { get; set; }
}

public sealed class TradeCommissionPublication
{
    public string PublicId { get; set; } = string.Empty;
    public string? PublicUrl { get; set; }
    public int Version { get; set; }
    public DateTime PublishedAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public bool IsTestFixture { get; set; }
    public TradeCompanyPublicationOwnership? Ownership { get; set; }
}
