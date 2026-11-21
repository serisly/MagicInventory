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

/// <summary>
/// Single canonical slot-level route resolver used by Tooltips, Route Overlays,
/// Highlight Filters, Retainer/Bell Evaluators, and Planners so every UI surface
/// agrees 100% on where an item belongs.
/// </summary>
public sealed unsafe class SlotRouteResolver
{
    private readonly IDataManager dataManager;
    private readonly InventorySafetyEvaluator safety;

    public SlotRouteResolver(IDataManager dataManager, InventorySafetyEvaluator safety)
    {
        this.dataManager = dataManager;
        this.safety = safety;
    }

    public ResolvedSlotRoute? ResolveSlot(
        uint rawItemId,
        InventoryType? container,
        InventoryItem* liveItem,
        IReadOnlyDictionary<uint, MagicSortEntry> manifestEntries,
        HashSet<uint> gearsetIds,
        Dictionary<uint, int> unlockedTop,
        bool includeBagToArmouryRoute = false)
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
            or InventoryType.RetainerMarket)
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

        if (container is InventoryType.EquippedItems or InventoryType.RetainerEquippedItems)
        {
            if (InventorySafetyEvaluator.IsMateriaExtractable(row, liveItem))
            {
                return new ResolvedSlotRoute("Materia", 0, string.Empty, null);
            }

            return ResolveStayOrBagRoute(container == InventoryType.RetainerEquippedItems, baseId, null);
        }

        // Locale-safe crystal/currency check (NEVER FilterGroup == 16!)
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
            // Tracked crafting materials sitting in a retainer are actively withdrawn to bags by Magic Sort!
            return new ResolvedSlotRoute(
                inRetainer ? "Tracked" : "Keep",
                0,
                string.Empty,
                null,
                IsTrackedInRetainer: inRetainer);
        }

        if (gearsetIds.Contains(baseId) || ItemIdentity.IsSoulCrystal(row) || row.Rarity >= 4)
        {
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
            var isPendingPrice = entry.Reason.StartsWith("Checking ", StringComparison.OrdinalIgnoreCase);
            var needsVisit = entry.Reason.StartsWith("Open a Summoning Bell", StringComparison.OrdinalIgnoreCase)
                || entry.Reason.StartsWith("Visit each retainer", StringComparison.OrdinalIgnoreCase);

            if (!isPendingPrice && !needsVisit && includeBagToArmouryRoute && !inRetainer && !inArmoury && ItemIdentity.IsEquipment(row))
            {
                return new ResolvedSlotRoute("Armoury", 0, string.Empty, entry);
            }

            return ResolveStayOrBagRoute(inRetainer, baseId, entry, isPendingPrice, needsVisit);
        }

        var effectiveRoute = safety.ResolveEffectiveSlotRoute(entry, row, liveItem, manifestEntries, gearsetIds, unlockedTop);
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

    /// <summary>
    /// Auto-protected items sitting inside a retainer stay in that retainer (Route = "Retainer", active retainer),
    /// whereas an explicit manual "Keep" override always resolves to "Keep" (Player Bags, withdrawn from retainer).
    /// </summary>
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
                // Fail-closed for HQ items: never display NQ price for an HQ item (matches MarketListingPlanner)
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
