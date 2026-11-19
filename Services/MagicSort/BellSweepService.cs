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

/// <summary>
/// Attended, zero-navigation Summoning Bell orchestrator.
/// Only starts on explicit user click inside an open RetainerList window.
/// Cycles retainers with pending Magic Sort / Market work, delegating actual slot
/// operations to MagicSortService + SafeTransactionQueue with human pacing.
/// </summary>
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

    private sealed record BellRetainerTarget(int SortedIndex, ulong RetainerId, string Name, bool HasFreeMarketSlots);

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
    }

    private void OnTransactionBatchEnded(QueueBatchOutcome outcome, string? reason)
    {
        if (!IsRunning)
        {
            return;
        }

        if (outcome is QueueBatchOutcome.CancelledByUser or QueueBatchOutcome.AbortedOnError)
        {
            Stop();
        }
    }

    public bool IsRunning => state != SweepState.Idle;

    public MagicSortButtonState GetBellSortButtonState()
    {
        if (IsRunning)
        {
            if (!marketSellOnlyMode)
            {
                var label = activeTarget != null ? $"Stop ({activeTarget.Name})" : "Stop";
                return new MagicSortButtonState(true, true, label, "Stop the active Magic Sort pass.");
            }

            return new MagicSortButtonState(false, false, "Magic Sort");
        }

        if (transactionQueue.IsBusy)
        {
            return new MagicSortButtonState(true, false, "Wait...", "Wait for the active operation to finish.");
        }

        var targets = BuildBellTargets(marketSellOnly: false);
        if (targets.Count == 0)
        {
            return new MagicSortButtonState(false, false, "Magic Sort");
        }

        var names = targets.ConvertAll(t => t.Name);
        var tooltip = magicSort.BuildBellSortTooltip(names);
        return new MagicSortButtonState(
            true,
            true,
            $"Magic Sort ({targets.Count})",
            tooltip);
    }

    public MagicSortButtonState GetBellSellButtonState()
    {
        if (IsRunning)
        {
            if (marketSellOnlyMode)
            {
                var label = activeTarget != null ? $"Stop ({activeTarget.Name})" : "Stop";
                return new MagicSortButtonState(true, true, label, "Stop the active Magic Sell pass.");
            }

            return new MagicSortButtonState(false, false, "Magic Sell");
        }

        if (transactionQueue.IsBusy)
        {
            return new MagicSortButtonState(false, false, "Magic Sell");
        }

        var targets = BuildBellTargets(marketSellOnly: true);
        if (targets.Count == 0)
        {
            return new MagicSortButtonState(false, false, "Magic Sell");
        }

        var names = targets.ConvertAll(t => t.Name);
        var tooltip = magicSort.BuildBellSellTooltip(names);
        var pendingMarketCount = magicSort.CountPendingBagMarketItems();
        var countSuffix = pendingMarketCount > 0 ? $" ({pendingMarketCount})" : $" ({targets.Count})";
        return new MagicSortButtonState(
            true,
            true,
            $"Magic Sell{countSuffix}",
            tooltip);
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

        var targets = BuildBellTargets(marketSellOnly);
        if (targets.Count == 0)
        {
            chatGui.Print(marketSellOnly
                ? "No retainers have free market slots for pending items."
                : "No retainers need sorting right now.");
            return;
        }

        marketSellOnlyMode = marketSellOnly;
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

    public void Stop(string? echoReason = null)
    {
        var wasRunning = IsRunning;
        pendingRetainers.Clear();
        activeTarget = null;
        state = SweepState.Idle;
        marketSellOnlyMode = false;
        nextActionUtc = DateTime.MinValue;
        stateStartedUtc = DateTime.MinValue;
        ResetCurrentRetainerFlags();

        if (wasRunning && transactionQueue.IsBusy)
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
        // Strict separation: Sort mode ONLY sorts retainer bags; Sell mode ONLY lists Market items.
        didRetainerSortOnCurrent = marketSellOnlyMode;
        didMarketSortOnCurrent = !marketSellOnlyMode;
        currentRetainerSortPasses = 0;
        currentMarketSellPasses = 0;
        triggeredBatch = false;
    }

    private List<BellRetainerTarget> BuildBellTargets(bool marketSellOnly = false)
    {
        var list = new List<BellRetainerTarget>();
        var rm = RetainerManager.Instance();
        if (rm == null || !rm->IsReady)
        {
            return list;
        }

        var count = rm->GetRetainerCount();
        var withdrawFirst = new List<BellRetainerTarget>();
        var regular = new List<BellRetainerTarget>();
        var funnelDestinations = new HashSet<ulong>();

        // Pre-pass: detect retainers that will withdraw misplaced items to funnel into another retainer
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

            if (!marketSellOnly && magicSort.HasRetainerMisplacedWithdrawWork(ret->RetainerId, out var destIds))
            {
                foreach (var destId in destIds)
                {
                    funnelDestinations.Add(destId);
                }
            }
        }

        foreach (var (sortedIdx, retId, name, hasFreeMarket) in allRetainers)
        {
            var isFunnelSource = !marketSellOnly && magicSort.HasRetainerMisplacedWithdrawWork(retId, out _);
            var isFunnelDest = !marketSellOnly && funnelDestinations.Contains(retId);
            var hasWork = marketSellOnly
                ? hasFreeMarket && magicSort.HasPendingBagMarketItems()
                : isFunnelSource || isFunnelDest || magicSort.HasPotentialBellWorkForRetainer(retId, includeMarketWork: false);
            if (!hasWork)
            {
                continue;
            }

            var target = new BellRetainerTarget(sortedIdx, retId, name, hasFreeMarket);
            if (isFunnelSource && !isFunnelDest)
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

        var args = stackalloc AtkValue[4];
        args[0] = NativeUiHelpers.IntValue(2);
        args[1] = NativeUiHelpers.IntValue(activeTarget.SortedIndex);
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
            if (magicSort.HasMoreRetainerSortWork() && currentRetainerSortPasses < MaxSortBatchesPerRetainer)
            {
                currentRetainerSortPasses++;
                triggeredBatch = true;
                magicSort.ExecuteRetainerSortFromOrchestrator();
                nextActionUtc = now.AddMilliseconds(500);
                return;
            }

            didRetainerSortOnCurrent = true;
            TransitionTo(SweepState.ClosingRetainerInventory, now, SampleJitterMs(700, 1150));
            return;
        }

        if (transactionQueue.LastOutcome is QueueBatchOutcome.CancelledByUser or QueueBatchOutcome.AbortedOnError)
        {
            Stop();
            return;
        }

        // Batch finished; re-check if another 25-step pass remains on this retainer
        triggeredBatch = false;
        magicSort.InvalidateButtonCache();
        nextActionUtc = now.AddMilliseconds(SampleJitterMs(650, 1050));
    }

    private void HandleClosingRetainerInventory(DateTime now)
    {
        var retAddon = GetOpenRetainerInventoryAddon();
        if (retAddon != null)
        {
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
            if (magicSort.HasMoreMarketSellWork() && currentMarketSellPasses < MaxMarketBatchesPerRetainer)
            {
                currentMarketSellPasses++;
                triggeredBatch = true;
                magicSort.ExecuteMarketSortFromOrchestrator();
                nextActionUtc = now.AddMilliseconds(500);
                return;
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

        // Re-check if remaining live slots/items exist after the 8-item pass
        triggeredBatch = false;
        magicSort.InvalidateButtonCache();
        nextActionUtc = now.AddMilliseconds(SampleJitterMs(700, 1150));
    }

    private void HandleClosingMarketSellList(DateTime now)
    {
        var sellList = NativeUiHelpers.GetReadyAddon(gameGui, "RetainerSellList");
        if (sellList != null)
        {
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
        if (pendingRetainers.Count == 0)
        {
            Stop("Magic Sort complete.");
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
        pendingRetainers.Clear();
        activeTarget = null;
        state = SweepState.Idle;
    }
}
