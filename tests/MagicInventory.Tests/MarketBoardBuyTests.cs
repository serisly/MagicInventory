using System.Collections.Generic;
using MagicInventory.Services;
using Xunit;

namespace MagicInventory.Tests;

public sealed class MarketBoardBuyTests
{
    [Fact]
    public void SelectBestListing_UsesHqAnchorForHqListings_NotNqAnchor()
    {
        var listings = new List<MarketListingInput>
        {
            new(0, Quantity: 5, UnitPrice: 420, TotalCost: 2100, IsHq: true, RetainerId: 10),
        };

        var best = MarketBoardBuyPlanner.SelectBestListing(
            listings,
            baseItemId: 1000,
            itemName: "Test Ingot",
            missingNeeded: 5,
            currentGil: 100_000,
            maxGilPerPurchase: 50_000,
            outlierMultiplier: 2.5,
            histAnchorNq: 100,
            histAnchorHq: 400,
            out var blockReason);

        Assert.NotNull(best);
        Assert.Equal(0, best!.ListingIndex);
        Assert.Equal(string.Empty, blockReason);
    }

    [Fact]
    public void SelectBestListing_PrefersFullCoverageOverTiny1xPartialWhenPricesAreClose()
    {
        var listings = new List<MarketListingInput>
        {
            new(0, Quantity: 1, UnitPrice: 100, TotalCost: 100, IsHq: false, RetainerId: 1),
            new(1, Quantity: 10, UnitPrice: 105, TotalCost: 1050, IsHq: false, RetainerId: 2),
        };

        var best = MarketBoardBuyPlanner.SelectBestListing(
            listings,
            baseItemId: 2000,
            itemName: "Test Ore",
            missingNeeded: 10,
            currentGil: 100_000,
            maxGilPerPurchase: 50_000,
            outlierMultiplier: 2.5,
            histAnchorNq: 105,
            histAnchorHq: 0,
            out _);

        Assert.NotNull(best);
        Assert.Equal(1, best!.ListingIndex);
    }

    [Fact]
    public void SelectBestListing_RespectsTaxInclusiveTotalCostAgainstCurrentGilAndOverbuyThreshold()
    {
        var tooExpensive = new List<MarketListingInput>
        {
            new(0, Quantity: 1, UnitPrice: 10_000, TotalCost: 10_500, IsHq: false, RetainerId: 5),
        };

        var best = MarketBoardBuyPlanner.SelectBestListing(
            tooExpensive,
            baseItemId: 3000,
            itemName: "Test Gem",
            missingNeeded: 1,
            currentGil: 10_200,
            maxGilPerPurchase: 50_000,
            outlierMultiplier: 2.5,
            histAnchorNq: 10_000,
            histAnchorHq: 0,
            out _);

        Assert.Null(best);

        var overbuyCase = new List<MarketListingInput>
        {
            new(0, Quantity: 20, UnitPrice: 1000, TotalCost: 21_000, IsHq: false, RetainerId: 1),
            new(1, Quantity: 4, UnitPrice: 1000, TotalCost: 4_200, IsHq: false, RetainerId: 2),
        };

        var picked = MarketBoardBuyPlanner.SelectBestListing(
            overbuyCase,
            baseItemId: 3001,
            itemName: "Test Crystal",
            missingNeeded: 5,
            currentGil: 100_000,
            maxGilPerPurchase: 50_000,
            outlierMultiplier: 2.5,
            histAnchorNq: 1000,
            histAnchorHq: 0,
            out _);

        Assert.NotNull(picked);
        Assert.Equal(1, picked!.ListingIndex);
    }

    [Fact]
    public void ExtractExactPromptNumbers_ParsesLocaleSeparatorsWithoutMergingAdjacentWords()
    {
        var nums1 = MarketBoardBuyPlanner.ExtractExactPromptNumbers("Purchase Grade 8 Dark Matter for 500 gil?");
        Assert.Contains(8L, nums1);
        Assert.Contains(500L, nums1);
        Assert.DoesNotContain(8500L, nums1);

        var numsEn = MarketBoardBuyPlanner.ExtractExactPromptNumbers("Purchase 12 items for 1,250,000 gil?");
        Assert.Contains(12L, numsEn);
        Assert.Contains(1_250_000L, numsEn);

        var numsFr = MarketBoardBuyPlanner.ExtractExactPromptNumbers("Acheter 5 objets pour 12\u00A0500 gils ?");
        Assert.Contains(5L, numsFr);
        Assert.Contains(12_500L, numsFr);

        var numsDe = MarketBoardBuyPlanner.ExtractExactPromptNumbers("3 Stück für 45.678 Gil kaufen?");
        Assert.Contains(3L, numsDe);
        Assert.Contains(45_678L, numsDe);
    }
}
