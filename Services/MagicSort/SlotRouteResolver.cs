using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using MagicInventory.Core;

namespace MagicInventory.Services.MagicSort;

public readonly record struct ResolvedSlotRoute(
    string Route,
    ulong TargetRetainerId,
    string TargetRetainerName,
    MagicSortEntry? ManifestEntry,
    bool IsTrackedInRetainer = false,
    bool IsPendingPrice = false,
    bool NeedsRetainerVisit = false);

public sealed unsafe class SlotRouteResolver
{
    private readonly IDataManager dataManager;
    private readonly InventorySafetyEvaluator safety;
    private readonly Func<Dictionary<uint, int>>? getMissingTrackedQuantities;

    public SlotRouteResolver(
        IDataManager dataManager,
        InventorySafetyEvaluator safety,
        Func<Dictionary<uint, int>>? getMissingTrackedQuantities = null)
    {
        this.dataManager = dataManager;
        this.safety = safety;
        this.getMissingTrackedQuantities = getMissingTrackedQuantities;
    }

    public IReadOnlyDictionary<uint, int>? CaptureMissingTrackedSnapshot()
        => getMissingTrackedQuantities?.Invoke();

    public ResolvedSlotRoute? ResolveSlot(
        uint rawItemId,
        InventoryType? container,
        InventoryItem* liveItem,
        IReadOnlyDictionary<uint, MagicSortEntry> manifestEntries,
        HashSet<uint> gearsetIds,
        UnlockedJobGearProfile unlockedProfile,
        bool includeBagToArmouryRoute = false,
        IReadOnlyDictionary<uint, int>? missingTracked = null)
    {
        if (rawItemId == 0)
        {
            return null;
        }

        if (container is InventoryType.Crystals
            or InventoryType.Currency
            or InventoryType.KeyItems
            or InventoryType.RetainerCrystals
            or InventoryType.RetainerGil
            or InventoryType.RetainerMarket
            or InventoryType.RetainerEquippedItems
            or InventoryType.SaddleBag1
            or InventoryType.SaddleBag2
            or InventoryType.PremiumSaddleBag1
            or InventoryType.PremiumSaddleBag2
            or InventoryType.FreeCompanyPage1
            or InventoryType.FreeCompanyPage2
            or InventoryType.FreeCompanyPage3
            or InventoryType.FreeCompanyPage4
            or InventoryType.FreeCompanyPage5
            or InventoryType.FreeCompanyGil
            or InventoryType.FreeCompanyCrystals)
        {
            return null;
        }

        var baseId = ItemIdentity.NormalizeBaseItemId(rawItemId);
        if (baseId == 0)
        {
            return null;
        }

        var itemSheet = dataManager.GetExcelSheet<Item>();
        var itemRow = itemSheet?.GetRowOrDefault(baseId);
        if (itemRow is null)
        {
            return null;
        }

        var row = itemRow.Value;
        var inRetainer = InventorySafetyEvaluator.IsRetainerContainer(container);
        var inArmoury = container.HasValue && InventorySafetyEvaluator.IsArmouryContainer(container.Value);

        if (container is InventoryType.EquippedItems)
        {
            if (InventorySafetyEvaluator.IsMateriaExtractable(row, liveItem))
            {
                return new ResolvedSlotRoute("Materia", 0, string.Empty, null);
            }

            return new ResolvedSlotRoute("Keep", 0, string.Empty, null);
        }

        if (!ItemIdentity.CanLockOrTagItem(baseId, row))
        {
            return null;
        }

        if (InventorySafetyEvaluator.IsMateriaExtractable(row, liveItem))
        {
            return new ResolvedSlotRoute("Materia", 0, string.Empty, null);
        }

        if (safety.IsItemTracked(baseId))
        {
            var needsRetainerPull = false;
            if (inRetainer)
            {
                var missingMap = missingTracked ?? getMissingTrackedQuantities?.Invoke();
                if (missingMap != null)
                {
                    needsRetainerPull = missingMap.TryGetValue(baseId, out var qty) && qty > 0;
                }
                else
                {
                    needsRetainerPull = true;
                }
            }

            if (needsRetainerPull)
            {
                return new ResolvedSlotRoute("Tracked", 0, string.Empty, null, IsTrackedInRetainer: true);
            }

            return ResolveStayOrBagRoute(inRetainer, baseId, null);
        }

        var hasExplicitRelicStorageOverride = ItemIdentity.IsRelicRarity(row)
            && manifestEntries.TryGetValue(baseId, out var relicEntry)
            && (string.Equals(relicEntry.Route, "Glamour", StringComparison.OrdinalIgnoreCase)
                || string.Equals(relicEntry.Route, "Armoire", StringComparison.OrdinalIgnoreCase)
                || (string.Equals(relicEntry.Route, "Retainer", StringComparison.OrdinalIgnoreCase) && relicEntry.TargetRetainerId != 0));

        if (gearsetIds.Contains(baseId)
            || ItemIdentity.IsSoulCrystal(row)
            || (ItemIdentity.IsRelicRarity(row) && !hasExplicitRelicStorageOverride))
        {
            if (includeBagToArmouryRoute && !inRetainer && !inArmoury && ItemIdentity.IsEquipment(row))
            {
                return new ResolvedSlotRoute("Armoury", 0, string.Empty, null);
            }

            return ResolveStayOrBagRoute(inRetainer, baseId, null);
        }

        if (!manifestEntries.TryGetValue(baseId, out var entry))
        {
            if (!inRetainer
                && !inArmoury
                && InventorySafetyEvaluator.IsEligibleForLiveRetainerDuplicateDeposit(row)
                && HoldingsService.GetActiveRetainerId() != 0
                && HoldingsService.GetLiveActiveRetainerCount(baseId) > 0)
            {
                var activeId = HoldingsService.GetActiveRetainerId();
                var activeName = HoldingsService.GetActiveRetainerName() ?? "Retainer";
                return new ResolvedSlotRoute("Retainer", activeId, activeName, null);
            }

            if (includeBagToArmouryRoute && !inRetainer && !inArmoury && ItemIdentity.IsEquipment(row))
            {
                return new ResolvedSlotRoute("Armoury", 0, string.Empty, null);
            }

            return ResolveStayOrBagRoute(inRetainer, baseId, null);
        }

        if (string.Equals(entry.Route, "Keep", StringComparison.OrdinalIgnoreCase))
        {
            var needsVisit = entry.NeedsRetainerVisit;
            var isPendingPrice = entry.IsPendingRefresh && !needsVisit;

            if (!entry.IsPendingRefresh && includeBagToArmouryRoute && !inRetainer && !inArmoury && ItemIdentity.IsEquipment(row))
            {
                return new ResolvedSlotRoute("Armoury", 0, string.Empty, entry);
            }

            return ResolveStayOrBagRoute(inRetainer, baseId, entry, isPendingPrice, needsVisit);
        }

        var effectiveRoute = safety.ResolveEffectiveSlotRoute(entry, row, liveItem, manifestEntries, gearsetIds, unlockedProfile);
        if (string.IsNullOrEmpty(effectiveRoute))
        {
            if (includeBagToArmouryRoute && !inRetainer && !inArmoury && ItemIdentity.IsEquipment(row))
            {
                return new ResolvedSlotRoute("Armoury", 0, string.Empty, entry);
            }

            return ResolveStayOrBagRoute(inRetainer, baseId, entry);
        }

        if (string.Equals(effectiveRoute, "Retainer", StringComparison.OrdinalIgnoreCase))
        {
            var retName = string.IsNullOrWhiteSpace(entry.TargetRetainerName) ? "Retainer" : entry.TargetRetainerName;
            return new ResolvedSlotRoute("Retainer", entry.TargetRetainerId, retName, entry);
        }

        return new ResolvedSlotRoute(effectiveRoute, 0, string.Empty, entry);
    }

    private ResolvedSlotRoute ResolveStayOrBagRoute(
        bool inRetainer,
        uint baseId,
        MagicSortEntry? entry,
        bool isPendingPrice = false,
        bool needsVisit = false)
    {
        if (inRetainer && !isPendingPrice && !needsVisit && !safety.HasExplicitKeepOverride(baseId))
        {
            var activeId = HoldingsService.GetActiveRetainerId();
            var activeName = HoldingsService.GetActiveRetainerName();
            if (activeId != 0 && !string.IsNullOrWhiteSpace(activeName))
            {
                return new ResolvedSlotRoute("Retainer", activeId, activeName, entry);
            }
        }

        return new ResolvedSlotRoute("Keep", 0, string.Empty, entry, IsPendingPrice: isPendingPrice, NeedsRetainerVisit: needsVisit);
    }

    public string FormatHoverTooltipLabel(
        ResolvedSlotRoute resolved,
        uint rawItemId,
        InventoryType? container,
        InventoryItem* liveItem,
        Item itemRow)
    {
        if (resolved.IsPendingPrice)
        {
            return "Magic Sort: Checking price...";
        }

        if (resolved.NeedsRetainerVisit)
        {
            return "Magic Sort: Visit retainers at Bell";
        }

        if (resolved.IsTrackedInRetainer)
        {
            return "Magic Sort: Withdraw (Tracked)";
        }

        var entry = resolved.ManifestEntry;
        var isHq = ItemIdentity.IsHighQuality(rawItemId, liveItem != null ? liveItem->Flags : InventoryItem.ItemFlags.None);

        switch (resolved.Route)
        {
            case "Retainer":
                return $"Magic Sort: {resolved.TargetRetainerName}";

            case "Use":
                return "Magic Sort: Use";

            case "Market":
            {
                long price = 0;
                if (entry != null)
                {
                    price = isHq ? entry.TargetPriceHq : entry.TargetPriceNq;
                }

                var prio = safety.GetMarketPriority(itemRow.RowId);
                var prioTag = prio > 0 ? " [High]" : (prio < 0 ? " [Low]" : string.Empty);
                return price > 0 ? $"Magic Sort: Market (~{price:N0}g){prioTag}" : $"Magic Sort: Market{prioTag}";
            }

            case "Vendor":
            {
                var qty = liveItem != null && liveItem->Quantity > 0 ? liveItem->Quantity : 1;
                var npcUnit = entry is { NpcSellPrice: > 0 } ? entry.NpcSellPrice : (long)itemRow.PriceLow;
                var total = Math.Max(0, npcUnit) * qty;
                return total > 0 ? $"Magic Sort: Vendor ({total:N0}g)" : "Magic Sort: Vendor";
            }

            case "GrandCompany":
                return "Magic Sort: GC Seals";

            case "GoldSaucer":
                return "Magic Sort: Gold Saucer";

            case "Materia":
                return "Magic Sort: Extract Materia";

            case "Armoire":
                return "Magic Sort: Armoire";

            case "Glamour":
                return "Magic Sort: Glamour Dresser";

            case "Armoury":
                return "Magic Sort: Armoury";

            default:
                return "Magic Sort: Keep";
        }
    }
}
