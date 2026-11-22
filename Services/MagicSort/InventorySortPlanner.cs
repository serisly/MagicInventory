using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using MagicInventory.Core;

namespace MagicInventory.Services.MagicSort;

public enum InventorySortMoveKind
{
    MergeStack,
    BagToArmoury,
    ArmouryToBag,
}

public sealed record InventorySortMove(
    InventorySortMoveKind Kind,
    InventoryType SrcContainer,
    int SrcSlot,
    InventoryType DstContainer,
    int DstSlot,
    uint RawItemId,
    uint BaseItemId,
    string Name);

public sealed unsafe class InventorySortPlanner
{
    private const int MaxMovesPerClick = 20;

    private readonly IDataManager dataManager;
    private readonly ICondition condition;
    private readonly SafeTransactionQueue transactionQueue;
    private readonly InventorySafetyEvaluator safety;
    private readonly SlotRouteResolver routeResolver;

    public InventorySortPlanner(
        IDataManager dataManager,
        ICondition condition,
        SafeTransactionQueue transactionQueue,
        InventorySafetyEvaluator safety,
        SlotRouteResolver routeResolver)
    {
        this.dataManager = dataManager;
        this.condition = condition;
        this.transactionQueue = transactionQueue;
        this.safety = safety;
        this.routeResolver = routeResolver;
    }

    public List<InventorySortMove> PlanMoves(int maxMoves, IReadOnlyDictionary<uint, MagicSortEntry> manifestEntries)
    {
        var moves = new List<InventorySortMove>(Math.Max(0, maxMoves));
        var manager = InventoryManager.Instance();
        var itemSheet = dataManager.GetExcelSheet<Item>();
        if (manager == null || itemSheet == null || maxMoves <= 0)
        {
            return moves;
        }

        var gearsetIds = safety.BuildGearsetItemIds();
        var unlockedProfile = safety.BuildUnlockedJobGearProfile();
        var missingTracked = routeResolver.CaptureMissingTrackedSnapshot();
        var usedSrc = new HashSet<(InventoryType Container, int Slot)>();

        var bagFreeSlots = CountEmptySlots(manager, InventorySafetyEvaluator.PlayerBagContainers);
        var armouryFreeByType = new Dictionary<InventoryType, int>();
        foreach (var armType in InventorySafetyEvaluator.ArmouryGearContainers)
        {
            armouryFreeByType[armType] = CountEmptySlots(manager, [armType]);
        }

        var partialStacks = new Dictionary<(uint ItemId, InventoryItem.ItemFlags Flags), List<(InventoryType Container, int Slot, int Qty, int StackSize, uint BaseId, string Name)>>();
        foreach (var bag in InventorySafetyEvaluator.PlayerBagContainers)
        {
            var container = manager->GetInventoryContainer(bag);
            if (!InventorySafetyEvaluator.IsContainerReadable(container))
            {
                continue;
            }

            for (var i = 0; i < container->Size; i++)
            {
                var slot = InventorySafetyEvaluator.GetSlot(container, i);
                if (slot == null || slot->ItemId == 0 || slot->Quantity <= 0)
                {
                    continue;
                }

                if (ItemIdentity.IsCollectable(slot))
                {
                    continue;
                }

                var baseId = ItemIdentity.NormalizeBaseItemId(slot->ItemId);
                var row = itemSheet.GetRowOrDefault(baseId);
                if (row is null || row.Value.StackSize <= 1 || slot->Quantity >= row.Value.StackSize)
                {
                    continue;
                }

                var stackKey = (slot->ItemId, slot->Flags);
                if (!partialStacks.TryGetValue(stackKey, out var list))
                {
                    list = new List<(InventoryType, int, int, int, uint, string)>();
                    partialStacks[stackKey] = list;
                }

                list.Add((bag, i, slot->Quantity, (int)row.Value.StackSize, baseId, row.Value.Name.ExtractText()));
            }
        }

        foreach (var (_, list) in partialStacks)
        {
            if (list.Count < 2)
            {
                continue;
            }

            var dst = list[0];
            var dstQty = dst.Qty;
            for (var k = 1; k < list.Count && moves.Count < maxMoves; k++)
            {
                var src = list[k];
                if (dstQty >= dst.StackSize)
                {
                    dst = src;
                    dstQty = src.Qty;
                    continue;
                }

                moves.Add(new InventorySortMove(
                    InventorySortMoveKind.MergeStack,
                    src.Container,
                    src.Slot,
                    dst.Container,
                    dst.Slot,
                    slotId(src.BaseId, src.Container, src.Slot, manager),
                    src.BaseId,
                    src.Name));
                usedSrc.Add((src.Container, src.Slot));

                var merged = Math.Min(dst.StackSize - dstQty, src.Qty);
                dstQty += merged;
                if (merged == src.Qty)
                {
                    bagFreeSlots++;
                }
                else
                {
                    dst = (src.Container, src.Slot, src.Qty - merged, src.StackSize, src.BaseId, src.Name);
                    dstQty = dst.Qty;
                }
            }
        }

        void PlanArmouryToBag()
        {
            foreach (var armType in InventorySafetyEvaluator.ArmouryGearContainers)
            {
                if (moves.Count >= maxMoves || bagFreeSlots <= 0)
                {
                    break;
                }

                var container = manager->GetInventoryContainer(armType);
                if (!InventorySafetyEvaluator.IsContainerReadable(container))
                {
                    continue;
                }

                for (var i = 0; i < container->Size && moves.Count < maxMoves && bagFreeSlots > 0; i++)
                {
                    if (usedSrc.Contains((armType, i)))
                    {
                        continue;
                    }

                    var slot = InventorySafetyEvaluator.GetSlot(container, i);
                    if (slot == null || slot->ItemId == 0 || slot->Quantity <= 0)
                    {
                        continue;
                    }

                    var rawId = slot->ItemId;
                    var resolved = routeResolver.ResolveSlot(
                        rawId,
                        armType,
                        slot,
                        manifestEntries,
                        gearsetIds,
                        unlockedProfile,
                        includeBagToArmouryRoute: false,
                        missingTracked: missingTracked);
                    if (!resolved.HasValue || resolved.Value.Route.Kind is not (RouteKind.Market or RouteKind.Vendor or RouteKind.Retainer))
                    {
                        continue;
                    }

                    var baseId = ItemIdentity.NormalizeBaseItemId(rawId);
                    var row = itemSheet.GetRowOrDefault(baseId);
                    if (row is null)
                    {
                        continue;
                    }

                    moves.Add(new InventorySortMove(
                        InventorySortMoveKind.ArmouryToBag,
                        armType,
                        i,
                        InventoryType.Inventory1,
                        -1,
                        rawId,
                        baseId,
                        row.Value.Name.ExtractText()));
                    usedSrc.Add((armType, i));
                    bagFreeSlots--;
                    armouryFreeByType[armType] = armouryFreeByType.GetValueOrDefault(armType) + 1;
                }
            }
        }

        void PlanBagToArmoury()
        {
            foreach (var bag in InventorySafetyEvaluator.PlayerBagContainers)
            {
                if (moves.Count >= maxMoves)
                {
                    break;
                }

                var container = manager->GetInventoryContainer(bag);
                if (!InventorySafetyEvaluator.IsContainerReadable(container))
                {
                    continue;
                }

                for (var i = 0; i < container->Size && moves.Count < maxMoves; i++)
                {
                    if (usedSrc.Contains((bag, i)))
                    {
                        continue;
                    }

                    var slot = InventorySafetyEvaluator.GetSlot(container, i);
                    if (slot == null || slot->ItemId == 0 || slot->Quantity <= 0)
                    {
                        continue;
                    }

                    var rawId = slot->ItemId;
                    var baseId = ItemIdentity.NormalizeBaseItemId(rawId);
                    var row = itemSheet.GetRowOrDefault(baseId);
                    if (row is null || row.Value.EquipSlotCategory.RowId == 0)
                    {
                        continue;
                    }

                    var targetArmoury = InventorySafetyEvaluator.ResolveArmouryContainerForItem(row.Value);
                    if (!targetArmoury.HasValue || armouryFreeByType.GetValueOrDefault(targetArmoury.Value) <= 0)
                    {
                        continue;
                    }

                    var resolved = routeResolver.ResolveSlot(
                        rawId,
                        bag,
                        slot,
                        manifestEntries,
                        gearsetIds,
                        unlockedProfile,
                        includeBagToArmouryRoute: true,
                        missingTracked: missingTracked);
                    if (!resolved.HasValue || resolved.Value.Route.Kind != RouteKind.Armoury)
                    {
                        continue;
                    }

                    moves.Add(new InventorySortMove(
                        InventorySortMoveKind.BagToArmoury,
                        bag,
                        i,
                        targetArmoury.Value,
                        -1,
                        rawId,
                        baseId,
                        row.Value.Name.ExtractText()));
                    usedSrc.Add((bag, i));
                    armouryFreeByType[targetArmoury.Value] = armouryFreeByType.GetValueOrDefault(targetArmoury.Value) - 1;
                    bagFreeSlots++;
                }
            }
        }

        if (bagFreeSlots > 0)
        {
            PlanArmouryToBag();
            PlanBagToArmoury();
        }
        else
        {
            PlanBagToArmoury();
            PlanArmouryToBag();
        }

        return moves;

        static uint slotId(uint fallbackBaseId, InventoryType cType, int idx, InventoryManager* mgr)
        {
            var c = mgr->GetInventoryContainer(cType);
            var s = InventorySafetyEvaluator.GetSlot(c, idx);
            return s != null && s->ItemId != 0 ? s->ItemId : fallbackBaseId;
        }
    }

    public List<(InventoryType Container, int Slot)> GetPlannedMoveSlots(IReadOnlyDictionary<uint, MagicSortEntry> manifestEntries)
    {
        var moves = PlanMoves(MaxMovesPerClick, manifestEntries);
        var slots = new List<(InventoryType, int)>(moves.Count);
        foreach (var move in moves)
        {
            slots.Add((move.SrcContainer, move.SrcSlot));
        }

        return slots;
    }

    public int CountPlannedMoves(IReadOnlyDictionary<uint, MagicSortEntry> manifestEntries)
        => PlanMoves(MaxMovesPerClick, manifestEntries).Count;

    public List<TransactionStep> BuildSteps(
        IReadOnlyDictionary<uint, MagicSortEntry> manifestEntries,
        out int moveCount,
        out string statusMessage)
    {
        moveCount = 0;
        statusMessage = string.Empty;

        if (condition[ConditionFlag.InCombat]
            || condition[ConditionFlag.Casting]
            || condition[ConditionFlag.BoundByDuty]
            || condition[ConditionFlag.OccupiedInEvent]
            || condition[ConditionFlag.OccupiedInCutSceneEvent])
        {
            statusMessage = "Cannot sort bags right now.";
            return new List<TransactionStep>();
        }

        var plan = PlanMoves(MaxMovesPerClick, manifestEntries);
        if (plan.Count == 0)
        {
            statusMessage = "Bags and Armoury Chest are already tidy. Visit a Summoning Bell for retainer items.";
            return new List<TransactionStep>();
        }

        moveCount = plan.Count;
        var steps = new List<TransactionStep>(plan.Count);
        foreach (var move in plan)
        {
            var captured = move;
            var expectedQtyBefore = 0;
            var dispatched = false;
            var skipped = false;

            steps.Add(new TransactionStep(
                $"Move {captured.Name}",
                RequiredAddon: string.Empty,
                Dispatch: () =>
                {
                    var mgr = InventoryManager.Instance();
                    if (mgr == null)
                    {
                        return false;
                    }

                    var srcSlot = InventorySafetyEvaluator.GetSlot(mgr, captured.SrcContainer, captured.SrcSlot);
                    if (srcSlot == null || srcSlot->ItemId != captured.RawItemId || srcSlot->Quantity <= 0)
                    {
                        skipped = true;
                        return true;
                    }

                    InventoryType dstContainer;
                    int dstSlot;

                    if (captured.Kind == InventorySortMoveKind.MergeStack)
                    {
                        dstContainer = captured.DstContainer;
                        dstSlot = captured.DstSlot;
                        var dst = InventorySafetyEvaluator.GetSlot(mgr, dstContainer, dstSlot);
                        var row = dataManager.GetExcelSheet<Item>()?.GetRowOrDefault(captured.BaseItemId);
                        var maxStack = row is not null ? (int)Math.Max(1, row.Value.StackSize) : 1;
                        if (dst == null
                            || dst->ItemId != captured.RawItemId
                            || dst->Flags != srcSlot->Flags
                            || dst->Quantity >= maxStack)
                        {
                            skipped = true;
                            return true;
                        }
                    }
                    else if (captured.Kind == InventorySortMoveKind.ArmouryToBag)
                    {
                        if (!TryFindFirstEmptyBagSlot(mgr, out dstContainer, out dstSlot))
                        {
                            transactionQueue.Cancel("Stopped: bags are full.", QueueBatchOutcome.AbortedOnError);
                            return false;
                        }
                    }
                    else
                    {
                        dstContainer = captured.DstContainer;
                        if (!TryFindFirstEmptySlot(mgr, dstContainer, out dstSlot))
                        {
                            transactionQueue.Cancel("Stopped: Armoury Chest tab is full.", QueueBatchOutcome.AbortedOnError);
                            return false;
                        }
                    }

                    expectedQtyBefore = srcSlot->Quantity;
                    mgr->MoveItemSlot(captured.SrcContainer, (ushort)captured.SrcSlot, dstContainer, (ushort)dstSlot, true);
                    dispatched = true;
                    return true;
                },
                HasServerAck: () =>
                {
                    if (skipped)
                    {
                        return true;
                    }

                    if (!dispatched)
                    {
                        return false;
                    }

                    var mgr = InventoryManager.Instance();
                    if (mgr == null)
                    {
                        return false;
                    }

                    var srcSlot = InventorySafetyEvaluator.GetSlot(mgr, captured.SrcContainer, captured.SrcSlot);
                    return srcSlot == null || srcSlot->ItemId != captured.RawItemId || srcSlot->Quantity < expectedQtyBefore;
                },
                PacingProfile.StandardSlot,
                TimeoutSeconds: 4.5));
        }

        return steps;
    }

    private static int CountEmptySlots(InventoryManager* mgr, ReadOnlySpan<InventoryType> containers)
    {
        var empty = 0;
        foreach (var cType in containers)
        {
            var c = mgr->GetInventoryContainer(cType);
            if (!InventorySafetyEvaluator.IsContainerReadable(c))
            {
                continue;
            }

            for (var i = 0; i < c->Size; i++)
            {
                var s = c->GetInventorySlot(i);
                if (s == null || s->ItemId == 0 || s->Quantity <= 0)
                {
                    empty++;
                }
            }
        }

        return empty;
    }

    private static bool TryFindFirstEmptySlot(InventoryManager* mgr, InventoryType containerType, out int emptySlot)
    {
        emptySlot = -1;
        var c = mgr->GetInventoryContainer(containerType);
        if (!InventorySafetyEvaluator.IsContainerReadable(c))
        {
            return false;
        }

        for (var i = 0; i < c->Size; i++)
        {
            var s = c->GetInventorySlot(i);
            if (s == null || s->ItemId == 0 || s->Quantity <= 0)
            {
                emptySlot = i;
                return true;
            }
        }

        return false;
    }

    private static bool TryFindFirstEmptyBagSlot(InventoryManager* mgr, out InventoryType bagContainer, out int emptySlot)
    {
        foreach (var bag in InventorySafetyEvaluator.PlayerBagContainers)
        {
            if (TryFindFirstEmptySlot(mgr, bag, out emptySlot))
            {
                bagContainer = bag;
                return true;
            }
        }

        bagContainer = InventoryType.Invalid;
        emptySlot = -1;
        return false;
    }
}
