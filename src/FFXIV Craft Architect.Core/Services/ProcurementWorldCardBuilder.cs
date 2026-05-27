using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public static class ProcurementWorldCardBuilder
{
    public static List<WorldProcurementCardModel> BuildWorldCards(
        IEnumerable<DetailedShoppingPlan> shoppingPlans,
        string fallbackDataCenter)
    {
        var worldCards = new Dictionary<string, WorldProcurementCardModel>(StringComparer.OrdinalIgnoreCase);

        foreach (var plan in shoppingPlans.Where(p => string.IsNullOrEmpty(p.Error)))
        {
            if (plan.RequiresSplitPurchase && plan.RecommendedSplit?.Any() == true)
            {
                foreach (var split in plan.RecommendedSplit)
                {
                    var worldIdentity = GetWorldIdentity(split, fallbackDataCenter);
                    var card = GetOrCreateCard(worldCards, worldIdentity, plan);

                    card.Items.Add(new WorldItemPurchase
                    {
                        ItemId = plan.ItemId,
                        ItemName = plan.Name,
                        IconId = plan.IconId,
                        QuantityOnThisWorld = split.QuantityToBuy,
                        TotalQuantityNeeded = plan.QuantityNeeded,
                        PricePerUnit = split.EffectivePricePerNeededUnit,
                        PriceIsEffectiveCost = true,
                        TotalCost = split.TotalCost,
                        IsSplitPurchase = true,
                        SourcePlan = plan,
                        TravelContext = split.TravelContext
                    });
                }
            }
            else if (plan.RecommendedWorld != null)
            {
                var worldIdentity = GetWorldIdentity(plan.RecommendedWorld, fallbackDataCenter);
                var card = GetOrCreateCard(worldCards, worldIdentity, plan);
                VendorInfo? vendor = null;

                if (worldIdentity.WorldName == MarketShoppingConstants.VendorWorldName && plan.Vendors.Any())
                {
                    vendor = plan.Vendors.FirstOrDefault(v => v.Price == plan.RecommendedWorld.AveragePricePerUnit)
                        ?? plan.Vendors.FirstOrDefault();
                    card.Vendors = plan.Vendors.ToList();
                    card.SelectedVendorName = plan.RecommendedWorld.VendorName;
                }

                card.Items.Add(new WorldItemPurchase
                {
                    ItemId = plan.ItemId,
                    ItemName = plan.Name,
                    IconId = plan.IconId,
                    QuantityOnThisWorld = plan.QuantityNeeded,
                    TotalQuantityNeeded = plan.QuantityNeeded,
                    PricePerUnit = plan.RecommendedWorld.AveragePricePerUnit,
                    TotalCost = plan.RecommendedWorld.TotalCost,
                    IsSplitPurchase = false,
                    SourcePlan = plan,
                    TravelContext = TravelContextConstants.Primary,
                    Vendor = vendor
                });
            }
        }

        return worldCards.Values
            .OrderBy(w => w.IsVendor ? 1 : 0)
            .ThenBy(w => w.DataCenter)
            .ThenByDescending(w => w.TotalCost)
            .ToList();
    }

    public static string GetWorldKey(WorldProcurementCardModel world)
    {
        return $"{world.DataCenter}|{world.WorldName}";
    }

    private static WorldProcurementCardModel GetOrCreateCard(
        Dictionary<string, WorldProcurementCardModel> worldCards,
        WorldIdentity worldIdentity,
        DetailedShoppingPlan plan)
    {
        var key = $"{worldIdentity.DataCenter}|{worldIdentity.WorldName}";
        if (worldCards.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var worldOption = plan.WorldOptions.FirstOrDefault(w => IsSameWorld(GetWorldIdentity(w, worldIdentity.DataCenter), worldIdentity));
        var card = new WorldProcurementCardModel
        {
            WorldName = worldIdentity.WorldName,
            DataCenter = worldIdentity.DataCenter,
            IsCongested = worldOption?.IsCongested ?? plan.RecommendedWorld?.IsCongested ?? false,
            Classification = worldOption?.Classification ?? plan.RecommendedWorld?.Classification ?? WorldClassification.Standard
        };

        worldCards[key] = card;
        return card;
    }

    private static WorldIdentity GetWorldIdentity(SplitWorldPurchase split, string fallbackDataCenter)
    {
        return GetWorldIdentity(split.WorldName, split.DataCenter, fallbackDataCenter);
    }

    private static WorldIdentity GetWorldIdentity(WorldShoppingSummary world, string fallbackDataCenter)
    {
        return GetWorldIdentity(world.WorldName, world.DataCenter, fallbackDataCenter);
    }

    private static WorldIdentity GetWorldIdentity(string worldName, string? structuredDataCenter, string fallbackDataCenter)
    {
        if (string.Equals(worldName, MarketShoppingConstants.VendorWorldName, StringComparison.OrdinalIgnoreCase))
        {
            return new WorldIdentity(MarketShoppingConstants.VendorWorldName, MarketShoppingConstants.VendorWorldName);
        }

        if (!string.IsNullOrWhiteSpace(structuredDataCenter))
        {
            return new WorldIdentity(worldName, structuredDataCenter);
        }

        var closeParen = worldName.LastIndexOf(')');
        var openParen = worldName.LastIndexOf('(');
        if (openParen > 0 && closeParen == worldName.Length - 1)
        {
            return new WorldIdentity(worldName[..openParen].Trim(), worldName[(openParen + 1)..closeParen].Trim());
        }

        return new WorldIdentity(worldName, fallbackDataCenter);
    }

    private static bool IsSameWorld(WorldIdentity left, WorldIdentity right)
    {
        return string.Equals(left.DataCenter, right.DataCenter, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.WorldName, right.WorldName, StringComparison.OrdinalIgnoreCase);
    }

    private readonly record struct WorldIdentity(string WorldName, string DataCenter);
}
