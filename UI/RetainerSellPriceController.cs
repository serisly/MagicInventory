using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using MagicInventory.Core;
using MagicInventory.Services;
using MagicInventory.Services.Triage;

namespace MagicInventory.UI;

/// <summary>
/// Context-only live price setup & inspection strip anchored below AddonRetainerSell.
/// - Auto-fills undercut price from live server Compare Prices (InfoProxyItemSearch) whenever opened
///   (excluding your own retainers), or from Universalis on new-listing open.
/// - Shows real sale frequency, recency, median sold price, and viability verdict so you inspect before confirming.
/// - Never clicks Confirm; completely invisible when RetainerSell is closed.
/// </summary>
public sealed unsafe class RetainerSellPriceController
{
    private static readonly Vector4 ColorGood = new(0.45f, 0.88f, 0.56f, 1.0f);
    private static readonly Vector4 ColorWarn = new(0.98f, 0.78f, 0.34f, 1.0f);
    private static readonly Vector4 ColorBad = new(0.96f, 0.44f, 0.44f, 1.0f);
    private static readonly Vector4 ColorAccent = new(0.56f, 0.82f, 0.98f, 1.0f);
    private static readonly Vector4 ColorMuted = new(0.68f, 0.72f, 0.76f, 1.0f);

    private readonly IDataManager dataManager;
    private readonly IGameGui gameGui;
    private readonly Configuration configuration;
    private readonly HoldingsService holdings;
    private readonly UniversalisClient universalis;
    private readonly ItemTriageEngine triageEngine;

    private nint lastSellAddonPtr;
    private uint lastTrackedRawItemId;
    private InventoryType lastTrackedInvType;
    private int lastTrackedSlot = -1;
    private bool didInitialUniversalisFill;
    private uint lastAppliedLiveSearchItemId;
    private uint lastAppliedLiveSearchCheapest;
    private int lastAppliedLiveListingCount;
    private uint sessionLiveCheapest;
    private int sessionLiveListingCount;
    private bool sessionLiveExcludedOwn;
    private DateTime lastOwnRetainerRefreshUtc = DateTime.MinValue;
    private HashSet<ulong> ownRetainerIds = new();

    public RetainerSellPriceController(
        IDataManager dataManager,
        IGameGui gameGui,
        Configuration configuration,
        HoldingsService holdings,
        UniversalisClient universalis,
        ItemTriageEngine triageEngine)
    {
        this.dataManager = dataManager;
        this.gameGui = gameGui;
        this.configuration = configuration;
        this.holdings = holdings;
        this.universalis = universalis;
        this.triageEngine = triageEngine;
    }

    public void Draw()
    {
        var sellAddon = (AddonRetainerSell*)NativeUiHelpers.GetReadyAddon(gameGui, "RetainerSell");
        if (sellAddon == null)
        {
            ResetSessionTracking();
            return;
        }

        if (!TryResolveActiveSellItem(sellAddon, out var invType, out var invSlot, out var rawItemId, out var baseId, out var isHq, out var quantity, out var itemRow))
        {
            return;
        }

        var addonPtr = (nint)sellAddon;
        if (addonPtr != lastSellAddonPtr
            || rawItemId != lastTrackedRawItemId
            || invType != lastTrackedInvType
            || invSlot != lastTrackedSlot)
        {
            lastSellAddonPtr = addonPtr;
            lastTrackedRawItemId = rawItemId;
            lastTrackedInvType = invType;
            lastTrackedSlot = invSlot;
            didInitialUniversalisFill = false;
            lastAppliedLiveSearchItemId = 0;
            lastAppliedLiveSearchCheapest = 0;
            lastAppliedLiveListingCount = -1;
            sessionLiveCheapest = 0;
            sessionLiveListingCount = 0;
            sessionLiveExcludedOwn = false;
        }

        RefreshOwnRetainerNamesIfNeeded();

        // 1. Check live game-server Compare Prices (only capture from InfoProxyItemSearch while ItemSearchResult is open,
        //    so stale searches from earlier MB browsing never bleed into RetainerSell; latch for this RetainerSell session)
        if (NativeUiHelpers.IsAddonOpenAndReady(gameGui, "ItemSearchResult")
            && TryReadLiveServerCheapest(baseId, isHq, out var freshLiveCheapest, out var freshLiveCount, out var freshLiveExcludedOwn))
        {
            sessionLiveCheapest = freshLiveCheapest;
            sessionLiveListingCount = freshLiveCount;
            sessionLiveExcludedOwn = freshLiveExcludedOwn;
        }

        var hasLiveServer = sessionLiveCheapest > 0;
        var liveCheapest = sessionLiveCheapest;
        var liveListingCount = sessionLiveListingCount;
        var liveExcludedOwn = sessionLiveExcludedOwn;
        var liveUndercut = hasLiveServer
            ? MarketViabilityEvaluator.ComputeDirectUndercut(liveCheapest, itemRow.PriceLow, configuration.MarketUndercutGil)
            : 0L;

        // 2. Read Universalis liquidity / history data (queue background fetch if missing)
        var world = triageEngine.ActiveWorldName;
        MarketPriceSnapshot? mkt = null;
        var hasFresh = false;
        if (!string.IsNullOrWhiteSpace(world))
        {
            if (universalis.TryGetFreshPrice(world, baseId, out var fresh))
            {
                mkt = fresh;
                hasFresh = true;
            }
            else
            {
                universalis.QueueMissing(world, [baseId]);
                if (universalis.TryGetAnyCachedPrice(world, baseId, out var stale))
                {
                    mkt = stale;
                }
            }
        }

        var viability = MarketViabilityEvaluator.Evaluate(mkt, isHq, quantity, itemRow.PriceLow, configuration);

        // 3. PennyPincher-style automatic price setup (NEVER clicks Confirm, NEVER fights manual typing)
        if (configuration.MarketAutoFillRetainerSell && sellAddon->AskingPrice != null)
        {
            if (hasLiveServer
                && liveUndercut >= 2
                && (lastAppliedLiveSearchItemId != baseId
                    || lastAppliedLiveSearchCheapest != liveCheapest
                    || lastAppliedLiveListingCount != liveListingCount))
            {
                ApplyAskingPrice(sellAddon, (int)Math.Clamp(liveUndercut, 2L, 999_999_999L));
                lastAppliedLiveSearchItemId = baseId;
                lastAppliedLiveSearchCheapest = liveCheapest;
                lastAppliedLiveListingCount = liveListingCount;
                didInitialUniversalisFill = true;
            }
            else if (!didInitialUniversalisFill
                && invType != InventoryType.RetainerMarket
                && viability.HasData
                && viability.SuggestedUndercutPrice >= 2)
            {
                ApplyAskingPrice(sellAddon, (int)Math.Clamp(viability.SuggestedUndercutPrice, 2L, 999_999_999L));
                didInitialUniversalisFill = true;
            }
        }

        DrawDockedPriceStrip(
            sellAddon,
            itemRow,
            isHq,
            quantity,
            hasLiveServer,
            liveCheapest,
            liveListingCount,
            liveExcludedOwn,
            liveUndercut,
            mkt,
            hasFresh,
            viability);
    }

    private void DrawDockedPriceStrip(
        AddonRetainerSell* sellAddon,
        Item itemRow,
        bool isHq,
        int quantity,
        bool hasLiveServer,
        uint liveCheapest,
        int liveListingCount,
        bool liveExcludedOwn,
        long liveUndercut,
        MarketPriceSnapshot? mkt,
        bool hasFresh,
        MarketViabilityResult viability)
    {
        var vp = ImGui.GetMainViewport();
        var scale = sellAddon->Scale > 0.01f ? sellAddon->Scale : 1f;
        var rootW = sellAddon->AtkUnitBase.RootNode != null ? sellAddon->AtkUnitBase.RootNode->Width * scale : 360f;
        var rootH = sellAddon->AtkUnitBase.RootNode != null ? sellAddon->AtkUnitBase.RootNode->Height * scale : 240f;

        var panelW = Math.Clamp(rootW, 320f, 430f);
        var x = vp.Pos.X + sellAddon->AtkUnitBase.X;
        var y = vp.Pos.Y + sellAddon->AtkUnitBase.Y + rootH + 4f;
        if (y + 135f > vp.Pos.Y + vp.Size.Y)
        {
            y = Math.Max(vp.Pos.Y + 8f, vp.Pos.Y + sellAddon->AtkUnitBase.Y - 135f);
        }

        x = Math.Clamp(x, vp.Pos.X + 8f, vp.Pos.X + vp.Size.X - panelW - 8f);

        ImGui.SetNextWindowPos(new Vector2(x, y), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(panelW, 0f), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.94f);

        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar
            | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoFocusOnAppearing
            | ImGuiWindowFlags.AlwaysAutoResize;

        if (!ImGui.Begin("##MagicInventoryRetainerSellPrice", flags))
        {
            ImGui.End();
            return;
        }

        var hqTag = isHq ? " HQ" : string.Empty;
        ImGui.TextColored(ColorAccent, $"Price Check: {itemRow.Name.ExtractText()}{hqTag}");
        if (quantity > 1)
        {
            ImGui.SameLine();
            ImGui.TextColored(ColorMuted, $"x{quantity}");
        }

        // Source line: Live Server vs Universalis
        if (hasLiveServer)
        {
            var ownNote = liveExcludedOwn ? " (excl. yours)" : string.Empty;
            ImGui.TextColored(ColorGood, $"Live Server Cheapest: {liveCheapest:N0}g{ownNote}  ({liveListingCount} listed)");
        }
        else if (viability.HasData)
        {
            var ageMin = mkt != null ? Math.Max(0, (int)Math.Round((DateTime.UtcNow - mkt.FetchedUtc).TotalMinutes)) : 0;
            var ageStr = ageMin < 60 ? $"{ageMin}m ago" : $"{ageMin / 60}h ago";
            var cheapestStr = viability.ServerCheapest > 0 ? $"{viability.ServerCheapest:N0}g" : "none";
            ImGui.TextColored(
                hasFresh ? ColorMuted : ColorWarn,
                $"World Cheapest (Universalis, {ageStr}): {cheapestStr}");
        }
        else
        {
            ImGui.TextColored(ColorMuted, "Fetching world price history... (click Compare Prices for live board)");
        }

        // Liquidity & History line
        if (viability.HasData)
        {
            var medianStr = viability.SoldMedian > 0 ? $"{viability.SoldMedian:N0}g" : "n/a";
            var lastSoldStr = MarketViabilityEvaluator.FormatAgeShort(viability.DaysSinceLastSale);
            ImGui.TextUnformatted(
                $"Sold Median: {medianStr}  |  Freq: {viability.EffectiveVelocity:0.00}/day  |  Last: {lastSoldStr}");

            var verdictColor = viability.IsRealisticToSell
                ? ColorGood
                : (viability.IsDeadOrIlliquid ? ColorBad : ColorWarn);
            ImGui.TextColored(verdictColor, viability.IsRealisticToSell
                ? $"Verdict: Good to list ({viability.Reason})"
                : $"Warning: {viability.Reason}");

            // Panic-dump warning when live cheapest is far below recent sold median
            if (hasLiveServer && viability.SoldMedian > 0 && liveCheapest < viability.SoldMedian * 0.55)
            {
                ImGui.TextColored(ColorWarn, $"Note: Live cheapest ({liveCheapest:N0}g) is far below median ({viability.SoldMedian:N0}g).");
            }
        }

        ImGui.Spacing();

        // Action row: Compare Prices + Apply Undercut + Apply Median (never clicks Confirm!)
        var compareBtn = sellAddon->ComparePrices != null
            ? sellAddon->ComparePrices
            : sellAddon->AtkUnitBase.GetComponentButtonById(4);
        if (compareBtn != null && compareBtn->IsEnabled)
        {
            if (ImGui.SmallButton("Compare Prices"))
            {
                NativeUiHelpers.ClickAddonButton((AtkUnitBase*)sellAddon, compareBtn);
            }

            ImGui.SameLine();
        }

        var preferredUndercut = hasLiveServer && liveUndercut >= 2
            ? liveUndercut
            : viability.SuggestedUndercutPrice;
        if (preferredUndercut >= 2)
        {
            var undercutLabel = hasLiveServer
                ? $"Set Live -{configuration.MarketUndercutGil} ({preferredUndercut:N0}g)"
                : $"Set Undercut ({preferredUndercut:N0}g)";
            if (ImGui.SmallButton(undercutLabel))
            {
                ApplyAskingPrice(sellAddon, (int)Math.Clamp(preferredUndercut, 2L, 999_999_999L));
            }
        }

        if (viability.SoldMedian >= Math.Max(2, (int)itemRow.PriceLow + 1)
            && Math.Abs(viability.SoldMedian - preferredUndercut) >= 50)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton($"Set Median ({viability.SoldMedian:N0}g)"))
            {
                ApplyAskingPrice(sellAddon, viability.SoldMedian);
            }
        }

        ImGui.End();
    }

    private static void ApplyAskingPrice(AddonRetainerSell* sellAddon, int price)
    {
        if (sellAddon == null || sellAddon->AskingPrice == null || price < 1)
        {
            return;
        }

        sellAddon->AskingPrice->SetValue(price);
    }

    private bool TryResolveActiveSellItem(
        AddonRetainerSell* sellAddon,
        out InventoryType invType,
        out int invSlot,
        out uint rawItemId,
        out uint baseId,
        out bool isHq,
        out int quantity,
        out Item itemRow)
    {
        invType = default;
        invSlot = -1;
        rawItemId = 0;
        baseId = 0;
        isHq = false;
        quantity = 1;
        itemRow = default;

        var itemSheet = dataManager.GetExcelSheet<Item>();
        if (itemSheet == null)
        {
            return false;
        }

        var agent = AgentRetainer.Instance();
        var mgr = InventoryManager.Instance();
        if (agent != null && mgr != null)
        {
            var candidateType = agent->SellItemInventoryType;
            var candidateSlot = (int)agent->SellItemInventorySlot;
            var slot = InventorySafetyEvaluator.GetSlot(mgr, candidateType, candidateSlot);
            if (slot != null && slot->ItemId != 0 && slot->Quantity > 0)
            {
                var norm = ItemIdentity.NormalizeBaseItemId(slot->ItemId);
                var row = itemSheet.GetRowOrDefault(norm);
                if (row is not null)
                {
                    invType = candidateType;
                    invSlot = candidateSlot;
                    rawItemId = slot->ItemId;
                    baseId = norm;
                    isHq = ItemIdentity.IsHighQuality(slot);
                    quantity = Math.Max(1, (int)slot->Quantity);
                    itemRow = row.Value;
                    return true;
                }
            }
        }

        return false;
    }

    private bool TryReadLiveServerCheapest(
        uint baseId,
        bool isHq,
        out uint cheapestPrice,
        out int matchingListingCount,
        out bool excludedOwnRetainer)
    {
        cheapestPrice = 0;
        matchingListingCount = 0;
        excludedOwnRetainer = false;

        var proxy = InfoProxyItemSearch.Instance();
        if (proxy == null
            || proxy->WaitingForListings
            || ItemIdentity.NormalizeBaseItemId(proxy->SearchItemId) != baseId)
        {
            return false;
        }

        var count = Math.Min((int)proxy->ListingCount, proxy->Listings.Length);
        if (count <= 0)
        {
            return false;
        }

        uint bestMatchingQuality = 0;
        uint bestAnyQuality = 0;
        var myContentId = InventorySafetyEvaluator.GetCurrentContentId();

        for (var i = 0; i < count; i++)
        {
            var l = proxy->Listings[i];
            if (l.IsMannequin || l.IsSellingAsSet || l.Quantity == 0 || l.UnitPrice == 0)
            {
                continue;
            }

            if (ItemIdentity.NormalizeBaseItemId(l.ItemId) != baseId)
            {
                continue;
            }

            if ((myContentId != 0 && l.ContentId == myContentId)
                || (l.RetainerId != 0 && ownRetainerIds.Contains(l.RetainerId)))
            {
                excludedOwnRetainer = true;
                continue;
            }

            if (bestAnyQuality == 0 || l.UnitPrice < bestAnyQuality)
            {
                bestAnyQuality = l.UnitPrice;
            }

            if (isHq)
            {
                if (l.IsHqItem)
                {
                    matchingListingCount++;
                    if (bestMatchingQuality == 0 || l.UnitPrice < bestMatchingQuality)
                    {
                        bestMatchingQuality = l.UnitPrice;
                    }
                }
            }
            else
            {
                // For NQ listings, compete against all listings (an NQ priced above an HQ won't sell)
                matchingListingCount++;
                if (bestMatchingQuality == 0 || l.UnitPrice < bestMatchingQuality)
                {
                    bestMatchingQuality = l.UnitPrice;
                }
            }
        }

        cheapestPrice = bestMatchingQuality > 0 ? bestMatchingQuality : bestAnyQuality;
        if (matchingListingCount == 0 && cheapestPrice > 0)
        {
            matchingListingCount = count;
        }

        return cheapestPrice > 0;
    }

    private void RefreshOwnRetainerNamesIfNeeded()
    {
        var now = DateTime.UtcNow;
        if ((now - lastOwnRetainerRefreshUtc).TotalSeconds < 10.0 && ownRetainerIds.Count > 0)
        {
            return;
        }

        lastOwnRetainerRefreshUtc = now;
        var next = new HashSet<ulong>();
        foreach (var r in holdings.GetKnownRetainersForCurrentCharacter())
        {
            if (r.RetainerId != 0)
            {
                next.Add(r.RetainerId);
            }
        }

        var activeId = HoldingsService.GetActiveRetainerId();
        if (activeId != 0)
        {
            next.Add(activeId);
        }

        ownRetainerIds = next;
    }

    private void ResetSessionTracking()
    {
        lastSellAddonPtr = 0;
        lastTrackedRawItemId = 0;
        lastTrackedSlot = -1;
        didInitialUniversalisFill = false;
        lastAppliedLiveSearchItemId = 0;
        lastAppliedLiveSearchCheapest = 0;
        lastAppliedLiveListingCount = -1;
        sessionLiveCheapest = 0;
        sessionLiveListingCount = 0;
        sessionLiveExcludedOwn = false;
    }
}
