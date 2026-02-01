namespace FFXIVCraftArchitect.Models;

/// <summary>
/// Represents a single inventory slot item from FFXIV.
/// Maps to the Python dict: {'itemId': int, 'containerId': int, 'slot': int, 'quantity': int, 'hq': bool}
/// </summary>
public record InventoryItem
{
    public int ItemId { get; init; }
    public int ContainerId { get; init; }
    public int Slot { get; init; }
    public int Quantity { get; init; }
    public bool IsHq { get; init; }

    /// <summary>
    /// Item name (populated from API, not from packet data)
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Icon ID for displaying item image
    /// </summary>
    public int? IconId { get; set; }
}

/// <summary>
/// Container types in FFXIV, matching Python CONTAINER_NAMES
/// </summary>
public static class ContainerTypes
{
    // Inventory Bags (0-3)
    public const int Bag1 = 0;
    public const int Bag2 = 1;
    public const int Bag3 = 2;
    public const int Bag4 = 3;

    // Gear Sets (1000-1001)
    public const int GearSet1 = 1000;
    public const int GearSet2 = 1001;

    // Special Inventories (2000-2009)
    public const int Currency = 2000;
    public const int Crystals = 2001;
    public const int Mail = 2003;
    public const int KeyItems = 2004;
    public const int HandIn = 2005;
    public const int DamagedGear = 2007;
    public const int TradeInventory = 2009;

    // Armory (3200-3500)
    public const int ArmoryOffHand = 3200;
    public const int ArmoryHead = 3201;
    public const int ArmoryBody = 3202;
    public const int ArmoryHands = 3203;
    public const int ArmoryWaist = 3204;
    public const int ArmoryLegs = 3205;
    public const int ArmoryFeet = 3206;
    public const int ArmoryNeck = 3207;
    public const int ArmoryEars = 3208;
    public const int ArmoryWrists = 3209;
    public const int ArmoryRings = 3300;
    public const int ArmorySoulCrystals = 3400;
    public const int ArmoryMainHand = 3500;

    // Saddlebags (4000-4101)
    public const int Saddlebag1 = 4000;
    public const int Saddlebag2 = 4001;
    public const int PremiumSaddlebag1 = 4100;
    public const int PremiumSaddlebag2 = 4101;

    // Retainers (10000-12002)
    public const int Retainer1Inventory = 10000;
    public const int Retainer2Inventory = 10001;
    public const int Retainer3Inventory = 10002;
    public const int Retainer4Inventory = 10003;
    public const int Retainer5Inventory = 10004;
    public const int Retainer6Inventory = 10005;
    public const int Retainer7Inventory = 10006;
    public const int RetainerEquippedGear = 11000;
    public const int RetainerGil = 12000;
    public const int RetainerCrystals = 12001;
    public const int RetainerMarket = 12002;

    // Free Company (20000-22001)
    public const int FcChest1 = 20000;
    public const int FcChest2 = 20001;
    public const int FcChest3 = 20002;
    public const int FcChest4 = 20003;
    public const int FcChest5 = 20004;
    public const int FcChest6 = 20005;
    public const int FcChest7 = 20006;
    public const int FcChest8 = 20007;
    public const int FcChest9 = 20008;
    public const int FcChest10 = 20009;
    public const int FcChest11 = 20010;
    public const int FcGil = 22000;
    public const int FcCrystals = 22001;

    // Housing (25000-27008)
    public const int HousingExteriorAppearance = 25000;
    public const int HousingExteriorPlacedItems = 25001;
    public const int HousingInteriorAppearance = 25002;
    public const int HousingInteriorStorage1 = 25003;
    public const int HousingInteriorStorage2 = 25004;
    public const int HousingInteriorStorage3 = 25005;
    public const int HousingInteriorStorage4 = 25006;
    public const int HousingInteriorStorage5 = 25007;
    public const int HousingInteriorStorage6 = 25008;
    public const int HousingInteriorStorage7 = 25009;
    public const int HousingInteriorStorage8 = 25010;
    public const int HousingExteriorStoreroom = 27000;
    public const int HousingInteriorStoreroom1 = 27001;
    public const int HousingInteriorStoreroom2 = 27002;
    public const int HousingInteriorStoreroom3 = 27003;
    public const int HousingInteriorStoreroom4 = 27004;
    public const int HousingInteriorStoreroom5 = 27005;
    public const int HousingInteriorStoreroom6 = 27006;
    public const int HousingInteriorStoreroom7 = 27007;
    public const int HousingInteriorStoreroom8 = 27008;

    // Island Sanctuary
    public const int IslandSanctuaryGranary = -10;
}
