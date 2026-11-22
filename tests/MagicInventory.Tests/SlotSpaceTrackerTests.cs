using FFXIVClientStructs.FFXIV.Client.Game;
using MagicInventory.Core;
using Xunit;

namespace MagicInventory.Tests;

public sealed class SlotSpaceTrackerTests
{
    [Fact]
    public void PartialMerge_ConsumesRoomAndFreesSourceOnlyWhenCompletelyMerged()
    {
        var tracker = new SlotSpaceTracker();
        tracker.RecordOccupiedSlot(100, 100, InventoryItem.ItemFlags.None, quantity: 90, stackSize: 99, isHq: false);

        Assert.Equal(9, tracker.PeekFirstPartialRoom(100, InventoryItem.ItemFlags.None));
        Assert.False(tracker.IsKnownFullFor(100, isHq: false, stackSize: 99));
        Assert.True(tracker.IsKnownFullFor(100, isHq: true, stackSize: 99));

        var first = tracker.TryCommitReceive(100, 100, InventoryItem.ItemFlags.None, quantity: 5, stackSize: 99, isUnique: false);
        Assert.True(first.Accepted);
        Assert.True(first.FreedSourceSlot);
        Assert.Equal(5, first.MovedQuantity);
        Assert.Equal(4, tracker.PeekFirstPartialRoom(100, InventoryItem.ItemFlags.None));

        var overflow = tracker.TryCommitReceive(100, 100, InventoryItem.ItemFlags.None, quantity: 10, stackSize: 99, isUnique: false);
        Assert.True(overflow.Accepted);
        Assert.False(overflow.FreedSourceSlot);
        Assert.Equal(4, overflow.MovedQuantity);
        Assert.Equal(0, tracker.PeekFirstPartialRoom(100, InventoryItem.ItemFlags.None));

        var blocked = tracker.TryCommitReceive(100, 100, InventoryItem.ItemFlags.None, quantity: 1, stackSize: 99, isUnique: false);
        Assert.False(blocked.Accepted);
    }

    [Fact]
    public void EmptySlotReceive_CreatesNewPartialStackForSubsequentMerge()
    {
        var tracker = new SlotSpaceTracker();
        tracker.RecordEmptySlot();

        var first = tracker.TryCommitReceive(200, 200, InventoryItem.ItemFlags.HighQuality, quantity: 30, stackSize: 99, isUnique: false);
        Assert.True(first.Accepted);
        Assert.True(first.FreedSourceSlot);
        Assert.Equal(0, tracker.EmptySlots);
        Assert.Equal(69, tracker.PeekFirstPartialRoom(200, InventoryItem.ItemFlags.HighQuality));

        var second = tracker.TryCommitReceive(200, 200, InventoryItem.ItemFlags.HighQuality, quantity: 69, stackSize: 99, isUnique: false);
        Assert.True(second.Accepted);
        Assert.True(second.FreedSourceSlot);
        Assert.Equal(0, tracker.PeekFirstPartialRoom(200, InventoryItem.ItemFlags.HighQuality));
    }

    [Fact]
    public void SplitReceive_RejectsPartialRemainderWhenNoEmptySlotExists()
    {
        var tracker = new SlotSpaceTracker();
        tracker.RecordOccupiedSlot(300, 300, InventoryItem.ItemFlags.None, quantity: 95, stackSize: 99, isHq: false);

        Assert.False(tracker.CanReceive(300, InventoryItem.ItemFlags.None, quantity: 10, stackSize: 99, allowPartialRemainder: false));
        var rejected = tracker.TryCommitReceive(300, 300, InventoryItem.ItemFlags.None, quantity: 10, stackSize: 99, isUnique: false, allowPartialRemainder: false);
        Assert.False(rejected.Accepted);
        Assert.Equal(4, tracker.PeekFirstPartialRoom(300, InventoryItem.ItemFlags.None));

        tracker.RecordEmptySlot();
        var emptySlotSplit = tracker.TryCommitReceive(300, 300, InventoryItem.ItemFlags.None, quantity: 10, stackSize: 99, isUnique: false, allowPartialRemainder: false);
        Assert.True(emptySlotSplit.Accepted);
        Assert.True(emptySlotSplit.SplitTargetsEmptySlot);
        Assert.Equal(10, emptySlotSplit.MovedQuantity);
        Assert.Equal(0, tracker.EmptySlots);
        Assert.Equal(4, tracker.PeekFirstPartialRoom(300, InventoryItem.ItemFlags.None));
    }

    [Fact]
    public void SplitReceive_UsesExistingPartialWhenQuantityFitsCompletely()
    {
        var tracker = new SlotSpaceTracker();
        tracker.RecordOccupiedSlot(400, 400, InventoryItem.ItemFlags.None, quantity: 80, stackSize: 99, isHq: false);

        var merged = tracker.TryCommitReceive(400, 400, InventoryItem.ItemFlags.None, quantity: 15, stackSize: 99, isUnique: false, allowPartialRemainder: false);
        Assert.True(merged.Accepted);
        Assert.False(merged.SplitTargetsEmptySlot);
        Assert.Equal(15, merged.MovedQuantity);
        Assert.Equal(4, tracker.PeekFirstPartialRoom(400, InventoryItem.ItemFlags.None));
    }

    [Fact]
    public void CollectableAndUniqueItems_NeverStackAndTrackBaseId()
    {
        var tracker = new SlotSpaceTracker();
        tracker.RecordEmptySlot();
        tracker.RecordOccupiedSlot(500, 500, InventoryItem.ItemFlags.Collectable, quantity: 1, stackSize: 99, isHq: false);

        Assert.Equal(0, tracker.PeekFirstPartialRoom(500, InventoryItem.ItemFlags.Collectable));

        var unique = tracker.TryCommitReceive(600, 600, InventoryItem.ItemFlags.None, quantity: 1, stackSize: 1, isUnique: true);
        Assert.True(unique.Accepted);
        Assert.True(tracker.HasBaseItem(600));
        Assert.Equal(0, tracker.EmptySlots);
    }
}
