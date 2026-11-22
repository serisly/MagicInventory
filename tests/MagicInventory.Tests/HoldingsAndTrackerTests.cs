using System.Collections.Generic;
using MagicInventory.Services;
using Xunit;

namespace MagicInventory.Tests;

public sealed class HoldingsAndTrackerTests
{
    [Fact]
    public void SplitCsvLine_HandlesRfc4180QuotedCommasAndEscapedQuotes()
    {
        var cols = HoldingsService.SplitCsvLine("12345,\"Ore, \"\"High\"\" Grade\",99,\"Retainer, One\",");
        Assert.Equal(5, cols.Count);
        Assert.Equal("12345", cols[0]);
        Assert.Equal("Ore, \"High\" Grade", cols[1]);
        Assert.Equal("99", cols[2]);
        Assert.Equal("Retainer, One", cols[3]);
        Assert.Equal(string.Empty, cols[4]);
    }

    [Fact]
    public void ComputeSensibleInitialTarget_DoesNotAddPhantomPlusOneToOwnedBags()
    {
        var bagsOnly = new ItemHoldingSummary(
            ItemId: 5111,
            BagCount: 400,
            Retainers: new List<RetainerHolding>(),
            SaddlebagCount: 0);
        Assert.Equal(400, MaterialTrackerService.ComputeSensibleInitialTarget(bagsOnly));

        var retainerOnly = new ItemHoldingSummary(
            ItemId: 5111,
            BagCount: 0,
            Retainers: new List<RetainerHolding> { new(1, "Alpha", 20) },
            SaddlebagCount: 0);
        Assert.Equal(20, MaterialTrackerService.ComputeSensibleInitialTarget(retainerOnly));

        var withSaddlebag = new ItemHoldingSummary(
            ItemId: 5111,
            BagCount: 10,
            Retainers: new List<RetainerHolding>(),
            SaddlebagCount: 15);
        Assert.Equal(15, withSaddlebag.TotalExternalCount);
        Assert.Equal(25, MaterialTrackerService.ComputeSensibleInitialTarget(withSaddlebag));

        var ownedZero = new ItemHoldingSummary(
            ItemId: 5111,
            BagCount: 0,
            Retainers: new List<RetainerHolding>(),
            SaddlebagCount: 0);
        Assert.Equal(1, MaterialTrackerService.ComputeSensibleInitialTarget(ownedZero));
    }
}
