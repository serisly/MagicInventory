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
    HashSet<ulong> FunnelDestinationIds,
    bool IsFull = false)
{
    public int TotalItemCount => DepositCount + WithdrawCount + TrackedWithdrawCount;
    public bool HasAnyWork => TotalItemCount > 0;
    public bool HasWithdrawWork => WithdrawCount > 0 || TrackedWithdrawCount > 0;
}

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

    public static bool IsWithdrawableRoute(ItemRoute? route)
    {
        if (!route.HasValue)
        {
            return false;
        }

        var kind = route.Value.Kind;
        if (kind == RouteKind.Armoire)
        {
            var uiState = FFXIVClientStructs.FFXIV.Client.Game.UI.UIState.Instance();
            return uiState != null && uiState->Cabinet.IsCabinetLoaded();
        }

        return kind is RouteKind.Market
            or RouteKind.Vendor
            or RouteKind.GrandCompany
            or RouteKind.GoldSaucer
            or RouteKind.Materia
            or RouteKind.Use
            or RouteKind.Glamour;
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
        var unlockedProfile = safety.BuildUnlockedJobGearProfile();
        var emptyBagSlots = (int)manager->GetEmptySlotsInBag();
        var missingTracked = getMissingTrackedQuantities();

        var playerUniqueBaseIds = new HashSet<uint>();
        foreach (var bagType in InventorySafetyEvaluator.PlayerBagContainers)
        {
            CollectBaseIds(manager, bagType, playerUniqueBaseIds);
        }

        foreach (var equipType in InventorySafetyEvaluator.ArmouryGearContainers)
        {
            CollectBaseIds(manager, equipType, playerUniqueBaseIds);
        }

        CollectBaseIds(manager, InventoryType.EquippedItems, playerUniqueBaseIds);

        var capacityList = new List<RetainerCapacityState>(knownRetainers.Count);
        var capacityById = new Dictionary<ulong, RetainerCapacityState>(knownRetainers.Count);
        var directWithdrawByRetainer = new Dictionary<ulong, int>(knownRetainers.Count);
        var trackedWithdrawByRetainer = new Dictionary<ulong, int>(knownRetainers.Count);

        foreach (var ret in knownRetainers)
        {
            var capturedRet = ret;
            var cap = new RetainerCapacityState
            {
                RetainerId = ret.RetainerId,
                BaseFreeSlots = holdings.GetKnownRetainerFreeSlots(ret.RetainerId),
                HasBaseItem = id => capturedRet.Quantities.GetValueOrDefault(id) > 0,
                HasPartialStack = (id, hq) => holdings.HasPartialStackOnRetainer(capturedRet.RetainerId, id, hq),
                IsKnownFullFor = (id, hq, stack) => holdings.IsRetainerKnownFullFor(capturedRet.RetainerId, id, hq, stack),
            };

            var projectedFreed = 0;
            var trackedCount = 0;
            if (missingTracked.Count > 0)
            {
                foreach (var (trackedId, needQty) in missingTracked)
                {
                    var retQty = ret.Quantities.GetValueOrDefault(trackedId);
                    if (needQty <= 0 || retQty <= 0)
                    {
                        continue;
                    }

                    if (ItemIdentity.IsCrystalItemId(trackedId))
                    {
                        if (HasPlayerCrystalRoom(manager, trackedId))
                        {
                            trackedCount++;
                        }

                        continue;
                    }

                    var tRow = itemSheet.GetRowOrDefault(trackedId);
                    if (tRow is not null && tRow.Value.IsUnique && playerUniqueBaseIds.Contains(trackedId))
                    {
                        continue;
                    }

                    trackedCount++;
                    if (needQty == int.MaxValue || retQty <= needQty || cap.BaseFreeSlots == 0)
                    {
                        projectedFreed++;
                    }
                }
            }

            var directWithdraws = 0;
            if (manifestEntries.Count > 0 && ret.Quantities.Count > 0)
            {
                foreach (var (baseId, qty) in ret.Quantities)
                {
                    if (qty <= 0
                        || !ItemIdentity.CanLockOrTagBaseId(baseId)
                        || gearsetIds.Contains(baseId)
                        || safety.IsItemTracked(baseId)
                        || !manifestEntries.TryGetValue(baseId, out var entry))
                    {
                        continue;
                    }

                    var row = itemSheet.GetRowOrDefault(baseId);
                    if (row is null
                        || ItemIdentity.IsCurrencyOrCrystal(baseId, row.Value)
                        || ItemIdentity.IsSoulCrystal(row.Value)
                        || ItemIdentity.IsRelicRarity(row.Value)
                        || (row.Value.IsUnique && playerUniqueBaseIds.Contains(baseId)))
                    {
                        continue;
                    }

                    if (entry.Route.IsKeep)
                    {
                        if (safety.HasExplicitKeepOverride(baseId))
                        {
                            directWithdraws++;
                            projectedFreed++;
                        }

                        continue;
                    }

                    var hqQty = ret.HqQuantities?.GetValueOrDefault(baseId) ?? 0;
                    var hasNq = qty > hqQty;
                    var hasHq = hqQty > 0;
                    var effectiveRoute = safety.ResolveEffectiveSlotRoute(
                        entry,
                        row.Value,
                        null,
                        manifestEntries,
                        gearsetIds,
                        unlockedProfile,
                        hasNq: hasNq,
                        hasHq: hasHq);
                    if (IsWithdrawableRoute(effectiveRoute))
                    {
                        directWithdraws++;
                        projectedFreed++;
                    }
                }
            }

            cap.ProjectedWithdrawalFreedSlots = projectedFreed;
            capacityList.Add(cap);
            capacityById[ret.RetainerId] = cap;
            trackedWithdrawByRetainer[ret.RetainerId] = trackedCount;
            directWithdrawByRetainer[ret.RetainerId] = directWithdraws;
        }

        RetainerTransferPolicy.InitializeProjectedBudgets(capacityList, emptyBagSlots);

        var depositCountsByRetainer = new Dictionary<ulong, int>(knownRetainers.Count);
        if (manifestEntries.Count > 0)
        {
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

                    var resolved = slotResolver.ResolveSlot(
                        slot->ItemId,
                        bagType,
                        slot,
                        manifestEntries,
                        gearsetIds,
                        unlockedProfile,
                        includeBagToArmouryRoute: false,
                        missingTracked: missingTracked);
                    if (resolved is null || !resolved.Value.Route.IsSpecificRetainer)
                    {
                        continue;
                    }

                    var baseId = ItemIdentity.NormalizeBaseItemId(slot->ItemId);
                    var row = itemSheet.GetRowOrDefault(baseId);
                    if (row is null)
                    {
                        continue;
                    }

                    var isHq = ItemIdentity.IsHighQuality(slot);
                    var stackSize = (int)Math.Max(1, row.Value.StackSize);
                    var decision = RetainerTransferPolicy.ResolveDepositTarget(
                        resolved.Value.TargetRetainerId,
                        baseId,
                        isHq,
                        row.Value.IsUnique,
                        stackSize,
                        capacityList,
                        capacityById,
                        consumeBudget: true);
                    if (decision.TargetRetainerId != 0)
                    {
                        depositCountsByRetainer[decision.TargetRetainerId] =
                            depositCountsByRetainer.GetValueOrDefault(decision.TargetRetainerId) + 1;
                    }
                }
            }
        }

        foreach (var ret in knownRetainers)
        {
            var deposits = depositCountsByRetainer.GetValueOrDefault(ret.RetainerId);
            var availableBagSlots = emptyBagSlots + deposits;

            var crystalTracked = 0;
            var bagTracked = 0;
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
                        if (HasPlayerCrystalRoom(manager, trackedId))
                        {
                            crystalTracked++;
                        }

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

                    bagTracked++;
                    availableBagSlots--;
                }
            }

            var trackedCount = crystalTracked + bagTracked;
            var withdrawCount = Math.Min(availableBagSlots, directWithdrawByRetainer.GetValueOrDefault(ret.RetainerId));
            availableBagSlots -= withdrawCount;

            var funnelDests = new HashSet<ulong>();
            if (availableBagSlots > 0 && manifestEntries.Count > 0 && ret.Quantities.Count > 0)
            {
                var localCapacityById = new Dictionary<ulong, RetainerCapacityState>(capacityById.Count);
                foreach (var (rid, state) in capacityById)
                {
                    localCapacityById[rid] = new RetainerCapacityState
                    {
                        RetainerId = state.RetainerId,
                        BaseFreeSlots = state.BaseFreeSlots,
                        ProjectedWithdrawalFreedSlots = state.ProjectedWithdrawalFreedSlots,
                        RemainingFreeSlots = state.RemainingFreeSlots,
                        HasBaseItem = state.HasBaseItem,
                        HasPartialStack = state.HasPartialStack,
                        IsKnownFullFor = state.IsKnownFullFor,
                    };
                }

                foreach (var (baseId, qty) in ret.Quantities)
                {
                    if (availableBagSlots <= 0)
                    {
                        break;
                    }

                    if (qty <= 0
                        || !ItemIdentity.CanLockOrTagBaseId(baseId)
                        || gearsetIds.Contains(baseId)
                        || safety.IsItemTracked(baseId)
                        || !manifestEntries.TryGetValue(baseId, out var entry))
                    {
                        continue;
                    }

                    var row = itemSheet.GetRowOrDefault(baseId);
                    if (row is null
                        || ItemIdentity.IsCurrencyOrCrystal(baseId, row.Value)
                        || ItemIdentity.IsSoulCrystal(row.Value)
                        || ItemIdentity.IsRelicRarity(row.Value)
                        || (row.Value.IsUnique && playerUniqueBaseIds.Contains(baseId)))
                    {
                        continue;
                    }

                    var hqQty = ret.HqQuantities?.GetValueOrDefault(baseId) ?? 0;
                    var hasNq = qty > hqQty;
                    var hasHq = hqQty > 0;
                    var effectiveRoute = safety.ResolveEffectiveSlotRoute(
                        entry,
                        row.Value,
                        null,
                        manifestEntries,
                        gearsetIds,
                        unlockedProfile,
                        hasNq: hasNq,
                        hasHq: hasHq);
                    if (!effectiveRoute.HasValue
                        || !effectiveRoute.Value.IsSpecificRetainer
                        || effectiveRoute.Value.RetainerId == ret.RetainerId)
                    {
                        continue;
                    }

                    var stackSize = (int)Math.Max(1, row.Value.StackSize);
                    var funnel = RetainerTransferPolicy.EvaluateCrossRetainerFunnel(
                        ret.RetainerId,
                        entry.TargetRetainerId,
                        baseId,
                        hasNq,
                        hasHq,
                        row.Value.IsUnique,
                        stackSize,
                        localCapacityById,
                        consumeBudget: true);
                    if (funnel.CanFunnel)
                    {
                        withdrawCount++;
                        availableBagSlots--;
                        funnelDests.Add(entry.TargetRetainerId);
                    }
                }
            }

            result[ret.RetainerId] = new RetainerBellWorkSummary(
                ret.RetainerId,
                deposits,
                withdrawCount,
                trackedCount,
                funnelDests,
                IsFull: holdings.IsRetainerVisited(ret.RetainerId) && holdings.GetKnownRetainerFreeSlots(ret.RetainerId) <= 0);
        }

        return result;
    }

    private static bool HasPlayerCrystalRoom(InventoryManager* manager, uint crystalBaseId)
    {
        var crystalBag = manager->GetInventoryContainer(InventoryType.Crystals);
        var slotIdx = (int)(crystalBaseId - 2);
        var slot = InventorySafetyEvaluator.GetSlot(crystalBag, slotIdx);
        return slot == null || slot->ItemId != crystalBaseId || slot->Quantity < 9999;
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
