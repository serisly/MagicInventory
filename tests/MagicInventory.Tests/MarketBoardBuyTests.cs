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

        var best = MarketBoardListingSelector.SelectBestListing(
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

        var best = MarketBoardListingSelector.SelectBestListing(
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

        var best = MarketBoardListingSelector.SelectBestListing(
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

        var picked = MarketBoardListingSelector.SelectBestListing(
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
    public void SelectBestListing_BlocksOutlierAndExtremeOverbuy()
    {
        var outlier = new List<MarketListingInput>
        {
            new(0, Quantity: 2, UnitPrice: 10_000, TotalCost: 21_000, IsHq: false, RetainerId: 1),
        };

        var blockedOutlier = MarketBoardListingSelector.SelectBestListing(
            outlier,
            baseItemId: 4000,
            itemName: "Iron Ore",
            missingNeeded: 2,
            currentGil: 500_000,
            maxGilPerPurchase: 50_000,
            outlierMultiplier: 2.5,
            histAnchorNq: 500,
            histAnchorHq: 0,
            out var outlierReason);

        Assert.Null(blockedOutlier);
        Assert.Contains("recent prices", outlierReason);

        var giantStack = new List<MarketListingInput>
        {
            new(0, Quantity: 99, UnitPrice: 400, TotalCost: 41_580, IsHq: false, RetainerId: 2),
        };

        var blockedStack = MarketBoardListingSelector.SelectBestListing(
            giantStack,
            baseItemId: 4001,
            itemName: "Copper Ore",
            missingNeeded: 2,
            currentGil: 500_000,
            maxGilPerPurchase: 50_000,
            outlierMultiplier: 2.5,
            histAnchorNq: 400,
            histAnchorHq: 0,
            out var stackReason);

        Assert.Null(blockedStack);
        Assert.Contains("far exceeds needed", stackReason);
    }

    [Fact]
    public void ExtractExactPromptNumbers_ParsesLocaleSeparatorsWithoutMergingAdjacentWords()
    {
        var nums1 = MarketBoardListingSelector.ExtractExactPromptNumbers("Purchase Grade 8 Dark Matter for 500 gil?");
        Assert.Contains(8L, nums1);
        Assert.Contains(500L, nums1);
        Assert.DoesNotContain(8500L, nums1);

        var numsEn = MarketBoardListingSelector.ExtractExactPromptNumbers("Purchase 12 items for 1,250,000 gil?");
        Assert.Contains(12L, numsEn);
        Assert.Contains(1_250_000L, numsEn);

        var numsFr = MarketBoardListingSelector.ExtractExactPromptNumbers("Acheter 5 objets pour 12\u00A0500 gils ?");
        Assert.Contains(5L, numsFr);
        Assert.Contains(12_500L, numsFr);

        var numsDe = MarketBoardListingSelector.ExtractExactPromptNumbers("3 Stück für 45.678 Gil kaufen?");
        Assert.Contains(3L, numsDe);
        Assert.Contains(45_678L, numsDe);
    }

    [Fact]
    public void NormalizeSearchRowText_StripsTrailingListingCountOnly()
    {
        Assert.Equal("Iron Ingot", MarketBoardListingSelector.NormalizeSearchRowText("Iron Ingot (24)"));
        Assert.Equal("Potion (HQ)", MarketBoardListingSelector.NormalizeSearchRowText("Potion (HQ)"));
        Assert.Equal("Dark Matter", MarketBoardListingSelector.NormalizeSearchRowText("Dark Matter (5)"));
    }

    [Fact]
    public void DoesPurchaseConfirmPromptMatch_VerifiesNameQuantityAndPrice()
    {
        Assert.True(MarketBoardListingSelector.DoesPurchaseConfirmPromptMatch(
            "Purchase 12 Iron Ingots for 6,300 gil?",
            "Iron Ingot",
            "Iron Ingots",
            unitPrice: 500,
            quantity: 12,
            expectedTotalCost: 6300));

        Assert.False(MarketBoardListingSelector.DoesPurchaseConfirmPromptMatch(
            "Purchase 12 Iron Ingots for 9,999 gil?",
            "Iron Ingot",
            "Iron Ingots",
            unitPrice: 500,
            quantity: 12,
            expectedTotalCost: 6300));

        Assert.False(MarketBoardListingSelector.DoesPurchaseConfirmPromptMatch(
            "Purchase 5 Iron Ingots for 6,300 gil?",
            "Iron Ingot",
            "Iron Ingots",
            unitPrice: 500,
            quantity: 12,
            expectedTotalCost: 6300));
    }
}
