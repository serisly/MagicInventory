using System;
using MagicInventory.Services.Triage;
using Xunit;

namespace MagicInventory.Tests;

public sealed class MarketViabilityTests
{
    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 2)]
    [InlineData(10, 11)]
    [InlineData(100, 106)]
    [InlineData(1000, 1053)]
    public void ComputeVendorBreakevenPrice_MatchesExactInteger5PctTax(long npcLow, long expectedMinPrice)
    {
        var price = MarketViabilityEvaluator.ComputeVendorBreakevenPrice(npcLow);
        Assert.Equal(expectedMinPrice, price);
        Assert.True(price - (price * 5L / 100L) > npcLow);
        if (price > 2)
        {
            var oneLower = price - 1;
            Assert.True(oneLower - (oneLower * 5L / 100L) <= npcLow);
        }
    }

    [Fact]
    public void BulkStackAboveMinStackValue_IsNotVetoedBySmallStackUnitFloor()
    {
        var cfg = new Configuration();
        cfg.ApplySellModePresets(SellMode.Safe);

        var now = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var recentSaleUnix = new DateTimeOffset(now.AddHours(-2)).ToUnixTimeSeconds();
        var snap = new MarketPriceSnapshot(
            ItemId: 5111,
            MinPriceNq: 45,
            MinPriceHq: 0,
            MinPriceAny: 45,
            RecentHistoryMedian: 45,
            RegularSaleVelocity: 25f,
            FetchedUtc: now,
            ListingCount: 15,
            NqSaleVelocity: 25f,
            LastSaleUnixNq: recentSaleUnix,
            SalesLast7DaysNq: 20,
            SchemaVersion: UniversalisClient.CurrentSnapshotSchemaVersion,
            CompetitiveListingCountNq: 8);

        var bulkResult = MarketViabilityEvaluator.Evaluate(snap, isHq: false, quantity: 999, npcPriceLow: 2, cfg, now);
        Assert.True(bulkResult.IsRealisticToSell, $"Expected bulk stack to be sellable, got reason: {bulkResult.Reason}");

        var singleResult = MarketViabilityEvaluator.Evaluate(snap, isHq: false, quantity: 1, npcPriceLow: 2, cfg, now);
        Assert.False(singleResult.IsRealisticToSell);
    }

    [Fact]
    public void CompetitiveBandDepth_DoesNotCountOverpricedBottomOfBoardJunkAsOversupply()
    {
        var cfg = new Configuration();
        cfg.ApplySellModePresets(SellMode.Safe);

        var now = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var recentSaleUnix = new DateTimeOffset(now.AddHours(-4)).ToUnixTimeSeconds();
        var snap = new MarketPriceSnapshot(
            ItemId: 40000,
            MinPriceNq: 15_000,
            MinPriceHq: 0,
            MinPriceAny: 15_000,
            RecentHistoryMedian: 15_500,
            RegularSaleVelocity: 1.5f,
            FetchedUtc: now,
            ListingCount: 120,
            NqSaleVelocity: 1.5f,
            LastSaleUnixNq: recentSaleUnix,
            SalesLast7DaysNq: 10,
            SchemaVersion: UniversalisClient.CurrentSnapshotSchemaVersion,
            CompetitiveListingCountNq: 5);

        var res = MarketViabilityEvaluator.Evaluate(snap, isHq: false, quantity: 1, npcPriceLow: 10, cfg, now);
        Assert.True(res.IsRealisticToSell, $"Expected sellable, got reason: {res.Reason}");
        Assert.False(res.IsDeadOrIlliquid);
    }

    [Fact]
    public void MissingLastSaleUnix_ProducesNaNAndUnknownFormatting_NeverFabricatesDays()
    {
        var cfg = new Configuration();
        var now = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var snap = new MarketPriceSnapshot(
            ItemId: 30001,
            MinPriceNq: 5000,
            MinPriceHq: 0,
            MinPriceAny: 5000,
            RecentHistoryMedian: 5000,
            RegularSaleVelocity: 2.0f,
            FetchedUtc: now,
            ListingCount: 5,
            NqSaleVelocity: 2.0f,
            LastSaleUnixNq: 0,
            SalesLast7DaysNq: 0,
            SchemaVersion: UniversalisClient.CurrentSnapshotSchemaVersion,
            CompetitiveListingCountNq: 3);

        var res = MarketViabilityEvaluator.Evaluate(snap, isHq: false, quantity: 1, npcPriceLow: 5, cfg, now);
        Assert.True(double.IsNaN(res.DaysSinceLastSale));
        Assert.Equal("unknown", MarketViabilityEvaluator.FormatAgeShort(res.DaysSinceLastSale));
    }

    [Fact]
    public void BaitDumpBelowThreshold_HoldsAtSoldMedian_WhileNormalCompetitorIsUndercut()
    {
        Assert.Equal(8999, MarketViabilityEvaluator.ComputeUndercutPrice(9000, 10000, npcPriceLow: 10, undercutGil: 1));

        var baitPrice = MarketViabilityEvaluator.ComputeUndercutPrice(2000, 10000, npcPriceLow: 10, undercutGil: 1);
        Assert.Equal(10000, baitPrice);
    }

    [Fact]
    public void HighValueRareItem_UsesValueTieredLiquidityThresholds()
    {
        var cfg = new Configuration();
        cfg.ApplySellModePresets(SellMode.Safe);

        var now = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var sold20DaysAgo = new DateTimeOffset(now.AddDays(-20)).ToUnixTimeSeconds();
        var snap = new MarketPriceSnapshot(
            ItemId: 44000,
            MinPriceNq: 120_000,
            MinPriceHq: 0,
            MinPriceAny: 120_000,
            RecentHistoryMedian: 125_000,
            RegularSaleVelocity: 0.15f,
            FetchedUtc: now,
            ListingCount: 3,
            NqSaleVelocity: 0.15f,
            LastSaleUnixNq: sold20DaysAgo,
            SalesLast7DaysNq: 0,
            SchemaVersion: UniversalisClient.CurrentSnapshotSchemaVersion,
            CompetitiveListingCountNq: 2);

        var res = MarketViabilityEvaluator.Evaluate(snap, isHq: false, quantity: 1, npcPriceLow: 0, cfg, now);
        Assert.True(res.IsRealisticToSell, $"Expected 120k high-value item to be sellable, got reason: {res.Reason}");
    }
}
