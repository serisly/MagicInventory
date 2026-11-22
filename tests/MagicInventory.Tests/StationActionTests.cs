using System.Collections.Generic;
using MagicInventory.Services;
using MagicInventory.Services.MagicSort;
using Xunit;

namespace MagicInventory.Tests;

public sealed class StationActionTests
{
    [Fact]
    public void StationActionHandler_CachesByKeyAndInvalidates()
    {
        var computeCalls = 0;
        var keyVal = 1UL;
        var handler = new StationActionHandler(
            StationActionId.VendorSell,
            _ =>
            {
                computeCalls++;
                return new MagicSortButtonState(true, true, $"Run #{computeCalls}");
            },
            _ => StationBatchPlan.Empty,
            cacheTtlMs: 60_000.0)
        {
            ResolveCacheKey = () => new StationCacheKey(keyVal),
        };

        IReadOnlyDictionary<uint, MagicSortEntry> Empty() => new Dictionary<uint, MagicSortEntry>();

        var first = handler.GetCachedButtonState(Empty);
        var second = handler.GetCachedButtonState(Empty);
        Assert.Equal("Run #1", first.Label);
        Assert.Equal("Run #1", second.Label);
        Assert.Equal(1, computeCalls);

        keyVal = 2UL;
        var changedKey = handler.GetCachedButtonState(Empty);
        Assert.Equal("Run #2", changedKey.Label);
        Assert.Equal(2, computeCalls);

        handler.InvalidateCache();
        var afterInvalidate = handler.GetCachedButtonState(Empty);
        Assert.Equal("Run #3", afterInvalidate.Label);
        Assert.Equal(3, computeCalls);
    }

    [Fact]
    public void StationActionHandler_ImmediateStateBypassesCacheAndManifest()
    {
        var manifestCalls = 0;
        var blocked = true;
        var handler = new StationActionHandler(
            StationActionId.MarketSell,
            _ => new MagicSortButtonState(true, true, "Magic Sell (1)"),
            _ => StationBatchPlan.Empty)
        {
            TryGetImmediateState = () => blocked ? new MagicSortButtonState(true, false, "Reviewing...") : null,
        };

        IReadOnlyDictionary<uint, MagicSortEntry> GetManifest()
        {
            manifestCalls++;
            return new Dictionary<uint, MagicSortEntry>();
        }

        var immediate = handler.GetCachedButtonState(GetManifest);
        Assert.Equal("Reviewing...", immediate.Label);
        Assert.Equal(0, manifestCalls);

        blocked = false;
        var normal = handler.GetCachedButtonState(GetManifest);
        Assert.Equal("Magic Sell (1)", normal.Label);
        Assert.Equal(1, manifestCalls);
    }

    [Fact]
    public void TrimToBatchLimit_CapsAndReportsOverflow()
    {
        var items = new List<int> { 1, 2, 3, 4 };
        Assert.True(StationActionHelpers.TrimToBatchLimit(items, 2));
        Assert.Equal([1, 2], items);

        Assert.False(StationActionHelpers.TrimToBatchLimit(items, 2));
        Assert.Equal([1, 2], items);

        Assert.True(StationActionHelpers.TrimToBatchLimit(items, 0));
        Assert.Empty(items);
    }

    [Fact]
    public void FormatBulletList_TruncatesWithOverflowAndFooter()
    {
        var text = StationActionHelpers.FormatBulletList(
            "Header:",
            ["A", "B", "C"],
            2,
            static s => s,
            "Footer");

        Assert.Equal("Header:\n  - A\n  - B\n  ... and 1 more\nFooter", text);
    }

    [Theory]
    [InlineData("Purchase Items", true)]
    [InlineData("Open Shop", true)]
    [InlineData("Buyback", false)]
    [InlineData("Rückkauf", false)]
    [InlineData("Leave", false)]
    public void MatchesShopSubMenu_FiltersBuybackAndMatchesLocalizedShops(string label, bool expected)
    {
        Assert.Equal(expected, MagicActionCoordinator.MatchesShopSubMenu(label));
    }

    [Theory]
    [InlineData("Place items in the armoire", true)]
    [InlineData("Déposer des objets", true)]
    [InlineData("Retrieve items", false)]
    public void MatchesArmoirePlaceMenu_MatchesStoreEntriesOnly(string label, bool expected)
    {
        Assert.Equal(expected, MagicActionCoordinator.MatchesArmoirePlaceMenu(label));
    }

    [Theory]
    [InlineData("Undertake supply and provisioning missions.", true)]
    [InlineData("Liefereinsätze annehmen", true)]
    [InlineData("Exchange seals", false)]
    public void MatchesGcSupplyMenu_MatchesSupplyEntriesOnly(string label, bool expected)
    {
        Assert.Equal(expected, MagicActionCoordinator.MatchesGcSupplyMenu(label));
    }
}
