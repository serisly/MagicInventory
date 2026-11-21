using System;

namespace MagicInventory.Services.Triage;

public readonly record struct MarketViabilityResult(
    bool HasData,
    bool IsRealisticToSell,
    bool IsDeadOrIlliquid,
    string Reason,
    float EffectiveVelocity,
    double DaysSinceLastSale,
    int SalesLast7Days,
    int ServerCheapest,
    int SoldMedian,
    int RealisticUnitPrice,
    long SuggestedUndercutPrice,
    long RealisticStackValue,
    int ListingCount,
    double EstimatedDaysToSell);

/// <summary>
/// Single source of truth for Market Board liquidity, frequency, recency,
/// oversupply saturation, and anti-manipulation pricing.
/// Shared by ItemTriageEngine, ActiveListingAuditor, and RetainerSellPriceController
/// so triage, listing review, and stale-listing detection always agree 100%.
/// </summary>
public static class MarketViabilityEvaluator
{
    public static MarketViabilityResult Evaluate(
        MarketPriceSnapshot? mkt,
        bool isHq,
        int quantity,
        long npcPriceLow,
        Configuration cfg,
        DateTime? nowUtcOverride = null)
    {
        if (mkt == null)
        {
            return new MarketViabilityResult(
                HasData: false,
                IsRealisticToSell: false,
                IsDeadOrIlliquid: false,
                Reason: "Checking Market Board price...",
                EffectiveVelocity: 0f,
                DaysSinceLastSale: double.PositiveInfinity,
                SalesLast7Days: 0,
                ServerCheapest: 0,
                SoldMedian: 0,
                RealisticUnitPrice: 0,
                SuggestedUndercutPrice: 0,
                RealisticStackValue: 0,
                ListingCount: -1,
                EstimatedDaysToSell: double.PositiveInfinity);
        }

        var nowUtc = nowUtcOverride ?? DateTime.UtcNow;
        var hasQualitySchema = mkt.SchemaVersion >= UniversalisClient.CurrentSnapshotSchemaVersion;

        // Quality-specific cheapest server price & recent sold median (fail-closed: never cross-price NQ from HQ)
        var serverCheapest = isHq
            ? mkt.MinPriceHq
            : (mkt.MinPriceNq > 0 ? mkt.MinPriceNq : (!hasQualitySchema ? mkt.MinPriceAny : 0));
        var soldMedian = isHq ? mkt.RecentHistoryMedianHq : mkt.RecentHistoryMedian;

        // Quality-specific velocity (falls back to blended RegularSaleVelocity only on legacy v1 snapshots)
        var velocity = hasQualitySchema
            ? (isHq ? mkt.HqSaleVelocity : mkt.NqSaleVelocity)
            : mkt.RegularSaleVelocity;

        var lastSaleUnix = isHq ? mkt.LastSaleUnixHq : mkt.LastSaleUnixNq;
        var sales7d = isHq ? mkt.SalesLast7DaysHq : mkt.SalesLast7DaysNq;

        double daysSinceLastSale;
        if (lastSaleUnix > 0)
        {
            var lastSaleUtc = DateTimeOffset.FromUnixTimeSeconds(lastSaleUnix).UtcDateTime;
            daysSinceLastSale = Math.Max(0.0, (nowUtc - lastSaleUtc).TotalDays);
        }
        else
        {
            daysSinceLastSale = hasQualitySchema ? double.PositiveInfinity : 0.0;
        }

        var realisticUnit = ComputeRealisticUnitPrice(serverCheapest, soldMedian, hasQualitySchema);
        var suggestedUndercut = ComputeUndercutPrice(serverCheapest, soldMedian, npcPriceLow, cfg.MarketUndercutGil);
        var safeQty = Math.Max(1, quantity);
        var stackVal = (long)realisticUnit * safeQty;
        var listingCount = mkt.ListingCount;
        var estDaysToSell = listingCount > 0 && velocity > 0.0001f
            ? listingCount / velocity
            : (velocity > 0.0001f ? 1.0 / velocity : double.PositiveInfinity);

        // 1. Zero recorded recent sales for this quality -> unrealistic (lone speculative listings don't count!)
        if (hasQualitySchema && lastSaleUnix <= 0)
        {
            return Build(false, true, $"No recent {(isHq ? "HQ" : "NQ")} sales");
        }

        // 2. Last sale was too long ago -> stale / dead market
        var maxDays = Math.Max(1, cfg.MarketMaxDaysSinceLastSale);
        if (hasQualitySchema && daysSinceLastSale > maxDays)
        {
            return Build(false, true, $"Last sold {(int)Math.Floor(daysSinceLastSale)}d ago");
        }

        // 3. Sale frequency below configured minimum
        if (velocity < cfg.MarketMinVelocity)
        {
            return Build(false, true, $"Low demand ({velocity:0.00}/day)");
        }

        // 4. Severe listing-vs-history manipulation (e.g. listed 5x+ above junk sold median)
        if (serverCheapest > 0 && soldMedian > 0 && serverCheapest > soldMedian * 5L && soldMedian < cfg.MarketMinFloorUnitPrice)
        {
            return Build(false, true, $"Inflated MB listing (median ~{soldMedian:N0}g)");
        }

        // 5. Severe queue saturation: too many competing sellers relative to daily sales
        var maxCompDays = Math.Max(5, cfg.MarketMaxCompetitionDays);
        if (listingCount >= 10 && estDaysToSell > maxCompDays)
        {
            return Build(false, true, $"Oversupplied ({listingCount} listed, {velocity:0.0}/day)");
        }

        // 6. Realistic price doesn't clear NPC vendor price or floor threshold
        var floorPrice = Math.Max(cfg.MarketMinFloorUnitPrice, (int)Math.Min(int.MaxValue, npcPriceLow + 1L));
        if (realisticUnit < floorPrice)
        {
            var reason = npcPriceLow > 0 && realisticUnit <= npcPriceLow
                ? $"MB (~{realisticUnit:N0}g) <= vendor ({npcPriceLow:N0}g)"
                : $"Below min unit floor ({realisticUnit:N0}g)";
            return Build(false, false, reason);
        }

        // 7. Value threshold (either total stack value or single-item value must justify a retainer slot)
        if (stackVal < cfg.MarketMinStackValue && realisticUnit < cfg.MarketMinUnitPrice)
        {
            return Build(false, false, $"Low stack value (~{stackVal:N0}g)");
        }

        var goodReason = $"Active ({velocity:0.0}/day, ~{realisticUnit:N0}g ea, ~{stackVal:N0}g total)";
        return Build(true, false, goodReason);

        MarketViabilityResult Build(bool isRealistic, bool isDead, string reason) => new(
            HasData: true,
            IsRealisticToSell: isRealistic,
            IsDeadOrIlliquid: isDead,
            Reason: reason,
            EffectiveVelocity: velocity,
            DaysSinceLastSale: daysSinceLastSale,
            SalesLast7Days: sales7d,
            ServerCheapest: serverCheapest,
            SoldMedian: soldMedian,
            RealisticUnitPrice: realisticUnit,
            SuggestedUndercutPrice: suggestedUndercut,
            RealisticStackValue: stackVal,
            ListingCount: listingCount,
            EstimatedDaysToSell: estDaysToSell);
    }

    public static int ComputeRealisticUnitPrice(int serverCheapest, int soldMedian, bool requireHistoryOnZeroCheapest = true)
    {
        if (serverCheapest > 0 && soldMedian > 0)
        {
            // Cap speculative outlier listings that sit >3x above actual sold history
            return serverCheapest > soldMedian * 3L
                ? (int)Math.Min(int.MaxValue, Math.Round(soldMedian * 1.25))
                : serverCheapest;
        }

        if (soldMedian > 0)
        {
            return soldMedian;
        }

        return requireHistoryOnZeroCheapest ? 0 : serverCheapest;
    }

    public static long ComputeUndercutPrice(long serverCheapest, long soldMedian, long npcPriceLow, int undercutGil)
    {
        var baseFloor = Math.Max(2L, npcPriceLow + 1L);
        var panicDumpFloor = soldMedian > 0 ? (long)Math.Round(soldMedian * 0.55) : 0L;
        var floorPrice = Math.Max(baseFloor, panicDumpFloor);
        var effective = serverCheapest > 0 ? serverCheapest : soldMedian;
        if (effective <= 0)
        {
            return 0;
        }

        // Clamp against speculative listing outliers (>3x sold median)
        if (serverCheapest > 0 && soldMedian > 0 && serverCheapest > soldMedian * 3L)
        {
            effective = (long)Math.Round(soldMedian * 1.25);
        }

        var step = Math.Max(0, undercutGil);
        var undercut = effective > floorPrice + step ? effective - step : floorPrice;
        return Math.Max(undercut, floorPrice);
    }

    public static long ComputeDirectUndercut(long liveCheapest, long npcPriceLow, int undercutGil)
    {
        if (liveCheapest <= 0)
        {
            return 0;
        }

        var floorPrice = Math.Max(2L, npcPriceLow + 1L);
        var step = Math.Max(0, undercutGil);
        return Math.Max(floorPrice, liveCheapest - step);
    }

    public static string FormatAgeShort(double days)
    {
        if (double.IsInfinity(days) || double.IsNaN(days))
        {
            return "never";
        }

        if (days < 1.0)
        {
            var hours = Math.Max(1, (int)Math.Floor(days * 24.0));
            return $"{hours}h ago";
        }

        return $"{(int)Math.Floor(days)}d ago";
    }
}
