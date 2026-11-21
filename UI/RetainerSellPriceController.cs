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
    private enum LiveSearchStatus
    {
        NotLoaded = 0,
        CompetitorsFound = 1,
        OnlyOwnListings = 2,
        NoHqListings = 3,
        EmptyBoard = 4,
    }

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
    private readonly SafeTransactionQueue transactionQueue;

    private Dictionary<string, uint>? itemNameToBaseId;
    private nint lastSellAddonPtr;
    private uint lastTrackedRawItemId;
    private InventoryType lastTrackedInvType;
    private int lastTrackedSlot = -1;
    private bool didInitialUniversalisFill;
    private bool userEditedManually;
    private int lastWrittenAskingPrice;
    private uint lastAppliedLiveSearchItemId;
    private uint lastAppliedLiveSearchCheapest;
    private LiveSearchStatus sessionLiveStatus = LiveSearchStatus.NotLoaded;
    private uint sessionLiveCheapest;
    private int sessionLiveListingCount;
    private bool sessionLiveExcludedOwn;
    private float lastPanelHeight = 155f;
    private DateTime lastOwnRetainerRefreshUtc = DateTime.MinValue;
    private HashSet<ulong> ownRetainerIds = new();

    public RetainerSellPriceController(
        IDataManager dataManager,
        IGameGui gameGui,
        Configuration configuration,
        HoldingsService holdings,
        UniversalisClient universalis,
        ItemTriageEngine triageEngine,
        SafeTransactionQueue transactionQueue)
    {
        this.dataManager = dataManager;
        this.gameGui = gameGui;
        this.configuration = configuration;
        this.holdings = holdings;
        this.universalis = universalis;
        this.triageEngine = triageEngine;
        this.transactionQueue = transactionQueue;
    }

    public void Draw()
    {
        var sellAddon = (AddonRetainerSell*)NativeUiHelpers.GetReadyAddon(gameGui, "RetainerSell");
        if (sellAddon == null)
        {
            ResetSessionTracking();
            return;
        }

        if (!TryResolveActiveSellItem(
            sellAddon,
            out var invType,
            out var invSlot,
            out var rawItemId,
            out var baseId,
            out var isHq,
            out var quantity,
            out var isAdjustingExisting,
            out var itemRow))
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
            userEditedManually = false;
            lastWrittenAskingPrice = 0;
            lastAppliedLiveSearchItemId = 0;
            lastAppliedLiveSearchCheapest = 0;
            sessionLiveStatus = LiveSearchStatus.NotLoaded;
            sessionLiveCheapest = 0;
            sessionLiveListingCount = 0;
            sessionLiveExcludedOwn = false;
        }

        RefreshOwnRetainerNamesIfNeeded();

        // Detect manual user edits so auto-fill never fights manual typing
        if (lastWrittenAskingPrice > 0 && sellAddon->AskingPrice != null)
        {
            var currentVal = sellAddon->AskingPrice->Value;
            if (currentVal != lastWrittenAskingPrice)
            {
                userEditedManually = true;
            }
        }

        // 1. Check live game-server Compare Prices (only capture from InfoProxyItemSearch while ItemSearchResult is open,
        //    so stale searches from earlier MB browsing never bleed into RetainerSell; latch for this RetainerSell session)
        if (NativeUiHelpers.IsAddonOpenAndReady(gameGui, "ItemSearchResult"))
        {
            var status = ReadLiveServerState(baseId, isHq, out var freshLiveCheapest, out var freshLiveCount, out var freshLiveExcludedOwn);
            if (status != LiveSearchStatus.NotLoaded)
            {
                sessionLiveStatus = status;
                sessionLiveCheapest = freshLiveCheapest;
                sessionLiveListingCount = freshLiveCount;
                sessionLiveExcludedOwn = freshLiveExcludedOwn;
            }
        }

        var hasLiveCompetitors = sessionLiveStatus == LiveSearchStatus.CompetitorsFound && sessionLiveCheapest > 0;
        var liveCheapest = sessionLiveCheapest;
        var liveListingCount = sessionLiveListingCount;
        var liveExcludedOwn = sessionLiveExcludedOwn;
        var liveUndercut = hasLiveCompetitors
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
        var isLiveBaitDump = hasLiveCompetitors
            && viability.SoldMedian > 0
            && liveCheapest < viability.SoldMedian * 0.55;

        // 3. Automatic price setup (NEVER runs while SafeTransactionQueue is executing, NEVER clicks Confirm,
        //    NEVER fights manual typing, NEVER undercuts your own monopoly, and PAUSES on bait-dumps)
        if (!transactionQueue.IsBusy
            && !userEditedManually
            && configuration.MarketAutoFillRetainerSell
            && sellAddon->AskingPrice != null)
        {
            if (hasLiveCompetitors
                && !isLiveBaitDump
                && liveUndercut >= 2
                && (lastAppliedLiveSearchItemId != baseId
                    || lastAppliedLiveSearchCheapest != liveCheapest))
            {
                ApplyAskingPrice(sellAddon, (int)Math.Clamp(liveUndercut, 2L, 999_999_999L));
                lastAppliedLiveSearchItemId = baseId;
                lastAppliedLiveSearchCheapest = liveCheapest;
                didInitialUniversalisFill = true;
            }
            else if (!didInitialUniversalisFill
                && !isAdjustingExisting
                && sessionLiveStatus != LiveSearchStatus.OnlyOwnListings
                && !isLiveBaitDump
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
            sessionLiveStatus,
            hasLiveCompetitors,
            isLiveBaitDump,
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
        LiveSearchStatus liveStatus,
        bool hasLiveCompetitors,
        bool isLiveBaitDump,
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

        var panelW = Math.Clamp(rootW, 330f, 440f);
        var panelH = Math.Clamp(lastPanelHeight, 120f, 260f);
        var x = vp.Pos.X + sellAddon->AtkUnitBase.X;
        var y = vp.Pos.Y + sellAddon->AtkUnitBase.Y + rootH + 4f;
        if (y + panelH > vp.Pos.Y + vp.Size.Y)
        {
            y = Math.Max(vp.Pos.Y + 8f, vp.Pos.Y + sellAddon->AtkUnitBase.Y - panelH - 4f);
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

        // Source line: Live Server status vs Universalis
        if (hasLiveCompetitors)
        {
            var ownNote = liveExcludedOwn ? " (excl. yours)" : string.Empty;
            ImGui.TextColored(
                isLiveBaitDump ? ColorWarn : ColorGood,
                $"Live Server Cheapest: {liveCheapest:N0}g{ownNote}  ({liveListingCount} listed)");
        }
        else if (liveStatus == LiveSearchStatus.OnlyOwnListings)
        {
            ImGui.TextColored(ColorGood, "Live Server: You already own all listings on this board.");
        }
        else if (liveStatus == LiveSearchStatus.NoHqListings)
        {
            var hqMedStr = viability.SoldMedian > 0 ? $"HQ sold median {viability.SoldMedian:N0}g" : "no HQ sales history";
            ImGui.TextColored(ColorAccent, $"Live Server: 0 HQ competitor listings ({hqMedStr}).");
        }
        else if (liveStatus == LiveSearchStatus.EmptyBoard)
        {
            ImGui.TextColored(ColorAccent, "Live Server: 0 listings on board.");
        }
        else if (viability.HasData)
        {
            var ageMin = mkt != null ? Math.Max(0, (int)Math.Round((DateTime.UtcNow - mkt.FetchedUtc).TotalMinutes)) : 0;
            var ageStr = ageMin < 60 ? $"{ageMin}m ago" : $"{ageMin / 60}h ago";
            var updatingTag = hasFresh ? string.Empty : " (updating...)";
            var cheapestStr = viability.ServerCheapest > 0 ? $"{viability.ServerCheapest:N0}g" : "none";
            ImGui.TextColored(
                hasFresh ? ColorMuted : ColorWarn,
                $"World Cheapest (Universalis, {ageStr}{updatingTag}): {cheapestStr}");
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

            // Prominent bait-dump warning when live cheapest is far below recent sold median
            if (isLiveBaitDump)
            {
                ImGui.TextColored(
                    ColorBad,
                    $"Auto-fill paused: live {liveCheapest:N0}g is <55% of median ({viability.SoldMedian:N0}g).");
            }
        }

        ImGui.Spacing();

        // Action row: Compare Prices + Apply Undercut + Apply Median (never clicks Confirm!)
        var drewButton = false;
        var compareBtn = sellAddon->ComparePrices != null
            ? sellAddon->ComparePrices
            : sellAddon->AtkUnitBase.GetComponentButtonById(4);
        if (compareBtn != null && compareBtn->IsEnabled)
        {
            if (ImGui.SmallButton("Compare Prices"))
            {
                NativeUiHelpers.ClickAddonButton((AtkUnitBase*)sellAddon, compareBtn);
            }

            drewButton = true;
        }

        var preferredUndercut = hasLiveCompetitors && liveUndercut >= 2
            ? liveUndercut
            : (liveStatus == LiveSearchStatus.OnlyOwnListings ? 0L : viability.SuggestedUndercutPrice);
        if (preferredUndercut >= 2)
        {
            if (drewButton)
            {
                ImGui.SameLine();
            }

            var undercutLabel = hasLiveCompetitors
                ? $"Set Live -{configuration.MarketUndercutGil} ({preferredUndercut:N0}g)"
                : $"Set Undercut ({preferredUndercut:N0}g)";
            if (ImGui.SmallButton(undercutLabel))
            {
                ApplyAskingPrice(sellAddon, (int)Math.Clamp(preferredUndercut, 2L, 999_999_999L));
                userEditedManually = false;
            }

            drewButton = true;
        }

        var minSafeMedian = MarketViabilityEvaluator.ComputeVendorBreakevenPrice(itemRow.PriceLow);
        if (viability.SoldMedian >= minSafeMedian
            && (preferredUndercut < 2 || Math.Abs(viability.SoldMedian - preferredUndercut) >= 50))
        {
            if (drewButton)
            {
                ImGui.SameLine();
            }

            if (ImGui.SmallButton($"Set Median ({viability.SoldMedian:N0}g)"))
            {
                ApplyAskingPrice(sellAddon, viability.SoldMedian);
                userEditedManually = false;
            }
        }

        var currentH = ImGui.GetWindowHeight();
        if (currentH > 40f)
        {
            lastPanelHeight = currentH;
        }

        ImGui.End();
    }

    private void ApplyAskingPrice(AddonRetainerSell* sellAddon, int price)
    {
        if (sellAddon == null || sellAddon->AskingPrice == null || price < 1)
        {
            return;
        }

        sellAddon->AskingPrice->SetValue(price);
        lastWrittenAskingPrice = price;
    }

    private bool TryResolveActiveSellItem(
        AddonRetainerSell* sellAddon,
        out InventoryType invType,
        out int invSlot,
        out uint rawItemId,
        out uint baseId,
        out bool isHq,
        out int quantity,
        out bool isAdjustingExisting,
        out Item itemRow)
    {
        invType = default;
        invSlot = -1;
        rawItemId = 0;
        baseId = 0;
        isHq = false;
        quantity = 1;
        isAdjustingExisting = false;
        itemRow = default;

        var itemSheet = dataManager.GetExcelSheet<Item>();
        if (itemSheet == null)
        {
            return false;
        }

        // Read the authoritative item name & HQ indicator directly from AddonRetainerSell
        var rawAddonLabel = sellAddon->ItemName != null
            ? NativeUiHelpers.ReadTextNodeValue(sellAddon->ItemName)
            : string.Empty;
        var addonHasLabel = !string.IsNullOrWhiteSpace(rawAddonLabel);
        var addonIsHq = addonHasLabel && rawAddonLabel.Contains('\uE03C');
        var addonNormName = addonHasLabel ? NativeUiHelpers.NormalizeItemLabel(rawAddonLabel) : string.Empty;
        var addonQty = sellAddon->Quantity != null && sellAddon->Quantity->Value > 0
            ? (int)sellAddon->Quantity->Value
            : 0;

        var agent = AgentRetainer.Instance();
        var mgr = InventoryManager.Instance();

        // 1. Check AgentRetainer's tracked slot — ONLY accept when it agrees with AddonRetainerSell->ItemName + HQ
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
                    var slotIsHq = ItemIdentity.IsHighQuality(slot);
                    var slotName = NativeUiHelpers.NormalizeItemLabel(row.Value.Name.ExtractText());
                    var matchesAddon = !addonHasLabel
                        || (slotIsHq == addonIsHq
                            && string.Equals(slotName, addonNormName, StringComparison.OrdinalIgnoreCase));
                    if (matchesAddon)
                    {
                        invType = candidateType;
                        invSlot = candidateSlot;
                        rawItemId = slot->ItemId;
                        baseId = norm;
                        isHq = slotIsHq;
                        quantity = addonQty > 0 ? addonQty : Math.Max(1, (int)slot->Quantity);
                        isAdjustingExisting = candidateType == InventoryType.RetainerMarket;
                        itemRow = row.Value;
                        return true;
                    }
                }
            }
        }

        if (!addonHasLabel || string.IsNullOrEmpty(addonNormName))
        {
            return false;
        }

        // 2. Fallback: scan InventoryType.RetainerMarket (handles Adjust Price when AgentRetainer still points to a stale bag slot)
        if (mgr != null)
        {
            var marketContainer = mgr->GetInventoryContainer(InventoryType.RetainerMarket);
            if (InventorySafetyEvaluator.IsContainerReadable(marketContainer))
            {
                var bestMatchSlot = -1;
                uint bestMatchRawId = 0;
                uint bestMatchBaseId = 0;
                int bestMatchQty = 1;
                Item bestMatchRow = default;

                for (var i = 0; i < marketContainer->Size; i++)
                {
                    var slot = InventorySafetyEvaluator.GetSlot(marketContainer, i);
                    if (slot == null || slot->ItemId == 0 || slot->Quantity <= 0)
                    {
                        continue;
                    }

                    if (ItemIdentity.IsHighQuality(slot) != addonIsHq)
                    {
                        continue;
                    }

                    var norm = ItemIdentity.NormalizeBaseItemId(slot->ItemId);
                    var row = itemSheet.GetRowOrDefault(norm);
                    if (row is null)
                    {
                        continue;
                    }

                    var slotName = NativeUiHelpers.NormalizeItemLabel(row.Value.Name.ExtractText());
                    if (!string.Equals(slotName, addonNormName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    bestMatchSlot = i;
                    bestMatchRawId = slot->ItemId;
                    bestMatchBaseId = norm;
                    bestMatchQty = Math.Max(1, (int)slot->Quantity);
                    bestMatchRow = row.Value;
                    if (addonQty > 0 && slot->Quantity == addonQty)
                    {
                        break;
                    }
                }

                if (bestMatchSlot >= 0)
                {
                    invType = InventoryType.RetainerMarket;
                    invSlot = bestMatchSlot;
                    rawItemId = bestMatchRawId;
                    baseId = bestMatchBaseId;
                    isHq = addonIsHq;
                    quantity = addonQty > 0 ? addonQty : bestMatchQty;
                    isAdjustingExisting = true;
                    itemRow = bestMatchRow;
                    return true;
                }
            }
        }

        // 3. Last-resort fallback: resolve by Item sheet name (fail-safe: treat as adjusting existing so we don't auto-overwrite)
        EnsureItemNameIndex(itemSheet);
        if (itemNameToBaseId != null
            && itemNameToBaseId.TryGetValue(addonNormName, out var sheetBaseId))
        {
            var row = itemSheet.GetRowOrDefault(sheetBaseId);
            if (row is not null)
            {
                invType = InventoryType.RetainerMarket;
                invSlot = -1;
                rawItemId = addonIsHq ? sheetBaseId + 1_000_000u : sheetBaseId;
                baseId = sheetBaseId;
                isHq = addonIsHq;
                quantity = addonQty > 0 ? addonQty : 1;
                isAdjustingExisting = true;
                itemRow = row.Value;
                return true;
            }
        }

        return false;
    }

    private void EnsureItemNameIndex(Lumina.Excel.ExcelSheet<Item> itemSheet)
    {
        if (itemNameToBaseId != null)
        {
            return;
        }

        var map = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in itemSheet)
        {
            if (row.RowId == 0 || !ItemIdentity.IsMarketable(row))
            {
                continue;
            }

            var norm = NativeUiHelpers.NormalizeItemLabel(row.Name.ExtractText());
            if (!string.IsNullOrEmpty(norm))
            {
                map.TryAdd(norm, row.RowId);
            }
        }

        itemNameToBaseId = map;
    }

    private LiveSearchStatus ReadLiveServerState(
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
            return LiveSearchStatus.NotLoaded;
        }

        var count = Math.Min((int)proxy->ListingCount, proxy->Listings.Length);
        if (count <= 0)
        {
            return LiveSearchStatus.EmptyBoard;
        }

        uint bestMatchingQuality = 0;
        var ownMatchingCount = 0;
        var anyCompetitorCount = 0;
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
                if (!isHq || l.IsHqItem)
                {
                    ownMatchingCount++;
                }

                continue;
            }

            anyCompetitorCount++;

            if (isHq)
            {
                // NEVER fall back to NQ listings when pricing an HQ item!
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

        if (bestMatchingQuality > 0)
        {
            cheapestPrice = bestMatchingQuality;
            return LiveSearchStatus.CompetitorsFound;
        }

        if (ownMatchingCount > 0)
        {
            return LiveSearchStatus.OnlyOwnListings;
        }

        if (isHq && anyCompetitorCount > 0)
        {
            return LiveSearchStatus.NoHqListings;
        }

        return LiveSearchStatus.EmptyBoard;
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
        userEditedManually = false;
        lastWrittenAskingPrice = 0;
        lastAppliedLiveSearchItemId = 0;
        lastAppliedLiveSearchCheapest = 0;
        sessionLiveStatus = LiveSearchStatus.NotLoaded;
        sessionLiveCheapest = 0;
        sessionLiveListingCount = 0;
        sessionLiveExcludedOwn = false;
    }
}
