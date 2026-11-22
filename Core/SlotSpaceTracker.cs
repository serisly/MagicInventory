using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace MagicInventory.Core;

public readonly record struct SlotTransferOutcome(
    bool Accepted,
    bool FreedSourceSlot,
    bool SplitTargetsEmptySlot,
    int MovedQuantity)
{
    public static readonly SlotTransferOutcome Rejected = new(false, false, false, 0);
}

public sealed class SlotSpaceTracker
{
    private readonly HashSet<uint> existingBaseIds = new();
    private readonly Dictionary<(uint RawItemId, InventoryItem.ItemFlags Flags), List<int>> partialSlots = new();
    private readonly HashSet<(uint BaseItemId, bool IsHq)> partialByQuality = new();

    public int EmptySlots { get; private set; }

    public void RecordEmptySlot() => EmptySlots++;

    public void FreeSlot() => EmptySlots++;

    public void RecordExistingBaseId(uint baseItemId)
    {
        if (baseItemId != 0)
        {
            existingBaseIds.Add(baseItemId);
        }
    }

    public void RecordOccupiedSlot(
        uint rawItemId,
        uint baseItemId,
        InventoryItem.ItemFlags flags,
        int quantity,
        int stackSize,
        bool isHq)
    {
        if (baseItemId != 0)
        {
            existingBaseIds.Add(baseItemId);
        }

        if (CanStack(flags, stackSize) && quantity > 0 && quantity < stackSize)
        {
            AddPartialRoom((rawItemId, flags), stackSize - quantity);
            if (baseItemId != 0)
            {
                partialByQuality.Add((baseItemId, isHq));
            }
        }
    }

    public bool HasBaseItem(uint baseItemId) => existingBaseIds.Contains(baseItemId);

    public bool HasPartialStackForQuality(uint baseItemId, bool isHq)
        => partialByQuality.Contains((baseItemId, isHq));

    public bool IsKnownFullFor(uint baseItemId, bool isHq, int stackSize)
        => EmptySlots <= 0 && !(stackSize > 1 && HasPartialStackForQuality(baseItemId, isHq));

    public int PeekFirstPartialRoom(uint rawItemId, InventoryItem.ItemFlags flags)
        => partialSlots.TryGetValue((rawItemId, flags), out var list) && list.Count > 0 ? list[0] : 0;

    public bool HasPartialRoom(uint rawItemId, InventoryItem.ItemFlags flags)
        => PeekFirstPartialRoom(rawItemId, flags) > 0;

    public bool CanReceive(
        uint rawItemId,
        InventoryItem.ItemFlags flags,
        int quantity,
        int stackSize,
        bool allowPartialRemainder = true)
    {
        var canStack = CanStack(flags, stackSize);
        var firstPartialRoom = canStack ? PeekFirstPartialRoom(rawItemId, flags) : 0;
        var fitsCompletelyInPartial = firstPartialRoom >= quantity;
        var fitsAnyPartial = firstPartialRoom > 0;
        if (fitsCompletelyInPartial)
        {
            return true;
        }

        if (allowPartialRemainder && fitsAnyPartial)
        {
            return true;
        }

        return EmptySlots > 0;
    }

    public SlotTransferOutcome TryCommitReceive(
        uint rawItemId,
        uint baseItemId,
        InventoryItem.ItemFlags flags,
        int quantity,
        int stackSize,
        bool isUnique,
        bool allowPartialRemainder = true)
    {
        var canStack = CanStack(flags, stackSize);
        var roomKey = (rawItemId, flags);
        var firstPartialRoom = canStack ? PeekFirstPartialRoom(rawItemId, flags) : 0;
        var fitsCompletelyInPartial = firstPartialRoom >= quantity;
        var fitsAnyPartial = firstPartialRoom > 0;

        if (!fitsCompletelyInPartial && EmptySlots <= 0 && (!allowPartialRemainder || !fitsAnyPartial))
        {
            return SlotTransferOutcome.Rejected;
        }

        bool freedSourceSlot;
        bool splitTargetsEmptySlot;
        int movedQty;

        if (fitsCompletelyInPartial || (allowPartialRemainder && fitsAnyPartial))
        {
            ConsumeFirstPartialRoom(roomKey, quantity, out freedSourceSlot);
            splitTargetsEmptySlot = false;
            movedQty = Math.Min(quantity, firstPartialRoom);
        }
        else
        {
            EmptySlots--;
            if (canStack && quantity < stackSize)
            {
                AddPartialRoom(roomKey, stackSize - quantity);
            }

            freedSourceSlot = true;
            splitTargetsEmptySlot = !allowPartialRemainder;
            movedQty = quantity;
        }

        if (isUnique && baseItemId != 0)
        {
            existingBaseIds.Add(baseItemId);
        }

        return new SlotTransferOutcome(true, freedSourceSlot, splitTargetsEmptySlot, movedQty);
    }

    private static bool CanStack(InventoryItem.ItemFlags flags, int stackSize)
        => stackSize > 1 && (flags & InventoryItem.ItemFlags.Collectable) == 0;

    private void AddPartialRoom((uint RawItemId, InventoryItem.ItemFlags Flags) key, int room)
    {
        if (room <= 0)
        {
            return;
        }

        if (!partialSlots.TryGetValue(key, out var list))
        {
            list = new List<int>(2);
            partialSlots[key] = list;
        }

        list.Add(room);
    }

    private void ConsumeFirstPartialRoom((uint RawItemId, InventoryItem.ItemFlags Flags) key, int qty, out bool completelyMerged)
    {
        completelyMerged = false;
        if (!partialSlots.TryGetValue(key, out var list) || list.Count == 0)
        {
            return;
        }

        var room = list[0];
        if (qty < room)
        {
            list[0] = room - qty;
            completelyMerged = true;
        }
        else
        {
            list.RemoveAt(0);
            completelyMerged = qty == room;
        }
    }
}
