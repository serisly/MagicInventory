using System;
using MagicInventory.Core;
using MagicInventory.Services;
using MagicInventory.UI.Settings;
using Xunit;

namespace MagicInventory.Tests;

public sealed class GameItemSearchIndexTests
{
    [Fact]
    public void Search_MatchesExactIdCaseInsensitiveSubstringAndRespectsLimit()
    {
        var items = new (uint RowId, string Name)[]
        {
            (10, "Iron Ore"),
            (20, "Iron Ingot"),
            (30, "Iron Rivets"),
            (40, "Mythril Ore"),
        };

        Assert.Equal([20u], GameItemSearchIndex.Search(items, "20"));
        Assert.Equal([10u, 20u, 30u], GameItemSearchIndex.Search(items, "iron"));
        Assert.Equal([10u, 20u], GameItemSearchIndex.Search(items, "iron", maxResults: 2));
        Assert.Empty(GameItemSearchIndex.Search(items, "i"));
    }

    [Fact]
    public void Query_DebouncesLazyIndexBuildAndCachesClassification()
    {
        var buildCalls = 0;
        var classifyCalls = 0;
        var searchIndex = new GameItemSearchIndex(() =>
        {
            buildCalls++;
            return
            [
                (100u, "Fire Shard"),
                (200u, "Fire Crystal"),
            ];
        });

        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        MagicSortEntry Classify(uint id)
        {
            classifyCalls++;
            return new MagicSortEntry(id, $"Item {id}", ItemRoute.Keep, string.Empty, 0, 0, 0, false, string.Empty);
        }

        var initial = searchIndex.Query("fire", t0, Classify);
        Assert.Empty(initial);
        Assert.Equal(0, buildCalls);
        Assert.False(searchIndex.IsDebounceReady("fire", t0.AddMilliseconds(100)));
        Assert.True(searchIndex.IsDebounceReady("fire", t0.AddMilliseconds(250)));

        var afterDebounce = searchIndex.Query("fire", t0.AddMilliseconds(250), Classify);
        Assert.Equal(2, afterDebounce.Count);
        Assert.Equal(1, buildCalls);
        Assert.Equal(2, classifyCalls);

        var cached = searchIndex.Query("fire", t0.AddMilliseconds(500), Classify);
        Assert.Equal(2, cached.Count);
        Assert.Equal(1, buildCalls);
        Assert.Equal(2, classifyCalls);

        searchIndex.InvalidateClassification();
        var refreshed = searchIndex.Query("fire", t0.AddMilliseconds(600), Classify);
        Assert.Equal(2, refreshed.Count);
        Assert.Equal(4, classifyCalls);
    }
}
