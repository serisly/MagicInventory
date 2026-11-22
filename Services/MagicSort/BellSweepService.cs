using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using MagicInventory.Core;

namespace MagicInventory.Services.MagicSort;

public sealed unsafe class BellSweepService : IDisposable
{
    private const float MovementToleranceSq = 0.04f;
    private const double TransitionTimeoutSeconds = 10.0;
    private const int MaxRetainersPerClick = 4;
    private const int MaxSortBatchesPerRetainer = 1;
    private const int MaxMarketBatchesPerRetainer = 1;

    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly ICondition condition;
    private readonly IDataManager dataManager;
    private readonly IGameGui gameGui;
    private readonly IChatGui chatGui;
    private readonly SafeTransactionQueue transactionQueue;
    private readonly MagicSortService magicSort;

    private readonly string entrustMenuPrefix;
    private readonly string sellInventoryMenuText;
    private readonly string quitMenuText;

    private readonly Queue<BellRetainerTarget> pendingRetainers = new();
    private readonly ButtonStateCache<int> bellSortButtonCache = new(250.0);
    private readonly ButtonStateCache<int> bellSellButtonCache = new(250.0);
    private BellRetainerTarget? activeTarget;
    private SweepState state = SweepState.Idle;
    private DateTime nextActionUtc = DateTime.MinValue;
    private DateTime stateStartedUtc = DateTime.MinValue;
    private Vector3 originPosition;
    private bool didRetainerSortOnCurrent;
    private bool didMarketSortOnCurrent;
    private int currentRetainerSortPasses;
    private int currentMarketSellPasses;
    private bool marketSellOnlyMode;
    private bool triggeredBatch;
    private bool truncatedByCap;

    private sealed record BellRetainerTarget(int SortedIndex, ulong RetainerId, string Name, bool HasFreeMarketSlots, int EstimatedItemCount);

    private enum SweepState
    {
        Idle,
        SelectingRetainer,
        WaitingForRetainerMenu,
        OpeningRetainerInventory,
        WaitingForRetainerInventory,
        RunningRetainerSort,
        ClosingRetainerInventory,
        OpeningMarketSellList,
        WaitingForMarketSellList,
        RunningMarketSort,
        ClosingMarketSellList,
        QuittingRetainer,
        WaitingForRetainerListReturn,
    }

    public BellSweepService(
        IFramework framework,
        IClientState clientState,
        IObjectTable objectTable,
        ICondition condition,
        IDataManager dataManager,
        IGameGui gameGui,
        IChatGui chatGui,
        SafeTransactionQueue transactionQueue,
        MagicSortService magicSort)
    {
        this.framework = framework;
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.condition = condition;
        this.dataManager = dataManager;
        this.gameGui = gameGui;
        this.chatGui = chatGui;
        this.transactionQueue = transactionQueue;
        this.magicSort = magicSort;

        var addonSheet = this.dataManager.GetExcelSheet<Addon>();
        var rawEntrust = addonSheet?.GetRowOrDefault(2378)?.Text.ExtractText() ?? string.Empty;
        var parenIdx = rawEntrust.IndexOf('(');
        var cleanEntrust = parenIdx > 0 ? rawEntrust[..parenIdx].Trim() : rawEntrust.Trim();
        entrustMenuPrefix = string.IsNullOrWhiteSpace(cleanEntrust) ? "Entrust or withdraw items." : cleanEntrust;

        var rawSell = addonSheet?.GetRowOrDefault(2380)?.Text.ExtractText();
        sellInventoryMenuText = string.IsNullOrWhiteSpace(rawSell)
            ? "Sell items in your inventory on the market."
            : rawSell.Trim();

        var rawQuit = addonSheet?.GetRowOrDefault(212)?.Text.ExtractText();
        quitMenuText = string.IsNullOrWhiteSpace(rawQuit) ? "Quit" : rawQuit.Trim();

        this.framework.Update += OnFrameworkUpdate;
        this.transactionQueue.BatchEnded += OnTransactionBatchEnded;
        this.magicSort.CacheInvalidated += InvalidateButtonCaches;
    }

    private void OnTransactionBatchEnded(QueueBatchOutcome outcome, string? reason)
    {
        if (!IsRunning)
        {
            return;
        }

        if (outcome is QueueBatchOutcome.CancelledByUser or QueueBatchOutcome.AbortedOnError)
        {
            Stop(echoReason: null, fromBatchEnded: true);
        }
    }

    public bool IsRunning => state != SweepState.Idle;

    public void InvalidateButtonCaches()
    {
        bellSortButtonCache.Invalidate();
        bellSellButtonCache.Invalidate();
    }

    public MagicSortButtonState GetBellSortButtonState()
    {
        if (IsRunning)
        {
            if (!marketSellOnlyMode)
            {
                var tip = activeTarget != null
                    ? $"Stop the active Magic Sort pass ({activeTarget.Name})."
                    : "Stop the active Magic Sort pass.";
                return new MagicSortButtonState(true, true, "Stop Sort", tip);
            }

            return MagicSortButtonState.Hidden;
        }

        if (transactionQueue.IsBusy)
        {
            return new MagicSortButtonState(true, false, "Wait...", "Wait for the active operation to finish.");
        }

        if (bellSortButtonCache.TryGet(0, out var cached))
        {
            return cached;
        }

        var targets = BuildBellTargets(marketSellOnly: false, out var workByRetainer, out _);
        if (targets.Count == 0)
        {
            return bellSortButtonCache.Set(0, MagicSortButtonState.Hidden);
        }

        var totalItems = 0;
        foreach (var t in targets)
        {
            totalItems += t.EstimatedItemCount;
        }

        var names = targets.ConvertAll(t => t.Name);
        var tooltip = magicSort.BuildBellSortTooltip(names, workByRetainer);
        var displayCount = totalItems > 0 ? totalItems : targets.Count;
        return bellSortButtonCache.Set(0, new MagicSortButtonState(
            true,
            true,
            $"Magic Sort ({displayCount})",
            tooltip));
    }

    public MagicSortButtonState GetBellSellButtonState()
    {
        if (IsRunning)
        {
            if (marketSellOnlyMode)
            {
                var tip = activeTarget != null
                    ? $"Stop the active Market pass ({activeTarget.Name})."
                    : "Stop the active Market pass.";
                return new MagicSortButtonState(true, true, "Stop Sell", tip);
            }

            return MagicSortButtonState.Hidden;
        }

        if (transactionQueue.IsBusy)
        {
            return MagicSortButtonState.Hidden;
        }

        if (!magicSort.HasRemainingAutoSellQuota)
        {
            return new MagicSortButtonState(
                true,
                false,
                "Daily Limit",
                $"Daily Market Board limit reached ({magicSort.GetUsedMarketActionsToday()}/{magicSort.MarketDailyActionLimit}). Raise the limit in Settings or enable Review Mode.");
        }

        if (bellSellButtonCache.TryGet(0, out var cached))
        {
            return cached;
        }

        var targets = BuildBellTargets(marketSellOnly: true, out _, out var bagMarketPlans);
        if (targets.Count == 0)
        {
            return bellSellButtonCache.Set(0, MagicSortButtonState.Hidden);
        }

        var names = targets.ConvertAll(t => t.Name);
        var tooltip = magicSort.BuildBellSellTooltip(names, bagMarketPlans);
        var pendingMarketCount = bagMarketPlans?.Count ?? 0;
        string label;
        if (magicSort.IsMarketReviewOnly)
        {
            label = pendingMarketCount > 1 ? $"Prep Sell (1/{pendingMarketCount})" : "Prep Sell (1)";
        }
        else
        {
            var countSuffix = pendingMarketCount > 0 ? $" ({pendingMarketCount})" : $" ({targets.Count})";
            label = $"Magic Sell{countSuffix}";
        }

        return bellSellButtonCache.Set(0, new MagicSortButtonState(
            true,
            true,
            label,
            tooltip));
    }

    public void ToggleBellSort() => ToggleBellSweep(marketSellOnly: false);

    public void ToggleBellSell() => ToggleBellSweep(marketSellOnly: true);

    public void ToggleBellSweep(bool marketSellOnly)
    {
        if (IsRunning)
        {
            Stop(marketSellOnlyMode ? "Magic Sell stopped." : "Magic Sort stopped.");
            return;
        }

        StartBellSweep(marketSellOnly);
    }

    public void StartBellSweep(bool marketSellOnly = false)
    {
        var player = objectTable.LocalPlayer;
        if (!clientState.IsLoggedIn || player == null || !IsInteractionSafe())
        {
            chatGui.PrintError("Cannot start Magic Sort right now.");
            return;
        }

        if (transactionQueue.IsBusy)
        {
            chatGui.PrintError("Another operation is already in progress.");
            return;
        }

        if (!NativeUiHelpers.IsAddonOpenAndReady(gameGui, "RetainerList"))
        {
            chatGui.PrintError("Open the Summoning Bell first.");
            return;
        }

        if (marketSellOnly && !magicSort.HasRemainingAutoSellQuota)
        {
            chatGui.Print($"Magic Sell: daily limit reached ({magicSort.GetUsedMarketActionsToday()}/{magicSort.MarketDailyActionLimit}). Raise the limit in Settings or enable Review Mode.");
            return;
        }

        var targets = BuildBellTargets(marketSellOnly, out _, out _);
        if (targets.Count == 0)
        {
            chatGui.Print(marketSellOnly
                ? "No retainers have free market slots for pending items."
                : "No retainers need sorting right now.");
            return;
        }

        marketSellOnlyMode = marketSellOnly;
        truncatedByCap = targets.Count > MaxRetainersPerClick;
        InvalidateButtonCaches();
        pendingRetainers.Clear();
        var cappedCount = Math.Min(targets.Count, MaxRetainersPerClick);
        for (var i = 0; i < cappedCount; i++)
        {
            pendingRetainers.Enqueue(targets[i]);
        }

        originPosition = player.Position;
        activeTarget = pendingRetainers.Dequeue();
        ResetCurrentRetainerFlags();
        TransitionTo(SweepState.SelectingRetainer, DateTime.UtcNow, 0);
    }

    public void Stop(string? echoReason = null, bool fromBatchEnded = false)
    {
        var wasRunning = IsRunning;
        InvalidateButtonCaches();
        pendingRetainers.Clear();
        activeTarget = null;
        state = SweepState.Idle;
        marketSellOnlyMode = false;
        truncatedByCap = false;
        nextActionUtc = DateTime.MinValue;
        stateStartedUtc = DateTime.MinValue;
        ResetCurrentRetainerFlags();

        if (wasRunning && !fromBatchEnded && transactionQueue.IsBusy)
        {
            transactionQueue.Cancel(null, QueueBatchOutcome.CancelledByUser);
        }

        if (wasRunning && !string.IsNullOrWhiteSpace(echoReason))
        {
            chatGui.Print(echoReason);
        }
    }

    private void ResetCurrentRetainerFlags()
    {
        didRetainerSortOnCurrent = marketSellOnlyMode;
        didMarketSortOnCurrent = !marketSellOnlyMode;
        currentRetainerSortPasses = 0;
        currentMarketSellPasses = 0;
        triggeredBatch = false;
    }

    private List<BellRetainerTarget> BuildBellTargets(
        bool marketSellOnly,
        out Dictionary<ulong, RetainerBellWorkSummary>? evaluatedWork,
        out List<MarketPlanItem>? evaluatedMarketPlans)
    {
        evaluatedWork = null;
        evaluatedMarketPlans = null;
        var list = new List<BellRetainerTarget>();
        var rm = RetainerManager.Instance();
        if (rm == null || !rm->IsReady)
        {
            return list;
        }

        var count = rm->GetRetainerCount();
        var allRetainers = new List<(int SortedIndex, ulong RetainerId, string Name, bool HasFreeMarket)>();
        for (uint i = 0; i < count; i++)
        {
            var ret = rm->GetRetainerBySortedIndex(i);
            if (ret == null || ret->RetainerId == 0 || !ret->Available)
            {
                continue;
            }

            var name = ret->NameString;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = $"Retainer {i + 1}";
            }

            var hasFreeMarket = ret->MarketItemCount < MarketListingPlanner.MaxRetainerMarketListings;
            allRetainers.Add(((int)i, ret->RetainerId, name, hasFreeMarket));
        }

        if (marketSellOnly)
        {
            evaluatedMarketPlans = magicSort.GetPendingBagMarketPlans();
            var pendingMarket = evaluatedMarketPlans.Count;
            if (pendingMarket <= 0)
            {
                return list;
            }

            foreach (var (sortedIdx, retId, name, hasFreeMarket) in allRetainers)
            {
                if (hasFreeMarket)
                {
                    list.Add(new BellRetainerTarget(sortedIdx, retId, name, true, pendingMarket));
                }
            }

            return list;
        }

        evaluatedWork = magicSort.EvaluateAllBellWork();
        var workByRetainer = evaluatedWork;
        var funnelDestinations = new HashSet<ulong>();
        foreach (var summary in workByRetainer.Values)
        {
            foreach (var destId in summary.FunnelDestinationIds)
            {
                funnelDestinations.Add(destId);
            }
        }

        var withdrawFirst = new List<BellRetainerTarget>();
        var regular = new List<BellRetainerTarget>();

        foreach (var (sortedIdx, retId, name, hasFreeMarket) in allRetainers)
        {
            var hasSummary = workByRetainer.TryGetValue(retId, out var summary);
            var isFunnelDest = funnelDestinations.Contains(retId);
            var itemCount = hasSummary ? summary.TotalItemCount : 0;
            if (!isFunnelDest && itemCount <= 0)
            {
                continue;
            }

            var target = new BellRetainerTarget(sortedIdx, retId, name, hasFreeMarket, itemCount);
            if (hasSummary && summary.HasWithdrawWork && (summary.IsFull || !isFunnelDest))
            {
                withdrawFirst.Add(target);
            }
            else
            {
                regular.Add(target);
            }
        }

        list.AddRange(withdrawFirst);
        list.AddRange(regular);
        return list;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (state == SweepState.Idle)
        {
            return;
        }

        var player = objectTable.LocalPlayer;
        if (!clientState.IsLoggedIn || player == null || !IsInteractionSafe())
        {
            Stop("Magic Sort interrupted.");
            return;
        }

        if (Vector3.DistanceSquared(player.Position, originPosition) > MovementToleranceSq)
        {
            Stop("Magic Sort stopped: movement detected.");
            return;
        }

        var now = DateTime.UtcNow;
        if (now < nextActionUtc)
        {
            return;
        }

        switch (state)
        {
            case SweepState.SelectingRetainer:
                HandleSelectingRetainer(now);
                break;
            case SweepState.WaitingForRetainerMenu:
                HandleWaitingForRetainerMenu(now);
                break;
            case SweepState.OpeningRetainerInventory:
                HandleOpeningRetainerInventory(now);
                break;
            case SweepState.WaitingForRetainerInventory:
                HandleWaitingForRetainerInventory(now);
                break;
            case SweepState.RunningRetainerSort:
                HandleRunningRetainerSort(now);
                break;
            case SweepState.ClosingRetainerInventory:
                HandleClosingRetainerInventory(now);
                break;
            case SweepState.OpeningMarketSellList:
                HandleOpeningMarketSellList(now);
                break;
            case SweepState.WaitingForMarketSellList:
                HandleWaitingForMarketSellList(now);
                break;
            case SweepState.RunningMarketSort:
                HandleRunningMarketSort(now);
                break;
            case SweepState.ClosingMarketSellList:
                HandleClosingMarketSellList(now);
                break;
            case SweepState.QuittingRetainer:
                HandleQuittingRetainer(now);
                break;
            case SweepState.WaitingForRetainerListReturn:
                HandleWaitingForRetainerListReturn(now);
                break;
        }
    }

    private void HandleSelectingRetainer(DateTime now)
    {
        if (activeTarget == null)
        {
            Stop("Magic Sort complete.");
            return;
        }

        var listAddon = NativeUiHelpers.GetReadyAddon(gameGui, "RetainerList");
        if (listAddon == null)
        {
            Stop("Magic Sort stopped: Summoning Bell closed.");
            return;
        }

        var resolvedIndex = activeTarget.SortedIndex;
        var rm = RetainerManager.Instance();
        if (rm != null && rm->IsReady)
        {
            var count = rm->GetRetainerCount();
            resolvedIndex = -1;
            for (uint i = 0; i < count; i++)
            {
                var ret = rm->GetRetainerBySortedIndex(i);
                if (ret != null && ret->Available && ret->RetainerId == activeTarget.RetainerId)
                {
                    resolvedIndex = (int)i;
                    break;
                }
            }

            if (resolvedIndex < 0)
            {
                Stop($"Magic Sort stopped: {activeTarget.Name} is no longer available.");
                return;
            }
        }

        var args = stackalloc AtkValue[4];
        args[0] = NativeUiHelpers.IntValue(2);
        args[1] = NativeUiHelpers.IntValue(resolvedIndex);
        args[2] = NativeUiHelpers.IntValue(0);
        args[3] = NativeUiHelpers.IntValue(0);
        if (!NativeUiHelpers.FireCallback(listAddon, false, new ReadOnlySpan<AtkValue>(args, 4)))
        {
            Stop($"Magic Sort stopped: unable to select {activeTarget.Name}.");
            return;
        }

        TransitionTo(SweepState.WaitingForRetainerMenu, now, SampleJitterMs(850, 1450));
    }

    private void HandleWaitingForRetainerMenu(DateTime now)
    {
        if (TryAdvanceTalkDialog())
        {
            nextActionUtc = now.AddMilliseconds(SampleJitterMs(650, 1050));
            return;
        }

        if (NativeUiHelpers.IsAddonOpenAndReady(gameGui, "SelectString"))
        {
            var openedId = HoldingsService.GetActiveRetainerId();
            if (activeTarget != null && openedId != 0 && openedId != activeTarget.RetainerId)
            {
                Stop($"Magic Sort stopped: expected {activeTarget.Name} but a different retainer opened.");
                return;
            }
            if (!didRetainerSortOnCurrent)
            {
                TransitionTo(SweepState.OpeningRetainerInventory, now, SampleJitterMs(550, 950));
            }
            else if (!didMarketSortOnCurrent && activeTarget is { HasFreeMarketSlots: true } && magicSort.HasPendingBagMarketItems())
            {
                TransitionTo(SweepState.OpeningMarketSellList, now, SampleJitterMs(550, 950));
            }
            else
            {
                TransitionTo(SweepState.QuittingRetainer, now, SampleJitterMs(550, 950));
            }

            return;
        }

        if ((now - stateStartedUtc).TotalSeconds > TransitionTimeoutSeconds)
        {
            Stop("Magic Sort timed out waiting for retainer menu.");
            return;
        }

        nextActionUtc = now.AddMilliseconds(350);
    }

    private void HandleOpeningRetainerInventory(DateTime now)
    {
        if (!TryClickSelectStringEntry(text => text.StartsWith(entrustMenuPrefix, StringComparison.OrdinalIgnoreCase)))
        {
            Stop("Magic Sort stopped: retainer inventory menu unavailable.");
            return;
        }

        TransitionTo(SweepState.WaitingForRetainerInventory, now, SampleJitterMs(850, 1350));
    }

    private void HandleWaitingForRetainerInventory(DateTime now)
    {
        if (GetOpenRetainerInventoryAddon() != null)
        {
            triggeredBatch = false;
            TransitionTo(SweepState.RunningRetainerSort, now, SampleJitterMs(550, 950));
            return;
        }

        if ((now - stateStartedUtc).TotalSeconds > TransitionTimeoutSeconds)
        {
            Stop("Magic Sort timed out opening retainer inventory.");
            return;
        }

        nextActionUtc = now.AddMilliseconds(350);
    }

    private void HandleRunningRetainerSort(DateTime now)
    {
        if (GetOpenRetainerInventoryAddon() == null)
        {
            Stop("Magic Sort stopped: retainer inventory closed.");
            return;
        }

        if (transactionQueue.IsBusy)
        {
            nextActionUtc = now.AddMilliseconds(350);
            return;
        }

        if (!triggeredBatch)
        {
            var hasMoreWork = magicSort.HasMoreRetainerSortWork();
            if (hasMoreWork && currentRetainerSortPasses < MaxSortBatchesPerRetainer)
            {
                if (magicSort.ExecuteAction(StationActionId.RetainerSort, fromOrchestrator: true))
                {
                    currentRetainerSortPasses++;
                    triggeredBatch = true;
                    nextActionUtc = now.AddMilliseconds(500);
                    return;
                }

                Stop("Magic Sort stopped: unable to enqueue retainer transfer batch.");
                return;
            }

            if (hasMoreWork)
            {
                truncatedByCap = true;
            }

            magicSort.CaptureActiveRetainerSnapshot(force: true);
            didRetainerSortOnCurrent = true;
            TransitionTo(SweepState.ClosingRetainerInventory, now, SampleJitterMs(700, 1150));
            return;
        }

        if (transactionQueue.LastOutcome is QueueBatchOutcome.CancelledByUser or QueueBatchOutcome.AbortedOnError)
        {
            Stop();
            return;
        }

        magicSort.InvalidateButtonCache();
        if (currentRetainerSortPasses >= MaxSortBatchesPerRetainer)
        {
            if (magicSort.HasMoreRetainerSortWork())
            {
                truncatedByCap = true;
            }

            magicSort.CaptureActiveRetainerSnapshot(force: true);
            didRetainerSortOnCurrent = true;
            TransitionTo(SweepState.ClosingRetainerInventory, now, SampleJitterMs(700, 1150));
            return;
        }

        triggeredBatch = false;
        nextActionUtc = now.AddMilliseconds(SampleJitterMs(650, 1050));
    }

    private void HandleClosingRetainerInventory(DateTime now)
    {
        var retAddon = GetOpenRetainerInventoryAddon();
        if (retAddon != null)
        {
            if ((now - stateStartedUtc).TotalSeconds > TransitionTimeoutSeconds)
            {
                Stop("Magic Sort stopped: timed out closing retainer inventory.");
                return;
            }

            retAddon->Close(true);
            nextActionUtc = now.AddMilliseconds(SampleJitterMs(750, 1200));
            return;
        }

        TransitionTo(SweepState.WaitingForRetainerMenu, now, SampleJitterMs(650, 1050));
    }

    private void HandleOpeningMarketSellList(DateTime now)
    {
        if (!TryClickSelectStringEntry(text => string.Equals(text, sellInventoryMenuText, StringComparison.OrdinalIgnoreCase)))
        {
            didMarketSortOnCurrent = true;
            TransitionTo(SweepState.QuittingRetainer, now, SampleJitterMs(650, 1050));
            return;
        }

        TransitionTo(SweepState.WaitingForMarketSellList, now, SampleJitterMs(900, 1450));
    }

    private void HandleWaitingForMarketSellList(DateTime now)
    {
        if (NativeUiHelpers.IsAddonOpenAndReady(gameGui, "RetainerSellList"))
        {
            triggeredBatch = false;
            TransitionTo(SweepState.RunningMarketSort, now, SampleJitterMs(600, 1000));
            return;
        }

        if ((now - stateStartedUtc).TotalSeconds > TransitionTimeoutSeconds)
        {
            Stop("Magic Sort timed out opening market listing window.");
            return;
        }

        nextActionUtc = now.AddMilliseconds(350);
    }

    private void HandleRunningMarketSort(DateTime now)
    {
        if (!NativeUiHelpers.IsAddonOpenAndReady(gameGui, "RetainerSellList")
            && !NativeUiHelpers.IsAddonOpenAndReady(gameGui, "RetainerSell"))
        {
            Stop("Magic Sort stopped: market listing window closed.");
            return;
        }

        if (transactionQueue.IsBusy)
        {
            nextActionUtc = now.AddMilliseconds(400);
            return;
        }

        if (!triggeredBatch)
        {
            var hasMoreWork = magicSort.HasMoreMarketSellWork();
            if (hasMoreWork && currentMarketSellPasses < MaxMarketBatchesPerRetainer)
            {
                if (magicSort.ExecuteAction(StationActionId.MarketSell, fromOrchestrator: true))
                {
                    currentMarketSellPasses++;
                    triggeredBatch = true;
                    nextActionUtc = now.AddMilliseconds(500);
                    return;
                }

                Stop("Magic Sell stopped: unable to enqueue market listing batch.");
                return;
            }

            if (hasMoreWork)
            {
                truncatedByCap = true;
            }

            didMarketSortOnCurrent = true;
            TransitionTo(SweepState.ClosingMarketSellList, now, SampleJitterMs(700, 1150));
            return;
        }

        if (transactionQueue.LastOutcome is QueueBatchOutcome.CancelledByUser or QueueBatchOutcome.AbortedOnError)
        {
            Stop();
            return;
        }

        if (magicSort.IsMarketReviewOnly)
        {
            magicSort.InvalidateButtonCache();
            Stop("Prepared 1 listing for review. Inspect price and click Confirm, then click Prep Sell for the next item.");
            return;
        }

        if (magicSort.IsMarketOnePerClick)
        {
            magicSort.InvalidateButtonCache();
            Stop(magicSort.HasMoreMarketSellWork()
                ? "Listed 1 item. Click Magic Sell again for the next one."
                : "Listed 1 item.");
            return;
        }

        magicSort.InvalidateButtonCache();
        if (currentMarketSellPasses >= MaxMarketBatchesPerRetainer)
        {
            if (magicSort.HasMoreMarketSellWork())
            {
                truncatedByCap = true;
            }

            didMarketSortOnCurrent = true;
            TransitionTo(SweepState.ClosingMarketSellList, now, SampleJitterMs(700, 1150));
            return;
        }

        triggeredBatch = false;
        nextActionUtc = now.AddMilliseconds(SampleJitterMs(700, 1150));
    }

    private void HandleClosingMarketSellList(DateTime now)
    {
        var sellList = NativeUiHelpers.GetReadyAddon(gameGui, "RetainerSellList");
        if (sellList != null)
        {
            if ((now - stateStartedUtc).TotalSeconds > TransitionTimeoutSeconds)
            {
                Stop("Magic Sell stopped: timed out closing market listing window.");
                return;
            }

            sellList->Close(true);
            nextActionUtc = now.AddMilliseconds(SampleJitterMs(750, 1200));
            return;
        }

        TransitionTo(SweepState.WaitingForRetainerMenu, now, SampleJitterMs(650, 1050));
    }

    private void HandleQuittingRetainer(DateTime now)
    {
        if (TryAdvanceTalkDialog())
        {
            nextActionUtc = now.AddMilliseconds(SampleJitterMs(650, 1050));
            return;
        }

        if (NativeUiHelpers.IsAddonOpenAndReady(gameGui, "RetainerList"))
        {
            AdvanceToNextRetainerOrFinish(now);
            return;
        }

        if (TryClickSelectStringEntry(text =>
                string.Equals(text.TrimEnd('.'), quitMenuText.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)))
        {
            TransitionTo(SweepState.WaitingForRetainerListReturn, now, SampleJitterMs(900, 1450));
            return;
        }

        if ((now - stateStartedUtc).TotalSeconds > TransitionTimeoutSeconds)
        {
            Stop("Magic Sort stopped: could not exit retainer menu.");
            return;
        }

        nextActionUtc = now.AddMilliseconds(350);
    }

    private void HandleWaitingForRetainerListReturn(DateTime now)
    {
        if (TryAdvanceTalkDialog())
        {
            nextActionUtc = now.AddMilliseconds(SampleJitterMs(650, 1050));
            return;
        }

        if (NativeUiHelpers.IsAddonOpenAndReady(gameGui, "RetainerList"))
        {
            AdvanceToNextRetainerOrFinish(now);
            return;
        }

        if ((now - stateStartedUtc).TotalSeconds > TransitionTimeoutSeconds)
        {
            Stop("Magic Sort timed out returning to Retainer List.");
            return;
        }

        nextActionUtc = now.AddMilliseconds(350);
    }

    private void AdvanceToNextRetainerOrFinish(DateTime now)
    {
        if (marketSellOnlyMode)
        {
            var remainingMarket = magicSort.CountPendingBagMarketItems();
            if (remainingMarket <= 0 || !magicSort.HasRemainingAutoSellQuota)
            {
                Stop(magicSort.HasRemainingAutoSellQuota
                    ? "Market Sell complete."
                    : "Market Sell stopped: daily limit reached.");
                return;
            }

            if (pendingRetainers.Count == 0)
            {
                Stop(truncatedByCap
                    ? $"Market Sell pass complete ({remainingMarket} item(s) remaining). Click again to continue."
                    : $"Market Sell pass finished ({remainingMarket} item(s) remaining in bags).");
                return;
            }
        }
        else if (pendingRetainers.Count == 0)
        {
            Stop(truncatedByCap
                ? "Magic Sort pass complete. Click Magic Sort again for remaining items."
                : "Magic Sort complete.");
            return;
        }

        activeTarget = pendingRetainers.Dequeue();
        ResetCurrentRetainerFlags();
        TransitionTo(SweepState.SelectingRetainer, now, SampleJitterMs(950, 1500));
    }

    private bool TryClickSelectStringEntry(Func<string, bool> predicate)
    {
        var selectAddon = (AddonSelectString*)NativeUiHelpers.GetReadyAddon(gameGui, "SelectString");
        if (selectAddon == null || selectAddon->PopupMenu.EntryNames == null)
        {
            return false;
        }

        var count = selectAddon->PopupMenu.EntryCount;
        for (var i = 0; i < count; i++)
        {
            var namePtr = selectAddon->PopupMenu.EntryNames[i].Value;
            if (namePtr == null)
            {
                continue;
            }

            var label = MemoryHelper.ReadSeStringNullTerminated((nint)namePtr).TextValue.Trim();
            if (predicate(label))
            {
                var args = stackalloc AtkValue[1];
                args[0] = NativeUiHelpers.IntValue(i);
                return NativeUiHelpers.FireCallback((AtkUnitBase*)selectAddon, true, new ReadOnlySpan<AtkValue>(args, 1));
            }
        }

        return false;
    }

    private bool TryAdvanceTalkDialog()
    {
        var talk = NativeUiHelpers.GetReadyAddon(gameGui, "Talk");
        if (talk == null)
        {
            return false;
        }

        var args = stackalloc AtkValue[1];
        args[0] = NativeUiHelpers.IntValue(0);
        return NativeUiHelpers.FireCallback(talk, true, new ReadOnlySpan<AtkValue>(args, 1));
    }

    private AtkUnitBase* GetOpenRetainerInventoryAddon()
    {
        var large = NativeUiHelpers.GetReadyAddon(gameGui, "InventoryRetainerLarge");
        return large != null ? large : NativeUiHelpers.GetReadyAddon(gameGui, "InventoryRetainer");
    }

    private void TransitionTo(SweepState nextState, DateTime now, int delayMs)
    {
        state = nextState;
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

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        transactionQueue.BatchEnded -= OnTransactionBatchEnded;
        magicSort.CacheInvalidated -= InvalidateButtonCaches;
        pendingRetainers.Clear();
        activeTarget = null;
        state = SweepState.Idle;
    }
}
