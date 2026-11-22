using System;
using System.Collections.Generic;
using System.Linq;
using MagicInventory.Core;

namespace MagicInventory.Services;

public sealed record MarketBuyCandidate(
    int ListingIndex,
    uint ItemId,
    string ItemName,
    uint Quantity,
    uint UnitPrice,
    uint TotalCost,
    bool IsHq,
    ulong RetainerId = 0);

public readonly record struct MarketListingInput(
    int ListingIndex,
    uint Quantity,
    uint UnitPrice,
    uint TotalCost,
    bool IsHq,
    ulong RetainerId);

public static class MarketBoardListingSelector
{
    public const uint MedianSlackGil = 500;

    public static MarketBuyCandidate? SelectBestListing(
        IReadOnlyList<MarketListingInput> listings,
        uint baseItemId,
        string itemName,
        int missingNeeded,
        ulong currentGil,
        ulong maxGilPerPurchase,
        double outlierMultiplier,
        ulong histAnchorNq,
        ulong histAnchorHq,
        out string blockReason)
    {
        blockReason = string.Empty;
        if (listings.Count == 0 || missingNeeded <= 0)
        {
            return null;
        }

        var nqPrices = new List<uint>(listings.Count);
        var hqPrices = new List<uint>(listings.Count);
        for (var i = 0; i < listings.Count; i++)
        {
            if (listings[i].IsHq) hqPrices.Add(listings[i].UnitPrice);
            else nqPrices.Add(listings[i].UnitPrice);
        }

        static ulong MedianOf(List<uint> list)
        {
            if (list.Count == 0) return 0;
            list.Sort();
            return list[list.Count / 2];
        }

        var liveMedianNq = MedianOf(nqPrices);
        var liveMedianHq = MedianOf(hqPrices);
        var anchorNq = histAnchorNq > 0 ? histAnchorNq : (liveMedianNq > 0 ? liveMedianNq : liveMedianHq);
        var anchorHq = histAnchorHq > 0 ? histAnchorHq : (liveMedianHq > 0 ? liveMedianHq : anchorNq);

        var maxSafeUnitNq = Math.Max((ulong)(anchorNq * outlierMultiplier), anchorNq + MedianSlackGil);
        var maxSafeUnitHq = Math.Max((ulong)(anchorHq * outlierMultiplier), anchorHq + MedianSlackGil);

        MarketBuyCandidate? bestCovering = null;
        var safePartials = new List<MarketBuyCandidate>();
        var sawTooPricey = false;
        var sawOutlier = false;

        foreach (var l in listings)
        {
            var maxSafeUnit = l.IsHq ? maxSafeUnitHq : maxSafeUnitNq;
            if (l.UnitPrice > maxSafeUnit)
            {
                sawOutlier = true;
                continue;
            }

            if (l.TotalCost > maxGilPerPurchase || l.TotalCost > currentGil)
            {
                sawTooPricey = true;
                continue;
            }

            var cand = new MarketBuyCandidate(l.ListingIndex, baseItemId, itemName, l.Quantity, l.UnitPrice, l.TotalCost, l.IsHq, l.RetainerId);
            if (l.Quantity >= (uint)missingNeeded)
            {
                if (bestCovering == null
                    || cand.TotalCost < bestCovering.TotalCost
                    || (cand.TotalCost == bestCovering.TotalCost && cand.Quantity < bestCovering.Quantity))
                {
                    bestCovering = cand;
                }
            }
            else
            {
                safePartials.Add(cand);
            }
        }

        MarketBuyCandidate? bestPartial = null;
        if (safePartials.Count > 0)
        {
            var cheapestPartialUnit = safePartials.Min(p => p.UnitPrice);
            var nearCheapestCeiling = Math.Max((ulong)Math.Ceiling(cheapestPartialUnit * 1.15), cheapestPartialUnit + 25UL);
            foreach (var p in safePartials)
            {
                if (p.UnitPrice > nearCheapestCeiling)
                {
                    continue;
                }

                if (bestPartial == null
                    || p.Quantity > bestPartial.Quantity
                    || (p.Quantity == bestPartial.Quantity && p.TotalCost < bestPartial.TotalCost))
                {
                    bestPartial = p;
                }
            }
        }

        if (bestCovering != null)
        {
            if (bestCovering.Quantity > (uint)missingNeeded)
            {
                var idealSubtotal = (ulong)bestCovering.UnitPrice * (ulong)missingNeeded;
                var neededIdealCost = idealSubtotal + (idealSubtotal * 5UL / 100UL);
                if (bestPartial != null
                    && (ulong)bestCovering.TotalCost > Math.Max((ulong)(neededIdealCost * 1.5), neededIdealCost + 500UL))
                {
                    return bestPartial;
                }

                if (bestCovering.Quantity > (uint)Math.Max(missingNeeded * 5, missingNeeded + 10)
                    && (ulong)bestCovering.TotalCost > Math.Max(neededIdealCost * 3UL, neededIdealCost + 2000UL))
                {
                    blockReason = $"Smallest stack (x{bestCovering.Quantity} for {bestCovering.TotalCost:N0}g) far exceeds needed x{missingNeeded}.";
                    return null;
                }
            }

            return bestCovering;
        }

        if (bestPartial != null)
        {
            return bestPartial;
        }

        if (sawTooPricey)
        {
            blockReason = $"Listings exceed the {maxGilPerPurchase:N0}g safety cap or your Gil.";
        }
        else if (sawOutlier)
        {
            var shownAnchor = anchorNq > 0 ? anchorNq : anchorHq;
            blockReason = $"Listings exceed recent prices (~{shownAnchor:N0}g median), skipped.";
        }

        return null;
    }

    public static string NormalizeSearchRowText(string raw)
    {
        var normalized = NativeUiHelpers.NormalizeItemLabel(raw);
        var openParen = normalized.LastIndexOf(" (", StringComparison.Ordinal);
        if (openParen > 0 && normalized.EndsWith(')'))
        {
            var inner = normalized.AsSpan(openParen + 2, normalized.Length - openParen - 3);
            var allDigits = inner.Length > 0;
            for (var i = 0; i < inner.Length; i++)
            {
                if (!char.IsDigit(inner[i]))
                {
                    allDigits = false;
                    break;
                }
            }

            if (allDigits)
            {
                return normalized[..openParen].TrimEnd();
            }
        }

        return normalized;
    }

    public static bool DoesPurchaseConfirmPromptMatch(
        string prompt,
        string expectedName,
        string? pluralName,
        uint unitPrice,
        uint quantity,
        uint expectedTotalCost)
    {
        if (string.IsNullOrEmpty(prompt) || string.IsNullOrWhiteSpace(expectedName))
        {
            return false;
        }

        var cleanName = NativeUiHelpers.NormalizeItemLabel(expectedName);
        var nameMatches = prompt.Contains(cleanName, StringComparison.OrdinalIgnoreCase);
        if (!nameMatches && quantity > 1 && !string.IsNullOrWhiteSpace(pluralName))
        {
            nameMatches = prompt.Contains(NativeUiHelpers.NormalizeItemLabel(pluralName), StringComparison.OrdinalIgnoreCase);
        }

        if (!nameMatches)
        {
            return false;
        }

        var numbers = ExtractExactPromptNumbers(prompt);
        if (quantity > 1 && !numbers.Contains(quantity))
        {
            return false;
        }

        var subtotal = (long)unitPrice * Math.Max(1u, quantity);
        return numbers.Contains(expectedTotalCost) || numbers.Contains(subtotal);
    }

    public static HashSet<long> ExtractExactPromptNumbers(string prompt)
    {
        var numbers = new HashSet<long>();
        var i = 0;
        var len = prompt.Length;
        while (i < len)
        {
            if (!char.IsAsciiDigit(prompt[i]))
            {
                i++;
                continue;
            }

            long value = 0;
            var overflow = false;
            var j = i;
            while (j < len)
            {
                var ch = prompt[j];
                if (char.IsAsciiDigit(ch))
                {
                    var d = ch - '0';
                    if (value > (long.MaxValue - d) / 10)
                    {
                        overflow = true;
                    }
                    else
                    {
                        value = value * 10 + d;
                    }

                    j++;
                    continue;
                }

                if ((ch is ',' or '.' or ' ' or '\u00A0' or '\u202F')
                    && j + 3 < len
                    && char.IsAsciiDigit(prompt[j + 1])
                    && char.IsAsciiDigit(prompt[j + 2])
                    && char.IsAsciiDigit(prompt[j + 3])
                    && (j + 4 >= len || !char.IsAsciiDigit(prompt[j + 4])))
                {
                    j++;
                    continue;
                }

                break;
            }

            if (!overflow && value > 0)
            {
                numbers.Add(value);
            }

            i = j;
        }

        return numbers;
    }
}
