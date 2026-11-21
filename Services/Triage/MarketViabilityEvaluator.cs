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
/// Single source of truth for Market Board liquidity, value-tiered frequency, recency,
/// oversupply saturation, 5% seller-tax vendor break-even, and anti-manipulation pricing.
/// Shared by ItemTriageEngine, ActiveListingAuditor, MarketListingPlanner, and RetainerSellPriceController.
/// </summary>
public static class MarketViabilityEvaluator
{
    private const long MidValueStackThreshold = 10_000;
    private const long HighValueStackThreshold = 50_000;

    /// <summary>
    /// Minimum Market Board asking price required to net strictly MORE than instant 0%-tax NPC vendoring
    /// after FFXIV's 5% Market Board seller tax.
    /// </summary>
    public static long ComputeVendorBreakevenPrice(long npcPriceLow)
        => npcPriceLow <= 0 ? 2L : Math.Max(2L, (long)Math.Ceiling(npcPriceLow / 0.95) + 1L);

    public static MarketViabilityResult Evaluate(
        MarketPriceSnapshot? mkt,
        bool isHq,
        int quantity,
        long npcPriceLow,
        Configuration cfg,
        DateTime? nowUtcOverride = null)
    {
        if (mkt == null || IsEmptySentinel(mkt))
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
        else if (hasQualitySchema && velocity <= 0.0001f)
        {
            daysSinceLastSale = double.PositiveInfinity;
        }
        else
        {
            // Minority quality had >0 velocity from Universalis server-side stats even if recentHistory was dominated by other quality
            daysSinceLastSale = velocity > 0.0001f ? Math.Min(7.0, 1.0 / velocity) : 0.0;
        }

        var realisticUnit = ComputeRealisticUnitPrice(serverCheapest, soldMedian, velocity, hasQualitySchema);
        var suggestedUndercut = ComputeUndercutPrice(serverCheapest, soldMedian, npcPriceLow, cfg.MarketUndercutGil);
        var safeQty = Math.Max(1, quantity);
        var stackVal = (long)realisticUnit * safeQty;
        var listingCount = mkt.ListingCount;

        // Compare combined NQ+HQ listing count against combined RegularSaleVelocity (never divide combined listings by single-quality velocity!)
        var combinedVel = mkt.RegularSaleVelocity > 0.0001f ? mkt.RegularSaleVelocity : velocity;
        var estDaysToSell = listingCount > 0 && combinedVel > 0.0001f
            ? listingCount / combinedVel
            : (velocity > 0.0001f ? 1.0 / velocity : double.PositiveInfinity);

        // Value-tiered liquidity requirements: expensive rare items (10k+, 50k+) naturally sell slower than 500g mats
        var effectiveMinVel = cfg.MarketMinVelocity;
        var effectiveMaxDays = Math.Max(1, cfg.MarketMaxDaysSinceLastSale);
        if (stackVal >= HighValueStackThreshold)
        {
            effectiveMinVel = MathF.Min(effectiveMinVel, 0.10f);
            effectiveMaxDays = Math.Max(effectiveMaxDays, 30);
        }
        else if (stackVal >= MidValueStackThreshold)
        {
            effectiveMinVel = MathF.Min(effectiveMinVel, 0.25f);
            effectiveMaxDays = Math.Max(effectiveMaxDays, 21);
        }

        // 1. Zero recorded recent sales AND zero velocity -> unrealistic (lone speculative listings don't count!)
        if (hasQualitySchema && lastSaleUnix <= 0 && velocity <= 0.0001f)
        {
            return Build(false, true, $"No recent {(isHq ? "HQ" : "NQ")} sales");
        }

        // 2. Last sale was too long ago -> stale / dead market
        if (hasQualitySchema && lastSaleUnix > 0 && daysSinceLastSale > effectiveMaxDays)
        {
            return Build(false, true, $"Last sold {(int)Math.Floor(daysSinceLastSale)}d ago");
        }

        // 3. Sale frequency below value-tiered minimum
        if (velocity < effectiveMinVel)
        {
            return Build(false, true, $"Low demand ({velocity:0.00}/day)");
        }

        // 4. Severe listing-vs-history manipulation (e.g. listed 5x+ above junk sold median)
        if (serverCheapest > 0 && soldMedian > 0 && serverCheapest > soldMedian * 5L && soldMedian < cfg.MarketMinFloorUnitPrice)
        {
            return Build(false, true, $"Inflated MB listing (median ~{soldMedian:N0}g)");
        }

        // 5. Severe queue saturation: too many competing sellers relative to total daily sales
        var maxCompDays = Math.Max(5, cfg.MarketMaxCompetitionDays);
        if (listingCount >= 10 && estDaysToSell > maxCompDays)
        {
            return Build(false, true, $"Oversupplied ({listingCount} listed, {combinedVel:0.0}/day)");
        }

        // 6. Realistic price must beat 5%-tax NPC vendor break-even AND minimum unit floor
        var vendorBreakeven = ComputeVendorBreakevenPrice(npcPriceLow);
        var floorPrice = Math.Max(cfg.MarketMinFloorUnitPrice, (int)Math.Min(int.MaxValue, vendorBreakeven));
        if (realisticUnit < floorPrice)
        {
            var reason = npcPriceLow > 0 && realisticUnit < vendorBreakeven
                ? $"MB (~{realisticUnit:N0}g) <= vendor net ({vendorBreakeven:N0}g)"
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

    public static bool IsEmptySentinel(MarketPriceSnapshot mkt)
        => mkt.ListingCount < 0
            && mkt.MinPriceAny == 0
            && mkt.MinPriceNq == 0
            && mkt.MinPriceHq == 0
            && mkt.RecentHistoryMedian == 0
            && mkt.RecentHistoryMedianHq == 0;

    public static int ComputeRealisticUnitPrice(
        int serverCheapest,
        int soldMedian,
        float velocity = 0f,
        bool hasQualitySchema = true)
    {
        if (serverCheapest > 0 && soldMedian > 0)
        {
            // Upward manipulation cap: ignore speculative listings >3x above actual sold history
            if (serverCheapest > soldMedian * 3L)
            {
                return (int)Math.Min(int.MaxValue, Math.Round(soldMedian * 1.25));
            }

            // Downward bait/fat-finger floor: prevent a single 1-gil bait listing from collapsing a valuable item into Vendor/Remove
            if (serverCheapest < soldMedian * 0.25)
            {
                return (int)Math.Min(int.MaxValue, Math.Round(soldMedian * 0.55));
            }

            return serverCheapest;
        }

        if (soldMedian > 0)
        {
            return soldMedian;
        }

        // If recentHistory was filled by the other quality, still allow serverCheapest when velocity confirms real sales
        if (serverCheapest > 0 && (!hasQualitySchema || velocity > 0.0001f))
        {
            return serverCheapest;
        }

        return 0;
    }

    public static long ComputeUndercutPrice(long serverCheapest, long soldMedian, long npcPriceLow, int undercutGil)
    {
        var baseFloor = ComputeVendorBreakevenPrice(npcPriceLow);
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

        var floorPrice = ComputeVendorBreakevenPrice(npcPriceLow);
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
