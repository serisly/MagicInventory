using System;
using System.Collections.Generic;
using System.IO;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using MagicInventory.Core;
using MagicInventory.Services.MagicSort;
using MagicInventory.Services.Triage;

namespace MagicInventory.Services;

public sealed unsafe class MagicSortService : IDisposable
{
    private const double ButtonStatePollMs = 250.0;
    private const int MaxTwoStepBatchItems = SafeTransactionQueue.MaxBatchSize / 2;
    private const int MaxThreeStepBatchItems = SafeTransactionQueue.MaxBatchSize / 3;
    private static readonly MagicSortButtonState BusyStopButton = new(true, true, "Stop", "Stop the active operation.");
    private static readonly MagicSortButtonState CoolingDownButton = new(true, false, "Wait...", "Completing previous operation...");
    private static readonly MagicSortButtonState HiddenSortButton = new(false, false, "Magic Sort");

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IDataManager dataManager;
    private readonly IGameGui gameGui;
    private readonly IChatGui chatGui;
    private readonly Configuration configuration;
    private readonly HoldingsService holdings;
    private readonly SafeTransactionQueue transactionQueue;
    private readonly InventorySafetyEvaluator safety;
    private readonly ItemTriageEngine triageEngine;
    private readonly Func<Dictionary<uint, int>> getMissingTrackedQuantities;

    private readonly SlotRouteResolver slotResolver;
    private readonly RetainerBellEvaluator bellEvaluator;
    private readonly RetainerSortPlanner retainerPlanner;
    private readonly MarketListingPlanner marketPlanner;
    private readonly VendorSellPlanner vendorPlanner;
    private readonly GrandCompanyPlanner grandCompanyPlanner;
    private readonly MateriaExtractPlanner materiaPlanner;
    private readonly UseItemsPlanner usePlanner;
    private readonly InventorySortPlanner inventorySortPlanner;
    private readonly ArmoireStorePlanner armoirePlanner;
    private readonly GlamourStorePlanner glamourPlanner;

    private bool wasQueueBusy;
    private bool isOwnBatchActive;

    private readonly ButtonStateCache<(ulong ActiveId, bool ArmouryOpen)> retainerButtonCache = new(ButtonStatePollMs);
    private readonly ButtonStateCache<(ulong ActiveId, int SourceMask)> marketButtonCache = new(ButtonStatePollMs);
    private readonly ButtonStateCache<bool> vendorButtonCache = new(ButtonStatePollMs);
    private readonly ButtonStateCache<(int Tab, int Filter, int SortBy)> gcButtonCache = new(ButtonStatePollMs);
    private readonly ButtonStateCache<int> materiaButtonCache = new(ButtonStatePollMs);
    private readonly ButtonStateCache<int> useButtonCache = new(ButtonStatePollMs);
    private readonly ButtonStateCache<int> invSortButtonCache = new(ButtonStatePollMs);
    private readonly ButtonStateCache<int> armoireButtonCache = new(ButtonStatePollMs);
    private readonly ButtonStateCache<int> glamourButtonCache = new(ButtonStatePollMs);

    public MagicSortService(
        IDalamudPluginInterface pluginInterface,
        IDataManager dataManager,
        IGameGui gameGui,
        ICondition condition,
        IChatGui chatGui,
        Configuration configuration,
        HoldingsService holdings,
        SafeTransactionQueue transactionQueue,
        InventorySafetyEvaluator safety,
        ItemTriageEngine triageEngine,
        Func<Dictionary<uint, int>> getMissingTrackedQuantities)
    {
        this.pluginInterface = pluginInterface;
        this.dataManager = dataManager;
        this.gameGui = gameGui;
        this.chatGui = chatGui;
        this.configuration = configuration;
        this.holdings = holdings;
        this.transactionQueue = transactionQueue;
        this.safety = safety;
        this.triageEngine = triageEngine;
        this.getMissingTrackedQuantities = getMissingTrackedQuantities;

        this.triageEngine.TriageUpdated += InvalidateButtonCache;

        slotResolver = new SlotRouteResolver(dataManager, safety);
        bellEvaluator = new RetainerBellEvaluator(dataManager, holdings, safety, slotResolver, getMissingTrackedQuantities);
        retainerPlanner = new RetainerSortPlanner(dataManager, gameGui, holdings, safety, getMissingTrackedQuantities, InvalidateButtonCache);
        marketPlanner = new MarketListingPlanner(dataManager, gameGui, transactionQueue, safety);
        vendorPlanner = new VendorSellPlanner(dataManager, gameGui, transactionQueue, safety);
        grandCompanyPlanner = new GrandCompanyPlanner(dataManager, gameGui, transactionQueue, safety);
        materiaPlanner = new MateriaExtractPlanner(dataManager, gameGui, condition, transactionQueue, InvalidateButtonCache);
        usePlanner = new UseItemsPlanner(dataManager, condition, chatGui, transactionQueue, safety);
        inventorySortPlanner = new InventorySortPlanner(dataManager, condition, transactionQueue, safety);
        armoirePlanner = new ArmoireStorePlanner(dataManager, gameGui, transactionQueue, safety);
        glamourPlanner = new GlamourStorePlanner(dataManager, gameGui, transactionQueue, safety);

        // One-time migration: the old "Lock Item" feature was removed. Preserve the user's intent by
        // converting any previously locked items into a "Keep" rule so they stay protected.
        var migratedLocks = false;
        foreach (var profile in configuration.Characters.Values)
        {
            foreach (var lockedId in profile.LockedItemIds)
            {
                if (lockedId != 0 && !configuration.ManualRouteOverrides.ContainsKey(lockedId))
                {
                    configuration.ManualRouteOverrides[lockedId] = "Keep";
                    migratedLocks = true;
                }
            }

            if (profile.LockedItemIds.Count > 0)
            {
                profile.LockedItemIds.Clear();
                migratedLocks = true;
            }
        }

        if (migratedLocks)
        {
            configuration.Save(pluginInterface);
        }
    }

    public SlotRouteResolver SlotResolver => slotResolver;

    public int ManifestItemCount => GetLatestManifest().Count;

    public int CustomRuleCount => configuration.ManualRouteOverrides.Count;

    public void InvalidateButtonCache()
    {
        retainerButtonCache.Invalidate();
        marketButtonCache.Invalidate();
        vendorButtonCache.Invalidate();
        gcButtonCache.Invalidate();
        materiaButtonCache.Invalidate();
        useButtonCache.Invalidate();
        invSortButtonCache.Invalidate();
        armoireButtonCache.Invalidate();
        glamourButtonCache.Invalidate();
    }

    public bool IsItemOverwritten(uint rawItemId)
    {
        var baseId = ItemIdentity.NormalizeBaseItemId(rawItemId);
        return baseId != 0 && configuration.ManualRouteOverrides.ContainsKey(baseId);
    }

    public void SetItemOverride(uint rawItemId, string routeSpec, int? marketPriority = null)
    {
        var baseId = ItemIdentity.NormalizeBaseItemId(rawItemId);
        if (!ItemIdentity.CanLockOrTagBaseId(baseId))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(routeSpec))
        {
            configuration.ManualRouteOverrides.Remove(baseId);
            configuration.MarketListingPriorities.Remove(baseId);
        }
        else
        {
            configuration.ManualRouteOverrides[baseId] = routeSpec;
            if (marketPriority.HasValue && string.Equals(routeSpec, "Market", StringComparison.OrdinalIgnoreCase))
            {
                if (marketPriority.Value == 0)
                {
                    configuration.MarketListingPriorities.Remove(baseId);
                }
                else
                {
                    configuration.MarketListingPriorities[baseId] = Math.Clamp(marketPriority.Value, -1, 1);
                }
            }
            else if (!string.Equals(routeSpec, "Market", StringComparison.OrdinalIgnoreCase))
            {
                configuration.MarketListingPriorities.Remove(baseId);
            }
        }

        configuration.Save(pluginInterface);
        triageEngine.Invalidate();
        InvalidateButtonCache();
    }

    public MagicSortButtonState GetUseAllButtonState()
    {
        if (TryGetBusyButtonState(out var busyState))
        {
            return busyState;
        }

        if (useButtonCache.TryGet(0, out var cached))
        {
            return cached;
        }

        var count = usePlanner.CountPlannedUses(GetLatestManifest());
        var state = count > 0
            ? new MagicSortButtonState(true, true, $"Use All ({count})", $"Use {count} unlockable / usable item(s) from your bags.")
            : HiddenSortButton;
        return useButtonCache.Set(0, state);
    }

    public void ExecuteUseAll()
    {
        if (!CanStartOrToggleDirectPlanner())
        {
            return;
        }

        if (usePlanner.Execute(GetLatestManifest(), out var msg))
        {
            isOwnBatchActive = true;
        }
        else if (!string.IsNullOrWhiteSpace(msg))
        {
            chatGui.Print(msg);
        }

        InvalidateButtonCache();
    }

    public MagicSortButtonState GetInventorySortButtonState()
    {
        if (TryGetBusyButtonState(out var busyState))
        {
            return busyState;
        }

        if (invSortButtonCache.TryGet(0, out var cached))
        {
            return cached;
        }

        var moves = inventorySortPlanner.PlanMoves(200, GetLatestManifest());
        MagicSortButtonState state;
        if (moves.Count > 0)
        {
            var sb = new System.Text.StringBuilder();
            var batchNote = moves.Count > 20 ? " (moves 20 per click)" : string.Empty;
            sb.Append($"Sort {moves.Count} item(s) between Bags & Armoury Chest{batchNote}:");
            var shown = Math.Min(8, moves.Count);
            for (var i = 0; i < shown; i++)
            {
                var dir = moves[i].Kind switch
                {
                    InventorySortMoveKind.MergeStack => "merge stack",
                    InventorySortMoveKind.BagToArmoury => "to Armoury",
                    _ => "to Bag",
                };
                sb.Append($"\n  - {moves[i].Name} ({dir})");
            }

            if (moves.Count > shown)
            {
                sb.Append($"\n  ... and {moves.Count - shown} more");
            }

            state = new MagicSortButtonState(true, true, $"Magic Sort ({moves.Count})", sb.ToString());
        }
        else
        {
            state = new MagicSortButtonState(
                true,
                true,
                "Magic Sort",
                "Hover to preview where each item goes. Bags & Armoury are tidy.");
        }

        return invSortButtonCache.Set(0, state);
    }

    public void ExecuteInventorySort()
    {
        if (!CanStartOrToggleDirectPlanner())
        {
            return;
        }

        if (inventorySortPlanner.Execute(GetLatestManifest(), out var msg))
        {
            isOwnBatchActive = true;
        }
        else if (!string.IsNullOrWhiteSpace(msg))
        {
            chatGui.Print(msg);
        }

        InvalidateButtonCache();
    }

    private bool CanStartOrToggleDirectPlanner()
    {
        InvalidateButtonCache();
        if (!transactionQueue.IsBusy)
        {
            isOwnBatchActive = false;
        }

        if (IsOrchestratorActive?.Invoke() == true)
        {
            OnUserStopRequested?.Invoke();
            return false;
        }

        if (transactionQueue.IgnoreToggleClick)
        {
            return false;
        }

        if (transactionQueue.IsBusy)
        {
            if (isOwnBatchActive)
            {
                if (OnUserStopRequested != null)
                {
                    OnUserStopRequested();
                }
                else
                {
                    transactionQueue.Cancel("Stopped.", QueueBatchOutcome.CancelledByUser);
                }
            }

            return false;
        }

        return true;
    }

    public int CountInventorySortMoves() => inventorySortPlanner.CountPlannedMoves(GetLatestManifest());

    public string GetItemDisplayName(uint rawItemId)
    {
        var baseId = ItemIdentity.NormalizeBaseItemId(rawItemId);
        if (baseId == 0)
        {
            return "Item";
        }

        var itemRow = dataManager.GetExcelSheet<Item>()?.GetRowOrDefault(baseId);
        var name = itemRow?.Name.ExtractText();
        return string.IsNullOrWhiteSpace(name) ? $"Item #{baseId}" : name;
    }

    public string? GetHoverSortLabel(
        uint rawItemId,
        InventoryType? container,
        short? slot,
        InventoryItem* liveItem)
    {
        var baseId = ItemIdentity.NormalizeBaseItemId(rawItemId);
        if (baseId == 0)
        {
            return null;
        }

        var itemRow = dataManager.GetExcelSheet<Item>()?.GetRowOrDefault(baseId);
        if (itemRow is null)
        {
            return null;
        }

        var resolved = slotResolver.ResolveSlot(
            rawItemId,
            container,
            liveItem,
            GetLatestManifest(),
            safety.BuildGearsetItemIds(),
            safety.BuildUnlockedJobTopIlvl());
        if (resolved is null)
        {
            return null;
        }

        return slotResolver.FormatHoverTooltipLabel(resolved.Value, rawItemId, container, liveItem, itemRow.Value);
    }

    public MagicSortButtonState GetRetainerSortButtonState()
    {
        if (TryGetBusyButtonState(out var busyState))
        {
            return busyState;
        }

        var activeId = HoldingsService.GetActiveRetainerId();
        var armouryOpen = NativeUiHelpers.IsAddonOpenAndReady(gameGui, "ArmouryBoard");
        var key = (activeId, armouryOpen);
        if (retainerButtonCache.TryGet(key, out var cached))
        {
            return cached;
        }

        // Capture active retainer BEFORE reading manifest so the first poll after opening a retainer uses fresh data
        if (activeId != 0)
        {
            holdings.CaptureActiveRetainerIfOpen(true);
        }

        var plan = retainerPlanner.BuildPlan(SafeTransactionQueue.MaxBatchSize, GetLatestManifest());
        MagicSortButtonState state;
        if (plan.Steps.Count > 0)
        {
            var sb = new System.Text.StringBuilder();
            if (plan.TrackedWithdrawCount > 0)
            {
                sb.Append($"Withdraw {plan.TrackedWithdrawCount} tracked material(s):");
                AppendTooltipItems(sb, plan.TrackedWithdrawSummary, 5);
            }

            if (plan.DepositCount > 0)
            {
                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }

                sb.Append($"Deposit {plan.DepositCount} matching item(s):");
                AppendTooltipItems(sb, plan.DepositSummary, 5);
            }

            if (plan.WithdrawCount > 0)
            {
                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }

                sb.Append($"Withdraw {plan.WithdrawCount} Market / Vendor / misplaced item(s):");
                AppendTooltipItems(sb, plan.SortWithdrawSummary, 5);
            }

            var transferCount = Math.Max(1, plan.TotalTransferCount);
            state = new MagicSortButtonState(true, true, $"Magic Sort ({transferCount})", sb.ToString());
        }
        else if (plan.BlockedByFullRetainer > 0)
        {
            state = new MagicSortButtonState(
                true,
                false,
                "Retainer Full",
                $"This retainer is full (175/175). Open another retainer to store {plan.BlockedByFullRetainer} overflow item(s).");
        }
        else if (plan.BlockedByFullBag > 0)
        {
            state = new MagicSortButtonState(
                true,
                false,
                "Bag Full",
                $"Your inventory is full (140/140). Free bag space before withdrawing {plan.BlockedByFullBag} item(s).");
        }
        else
        {
            state = HiddenSortButton;
        }

        return retainerButtonCache.Set(key, state);
    }

    private static void AppendTooltipItems(System.Text.StringBuilder sb, IReadOnlyList<string> items, int maxShown)
    {
        var shown = Math.Min(items.Count, maxShown);
        for (var i = 0; i < shown; i++)
        {
            sb.Append("\n  - ").Append(items[i]);
        }

        if (items.Count > shown)
        {
            sb.Append($"\n  ... and {items.Count - shown} more");
        }
    }

    public bool HasMoreRetainerSortWork()
    {
        // Pass 2 so a 2-step SplitItem + MoveItemSlot tracked withdrawal isn't skipped by a 1-step budget.
        var plan = retainerPlanner.BuildPlan(2, GetLatestManifest());
        return plan.Steps.Count > 0;
    }

    public void ExecuteRetainerSort() => ExecuteRetainerSortInternal(fromOrchestrator: false);

    public void ExecuteRetainerSortFromOrchestrator() => ExecuteRetainerSortInternal(fromOrchestrator: true);

    private void ExecuteRetainerSortInternal(bool fromOrchestrator)
        => TryExecuteOrCancel(
            () =>
            {
                var plan = retainerPlanner.BuildPlan(SafeTransactionQueue.MaxBatchSize, GetLatestManifest());
                return (plan.Steps, plan.HasMoreRemaining);
            },
            fromOrchestrator: fromOrchestrator);

    public long GetEstimatedMarketUnitPrice(uint baseItemId)
    {
        var normalized = ItemIdentity.NormalizeBaseItemId(baseItemId);
        if (normalized == 0)
        {
            return 0;
        }

        if (GetLatestManifest().TryGetValue(normalized, out var entry))
        {
            if (entry.TargetPriceNq > 0)
            {
                return entry.TargetPriceNq;
            }

            if (entry.TargetPriceHq > 0)
            {
                return entry.TargetPriceHq;
            }
        }

        return 0;
    }

    public Dictionary<ulong, RetainerBellWorkSummary> EvaluateAllBellWork()
    {
        var known = holdings.GetKnownRetainersForCurrentCharacter();
        return bellEvaluator.EvaluateAllRetainers(known, GetLatestManifest());
    }

    public string BuildBellSortTooltip(IReadOnlyList<string> retainerNames)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Sort retainers: ").Append(string.Join(", ", retainerNames));

        var known = holdings.GetKnownRetainersForCurrentCharacter();
        var work = bellEvaluator.EvaluateAllRetainers(known, GetLatestManifest());
        foreach (var r in known)
        {
            if (!work.TryGetValue(r.RetainerId, out var s) || !s.HasAnyWork)
            {
                continue;
            }

            var parts = new List<string>(3);
            if (s.TrackedWithdrawCount > 0) parts.Add($"pull {s.TrackedWithdrawCount} tracked");
            if (s.WithdrawCount > 0) parts.Add($"withdraw {s.WithdrawCount}");
            if (s.DepositCount > 0) parts.Add($"deposit {s.DepositCount}");
            if (parts.Count > 0)
            {
                sb.Append($"\n  - {r.Name}: {string.Join(", ", parts)}");
            }
        }

        return sb.ToString();
    }

    public string BuildBellSellTooltip(IReadOnlyList<string> retainerNames)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("List Market items via: ").Append(string.Join(", ", retainerNames));

        var bagMarketPlans = CollectBagMarketPlansForPreview();
        if (bagMarketPlans.Count > 0)
        {
            long totalGil = 0;
            sb.Append("\nMarket listings to post:");
            var shown = 0;
            foreach (var p in bagMarketPlans)
            {
                var lineTotal = p.UnitPrice * Math.Max(1, p.Quantity);
                totalGil += lineTotal;
                if (shown < 8)
                {
                    var qtySuffix = p.Quantity > 1 ? $" x{p.Quantity}" : string.Empty;
                    sb.Append($"\n  - {p.Name}{qtySuffix} (~{lineTotal:N0}g)");
                    shown++;
                }
            }

            if (bagMarketPlans.Count > shown)
            {
                sb.Append($"\n  ... and {bagMarketPlans.Count - shown} more");
            }

            sb.Append($"\nEstimated Market Total: ~{totalGil:N0}g");
        }

        return sb.ToString();
    }

    public int CountPendingBagMarketItems() => CollectBagMarketPlansForPreview().Count;

    private List<MarketPlanItem> CollectBagMarketPlansForPreview()
        => marketPlanner.PlanBagMarketPreview(GetLatestManifest());

    private static string FormatMarketSellTooltip(IReadOnlyList<MarketPlanItem> plans)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Items to list on the Market Board:");
        long total = 0;
        var shown = 0;
        foreach (var p in plans)
        {
            var lineTotal = p.UnitPrice * Math.Max(1, p.Quantity);
            total += lineTotal;
            if (shown < 10)
            {
                var qtySuffix = p.Quantity > 1 ? $" x{p.Quantity}" : string.Empty;
                sb.Append($"\n  - {p.Name}{qtySuffix} (~{lineTotal:N0}g)");
                shown++;
            }
        }

        if (plans.Count > shown)
        {
            sb.Append($"\n  ... and {plans.Count - shown} more");
        }

        sb.Append($"\nEstimated Total: ~{total:N0}g");
        return sb.ToString();
    }

    private static string FormatVendorSellTooltip(IReadOnlyList<VendorPlanItem> plans)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Items to sell to this vendor:");
        long total = 0;
        var shown = 0;
        foreach (var p in plans)
        {
            var lineTotal = (long)p.NpcUnitPrice * Math.Max(1, p.Quantity);
            total += lineTotal;
            if (shown < 10)
            {
                var qtySuffix = p.Quantity > 1 ? $" x{p.Quantity}" : string.Empty;
                sb.Append($"\n  - {p.Name}{qtySuffix} ({lineTotal:N0}g)");
                shown++;
            }
        }

        if (plans.Count > shown)
        {
            sb.Append($"\n  ... and {plans.Count - shown} more");
        }

        sb.Append($"\nTotal Vendor Gil: {total:N0}g");
        return sb.ToString();
    }

    private static string FormatGrandCompanyTooltip(IReadOnlyList<GrandCompanyPlanItem> plans)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Gear to deliver for Company Seals:");
        long total = 0;
        var shown = 0;
        foreach (var p in plans)
        {
            total += p.SealReward;
            if (shown < 10)
            {
                sb.Append($"\n  - {p.Name} (+{p.SealReward:N0} seals)");
                shown++;
            }
        }

        if (plans.Count > shown)
        {
            sb.Append($"\n  ... and {plans.Count - shown} more");
        }

        sb.Append($"\nTotal Seals: +{total:N0}");
        return sb.ToString();
    }

    public HashSet<(InventoryType Container, int Slot)> GetActiveSellHighlightSlots(string? hoveredSellAddon)
    {
        var slots = new HashSet<(InventoryType Container, int Slot)>();
        if (string.IsNullOrWhiteSpace(hoveredSellAddon))
        {
            return slots;
        }

        var entries = GetLatestManifest();
        if (entries.Count == 0)
        {
            return slots;
        }

        if (string.Equals(hoveredSellAddon, "RetainerSellList", StringComparison.Ordinal)
            && NativeUiHelpers.IsAddonOpenAndReady(gameGui, "RetainerSellList"))
        {
            var rm = RetainerManager.Instance();
            var inv = InventoryManager.Instance();
            var active = rm != null && rm->IsReady ? rm->GetActiveRetainer() : null;
            var liveCount = active != null ? InventorySafetyEvaluator.GetLiveRetainerMarketCount(inv, active) : MarketListingPlanner.MaxRetainerMarketListings;
            if (active != null && liveCount < MarketListingPlanner.MaxRetainerMarketListings)
            {
                var limit = Math.Min(MaxThreeStepBatchItems, MarketListingPlanner.MaxRetainerMarketListings - liveCount);
                foreach (var p in marketPlanner.PlanListings(limit, entries))
                {
                    slots.Add((p.Container, p.Slot));
                }
            }

            return slots;
        }

        if (string.Equals(hoveredSellAddon, "RetainerList:Sell", StringComparison.Ordinal))
        {
            foreach (var p in CollectBagMarketPlansForPreview())
            {
                slots.Add((p.Container, p.Slot));
            }

            return slots;
        }

        if (string.Equals(hoveredSellAddon, "RetainerList:Sort", StringComparison.Ordinal))
        {
            GetSlotsMatchingRouteFilter("Retainer", out var retBags, out _, out _);
            foreach (var s in retBags)
            {
                slots.Add(s);
            }

            return slots;
        }

        if (string.Equals(hoveredSellAddon, "Shop", StringComparison.Ordinal)
            && NativeUiHelpers.IsAddonOpenAndReady(gameGui, "Shop"))
        {
            var includeArmoury = NativeUiHelpers.IsAddonOpenAndReady(gameGui, "ArmouryBoard");
            foreach (var p in vendorPlanner.PlanSales(MaxTwoStepBatchItems, includeArmoury, entries))
            {
                slots.Add((p.Container, p.Slot));
            }

            return slots;
        }

        if (string.Equals(hoveredSellAddon, "GrandCompanySupplyList", StringComparison.Ordinal))
        {
            var gcAddon = (AddonGrandCompanySupplyList*)NativeUiHelpers.GetReadyAddon(gameGui, "GrandCompanySupplyList");
            if (gcAddon != null && gcAddon->SelectedTab == 2)
            {
                foreach (var p in grandCompanyPlanner.PlanTurnIns(MaxTwoStepBatchItems, entries, out _))
                {
                    slots.Add((p.Container, p.Slot));
                }
            }

            return slots;
        }

        if (string.Equals(hoveredSellAddon, "Cabinet", StringComparison.Ordinal)
            && NativeUiHelpers.IsAddonOpenAndReady(gameGui, "Cabinet"))
        {
            foreach (var p in armoirePlanner.PlanStores(SafeTransactionQueue.MaxBatchSize, entries, out _, out _))
            {
                slots.Add((p.Container, p.Slot));
            }

            return slots;
        }

        if (string.Equals(hoveredSellAddon, "MiragePrismPrismBoxCrystallize", StringComparison.Ordinal)
            && NativeUiHelpers.IsAddonOpenAndReady(gameGui, "MiragePrismPrismBoxCrystallize"))
        {
            foreach (var p in glamourPlanner.PlanStores(SafeTransactionQueue.MaxBatchSize, entries, out _))
            {
                slots.Add((p.Container, p.Slot));
            }

            return slots;
        }

        if (string.Equals(hoveredSellAddon, "InventoryExpansion", StringComparison.Ordinal)
            || string.Equals(hoveredSellAddon, "InventoryLarge", StringComparison.Ordinal)
            || string.Equals(hoveredSellAddon, "Inventory", StringComparison.Ordinal))
        {
            foreach (var p in usePlanner.GetPlannedUseSlots(entries))
            {
                slots.Add(p);
            }
        }

        return slots;
    }

    public void GetActiveRetainerSortHighlightSlots(
        string? hoveredRetainerAddon,
        out HashSet<(InventoryType Container, int Slot)> playerDepositSlots,
        out HashSet<(InventoryType Container, int Slot)> retainerWithdrawSlots)
    {
        playerDepositSlots = new HashSet<(InventoryType Container, int Slot)>();
        retainerWithdrawSlots = new HashSet<(InventoryType Container, int Slot)>();

        if (string.IsNullOrWhiteSpace(hoveredRetainerAddon)
            || !NativeUiHelpers.IsAddonOpenAndReady(gameGui, hoveredRetainerAddon)
            || HoldingsService.GetActiveRetainerId() == 0)
        {
            return;
        }

        var plan = retainerPlanner.BuildPlan(SafeTransactionQueue.MaxBatchSize, GetLatestManifest());
        foreach (var slot in plan.DepositSlots)
        {
            playerDepositSlots.Add(slot);
        }

        foreach (var slot in plan.WithdrawSlots)
        {
            retainerWithdrawSlots.Add(slot);
        }
    }

    /// <summary>
    /// Collects all live player-bag, Armoury Chest, and open-retainer slots matching a route filter
    /// ("Keep", "Use", "Sell", "Market", "Vendor", "GrandCompany", "Armoire", "Glamour", "Retainer", or "Retainer:&lt;id&gt;")
    /// for live sibling highlighting when hovering context-menu override options.
    /// </summary>
    public void GetSlotsMatchingRouteFilter(
        string routeFilter,
        out HashSet<(InventoryType Container, int Slot)> playerSlots,
        out HashSet<(InventoryType Container, int Slot)> retainerSlots,
        out HashSet<(InventoryType Container, int Slot)> armourySlots)
    {
        playerSlots = new HashSet<(InventoryType Container, int Slot)>();
        retainerSlots = new HashSet<(InventoryType Container, int Slot)>();
        armourySlots = new HashSet<(InventoryType Container, int Slot)>();

        if (string.IsNullOrWhiteSpace(routeFilter))
        {
            return;
        }

        var manager = InventoryManager.Instance();
        var itemSheet = dataManager.GetExcelSheet<Item>();
        var entries = GetLatestManifest();
        if (manager == null || itemSheet == null || entries.Count == 0)
        {
            return;
        }

        var filterRetainerId = 0UL;
        var matchAnySell = string.Equals(routeFilter, "Sell", StringComparison.OrdinalIgnoreCase);
        var matchRoute = routeFilter;
        if (routeFilter.StartsWith("Retainer:", StringComparison.OrdinalIgnoreCase)
            && ulong.TryParse(routeFilter["Retainer:".Length..], out var parsedRetId))
        {
            matchRoute = "Retainer";
            filterRetainerId = parsedRetId;
        }

        var gearsetIds = safety.BuildGearsetItemIds();
        var unlockedTop = safety.BuildUnlockedJobTopIlvl();

        void ScanContainer(InventoryType containerType, HashSet<(InventoryType, int)> targetSet)
        {
            var container = manager->GetInventoryContainer(containerType);
            if (!InventorySafetyEvaluator.IsContainerReadable(container))
            {
                return;
            }

            for (var i = 0; i < container->Size; i++)
            {
                var slot = InventorySafetyEvaluator.GetSlot(container, i);
                if (slot == null || slot->ItemId == 0 || slot->Quantity <= 0)
                {
                    continue;
                }

                var resolved = slotResolver.ResolveSlot(slot->ItemId, containerType, slot, entries, gearsetIds, unlockedTop);
                if (resolved is null)
                {
                    continue;
                }

                var effective = resolved.Value.Route;
                var targetRetId = resolved.Value.TargetRetainerId;

                if (matchAnySell)
                {
                    if (!string.Equals(effective, "Market", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(effective, "Vendor", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(effective, "GrandCompany", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(effective, "GoldSaucer", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }
                else if (!string.Equals(effective, matchRoute, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (filterRetainerId != 0 && targetRetId != filterRetainerId)
                {
                    continue;
                }

                targetSet.Add((containerType, i));
            }
        }

        foreach (var bag in InventorySafetyEvaluator.PlayerBagContainers)
        {
            if (bag != InventoryType.Crystals)
            {
                ScanContainer(bag, playerSlots);
            }
        }

        foreach (var armoury in InventorySafetyEvaluator.ArmouryGearContainers)
        {
            ScanContainer(armoury, armourySlots);
        }

        ScanContainer(InventoryType.ArmorySoulCrystal, armourySlots);

        if (HoldingsService.GetActiveRetainerId() != 0)
        {
            foreach (var rBag in InventorySafetyEvaluator.RetainerBagContainers)
            {
                if (rBag != InventoryType.RetainerCrystals)
                {
                    ScanContainer(rBag, retainerSlots);
                }
            }
        }
    }

    public bool HasPendingBagMarketItems()
        => CollectBagMarketPlansForPreview().Count > 0;

    public MagicSortButtonState GetMarketSortButtonState()
    {
        if (TryGetBusyButtonState(out var busyState))
        {
            return busyState;
        }

        var rm = RetainerManager.Instance();
        var active = rm != null && rm->IsReady ? rm->GetActiveRetainer() : null;
        if (active == null || active->RetainerId == 0)
        {
            return HiddenSortButton;
        }

        var sourceMask = marketPlanner.GetOpenMarketSourceMask();
        var key = (active->RetainerId, sourceMask);
        if (marketButtonCache.TryGet(key, out var cached))
        {
            return cached;
        }

        var entries = GetLatestManifest();
        var liveMarketCount = InventorySafetyEvaluator.GetLiveRetainerMarketCount(InventoryManager.Instance(), active);
        MagicSortButtonState state;
        if ((sourceMask & 3) == 0)
        {
            state = HiddenSortButton;
        }
        else if (liveMarketCount >= MarketListingPlanner.MaxRetainerMarketListings)
        {
            var hasWaiting = marketPlanner.PlanListings(1, entries).Count > 0
                || marketPlanner.CountArmouryMarketItems(1, entries) > 0;
            state = hasWaiting
                ? new MagicSortButtonState(
                    true,
                    false,
                    "Listings Full",
                    "This retainer has 20/20 market listings. Switch retainers to list remaining items.")
                : HiddenSortButton;
        }
        else
        {
            var availableSlots = MarketListingPlanner.MaxRetainerMarketListings - liveMarketCount;
            var batchLimit = Math.Min(MaxThreeStepBatchItems, availableSlots);
            var plans = marketPlanner.PlanListings(batchLimit, entries);
            if (plans.Count > 0)
            {
                var label = configuration.MarketReviewBeforeConfirm
                    ? $"Prep Sell ({plans.Count})"
                    : $"Magic Sell ({plans.Count})";
                var tooltip = configuration.MarketReviewBeforeConfirm
                    ? "Review mode: opens & prefills 1 item so you can inspect the price before confirming.\n" + FormatMarketSellTooltip(plans)
                    : FormatMarketSellTooltip(plans);
                state = new MagicSortButtonState(true, true, label, tooltip);
            }
            else
            {
                var armouryMkt = marketPlanner.CountArmouryMarketItems(SafeTransactionQueue.MaxBatchSize, entries);
                state = armouryMkt > 0
                    ? new MagicSortButtonState(
                        true,
                        false,
                        $"Armoury ({armouryMkt})",
                        $"Move {armouryMkt} market-ready gear item(s) from your Armoury Chest to your inventory first.")
                    : HiddenSortButton;
            }
        }

        return marketButtonCache.Set(key, state);
    }

    public bool HasMoreMarketSellWork()
    {
        var rm = RetainerManager.Instance();
        var active = rm != null && rm->IsReady ? rm->GetActiveRetainer() : null;
        if (active == null || active->RetainerId == 0)
        {
            return false;
        }

        var liveMarketCount = InventorySafetyEvaluator.GetLiveRetainerMarketCount(InventoryManager.Instance(), active);
        if (liveMarketCount >= MarketListingPlanner.MaxRetainerMarketListings)
        {
            return false;
        }

        return marketPlanner.PlanListings(1, GetLatestManifest()).Count > 0;
    }

    public void ExecuteMarketSort() => ExecuteMarketSortInternal(fromOrchestrator: false);

    public bool IsMarketOnePerClick => configuration.MarketOneListingPerClick;

    public bool IsMarketReviewOnly => configuration.MarketReviewBeforeConfirm;

    public void ExecuteMarketSortFromOrchestrator() => ExecuteMarketSortInternal(fromOrchestrator: true);

    private void ExecuteMarketSortInternal(bool fromOrchestrator)
    {
        TryExecuteOrCancel(() =>
        {
            var rm = RetainerManager.Instance();
            var active = rm != null && rm->IsReady ? rm->GetActiveRetainer() : null;
            if (active == null || active->RetainerId == 0)
            {
                return (Array.Empty<TransactionStep>(), false);
            }

            var liveMarketCount = InventorySafetyEvaluator.GetLiveRetainerMarketCount(InventoryManager.Instance(), active);
            var availableSlots = MarketListingPlanner.MaxRetainerMarketListings - liveMarketCount;
            if (availableSlots <= 0)
            {
                chatGui.Print("Market Board listing cap reached (20/20).");
                return (Array.Empty<TransactionStep>(), false);
            }

            var reviewOnly = configuration.MarketReviewBeforeConfirm;
            var remainingToday = configuration.GetRemainingMarketActionsToday();
            if (!reviewOnly && remainingToday <= 0)
            {
                chatGui.Print($"Magic Sell: daily limit reached ({configuration.MarketDailyActionLimit} Market Board actions). List the rest manually or raise the limit in Settings.");
                return (Array.Empty<TransactionStep>(), false);
            }

            var perClick = (reviewOnly || configuration.MarketOneListingPerClick) ? 1 : MaxThreeStepBatchItems;
            var batchLimit = reviewOnly
                ? Math.Min(1, availableSlots)
                : Math.Min(Math.Min(perClick, availableSlots), remainingToday);
            var probeLimit = Math.Min(batchLimit + 1, availableSlots);
            var plans = marketPlanner.PlanListings(probeLimit, GetLatestManifest());
            var hasMore = plans.Count > batchLimit;
            if (hasMore)
            {
                plans.RemoveRange(batchLimit, plans.Count - batchLimit);
            }

            var steps = marketPlanner.BuildListingSteps(plans, GetLatestManifest, reviewOnly: reviewOnly);
            if (reviewOnly)
            {
                // Review mode leaves RetainerSell open for user confirmation — don't consume automated action quota.
                return (steps, hasMore);
            }

            var counted = new List<TransactionStep>(steps.Count);
            for (var i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                // Each listing produces 3 steps (Open, Set price, Confirm).
                // Record 1 daily Market Board action ONLY on the final Confirm step — NOT 3x per item!
                var isFinalListingStep = ((i + 1) % 3) == 0;
                if (!isFinalListingStep)
                {
                    counted.Add(step);
                    continue;
                }

                var original = step.HasServerAck;
                var recorded = false;
                counted.Add(step with
                {
                    HasServerAck = () =>
                    {
                        var acked = original();
                        if (acked && !recorded)
                        {
                            recorded = true;
                            configuration.RecordMarketAction();
                            configuration.Save(pluginInterface);
                        }

                        return acked;
                    },
                });
            }

            return (counted, hasMore);
        }, fromOrchestrator: fromOrchestrator);
    }

    public MagicSortButtonState GetVendorSortButtonState()
    {
        if (TryGetBusyButtonState(out var busyState))
        {
            return busyState;
        }

        var armouryOpen = NativeUiHelpers.IsAddonOpenAndReady(gameGui, "ArmouryBoard");
        if (vendorButtonCache.TryGet(armouryOpen, out var cached))
        {
            return cached;
        }

        var entries = GetLatestManifest();
        var plans = vendorPlanner.PlanSales(MaxTwoStepBatchItems, armouryOpen, entries);
        MagicSortButtonState state;
        if (plans.Count > 0)
        {
            state = new MagicSortButtonState(
                true,
                true,
                $"Magic Sell ({plans.Count})",
                FormatVendorSellTooltip(plans));
        }
        else if (!armouryOpen)
        {
            var armouryCount = vendorPlanner.CountArmouryVendorItems(99, entries);
            state = armouryCount > 0
                ? new MagicSortButtonState(
                    true,
                    false,
                    $"Armoury ({armouryCount})",
                    $"Open your Armoury Chest while talking to this vendor to sell {armouryCount} obsolete gear item(s).")
                : HiddenSortButton;
        }
        else
        {
            state = HiddenSortButton;
        }

        return vendorButtonCache.Set(armouryOpen, state);
    }

    public void ExecuteVendorSort()
    {
        TryExecuteOrCancel(() =>
        {
            var armouryOpen = NativeUiHelpers.IsAddonOpenAndReady(gameGui, "ArmouryBoard");
            var plans = vendorPlanner.PlanSales(MaxTwoStepBatchItems + 1, armouryOpen, GetLatestManifest());
            var hasMore = plans.Count > MaxTwoStepBatchItems;
            if (hasMore)
            {
                plans.RemoveRange(MaxTwoStepBatchItems, plans.Count - MaxTwoStepBatchItems);
            }

            return (vendorPlanner.BuildSellSteps(plans, GetLatestManifest), hasMore);
        });
    }

    public MagicSortButtonState GetGrandCompanySortButtonState()
    {
        if (TryGetBusyButtonState(out var busyState))
        {
            return busyState;
        }

        var supplyAddon = (AddonGrandCompanySupplyList*)NativeUiHelpers.GetReadyAddon(gameGui, "GrandCompanySupplyList");
        if (supplyAddon == null)
        {
            return HiddenSortButton;
        }

        var tab = supplyAddon->SelectedTab;
        var filter = supplyAddon->SelectedFilter;
        var sortBy = supplyAddon->SelectedSortBy;
        var key = (tab, filter, sortBy);
        if (gcButtonCache.TryGet(key, out var cached))
        {
            return cached;
        }

        var entries = GetLatestManifest();
        MagicSortButtonState state;
        if (tab != 2)
        {
            var eligibleCount = grandCompanyPlanner.CountEligibleItemsInBagsAndArmoury(MaxTwoStepBatchItems, entries);
            state = eligibleCount == 0
                ? HiddenSortButton
                : new MagicSortButtonState(
                    true,
                    true,
                    $"Magic Sell ({eligibleCount})",
                    "Open the Expert Delivery tab to deliver unassigned gear for Company Seals.");
        }
        else
        {
            var plans = grandCompanyPlanner.PlanTurnIns(MaxTwoStepBatchItems, entries, out var blockedBySealCap);
            if (plans.Count > 0)
            {
                state = new MagicSortButtonState(
                    true,
                    true,
                    $"Magic Sell ({plans.Count})",
                    FormatGrandCompanyTooltip(plans));
            }
            else if (blockedBySealCap)
            {
                state = new MagicSortButtonState(
                    true,
                    false,
                    "Seal Cap",
                    "Company Seal cap reached. Spend seals before turning in more gear.");
            }
            else
            {
                state = HiddenSortButton;
            }
        }

        return gcButtonCache.Set(key, state);
    }

    public void ExecuteGrandCompanySort()
    {
        var supplyAddonCheck = (AddonGrandCompanySupplyList*)NativeUiHelpers.GetReadyAddon(gameGui, "GrandCompanySupplyList");
        if (supplyAddonCheck != null && supplyAddonCheck->SelectedTab != 2)
        {
            if (supplyAddonCheck->ExpertDeliveryRadioButton != null)
            {
                NativeUiHelpers.ClickAddonButton(
                    (AtkUnitBase*)supplyAddonCheck,
                    (AtkComponentButton*)supplyAddonCheck->ExpertDeliveryRadioButton);
                InvalidateButtonCache();
            }

            return;
        }

        TryExecuteOrCancel(() =>
        {
            var supplyAddon = (AddonGrandCompanySupplyList*)NativeUiHelpers.GetReadyAddon(gameGui, "GrandCompanySupplyList");
            var ps = PlayerState.Instance();
            var gc = ps != null ? ps->GrandCompany : (byte)0;
            if (supplyAddon == null || supplyAddon->SelectedTab != 2 || gc == 0)
            {
                return (Array.Empty<TransactionStep>(), false);
            }

            var plans = grandCompanyPlanner.PlanTurnIns(MaxTwoStepBatchItems + 1, GetLatestManifest(), out _);
            var hasMore = plans.Count > MaxTwoStepBatchItems;
            if (hasMore)
            {
                plans.RemoveRange(MaxTwoStepBatchItems, plans.Count - MaxTwoStepBatchItems);
            }

            return (grandCompanyPlanner.BuildTurnInSteps(plans, gc, GetLatestManifest), hasMore);
        });
    }

    public MagicSortButtonState GetMateriaExtractButtonState()
    {
        if (TryGetBusyButtonState(out var busyState))
        {
            return busyState;
        }

        if (materiaButtonCache.TryGet(0, out var cached))
        {
            return cached;
        }

        var plans = materiaPlanner.PlanExtractions(SafeTransactionQueue.MaxBatchSize, out var blockedByFullBag);
        MagicSortButtonState state;
        if (plans.Count > 0)
        {
            state = new MagicSortButtonState(
                true,
                true,
                $"Extract All ({plans.Count})",
                $"Extract Materia from {plans.Count} gear item(s) at 100% Spiritbond.");
        }
        else if (blockedByFullBag > 0)
        {
            state = new MagicSortButtonState(
                true,
                false,
                "Bag Full",
                "Your inventory is full (140/140). Free bag space before extracting Materia.");
        }
        else
        {
            state = HiddenSortButton;
        }

        return materiaButtonCache.Set(0, state);
    }

    public void ExecuteMateriaExtractSort()
    {
        TryExecuteOrCancel(() =>
        {
            var plans = materiaPlanner.PlanExtractions(SafeTransactionQueue.MaxBatchSize + 1, out _);
            var hasMore = plans.Count > SafeTransactionQueue.MaxBatchSize;
            if (hasMore)
            {
                plans.RemoveRange(SafeTransactionQueue.MaxBatchSize, plans.Count - SafeTransactionQueue.MaxBatchSize);
            }

            return (materiaPlanner.BuildExtractionSteps(plans), hasMore);
        });
    }

    public MagicSortButtonState GetArmoireStoreButtonState()
    {
        if (TryGetBusyButtonState(out var busyState))
        {
            return busyState;
        }

        if (armoireButtonCache.TryGet(0, out var cached))
        {
            return cached;
        }

        var plans = armoirePlanner.PlanStores(SafeTransactionQueue.MaxBatchSize, GetLatestManifest(), out var needsRepair, out var meldedSkipped);
        MagicSortButtonState state;
        if (plans.Count > 0)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"Store {plans.Count} item(s) in your Armoire:");
            var shown = Math.Min(10, plans.Count);
            for (var i = 0; i < shown; i++)
            {
                sb.Append($"\n  - {plans[i].Name}");
            }

            if (plans.Count > shown)
            {
                sb.Append($"\n  ... and {plans.Count - shown} more");
            }

            if (needsRepair > 0)
            {
                sb.Append($"\n({needsRepair} skipped — repair to 100% first)");
            }

            state = new MagicSortButtonState(true, true, $"Magic Store ({plans.Count})", sb.ToString());
        }
        else if (needsRepair > 0)
        {
            state = new MagicSortButtonState(
                true,
                false,
                "Repair First",
                $"{needsRepair} Armoire item(s) below 100% condition — repair before storing.");
        }
        else if (meldedSkipped > 0)
        {
            state = new MagicSortButtonState(
                true,
                false,
                "Unmeld First",
                $"{meldedSkipped} Armoire item(s) have Materia melded.");
        }
        else
        {
            state = HiddenSortButton;
        }

        return armoireButtonCache.Set(0, state);
    }

    public void ExecuteArmoireStore()
    {
        TryExecuteOrCancel(() =>
        {
            var plans = armoirePlanner.PlanStores(SafeTransactionQueue.MaxBatchSize + 1, GetLatestManifest(), out var needsRepair, out _);
            var hasMore = plans.Count > SafeTransactionQueue.MaxBatchSize;
            if (hasMore)
            {
                plans.RemoveRange(SafeTransactionQueue.MaxBatchSize, plans.Count - SafeTransactionQueue.MaxBatchSize);
            }

            if (plans.Count == 0 && needsRepair > 0)
            {
                chatGui.Print($"Magic Inventory: {needsRepair} Armoire item(s) need to be repaired to 100% first.");
            }

            return (armoirePlanner.BuildStoreSteps(plans, InvalidateButtonCache), hasMore);
        });
    }

    public MagicSortButtonState GetGlamourDresserButtonState()
    {
        if (TryGetBusyButtonState(out var busyState))
        {
            return busyState;
        }

        if (glamourButtonCache.TryGet(0, out var cached))
        {
            return cached;
        }

        var plans = glamourPlanner.PlanStores(SafeTransactionQueue.MaxBatchSize, GetLatestManifest(), out var blockReason);
        MagicSortButtonState state;
        if (plans.Count > 0)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"Store {plans.Count} item(s) in your Glamour Dresser (1 Glamour Prism each):");
            var shown = Math.Min(10, plans.Count);
            for (var i = 0; i < shown; i++)
            {
                sb.Append($"\n  - {plans[i].Name}");
            }

            if (plans.Count > shown)
            {
                sb.Append($"\n  ... and {plans.Count - shown} more");
            }

            state = new MagicSortButtonState(true, true, $"Magic Store ({plans.Count})", sb.ToString());
        }
        else if (!string.IsNullOrEmpty(blockReason))
        {
            state = new MagicSortButtonState(true, false, "Magic Store", blockReason);
        }
        else
        {
            state = HiddenSortButton;
        }

        return glamourButtonCache.Set(0, state);
    }

    public void ExecuteGlamourDresserStore()
    {
        TryExecuteOrCancel(() =>
        {
            var plans = glamourPlanner.PlanStores(SafeTransactionQueue.MaxBatchSize + 1, GetLatestManifest(), out var blockReason);
            var hasMore = plans.Count > SafeTransactionQueue.MaxBatchSize;
            if (hasMore)
            {
                plans.RemoveRange(SafeTransactionQueue.MaxBatchSize, plans.Count - SafeTransactionQueue.MaxBatchSize);
            }

            if (plans.Count == 0 && !string.IsNullOrEmpty(blockReason))
            {
                chatGui.Print($"Magic Inventory: {blockReason}");
            }

            return (glamourPlanner.BuildStoreSteps(plans, InvalidateButtonCache), hasMore);
        });
    }

    public IReadOnlyList<string> GetGlamourStoreItemNames()
    {
        var names = new List<string>();
        foreach (var p in glamourPlanner.PlanStores(200, GetLatestManifest(), out _))
        {
            names.Add(p.Name);
        }

        return names;
    }

    public Func<bool>? IsOrchestratorActive { get; set; }

    public System.Action? OnUserStopRequested { get; set; }

    private void TryExecuteOrCancel(
        Func<(IReadOnlyList<TransactionStep> Steps, bool HasMoreRemaining)> buildBatch,
        System.Action? onBatchFinished = null,
        bool fromOrchestrator = false)
    {
        InvalidateButtonCache();
        if (!transactionQueue.IsBusy)
        {
            isOwnBatchActive = false;
        }

        if (!fromOrchestrator && IsOrchestratorActive?.Invoke() == true)
        {
            OnUserStopRequested?.Invoke();
            return;
        }

        if (!fromOrchestrator && transactionQueue.IgnoreToggleClick)
        {
            return;
        }

        if (transactionQueue.IsBusy)
        {
            if (!fromOrchestrator && isOwnBatchActive)
            {
                if (OnUserStopRequested != null)
                {
                    OnUserStopRequested();
                }
                else
                {
                    transactionQueue.Cancel("Stopped.", QueueBatchOutcome.CancelledByUser);
                }
            }

            return;
        }

        var (steps, hasMoreRemaining) = buildBatch();
        if (steps.Count == 0)
        {
            return;
        }

        if (transactionQueue.TryEnqueueBatch(
                steps,
                out var reason,
                onCompleted: () =>
                {
                    onBatchFinished?.Invoke();
                    InvalidateButtonCache();
                    if (hasMoreRemaining && IsOrchestratorActive?.Invoke() != true)
                    {
                        chatGui.Print("Batch paused at the per-click safety cap. Click again for remaining items.");
                    }
                }))
        {
            isOwnBatchActive = true;
        }
        else
        {
            chatGui.PrintError(reason);
        }
    }

    private bool TryGetBusyButtonState(out MagicSortButtonState busyState)
    {
        if (IsOrchestratorActive?.Invoke() == true)
        {
            busyState = BusyStopButton;
            return true;
        }

        if (!transactionQueue.IsBusy)
        {
            isOwnBatchActive = false;
            if (wasQueueBusy)
            {
                wasQueueBusy = false;
                InvalidateButtonCache();
            }

            busyState = HiddenSortButton;
            return false;
        }

        wasQueueBusy = true;
        if (transactionQueue.IsExecuting)
        {
            busyState = isOwnBatchActive ? BusyStopButton : CoolingDownButton;
            return true;
        }

        busyState = CoolingDownButton;
        return true;
    }

    public IReadOnlyDictionary<uint, MagicSortEntry> GetLatestManifest()
        => triageEngine.GetCurrentEntries();

    public void Dispose()
    {
        triageEngine.TriageUpdated -= InvalidateButtonCache;
    }

}
