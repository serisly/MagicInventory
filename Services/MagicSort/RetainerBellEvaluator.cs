using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using MagicInventory.Core;

namespace MagicInventory.Services.MagicSort;

public readonly record struct RetainerBellWorkSummary(
    ulong RetainerId,
    int DepositCount,
    int WithdrawCount,
    int TrackedWithdrawCount,
    HashSet<ulong> FunnelDestinationIds)
{
    public int TotalItemCount => DepositCount + WithdrawCount + TrackedWithdrawCount;
    public bool HasAnyWork => TotalItemCount > 0;
    public bool HasWithdrawWork => WithdrawCount > 0 || TrackedWithdrawCount > 0;
}

/// <summary>
/// Evaluates Summoning Bell work across all retainers using the exact same route rules
/// as RetainerSortPlanner — including ALL 9 withdrawable routes (Market, Vendor, GC,
/// Gold Saucer, Materia, Use, Armoire, Glamour, misplaced Retainer) plus Tracked pulls
/// and bag deposits — so full retainers (e.g. 175/175) are drained first instead of skipped.
/// </summary>
public sealed unsafe class RetainerBellEvaluator
{
    private readonly IDataManager dataManager;
    private readonly HoldingsService holdings;
    private readonly InventorySafetyEvaluator safety;
    private readonly SlotRouteResolver slotResolver;
    private readonly Func<Dictionary<uint, int>> getMissingTrackedQuantities;

    public RetainerBellEvaluator(
        IDataManager dataManager,
        HoldingsService holdings,
        InventorySafetyEvaluator safety,
        SlotRouteResolver slotResolver,
        Func<Dictionary<uint, int>> getMissingTrackedQuantities)
    {
        this.dataManager = dataManager;
        this.holdings = holdings;
        this.safety = safety;
        this.slotResolver = slotResolver;
        this.getMissingTrackedQuantities = getMissingTrackedQuantities;
    }

    public static bool IsWithdrawableRoute(string? route)
    {
        return route is "Market"
            or "Vendor"
            or "GrandCompany"
            or "GoldSaucer"
            or "Materia"
            or "Use"
            or "Armoire"
            or "Glamour";
    }

    public Dictionary<ulong, RetainerBellWorkSummary> EvaluateAllRetainers(
        IReadOnlyList<KnownRetainerInfo> knownRetainers,
        IReadOnlyDictionary<uint, MagicSortEntry> manifestEntries)
    {
        var result = new Dictionary<ulong, RetainerBellWorkSummary>(knownRetainers.Count);
        if (knownRetainers.Count == 0)
        {
            return result;
        }

        var manager = InventoryManager.Instance();
        var itemSheet = dataManager.GetExcelSheet<Item>();
        if (manager == null || itemSheet == null)
        {
            return result;
        }

        var gearsetIds = safety.BuildGearsetItemIds();
        var unlockedTop = safety.BuildUnlockedJobTopIlvl();
        var emptyBagSlots = (int)manager->GetEmptySlotsInBag();
        var missingTracked = getMissingTrackedQuantities();

        // Collect player-bag base IDs once for IsUnique conflict checks + compute validated deposits per retainer
        var playerUniqueBaseIds = new HashSet<uint>();
        var depositCountsByRetainer = new Dictionary<ulong, int>();
        var rawBagDepositsByDest = new Dictionary<ulong, int>();
        var overflowBagSlots = new List<(uint BaseId, bool IsHq, int StackSize, ulong PrimaryRetainerId)>();

        foreach (var bagType in InventorySafetyEvaluator.PlayerBagContainers)
        {
            var container = manager->GetInventoryContainer(bagType);
            if (!InventorySafetyEvaluator.IsContainerReadable(container))
            {
                continue;
            }

            for (var s = 0; s < container->Size; s++)
            {
                var slot = InventorySafetyEvaluator.GetSlot(container, s);
                if (slot == null || slot->ItemId == 0 || slot->Quantity <= 0)
                {
                    continue;
                }

                var baseId = ItemIdentity.NormalizeBaseItemId(slot->ItemId);
                if (baseId == 0)
                {
                    continue;
                }

                playerUniqueBaseIds.Add(baseId);
                if (manifestEntries.Count == 0)
                {
                    continue;
                }

                var resolved = slotResolver.ResolveSlot(slot->ItemId, bagType, slot, manifestEntries, gearsetIds, unlockedTop);
                if (resolved is null || !string.Equals(resolved.Value.Route, "Retainer", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var destId = resolved.Value.TargetRetainerId;
                if (destId == 0)
                {
                    continue;
                }

                var row = itemSheet.GetRowOrDefault(baseId);
                if (row is null)
                {
                    continue;
                }

                var isHq = ItemIdentity.IsHighQuality(slot);
                var stackSize = (int)Math.Max(1, row.Value.StackSize);
                var mergesPartial = stackSize > 1 && holdings.HasPartialStackOnRetainer(destId, baseId, isHq);

                if (mergesPartial || !holdings.IsRetainerKnownFullFor(destId, baseId, isHq, stackSize))
                {
                    depositCountsByRetainer[destId] = depositCountsByRetainer.GetValueOrDefault(destId) + 1;
                    if (!mergesPartial)
                    {
                        rawBagDepositsByDest[destId] = rawBagDepositsByDest.GetValueOrDefault(destId) + 1;
                    }
                }
                else
                {
                    overflowBagSlots.Add((baseId, isHq, stackSize, destId));
                }
            }
        }

        // Also include Armoury + Equipped base IDs for FFXIV's character-wide IsUnique conflict check
        foreach (var equipType in InventorySafetyEvaluator.ArmouryGearContainers)
        {
            CollectBaseIds(manager, equipType, playerUniqueBaseIds);
        }

        CollectBaseIds(manager, InventoryType.EquippedItems, playerUniqueBaseIds);

        // Track remaining free slots per retainer (after accounting for validated bag deposits)
        var destFreeBudget = new Dictionary<ulong, int>(knownRetainers.Count);
        foreach (var ret in knownRetainers)
        {
            var free = holdings.GetKnownRetainerFreeSlots(ret.RetainerId);
            var incoming = rawBagDepositsByDest.GetValueOrDefault(ret.RetainerId);
            destFreeBudget[ret.RetainerId] = Math.Max(0, free - incoming);
        }

        // Attribute overflow bag items to the first retainer with remaining space (only if NOT already counted on primary)
        foreach (var (baseId, isHq, stackSize, primaryId) in overflowBagSlots)
        {
            foreach (var ret in knownRetainers)
            {
                if (ret.RetainerId == primaryId)
                {
                    continue;
                }

                if (!holdings.IsRetainerKnownFullFor(ret.RetainerId, baseId, isHq, stackSize)
                    && destFreeBudget.GetValueOrDefault(ret.RetainerId) > 0)
                {
                    depositCountsByRetainer[ret.RetainerId] = depositCountsByRetainer.GetValueOrDefault(ret.RetainerId) + 1;
                    destFreeBudget[ret.RetainerId]--;
                    break;
                }
            }
        }

        // Evaluate withdrawals + tracked pulls per retainer (capped by actual available player bag space)
        foreach (var ret in knownRetainers)
        {
            var deposits = depositCountsByRetainer.GetValueOrDefault(ret.RetainerId);
            // Deposits free player bag slots on the same visit; crystals withdraw directly to the Crystal bag
            var availableBagSlots = emptyBagSlots + deposits;

            var trackedCount = 0;
            if (missingTracked.Count > 0)
            {
                foreach (var (trackedId, needQty) in missingTracked)
                {
                    if (needQty <= 0 || ret.Quantities.GetValueOrDefault(trackedId) <= 0)
                    {
                        continue;
                    }

                    if (ItemIdentity.IsCrystalItemId(trackedId))
                    {
                        trackedCount++;
                        continue;
                    }

                    if (availableBagSlots <= 0)
                    {
                        continue;
                    }

                    var tRow = itemSheet.GetRowOrDefault(trackedId);
                    if (tRow is not null && tRow.Value.IsUnique && playerUniqueBaseIds.Contains(trackedId))
                    {
                        continue;
                    }

                    trackedCount++;
                    availableBagSlots--;
                }
            }

            var withdrawCount = 0;
            var funnelDests = new HashSet<ulong>();

            if (availableBagSlots > 0 && manifestEntries.Count > 0 && ret.Quantities.Count > 0)
            {
                var localDestBudget = new Dictionary<ulong, int>(destFreeBudget);

                foreach (var (baseId, qty) in ret.Quantities)
                {
                    if (availableBagSlots <= 0)
                    {
                        break;
                    }

                    if (qty <= 0
                        || !ItemIdentity.CanLockOrTagBaseId(baseId)
                        || gearsetIds.Contains(baseId)
                        || safety.IsItemTracked(baseId))
                    {
                        continue;
                    }

                    if (!manifestEntries.TryGetValue(baseId, out var entry))
                    {
                        continue;
                    }

                    var row = itemSheet.GetRowOrDefault(baseId);
                    if (row is null
                        || ItemIdentity.IsCurrencyOrCrystal(baseId, row.Value)
                        || ItemIdentity.IsSoulCrystal(row.Value)
                        || row.Value.Rarity >= 4)
                    {
                        continue;
                    }

                    if (row.Value.IsUnique && playerUniqueBaseIds.Contains(baseId))
                    {
                        continue;
                    }

                    // Explicit manual "Keep" override pulls the item out of the retainer into Player Bags
                    if (string.Equals(entry.Route, "Keep", StringComparison.OrdinalIgnoreCase))
                    {
                        if (safety.HasExplicitKeepOverride(baseId))
                        {
                            withdrawCount++;
                            availableBagSlots--;
                        }

                        continue;
                    }

                    // Validate via ResolveEffectiveSlotRoute (liveItem: null) so safety-blocked items never trigger wasted visits
                    var effectiveRoute = safety.ResolveEffectiveSlotRoute(entry, row.Value, null, manifestEntries, gearsetIds, unlockedTop);
                    if (string.IsNullOrEmpty(effectiveRoute))
                    {
                        continue;
                    }

                    // 1. Any of the 8 action/disposal/storage routes that must leave the retainer -> withdraw!
                    if (IsWithdrawableRoute(effectiveRoute))
                    {
                        withdrawCount++;
                        availableBagSlots--;
                        continue;
                    }

                    // 2. Misplaced retainer item belonging on a DIFFERENT retainer that has space / partial merge
                    if (string.Equals(effectiveRoute, "Retainer", StringComparison.OrdinalIgnoreCase)
                        && entry.TargetRetainerId != 0
                        && entry.TargetRetainerId != ret.RetainerId)
                    {
                        var stackSize = (int)Math.Max(1, row.Value.StackSize);
                        var mergesPartial = stackSize > 1
                            && (holdings.HasPartialStackOnRetainer(entry.TargetRetainerId, baseId, isHq: false)
                                || holdings.HasPartialStackOnRetainer(entry.TargetRetainerId, baseId, isHq: true));
                        if (mergesPartial)
                        {
                            withdrawCount++;
                            availableBagSlots--;
                            funnelDests.Add(entry.TargetRetainerId);
                            continue;
                        }

                        var remSlots = localDestBudget.GetValueOrDefault(entry.TargetRetainerId);
                        if (remSlots > 0)
                        {
                            localDestBudget[entry.TargetRetainerId] = remSlots - 1;
                            withdrawCount++;
                            availableBagSlots--;
                            funnelDests.Add(entry.TargetRetainerId);
                        }
                    }
                }
            }

            result[ret.RetainerId] = new RetainerBellWorkSummary(
                ret.RetainerId,
                deposits,
                withdrawCount,
                trackedCount,
                funnelDests);
        }

        return result;
    }

    private static void CollectBaseIds(InventoryManager* manager, InventoryType containerType, HashSet<uint> target)
    {
        var container = manager->GetInventoryContainer(containerType);
        if (!InventorySafetyEvaluator.IsContainerReadable(container))
        {
            return;
        }

        for (var i = 0; i < container->Size; i++)
        {
            var slot = InventorySafetyEvaluator.GetSlot(container, i);
            if (slot != null && slot->ItemId != 0 && slot->Quantity > 0)
            {
                var baseId = ItemIdentity.NormalizeBaseItemId(slot->ItemId);
                if (baseId != 0)
                {
                    target.Add(baseId);
                }
            }
        }
    }
}
