using System.Collections.Frozen;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace MagicInventory.Core;

/// <summary>
/// Single source of truth for FFXIV item ID normalization, HQ/Collectable flags,
/// locale-safe Lumina category classification, and manual-route eligibility.
/// </summary>
public static class ItemIdentity
{
    public const uint CollectableIdOffset = 500_000;
    public const uint HighQualityIdOffset = 1_000_000;
    public const uint EventItemIdThreshold = 2_000_000;

    // Exact Lumina ItemUICategory RowIds (verified against FFXIV 7.x ItemUICategory sheet)
    public const uint UiCatFishingTackle = 33;
    public const uint UiCatMedicine = 44;
    public const uint UiCatIngredient = 45;
    public const uint UiCatMeal = 46;
    public const uint UiCatSeafood = 47;
    public const uint UiCatStone = 48;
    public const uint UiCatMetal = 49;
    public const uint UiCatLumber = 50;
    public const uint UiCatCloth = 51;
    public const uint UiCatLeather = 52;
    public const uint UiCatBone = 53;
    public const uint UiCatReagent = 54;
    public const uint UiCatDye = 55;
    public const uint UiCatPart = 56;
    public const uint UiCatFurnishing = 57;
    public const uint UiCatMateria = 58;
    public const uint UiCatCrystal = 59;
    public const uint UiCatCatalyst = 60;
    public const uint UiCatMiscellany = 61;
    public const uint UiCatSoulCrystal = 62;
    public const uint UiCatOther = 63;
    public const uint UiCatMinion = 81;
    public const uint UiCatGardening = 82;
    public const uint UiCatDemimateria = 83;
    public const uint UiCatSeasonalMiscellany = 85;
    public const uint UiCatTripleTriadCard = 86;
    public const uint UiCatOrchestrionRoll = 94;
    public const uint UiCatPainting = 95;
    public const uint UiCatCurrency = 100;
    public const uint UiCatOutfits = 112;

    // Exact Lumina ItemSortCategory RowId for currencies (Gil, GC Seals, Tomestones, MGP, etc.)
    public const uint SortCatCurrency = 3;

    // Exact Lumina FilterGroup values
    public const byte FilterGroupCrystal = 11;
    public const byte FilterGroupHousing = 14;
    public const byte FilterGroupPainting = 34;

    public static readonly FrozenSet<uint> HousingCategoryIds = new uint[]
    {
        UiCatFurnishing, // 57
        64, // Construction Permit
        65, // Roof
        66, // Exterior Wall
        67, // Window
        68, // Door
        69, // Roof Decoration
        70, // Exterior Wall Decoration
        71, // Placard
        72, // Fence
        73, // Interior Wall
        74, // Flooring
        75, // Ceiling Light
        76, // Outdoor Furnishing
        77, // Table
        78, // Tabletop
        79, // Wall-mounted
        80, // Rug
        UiCatGardening, // 82
        UiCatPainting, // 95
    }.ToFrozenSet();

    public static readonly FrozenSet<uint> ProtectedConsumableCategoryIds = new uint[]
    {
        UiCatMedicine, // 44
        UiCatMeal, // 46
    }.ToFrozenSet();

    public static readonly FrozenSet<uint> ProtectedStoreNoAutoMarketCategoryIds = new uint[]
    {
        UiCatFishingTackle, // 33
        UiCatDye, // 55
        UiCatMateria, // 58
        UiCatCatalyst, // 60
    }.ToFrozenSet();

    public static readonly FrozenSet<uint> CraftingStapleCategoryIds = new uint[]
    {
        UiCatIngredient, // 45
        UiCatSeafood, // 47
        UiCatStone, // 48
        UiCatMetal, // 49
        UiCatLumber, // 50
        UiCatCloth, // 51
        UiCatLeather, // 52
        UiCatBone, // 53
        UiCatReagent, // 54
        UiCatPart, // 56
        UiCatCatalyst, // 60
    }.ToFrozenSet();

    public static readonly FrozenSet<uint> ProtectedNoVendorCategoryIds = new uint[]
    {
        UiCatDye, // 55
        UiCatMateria, // 58
        UiCatCatalyst, // 60
        UiCatFishingTackle, // 33
        UiCatSeasonalMiscellany, // 85
        UiCatMinion, // 81
        UiCatTripleTriadCard, // 86
        UiCatOrchestrionRoll, // 94
        UiCatDemimateria, // 83
        UiCatOutfits, // 112
    }.ToFrozenSet();

    public static readonly FrozenSet<uint> CrafterGathererToolCategoryIds = new uint[]
    {
        12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, // DoH tools
        28, 29, 30, 31, 32, 99, // DoL tools (incl. Fisher's Secondary Tool 99)
    }.ToFrozenSet();

    /// <summary>
    /// Normalizes a raw FFXIV item ID to its base Item sheet RowId.
    /// Strips HQ (+1,000,000) and Collectable (+500,000) offsets; rejects 0 and Event/Key items (>= 2,000,000).
    /// </summary>
    public static uint NormalizeBaseItemId(uint rawItemId)
    {
        if (rawItemId == 0 || rawItemId >= EventItemIdThreshold)
        {
            return 0;
        }

        if (rawItemId >= HighQualityIdOffset)
        {
            return rawItemId - HighQualityIdOffset;
        }

        if (rawItemId >= CollectableIdOffset)
        {
            return rawItemId - CollectableIdOffset;
        }

        return rawItemId;
    }

    public static bool IsHighQuality(uint rawItemId, InventoryItem.ItemFlags flags)
        => rawItemId is >= HighQualityIdOffset and < EventItemIdThreshold
            || flags.HasFlag(InventoryItem.ItemFlags.HighQuality);

    public static unsafe bool IsHighQuality(InventoryItem* slot)
        => slot != null && IsHighQuality(slot->ItemId, slot->Flags);

    public static bool IsCollectable(uint rawItemId, InventoryItem.ItemFlags flags)
        => rawItemId is >= CollectableIdOffset and < HighQualityIdOffset
            || flags.HasFlag(InventoryItem.ItemFlags.Collectable);

    public static unsafe bool IsCollectable(InventoryItem* slot)
        => slot != null && IsCollectable(slot->ItemId, slot->Flags);

    /// <summary>
    /// Returns the item ID expected by ActionManager.UseAction(ActionType.Item, ...).
    /// FFXIV requires baseId + 1,000,000 for HQ consumables/items.
    /// </summary>
    public static uint ToActionItemId(uint baseItemId, bool isHq)
        => isHq ? baseItemId + HighQualityIdOffset : baseItemId;

    /// <summary>
    /// Locale-safe currency/crystal check.
    /// NEVER uses FilterGroup == 16 (which contains Seafood, Medicine/Antidotes, Glamour Prisms, Dark Matter, Minions, etc.).
    /// </summary>
    public static bool IsCurrencyOrCrystal(uint baseItemId, Item itemRow)
    {
        if (baseItemId <= 19)
        {
            return true;
        }

        if (itemRow.FilterGroup == FilterGroupCrystal)
        {
            return true;
        }

        var uiCatId = itemRow.ItemUICategory.RowId;
        if (uiCatId is UiCatCrystal or UiCatCurrency)
        {
            return true;
        }

        return itemRow.ItemSortCategory.RowId == SortCatCurrency;
    }

    public static bool CanLockOrTagBaseId(uint baseItemId)
        => baseItemId >= 20 && baseItemId < CollectableIdOffset;

    public static bool CanLockOrTagItem(uint baseItemId, Item? itemRow)
    {
        if (!CanLockOrTagBaseId(baseItemId))
        {
            return false;
        }

        return itemRow is null || !IsCurrencyOrCrystal(baseItemId, itemRow.Value);
    }

    public static bool IsMarketable(Item itemRow)
        => !itemRow.IsUntradable && itemRow.ItemSearchCategory.RowId > 0;

    public static bool IsTripleTriadCard(Item itemRow)
        => itemRow.ItemUICategory.RowId == UiCatTripleTriadCard;

    public static bool IsSoulCrystal(Item itemRow)
        => itemRow.ItemUICategory.RowId == UiCatSoulCrystal;

    public static bool IsHousingItem(Item itemRow)
        => itemRow.FilterGroup is FilterGroupHousing or FilterGroupPainting
            || HousingCategoryIds.Contains(itemRow.ItemUICategory.RowId);

    /// <summary>
    /// True for actual unlockable items in protected categories (Triple Triad Card, Minion,
    /// and usable Orchestrion Rolls — excluding Blank Orchestrion Roll crafting mats with ItemAction == 0).
    /// </summary>
    public static bool IsUnlockableCategory(Item itemRow)
    {
        var uiCat = itemRow.ItemUICategory.RowId;
        if (uiCat is UiCatTripleTriadCard or UiCatMinion)
        {
            return true;
        }

        return uiCat == UiCatOrchestrionRoll && itemRow.ItemAction.RowId != 0;
    }

    public static bool IsCrafterOrGathererTool(Item itemRow)
        => CrafterGathererToolCategoryIds.Contains(itemRow.ItemUICategory.RowId);

    public static bool IsEquipment(Item itemRow)
        => itemRow.EquipSlotCategory.RowId != 0 && !IsSoulCrystal(itemRow);
}

public readonly record struct ItemRouteEligibility(
    bool CanUse,
    bool CanMarket,
    bool CanVendor,
    bool CanGrandCompany,
    bool CanGoldSaucer,
    bool CanGlamour,
    bool CanArmoire)
{
    public bool CanSellAny => CanMarket || CanVendor || CanGrandCompany || CanGoldSaucer;

    public static ItemRouteEligibility Compute(Item? itemRow, bool isCabinetItem)
    {
        if (itemRow is null)
        {
            return new ItemRouteEligibility(
                CanUse: false,
                CanMarket: true,
                CanVendor: true,
                CanGrandCompany: false,
                CanGoldSaucer: false,
                CanGlamour: false,
                CanArmoire: isCabinetItem);
        }

        var row = itemRow.Value;
        var isEquip = ItemIdentity.IsEquipment(row);
        return new ItemRouteEligibility(
            CanUse: row.ItemAction.RowId > 0,
            CanMarket: ItemIdentity.IsMarketable(row),
            CanVendor: row.PriceLow > 0 && !row.IsUnique,
            CanGrandCompany: isEquip && row.Rarity is 2 or 3 or 7 && row.LevelItem.RowId >= 15,
            CanGoldSaucer: ItemIdentity.IsTripleTriadCard(row),
            CanGlamour: isEquip,
            CanArmoire: isCabinetItem);
    }
}
