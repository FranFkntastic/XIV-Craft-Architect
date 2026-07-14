using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;

public sealed record WorkshopHostConnectionOptions
{
    public string ApiBaseUrl { get; init; } = string.Empty;
    public string ApiKey { get; init; } = string.Empty;
}

public sealed record WorkshopHostAcquisitionBatchCreateRequest
{
    public int SchemaVersion { get; init; } = 1;
    public string IdempotencyKey { get; init; } = string.Empty;
    public string Origin { get; init; } = "CraftArchitect";
    public string TargetCharacterName { get; init; } = string.Empty;
    public string TargetWorld { get; init; } = string.Empty;
    public string Region { get; init; } = "North America";
    public string WorldMode { get; init; } = "Recommended";
    public IReadOnlyList<string> SelectedWorlds { get; init; } = [];
    public string SweepScope { get; init; } = "Region";
    public IReadOnlyList<string> SweepDataCenters { get; init; } = [];
    public int ExpiresInSeconds { get; init; } = 3600;
    public IReadOnlyList<WorkshopHostAcquisitionBatchLineCreateRequest> Lines { get; init; } = [];
}

public sealed record WorkshopHostAcquisitionBatchLineCreateRequest
{
    public uint ItemId { get; init; }
    public string? ItemName { get; init; }
    public string? ItemKind { get; init; }
    public string QuantityMode { get; init; } = "TargetQuantity";
    public uint TargetQuantity { get; init; }
    public uint MaxQuantity { get; init; }
    public string HqPolicy { get; init; } = "Either";
    public uint MaxUnitPrice { get; init; }
    public uint GilCap { get; init; }
}

public sealed record WorkshopHostAcquisitionRequestView
{
    public string Id { get; init; } = string.Empty;
    public int Revision { get; init; }
    public string Status { get; init; } = string.Empty;
    public string Origin { get; init; } = string.Empty;
    public string TargetCharacterName { get; init; } = string.Empty;
    public string TargetWorld { get; init; } = string.Empty;
    public string Region { get; init; } = string.Empty;
    public IReadOnlyList<WorkshopHostAcquisitionBatchLineView> Lines { get; init; } = [];
}

public sealed record WorkshopHostAcquisitionBatchLineView
{
    public string LineId { get; init; } = string.Empty;
    public uint ItemId { get; init; }
    public string? ItemName { get; init; }
    public uint TargetQuantity { get; init; }
    public uint MaxQuantity { get; init; }
    public uint MaxUnitPrice { get; init; }
    public uint GilCap { get; init; }
    public string Status { get; init; } = string.Empty;
    public uint PurchasedQuantity { get; init; }
    public uint SpentGil { get; init; }
    public string? LatestMessage { get; init; }
}

public sealed record WorkshopHostAcquisitionTimeline
{
    public WorkshopHostAcquisitionRequestView Request { get; init; } = new();
    public IReadOnlyList<WorkshopHostMarketObservation> MarketObservations { get; init; } = [];
}

public sealed record WorkshopHostMarketObservation
{
    public string ObservationId { get; init; } = string.Empty;
    public string RequestId { get; init; } = string.Empty;
    public string AttemptId { get; init; } = string.Empty;
    public long Sequence { get; init; }
    public string LineId { get; init; } = string.Empty;
    public uint ItemId { get; init; }
    public string? ItemName { get; init; }
    public string DataCenter { get; init; } = string.Empty;
    public string WorldName { get; init; } = string.Empty;
    public string ReadState { get; init; } = string.Empty;
    public int ReportedListingCount { get; init; }
    public int ReadableListingCount { get; init; }
    public int ListingCapacity { get; init; }
    public bool IsTruncated { get; init; }
    public DateTimeOffset ObservedAtUtc { get; init; }
    public IReadOnlyList<WorkshopHostMarketObservationListing> Listings { get; init; } = [];

    public MarketWorldEvidenceSnapshot ToMarketEvidenceSnapshot() =>
        new(
            checked((int)ItemId),
            DataCenter,
            WorldName,
            MarketEvidenceOrigin.MarketMafioso,
            ObservedAtUtc.UtcDateTime,
            MarketUpdatedAtUtc: null,
            Listings.Select(listing => new MarketWorldEvidenceListing(
                checked((int)listing.Quantity),
                listing.UnitPrice,
                listing.RetainerName,
                listing.IsHq,
                ListingId: listing.ListingId,
                RetainerId: listing.RetainerId)).ToArray(),
            ReadState switch
            {
                "Complete" => MarketEvidenceCompleteness.Complete,
                "Partial" => MarketEvidenceCompleteness.Partial,
                _ => MarketEvidenceCompleteness.Unavailable,
            },
            ReportedListingCount,
            ListingCapacity,
            IsTruncated);
}

public sealed record WorkshopHostMarketObservationListing
{
    public string ListingId { get; init; } = string.Empty;
    public string RetainerId { get; init; } = string.Empty;
    public string RetainerName { get; init; } = string.Empty;
    public uint Quantity { get; init; }
    public uint UnitPrice { get; init; }
    public bool IsHq { get; init; }
}

public sealed record WorkshopHostCapabilityResponse
{
    public string Service { get; init; } = string.Empty;
    public int SchemaVersion { get; init; }
    public IReadOnlyList<WorkshopHostCapabilityDescriptor> Capabilities { get; init; } = [];

    public bool Supports(string capabilityId, int schemaVersion = 1) =>
        Capabilities.Any(capability =>
            string.Equals(capability.Id, capabilityId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(capability.Status, "available", StringComparison.OrdinalIgnoreCase) &&
            capability.SupportedSchemaVersions.Contains(schemaVersion));
}

public sealed record WorkshopHostCapabilityDescriptor
{
    public string Id { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public IReadOnlyList<int> SupportedSchemaVersions { get; init; } = [];
    public IReadOnlyList<string> RequiredScopes { get; init; } = [];
}
