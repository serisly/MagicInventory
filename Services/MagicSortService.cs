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

    private readonly RetainerSortPlanner retainerPlanner;
    private readonly MarketListingPlanner marketPlanner;
    private readonly VendorSellPlanner vendorPlanner;
    private readonly GrandCompanyPlanner grandCompanyPlanner;
    private readonly MateriaExtractPlanner materiaPlanner;
    private readonly UseItemsPlanner usePlanner;
    private readonly InventorySortPlanner inventorySortPlanner;
    private readonly ArmoireStorePlanner armoirePlanner;

    private bool wasQueueBusy;
    private bool isOwnBatchActive;

    private DateTime lastRetainerButtonUtc = DateTime.MinValue;
    private ulong lastRetainerButtonActiveId;
    private bool lastRetainerArmouryOpen;
    private MagicSortButtonState cachedRetainerButton = HiddenSortButton;

    private DateTime lastMarketButtonUtc = DateTime.MinValue;
    private ulong lastMarketButtonActiveId;
    private int lastMarketSourceMask = -1;
    private MagicSortButtonState cachedMarketButton = HiddenSortButton;

    private DateTime lastVendorButtonUtc = DateTime.MinValue;
    private bool lastVendorArmouryOpen;
    private MagicSortButtonState cachedVendorButton = HiddenSortButton;

    private DateTime lastGcButtonUtc = DateTime.MinValue;
    private int lastGcSelectedTab = -1;
    private int lastGcSelectedFilter = -1;
    private MagicSortButtonState cachedGcButton = HiddenSortButton;

    private DateTime lastMateriaButtonUtc = DateTime.MinValue;
    private MagicSortButtonState cachedMateriaButton = HiddenSortButton;

    private DateTime lastUseButtonUtc = DateTime.MinValue;
    private MagicSortButtonState cachedUseButton = HiddenSortButton;

    private DateTime lastInvSortButtonUtc = DateTime.MinValue;
    private MagicSortButtonState cachedInvSortButton = new(true, true, "Magic Sort", "Hover to preview destinations.");

    private DateTime lastArmoireButtonUtc = DateTime.MinValue;
    private MagicSortButtonState cachedArmoireButton = HiddenSortButton;

    private DateTime lastGlamourButtonUtc = DateTime.MinValue;
    private MagicSortButtonState cachedGlamourButton = HiddenSortButton;

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

        retainerPlanner = new RetainerSortPlanner(dataManager, gameGui, holdings, safety, getMissingTrackedQuantities, InvalidateButtonCache);
        marketPlanner = new MarketListingPlanner(dataManager, gameGui, transactionQueue, safety);
        vendorPlanner = new VendorSellPlanner(dataManager, gameGui, transactionQueue, safety);
        grandCompanyPlanner = new GrandCompanyPlanner(dataManager, gameGui, transactionQueue, safety);
        materiaPlanner = new MateriaExtractPlanner(dataManager, gameGui, condition, transactionQueue, InvalidateButtonCache);
        usePlanner = new UseItemsPlanner(dataManager, condition, chatGui, transactionQueue, safety);
        inventorySortPlanner = new InventorySortPlanner(dataManager, condition, transactionQueue, safety);
        armoirePlanner = new ArmoireStorePlanner(dataManager, gameGui, transactionQueue, safety);

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

    public int ManifestItemCount => GetLatestManifest().Count;

    public int CustomRuleCount => configuration.ManualRouteOverrides.Count;

    public static bool CanLockOrTagBaseId(uint baseItemId)
        => InventorySafetyEvaluator.CanLockOrTagBaseId(baseItemId);

    public void InvalidateButtonCache()
    {
        lastRetainerButtonUtc = DateTime.MinValue;
        lastMarketButtonUtc = DateTime.MinValue;
        lastVendorButtonUtc = DateTime.MinValue;
        lastGcButtonUtc = DateTime.MinValue;
        lastMateriaButtonUtc = DateTime.MinValue;
        lastUseButtonUtc = DateTime.MinValue;
        lastInvSortButtonUtc = DateTime.MinValue;
        lastArmoireButtonUtc = DateTime.MinValue;
        lastGlamourButtonUtc = DateTime.MinValue;
    }

    public bool IsItemOverwritten(uint rawItemId)
    {
        var baseId = HoldingsService.NormalizeBaseItemId(rawItemId);
        return baseId != 0 && configuration.ManualRouteOverrides.ContainsKey(baseId);
    }

    public void SetItemOverride(uint rawItemId, string routeSpec, int? marketPriority = null)
    {
        var baseId = HoldingsService.NormalizeBaseItemId(rawItemId);
        if (!CanLockOrTagBaseId(baseId))
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

        var now = DateTime.UtcNow;
        if ((now - lastUseButtonUtc).TotalMilliseconds < ButtonStatePollMs)
        {
            return cachedUseButton;
        }

        lastUseButtonUtc = now;
        var count = usePlanner.CountPlannedUses(GetLatestManifest());
        cachedUseButton = count > 0
            ? new MagicSortButtonState(true, true, $"Use All ({count})", $"Use {count} unlockable / usable item(s) from your bags.")
            : HiddenSortButton;
        return cachedUseButton;
    }

    public void ExecuteUseAll()
    {
        if (transactionQueue.IsBusy)
        {
            transactionQueue.Cancel("Stopped.", QueueBatchOutcome.CancelledByUser);
            InvalidateButtonCache();
            return;
        }

        if (!usePlanner.Execute(GetLatestManifest(), out var msg) && !string.IsNullOrWhiteSpace(msg))
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

        var now = DateTime.UtcNow;
        if ((now - lastInvSortButtonUtc).TotalMilliseconds < ButtonStatePollMs)
        {
            return cachedInvSortButton;
        }

        lastInvSortButtonUtc = now;
        var moves = inventorySortPlanner.PlanMoves(200, GetLatestManifest());
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

            cachedInvSortButton = new MagicSortButtonState(true, true, $"Magic Sort ({moves.Count})", sb.ToString());
        }
        else
        {
            cachedInvSortButton = new MagicSortButtonState(
                true,
                true,
                "Magic Sort",
                "Hover to preview where each item goes. Bags & Armoury are tidy.");
        }

        return cachedInvSortButton;
    }

    public void ExecuteInventorySort()
    {
        if (transactionQueue.IsBusy)
        {
            transactionQueue.Cancel("Stopped.", QueueBatchOutcome.CancelledByUser);
            InvalidateButtonCache();
            return;
        }

        if (!inventorySortPlanner.Execute(GetLatestManifest(), out var msg) && !string.IsNullOrWhiteSpace(msg))
        {
            chatGui.Print(msg);
        }

        InvalidateButtonCache();
    }

    public int CountInventorySortMoves() => inventorySortPlanner.CountPlannedMoves(GetLatestManifest());

    public string GetItemDisplayName(uint rawItemId)
    {
        var baseId = HoldingsService.NormalizeBaseItemId(rawItemId);
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
        if (rawItemId == 0)
        {
            return null;
        }

        if (container is InventoryType.Crystals
            or InventoryType.Currency
            or InventoryType.KeyItems
            or InventoryType.RetainerCrystals
            or InventoryType.RetainerGil
            or InventoryType.RetainerMarket)
        {
            return null;
        }

        if (container is InventoryType.EquippedItems or InventoryType.RetainerEquippedItems)
        {
            var eqBaseId = HoldingsService.NormalizeBaseItemId(rawItemId);
            var eqRow = dataManager.GetExcelSheet<Item>()?.GetRowOrDefault(eqBaseId);
            if (eqRow is not null && InventorySafetyEvaluator.IsMateriaExtractable(eqRow.Value, liveItem))
            {
                return "Magic Sort: Extract Materia";
            }

            return "Magic Sort: Keep";
        }

        var baseId = HoldingsService.NormalizeBaseItemId(rawItemId);
        if (!CanLockOrTagBaseId(baseId))
        {
            return null;
        }

        var itemRow = dataManager.GetExcelSheet<Item>()?.GetRowOrDefault(baseId);
        // FilterGroup 11 = Crystals/Shards, 16 = Currency (FilterGroup 12 is Crafting Materials/Ingredients!)
        if (itemRow is null or { FilterGroup: 11 } or { FilterGroup: 16 })
        {
            return null;
        }

        var inRetainer = InventorySafetyEvaluator.IsRetainerContainer(container);
        var inArmoury = container.HasValue && InventorySafetyEvaluator.IsArmouryContainer(container.Value);
        var activeRetLabel = HoldingsService.GetActiveRetainerName() ?? "Retainer";

        if (InventorySafetyEvaluator.IsMateriaExtractable(itemRow.Value, liveItem))
        {
            return "Magic Sort: Extract Materia";
        }

        if (safety.IsItemTracked(baseId))
        {
            return "Magic Sort: Keep";
        }

        var gearsetIds = safety.BuildGearsetItemIds();
        if (gearsetIds.Contains(baseId))
        {
            return "Magic Sort: Keep";
        }

        if (itemRow.Value.ItemUICategory.RowId == 62 || itemRow.Value.Rarity >= 4)
        {
            return "Magic Sort: Keep";
        }

        var entries = GetLatestManifest();
        if (!entries.TryGetValue(baseId, out var entry))
        {
            if (!inRetainer
                && !inArmoury
                && InventorySafetyEvaluator.IsEligibleForLiveRetainerDuplicateDeposit(itemRow.Value)
                && HoldingsService.GetActiveRetainerId() != 0
                && HoldingsService.GetLiveActiveRetainerCount(baseId) > 0)
            {
                return $"Magic Sort: {activeRetLabel}";
            }

            return "Magic Sort: Keep";
        }

        if (string.Equals(entry.Route, "Keep", StringComparison.OrdinalIgnoreCase))
        {
            if (entry.Reason.StartsWith("Checking ", StringComparison.OrdinalIgnoreCase))
            {
                return "Magic Sort: Checking price...";
            }

            if (entry.Reason.StartsWith("Open a Summoning Bell", StringComparison.OrdinalIgnoreCase)
                || entry.Reason.StartsWith("Visit each retainer", StringComparison.OrdinalIgnoreCase))
            {
                return "Magic Sort: Visit retainers at Bell";
            }

            return "Magic Sort: Keep";
        }

        var unlockedTop = safety.BuildUnlockedJobTopIlvl();
        var effectiveRoute = safety.ResolveEffectiveSlotRoute(entry, itemRow.Value, liveItem, entries, gearsetIds, unlockedTop);
        if (effectiveRoute == null)
        {
            return "Magic Sort: Keep";
        }

        var isHq = rawItemId >= 1_000_000
            || (liveItem != null && liveItem->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality));

        switch (effectiveRoute)
        {
            case "Retainer":
            {
                var retName = string.IsNullOrWhiteSpace(entry.TargetRetainerName) ? "Retainer" : entry.TargetRetainerName;
                return $"Magic Sort: {retName}";
            }

            case "Use":
                return "Magic Sort: Use";

            case "Market":
            {
                var price = isHq && entry.TargetPriceHq > 0
                    ? entry.TargetPriceHq
                    : (entry.TargetPriceNq > 0 ? entry.TargetPriceNq : entry.TargetPriceHq);
                var prio = safety.GetMarketPriority(baseId);
                var prioTag = prio > 0 ? " [High]" : (prio < 0 ? " [Low]" : string.Empty);
                return price > 0 ? $"Magic Sort: Market (~{price:N0}g){prioTag}" : $"Magic Sort: Market{prioTag}";
            }

            case "Vendor":
            {
                var qty = liveItem != null && liveItem->Quantity > 0 ? liveItem->Quantity : 1;
                var npcUnit = entry.NpcSellPrice > 0 ? entry.NpcSellPrice : itemRow.Value.PriceLow;
                var total = Math.Max(0, npcUnit) * qty;
                return total > 0 ? $"Magic Sort: Vendor ({total:N0}g)" : "Magic Sort: Vendor";
            }

            case "GrandCompany":
                return "Magic Sort: GC Seals";

            case "GoldSaucer":
                return "Magic Sort: Gold Saucer";

            case "Materia":
                return "Magic Sort: Extract Materia";

            case "Armoire":
                return "Magic Sort: Armoire";

            case "Glamour":
                return "Magic Sort: Glamour Dresser";

            default:
                return "Magic Sort: Keep";
        }
    }

    public MagicSortButtonState GetRetainerSortButtonState()
    {
        if (TryGetBusyButtonState(out var busyState))
        {
            return busyState;
        }

        var now = DateTime.UtcNow;
        var activeId = HoldingsService.GetActiveRetainerId();
        var armouryOpen = NativeUiHelpers.IsAddonOpenAndReady(gameGui, "ArmouryBoard");
        if (activeId == lastRetainerButtonActiveId
            && armouryOpen == lastRetainerArmouryOpen
            && (now - lastRetainerButtonUtc).TotalMilliseconds < ButtonStatePollMs)
        {
            return cachedRetainerButton;
        }

        var plan = retainerPlanner.BuildPlan(SafeTransactionQueue.MaxBatchSize, GetLatestManifest());
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
            cachedRetainerButton = new MagicSortButtonState(true, true, $"Magic Sort ({transferCount})", sb.ToString());
        }
        else if (plan.BlockedByFullRetainer > 0)
        {
            cachedRetainerButton = new MagicSortButtonState(
                true,
                false,
                "Retainer Full",
                $"This retainer is full (175/175). Open another retainer to store {plan.BlockedByFullRetainer} overflow item(s).");
        }
        else if (plan.BlockedByFullBag > 0)
        {
            cachedRetainerButton = new MagicSortButtonState(
                true,
                false,
                "Bag Full",
                $"Your inventory is full (140/140). Free bag space before withdrawing {plan.BlockedByFullBag} item(s).");
        }
        else
        {
            cachedRetainerButton = HiddenSortButton;
        }

        lastRetainerButtonActiveId = activeId;
        lastRetainerArmouryOpen = armouryOpen;
        lastRetainerButtonUtc = now;
        return cachedRetainerButton;
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
        var normalized = HoldingsService.NormalizeBaseItemId(baseItemId);
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

    public string BuildBellSortTooltip(IReadOnlyList<string> retainerNames)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Sort retainers: ").Append(string.Join(", ", retainerNames));

        var missingTracked = getMissingTrackedQuantities();
        if (missingTracked.Count > 0)
        {
            var pullCount = 0;
            foreach (var (trackedId, needQty) in missingTracked)
            {
                if (needQty > 0 && holdings.GetHoldingSummary(trackedId).TotalRetainerCount > 0)
                {
                    pullCount++;
                }
            }

            if (pullCount > 0)
            {
                sb.Append($"\n  - Withdraw {pullCount} tracked crafting material(s)");
            }
        }

        var known = holdings.GetKnownRetainersForCurrentCharacter();
        var funnelCount = 0;
        foreach (var r in known)
        {
            if (HasRetainerMisplacedWithdrawWork(r.RetainerId, out _))
            {
                funnelCount++;
            }
        }

        if (funnelCount > 0)
        {
            sb.Append("\n  - Consolidate misplaced specialty items across retainers");
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
    {
        var results = new List<MarketPlanItem>();
        var entries = GetLatestManifest();
        var manager = InventoryManager.Instance();
        var itemSheet = dataManager.GetExcelSheet<Item>();
        if (manager == null || itemSheet == null || entries.Count == 0)
        {
            return results;
        }

        var gearsetIds = safety.BuildGearsetItemIds();
        var unlockedTop = safety.BuildUnlockedJobTopIlvl();

        foreach (var bag in InventorySafetyEvaluator.PlayerBagContainers)
        {
            var container = manager->GetInventoryContainer(bag);
            if (!InventorySafetyEvaluator.IsContainerReadable(container))
            {
                continue;
            }

            for (var i = 0; i < container->Size; i++)
            {
                var slot = InventorySafetyEvaluator.GetSlot(container, i);
                if (slot == null || slot->ItemId == 0 || slot->Quantity <= 0)
                {
                    continue;
                }

                var rawId = slot->ItemId;
                var baseId = HoldingsService.NormalizeBaseItemId(rawId);
                if (!entries.TryGetValue(baseId, out var entry)
                    || !string.Equals(entry.Route, "Market", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var row = itemSheet.GetRowOrDefault(baseId);
                if (row is null || !safety.IsSafeToDisposeOrList(slot, row.Value, "Market", entries, gearsetIds, unlockedTop))
                {
                    continue;
                }

                var isHq = rawId >= 1_000_000 || slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
                var unitPrice = isHq
                    ? entry.TargetPriceHq
                    : (entry.TargetPriceNq > 0 ? entry.TargetPriceNq : entry.TargetPriceHq);
                var minAllowed = Math.Max(2L, row.Value.PriceLow + 1L);
                if (unitPrice < minAllowed || unitPrice > 999_999_999L)
                {
                    continue;
                }

                results.Add(new MarketPlanItem(
                    bag,
                    i,
                    rawId,
                    slot->Flags,
                    baseId,
                    row.Value.Name.ExtractText(),
                    slot->Quantity,
                    unitPrice));
            }
        }

        return results;
    }

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

        if (string.Equals(hoveredSellAddon, "Shop", StringComparison.Ordinal)
            && NativeUiHelpers.IsAddonOpenAndReady(gameGui, "Shop"))
        {
            foreach (var p in vendorPlanner.PlanSales(MaxTwoStepBatchItems, includeArmoury: false, entries))
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
            GetSlotsMatchingRouteFilter("Glamour", out var glamBags, out _, out var glamArmoury);
            foreach (var s in glamBags)
            {
                slots.Add(s);
            }

            foreach (var s in glamArmoury)
            {
                slots.Add(s);
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

                var baseId = HoldingsService.NormalizeBaseItemId(slot->ItemId);
                if (!CanLockOrTagBaseId(baseId))
                {
                    continue;
                }

                var itemRow = itemSheet.GetRowOrDefault(baseId);
                if (itemRow is null)
                {
                    continue;
                }

                string effective;
                ulong targetRetId = 0;
                if (InventorySafetyEvaluator.IsMateriaExtractable(itemRow.Value, slot))
                {
                    effective = "Materia";
                }
                else if (safety.IsItemTracked(baseId) || gearsetIds.Contains(baseId))
                {
                    effective = "Keep";
                }
                else if (!entries.TryGetValue(baseId, out var entry))
                {
                    effective = "Keep";
                }
                else if (string.Equals(entry.Route, "Keep", StringComparison.OrdinalIgnoreCase))
                {
                    effective = "Keep";
                }
                else
                {
                    effective = safety.ResolveEffectiveSlotRoute(entry, itemRow.Value, slot, entries, gearsetIds, unlockedTop) ?? "Keep";
                    targetRetId = entry.TargetRetainerId;
                }

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
    {
        var entries = GetLatestManifest();
        var manager = InventoryManager.Instance();
        var itemSheet = dataManager.GetExcelSheet<Item>();
        if (manager == null || itemSheet == null || entries.Count == 0)
        {
            return false;
        }

        var gearsetIds = safety.BuildGearsetItemIds();
        var unlockedTop = safety.BuildUnlockedJobTopIlvl();

        foreach (var bag in InventorySafetyEvaluator.PlayerBagContainers)
        {
            var container = manager->GetInventoryContainer(bag);
            if (!InventorySafetyEvaluator.IsContainerReadable(container))
            {
                continue;
            }

            for (var i = 0; i < container->Size; i++)
            {
                var slot = InventorySafetyEvaluator.GetSlot(container, i);
                if (slot == null || slot->ItemId == 0 || slot->Quantity <= 0)
                {
                    continue;
                }

                var baseId = HoldingsService.NormalizeBaseItemId(slot->ItemId);
                if (!entries.TryGetValue(baseId, out var entry)
                    || !string.Equals(entry.Route, "Market", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var row = itemSheet.GetRowOrDefault(baseId);
                if (row is not null
                    && safety.IsSafeToDisposeOrList(slot, row.Value, "Market", entries, gearsetIds, unlockedTop))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public bool HasPotentialBellWorkForRetainer(ulong retainerId, bool includeMarketWork)
    {
        if (retainerId == 0)
        {
            return false;
        }

        if (includeMarketWork && HasPendingBagMarketItems())
        {
            return true;
        }

        var missingTracked = getMissingTrackedQuantities();
        if (missingTracked.Count > 0)
        {
            foreach (var (trackedId, needQty) in missingTracked)
            {
                if (needQty <= 0)
                {
                    continue;
                }

                var summary = holdings.GetHoldingSummary(trackedId);
                foreach (var ret in summary.Retainers)
                {
                    if (ret.RetainerId == retainerId && ret.Quantity > 0)
                    {
                        return true;
                    }
                }
            }
        }

        var entries = GetLatestManifest();
        var manager = InventoryManager.Instance();
        var itemSheet = dataManager.GetExcelSheet<Item>();
        if (manager == null || itemSheet == null || entries.Count == 0)
        {
            return false;
        }

        var gearsetIds = safety.BuildGearsetItemIds();
        var unlockedTop = safety.BuildUnlockedJobTopIlvl();

        foreach (var bag in InventorySafetyEvaluator.PlayerBagContainers)
        {
            var container = manager->GetInventoryContainer(bag);
            if (!InventorySafetyEvaluator.IsContainerReadable(container))
            {
                continue;
            }

            for (var i = 0; i < container->Size; i++)
            {
                var slot = InventorySafetyEvaluator.GetSlot(container, i);
                if (slot == null || slot->ItemId == 0 || slot->Quantity <= 0)
                {
                    continue;
                }

                var baseId = HoldingsService.NormalizeBaseItemId(slot->ItemId);
                if (!InventorySafetyEvaluator.CanLockOrTagBaseId(baseId)
                    || gearsetIds.Contains(baseId)
                    || safety.IsItemTracked(baseId))
                {
                    continue;
                }

                if (!entries.TryGetValue(baseId, out var entry))
                {
                    continue;
                }

                var row = itemSheet.GetRowOrDefault(baseId);
                if (row is null || row.Value.ItemUICategory.RowId == 62 || row.Value.Rarity >= 4)
                {
                    continue;
                }

                var effRoute = safety.ResolveEffectiveSlotRoute(entry, row.Value, slot, entries, gearsetIds, unlockedTop);
                if (effRoute != "Retainer")
                {
                    continue;
                }

                var isHq = slot->ItemId >= 1_000_000 || slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
                var stackSize = (int)Math.Max(1, row.Value.StackSize);
                var isPrimary = entry.TargetRetainerId == retainerId
                    && !holdings.IsRetainerKnownFullFor(retainerId, baseId, isHq, stackSize);
                var isOverflow = entry.TargetRetainerId != 0
                    && entry.TargetRetainerId != retainerId
                    && holdings.IsRetainerKnownFullFor(entry.TargetRetainerId, baseId, isHq, stackSize)
                    && !holdings.IsRetainerKnownFullFor(retainerId, baseId, isHq, stackSize);
                if (isPrimary || isOverflow)
                {
                    return true;
                }
            }
        }

        if (HasRetainerMisplacedWithdrawWork(retainerId, out _))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Checks whether a retainer holds unlocked, non-gearset items whose assigned specialty/target
    /// belongs on a DIFFERENT non-full retainer (so Bell Sweep can pull them first and deposit next).
    /// </summary>
    public bool HasRetainerMisplacedWithdrawWork(ulong retainerId, out HashSet<ulong> destinationRetainerIds)
    {
        destinationRetainerIds = new HashSet<ulong>();
        var manager = InventoryManager.Instance();
        var itemSheet = dataManager.GetExcelSheet<Item>();
        var entries = GetLatestManifest();
        if (retainerId == 0 || manager == null || itemSheet == null || entries.Count == 0 || manager->GetEmptySlotsInBag() < 1)
        {
            return false;
        }

        var known = holdings.GetKnownRetainersForCurrentCharacter();
        KnownRetainerInfo? retInfo = null;
        foreach (var r in known)
        {
            if (r.RetainerId == retainerId)
            {
                retInfo = r;
                break;
            }
        }

        if (retInfo == null || retInfo.Quantities.Count == 0)
        {
            return false;
        }

        // Subtract player-bag items already queued for each destination retainer so we never trigger a funnel
        // visit when the target retainer will already be full after receiving bag deposits.
        var destFreeBudget = new Dictionary<ulong, int>();
        foreach (var bagType in InventorySafetyEvaluator.PlayerBagContainers)
        {
            var bag = manager->GetInventoryContainer(bagType);
            if (!InventorySafetyEvaluator.IsContainerReadable(bag))
            {
                continue;
            }

            for (var s = 0; s < bag->Size; s++)
            {
                var pSlot = InventorySafetyEvaluator.GetSlot(bag, s);
                if (pSlot == null || pSlot->ItemId == 0 || pSlot->Quantity <= 0)
                {
                    continue;
                }

                var pBase = HoldingsService.NormalizeBaseItemId(pSlot->ItemId);
                if (!entries.TryGetValue(pBase, out var pEntry)
                    || !string.Equals(pEntry.Route, "Retainer", StringComparison.OrdinalIgnoreCase)
                    || pEntry.TargetRetainerId == 0
                    || pEntry.TargetRetainerId == retainerId)
                {
                    continue;
                }

                var pRow = itemSheet.GetRowOrDefault(pBase);
                if (pRow is null)
                {
                    continue;
                }

                var pStack = (int)Math.Max(1, pRow.Value.StackSize);
                var pHq = pSlot->ItemId >= 1_000_000 || pSlot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
                if (pStack > 1 && holdings.HasPartialStackOnRetainer(pEntry.TargetRetainerId, pBase, pHq))
                {
                    continue;
                }

                if (!destFreeBudget.TryGetValue(pEntry.TargetRetainerId, out var rem))
                {
                    rem = holdings.GetKnownRetainerFreeSlots(pEntry.TargetRetainerId);
                }

                destFreeBudget[pEntry.TargetRetainerId] = Math.Max(0, rem - 1);
            }
        }

        var gearsetIds = safety.BuildGearsetItemIds();
        foreach (var (baseId, qty) in retInfo.Quantities)
        {
            if (qty <= 0
                || !InventorySafetyEvaluator.CanLockOrTagBaseId(baseId)
                || gearsetIds.Contains(baseId)
                || safety.IsItemTracked(baseId))
            {
                continue;
            }

            if (!entries.TryGetValue(baseId, out var entry)
                || !string.Equals(entry.Route, "Retainer", StringComparison.OrdinalIgnoreCase)
                || entry.TargetRetainerId == 0
                || entry.TargetRetainerId == retainerId)
            {
                continue;
            }

            var row = itemSheet.GetRowOrDefault(baseId);
            if (row is null || row.Value.ItemUICategory.RowId == 62 || row.Value.Rarity >= 4 || row.Value.IsUnique)
            {
                continue;
            }

            var stackSize = (int)Math.Max(1, row.Value.StackSize);
            var mergesIntoPartial = stackSize > 1
                && (holdings.HasPartialStackOnRetainer(entry.TargetRetainerId, baseId, isHq: false)
                    || holdings.HasPartialStackOnRetainer(entry.TargetRetainerId, baseId, isHq: true));
            if (mergesIntoPartial)
            {
                destinationRetainerIds.Add(entry.TargetRetainerId);
                continue;
            }

            if (!destFreeBudget.TryGetValue(entry.TargetRetainerId, out var remSlots))
            {
                remSlots = holdings.GetKnownRetainerFreeSlots(entry.TargetRetainerId);
            }

            if (remSlots > 0)
            {
                destFreeBudget[entry.TargetRetainerId] = remSlots - 1;
                destinationRetainerIds.Add(entry.TargetRetainerId);
            }
        }

        return destinationRetainerIds.Count > 0;
    }

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

        var now = DateTime.UtcNow;
        var sourceMask = marketPlanner.GetOpenMarketSourceMask();
        if (active->RetainerId == lastMarketButtonActiveId
            && sourceMask == lastMarketSourceMask
            && (now - lastMarketButtonUtc).TotalMilliseconds < ButtonStatePollMs)
        {
            return cachedMarketButton;
        }

        var entries = GetLatestManifest();
        var liveMarketCount = InventorySafetyEvaluator.GetLiveRetainerMarketCount(InventoryManager.Instance(), active);
        if ((sourceMask & 3) == 0)
        {
            cachedMarketButton = HiddenSortButton;
        }
        else if (liveMarketCount >= MarketListingPlanner.MaxRetainerMarketListings)
        {
            var hasWaiting = marketPlanner.PlanListings(1, entries).Count > 0
                || marketPlanner.CountArmouryMarketItems(1, entries) > 0;
            cachedMarketButton = hasWaiting
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
                cachedMarketButton = new MagicSortButtonState(
                    true,
                    true,
                    $"Magic Sell ({plans.Count})",
                    FormatMarketSellTooltip(plans));
            }
            else
            {
                var armouryMkt = marketPlanner.CountArmouryMarketItems(SafeTransactionQueue.MaxBatchSize, entries);
                cachedMarketButton = armouryMkt > 0
                    ? new MagicSortButtonState(
                        true,
                        false,
                        $"Armoury ({armouryMkt})",
                        $"Move {armouryMkt} market-ready gear item(s) from your Armoury Chest to your inventory first.")
                    : HiddenSortButton;
            }
        }

        lastMarketButtonActiveId = active->RetainerId;
        lastMarketSourceMask = sourceMask;
        lastMarketButtonUtc = now;
        return cachedMarketButton;
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

            var remainingToday = configuration.GetRemainingMarketActionsToday();
            if (remainingToday <= 0)
            {
                chatGui.Print($"Magic Sell: daily limit reached ({configuration.MarketDailyActionLimit} Market Board actions). List the rest manually or raise the limit in Settings.");
                return (Array.Empty<TransactionStep>(), false);
            }

            var perClick = configuration.MarketOneListingPerClick ? 1 : MaxThreeStepBatchItems;
            var batchLimit = Math.Min(Math.Min(perClick, availableSlots), remainingToday);
            var probeLimit = Math.Min(batchLimit + 1, availableSlots);
            var plans = marketPlanner.PlanListings(probeLimit, GetLatestManifest());
            var hasMore = plans.Count > batchLimit;
            if (hasMore)
            {
                plans.RemoveRange(batchLimit, plans.Count - batchLimit);
            }

            var steps = marketPlanner.BuildListingSteps(plans, GetLatestManifest);
            var counted = new List<TransactionStep>(steps.Count);
            foreach (var step in steps)
            {
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

        var now = DateTime.UtcNow;
        var armouryOpen = NativeUiHelpers.IsAddonOpenAndReady(gameGui, "ArmouryBoard");
        if (armouryOpen == lastVendorArmouryOpen
            && (now - lastVendorButtonUtc).TotalMilliseconds < ButtonStatePollMs)
        {
            return cachedVendorButton;
        }

        var entries = GetLatestManifest();
        var plans = vendorPlanner.PlanSales(MaxTwoStepBatchItems, armouryOpen, entries);
        if (plans.Count > 0)
        {
            cachedVendorButton = new MagicSortButtonState(
                true,
                true,
                $"Magic Sell ({plans.Count})",
                FormatVendorSellTooltip(plans));
        }
        else if (!armouryOpen)
        {
            var armouryCount = vendorPlanner.CountArmouryVendorItems(99, entries);
            cachedVendorButton = armouryCount > 0
                ? new MagicSortButtonState(
                    true,
                    false,
                    $"Armoury ({armouryCount})",
                    $"Open your Armoury Chest while talking to this vendor to sell {armouryCount} obsolete gear item(s).")
                : HiddenSortButton;
        }
        else
        {
            cachedVendorButton = HiddenSortButton;
        }

        lastVendorArmouryOpen = armouryOpen;
        lastVendorButtonUtc = now;
        return cachedVendorButton;
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

        var now = DateTime.UtcNow;
        var tab = supplyAddon->SelectedTab;
        var filter = supplyAddon->SelectedFilter;
        if (tab == lastGcSelectedTab
            && filter == lastGcSelectedFilter
            && (now - lastGcButtonUtc).TotalMilliseconds < ButtonStatePollMs)
        {
            return cachedGcButton;
        }

        var entries = GetLatestManifest();
        if (tab != 2)
        {
            var eligibleCount = grandCompanyPlanner.CountEligibleItemsInBagsAndArmoury(MaxTwoStepBatchItems, entries);
            cachedGcButton = eligibleCount == 0
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
                cachedGcButton = new MagicSortButtonState(
                    true,
                    true,
                    $"Magic Sell ({plans.Count})",
                    FormatGrandCompanyTooltip(plans));
            }
            else if (blockedBySealCap)
            {
                cachedGcButton = new MagicSortButtonState(
                    true,
                    false,
                    "Seal Cap",
                    "Company Seal cap reached. Spend seals before turning in more gear.");
            }
            else
            {
                cachedGcButton = HiddenSortButton;
            }
        }

        lastGcSelectedTab = tab;
        lastGcSelectedFilter = filter;
        lastGcButtonUtc = now;
        return cachedGcButton;
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

        var now = DateTime.UtcNow;
        if ((now - lastMateriaButtonUtc).TotalMilliseconds < ButtonStatePollMs)
        {
            return cachedMateriaButton;
        }

        var plans = materiaPlanner.PlanExtractions(SafeTransactionQueue.MaxBatchSize, out var blockedByFullBag);
        if (plans.Count > 0)
        {
            cachedMateriaButton = new MagicSortButtonState(
                true,
                true,
                $"Extract All ({plans.Count})",
                $"Extract Materia from {plans.Count} gear item(s) at 100% Spiritbond.");
        }
        else if (blockedByFullBag > 0)
        {
            cachedMateriaButton = new MagicSortButtonState(
                true,
                false,
                "Bag Full",
                "Your inventory is full (140/140). Free bag space before extracting Materia.");
        }
        else
        {
            cachedMateriaButton = HiddenSortButton;
        }

        lastMateriaButtonUtc = now;
        return cachedMateriaButton;
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

        var now = DateTime.UtcNow;
        if ((now - lastArmoireButtonUtc).TotalMilliseconds < ButtonStatePollMs)
        {
            return cachedArmoireButton;
        }

        lastArmoireButtonUtc = now;
        var plans = armoirePlanner.PlanStores(SafeTransactionQueue.MaxBatchSize, GetLatestManifest(), out var needsRepair, out var meldedSkipped);
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

            cachedArmoireButton = new MagicSortButtonState(true, true, $"Magic Store ({plans.Count})", sb.ToString());
        }
        else if (needsRepair > 0)
        {
            cachedArmoireButton = new MagicSortButtonState(
                true,
                false,
                "Repair First",
                $"{needsRepair} Armoire item(s) below 100% condition — repair before storing.");
        }
        else if (meldedSkipped > 0)
        {
            cachedArmoireButton = new MagicSortButtonState(
                true,
                false,
                "Unmeld First",
                $"{meldedSkipped} Armoire item(s) have Materia melded.");
        }
        else
        {
            cachedArmoireButton = HiddenSortButton;
        }

        return cachedArmoireButton;
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
        var now = DateTime.UtcNow;
        if ((now - lastGlamourButtonUtc).TotalMilliseconds < ButtonStatePollMs)
        {
            return cachedGlamourButton;
        }

        lastGlamourButtonUtc = now;
        GetSlotsMatchingRouteFilter("Glamour", out var bagSlots, out _, out var armSlots);
        var total = bagSlots.Count + armSlots.Count;
        if (total <= 0)
        {
            cachedGlamourButton = HiddenSortButton;
            return cachedGlamourButton;
        }

        var prismCount = 0;
        var mgr = InventoryManager.Instance();
        if (mgr != null)
        {
            prismCount = mgr->GetInventoryItemCount(21800, isHq: false, checkEquipped: false, checkArmory: false);
        }

        var usedText = string.Empty;
        var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMiragePrismPrismBox.Instance();
        if (agent != null && agent->Data != null)
        {
            usedText = $"\nDresser space: {agent->Data->UsedSlots}/800 | Glamour Prisms: {prismCount}";
        }

        cachedGlamourButton = new MagicSortButtonState(
            true,
            true,
            $"Glamour ({total})",
            $"Hover to highlight {total} Glamour Dresser item(s) across Bags & Armoury.{usedText}");
        return cachedGlamourButton;
    }

    public void ExecuteGlamourDresserNotice()
    {
        GetSlotsMatchingRouteFilter("Glamour", out var bagSlots, out _, out var armSlots);
        var total = bagSlots.Count + armSlots.Count;
        chatGui.Print($"Magic Inventory: {total} item(s) tagged for Glamour Dresser (highlighted on hover). Auto-store needs live callback verification first.");
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
