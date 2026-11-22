using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using MagicInventory.Core;
using MagicInventory.Services.MagicSort;
using MagicInventory.Services.Triage;

namespace MagicInventory.Services;

public sealed unsafe class MagicSortService : IDisposable
{
    private const string BatchOwnerKey = "MagicSort";
    private static readonly MagicSortButtonState BusyStopButton = MagicSortButtonState.Enabled("Stop", "Stop the active operation.");
    private static readonly MagicSortButtonState CoolingDownButton = MagicSortButtonState.Disabled("Wait...", "Completing previous operation...");
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

    private readonly SlotRouteResolver slotResolver;
    private readonly RetainerBellEvaluator bellEvaluator;
    private readonly RetainerSortPlanner retainerPlanner;
    private readonly MarketListingPlanner marketPlanner;
    private readonly VendorSellPlanner vendorPlanner;
    private readonly GrandCompanyPlanner grandCompanyPlanner;
    private readonly InventorySortPlanner inventorySortPlanner;
    private readonly GlamourStorePlanner glamourPlanner;

    private readonly Dictionary<StationActionId, StationActionHandler> actionHandlers;
    private readonly Dictionary<string, Action<IReadOnlyDictionary<uint, MagicSortEntry>, HashSet<(InventoryType Container, int Slot)>>> highlightCollectors;

    private bool wasQueueBusy;

    public event System.Action? CacheInvalidated;

    public Func<bool>? IsOrchestratorActive { get; set; }

    public System.Action? OnUserStopRequested { get; set; }

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

        this.triageEngine.TriageUpdated += InvalidateButtonCache;

        slotResolver = new SlotRouteResolver(dataManager, safety, getMissingTrackedQuantities);
        bellEvaluator = new RetainerBellEvaluator(dataManager, holdings, safety, slotResolver, getMissingTrackedQuantities);
        retainerPlanner = new RetainerSortPlanner(dataManager, gameGui, holdings, safety, getMissingTrackedQuantities, InvalidateButtonCache);
        marketPlanner = new MarketListingPlanner(dataManager, gameGui, configuration, transactionQueue, safety);
        vendorPlanner = new VendorSellPlanner(dataManager, gameGui, transactionQueue, safety);
        grandCompanyPlanner = new GrandCompanyPlanner(dataManager, gameGui, transactionQueue, safety);
        var materiaPlanner = new MateriaExtractPlanner(dataManager, gameGui, condition, transactionQueue, InvalidateButtonCache);
        var usePlanner = new UseItemsPlanner(dataManager, condition, transactionQueue, safety);
        inventorySortPlanner = new InventorySortPlanner(dataManager, condition, transactionQueue, safety, slotResolver);
        var armoirePlanner = new ArmoireStorePlanner(dataManager, gameGui, transactionQueue, safety);
        glamourPlanner = new GlamourStorePlanner(dataManager, gameGui, transactionQueue, safety);

        actionHandlers = StationActionRegistry.CreateAll(new StationActionContext
        {
            PluginInterface = pluginInterface,
            GameGui = gameGui,
            Configuration = configuration,
            Holdings = holdings,
            RetainerPlanner = retainerPlanner,
            MarketPlanner = marketPlanner,
            VendorPlanner = vendorPlanner,
            GrandCompanyPlanner = grandCompanyPlanner,
            MateriaPlanner = materiaPlanner,
            UsePlanner = usePlanner,
            InventorySortPlanner = inventorySortPlanner,
            ArmoirePlanner = armoirePlanner,
            GlamourPlanner = glamourPlanner,
            GetLatestManifest = GetLatestManifest,
            InvalidateButtonCache = InvalidateButtonCache,
        });
        highlightCollectors = BuildHighlightCollectors(actionHandlers);
    }

    public SlotRouteResolver SlotResolver => slotResolver;

    public int ManifestItemCount => GetLatestManifest().Count;

    public int CustomRuleCount => configuration.GetManualRouteOverrides(InventorySafetyEvaluator.GetCurrentContentId()).Count;

    public bool IsMarketOnePerClick => configuration.MarketOneListingPerClick;

    public bool IsMarketReviewOnly => configuration.MarketReviewBeforeConfirm;

    public bool HasRemainingAutoSellQuota
        => configuration.MarketReviewBeforeConfirm || configuration.GetRemainingMarketActionsToday(InventorySafetyEvaluator.GetCurrentContentId()) > 0;

    public int GetUsedMarketActionsToday() => configuration.GetUsedMarketActionsToday(InventorySafetyEvaluator.GetCurrentContentId());

    public int MarketDailyActionLimit => configuration.MarketDailyActionLimit;

    public void InvalidateButtonCache()
    {
        foreach (var handler in actionHandlers.Values)
        {
            handler.InvalidateCache();
        }

        CacheInvalidated?.Invoke();
    }

    public MagicSortButtonState GetButtonState(StationActionId actionId)
    {
        if (TryGetBusyButtonState(out var busyState))
        {
            return busyState;
        }

        return actionHandlers.TryGetValue(actionId, out var handler)
            ? handler.GetCachedButtonState(GetLatestManifest)
            : HiddenSortButton;
    }

    public bool ExecuteAction(StationActionId actionId, bool fromOrchestrator = false)
    {
        if (!actionHandlers.TryGetValue(actionId, out var handler))
        {
            return false;
        }

        InvalidateButtonCache();

        if (!fromOrchestrator && IsOrchestratorActive?.Invoke() == true)
        {
            OnUserStopRequested?.Invoke();
            return false;
        }

        if (!fromOrchestrator && transactionQueue.IgnoreToggleClick)
        {
            return false;
        }

        if (transactionQueue.IsBusy)
        {
            if (!fromOrchestrator && transactionQueue.IsOwnedBy(BatchOwnerKey))
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

        if (handler.TryPreExecute != null && handler.TryPreExecute())
        {
            return false;
        }

        var plan = handler.BuildBatch(GetLatestManifest());
        if (plan.Steps.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(plan.EmptyMessage))
            {
                chatGui.Print(plan.EmptyMessage);
            }

            return false;
        }

        if (transactionQueue.TryEnqueueBatch(
                plan.Steps,
                out var reason,
                onCompleted: () =>
                {
                    plan.OnBatchFinished?.Invoke();
                    InvalidateButtonCache();
                    if (plan.HasMoreRemaining && IsOrchestratorActive?.Invoke() != true)
                    {
                        chatGui.Print("Batch paused at the per-click safety cap. Click again for remaining items.");
                    }
                },
                batchOwner: BatchOwnerKey))
        {
            if (!string.IsNullOrWhiteSpace(plan.StartedMessage))
            {
                chatGui.Print(plan.StartedMessage);
            }

            return true;
        }

        chatGui.PrintError(reason);
        return false;
    }

    public bool TryGetEffectiveOverride(uint rawItemId, out ItemRoute effectiveRoute)
    {
        effectiveRoute = default;
        var baseId = ItemIdentity.NormalizeBaseItemId(rawItemId);
        if (baseId == 0
            || !configuration.TryGetManualRoute(InventorySafetyEvaluator.GetCurrentContentId(), baseId, out var route))
        {
            return false;
        }

        if (route.IsSpecificRetainer)
        {
            var ownsRetainer = holdings.GetKnownRetainersForCurrentCharacter().Any(r => r.RetainerId == route.RetainerId);
            if (!ownsRetainer)
            {
                return false;
            }
        }

        effectiveRoute = route;
        return true;
    }

    public bool IsItemOverwritten(uint rawItemId)
        => TryGetEffectiveOverride(rawItemId, out _);

    public void SetItemOverride(uint rawItemId, ItemRoute? route, int? marketPriority = null)
    {
        var baseId = ItemIdentity.NormalizeBaseItemId(rawItemId);
        if (!ItemIdentity.CanLockOrTagBaseId(baseId))
        {
            return;
        }

        var cid = InventorySafetyEvaluator.GetCurrentContentId();
        var overrides = configuration.GetManualRouteOverrides(cid);
        var priorities = configuration.GetMarketListingPriorities(cid);

        if (!route.HasValue)
        {
            overrides.Remove(baseId);
            priorities.Remove(baseId);
        }
        else
        {
            var resolved = route.Value;
            overrides[baseId] = resolved.ToConfigString();
            if (marketPriority.HasValue && resolved.Kind == RouteKind.Market)
            {
                if (marketPriority.Value == 0)
                {
                    priorities.Remove(baseId);
                }
                else
                {
                    priorities[baseId] = Math.Clamp(marketPriority.Value, -1, 1);
                }
            }
            else if (resolved.Kind != RouteKind.Market)
            {
                priorities.Remove(baseId);
            }
        }

        configuration.Save(pluginInterface);
        triageEngine.Invalidate();
        InvalidateButtonCache();
    }

    public int CountInventorySortMoves() => inventorySortPlanner.CountPlannedMoves(GetLatestManifest());

    public List<(InventoryType Container, int Slot)> GetInventorySortHighlightSlots()
        => inventorySortPlanner.GetPlannedMoveSlots(GetLatestManifest());

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
            safety.BuildUnlockedJobGearProfile(),
            includeBagToArmouryRoute: true);
        if (resolved is null)
        {
            return null;
        }

        return slotResolver.FormatHoverTooltipLabel(resolved.Value, rawItemId, container, liveItem, itemRow.Value);
    }

    public bool HasMoreRetainerSortWork()
        => retainerPlanner.BuildPlan(2, GetLatestManifest()).Steps.Count > 0;

    public bool HasMoreMarketSellWork()
    {
        var active = StationActionRegistry.GetActiveRetainer();
        if (active == null)
        {
            return false;
        }

        var liveMarketCount = InventorySafetyEvaluator.GetLiveRetainerMarketCount(InventoryManager.Instance(), active);
        return liveMarketCount < MarketListingPlanner.MaxRetainerMarketListings
            && marketPlanner.PlanListings(1, GetLatestManifest()).Count > 0;
    }

    public long GetEstimatedMarketUnitPrice(uint baseItemId)
    {
        var normalized = ItemIdentity.NormalizeBaseItemId(baseItemId);
        if (normalized == 0 || !GetLatestManifest().TryGetValue(normalized, out var entry))
        {
            return 0;
        }

        if (entry.TargetPriceNq > 0)
        {
            return entry.TargetPriceNq;
        }

        return entry.TargetPriceHq > 0 ? entry.TargetPriceHq : 0;
    }

    public Dictionary<ulong, RetainerBellWorkSummary> EvaluateAllBellWork()
        => bellEvaluator.EvaluateAllRetainers(holdings.GetKnownRetainersForCurrentCharacter(), GetLatestManifest());

    public string BuildBellSortTooltip(
        IReadOnlyList<string> retainerNames,
        IReadOnlyDictionary<ulong, RetainerBellWorkSummary>? precomputedWork = null)
    {
        var sb = new StringBuilder();
        sb.Append("Sort retainers: ").Append(string.Join(", ", retainerNames));

        var known = holdings.GetKnownRetainersForCurrentCharacter();
        var work = precomputedWork ?? bellEvaluator.EvaluateAllRetainers(known, GetLatestManifest());
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

    public string BuildBellSellTooltip(
        IReadOnlyList<string> retainerNames,
        IReadOnlyList<MarketPlanItem>? precomputedPlans = null)
    {
        var sb = new StringBuilder();
        if (configuration.MarketReviewBeforeConfirm)
        {
            sb.Append("Prep Market listing for review via: ").Append(string.Join(", ", retainerNames));
            sb.Append(configuration.MarketAutoComparePricesInReview
                ? "\nOpens & prefills 1 item + live Compare Prices so you inspect before clicking Confirm."
                : "\nOpens & prefills 1 item so you inspect before clicking Confirm.");
        }
        else
        {
            sb.Append("List Market items via: ").Append(string.Join(", ", retainerNames));
        }

        var bagMarketPlans = precomputedPlans ?? GetPendingBagMarketPlans();
        if (bagMarketPlans.Count > 0)
        {
            long totalGil = 0;
            foreach (var p in bagMarketPlans)
            {
                totalGil += p.UnitPrice * Math.Max(1, p.Quantity);
            }

            sb.Append("\nMarket listings to post:");
            StationActionHelpers.AppendBulletLines(sb, bagMarketPlans, 8, StationActionRegistry.FormatMarketPlanLine);
            sb.Append($"\nEstimated Market Total: ~{totalGil:N0}g");
        }

        return sb.ToString();
    }

    public List<MarketPlanItem> GetPendingBagMarketPlans()
        => marketPlanner.PlanBagMarketPreview(GetLatestManifest());

    public int CountPendingBagMarketItems() => GetPendingBagMarketPlans().Count;

    public bool HasPendingBagMarketItems() => GetPendingBagMarketPlans().Count > 0;

    public int CountBagVendorItems()
        => vendorPlanner.PlanSales(1, includeArmoury: false, GetLatestManifest()).Count;

    public int CountBagGcItems()
        => grandCompanyPlanner.CountEligibleItemsInBagsAndArmoury(1, GetLatestManifest(), selectedFilter: 0, out var atSealCap) > 0 && !atSealCap
            ? 1
            : 0;

    public bool HasArmoireWork() => HasBagOrArmouryRouteItems(ItemRoute.Armoire);

    public bool HasGlamourRouteItems() => HasBagOrArmouryRouteItems(ItemRoute.Glamour);

    public void CaptureActiveRetainerSnapshot(bool force = true)
        => holdings.CaptureActiveRetainerIfOpen(true, force);

    public HashSet<(InventoryType Container, int Slot)> GetActiveSellHighlightSlots(string? hoveredSellAddon)
    {
        var slots = new HashSet<(InventoryType Container, int Slot)>();
        if (string.IsNullOrWhiteSpace(hoveredSellAddon)
            || !highlightCollectors.TryGetValue(hoveredSellAddon, out var collect))
        {
            return slots;
        }

        var entries = GetLatestManifest();
        if (entries.Count == 0)
        {
            return slots;
        }

        collect(entries, slots);
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

    public void GetSlotsMatchingRouteFilter(
        RouteFilter routeFilter,
        out HashSet<(InventoryType Container, int Slot)> playerSlots,
        out HashSet<(InventoryType Container, int Slot)> retainerSlots,
        out HashSet<(InventoryType Container, int Slot)> armourySlots)
    {
        playerSlots = new HashSet<(InventoryType Container, int Slot)>();
        retainerSlots = new HashSet<(InventoryType Container, int Slot)>();
        armourySlots = new HashSet<(InventoryType Container, int Slot)>();

        var manager = InventoryManager.Instance();
        var itemSheet = dataManager.GetExcelSheet<Item>();
        var entries = GetLatestManifest();
        if (manager == null || itemSheet == null || entries.Count == 0)
        {
            return;
        }

        var gearsetIds = safety.BuildGearsetItemIds();
        var unlockedProfile = safety.BuildUnlockedJobGearProfile();
        var missingTracked = slotResolver.CaptureMissingTrackedSnapshot();

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

                var resolved = slotResolver.ResolveSlot(
                    slot->ItemId,
                    containerType,
                    slot,
                    entries,
                    gearsetIds,
                    unlockedProfile,
                    includeBagToArmouryRoute: false,
                    missingTracked: missingTracked);
                if (resolved is null || !routeFilter.Matches(resolved.Value.Route))
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

    public HashSet<(InventoryType Container, int Slot)> GetGlamourStoreSlots()
    {
        var slots = new HashSet<(InventoryType, int)>();
        foreach (var p in glamourPlanner.PlanStores(200, GetLatestManifest(), out _))
        {
            slots.Add((p.Container, p.Slot));
        }

        return slots;
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

    public IReadOnlyDictionary<uint, MagicSortEntry> GetLatestManifest()
        => triageEngine.GetCurrentEntries();

    public void Dispose()
    {
        triageEngine.TriageUpdated -= InvalidateButtonCache;
    }

    private Dictionary<string, Action<IReadOnlyDictionary<uint, MagicSortEntry>, HashSet<(InventoryType Container, int Slot)>>> BuildHighlightCollectors(
        Dictionary<StationActionId, StationActionHandler> handlers)
    {
        var map = new Dictionary<string, Action<IReadOnlyDictionary<uint, MagicSortEntry>, HashSet<(InventoryType Container, int Slot)>>>(StringComparer.Ordinal);
        foreach (var handler in handlers.Values)
        {
            if (handler.CollectHighlightSlots == null)
            {
                continue;
            }

            foreach (var addonKey in handler.HighlightAddonKeys)
            {
                map[addonKey] = handler.CollectHighlightSlots;
            }
        }

        map["RetainerList:Sell"] = (manifest, slots) =>
        {
            foreach (var p in GetPendingBagMarketPlans())
            {
                slots.Add((p.Container, p.Slot));
            }
        };

        map["RetainerList:Sort"] = (manifest, slots) =>
        {
            GetSlotsMatchingRouteFilter(RouteFilter.Exact(ItemRoute.AnyRetainer), out var retBags, out _, out _);
            foreach (var s in retBags)
            {
                slots.Add(s);
            }
        };

        return map;
    }

    private bool HasBagOrArmouryRouteItems(ItemRoute route)
    {
        GetSlotsMatchingRouteFilter(RouteFilter.Exact(route), out var bagSlots, out _, out var armourySlots);
        return bagSlots.Count > 0 || armourySlots.Count > 0;
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
            busyState = transactionQueue.IsOwnedBy(BatchOwnerKey) ? BusyStopButton : CoolingDownButton;
            return true;
        }

        busyState = CoolingDownButton;
        return true;
    }
}
