using System;

namespace MagicInventory.Services.Triage;

public readonly record struct MarketViabilityResult(
    bool HasData,
    bool IsRealisticToSell,
    bool IsDeadOrIlliquid,
    bool IsBaitDump,
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

public static class MarketViabilityEvaluator
{
    public const double BaitDumpRatioThreshold = 0.55;
    private const long MidValueStackThreshold = 10_000;
    private const long HighValueStackThreshold = 50_000;

    public static long ComputeVendorBreakevenPrice(long npcPriceLow)
    {
        if (npcPriceLow <= 0)
        {
            return 2L;
        }

        var price = Math.Max(2L, npcPriceLow * 100L / 95L);
        while (price - (price * 5L / 100L) <= npcPriceLow)
        {
            price++;
        }

        return price;
    }

    public static bool IsBaitDumpPrice(long liveCheapest, long soldMedian)
        => liveCheapest > 0 && soldMedian > 0 && liveCheapest < soldMedian * BaitDumpRatioThreshold;

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
                IsBaitDump: false,
                Reason: "Checking Market Board price...",
                EffectiveVelocity: 0f,
                DaysSinceLastSale: double.NaN,
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
        var hasQualitySchema = mkt.SchemaVersion >= 2;

        var serverCheapest = isHq
            ? mkt.MinPriceHq
            : (mkt.MinPriceNq > 0 ? mkt.MinPriceNq : (!hasQualitySchema ? mkt.MinPriceAny : 0));
        var soldMedian = isHq ? mkt.RecentHistoryMedianHq : mkt.RecentHistoryMedian;

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
            daysSinceLastSale = double.NaN;
        }

        var isBaitDump = IsBaitDumpPrice(serverCheapest, soldMedian);
        var realisticUnit = ComputeRealisticUnitPrice(serverCheapest, soldMedian, velocity, hasQualitySchema);
        var suggestedUndercut = ComputeUndercutPrice(serverCheapest, soldMedian, npcPriceLow, cfg.MarketUndercutGil);
        var safeQty = Math.Max(1, quantity);
        var stackVal = (long)realisticUnit * safeQty;
        var listingCount = mkt.ListingCount;

        var competitiveDepth = isHq ? mkt.CompetitiveListingCountHq : mkt.CompetitiveListingCountNq;
        var estDaysToSell = competitiveDepth > 0 && velocity > 0.0001f
            ? competitiveDepth / velocity
            : (velocity > 0.0001f ? 1.0 / velocity : double.PositiveInfinity);

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

        if (hasQualitySchema && lastSaleUnix <= 0 && velocity <= 0.0001f)
        {
            return Build(false, true, $"No recent {(isHq ? "HQ" : "NQ")} sales");
        }

        if (hasQualitySchema && lastSaleUnix > 0 && !double.IsNaN(daysSinceLastSale) && daysSinceLastSale > effectiveMaxDays)
        {
            return Build(false, true, $"Last sold {(int)Math.Floor(daysSinceLastSale)}d ago");
        }

        if (velocity < effectiveMinVel)
        {
            return Build(false, true, $"Low demand ({velocity:0.00}/day)");
        }

        if (serverCheapest > 0 && soldMedian > 0 && serverCheapest > soldMedian * 3L && soldMedian < cfg.MarketMinFloorUnitPrice)
        {
            return Build(false, true, $"Inflated MB listing (median ~{soldMedian:N0}g)");
        }

        var maxCompDays = Math.Max(5, cfg.MarketMaxCompetitionDays);
        if (competitiveDepth >= 8 && estDaysToSell > maxCompDays)
        {
            return Build(false, true, $"Oversupplied ({competitiveDepth} competing, {velocity:0.0}/day)");
        }

        var vendorBreakeven = ComputeVendorBreakevenPrice(npcPriceLow);
        if (realisticUnit < vendorBreakeven)
        {
            return Build(false, false, $"MB (~{realisticUnit:N0}g) <= vendor net ({vendorBreakeven:N0}g)");
        }

        if (stackVal < cfg.MarketMinStackValue && realisticUnit < cfg.MarketMinFloorUnitPrice)
        {
            return Build(false, false, $"Below min unit floor ({realisticUnit:N0}g)");
        }

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
            IsBaitDump: isBaitDump,
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

    public static int ComputeRealisticUnitPrice(
        int serverCheapest,
        int soldMedian,
        float velocity = 0f,
        bool hasQualitySchema = true)
    {
        if (serverCheapest > 0 && soldMedian > 0)
        {
            if (serverCheapest > soldMedian * 3L)
            {
                return (int)Math.Min(int.MaxValue, Math.Round(soldMedian * 1.25));
            }

            if (serverCheapest < soldMedian * 0.25)
            {
                return (int)Math.Min(int.MaxValue, Math.Round(soldMedian * BaitDumpRatioThreshold));
            }

            return serverCheapest;
        }

        if (soldMedian > 0)
        {
            return soldMedian;
        }

        if (serverCheapest > 0 && (!hasQualitySchema || velocity > 0.0001f))
        {
            return serverCheapest;
        }

        return 0;
    }

    public static long ComputeUndercutPrice(long serverCheapest, long soldMedian, long npcPriceLow, int undercutGil)
    {
        var baseFloor = ComputeVendorBreakevenPrice(npcPriceLow);
        var effective = serverCheapest > 0 ? serverCheapest : soldMedian;
        if (effective <= 0)
        {
            return 0;
        }

        if (serverCheapest > 0 && soldMedian > 0)
        {
            if (serverCheapest > soldMedian * 3L)
            {
                effective = (long)Math.Round(soldMedian * 1.25);
            }
            else if (IsBaitDumpPrice(serverCheapest, soldMedian))
            {
                return Math.Max(baseFloor, soldMedian);
            }
        }

        var step = Math.Max(0, undercutGil);
        return Math.Max(baseFloor, effective - step);
    }

    public static long ComputeDirectUndercut(long liveCheapest, long npcPriceLow, int undercutGil)
        => ComputeUndercutPrice(liveCheapest, soldMedian: 0, npcPriceLow, undercutGil);

    public static string FormatAgeShort(double days)
    {
        if (double.IsNaN(days))
        {
            return "unknown";
        }

        if (double.IsInfinity(days))
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
