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

        var unowned = missing.Where(l => !l.IsCoveredByHoldings).ToList();
        if (unowned.Count == 0)
        {
            return new ContextButtonState(
                true,
                false,
                $"On Retainer ({missing.Count})",
                $"Remaining materials are already stored on your retainers: {string.Join(", ", missing.Select(l => l.Name))}.");
        }

        if (NativeUiHelpers.IsAddonOpenAndReady(gameGui, "ItemSearchResult"))
        {
            return new ContextButtonState(
                true,
                false,
                $"Magic Buy ({unowned.Count})",
                "Close the open listings popup (or use Magic Buy inside it).");
        }

        var next = FindNextUnownedMissing(DateTime.UtcNow);
        if (next == null)
        {
            // All remaining unowned items were recently skipped (e.g. out of stock on Home World); allow re-checking on click
            return new ContextButtonState(
                true,
                true,
                $"Magic Buy ({unowned.Count})",
                BuildPredictedBuyTooltip(unowned));
        }

        return new ContextButtonState(
            true,
            true,
            $"Magic Buy ({unowned.Count})",
            $"1 click = 1 item search & safe buy (next: {next.Name} x{next.MissingCount}).\n{BuildPredictedBuyTooltip(unowned)}");
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
        if (!NativeUiHelpers.IsAddonOpenAndReady(gameGui, "ItemSearchResult"))
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

        var baseItemId = HoldingsService.NormalizeBaseItemId(infoProxy->SearchItemId);
        var missingNeeded = GetTrackedMissingCount(baseItemId);
        if (missingNeeded <= 0)
        {
            return new ContextButtonState(false, false, "Magic Buy", string.Empty);
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

        var infoProxy = InfoProxyItemSearch.Instance();
        if (infoProxy == null || infoProxy->WaitingForListings || infoProxy->SearchItemId == 0)
        {
            return;
        }

        var baseItemId = HoldingsService.NormalizeBaseItemId(infoProxy->SearchItemId);
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

            TriggerDataCenterPriceHintAsync(baseItemId, itemName);
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

                        if (!DoesPurchaseConfirmMatch(yesNo, captured.ItemName, captured.TotalCost))
                        {
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
            && HoldingsService.NormalizeBaseItemId(proxy->SearchItemId) == activeTarget.ItemId)
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
            TriggerDataCenterPriceHintAsync(activeTarget.ItemId, activeTarget.Name);
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

                if (!DoesPurchaseConfirmMatch(yesNo, activeCandidate.ItemName, activeCandidate.TotalCost))
                {
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
            purchasedCount++;
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

    private TrackedMaterialLine? FindNextUnownedMissing(DateTime now)
    {
        var snapshot = tracker.GetSnapshot();
        foreach (var line in snapshot.Lines)
        {
            if (line.IsComplete || line.IsCoveredByHoldings)
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

    private string BuildPredictedBuyTooltip(IReadOnlyList<TrackedMaterialLine> unowned)
    {
        var sb = new StringBuilder();
        sb.Append("Buy missing tracked materials:");
        long predictedTotal = 0;
        var unpricedCount = 0;
        var shown = 0;

        foreach (var line in unowned)
        {
            var estUnit = magicSort.GetEstimatedMarketUnitPrice(line.ItemId);
            if (estUnit > 0)
            {
                var estLine = estUnit * line.MissingCount;
                predictedTotal += estLine;
                if (shown < 10)
                {
                    sb.Append($"\n  - {line.Name} x{line.MissingCount} (~{estLine:N0}g)");
                    shown++;
                }
            }
            else
            {
                unpricedCount++;
                if (shown < 10)
                {
                    sb.Append($"\n  - {line.Name} x{line.MissingCount} (live price)");
                    shown++;
                }
            }
        }

        if (unowned.Count > shown)
        {
            sb.Append($"\n  ... and {unowned.Count - shown} more");
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
        var snapshot = tracker.GetSnapshot();
        foreach (var line in snapshot.Lines)
        {
            if (line.ItemId == baseItemId && !line.IsComplete && !line.IsCoveredByHoldings)
            {
                return line.MissingCount;
            }
        }

        return 0;
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

        var itemName = dataManager.GetExcelSheet<Item>()?.GetRowOrDefault(baseItemId)?.Name.ExtractText() ?? $"Item #{baseItemId}";
        var inv = InventoryManager.Instance();
        var currentGil = inv != null ? inv->GetGil() : 0u;

        var validUnitPrices = new List<uint>(count);
        var rawListings = new List<(int Index, uint Qty, uint Unit, uint Total, bool IsHq)>(count);

        for (var i = 0; i < count; i++)
        {
            var l = infoProxy->Listings[i];
            if (l.IsMannequin || l.IsSellingAsSet || l.Quantity == 0 || l.UnitPrice == 0)
            {
                continue;
            }

            if (HoldingsService.NormalizeBaseItemId(l.ItemId) != baseItemId)
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

        validUnitPrices.Sort();
        var medianUnit = validUnitPrices[validUnitPrices.Count / 2];
        var maxGil = EffectiveMaxGilPerPurchase;
        var maxSafeUnit = Math.Max((ulong)(medianUnit * EffectiveOutlierMultiplier), (ulong)medianUnit + MedianSlackGil);

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
            blockReason = "Listings are way above recent prices — skipped.";
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
            return true;
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
                    && HoldingsService.NormalizeBaseItemId(slot->ItemId) == candidate.ItemId
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
        if (HoldingsService.NormalizeBaseItemId(proxy->SearchItemId) != captured.ItemId
            || captured.ListingIndex < 0
            || captured.ListingIndex >= (int)proxy->ListingCount
            || inv->GetGil() < captured.TotalCost)
        {
            return false;
        }

        var listing = proxy->Listings[captured.ListingIndex];
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
                if (HoldingsService.NormalizeBaseItemId(pageIds[i]) == expectedBaseItemId)
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

    private static bool DoesPurchaseConfirmMatch(AddonSelectYesno* yesNo, string expectedName, uint expectedTotalCost)
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
        if (!prompt.Contains(cleanName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var costFormatted = expectedTotalCost.ToString("N0");
        return prompt.Contains(costFormatted, StringComparison.Ordinal)
            || prompt.Contains(expectedTotalCost.ToString(), StringComparison.Ordinal);
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

    private void TriggerDataCenterPriceHintAsync(uint baseItemId, string itemName)
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
                chatGui.Print(
                    $"Magic Inventory ({dcName} hint): {itemName} cheapest on {best.WorldName} at {best.UnitPrice:N0}g (x{best.Quantity}{hqTag}).");

                if (promptTravel && onWorldHopOffer != null)
                {
                    onWorldHopOffer(itemName, currentWorld, dcName, best);
                }
            }).ConfigureAwait(false);
        });
    }
}
