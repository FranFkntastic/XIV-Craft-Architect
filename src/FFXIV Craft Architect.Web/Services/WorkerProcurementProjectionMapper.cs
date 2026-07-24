using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services;

public static class WorkerProcurementProjectionMapper
{
    public static WorldProcurementCardModel ToWorldCard(
        WorkerProcurementWorldProjection world) =>
        new()
        {
            DataCenter = world.DataCenter,
            WorldName = world.WorldName,
            IsCongested = world.IsCongested,
            CongestedWarning = world.CongestedWarning,
            Classification = world.Classification,
            Vendors = world.Vendors.ToList(),
            SelectedVendorName = world.SelectedVendorName,
            Items = world.Items.Select(item => new WorldItemPurchase
            {
                ItemId = item.ItemId,
                ItemName = item.Name,
                IconId = item.IconId,
                QuantityOnThisWorld = item.Quantity,
                TotalQuantityNeeded = item.TotalQuantityNeeded,
                PricePerUnit = item.UnitPrice,
                PriceIsEffectiveCost = item.PriceIsEffectiveCost,
                TotalCost = item.TotalCost,
                IsSplitPurchase = item.IsSplitPurchase,
                TravelContext = item.TravelContext,
                Vendor = item.Vendor
            }).ToList()
        };
}
