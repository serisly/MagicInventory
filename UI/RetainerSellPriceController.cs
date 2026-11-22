using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
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

public sealed unsafe class RetainerSellPriceController
{
    private enum LiveSearchStatus
    {
        NotLoaded = 0,
        CompetitorsFound = 1,
        OnlyOwnListings = 2,
        NoHqListings = 3,
        NoHqInTop100 = 4,
        EmptyBoard = 5,
    }

    private readonly record struct ResolvedSellSession(
        InventoryType InvType,
        int InvSlot,
        uint RawItemId,
        uint BaseId,
        bool IsHq,
        int Quantity,
        bool IsAdjustingExisting,
        Item ItemRow);

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

    private nint lastSellAddonPtr;
    private string lastAddonRawLabel = string.Empty;
    private ResolvedSellSession? cachedSession;
    private uint lastTrackedRawItemId;
    private InventoryType lastTrackedInvType;
    private int lastTrackedSlot = -1;
    private int initialAskingPrice;
    private bool hasCapturedInitialPrice;
    private bool didInitialUniversalisFill;
    private bool userEditedManually;
    private bool wasQueueBusyInSession;
    private int lastWrittenAskingPrice;
    private uint lastAppliedLiveSearchItemId;
    private uint lastAppliedLiveSearchCheapest;
    private LiveSearchStatus sessionLiveStatus = LiveSearchStatus.NotLoaded;
    private uint sessionLiveCheapest;
    private uint sessionOwnCheapest;
    private int sessionLiveListingCount;
    private bool sessionLiveExcludedOwn;
    private bool waitForFreshSameItemSearch;
    private float lastPanelHeight = 155f;
    private DateTime lastOwnRetainerRefreshUtc = DateTime.MinValue;
    private ulong lastOwnRetainerContentId;
    private ulong lastOwnRetainerActiveId;
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

        var addonPtr = (nint)sellAddon;
        var rawAddonLabel = RetainerSellAddonHelper.ReadDisplayedItemText(sellAddon);
        if (addonPtr != lastSellAddonPtr
            || !string.Equals(rawAddonLabel, lastAddonRawLabel, StringComparison.Ordinal)
            || !cachedSession.HasValue)
        {
            if (!TryResolveActiveSellItem(sellAddon, rawAddonLabel, out var resolved))
            {
                return;
            }

            cachedSession = resolved;
            lastAddonRawLabel = rawAddonLabel;
        }

        var session = cachedSession.Value;
        var liveQty = sellAddon->Quantity != null && sellAddon->Quantity->Value > 0
            ? (int)sellAddon->Quantity->Value
            : session.Quantity;

        if (addonPtr != lastSellAddonPtr
            || session.RawItemId != lastTrackedRawItemId
            || session.InvType != lastTrackedInvType
            || session.InvSlot != lastTrackedSlot)
        {
            lastSellAddonPtr = addonPtr;
            lastTrackedRawItemId = session.RawItemId;
            lastTrackedInvType = session.InvType;
            lastTrackedSlot = session.InvSlot;
            hasCapturedInitialPrice = sellAddon->AskingPrice != null;
            initialAskingPrice = hasCapturedInitialPrice ? sellAddon->AskingPrice->Value : 0;
            didInitialUniversalisFill = false;
            userEditedManually = false;
            wasQueueBusyInSession = transactionQueue.IsBusy;
            lastWrittenAskingPrice = 0;
            lastAppliedLiveSearchItemId = 0;
            lastAppliedLiveSearchCheapest = 0;
            sessionLiveStatus = LiveSearchStatus.NotLoaded;
            sessionLiveCheapest = 0;
            sessionOwnCheapest = 0;
            sessionLiveListingCount = 0;
            sessionLiveExcludedOwn = false;
            var startProxy = InfoProxyItemSearch.Instance();
            waitForFreshSameItemSearch = NativeUiHelpers.IsAddonOpenAndReady(gameGui, "ItemSearchResult")
                && startProxy != null
                && !startProxy->WaitingForListings
                && ItemIdentity.NormalizeBaseItemId(startProxy->SearchItemId) == session.BaseId;
        }

        if (transactionQueue.IsBusy)
        {
            wasQueueBusyInSession = true;
            if (sellAddon->AskingPrice != null && sellAddon->AskingPrice->Value > 0)
            {
                lastWrittenAskingPrice = sellAddon->AskingPrice->Value;
            }
        }
        else if (wasQueueBusyInSession)
        {
            wasQueueBusyInSession = false;
            didInitialUniversalisFill = true;
            if (sellAddon->AskingPrice != null && sellAddon->AskingPrice->Value > 0)
            {
                lastWrittenAskingPrice = sellAddon->AskingPrice->Value;
            }
        }

        RefreshOwnRetainerNamesIfNeeded();

        if (!transactionQueue.IsBusy && sellAddon->AskingPrice != null)
        {
            var currentVal = sellAddon->AskingPrice->Value;
            if (lastWrittenAskingPrice > 0)
            {
                if (currentVal != lastWrittenAskingPrice)
                {
                    userEditedManually = true;
                }
            }
            else if (hasCapturedInitialPrice && currentVal != initialAskingPrice)
            {
                userEditedManually = true;
            }
        }

        var searchResultOpen = NativeUiHelpers.IsAddonOpenAndReady(gameGui, "ItemSearchResult");
        if (!searchResultOpen)
        {
            waitForFreshSameItemSearch = false;
        }
        else
        {
            if (waitForFreshSameItemSearch)
            {
                var proxy = InfoProxyItemSearch.Instance();
                if (proxy != null
                    && (proxy->WaitingForListings
                        || ItemIdentity.NormalizeBaseItemId(proxy->SearchItemId) != session.BaseId))
                {
                    waitForFreshSameItemSearch = false;
                }
            }

            if (!waitForFreshSameItemSearch)
            {
                var status = ReadLiveServerState(
                    session.BaseId,
                    session.IsHq,
                    out var freshLiveCheapest,
                    out var freshOwnCheapest,
                    out var freshLiveCount,
                    out var freshLiveExcludedOwn);
                if (status != LiveSearchStatus.NotLoaded)
                {
                    sessionLiveStatus = status;
                    sessionLiveCheapest = freshLiveCheapest;
                    sessionOwnCheapest = freshOwnCheapest;
                    sessionLiveListingCount = freshLiveCount;
                    sessionLiveExcludedOwn = freshLiveExcludedOwn;
                }
            }
        }

        var hasLiveCompetitors = sessionLiveStatus == LiveSearchStatus.CompetitorsFound && sessionLiveCheapest > 0;
        var liveCheapest = sessionLiveCheapest;
        var liveListingCount = sessionLiveListingCount;
        var liveExcludedOwn = sessionLiveExcludedOwn;
        var liveUndercut = hasLiveCompetitors
            ? MarketViabilityEvaluator.ComputeDirectUndercut(liveCheapest, session.ItemRow.PriceLow, configuration.MarketUndercutGil)
            : 0L;

        var world = triageEngine.ActiveWorldName;
        MarketPriceSnapshot? mkt = null;
        var hasFresh = false;
        if (!string.IsNullOrWhiteSpace(world))
        {
            if (universalis.TryGetFreshPrice(world, session.BaseId, out var fresh))
            {
                mkt = fresh;
                hasFresh = true;
            }
            else
            {
                universalis.QueueMissing(world, [session.BaseId]);
                if (universalis.TryGetAnyCachedPrice(world, session.BaseId, out var stale))
                {
                    mkt = stale;
                }
            }
        }

        var viability = MarketViabilityEvaluator.Evaluate(mkt, session.IsHq, liveQty, session.ItemRow.PriceLow, configuration);
        var isLiveBaitDump = hasLiveCompetitors
            && MarketViabilityEvaluator.IsBaitDumpPrice(liveCheapest, viability.SoldMedian);

        var askingHasFocus = IsAskingPriceFocused(sellAddon);
        if (!transactionQueue.IsBusy
            && !userEditedManually
            && !askingHasFocus
            && configuration.MarketAutoFillRetainerSell
            && sellAddon->AskingPrice != null)
        {
            if (hasLiveCompetitors
                && !isLiveBaitDump
                && liveUndercut >= 2
                && (lastAppliedLiveSearchItemId != session.BaseId
                    || lastAppliedLiveSearchCheapest != liveCheapest))
            {
                ApplyAskingPrice(sellAddon, (int)Math.Clamp(liveUndercut, 2L, 999_999_999L));
                lastAppliedLiveSearchItemId = session.BaseId;
                lastAppliedLiveSearchCheapest = liveCheapest;
                didInitialUniversalisFill = true;
            }
            else if (sessionLiveStatus == LiveSearchStatus.OnlyOwnListings
                && sessionOwnCheapest >= 2
                && sellAddon->AskingPrice->Value > 0
                && sellAddon->AskingPrice->Value < sessionOwnCheapest
                && lastAppliedLiveSearchItemId != session.BaseId)
            {
                ApplyAskingPrice(sellAddon, (int)Math.Clamp((long)sessionOwnCheapest, 2L, 999_999_999L));
                lastAppliedLiveSearchItemId = session.BaseId;
                didInitialUniversalisFill = true;
            }
            else if (!didInitialUniversalisFill
                && !session.IsAdjustingExisting
                && sessionLiveStatus != LiveSearchStatus.OnlyOwnListings
                && !isLiveBaitDump
                && !viability.IsBaitDump
                && hasFresh
                && viability.HasData
                && viability.SuggestedUndercutPrice >= 2)
            {
                ApplyAskingPrice(sellAddon, (int)Math.Clamp(viability.SuggestedUndercutPrice, 2L, 999_999_999L));
                didInitialUniversalisFill = true;
            }
        }

        DrawDockedPriceStrip(
            sellAddon,
            session.ItemRow,
            session.IsHq,
            liveQty,
            sessionLiveStatus,
            hasLiveCompetitors,
            isLiveBaitDump,
            liveCheapest,
            sessionOwnCheapest,
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
        uint ownCheapest,
        int liveListingCount,
        bool liveExcludedOwn,
        long liveUndercut,
        MarketPriceSnapshot? mkt,
        bool hasFresh,
        MarketViabilityResult viability)
    {
        var vp = ImGui.GetMainViewport();
        var uiScale = ImGuiHelpers.GlobalScale;
        var scale = sellAddon->Scale > 0.01f ? sellAddon->Scale : 1f;
        var rootW = sellAddon->AtkUnitBase.RootNode != null ? sellAddon->AtkUnitBase.RootNode->Width * scale : 360f * uiScale;
        var rootH = sellAddon->AtkUnitBase.RootNode != null ? sellAddon->AtkUnitBase.RootNode->Height * scale : 240f * uiScale;

        var panelW = Math.Clamp(rootW, 340f * uiScale, 480f * uiScale);
        var panelH = Math.Clamp(lastPanelHeight, 120f * uiScale, 320f * uiScale);
        var x = vp.Pos.X + sellAddon->AtkUnitBase.X;
        var y = vp.Pos.Y + sellAddon->AtkUnitBase.Y + rootH + 4f;
        if (y + panelH > vp.Pos.Y + vp.Size.Y)
        {
            y = Math.Max(vp.Pos.Y + 8f, vp.Pos.Y + sellAddon->AtkUnitBase.Y - panelH - 4f);
        }

        var minX = vp.Pos.X + 8f;
        var maxX = Math.Max(minX, vp.Pos.X + vp.Size.X - panelW - 8f);
        x = Math.Clamp(x, minX, maxX);

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

        ImGui.PushTextWrapPos(panelW - 16f * uiScale);

        var hqTag = isHq ? " HQ" : string.Empty;
        var qtyTag = quantity > 1 ? $" x{quantity}" : string.Empty;
        ImGui.TextColored(ColorAccent, $"Price Check: {itemRow.Name.ExtractText()}{hqTag}{qtyTag}");

        if (hasLiveCompetitors)
        {
            var ownNote = liveExcludedOwn ? " (excl. yours)" : string.Empty;
            ImGui.TextColored(
                isLiveBaitDump ? ColorWarn : ColorGood,
                $"Live Server Cheapest: {liveCheapest:N0}g{ownNote}  ({liveListingCount} listed)");
        }
        else if (liveStatus == LiveSearchStatus.OnlyOwnListings)
        {
            var ownPriceStr = ownCheapest > 0 ? $" (yours: {ownCheapest:N0}g)" : string.Empty;
            ImGui.TextColored(ColorGood, $"Live Server: You already own all listings{ownPriceStr}.");
        }
        else if (liveStatus == LiveSearchStatus.NoHqInTop100)
        {
            var hqMedStr = viability.SoldMedian > 0 ? $"HQ sold median {viability.SoldMedian:N0}g" : "no HQ sales history";
            ImGui.TextColored(ColorAccent, $"Live Server: 0 HQ in top 100 listings ({hqMedStr}).");
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
            ImGui.TextColored(ColorMuted, "Fetching world price history... (click Compare Prices above for live board)");
        }

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

            if (isLiveBaitDump)
            {
                ImGui.TextColored(
                    ColorBad,
                    $"Auto-fill paused: live {liveCheapest:N0}g is <55% of median ({viability.SoldMedian:N0}g).");
            }
        }

        ImGui.PopTextWrapPos();
        ImGui.Spacing();

        var drewButton = false;
        var preferredUndercut = hasLiveCompetitors && liveUndercut >= 2
            ? liveUndercut
            : (liveStatus == LiveSearchStatus.OnlyOwnListings ? 0L : viability.SuggestedUndercutPrice);
        if (preferredUndercut >= 2)
        {
            var undercutLabel = hasLiveCompetitors
                ? $"Set Live -{configuration.MarketUndercutGil} ({preferredUndercut:N0}g)"
                : $"Set Undercut ({preferredUndercut:N0}g)";
            if (ImGui.SmallButton(undercutLabel))
            {
                ApplyAskingPrice(sellAddon, (int)Math.Clamp(preferredUndercut, 2L, 999_999_999L));
                userEditedManually = true;
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
                userEditedManually = true;
            }
        }

        var currentH = ImGui.GetWindowHeight();
        if (currentH > 40f)
        {
            lastPanelHeight = currentH;
        }

        ImGui.End();
    }

    private static bool IsAskingPriceFocused(AddonRetainerSell* sellAddon)
    {
        if (sellAddon == null || sellAddon->AskingPrice == null)
        {
            return false;
        }

        var stage = AtkStage.Instance();
        if (stage == null)
        {
            return false;
        }

        var focus = stage->GetFocus();
        if (focus == null)
        {
            return false;
        }

        var owner = (AtkResNode*)sellAddon->AskingPrice->OwnerNode;
        var cur = focus;
        for (var depth = 0; cur != null && depth < 6; depth++)
        {
            if (cur == owner)
            {
                return true;
            }

            cur = cur->ParentNode;
        }

        return false;
    }

    private void ApplyAskingPrice(AddonRetainerSell* sellAddon, int price)
    {
        if (sellAddon == null || sellAddon->AskingPrice == null || price < 1)
        {
            return;
        }

        sellAddon->AskingPrice->SetValue(price);
        lastWrittenAskingPrice = sellAddon->AskingPrice->Value > 0 ? sellAddon->AskingPrice->Value : price;
    }

    private bool TryResolveActiveSellItem(
        AddonRetainerSell* sellAddon,
        string rawAddonLabel,
        out ResolvedSellSession resolved)
    {
        resolved = default;
        var itemSheet = dataManager.GetExcelSheet<Item>();
        if (itemSheet == null)
        {
            return false;
        }

        var addonHasLabel = !string.IsNullOrWhiteSpace(rawAddonLabel);
        var addonIsHq = addonHasLabel && rawAddonLabel.Contains('\uE03C');
        var addonNormName = addonHasLabel ? NativeUiHelpers.NormalizeItemLabel(rawAddonLabel) : string.Empty;
        var addonQty = sellAddon->Quantity != null && sellAddon->Quantity->Value > 0
            ? (int)sellAddon->Quantity->Value
            : 0;

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
                    var slotIsHq = ItemIdentity.IsHighQuality(slot);
                    var slotName = NativeUiHelpers.NormalizeItemLabel(row.Value.Name.ExtractText());
                    var matchesAddon = !addonHasLabel
                        || (slotIsHq == addonIsHq
                            && string.Equals(slotName, addonNormName, StringComparison.OrdinalIgnoreCase));
                    if (matchesAddon)
                    {
                        resolved = new ResolvedSellSession(
                            candidateType,
                            candidateSlot,
                            slot->ItemId,
                            norm,
                            slotIsHq,
                            addonQty > 0 ? addonQty : Math.Max(1, (int)slot->Quantity),
                            candidateType == InventoryType.RetainerMarket,
                            row.Value);
                        return true;
                    }
                }
            }
        }

        if (!addonHasLabel || string.IsNullOrEmpty(addonNormName) || mgr == null)
        {
            return false;
        }

        ReadOnlySpan<InventoryType> scanOrder =
        [
            InventoryType.RetainerMarket,
            InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4,
            InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3, InventoryType.RetainerPage4,
            InventoryType.RetainerPage5, InventoryType.RetainerPage6, InventoryType.RetainerPage7,
        ];

        foreach (var cType in scanOrder)
        {
            var container = mgr->GetInventoryContainer(cType);
            if (!InventorySafetyEvaluator.IsContainerReadable(container))
            {
                continue;
            }

            ResolvedSellSession? fallbackMatch = null;
            for (var i = 0; i < container->Size; i++)
            {
                var slot = InventorySafetyEvaluator.GetSlot(container, i);
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

                var match = new ResolvedSellSession(
                    cType,
                    i,
                    slot->ItemId,
                    norm,
                    addonIsHq,
                    addonQty > 0 ? addonQty : Math.Max(1, (int)slot->Quantity),
                    cType == InventoryType.RetainerMarket,
                    row.Value);

                if (addonQty <= 0 || slot->Quantity == addonQty)
                {
                    resolved = match;
                    return true;
                }

                fallbackMatch ??= match;
            }

            if (fallbackMatch.HasValue)
            {
                resolved = fallbackMatch.Value;
                return true;
            }
        }

        return false;
    }

    private LiveSearchStatus ReadLiveServerState(
        uint baseId,
        bool isHq,
        out uint cheapestPrice,
        out uint ownCheapestPrice,
        out int matchingListingCount,
        out bool excludedOwnRetainer)
    {
        cheapestPrice = 0;
        ownCheapestPrice = 0;
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

        var hitListingCap = proxy->ListingCount >= proxy->Listings.Length;
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

            var qualityMatches = !isHq || l.IsHqItem;
            if ((myContentId != 0 && l.ContentId == myContentId)
                || (l.RetainerId != 0 && ownRetainerIds.Contains(l.RetainerId)))
            {
                if (qualityMatches)
                {
                    excludedOwnRetainer = true;
                    ownMatchingCount++;
                    if (ownCheapestPrice == 0 || l.UnitPrice < ownCheapestPrice)
                    {
                        ownCheapestPrice = l.UnitPrice;
                    }
                }

                continue;
            }

            anyCompetitorCount++;

            if (qualityMatches)
            {
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
            return hitListingCap ? LiveSearchStatus.NoHqInTop100 : LiveSearchStatus.NoHqListings;
        }

        return LiveSearchStatus.EmptyBoard;
    }

    private void RefreshOwnRetainerNamesIfNeeded()
    {
        var now = DateTime.UtcNow;
        var currentCid = InventorySafetyEvaluator.GetCurrentContentId();
        var activeId = HoldingsService.GetActiveRetainerId();
        if ((now - lastOwnRetainerRefreshUtc).TotalSeconds < 10.0
            && ownRetainerIds.Count > 0
            && currentCid == lastOwnRetainerContentId
            && activeId == lastOwnRetainerActiveId)
        {
            return;
        }

        lastOwnRetainerRefreshUtc = now;
        lastOwnRetainerContentId = currentCid;
        lastOwnRetainerActiveId = activeId;
        var next = new HashSet<ulong>();
        foreach (var r in holdings.GetKnownRetainersForCurrentCharacter())
        {
            if (r.RetainerId != 0)
            {
                next.Add(r.RetainerId);
            }
        }

        if (activeId != 0)
        {
            next.Add(activeId);
        }

        ownRetainerIds = next;
    }

    private void ResetSessionTracking()
    {
        lastSellAddonPtr = 0;
        lastAddonRawLabel = string.Empty;
        cachedSession = null;
        lastTrackedRawItemId = 0;
        lastTrackedSlot = -1;
        initialAskingPrice = 0;
        hasCapturedInitialPrice = false;
        didInitialUniversalisFill = false;
        userEditedManually = false;
        wasQueueBusyInSession = false;
        lastWrittenAskingPrice = 0;
        waitForFreshSameItemSearch = false;
        lastAppliedLiveSearchItemId = 0;
        lastAppliedLiveSearchCheapest = 0;
        sessionLiveStatus = LiveSearchStatus.NotLoaded;
        sessionLiveCheapest = 0;
        sessionOwnCheapest = 0;
        sessionLiveListingCount = 0;
        sessionLiveExcludedOwn = false;
    }
}
