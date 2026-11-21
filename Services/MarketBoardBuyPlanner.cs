using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using MagicInventory.Core;
using MagicInventory.Services.Triage;

namespace MagicInventory.Services;

public sealed record MarketBuyCandidate(
    int ListingIndex,
    uint ItemId,
    string ItemName,
    uint Quantity,
    uint UnitPrice,
    uint TotalCost,
    bool IsHq);

/// <summary>
/// Guarded attended Market Board Magic Buy engine for active Material Tracker items.
/// - Inside ItemSearch (first MB window): 1-click Magic Buy searches and buys each missing
///   unowned tracked material sequentially with human pacing, outlier protection, and hard Gil caps.
/// - Inside ItemSearchResult: 1-click Magic Buy purchases the best safe listing for that item.
/// </summary>
public sealed unsafe class MarketBoardBuyPlanner : IDisposable
{
    private const uint MedianSlackGil = 500;
    private const int MaxAutoBuyItemsPerClick = 1;
    private const int MinSearchCooldownMs = 2500;
    private const int MaxSearchCooldownMs = 3500;
    private const double SkipMemoryMinutes = 2.0;
    private const float MovementToleranceSq = 0.04f;
    private const double TransitionTimeoutSeconds = 10.0;

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly ICondition condition;
    private readonly IDataManager dataManager;
    private readonly IGameGui gameGui;
    private readonly IChatGui chatGui;
    private readonly Configuration configuration;
    private readonly SafeTransactionQueue transactionQueue;
    private readonly MaterialTrackerService tracker;
    private readonly MagicSortService magicSort;
    private readonly UniversalisClient universalis;
    private readonly HoldingsService holdings;
    private readonly Action<string, string, string, DcCheapestListing>? onWorldHopOffer;

    private readonly Dictionary<uint, DateTime> skippedUntilUtcByItem = new();
    private readonly Dictionary<uint, DateTime> lastDcHintUtcByItem = new();
    private AutoBuyState autoState = AutoBuyState.Idle;
    private TrackedMaterialLine? activeTarget;
    private DateTime nextActionUtc = DateTime.MinValue;
    private DateTime stateStartedUtc = DateTime.MinValue;
    private DateTime lastSearchDispatchedUtc = DateTime.MinValue;
    private DateTime resultsReadyAtUtc = DateTime.MinValue;
    private DateTime yesNoReadyAtUtc = DateTime.MinValue;
    private Vector3 originPosition;
    private int purchasedCount;
    private int preBagCount;
    private bool yesNoConfirmed;
    private bool clickedSearchRow;
    private bool isDisposed;
    private MarketBuyCandidate? activeCandidate;
    private uint lastBoughtItemId;
    private int lastBoughtListingIndex = -1;
    private uint lastBoughtUnitPrice;
    private uint lastBoughtQty;
    private DateTime lastBoughtUtc = DateTime.MinValue;
    private HashSet<ulong> ownRetainerIds = new();
    private DateTime lastOwnRetainerRefreshUtc = DateTime.MinValue;

    private uint EffectiveMaxGilPerPurchase => (uint)Math.Max(100, configuration.BuyMaxGilPerPurchase);
    private double EffectiveOutlierMultiplier => Math.Max(1.1, configuration.BuyOutlierMultiplier);

    private enum AutoBuyState
    {
        Idle,
        DispatchingSearch,
        WaitingForListings,
        SelectingListing,
        WaitingForPurchaseAck,
        ClosingResultWindow,
    }

    public MarketBoardBuyPlanner(
        IDalamudPluginInterface pluginInterface,
        IFramework framework,
        IClientState clientState,
        IObjectTable objectTable,
        ICondition condition,
        IDataManager dataManager,
        IGameGui gameGui,
        IChatGui chatGui,
        Configuration configuration,
        SafeTransactionQueue transactionQueue,
        MaterialTrackerService tracker,
        MagicSortService magicSort,
        UniversalisClient universalis,
        HoldingsService holdings,
        Action<string, string, string, DcCheapestListing>? onWorldHopOffer = null)
    {
        this.pluginInterface = pluginInterface;
        this.framework = framework;
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.condition = condition;
        this.dataManager = dataManager;
        this.gameGui = gameGui;
        this.chatGui = chatGui;
        this.configuration = configuration;
        this.transactionQueue = transactionQueue;
        this.tracker = tracker;
        this.magicSort = magicSort;
        this.universalis = universalis;
        this.holdings = holdings;
        this.onWorldHopOffer = onWorldHopOffer;

        this.framework.Update += OnFrameworkUpdate;
        this.chatGui.ChatMessage += OnChatMessage;
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        if (autoState != AutoBuyState.WaitingForPurchaseAck)
        {
            return;
        }

        if (message.LogKind is not (XivChatType.ErrorMessage or XivChatType.SystemError))
        {
            return;
        }

        var text = message.Message.TextValue.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            StopAutoBuy($"Magic Buy stopped: {text}");
        }
    }

    public bool IsAutoBuying => autoState != AutoBuyState.Idle;

    public ContextButtonState GetSearchWindowBuyButtonState()
    {
        if (!NativeUiHelpers.IsAddonOpenAndReady(gameGui, "ItemSearch"))
        {
            return new ContextButtonState(false, false, "Magic Buy", string.Empty);
        }

        if (IsAutoBuying)
        {
            var current = activeTarget != null ? activeTarget.Name : "items";
            return new ContextButtonState(true, true, "Stop Buy", $"Stop buying tracked materials ({current}).");
        }

        var snapshot = tracker.GetSnapshot();
        if (snapshot.Lines.Count == 0)
        {
            return new ContextButtonState(false, false, "Magic Buy", string.Empty);
        }

        if (transactionQueue.IsBusy)
        {
            return new ContextButtonState(true, false, "Wait...", "Wait for the active operation to finish.");
        }

        var missing = snapshot.Lines.Where(l => !l.IsComplete).ToList();
        if (missing.Count == 0)
        {
            return new ContextButtonState(true, false, "All Owned", "All tracked materials are already in your inventory.");
        }

        var unowned = missing.Where(l => !l.IsCoveredByHoldings && l.UnownedMissingCount > 0).ToList();
        if (unowned.Count == 0)
        {
            return new ContextButtonState(
                true,
                false,
                $"On Retainer ({missing.Count})",
                $"Remaining materials are already stored on your retainers: {string.Join(", ", missing.Select(l => l.Name))}.");
        }

        var buyable = new List<TrackedMaterialLine>(unowned.Count);
        var untradable = new List<TrackedMaterialLine>();
        foreach (var line in unowned)
        {
            if (IsItemMarketable(line.ItemId))
            {
                buyable.Add(line);
            }
            else
            {
                untradable.Add(line);
            }
        }

        if (buyable.Count == 0)
        {
            return new ContextButtonState(
                true,
                false,
                $"Untradable ({untradable.Count})",
                $"Remaining missing materials cannot be bought on the Market Board: {string.Join(", ", untradable.Select(l => l.Name))}.");
        }

        if (configuration.GetRemainingMarketActionsToday() <= 0)
        {
            return new ContextButtonState(
                true,
                false,
                "Daily Limit",
                $"Daily Market Board limit reached ({configuration.GetUsedMarketActionsToday()}/{configuration.MarketDailyActionLimit}). Raise the limit in Settings or buy manually.");
        }

        if (NativeUiHelpers.IsAddonOpenAndReady(gameGui, "ItemSearchResult"))
        {
            return new ContextButtonState(
                true,
                false,
                $"Magic Buy ({buyable.Count})",
                "Close the open listings popup (or use Magic Buy inside it).");
        }

        var next = FindNextUnownedMissing(DateTime.UtcNow);
        if (next == null)
        {
            // All remaining unowned items were recently skipped (e.g. out of stock on Home World); allow re-checking on click
            return new ContextButtonState(
                true,
                true,
                $"Magic Buy ({buyable.Count})",
                BuildPredictedBuyTooltip(buyable, untradable));
        }

        return new ContextButtonState(
            true,
            true,
            $"Magic Buy ({buyable.Count})",
            $"1 click = 1 item search & safe buy (next: {next.Name} x{next.UnownedMissingCount}).\n{BuildPredictedBuyTooltip(buyable, untradable)}");
    }

    public void ToggleAutoBuy()
    {
        if (IsAutoBuying)
        {
            StopAutoBuy("Magic Buy stopped.");
            return;
        }

        StartAutoBuy();
    }

    private void StartAutoBuy()
    {
        var player = objectTable.LocalPlayer;
        if (!clientState.IsLoggedIn || player == null || !IsInteractionSafe())
        {
            chatGui.PrintError("Cannot start Magic Buy right now.");
            return;
        }

        if (transactionQueue.IsBusy)
        {
            chatGui.PrintError("Another operation is already in progress.");
            return;
        }

        if (!NativeUiHelpers.IsAddonOpenAndReady(gameGui, "ItemSearch"))
        {
            chatGui.PrintError("Open the Market Board first.");
            return;
        }

        if (configuration.GetRemainingMarketActionsToday() <= 0)
        {
            chatGui.Print($"Magic Buy: daily limit reached ({configuration.MarketDailyActionLimit} Market Board actions). Buy the rest manually or raise the limit in Settings.");
            return;
        }

        var now = DateTime.UtcNow;
        tracker.MarkDirty();
        PruneExpiredSkips(now);
        var next = FindNextUnownedMissing(now);
        if (next == null)
        {
            // If everything was skipped earlier, clear skip memory once and re-check from the top
            skippedUntilUtcByItem.Clear();
            next = FindNextUnownedMissing(now);
            if (next == null)
            {
                chatGui.Print("No unowned tracked materials need buying.");
                return;
            }
        }

        purchasedCount = 0;
        originPosition = player.Position;
        activeTarget = next;
        activeCandidate = null;
        yesNoConfirmed = false;
        var elapsedSinceSearch = (now - lastSearchDispatchedUtc).TotalMilliseconds;
        var initialDelay = elapsedSinceSearch < MinSearchCooldownMs
            ? (int)Math.Ceiling(MinSearchCooldownMs - elapsedSinceSearch)
            : 0;
        TransitionTo(AutoBuyState.DispatchingSearch, now, initialDelay);
    }

    public void StopAutoBuy(string? echoReason = null)
    {
        var wasRunning = IsAutoBuying;
        autoState = AutoBuyState.Idle;
        activeTarget = null;
        activeCandidate = null;
        yesNoConfirmed = false;
        nextActionUtc = DateTime.MinValue;
        stateStartedUtc = DateTime.MinValue;

        if (wasRunning && !string.IsNullOrWhiteSpace(echoReason))
        {
            chatGui.Print(echoReason);
        }
    }

    public ContextButtonState GetBuyButtonState()
    {
        // Only show Magic Buy when browsing the actual Market Board — NEVER inside view-only Retainer Compare Prices!
        if (!NativeUiHelpers.IsAddonOpenAndReady(gameGui, "ItemSearchResult")
            || !NativeUiHelpers.IsAddonOpenAndReady(gameGui, "ItemSearch")
            || NativeUiHelpers.IsAddonOpenAndReady(gameGui, "RetainerSell"))
        {
            return new ContextButtonState(false, false, "Magic Buy", string.Empty);
        }

        if (IsAutoBuying)
        {
            return new ContextButtonState(true, true, "Stop Buy", "Stop the active Magic Buy sequence.");
        }

        if (transactionQueue.IsBusy)
        {
            return new ContextButtonState(true, false, "Buying...", "Completing Market Board purchase...");
        }

        var infoProxy = InfoProxyItemSearch.Instance();
        if (infoProxy == null || infoProxy->WaitingForListings || infoProxy->SearchItemId == 0)
        {
            return new ContextButtonState(false, false, "Magic Buy", string.Empty);
        }

        var baseItemId = ItemIdentity.NormalizeBaseItemId(infoProxy->SearchItemId);
        var missingNeeded = GetTrackedMissingCount(baseItemId);
        if (missingNeeded <= 0)
        {
            return new ContextButtonState(false, false, "Magic Buy", string.Empty);
        }

        if (configuration.GetRemainingMarketActionsToday() <= 0)
        {
            return new ContextButtonState(
                true,
                false,
                "Daily Limit",
                $"Daily Market Board limit reached ({configuration.GetUsedMarketActionsToday()}/{configuration.MarketDailyActionLimit}). Raise the limit in Settings or buy manually.");
        }

        var candidate = PickBestListing(infoProxy, baseItemId, missingNeeded, out var blockReason);
        if (candidate == null)
        {
            return string.IsNullOrEmpty(blockReason)
                ? new ContextButtonState(false, false, "Magic Buy", string.Empty)
                : new ContextButtonState(true, false, "Buy Blocked", blockReason);
        }

        return new ContextButtonState(
            true,
            true,
            $"Magic Buy x{candidate.Quantity} ({candidate.TotalCost:N0}g)",
            $"Buy {candidate.ItemName} x{candidate.Quantity} for {candidate.TotalCost:N0}g ({candidate.UnitPrice:N0}g ea; need {missingNeeded}).");
    }

    public void ExecuteBuyBestListing()
    {
        if (IsAutoBuying)
        {
            StopAutoBuy("Magic Buy stopped.");
            return;
        }

        if (transactionQueue.IsBusy)
        {
            return;
        }

        if (!NativeUiHelpers.IsAddonOpenAndReady(gameGui, "ItemSearch")
            || NativeUiHelpers.IsAddonOpenAndReady(gameGui, "RetainerSell"))
        {
            chatGui.PrintError("Magic Buy is only available at a Market Board (not inside Retainer Compare Prices).");
            return;
        }

        var remainingToday = configuration.GetRemainingMarketActionsToday();
        if (remainingToday <= 0)
        {
            chatGui.Print($"Magic Buy: daily limit reached ({configuration.MarketDailyActionLimit} Market Board actions). Buy the rest manually or raise the limit in Settings.");
            return;
        }

        var infoProxy = InfoProxyItemSearch.Instance();
        if (infoProxy == null || infoProxy->WaitingForListings || infoProxy->SearchItemId == 0)
        {
            return;
        }

        var baseItemId = ItemIdentity.NormalizeBaseItemId(infoProxy->SearchItemId);
        var missingNeeded = GetTrackedMissingCount(baseItemId);
        if (missingNeeded <= 0)
        {
            return;
        }

        var candidate = PickBestListing(infoProxy, baseItemId, missingNeeded, out var blockReason);
        if (candidate == null)
        {
            var itemName = dataManager.GetExcelSheet<Item>()?.GetRowOrDefault(baseItemId)?.Name.ExtractText() ?? $"Item #{baseItemId}";
            if (!string.IsNullOrEmpty(blockReason))
            {
                chatGui.PrintError(blockReason);
            }
            else
            {
                chatGui.Print($"Magic Buy: no listings for {itemName} on your current world.");
            }

            TriggerDataCenterPriceHintAsync(baseItemId, itemName, missingNeeded);
            return;
        }

        var captured = candidate;
        var singlePreBag = 0;
        var singleYesConfirmed = false;
        var singleYesNoReadyAtUtc = DateTime.MinValue;

        var step = new TransactionStep(
            $"Buy {captured.ItemName} x{captured.Quantity}",
            "ItemSearchResult",
            Dispatch: () =>
            {
                var resultAddon = (AddonItemSearchResult*)NativeUiHelpers.GetReadyAddon(gameGui, "ItemSearchResult");
                var proxy = InfoProxyItemSearch.Instance();
                var inv = InventoryManager.Instance();
                if (resultAddon == null || proxy == null || proxy->WaitingForListings || inv == null)
                {
                    return false;
                }

                if (!HasBagSpaceForPurchase(inv, captured))
                {
                    transactionQueue.Cancel("Stopped: inventory is full (140/140).", QueueBatchOutcome.AbortedOnError);
                    return false;
                }

                if (!ValidateListingUnchanged(proxy, inv, captured))
                {
                    transactionQueue.Cancel("Listing changed before purchase.", QueueBatchOutcome.AbortedOnError);
                    return false;
                }

                singlePreBag = HoldingsService.GetLiveBagCount(captured.ItemId);
                singleYesConfirmed = false;
                singleYesNoReadyAtUtc = DateTime.MinValue;

                var args = stackalloc AtkValue[2];
                args[0] = NativeUiHelpers.IntValue(2);
                args[1] = NativeUiHelpers.IntValue(captured.ListingIndex);
                return NativeUiHelpers.FireCallback((AtkUnitBase*)resultAddon, true, new ReadOnlySpan<AtkValue>(args, 2));
            },
            HasServerAck: () =>
            {
                var yesNo = (AddonSelectYesno*)NativeUiHelpers.GetReadyAddon(gameGui, "SelectYesno");
                if (yesNo != null)
                {
                    if (!singleYesConfirmed)
                    {
                        var now = DateTime.UtcNow;
                        if (singleYesNoReadyAtUtc == DateTime.MinValue)
                        {
                            singleYesNoReadyAtUtc = now.AddMilliseconds(SampleJitterMs(380, 680));
                            return false;
                        }

                        if (now < singleYesNoReadyAtUtc)
                        {
                            return false;
                        }

                        if (!DoesPurchaseConfirmMatch(yesNo, captured.ItemId, captured.ItemName, captured.UnitPrice, captured.Quantity, captured.TotalCost))
                        {
                            if (yesNo->NoButton != null)
                            {
                                NativeUiHelpers.ClickAddonButton((AtkUnitBase*)yesNo, yesNo->NoButton);
                            }

                            transactionQueue.Cancel("Purchase confirmation did not match selected listing.", QueueBatchOutcome.AbortedOnError);
                            return false;
                        }

                        if (NativeUiHelpers.ClickAddonButton((AtkUnitBase*)yesNo, yesNo->YesButton))
                        {
                            singleYesConfirmed = true;
                        }
                    }

                    return false;
                }

                if (HoldingsService.GetLiveBagCount(captured.ItemId) > singlePreBag)
                {
                    lastBoughtItemId = captured.ItemId;
                    lastBoughtListingIndex = captured.ListingIndex;
                    lastBoughtUnitPrice = captured.UnitPrice;
                    lastBoughtQty = captured.Quantity;
                    lastBoughtUtc = DateTime.UtcNow;
                    configuration.RecordMarketAction();
                    configuration.Save(pluginInterface);
                    tracker.MarkDirty();
                    return true;
                }

                return false;
            },
            PacingProfile.MarketListing,
            SecondaryRequiredAddon: "SelectYesno",
            TimeoutSeconds: 6.0);

        if (!transactionQueue.TryEnqueueBatch([step], out var reason))
        {
            chatGui.PrintError(reason);
        }
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (autoState == AutoBuyState.Idle)
        {
            return;
        }

        var player = objectTable.LocalPlayer;
        if (!clientState.IsLoggedIn || player == null || !IsInteractionSafe())
        {
            StopAutoBuy("Magic Buy interrupted.");
            return;
        }

        if (Vector3.DistanceSquared(player.Position, originPosition) > MovementToleranceSq)
        {
            StopAutoBuy("Magic Buy stopped: movement detected.");
            return;
        }

        if (!NativeUiHelpers.IsAddonOpenAndReady(gameGui, "ItemSearch")
            && !NativeUiHelpers.IsAddonOpenAndReady(gameGui, "ItemSearchResult")
            && !NativeUiHelpers.IsAddonOpenAndReady(gameGui, "SelectYesno"))
        {
            StopAutoBuy("Magic Buy stopped: Market Board closed.");
            return;
        }

        var now = DateTime.UtcNow;
        if (now < nextActionUtc)
        {
            return;
        }

        switch (autoState)
        {
            case AutoBuyState.DispatchingSearch:
                HandleDispatchingSearch(now);
                break;
            case AutoBuyState.WaitingForListings:
                HandleWaitingForListings(now);
                break;
            case AutoBuyState.SelectingListing:
                HandleSelectingListing(now);
                break;
            case AutoBuyState.WaitingForPurchaseAck:
                HandleWaitingForPurchaseAck(now);
                break;
            case AutoBuyState.ClosingResultWindow:
                HandleClosingResultWindow(now);
                break;
        }
    }

    private void HandleDispatchingSearch(DateTime now)
    {
        if (activeTarget == null)
        {
            StopAutoBuy("Magic Buy complete.");
            return;
        }

        var searchAddon = (AddonItemSearch*)NativeUiHelpers.GetReadyAddon(gameGui, "ItemSearch");
        if (searchAddon == null || searchAddon->SearchTextInput == null)
        {
            StopAutoBuy("Magic Buy stopped: Market Board closed.");
            return;
        }

        var elapsedSinceLastSearch = (now - lastSearchDispatchedUtc).TotalMilliseconds;
        if (elapsedSinceLastSearch < MinSearchCooldownMs)
        {
            nextActionUtc = lastSearchDispatchedUtc.AddMilliseconds(SampleJitterMs(MinSearchCooldownMs, MaxSearchCooldownMs));
            return;
        }

        clickedSearchRow = false;
        resultsReadyAtUtc = DateTime.MinValue;
        lastSearchDispatchedUtc = now;
        searchAddon->SearchTextInput->SetText(activeTarget.Name);
        if (!DispatchItemSearchQuery((AtkUnitBase*)searchAddon, activeTarget.Name))
        {
            searchAddon->RunSearch(false);
        }

        TransitionTo(AutoBuyState.WaitingForListings, now, SampleJitterMs(1200, 1800));
    }

    private static bool DispatchItemSearchQuery(AtkUnitBase* searchAddon, string query)
    {
        if (searchAddon == null || string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        // Exact live-captured native FFXIV ItemSearch text-search callback:
        // update=1 args=[Int(9), Undefined, Undefined, Str(query), Str(query), Undefined, Undefined, Undefined]
        var utf8 = System.Text.Encoding.UTF8.GetBytes(query + "\0");
        fixed (byte* strPtr = utf8)
        {
            var args = stackalloc AtkValue[8];
            for (var i = 0; i < 8; i++)
            {
                args[i] = default;
            }

            args[0] = NativeUiHelpers.IntValue(9);
            args[3].SetString(strPtr);
            args[4].SetString(strPtr);
            return NativeUiHelpers.FireCallback(searchAddon, true, new ReadOnlySpan<AtkValue>(args, 8));
        }
    }

    private void HandleWaitingForListings(DateTime now)
    {
        if (activeTarget == null)
        {
            StopAutoBuy("Magic Buy complete.");
            return;
        }

        var proxy = InfoProxyItemSearch.Instance();
        var resultReady = NativeUiHelpers.IsAddonOpenAndReady(gameGui, "ItemSearchResult");
        if (resultReady
            && proxy != null
            && !proxy->WaitingForListings
            && ItemIdentity.NormalizeBaseItemId(proxy->SearchItemId) == activeTarget.ItemId)
        {
            TransitionTo(AutoBuyState.SelectingListing, now, SampleJitterMs(550, 950));
            return;
        }

        // If exact search returned a list inside ItemSearch without auto-opening ItemSearchResult,
        // wait a natural human beat after results appear, then click the exact matching row ONCE.
        var searchAddon = (AddonItemSearch*)NativeUiHelpers.GetReadyAddon(gameGui, "ItemSearch");
        if (!resultReady
            && !clickedSearchRow
            && searchAddon != null)
        {
            var matchedRow = FindExactItemSearchRow(searchAddon, activeTarget.ItemId, activeTarget.Name);
            if (matchedRow >= 0)
            {
                if (resultsReadyAtUtc == DateTime.MinValue)
                {
                    resultsReadyAtUtc = now.AddMilliseconds(SampleJitterMs(450, 750));
                    nextActionUtc = resultsReadyAtUtc;
                    return;
                }

                if (now < resultsReadyAtUtc)
                {
                    nextActionUtc = resultsReadyAtUtc;
                    return;
                }

                clickedSearchRow = true;
                var args = stackalloc AtkValue[2];
                args[0] = NativeUiHelpers.IntValue(5);
                args[1] = NativeUiHelpers.IntValue(matchedRow);
                // Live-verified native click signature: addon=ItemSearch update=1 args=[Int(5), Int(row)]
                NativeUiHelpers.FireCallback((AtkUnitBase*)searchAddon, true, new ReadOnlySpan<AtkValue>(args, 2));
                nextActionUtc = now.AddMilliseconds(SampleJitterMs(1100, 1650));
                return;
            }
        }

        if ((now - stateStartedUtc).TotalSeconds > TransitionTimeoutSeconds)
        {
            chatGui.Print($"Magic Buy skipped {activeTarget.Name}: listings did not load.");
            TriggerDataCenterPriceHintAsync(activeTarget.ItemId, activeTarget.Name, activeTarget.UnownedMissingCount);
            skippedUntilUtcByItem[activeTarget.ItemId] = now.AddMinutes(SkipMemoryMinutes);
            TransitionTo(AutoBuyState.ClosingResultWindow, now, SampleJitterMs(500, 850));
            return;
        }

        nextActionUtc = now.AddMilliseconds(350);
    }

    private void HandleSelectingListing(DateTime now)
    {
        if (activeTarget == null)
        {
            StopAutoBuy("Magic Buy complete.");
            return;
        }

        var resultAddon = (AddonItemSearchResult*)NativeUiHelpers.GetReadyAddon(gameGui, "ItemSearchResult");
        var proxy = InfoProxyItemSearch.Instance();
        var inv = InventoryManager.Instance();
        if (resultAddon == null || proxy == null || proxy->WaitingForListings || inv == null)
        {
            TransitionTo(AutoBuyState.ClosingResultWindow, now, SampleJitterMs(500, 850));
            return;
        }

        var missing = GetTrackedMissingCount(activeTarget.ItemId);
        if (missing <= 0)
        {
            TransitionTo(AutoBuyState.ClosingResultWindow, now, SampleJitterMs(500, 850));
            return;
        }

        var candidate = PickBestListing(proxy, activeTarget.ItemId, missing, out var blockReason);
        if (candidate == null)
        {
            var reasonText = string.IsNullOrEmpty(blockReason) ? "no safe listings found." : blockReason;
            chatGui.Print($"Magic Buy skipped {activeTarget.Name}: {reasonText}");
            TriggerDataCenterPriceHintAsync(activeTarget.ItemId, activeTarget.Name, missing);
            skippedUntilUtcByItem[activeTarget.ItemId] = now.AddMinutes(SkipMemoryMinutes);
            TransitionTo(AutoBuyState.ClosingResultWindow, now, SampleJitterMs(600, 950));
            return;
        }

        if (!HasBagSpaceForPurchase(inv, candidate))
        {
            StopAutoBuy("Magic Buy stopped: inventory is full (140/140).");
            return;
        }

        activeCandidate = candidate;
        preBagCount = HoldingsService.GetLiveBagCount(candidate.ItemId);
        yesNoConfirmed = false;
        yesNoReadyAtUtc = DateTime.MinValue;

        var args = stackalloc AtkValue[2];
        args[0] = NativeUiHelpers.IntValue(2);
        args[1] = NativeUiHelpers.IntValue(candidate.ListingIndex);
        if (!NativeUiHelpers.FireCallback((AtkUnitBase*)resultAddon, true, new ReadOnlySpan<AtkValue>(args, 2)))
        {
            StopAutoBuy($"Magic Buy stopped: unable to select listing for {candidate.ItemName}.");
            return;
        }

        TransitionTo(AutoBuyState.WaitingForPurchaseAck, now, SampleJitterMs(450, 750));
    }

    private void HandleWaitingForPurchaseAck(DateTime now)
    {
        if (activeCandidate == null)
        {
            TransitionTo(AutoBuyState.ClosingResultWindow, now, SampleJitterMs(500, 850));
            return;
        }

        var yesNo = (AddonSelectYesno*)NativeUiHelpers.GetReadyAddon(gameGui, "SelectYesno");
        if (yesNo != null)
        {
            if (!yesNoConfirmed)
            {
                if (yesNoReadyAtUtc == DateTime.MinValue)
                {
                    yesNoReadyAtUtc = now.AddMilliseconds(SampleJitterMs(380, 680));
                    nextActionUtc = yesNoReadyAtUtc;
                    return;
                }

                if (now < yesNoReadyAtUtc)
                {
                    nextActionUtc = yesNoReadyAtUtc;
                    return;
                }

                if (!DoesPurchaseConfirmMatch(
                        yesNo,
                        activeCandidate.ItemId,
                        activeCandidate.ItemName,
                        activeCandidate.UnitPrice,
                        activeCandidate.Quantity,
                        activeCandidate.TotalCost))
                {
                    if (yesNo->NoButton != null)
                    {
                        NativeUiHelpers.ClickAddonButton((AtkUnitBase*)yesNo, yesNo->NoButton);
                    }

                    StopAutoBuy("Magic Buy stopped: confirmation dialog did not match listing.");
                    return;
                }

                if (NativeUiHelpers.ClickAddonButton((AtkUnitBase*)yesNo, yesNo->YesButton))
                {
                    yesNoConfirmed = true;
                }
            }

            nextActionUtc = now.AddMilliseconds(300);
            return;
        }

        if (HoldingsService.GetLiveBagCount(activeCandidate.ItemId) > preBagCount)
        {
            var boughtName = activeCandidate.ItemName;
            var boughtQty = activeCandidate.Quantity;
            var boughtCost = activeCandidate.TotalCost;
            lastBoughtItemId = activeCandidate.ItemId;
            lastBoughtListingIndex = activeCandidate.ListingIndex;
            lastBoughtUnitPrice = activeCandidate.UnitPrice;
            lastBoughtQty = activeCandidate.Quantity;
            lastBoughtUtc = now;
            purchasedCount++;
            configuration.RecordMarketAction();
            configuration.Save(pluginInterface);
            tracker.MarkDirty();
            activeCandidate = null;

            // Strict 1-item-per-click Market Board rule: close cleanly after 1 purchase; click again for next item.
            if (purchasedCount >= MaxAutoBuyItemsPerClick)
            {
                chatGui.Print($"Magic Buy purchased {boughtName} x{boughtQty} for {boughtCost:N0}g.");
                TransitionTo(AutoBuyState.ClosingResultWindow, now, SampleJitterMs(750, 1200));
                return;
            }

            TransitionTo(AutoBuyState.ClosingResultWindow, now, SampleJitterMs(900, 1450));
            return;
        }

        if ((now - stateStartedUtc).TotalSeconds > TransitionTimeoutSeconds)
        {
            StopAutoBuy($"Magic Buy timed out waiting for {activeCandidate.ItemName} purchase.");
            return;
        }

        nextActionUtc = now.AddMilliseconds(300);
    }

    private void HandleClosingResultWindow(DateTime now)
    {
        var resultAddon = NativeUiHelpers.GetReadyAddon(gameGui, "ItemSearchResult");
        if (resultAddon != null)
        {
            if ((now - stateStartedUtc).TotalSeconds > TransitionTimeoutSeconds)
            {
                StopAutoBuy("Magic Buy finished (could not close listings popup).");
                return;
            }

            resultAddon->Close(true);
            nextActionUtc = now.AddMilliseconds(SampleJitterMs(650, 1050));
            return;
        }

        tracker.MarkDirty();
        var nextRemaining = FindNextUnownedMissing(now);
        if (purchasedCount > 0)
        {
            var nextNote = nextRemaining != null
                ? $" Click Magic Buy again for next item ({nextRemaining.Name})."
                : string.Empty;
            StopAutoBuy($"Magic Buy complete.{nextNote}");
            return;
        }

        // Strict 1-search-per-human-click rule: even if skipped/out-of-stock, stop and let the user click for the next item.
        var skipNextNote = nextRemaining != null
            ? $" Click Magic Buy again to check {nextRemaining.Name}."
            : string.Empty;
        StopAutoBuy($"Magic Buy finished.{skipNextNote}");
    }

    private void PruneExpiredSkips(DateTime now)
    {
        if (skippedUntilUtcByItem.Count == 0)
        {
            return;
        }

        var expired = new List<uint>();
        foreach (var (id, untilUtc) in skippedUntilUtcByItem)
        {
            if (now >= untilUtc)
            {
                expired.Add(id);
            }
        }

        foreach (var id in expired)
        {
            skippedUntilUtcByItem.Remove(id);
        }
    }

    private bool IsItemMarketable(uint baseItemId)
    {
        var row = dataManager.GetExcelSheet<Item>()?.GetRowOrDefault(baseItemId);
        return row is not null && ItemIdentity.IsMarketable(row.Value);
    }

    private TrackedMaterialLine? FindNextUnownedMissing(DateTime now)
    {
        var snapshot = tracker.GetSnapshot();
        foreach (var line in snapshot.Lines)
        {
            if (line.IsComplete || line.IsCoveredByHoldings || line.UnownedMissingCount <= 0)
            {
                continue;
            }

            if (!IsItemMarketable(line.ItemId))
            {
                continue;
            }

            if (skippedUntilUtcByItem.TryGetValue(line.ItemId, out var untilUtc) && now < untilUtc)
            {
                continue;
            }

            return line;
        }

        return null;
    }

    private string BuildPredictedBuyTooltip(
        IReadOnlyList<TrackedMaterialLine> buyable,
        IReadOnlyList<TrackedMaterialLine> untradable)
    {
        var sb = new StringBuilder();
        sb.Append("Buy missing tracked materials:");
        long predictedTotal = 0;
        var unpricedCount = 0;
        var shown = 0;
        var world = objectTable.LocalPlayer?.HomeWorld.ValueNullable?.Name.ExtractText() ?? string.Empty;
        List<uint>? missingUniversalis = null;

        foreach (var line in buyable)
        {
            var neededQty = line.UnownedMissingCount;
            var estUnit = magicSort.GetEstimatedMarketUnitPrice(line.ItemId);
            if (estUnit <= 0 && !string.IsNullOrWhiteSpace(world))
            {
                if (universalis.TryGetAnyCachedPrice(world, line.ItemId, out var snap))
                {
                    estUnit = snap.MinPriceNq > 0
                        ? snap.MinPriceNq
                        : (snap.RecentHistoryMedian > 0 ? snap.RecentHistoryMedian : snap.MinPriceAny);
                }
                else
                {
                    missingUniversalis ??= new List<uint>();
                    missingUniversalis.Add(line.ItemId);
                }
            }

            if (estUnit > 0)
            {
                var estLine = estUnit * neededQty;
                predictedTotal += estLine;
                if (shown < 10)
                {
                    sb.Append($"\n  - {line.Name} x{neededQty} (~{estLine:N0}g)");
                    shown++;
                }
            }
            else
            {
                unpricedCount++;
                if (shown < 10)
                {
                    sb.Append($"\n  - {line.Name} x{neededQty} (live price)");
                    shown++;
                }
            }
        }

        if (missingUniversalis != null && missingUniversalis.Count > 0 && !string.IsNullOrWhiteSpace(world))
        {
            universalis.QueueMissing(world, missingUniversalis);
        }

        if (buyable.Count > shown)
        {
            sb.Append($"\n  ... and {buyable.Count - shown} more");
        }

        if (untradable.Count > 0)
        {
            sb.Append($"\nUntradable (skipped): {string.Join(", ", untradable.Take(4).Select(l => l.Name))}");
            if (untradable.Count > 4)
            {
                sb.Append($" (+{untradable.Count - 4})");
            }
        }

        if (predictedTotal > 0)
        {
            var plusNote = unpricedCount > 0 ? $" (+{unpricedCount} checked live)" : string.Empty;
            sb.Append($"\nPredicted Total: ~{predictedTotal:N0}g{plusNote}");
        }
        else
        {
            sb.Append($"\nSkips overpriced spikes and stays under {EffectiveMaxGilPerPurchase:N0}g per buy.");
        }

        return sb.ToString();
    }

    private int GetTrackedMissingCount(uint baseItemId)
    {
        if (!IsItemMarketable(baseItemId))
        {
            return 0;
        }

        var snapshot = tracker.GetSnapshot();
        foreach (var line in snapshot.Lines)
        {
            if (line.ItemId == baseItemId && !line.IsComplete && !line.IsCoveredByHoldings)
            {
                return line.UnownedMissingCount;
            }
        }

        return 0;
    }

    private void RefreshOwnRetainerIdsIfNeeded()
    {
        var now = DateTime.UtcNow;
        if ((now - lastOwnRetainerRefreshUtc).TotalSeconds < 15.0 && ownRetainerIds.Count > 0)
        {
            return;
        }

        lastOwnRetainerRefreshUtc = now;
        var set = new HashSet<ulong>();
        foreach (var r in holdings.GetKnownRetainersForCurrentCharacter())
        {
            if (r.RetainerId != 0)
            {
                set.Add(r.RetainerId);
            }
        }

        ownRetainerIds = set;
    }

    private MarketBuyCandidate? PickBestListing(
        InfoProxyItemSearch* infoProxy,
        uint baseItemId,
        int missingNeeded,
        out string blockReason)
    {
        blockReason = string.Empty;
        var count = (int)Math.Min(infoProxy->ListingCount, (uint)infoProxy->Listings.Length);
        if (count <= 0)
        {
            return null;
        }

        var itemRow = dataManager.GetExcelSheet<Item>()?.GetRowOrDefault(baseItemId);
        var itemName = itemRow?.Name.ExtractText() ?? $"Item #{baseItemId}";
        var inv = InventoryManager.Instance();
        var currentGil = inv != null ? inv->GetGil() : 0u;
        var myContentId = InventorySafetyEvaluator.GetCurrentContentId();
        RefreshOwnRetainerIdsIfNeeded();

        var now = DateTime.UtcNow;
        var skipJustBought = lastBoughtItemId == baseItemId && (now - lastBoughtUtc).TotalSeconds < 3.0;

        var validUnitPrices = new List<uint>(count);
        var rawListings = new List<(int Index, uint Qty, uint Unit, uint Total, bool IsHq)>(count);

        for (var i = 0; i < count; i++)
        {
            var l = infoProxy->Listings[i];
            if (l.IsMannequin || l.IsSellingAsSet || l.Quantity == 0 || l.UnitPrice == 0)
            {
                continue;
            }

            if (ItemIdentity.NormalizeBaseItemId(l.ItemId) != baseItemId)
            {
                continue;
            }

            // Never buy your own retainer's listings
            if ((myContentId != 0 && l.ContentId == myContentId)
                || (l.RetainerId != 0 && ownRetainerIds.Contains(l.RetainerId)))
            {
                continue;
            }

            // Guard against stale InfoProxyItemSearch right after a partial purchase
            if (skipJustBought
                && i == lastBoughtListingIndex
                && l.UnitPrice == lastBoughtUnitPrice
                && l.Quantity == lastBoughtQty)
            {
                continue;
            }

            ulong total64 = (ulong)l.UnitPrice * l.Quantity + l.TotalTax;
            if (total64 > uint.MaxValue)
            {
                continue;
            }

            validUnitPrices.Add(l.UnitPrice);
            rawListings.Add((i, l.Quantity, l.UnitPrice, (uint)total64, l.IsHqItem));
        }

        if (rawListings.Count == 0)
        {
            return null;
        }

        // Anchor outlier guard to Universalis historical sold median (catches fully scalped boards!),
        // falling back to live-board median only when no historical baseline is cached.
        ulong anchorUnit = 0;
        var world = objectTable.LocalPlayer?.HomeWorld.ValueNullable?.Name.ExtractText() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(world) && universalis.TryGetAnyCachedPrice(world, baseItemId, out var histSnap))
        {
            if (histSnap.RecentHistoryMedian > 0)
            {
                anchorUnit = (ulong)histSnap.RecentHistoryMedian;
            }
            else if (histSnap.MinPriceNq > 0)
            {
                anchorUnit = (ulong)histSnap.MinPriceNq;
            }
        }

        if (anchorUnit == 0)
        {
            validUnitPrices.Sort();
            anchorUnit = validUnitPrices[validUnitPrices.Count / 2];
        }

        var maxGil = EffectiveMaxGilPerPurchase;
        var maxSafeUnit = Math.Max((ulong)(anchorUnit * EffectiveOutlierMultiplier), anchorUnit + MedianSlackGil);

        MarketBuyCandidate? bestCovering = null;
        MarketBuyCandidate? bestPartial = null;
        var sawTooPricey = false;
        var sawOutlier = false;

        foreach (var l in rawListings)
        {
            if (l.Unit > maxSafeUnit)
            {
                sawOutlier = true;
                continue;
            }

            if (l.Total > maxGil || l.Total > currentGil)
            {
                sawTooPricey = true;
                continue;
            }

            var cand = new MarketBuyCandidate(l.Index, baseItemId, itemName, l.Qty, l.Unit, l.Total, l.IsHq);
            if (l.Qty >= (uint)missingNeeded)
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
                if (bestPartial == null
                    || cand.UnitPrice < bestPartial.UnitPrice
                    || (cand.UnitPrice == bestPartial.UnitPrice && cand.Quantity > bestPartial.Quantity))
                {
                    bestPartial = cand;
                }
            }
        }

        if (bestCovering != null)
        {
            // Overbuy guard: avoid buying a huge 99x stack when only a few are needed
            if (bestCovering.Quantity > (uint)missingNeeded)
            {
                var neededIdealCost = (ulong)bestCovering.UnitPrice * (ulong)missingNeeded;
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
            blockReason = $"Listings exceed the {maxGil:N0}g safety cap or your Gil.";
        }
        else if (sawOutlier)
        {
            blockReason = $"Listings exceed recent prices (~{anchorUnit:N0}g median) — skipped.";
        }

        return null;
    }

    private bool HasBagSpaceForPurchase(InventoryManager* inv, MarketBuyCandidate candidate)
    {
        if (inv == null)
        {
            return false;
        }

        if (candidate.ItemId is >= 2 and <= 19)
        {
            var ownedCrystals = inv->GetInventoryItemCount(candidate.ItemId, false, false, false);
            return ownedCrystals + candidate.Quantity <= 9999;
        }

        if (inv->GetEmptySlotsInBag() >= 1)
        {
            return true;
        }

        var row = dataManager.GetExcelSheet<Item>()?.GetRowOrDefault(candidate.ItemId);
        var stackSize = row is not null ? (int)Math.Max(1, row.Value.StackSize) : 1;
        if (stackSize <= 1)
        {
            return false;
        }

        var expectedFlags = candidate.IsHq ? InventoryItem.ItemFlags.HighQuality : InventoryItem.ItemFlags.None;
        foreach (var bag in InventorySafetyEvaluator.PlayerBagContainers)
        {
            var container = inv->GetInventoryContainer(bag);
            if (!InventorySafetyEvaluator.IsContainerReadable(container))
            {
                continue;
            }

            for (var i = 0; i < container->Size; i++)
            {
                var slot = InventorySafetyEvaluator.GetSlot(container, i);
                if (slot != null
                    && ItemIdentity.NormalizeBaseItemId(slot->ItemId) == candidate.ItemId
                    && slot->Flags == expectedFlags
                    && slot->Quantity > 0
                    && slot->Quantity + candidate.Quantity <= stackSize)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool ValidateListingUnchanged(InfoProxyItemSearch* proxy, InventoryManager* inv, MarketBuyCandidate captured)
    {
        var maxCount = Math.Min((int)proxy->ListingCount, proxy->Listings.Length);
        if (ItemIdentity.NormalizeBaseItemId(proxy->SearchItemId) != captured.ItemId
            || captured.ListingIndex < 0
            || captured.ListingIndex >= maxCount
            || inv->GetGil() < captured.TotalCost)
        {
            return false;
        }

        var listing = proxy->Listings[captured.ListingIndex];
        if (listing.IsMannequin
            || listing.IsSellingAsSet
            || ItemIdentity.NormalizeBaseItemId(listing.ItemId) != captured.ItemId
            || listing.IsHqItem != captured.IsHq)
        {
            return false;
        }

        ulong liveTotal = (ulong)listing.UnitPrice * listing.Quantity + listing.TotalTax;
        return listing.Quantity == captured.Quantity
            && listing.UnitPrice == captured.UnitPrice
            && liveTotal == captured.TotalCost;
    }

    private static int FindExactItemSearchRow(AddonItemSearch* searchAddon, uint expectedBaseItemId, string expectedName)
    {
        // Primary: exact ItemId match directly from AgentItemSearch's loaded results page.
        var agent = AgentItemSearch.Instance();
        if (agent != null && agent->ListingPageLoaded && agent->ListingPageItemCount > 0)
        {
            var pageIds = agent->ListingPageItemIds;
            var count = (int)Math.Min(agent->ListingPageItemCount, (uint)pageIds.Length);
            for (var i = 0; i < count; i++)
            {
                if (ItemIdentity.NormalizeBaseItemId(pageIds[i]) == expectedBaseItemId)
                {
                    return i;
                }
            }
        }

        // Fallback: text match on visible list renderers (stripping trailing listing count if present).
        var list = searchAddon->ResultsList;
        if (list == null || list->ListLength <= 0 || list->ItemRendererList == null || list->AllocatedItemRendererListLength <= 0)
        {
            return -1;
        }

        var cleanExpected = NativeUiHelpers.NormalizeItemLabel(expectedName);
        for (var r = 0; r < list->AllocatedItemRendererListLength; r++)
        {
            var renderer = list->ItemRendererList[r].AtkComponentListItemRenderer;
            if (renderer == null || renderer->ListItemIndex < 0 || renderer->ListItemIndex >= list->ListLength)
            {
                continue;
            }

            var btnText = NormalizeSearchRowText(NativeUiHelpers.ReadTextNodeValue(renderer->AtkComponentButton.ButtonTextNode));
            if (string.Equals(btnText, cleanExpected, StringComparison.OrdinalIgnoreCase))
            {
                return renderer->ListItemIndex;
            }

            var uld = renderer->AtkComponentButton.AtkComponentBase.UldManager;
            if (uld.NodeList == null)
            {
                continue;
            }

            for (var i = 0; i < uld.NodeListCount; i++)
            {
                var node = uld.NodeList[i];
                if (node == null || node->Type != NodeType.Text)
                {
                    continue;
                }

                var text = NormalizeSearchRowText(NativeUiHelpers.ReadTextNodeValue(node->GetAsAtkTextNode()));
                if (string.Equals(text, cleanExpected, StringComparison.OrdinalIgnoreCase))
                {
                    return renderer->ListItemIndex;
                }
            }
        }

        return -1;
    }

    private static string NormalizeSearchRowText(string raw)
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

    private bool DoesPurchaseConfirmMatch(
        AddonSelectYesno* yesNo,
        uint baseItemId,
        string expectedName,
        uint unitPrice,
        uint quantity,
        uint expectedTotalCost)
    {
        if (yesNo == null || yesNo->PromptText == null || string.IsNullOrWhiteSpace(expectedName))
        {
            return false;
        }

        var prompt = NativeUiHelpers.ReadTextNodeValue(yesNo->PromptText);
        if (string.IsNullOrEmpty(prompt))
        {
            return false;
        }

        var cleanName = NativeUiHelpers.NormalizeItemLabel(expectedName);
        var nameMatches = prompt.Contains(cleanName, StringComparison.OrdinalIgnoreCase);
        if (!nameMatches && quantity > 1 && baseItemId > 0)
        {
            var plural = dataManager.GetExcelSheet<Item>()?.GetRowOrDefault(baseItemId)?.Plural.ExtractText();
            if (!string.IsNullOrWhiteSpace(plural))
            {
                nameMatches = prompt.Contains(NativeUiHelpers.NormalizeItemLabel(plural), StringComparison.OrdinalIgnoreCase);
            }
        }

        if (!nameMatches)
        {
            return false;
        }

        // Parse complete numeric tokens (supporting EN ',', DE '.', FR spaces/NBSP between digits)
        // so "500" NEVER matches "500,000" or "25,500", and verify quantity when quantity > 1.
        var numbers = ExtractExactPromptNumbers(prompt);
        if (quantity > 1 && !numbers.Contains(quantity))
        {
            return false;
        }

        var subtotal = (long)unitPrice * Math.Max(1u, quantity);
        return numbers.Contains(expectedTotalCost) || numbers.Contains(subtotal);
    }

    private static HashSet<long> ExtractExactPromptNumbers(string prompt)
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

                // Allow thousands separators ONLY when immediately followed by an ASCII digit
                if ((ch is ',' or '.' or ' ' or '\u00A0' or '\u202F')
                    && j + 1 < len
                    && char.IsAsciiDigit(prompt[j + 1]))
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

    private void TransitionTo(AutoBuyState nextState, DateTime now, int delayMs)
    {
        autoState = nextState;
        stateStartedUtc = now;
        nextActionUtc = delayMs > 0 ? now.AddMilliseconds(delayMs) : now;
    }

    private bool IsInteractionSafe()
    {
        return !condition[ConditionFlag.BetweenAreas]
            && !condition[ConditionFlag.BetweenAreas51]
            && !condition[ConditionFlag.InCombat]
            && !condition[ConditionFlag.OccupiedInCutSceneEvent]
            && !condition[ConditionFlag.Unconscious];
    }

    private static int SampleJitterMs(int minMs, int maxMs)
    {
        var a = Random.Shared.Next(minMs, maxMs + 1);
        var b = Random.Shared.Next(minMs, maxMs + 1);
        return (a + b) / 2;
    }

    private void TriggerDataCenterPriceHintAsync(uint baseItemId, string itemName, int missingNeeded)
    {
        var now = DateTime.UtcNow;
        if (lastDcHintUtcByItem.TryGetValue(baseItemId, out var lastHint)
            && (now - lastHint).TotalSeconds < 30.0)
        {
            return;
        }

        var player = objectTable.LocalPlayer;
        var worldRow = player?.CurrentWorld.ValueNullable;
        var currentWorld = worldRow?.Name.ExtractText() ?? string.Empty;
        var dcName = worldRow?.DataCenter.ValueNullable?.Name.ExtractText() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(dcName) || string.IsNullOrWhiteSpace(currentWorld))
        {
            return;
        }

        var inv = InventoryManager.Instance();
        var currentGil = inv != null ? (long)inv->GetGil() : 0L;
        long currentWorldUnit = 0;
        if (universalis.TryGetAnyCachedPrice(currentWorld, baseItemId, out var localSnap))
        {
            currentWorldUnit = localSnap.MinPriceNq > 0 ? localSnap.MinPriceNq : localSnap.MinPriceAny;
        }

        lastDcHintUtcByItem[baseItemId] = now;
        var promptTravel = configuration.EnableLifestreamWorldHopPrompt && IsLifestreamLoaded();
        MarketBoardDcHintHelper.DispatchHint(
            universalis,
            framework,
            chatGui,
            dcName,
            currentWorld,
            baseItemId,
            itemName,
            Math.Max(1, missingNeeded),
            currentWorldUnit,
            EffectiveMaxGilPerPurchase,
            Math.Max(0, configuration.WorldHopMinSavingsGil),
            currentGil,
            promptTravel,
            () => isDisposed,
            onWorldHopOffer);
    }

    private bool IsLifestreamLoaded()
    {
        try
        {
            foreach (var p in pluginInterface.InstalledPlugins)
            {
                if (p.IsLoaded && string.Equals(p.InternalName, "Lifestream", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
            // Ignore plugin list inspection issues.
        }

        return false;
    }

    public void Dispose()
    {
        isDisposed = true;
        framework.Update -= OnFrameworkUpdate;
        chatGui.ChatMessage -= OnChatMessage;
        autoState = AutoBuyState.Idle;
        activeTarget = null;
        activeCandidate = null;
        clickedSearchRow = false;
        skippedUntilUtcByItem.Clear();
        lastDcHintUtcByItem.Clear();
    }
}

internal static class MarketBoardDcHintHelper
{
    public static void DispatchHint(
        UniversalisClient universalis,
        IFramework framework,
        IChatGui chatGui,
        string dcName,
        string currentWorld,
        uint baseItemId,
        string itemName,
        int missingNeeded,
        long currentWorldUnitPrice,
        long maxGilPerPurchase,
        long minSavingsGil,
        long currentGil,
        bool promptTravel,
        Func<bool> isDisposed,
        Action<string, string, string, DcCheapestListing>? onWorldHopOffer)
    {
        _ = Task.Run(async () =>
        {
            var best = await universalis.FetchDataCenterCheapestAsync(dcName, baseItemId, excludeWorld: currentWorld).ConfigureAwait(false);
            if (isDisposed())
            {
                return;
            }

            await framework.RunOnFrameworkThread(() =>
            {
                if (isDisposed())
                {
                    return;
                }

                if (best == null)
                {
                    chatGui.Print($"Magic Inventory: No other listings for {itemName} found across {dcName}.");
                    return;
                }

                var hqTag = best.IsHq ? " HQ" : string.Empty;
                var offerTotal = (long)best.UnitPrice * Math.Max(1, best.Quantity);
                chatGui.Print(
                    $"Magic Inventory ({dcName} hint): {itemName} cheapest on {best.WorldName} at {best.UnitPrice:N0}g ea x{best.Quantity}{hqTag} ({offerTotal:N0}g total).");

                if (promptTravel && onWorldHopOffer != null)
                {
                    var affordable = offerTotal <= maxGilPerPurchase && offerTotal <= currentGil;
                    var savingsOk = currentWorldUnitPrice <= 0
                        || (currentWorldUnitPrice - best.UnitPrice) * Math.Max(1, missingNeeded) >= minSavingsGil;
                    if (affordable && savingsOk)
                    {
                        onWorldHopOffer(itemName, currentWorld, dcName, best);
                    }
                }
            }).ConfigureAwait(false);
        });
    }
}
