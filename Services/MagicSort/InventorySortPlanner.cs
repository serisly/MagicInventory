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

/// <summary>
/// Safe Bag <-> Armoury Chest organizer + partial stack consolidator.
/// Never runs native /isort (preserves the player's personal bag layout).
/// - Merges split partial stacks of identical items inside Bags.
/// - Pulls non-Keep gear (Market, Vendor, GC Seals, Glamour Dresser, Armoire, Retainer) out of Armoury Chest into Bags.
/// - Moves Keep gear from Bags into its matching Armoury Chest tab when space is available.
/// </summary>
public sealed unsafe class InventorySortPlanner
{
    private const int MaxMovesPerClick = 20;

    private readonly IDataManager dataManager;
    private readonly ICondition condition;
    private readonly SafeTransactionQueue transactionQueue;
    private readonly InventorySafetyEvaluator safety;

    public InventorySortPlanner(
        IDataManager dataManager,
        ICondition condition,
        SafeTransactionQueue transactionQueue,
        InventorySafetyEvaluator safety)
    {
        this.dataManager = dataManager;
        this.condition = condition;
        this.transactionQueue = transactionQueue;
        this.safety = safety;
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
        var unlockedTop = safety.BuildUnlockedJobTopIlvl();
        var usedSrc = new HashSet<(InventoryType Container, int Slot)>();

        // Count current free slots in Bags and in each Armoury container
        var bagFreeSlots = CountEmptySlots(manager, InventorySafetyEvaluator.PlayerBagContainers);
        var armouryFreeByType = new Dictionary<InventoryType, int>();
        foreach (var armType in InventorySafetyEvaluator.ArmouryGearContainers)
        {
            armouryFreeByType[armType] = CountEmptySlots(manager, [armType]);
        }

        armouryFreeByType[InventoryType.ArmorySoulCrystal] = CountEmptySlots(manager, [InventoryType.ArmorySoulCrystal]);

        // 1. Merge split partial stacks inside Bags (identical RawItemId = same item + same NQ/HQ)
        // Keyed by (ItemId, IsHq) so NQ and HQ stacks are never "merged" (which would just swap them).
        var partialStacks = new Dictionary<(uint ItemId, bool IsHq), List<(InventoryType Container, int Slot, int Qty, int StackSize, uint BaseId, string Name)>>();
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

                var baseId = ItemIdentity.NormalizeBaseItemId(slot->ItemId);
                var row = itemSheet.GetRowOrDefault(baseId);
                if (row is null || row.Value.StackSize <= 1 || slot->Quantity >= row.Value.StackSize)
                {
                    continue;
                }

                var stackKey = (slot->ItemId, slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality));
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

            // Merge later partial stacks into the earliest partial stack
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
                    // dst is now full and src still holds a remainder: promote src as the next destination!
                    dst = (src.Container, src.Slot, src.Qty - merged, src.StackSize, src.BaseId, src.Name);
                    dstQty = dst.Qty;
                }
            }
        }

        // 2. Bags -> Armoury Chest FIRST for Keep gear when target Armoury tab has free space
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
                if (!ItemIdentity.CanLockOrTagBaseId(baseId))
                {
                    continue;
                }

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

                // Only move Keep gear into Armoury Chest (not GC Seals, Vendor, Market, Glamour, Armoire, Retainer)
                string effectiveRoute = "Keep";
                if (!gearsetIds.Contains(baseId)
                    && !safety.IsItemTracked(baseId)
                    && !ItemIdentity.IsSoulCrystal(row.Value)
                    && row.Value.Rarity < 4
                    && manifestEntries.TryGetValue(baseId, out var entry)
                    && !string.Equals(entry.Route, "Keep", StringComparison.OrdinalIgnoreCase))
                {
                    effectiveRoute = safety.ResolveEffectiveSlotRoute(entry, row.Value, slot, manifestEntries, gearsetIds, unlockedTop) ?? "Keep";
                }

                if (!string.Equals(effectiveRoute, "Keep", StringComparison.OrdinalIgnoreCase))
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
                armouryFreeByType[targetArmoury.Value] = armouryFreeByType.GetValueOrDefault(targetArmoury.Value) - 1;
                bagFreeSlots++;
            }
        }

        // 3. Armoury Chest -> Bags ONLY for Market, Vendor, and Retainer gear
        //    (GC Seals turns in straight from Armoury Chest; Glamour/Armoire stay in Armoury until you visit an Inn)
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
                var slot = InventorySafetyEvaluator.GetSlot(container, i);
                if (slot == null || slot->ItemId == 0 || slot->Quantity <= 0)
                {
                    continue;
                }

                var rawId = slot->ItemId;
                var baseId = ItemIdentity.NormalizeBaseItemId(rawId);
                if (!ItemIdentity.CanLockOrTagBaseId(baseId)
                    || gearsetIds.Contains(baseId)
                    || safety.IsItemTracked(baseId))
                {
                    continue;
                }

                var row = itemSheet.GetRowOrDefault(baseId);
                if (row is null || row.Value.Rarity >= 4)
                {
                    continue;
                }

                if (InventorySafetyEvaluator.IsMateriaExtractable(row.Value, slot))
                {
                    continue;
                }

                if (!manifestEntries.TryGetValue(baseId, out var entry))
                {
                    continue;
                }

                var effRoute = safety.ResolveEffectiveSlotRoute(entry, row.Value, slot, manifestEntries, gearsetIds, unlockedTop);
                if (effRoute is not ("Market" or "Vendor" or "Retainer"))
                {
                    continue;
                }

                moves.Add(new InventorySortMove(
                    InventorySortMoveKind.ArmouryToBag,
                    armType,
                    i,
                    InventoryType.Inventory1, // Actual live empty bag slot is resolved at dispatch time
                    -1,
                    rawId,
                    baseId,
                    row.Value.Name.ExtractText()));
                usedSrc.Add((armType, i));
                bagFreeSlots--;
                armouryFreeByType[armType] = armouryFreeByType.GetValueOrDefault(armType) + 1;
            }
        }

        return moves;

        static uint slotId(uint fallbackBaseId, InventoryType cType, int idx, InventoryManager* mgr)
        {
            var c = mgr->GetInventoryContainer(cType);
            var s = InventorySafetyEvaluator.GetSlot(c, idx);
            return s != null && s->ItemId != 0 ? s->ItemId : fallbackBaseId;
        }
    }

    public int CountPlannedMoves(IReadOnlyDictionary<uint, MagicSortEntry> manifestEntries)
        => PlanMoves(MaxMovesPerClick, manifestEntries).Count;

    public bool Execute(IReadOnlyDictionary<uint, MagicSortEntry> manifestEntries, out string statusMessage)
    {
        statusMessage = string.Empty;
        if (transactionQueue.IsBusy)
        {
            statusMessage = "Busy — wait for the current action to finish.";
            return false;
        }

        if (condition[ConditionFlag.InCombat]
            || condition[ConditionFlag.Casting]
            || condition[ConditionFlag.BoundByDuty]
            || condition[ConditionFlag.OccupiedInEvent]
            || condition[ConditionFlag.OccupiedInCutSceneEvent])
        {
            statusMessage = "Cannot sort bags right now.";
            return false;
        }

        var plan = PlanMoves(MaxMovesPerClick, manifestEntries);
        if (plan.Count == 0)
        {
            statusMessage = "Bags and Armoury Chest are already tidy. Visit a Summoning Bell for retainer items.";
            return false;
        }

        var steps = new List<TransactionStep>(plan.Count);
        foreach (var move in plan)
        {
            var captured = move;
            var expectedQtyBefore = 0;
            var dispatched = false;

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
                        dispatched = true;
                        expectedQtyBefore = 0;
                        return true;
                    }

                    InventoryType dstContainer;
                    int dstSlot;

                    if (captured.Kind == InventorySortMoveKind.MergeStack)
                    {
                        dstContainer = captured.DstContainer;
                        dstSlot = captured.DstSlot;
                        var dst = InventorySafetyEvaluator.GetSlot(mgr, dstContainer, dstSlot);
                        if (dst == null || dst->ItemId != captured.RawItemId || dst->Flags != srcSlot->Flags)
                        {
                            return false;
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

        if (!transactionQueue.TryEnqueueBatch(steps, out var reason))
        {
            statusMessage = string.IsNullOrWhiteSpace(reason) ? "Unable to start bag sort." : reason;
            return false;
        }

        return true;
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
