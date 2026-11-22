using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
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
    bool IsHq,
    ulong RetainerId = 0);

public readonly record struct MarketListingInput(
    int ListingIndex,
    uint Quantity,
    uint UnitPrice,
    uint TotalCost,
    bool IsHq,
    ulong RetainerId);

public sealed unsafe class MarketBoardBuyPlanner : IDisposable
{
    private const uint MedianSlackGil = 500;
    private const int MinSearchCooldownMs = 2500;
    private const double SkipMemoryMinutes = 2.0;

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly IDataManager dataManager;
    private readonly IGameGui gameGui;
    private readonly IChatGui chatGui;
    private readonly Configuration configuration;
    private readonly SafeTransactionQueue transactionQueue;
    private readonly MaterialTrackerService tracker;
    private readonly MagicSortService magicSort;
    private readonly UniversalisClient universalis;
    private readonly HoldingsService holdings;
    private readonly Action<string, string, string, DcCheapestListing, int>? onWorldHopOffer;

    private readonly Dictionary<uint, DateTime> skippedUntilUtcByItem = new();
    private readonly Dictionary<uint, DateTime> lastDcHintUtcByItem = new();

    private bool isBuyBatchActive;
    private TrackedMaterialLine? activeTarget;
    private DateTime lastSearchDispatchedUtc = DateTime.MinValue;
    private bool isDisposed;

    private uint lastBoughtItemId;
    private ulong lastBoughtRetainerId;
    private uint lastBoughtUnitPrice;
    private uint lastBoughtQty;
    private DateTime lastBoughtUtc = DateTime.MinValue;

    private HashSet<ulong> ownRetainerIds = new();
    private DateTime lastOwnRetainerRefreshUtc = DateTime.MinValue;
    private DateTime lastLifestreamCheckUtc = DateTime.MinValue;
    private bool cachedLifestreamLoaded;

    private uint EffectiveMaxGilPerPurchase => (uint)Math.Max(100, configuration.BuyMaxGilPerPurchase);
    private double EffectiveOutlierMultiplier => Math.Max(1.1, configuration.BuyOutlierMultiplier);

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
        Action<string, string, string, DcCheapestListing, int>? onWorldHopOffer = null)
    {
        _ = condition;
        this.pluginInterface = pluginInterface;
        this.framework = framework;
        this.clientState = clientState;
        this.objectTable = objectTable;
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

        this.transactionQueue.BatchEnded += OnTransactionBatchEnded;
    }

    public bool IsAutoBuying => isBuyBatchActive && transactionQueue.IsExecuting;

    private void OnTransactionBatchEnded(QueueBatchOutcome _, string? __)
    {
        isBuyBatchActive = false;
        activeTarget = null;
    }

    public MagicSortButtonState GetSearchWindowBuyButtonState()
    {
        if (!NativeUiHelpers.IsAddonOpenAndReady(gameGui, "ItemSearch"))
        {
            return MagicSortButtonState.Hidden;
        }

        if (IsAutoBuying)
        {
            var current = activeTarget != null ? activeTarget.Name : "items";
            return new MagicSortButtonState(true, true, "Stop Buy", $"Stop buying tracked materials ({current}).");
        }

        var snapshot = tracker.GetSnapshot();
        if (snapshot.Lines.Count == 0)
        {
            return MagicSortButtonState.Hidden;
        }

        if (transactionQueue.IsBusy)
        {
            return new MagicSortButtonState(true, false, "Wait...", "Wait for the active operation to finish.");
        }

        var missing = snapshot.Lines.Where(l => !l.IsComplete).ToList();
        if (missing.Count == 0)
        {
            return new MagicSortButtonState(true, false, "All Owned", "All tracked materials are already in your inventory.");
        }

        var unowned = missing.Where(l => !l.IsCoveredByHoldings && l.UnownedMissingCount > 0).ToList();
        if (unowned.Count == 0)
        {
            var anyRetainer = false;
            var anySaddlebag = false;
            foreach (var m in missing)
            {
                var summary = holdings.GetHoldingSummary(m.ItemId);
                if (summary.TotalRetainerCount > 0) anyRetainer = true;
                if (summary.SaddlebagCount > 0) anySaddlebag = true;
            }

            var label = anyRetainer && anySaddlebag
                ? $"Stored ({missing.Count})"
                : (anySaddlebag && !anyRetainer ? $"In Saddlebag ({missing.Count})" : $"On Retainer ({missing.Count})");
            var where = anyRetainer && anySaddlebag
                ? "on your retainers / Chocobo Saddlebag"
                : (anySaddlebag && !anyRetainer ? "in your Chocobo Saddlebag" : "on your retainers");
            return new MagicSortButtonState(
                true,
                false,
                label,
                $"Remaining materials are already stored {where}: {string.Join(", ", missing.Select(l => l.Name))}.");
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
            return new MagicSortButtonState(
                true,
                false,
                $"Untradable ({untradable.Count})",
                $"Remaining missing materials cannot be bought on the Market Board: {string.Join(", ", untradable.Select(l => l.Name))}.");
        }

        var cid = InventorySafetyEvaluator.GetCurrentContentId();
        if (configuration.GetRemainingMarketActionsToday(cid) <= 0)
        {
            return new MagicSortButtonState(
                true,
                false,
                "Daily Limit",
                $"Daily Market Board limit reached ({configuration.GetUsedMarketActionsToday(cid)}/{configuration.MarketDailyActionLimit}). Raise the limit in Settings or buy manually.");
        }

        if (NativeUiHelpers.IsAddonOpenAndReady(gameGui, "ItemSearchResult"))
        {
            return new MagicSortButtonState(
                true,
                false,
                $"Magic Buy ({buyable.Count})",
                "Close the open listings popup (or use Magic Buy inside it).");
        }

        var next = FindNextUnownedMissing(DateTime.UtcNow);
        if (next == null)
        {
            return new MagicSortButtonState(
                true,
                true,
                $"Magic Buy ({buyable.Count})",
                BuildPredictedBuyTooltip(buyable, untradable));
        }

        return new MagicSortButtonState(
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

    public void StopAutoBuy(string? echoReason = null)
    {
        var wasRunning = IsAutoBuying;
        isBuyBatchActive = false;
        activeTarget = null;

        if (wasRunning)
        {
            transactionQueue.Cancel(echoReason, QueueBatchOutcome.CancelledByUser);
        }
    }

    private void StartAutoBuy()
    {
        if (!clientState.IsLoggedIn || objectTable.LocalPlayer == null)
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

        var cid = InventorySafetyEvaluator.GetCurrentContentId();
        if (configuration.GetRemainingMarketActionsToday(cid) <= 0)
        {
            chatGui.Print($"Magic Buy: daily limit reached ({configuration.MarketDailyActionLimit} Market Board actions). Buy the rest manually or raise the limit in Settings.");
            return;
        }

        var now = DateTime.UtcNow;
        if ((now - lastSearchDispatchedUtc).TotalMilliseconds < MinSearchCooldownMs)
        {
            chatGui.Print("Magic Buy: waiting for Market Board search cooldown...");
            return;
        }

        tracker.MarkDirty();
        PruneExpiredSkips(now);
        var target = FindNextUnownedMissing(now);
        if (target == null)
        {
            skippedUntilUtcByItem.Clear();
            target = FindNextUnownedMissing(now);
            if (target == null)
            {
                chatGui.Print("No unowned tracked materials need buying.");
                return;
            }
        }

        activeTarget = target;
        var clickedSearchRow = false;
        var rowReadyAtUtc = DateTime.MinValue;
        var pickedCandidate = (MarketBuyCandidate?)null;
        var preBag = 0;
        var yesConfirmed = false;
        var yesReadyAtUtc = DateTime.MinValue;

        var steps = new List<TransactionStep>(3)
        {
            new TransactionStep(
                $"Search {target.Name}",
                "ItemSearch",
                Dispatch: () =>
                {
                    var searchAddon = (AddonItemSearch*)NativeUiHelpers.GetReadyAddon(gameGui, "ItemSearch");
                    if (searchAddon == null || searchAddon->SearchTextInput == null)
                    {
                        return false;
                    }

                    clickedSearchRow = false;
                    rowReadyAtUtc = DateTime.MinValue;
                    lastSearchDispatchedUtc = DateTime.UtcNow;
                    searchAddon->SearchTextInput->SetText(target.Name);
                    if (!DispatchItemSearchQuery((AtkUnitBase*)searchAddon, target.Name))
                    {
                        searchAddon->RunSearch(false);
                    }

                    return true;
                },
                HasServerAck: () =>
                {
                    var proxy = InfoProxyItemSearch.Instance();
                    var resultReady = NativeUiHelpers.IsAddonOpenAndReady(gameGui, "ItemSearchResult");
                    if (resultReady
                        && proxy != null
                        && !proxy->WaitingForListings
                        && ItemIdentity.NormalizeBaseItemId(proxy->SearchItemId) == target.ItemId)
                    {
                        return true;
                    }

                    if (!resultReady && !clickedSearchRow)
                    {
                        var searchAddon = (AddonItemSearch*)NativeUiHelpers.GetReadyAddon(gameGui, "ItemSearch");
                        if (searchAddon != null)
                        {
                            var matchedRow = FindExactItemSearchRow(searchAddon, target.ItemId, target.Name);
                            if (matchedRow >= 0)
                            {
                                var nowTick = DateTime.UtcNow;
                                if (rowReadyAtUtc == DateTime.MinValue)
                                {
                                    rowReadyAtUtc = nowTick.AddMilliseconds(SampleJitterMs(420, 720));
                                    return false;
                                }

                                if (nowTick < rowReadyAtUtc)
                                {
                                    return false;
                                }

                                clickedSearchRow = true;
                                var args = stackalloc AtkValue[2];
                                args[0] = NativeUiHelpers.IntValue(5);
                                args[1] = NativeUiHelpers.IntValue(matchedRow);
                                NativeUiHelpers.FireCallback((AtkUnitBase*)searchAddon, true, new ReadOnlySpan<AtkValue>(args, 2));
                            }
                        }
                    }

                    return false;
                },
                PacingProfile.MarketFormEntry,
                SecondaryRequiredAddon: "ItemSearchResult",
                TimeoutSeconds: 10.0,
                SkipOnFailureCount: 2),

            new TransactionStep(
                $"Buy {target.Name}",
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

                    var missingNeeded = GetTrackedMissingCount(target.ItemId);
                    if (missingNeeded <= 0)
                    {
                        transactionQueue.Cancel($"Already have enough {target.Name}.", QueueBatchOutcome.Succeeded);
                        return false;
                    }

                    var candidate = PickBestListing(proxy, target.ItemId, missingNeeded, out var blockReason);
                    if (candidate == null)
                    {
                        skippedUntilUtcByItem[target.ItemId] = DateTime.UtcNow.AddMinutes(SkipMemoryMinutes);
                        if (!string.IsNullOrEmpty(blockReason))
                        {
                            chatGui.PrintError($"Magic Buy skipped {target.Name}: {blockReason}");
                        }
                        else
                        {
                            chatGui.Print($"Magic Buy skipped {target.Name}: no listings on {ResolveCurrentWorldName()}.");
                        }

                        TriggerDataCenterPriceHintAsync(target.ItemId, target.Name, missingNeeded);
                        ((AtkUnitBase*)resultAddon)->Close(true);
                        transactionQueue.Cancel(null, QueueBatchOutcome.Succeeded);
                        return false;
                    }

                    if (!HasBagSpaceForPurchase(inv, candidate))
                    {
                        transactionQueue.Cancel("Stopped: inventory is full.", QueueBatchOutcome.AbortedOnError);
                        return false;
                    }

                    if (!ValidateListingUnchanged(proxy, inv, candidate))
                    {
                        transactionQueue.Cancel("Listing changed before purchase.", QueueBatchOutcome.AbortedOnError);
                        return false;
                    }

                    pickedCandidate = candidate;
                    preBag = HoldingsService.GetLiveBagCount(candidate.ItemId);
                    yesConfirmed = false;
                    yesReadyAtUtc = DateTime.MinValue;

                    var args = stackalloc AtkValue[2];
                    args[0] = NativeUiHelpers.IntValue(2);
                    args[1] = NativeUiHelpers.IntValue(candidate.ListingIndex);
                    return NativeUiHelpers.FireCallback((AtkUnitBase*)resultAddon, true, new ReadOnlySpan<AtkValue>(args, 2));
                },
                HasServerAck: () =>
                {
                    if (pickedCandidate == null)
                    {
                        return false;
                    }

                    return PollPurchaseConfirmationAndAck(
                        pickedCandidate,
                        preBag,
                        ref yesConfirmed,
                        ref yesReadyAtUtc);
                },
                PacingProfile.MarketListing,
                SecondaryRequiredAddon: "SelectYesno",
                TimeoutSeconds: 6.0,
                SkipOnFailureCount: 1),

            new TransactionStep(
                $"Close {target.Name} listings",
                "ItemSearch",
                Dispatch: () =>
                {
                    var resultAddon = NativeUiHelpers.GetReadyAddon(gameGui, "ItemSearchResult");
                    if (resultAddon != null)
                    {
                        resultAddon->Close(true);
                    }

                    return true;
                },
                HasServerAck: () => !NativeUiHelpers.IsAddonOpenAndReady(gameGui, "ItemSearchResult"),
                PacingProfile.MarketFormEntry,
                SecondaryRequiredAddon: "ItemSearchResult",
                TimeoutSeconds: 3.0),
        };

        isBuyBatchActive = true;
        if (!transactionQueue.TryEnqueueBatch(steps, out var failReason, batchOwner: "MarketBoardBuy", onCompleted: () =>
        {
            isBuyBatchActive = false;
            var bought = pickedCandidate;
            activeTarget = null;
            if (bought != null)
            {
                tracker.MarkDirty();
                var remaining = FindNextUnownedMissing(DateTime.UtcNow);
                chatGui.Print(remaining != null
                    ? $"Magic Buy: purchased {bought.ItemName} x{bought.Quantity} ({bought.TotalCost:N0}g). Click Magic Buy again for next item ({remaining.Name})."
                    : $"Magic Buy: purchased {bought.ItemName} x{bought.Quantity} ({bought.TotalCost:N0}g). All tracked materials covered.");
            }
        }))
        {
            isBuyBatchActive = false;
            activeTarget = null;
            if (!string.IsNullOrWhiteSpace(failReason))
            {
                chatGui.PrintError(failReason);
            }
        }
    }

    public MagicSortButtonState GetBuyButtonState()
    {
        if (!NativeUiHelpers.IsAddonOpenAndReady(gameGui, "ItemSearchResult")
            || !NativeUiHelpers.IsAddonOpenAndReady(gameGui, "ItemSearch")
            || NativeUiHelpers.IsAddonOpenAndReady(gameGui, "RetainerSell"))
        {
            return MagicSortButtonState.Hidden;
        }

        if (IsAutoBuying)
        {
            return new MagicSortButtonState(true, true, "Stop Buy", "Stop the active Magic Buy sequence.");
        }

        if (transactionQueue.IsBusy)
        {
            return new MagicSortButtonState(true, false, "Buying...", "Completing Market Board purchase...");
        }

        var infoProxy = InfoProxyItemSearch.Instance();
        if (infoProxy == null || infoProxy->WaitingForListings || infoProxy->SearchItemId == 0)
        {
            return MagicSortButtonState.Hidden;
        }

        var baseItemId = ItemIdentity.NormalizeBaseItemId(infoProxy->SearchItemId);
        var missingNeeded = GetTrackedMissingCount(baseItemId);
        if (missingNeeded <= 0)
        {
            return MagicSortButtonState.Hidden;
        }

        var cid = InventorySafetyEvaluator.GetCurrentContentId();
        if (configuration.GetRemainingMarketActionsToday(cid) <= 0)
        {
            return new MagicSortButtonState(
                true,
                false,
                "Daily Limit",
                $"Daily Market Board limit reached ({configuration.GetUsedMarketActionsToday(cid)}/{configuration.MarketDailyActionLimit}). Raise the limit in Settings or buy manually.");
        }

        var candidate = PickBestListing(infoProxy, baseItemId, missingNeeded, out var blockReason);
        if (candidate == null)
        {
            return string.IsNullOrEmpty(blockReason)
                ? MagicSortButtonState.Hidden
                : new MagicSortButtonState(true, false, "Buy Blocked", blockReason);
        }

        return new MagicSortButtonState(
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

        var cid = InventorySafetyEvaluator.GetCurrentContentId();
        if (configuration.GetRemainingMarketActionsToday(cid) <= 0)
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
                chatGui.Print($"Magic Buy: no listings for {itemName} on {ResolveCurrentWorldName()}.");
            }

            TriggerDataCenterPriceHintAsync(baseItemId, itemName, missingNeeded);
            return;
        }

        var captured = candidate;
        var preBag = 0;
        var yesConfirmed = false;
        var yesReadyAtUtc = DateTime.MinValue;

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
                    transactionQueue.Cancel("Stopped: inventory is full.", QueueBatchOutcome.AbortedOnError);
                    return false;
                }

                if (!ValidateListingUnchanged(proxy, inv, captured))
                {
                    transactionQueue.Cancel("Listing changed before purchase.", QueueBatchOutcome.AbortedOnError);
                    return false;
                }

                preBag = HoldingsService.GetLiveBagCount(captured.ItemId);
                yesConfirmed = false;
                yesReadyAtUtc = DateTime.MinValue;

                var args = stackalloc AtkValue[2];
                args[0] = NativeUiHelpers.IntValue(2);
                args[1] = NativeUiHelpers.IntValue(captured.ListingIndex);
                return NativeUiHelpers.FireCallback((AtkUnitBase*)resultAddon, true, new ReadOnlySpan<AtkValue>(args, 2));
            },
            HasServerAck: () => PollPurchaseConfirmationAndAck(captured, preBag, ref yesConfirmed, ref yesReadyAtUtc),
            PacingProfile.MarketListing,
            SecondaryRequiredAddon: "SelectYesno",
            TimeoutSeconds: 6.0);

        isBuyBatchActive = true;
        if (!transactionQueue.TryEnqueueBatch([step], out var reason, batchOwner: "MarketBoardBuy", onCompleted: () => isBuyBatchActive = false))
        {
            isBuyBatchActive = false;
            chatGui.PrintError(reason);
        }
    }

    private bool PollPurchaseConfirmationAndAck(
        MarketBuyCandidate captured,
        int preBag,
        ref bool yesConfirmed,
        ref DateTime yesReadyAtUtc)
    {
        var yesNo = (AddonSelectYesno*)NativeUiHelpers.GetReadyAddon(gameGui, "SelectYesno");
        if (yesNo != null)
        {
            if (!yesConfirmed)
            {
                var now = DateTime.UtcNow;
                if (yesReadyAtUtc == DateTime.MinValue)
                {
                    yesReadyAtUtc = now.AddMilliseconds(SampleJitterMs(380, 680));
                    return false;
                }

                if (now < yesReadyAtUtc)
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
                    yesConfirmed = true;
                }
            }

            return false;
        }

        if (HoldingsService.GetLiveBagCount(captured.ItemId) > preBag)
        {
            lastBoughtItemId = captured.ItemId;
            lastBoughtRetainerId = captured.RetainerId;
            lastBoughtUnitPrice = captured.UnitPrice;
            lastBoughtQty = captured.Quantity;
            lastBoughtUtc = DateTime.UtcNow;
            var cid = InventorySafetyEvaluator.GetCurrentContentId();
            configuration.RecordMarketAction(cid);
            configuration.Save(pluginInterface);
            tracker.MarkDirty();
            return true;
        }

        return false;
    }

    private static bool DispatchItemSearchQuery(AtkUnitBase* searchAddon, string query)
    {
        if (searchAddon == null || string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        var utf8 = Encoding.UTF8.GetBytes(query + "\0");
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

    private void PruneExpiredSkips(DateTime now)
    {
        if (skippedUntilUtcByItem.Count == 0)
        {
            return;
        }

        List<uint>? expired = null;
        foreach (var (id, until) in skippedUntilUtcByItem)
        {
            if (now >= until)
            {
                expired ??= new List<uint>();
                expired.Add(id);
            }
        }

        if (expired != null)
        {
            foreach (var id in expired)
            {
                skippedUntilUtcByItem.Remove(id);
            }
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

            if (skippedUntilUtcByItem.TryGetValue(line.ItemId, out var skipUntil) && now < skipUntil)
            {
                continue;
            }

            return line;
        }

        return null;
    }

    private string ResolveCurrentWorldName()
        => objectTable.LocalPlayer?.CurrentWorld.ValueNullable?.Name.ExtractText() ?? string.Empty;

    private string BuildPredictedBuyTooltip(
        IReadOnlyList<TrackedMaterialLine> buyable,
        IReadOnlyList<TrackedMaterialLine> untradable)
    {
        var sb = new StringBuilder();
        sb.Append("Buy missing tracked materials:");
        long predictedTotal = 0;
        var unpricedCount = 0;
        var shown = 0;
        var world = ResolveCurrentWorldName();
        List<uint>? missingUniversalis = null;

        foreach (var line in buyable)
        {
            var neededQty = line.UnownedMissingCount;
            var estUnit = 0L;
            if (!string.IsNullOrWhiteSpace(world))
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

            if (estUnit <= 0)
            {
                estUnit = magicSort.GetEstimatedMarketUnitPrice(line.ItemId);
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
            sb.Append($"\nPredicted Total ({world}): ~{predictedTotal:N0}g{plusNote}");
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
        var skipJustBought = lastBoughtItemId == baseItemId
            && lastBoughtRetainerId != 0
            && (now - lastBoughtUtc).TotalSeconds < 3.0;
        var skippedJustBoughtOnce = false;

        var inputs = new List<MarketListingInput>(count);
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

            if ((myContentId != 0 && l.ContentId == myContentId)
                || (l.RetainerId != 0 && ownRetainerIds.Contains(l.RetainerId)))
            {
                continue;
            }

            if (skipJustBought
                && !skippedJustBoughtOnce
                && l.RetainerId == lastBoughtRetainerId
                && l.UnitPrice == lastBoughtUnitPrice
                && l.Quantity == lastBoughtQty)
            {
                skippedJustBoughtOnce = true;
                continue;
            }

            ulong total64 = (ulong)l.UnitPrice * l.Quantity + l.TotalTax;
            if (total64 > uint.MaxValue)
            {
                continue;
            }

            inputs.Add(new MarketListingInput(i, l.Quantity, l.UnitPrice, (uint)total64, l.IsHqItem, l.RetainerId));
        }

        ulong histAnchorNq = 0;
        ulong histAnchorHq = 0;
        var world = ResolveCurrentWorldName();
        if (!string.IsNullOrWhiteSpace(world) && universalis.TryGetAnyCachedPrice(world, baseItemId, out var histSnap))
        {
            histAnchorNq = histSnap.RecentHistoryMedian > 0
                ? (ulong)histSnap.RecentHistoryMedian
                : (histSnap.MinPriceNq > 0 ? (ulong)histSnap.MinPriceNq : 0UL);
            histAnchorHq = histSnap.RecentHistoryMedianHq > 0
                ? (ulong)histSnap.RecentHistoryMedianHq
                : (histSnap.MinPriceHq > 0 ? (ulong)histSnap.MinPriceHq : 0UL);
        }

        return SelectBestListing(
            inputs,
            baseItemId,
            itemName,
            missingNeeded,
            currentGil,
            EffectiveMaxGilPerPurchase,
            EffectiveOutlierMultiplier,
            histAnchorNq,
            histAnchorHq,
            out blockReason);
    }

    private bool HasBagSpaceForPurchase(InventoryManager* inv, MarketBuyCandidate candidate)
    {
        if (inv == null)
        {
            return false;
        }

        var itemRow = dataManager.GetExcelSheet<Item>()?.GetRowOrDefault(candidate.ItemId);
        var stackSize = itemRow is not null ? (int)Math.Max(1, itemRow.Value.StackSize) : 1;
        var flags = candidate.IsHq ? InventoryItem.ItemFlags.HighQuality : InventoryItem.ItemFlags.None;
        return InventorySafetyEvaluator.FindDestinationSlot(
            inv,
            InventorySafetyEvaluator.PlayerBagContainers,
            candidate.ItemId,
            flags,
            stackSize,
            out _,
            out _,
            routeCrystalsToCrystalBag: true,
            allowPartialMerge: false,
            incomingQty: (int)candidate.Quantity);
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
        var now = DateTime.UtcNow;
        if ((now - lastLifestreamCheckUtc).TotalSeconds < 15.0)
        {
            return cachedLifestreamLoaded;
        }

        lastLifestreamCheckUtc = now;
        cachedLifestreamLoaded = false;
        try
        {
            foreach (var p in pluginInterface.InstalledPlugins)
            {
                if (p.IsLoaded && string.Equals(p.InternalName, "Lifestream", StringComparison.OrdinalIgnoreCase))
                {
                    cachedLifestreamLoaded = true;
                    break;
                }
            }
        }
        catch
        {
        }

        return cachedLifestreamLoaded;
    }

    public void Dispose()
    {
        isDisposed = true;
        transactionQueue.BatchEnded -= OnTransactionBatchEnded;
        isBuyBatchActive = false;
        activeTarget = null;
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
        Action<string, string, string, DcCheapestListing, int>? onWorldHopOffer)
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
                var subtotal = (long)best.UnitPrice * Math.Max(1, best.Quantity);
                var offerTotalWithTax = subtotal + (subtotal * 5L / 100L);
                chatGui.Print(
                    $"Magic Inventory ({dcName} hint): {itemName} cheapest on {best.WorldName} at {best.UnitPrice:N0}g ea x{best.Quantity}{hqTag} (~{offerTotalWithTax:N0}g incl. tax).");

                if (promptTravel && onWorldHopOffer != null)
                {
                    var affordable = offerTotalWithTax <= maxGilPerPurchase && offerTotalWithTax <= currentGil;
                    var savingsOk = currentWorldUnitPrice <= 0
                        || (currentWorldUnitPrice - best.UnitPrice) * Math.Max(1, missingNeeded) >= minSavingsGil;
                    if (affordable && savingsOk)
                    {
                        onWorldHopOffer(itemName, currentWorld, dcName, best, missingNeeded);
                    }
                }
            }).ConfigureAwait(false);
        });
    }
}
