using System.Collections.Frozen;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace MagicInventory.Core;

public static class ItemIdentity
{
    public const uint CollectableIdOffset = 500_000;
    public const uint HighQualityIdOffset = 1_000_000;
    public const uint EventItemIdThreshold = 2_000_000;

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

    public const uint SortCatCurrency = 3;

    public const byte FilterGroupCrystal = 11;
    public const byte FilterGroupHousing = 14;
    public const byte FilterGroupPainting = 34;

    public static readonly FrozenSet<uint> HousingCategoryIds = new uint[]
    {
        UiCatFurnishing,
        64,
        65,
        66,
        67,
        68,
        69,
        70,
        71,
        72,
        73,
        74,
        75,
        76,
        77,
        78,
        79,
        80,
        UiCatGardening,
        UiCatPainting,
    }.ToFrozenSet();

    public static readonly FrozenSet<uint> ProtectedConsumableCategoryIds = new uint[]
    {
        UiCatMedicine,
        UiCatMeal,
    }.ToFrozenSet();

    public static readonly FrozenSet<uint> ProtectedStoreNoAutoMarketCategoryIds = new uint[]
    {
        UiCatFishingTackle,
        UiCatDye,
        UiCatMateria,
        UiCatCatalyst,
    }.ToFrozenSet();

    public static readonly FrozenSet<uint> CraftingStapleCategoryIds = new uint[]
    {
        UiCatIngredient,
        UiCatSeafood,
        UiCatStone,
        UiCatMetal,
        UiCatLumber,
        UiCatCloth,
        UiCatLeather,
        UiCatBone,
        UiCatReagent,
        UiCatPart,
        UiCatCatalyst,
    }.ToFrozenSet();

    public static readonly FrozenSet<uint> ProtectedNoVendorCategoryIds = new uint[]
    {
        UiCatDye,
        UiCatMateria,
        UiCatCatalyst,
        UiCatFishingTackle,
        UiCatSeasonalMiscellany,
        UiCatMinion,
        UiCatTripleTriadCard,
        UiCatOrchestrionRoll,
        UiCatDemimateria,
        UiCatOutfits,
    }.ToFrozenSet();

    public const uint ActionIdExpManual = 816;
    public const uint ActionIdGysahlGreens = 851;
    public const uint ActionIdCordial = 1055;
    public const uint ActionIdFantasia = 1326;
    public const uint ActionIdChocoboTrainingGreens = 2483;
    public const uint ActionIdThavnairianOnion = 3292;
    public const uint ActionIdMgpCard = 3800;
    public const uint ActionIdGearCofferA = 4647;
    public const uint ActionIdAetheryteTicket = 9994;
    public const uint ActionIdTalesOfAdventure = 25174;
    public const uint ActionIdGearCofferB = 29153;

    public static readonly FrozenSet<uint> ProtectedUtilityActionIds = new uint[]
    {
        ActionIdExpManual,
        ActionIdGysahlGreens,
        ActionIdCordial,
        ActionIdFantasia,
        ActionIdChocoboTrainingGreens,
        ActionIdThavnairianOnion,
        ActionIdAetheryteTicket,
        ActionIdTalesOfAdventure,
    }.ToFrozenSet();

    public static readonly FrozenSet<uint> CofferActionIds = new uint[]
    {
        ActionIdGearCofferA,
        ActionIdGearCofferB,
    }.ToFrozenSet();

    public static readonly FrozenSet<uint> ProtectedUtilityItemIds = new uint[]
    {
        5594, 5595, 5596, 5597, 5598, 10386, 17837, 33916,
        7569,
        7621,
        8165,
        12994, 12995,
        21800,
    }.ToFrozenSet();

    public static readonly FrozenSet<uint> CrafterGathererToolCategoryIds = new uint[]
    {
        12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27,
        28, 29, 30, 31, 32, 99,
    }.ToFrozenSet();

    public static bool IsValidItemId(ulong rawItemId)
        => rawItemId is > 0 and < EventItemIdThreshold;

    public static bool IsCrystalItemId(uint baseItemId)
        => baseItemId is >= 2 and <= 19;

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

    public static uint ToActionItemId(uint baseItemId, bool isHq)
        => isHq ? baseItemId + HighQualityIdOffset : baseItemId;

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

    public static bool IsUnlockableCategory(Item itemRow)
    {
        var uiCat = itemRow.ItemUICategory.RowId;
        if (uiCat is UiCatTripleTriadCard or UiCatMinion or UiCatOutfits)
        {
            return true;
        }

        return uiCat == UiCatOrchestrionRoll && itemRow.ItemAction.RowId != 0;
    }

    public static bool IsCrafterOrGathererTool(Item itemRow)
        => CrafterGathererToolCategoryIds.Contains(itemRow.ItemUICategory.RowId);

    public static bool IsEquipment(Item itemRow)
        => itemRow.EquipSlotCategory.RowId != 0 && !IsSoulCrystal(itemRow);

    public static bool IsRelicRarity(Item itemRow)
        => itemRow.Rarity == 4;

    public static bool IsMgpCard(Item itemRow)
        => itemRow.ItemAction.RowId != 0
            && itemRow.ItemAction.ValueNullable?.Action.RowId == ActionIdMgpCard;

    public static bool IsCofferOrContainer(Item itemRow)
        => itemRow.ItemAction.RowId != 0
            && itemRow.ItemAction.ValueNullable is { } act
            && CofferActionIds.Contains(act.Action.RowId);

    public static bool IsProtectedUtilityItem(uint baseItemId, Item itemRow)
    {
        if (ProtectedUtilityItemIds.Contains(baseItemId))
        {
            return true;
        }

        return itemRow.ItemAction.RowId != 0
            && itemRow.ItemAction.ValueNullable is { } act
            && ProtectedUtilityActionIds.Contains(act.Action.RowId);
    }
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
                CanMarket: false,
                CanVendor: false,
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
            CanVendor: row.PriceLow > 0,
            CanGrandCompany: isEquip && row.Rarity is 2 or 3 or 7 && row.LevelItem.RowId >= 15 && row.LevelEquip >= 15 && row.PriceLow > 0,
            CanGoldSaucer: ItemIdentity.IsTripleTriadCard(row),
            CanGlamour: isEquip,
            CanArmoire: isCabinetItem);
    }
}
