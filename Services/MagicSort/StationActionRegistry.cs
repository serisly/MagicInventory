using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using MagicInventory.Core;

namespace MagicInventory.Services.MagicSort;

internal sealed unsafe class StationActionContext
{
    public required IDalamudPluginInterface PluginInterface { get; init; }
    public required IGameGui GameGui { get; init; }
    public required Configuration Configuration { get; init; }
    public required HoldingsService Holdings { get; init; }
    public required RetainerSortPlanner RetainerPlanner { get; init; }
    public required MarketListingPlanner MarketPlanner { get; init; }
    public required VendorSellPlanner VendorPlanner { get; init; }
    public required GrandCompanyPlanner GrandCompanyPlanner { get; init; }
    public required MateriaExtractPlanner MateriaPlanner { get; init; }
    public required UseItemsPlanner UsePlanner { get; init; }
    public required InventorySortPlanner InventorySortPlanner { get; init; }
    public required ArmoireStorePlanner ArmoirePlanner { get; init; }
    public required GlamourStorePlanner GlamourPlanner { get; init; }
    public required Func<IReadOnlyDictionary<uint, MagicSortEntry>> GetLatestManifest { get; init; }
    public required Action InvalidateButtonCache { get; init; }
}

internal static unsafe class StationActionRegistry
{
    private const double ButtonStatePollMs = 250.0;
    private const int MaxTwoStepBatchItems = SafeTransactionQueue.MaxBatchSize / 2;
    private const int MaxThreeStepBatchItems = SafeTransactionQueue.MaxBatchSize / 3;
    private static readonly MagicSortButtonState HiddenSortButton = new(false, false, "Magic Sort");

    public static Dictionary<StationActionId, StationActionHandler> CreateAll(StationActionContext ctx)
    {
        var handlers = new[]
        {
            BuildUseAll(ctx),
            BuildInventorySort(ctx),
            BuildRetainerSort(ctx),
            BuildMarketSell(ctx),
            BuildVendorSell(ctx),
            BuildGrandCompany(ctx),
            BuildMateriaExtract(ctx),
            BuildArmoireStore(ctx),
            BuildGlamourStore(ctx),
        };

        return handlers.ToDictionary(static h => h.Id);
    }

    public static string FormatMarketPlanLine(MarketPlanItem p)
    {
        var lineTotal = p.UnitPrice * Math.Max(1, p.Quantity);
        var qtySuffix = p.Quantity > 1 ? $" x{p.Quantity}" : string.Empty;
        return $"{p.Name}{qtySuffix} (~{lineTotal:N0}g)";
    }

    public static RetainerManager.Retainer* GetActiveRetainer()
    {
        var rm = RetainerManager.Instance();
        var active = rm != null && rm->IsReady ? rm->GetActiveRetainer() : null;
        return active != null && active->RetainerId != 0 ? active : null;
    }

    private static StationActionHandler BuildUseAll(StationActionContext ctx) => new(
        StationActionId.UseAll,
        entries =>
        {
            var count = ctx.UsePlanner.CountPlannedUses(entries);
            return count > 0
                ? MagicSortButtonState.Enabled($"Use All ({count})", $"Use {count} unlockable / usable item(s) from your bags.")
                : HiddenSortButton;
        },
        entries =>
        {
            var steps = ctx.UsePlanner.BuildSteps(entries, out var usedCount, out var statusMsg);
            return new StationBatchPlan(
                steps,
                StartedMessage: usedCount > 0 ? $"Using {usedCount} item(s)..." : null,
                EmptyMessage: statusMsg);
        },
        highlightAddonKeys: ["InventoryExpansion", "InventoryLarge", "Inventory"],
        cacheTtlMs: ButtonStatePollMs)
    {
        CollectHighlightSlots = (entries, slots) =>
        {
            foreach (var slot in ctx.UsePlanner.GetPlannedUseSlots(entries))
            {
                slots.Add(slot);
            }
        },
    };

    private static StationActionHandler BuildInventorySort(StationActionContext ctx) => new(
        StationActionId.InventorySort,
        entries =>
        {
            var moves = ctx.InventorySortPlanner.PlanMoves(200, entries);
            if (moves.Count == 0)
            {
                return MagicSortButtonState.Enabled("Magic Sort", "Hover to preview where each item goes. Bags & Armoury are tidy.");
            }

            var batchNote = moves.Count > 20 ? " (moves 20 per click)" : string.Empty;
            var tooltip = StationActionHelpers.FormatBulletList(
                $"Sort {moves.Count} item(s) between Bags & Armoury Chest{batchNote}:",
                moves,
                8,
                static m =>
                {
                    var dir = m.Kind switch
                    {
                        InventorySortMoveKind.MergeStack => "merge stack",
                        InventorySortMoveKind.BagToArmoury => "to Armoury",
                        _ => "to Bag",
                    };
                    return $"{m.Name} ({dir})";
                });
            return MagicSortButtonState.Enabled($"Magic Sort ({moves.Count})", tooltip);
        },
        entries =>
        {
            var steps = ctx.InventorySortPlanner.BuildSteps(entries, out _, out var statusMsg);
            return new StationBatchPlan(steps, EmptyMessage: statusMsg);
        },
        highlightAddonKeys: ["Inventory:Sort"],
        cacheTtlMs: ButtonStatePollMs)
    {
        CollectHighlightSlots = (entries, slots) =>
        {
            foreach (var slot in ctx.InventorySortPlanner.GetPlannedMoveSlots(entries))
            {
                slots.Add(slot);
            }
        },
    };

    private static StationActionHandler BuildRetainerSort(StationActionContext ctx) => new(
        StationActionId.RetainerSort,
        entries =>
        {
            if (HoldingsService.GetActiveRetainerId() != 0)
            {
                ctx.Holdings.CaptureActiveRetainerIfOpen(true);
            }

            var plan = ctx.RetainerPlanner.BuildPlan(SafeTransactionQueue.MaxBatchSize, entries);
            if (plan.Steps.Count > 0)
            {
                var sb = new StringBuilder();
                if (plan.TrackedWithdrawCount > 0)
                {
                    sb.Append($"Withdraw {plan.TrackedWithdrawCount} tracked material(s):");
                    StationActionHelpers.AppendBulletLines(sb, plan.TrackedWithdrawSummary, 5, static s => s);
                }

                if (plan.DepositCount > 0)
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append($"Deposit {plan.DepositCount} matching item(s):");
                    StationActionHelpers.AppendBulletLines(sb, plan.DepositSummary, 5, static s => s);
                }

                if (plan.WithdrawCount > 0)
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append($"Withdraw {plan.WithdrawCount} Market / Vendor / misplaced item(s):");
                    StationActionHelpers.AppendBulletLines(sb, plan.SortWithdrawSummary, 5, static s => s);
                }

                return MagicSortButtonState.Enabled($"Magic Sort ({Math.Max(1, plan.TotalTransferCount)})", sb.ToString());
            }

            if (plan.BlockedByFullRetainer > 0)
            {
                return MagicSortButtonState.Disabled(
                    "Retainer Full",
                    $"This retainer is full (175/175). Open another retainer to store {plan.BlockedByFullRetainer} overflow item(s).");
            }

            if (plan.BlockedByFullBag > 0)
            {
                return MagicSortButtonState.Disabled(
                    "Bag Full",
                    $"Your inventory is full (140/140). Free bag space before withdrawing {plan.BlockedByFullBag} item(s).");
            }

            return HiddenSortButton;
        },
        entries =>
        {
            var plan = ctx.RetainerPlanner.BuildPlan(SafeTransactionQueue.MaxBatchSize, entries);
            return new StationBatchPlan(
                plan.Steps,
                plan.HasMoreRemaining,
                OnBatchFinished: () => ctx.Holdings.CaptureActiveRetainerIfOpen(true, force: true));
        },
        cacheTtlMs: ButtonStatePollMs)
    {
        ResolveCacheKey = () => new StationCacheKey(
            HoldingsService.GetActiveRetainerId(),
            NativeUiHelpers.IsAddonOpenAndReady(ctx.GameGui, "ArmouryBoard") ? 1 : 0),
    };

    private static StationActionHandler BuildMarketSell(StationActionContext ctx) => new(
        StationActionId.MarketSell,
        entries =>
        {
            var active = GetActiveRetainer();
            if (active == null || (ctx.MarketPlanner.GetOpenMarketSourceMask() & 3) == 0)
            {
                return HiddenSortButton;
            }

            var liveMarketCount = InventorySafetyEvaluator.GetLiveRetainerMarketCount(InventoryManager.Instance(), active);
            if (liveMarketCount >= MarketListingPlanner.MaxRetainerMarketListings)
            {
                var hasWaiting = ctx.MarketPlanner.PlanListings(1, entries).Count > 0
                    || ctx.MarketPlanner.CountArmouryMarketItems(1, entries) > 0;
                return hasWaiting
                    ? MagicSortButtonState.Disabled("Listings Full", "This retainer has 20/20 market listings. Switch retainers to list remaining items.")
                    : HiddenSortButton;
            }

            var batchLimit = Math.Min(MaxThreeStepBatchItems, MarketListingPlanner.MaxRetainerMarketListings - liveMarketCount);
            var plans = ctx.MarketPlanner.PlanListings(batchLimit, entries);
            if (plans.Count > 0)
            {
                var label = ctx.Configuration.MarketReviewBeforeConfirm
                    ? (plans.Count > 1 ? $"Prep Sell (1/{plans.Count})" : "Prep Sell (1)")
                    : $"Magic Sell ({plans.Count})";
                var tooltip = ctx.Configuration.MarketReviewBeforeConfirm
                    ? $"Review mode: opens & prefills 1 of {plans.Count} item(s) so you inspect before clicking Confirm.\n" + FormatMarketSellTooltip(plans)
                    : FormatMarketSellTooltip(plans);
                return MagicSortButtonState.Enabled(label, tooltip);
            }

            var armouryMkt = ctx.MarketPlanner.CountArmouryMarketItems(SafeTransactionQueue.MaxBatchSize, entries);
            return armouryMkt > 0
                ? MagicSortButtonState.Disabled($"Armoury ({armouryMkt})", $"Move {armouryMkt} market-ready gear item(s) from your Armoury Chest to your inventory first.")
                : HiddenSortButton;
        },
        entries =>
        {
            var active = GetActiveRetainer();
            if (active == null)
            {
                return StationBatchPlan.Empty;
            }

            var availableSlots = MarketListingPlanner.MaxRetainerMarketListings
                - InventorySafetyEvaluator.GetLiveRetainerMarketCount(InventoryManager.Instance(), active);
            if (availableSlots <= 0)
            {
                return new StationBatchPlan(Array.Empty<TransactionStep>(), EmptyMessage: "Market Board listing cap reached (20/20).");
            }

            var cid = InventorySafetyEvaluator.GetCurrentContentId();
            var reviewOnly = ctx.Configuration.MarketReviewBeforeConfirm;
            var remainingToday = ctx.Configuration.GetRemainingMarketActionsToday(cid);
            if (!reviewOnly && remainingToday <= 0)
            {
                return new StationBatchPlan(
                    Array.Empty<TransactionStep>(),
                    EmptyMessage: $"Magic Sell: daily limit reached ({ctx.Configuration.MarketDailyActionLimit} Market Board actions). List the rest manually or raise the limit in Settings.");
            }

            var perClick = (reviewOnly || ctx.Configuration.MarketOneListingPerClick) ? 1 : MaxThreeStepBatchItems;
            var batchLimit = reviewOnly
                ? Math.Min(1, availableSlots)
                : Math.Min(Math.Min(perClick, availableSlots), remainingToday);
            var probeLimit = Math.Min(batchLimit + 1, availableSlots);
            var plans = ctx.MarketPlanner.PlanListings(probeLimit, entries);
            var hasMore = StationActionHelpers.TrimToBatchLimit(plans, batchLimit);

            var steps = ctx.MarketPlanner.BuildListingSteps(
                plans,
                ctx.GetLatestManifest,
                reviewOnly: reviewOnly,
                onItemListed: reviewOnly
                    ? null
                    : () =>
                    {
                        ctx.Configuration.RecordMarketAction(cid);
                        ctx.Configuration.Save(ctx.PluginInterface);
                    });

            return new StationBatchPlan(steps, reviewOnly ? false : hasMore);
        },
        highlightAddonKeys: ["RetainerSellList"],
        cacheTtlMs: ButtonStatePollMs)
    {
        TryGetImmediateState = () =>
        {
            if (NativeUiHelpers.IsAddonOpenAndReady(ctx.GameGui, "RetainerSell"))
            {
                return MagicSortButtonState.Disabled("Reviewing...", "Finish or cancel the open Market listing window before preparing another item.");
            }

            if (GetActiveRetainer() == null)
            {
                return HiddenSortButton;
            }

            var cid = InventorySafetyEvaluator.GetCurrentContentId();
            if (!ctx.Configuration.MarketReviewBeforeConfirm && ctx.Configuration.GetRemainingMarketActionsToday(cid) <= 0)
            {
                return MagicSortButtonState.Disabled(
                    "Daily Limit",
                    $"Daily Market Board limit reached ({ctx.Configuration.GetUsedMarketActionsToday(cid)}/{ctx.Configuration.MarketDailyActionLimit}). Raise the limit in Settings or switch to Review Mode.");
            }

            return null;
        },
        ResolveCacheKey = () =>
        {
            var active = GetActiveRetainer();
            return new StationCacheKey(active != null ? active->RetainerId : 0UL, ctx.MarketPlanner.GetOpenMarketSourceMask());
        },
        CollectHighlightSlots = (entries, slots) =>
        {
            if (!NativeUiHelpers.IsAddonOpenAndReady(ctx.GameGui, "RetainerSellList"))
            {
                return;
            }

            var active = GetActiveRetainer();
            var liveCount = active != null
                ? InventorySafetyEvaluator.GetLiveRetainerMarketCount(InventoryManager.Instance(), active)
                : MarketListingPlanner.MaxRetainerMarketListings;
            if (active == null || liveCount >= MarketListingPlanner.MaxRetainerMarketListings)
            {
                return;
            }

            var limit = Math.Min(MaxThreeStepBatchItems, MarketListingPlanner.MaxRetainerMarketListings - liveCount);
            foreach (var p in ctx.MarketPlanner.PlanListings(limit, entries))
            {
                slots.Add((p.Container, p.Slot));
            }
        },
    };

    private static StationActionHandler BuildVendorSell(StationActionContext ctx) => new(
        StationActionId.VendorSell,
        entries =>
        {
            var armouryOpen = NativeUiHelpers.IsAddonOpenAndReady(ctx.GameGui, "ArmouryBoard");
            var plans = ctx.VendorPlanner.PlanSales(MaxTwoStepBatchItems, armouryOpen, entries);
            if (plans.Count > 0)
            {
                return MagicSortButtonState.Enabled($"Magic Sell ({plans.Count})", FormatVendorSellTooltip(plans));
            }

            if (!armouryOpen)
            {
                var armouryCount = ctx.VendorPlanner.CountArmouryVendorItems(99, entries);
                if (armouryCount > 0)
                {
                    return MagicSortButtonState.Disabled(
                        $"Armoury ({armouryCount})",
                        $"Open your Armoury Chest while talking to this vendor to sell {armouryCount} obsolete gear item(s).");
                }
            }

            return HiddenSortButton;
        },
        entries =>
        {
            var armouryOpen = NativeUiHelpers.IsAddonOpenAndReady(ctx.GameGui, "ArmouryBoard");
            var plans = ctx.VendorPlanner.PlanSales(MaxTwoStepBatchItems + 1, armouryOpen, entries);
            var hasMore = StationActionHelpers.TrimToBatchLimit(plans, MaxTwoStepBatchItems);
            return new StationBatchPlan(ctx.VendorPlanner.BuildSellSteps(plans, ctx.GetLatestManifest), hasMore);
        },
        highlightAddonKeys: ["Shop"],
        cacheTtlMs: ButtonStatePollMs)
    {
        ResolveCacheKey = () => new StationCacheKey(NativeUiHelpers.IsAddonOpenAndReady(ctx.GameGui, "ArmouryBoard") ? 1UL : 0UL),
        CollectHighlightSlots = (entries, slots) =>
        {
            if (!NativeUiHelpers.IsAddonOpenAndReady(ctx.GameGui, "Shop"))
            {
                return;
            }

            var includeArmoury = NativeUiHelpers.IsAddonOpenAndReady(ctx.GameGui, "ArmouryBoard");
            foreach (var p in ctx.VendorPlanner.PlanSales(MaxTwoStepBatchItems, includeArmoury, entries))
            {
                slots.Add((p.Container, p.Slot));
            }
        },
    };

    private static StationActionHandler BuildGrandCompany(StationActionContext ctx) => new(
        StationActionId.GrandCompany,
        entries =>
        {
            var supplyAddon = (AddonGrandCompanySupplyList*)NativeUiHelpers.GetReadyAddon(ctx.GameGui, "GrandCompanySupplyList");
            if (supplyAddon == null)
            {
                return HiddenSortButton;
            }

            if (supplyAddon->SelectedTab != 2)
            {
                var eligibleCount = ctx.GrandCompanyPlanner.CountEligibleItemsInBagsAndArmoury(
                    MaxTwoStepBatchItems,
                    entries,
                    supplyAddon->SelectedFilter,
                    out var atSealCap);
                if (eligibleCount == 0)
                {
                    return HiddenSortButton;
                }

                return atSealCap
                    ? MagicSortButtonState.Disabled("Seal Cap", "Company Seal cap reached. Spend seals before turning in more gear.")
                    : MagicSortButtonState.Enabled(
                        "Expert Delivery",
                        $"Switch to the Expert Delivery tab ({eligibleCount} unassigned gear item(s) eligible for Company Seals).");
            }

            var plans = ctx.GrandCompanyPlanner.PlanTurnIns(MaxTwoStepBatchItems, entries, out var blockedBySealCap);
            if (plans.Count > 0)
            {
                return MagicSortButtonState.Enabled($"Magic Sell ({plans.Count})", FormatGrandCompanyTooltip(plans));
            }

            return blockedBySealCap
                ? MagicSortButtonState.Disabled("Seal Cap", "Company Seal cap reached. Spend seals before turning in more gear.")
                : HiddenSortButton;
        },
        entries =>
        {
            var supplyAddon = (AddonGrandCompanySupplyList*)NativeUiHelpers.GetReadyAddon(ctx.GameGui, "GrandCompanySupplyList");
            var ps = PlayerState.Instance();
            var gc = ps != null ? ps->GrandCompany : (byte)0;
            if (supplyAddon == null || supplyAddon->SelectedTab != 2 || gc == 0)
            {
                return StationBatchPlan.Empty;
            }

            var plans = ctx.GrandCompanyPlanner.PlanTurnIns(MaxTwoStepBatchItems + 1, entries, out _);
            var hasMore = StationActionHelpers.TrimToBatchLimit(plans, MaxTwoStepBatchItems);
            return new StationBatchPlan(ctx.GrandCompanyPlanner.BuildTurnInSteps(plans, gc, ctx.GetLatestManifest), hasMore);
        },
        highlightAddonKeys: ["GrandCompanySupplyList"],
        cacheTtlMs: ButtonStatePollMs)
    {
        TryGetImmediateState = () =>
            NativeUiHelpers.GetReadyAddon(ctx.GameGui, "GrandCompanySupplyList") == null ? HiddenSortButton : null,
        ResolveCacheKey = () =>
        {
            var supplyAddon = (AddonGrandCompanySupplyList*)NativeUiHelpers.GetReadyAddon(ctx.GameGui, "GrandCompanySupplyList");
            return supplyAddon != null
                ? new StationCacheKey((ulong)supplyAddon->SelectedTab, supplyAddon->SelectedFilter, supplyAddon->SelectedSortBy)
                : default;
        },
        TryPreExecute = () =>
        {
            var supplyAddon = (AddonGrandCompanySupplyList*)NativeUiHelpers.GetReadyAddon(ctx.GameGui, "GrandCompanySupplyList");
            if (supplyAddon == null || supplyAddon->SelectedTab == 2)
            {
                return false;
            }

            if (supplyAddon->ExpertDeliveryRadioButton != null)
            {
                NativeUiHelpers.ClickAddonButton(
                    (AtkUnitBase*)supplyAddon,
                    (AtkComponentButton*)supplyAddon->ExpertDeliveryRadioButton);
                ctx.InvalidateButtonCache();
            }

            return true;
        },
        CollectHighlightSlots = (entries, slots) =>
        {
            var gcAddon = (AddonGrandCompanySupplyList*)NativeUiHelpers.GetReadyAddon(ctx.GameGui, "GrandCompanySupplyList");
            if (gcAddon == null || gcAddon->SelectedTab != 2)
            {
                return;
            }

            foreach (var p in ctx.GrandCompanyPlanner.PlanTurnIns(MaxTwoStepBatchItems, entries, out _))
            {
                slots.Add((p.Container, p.Slot));
            }
        },
    };

    private static StationActionHandler BuildMateriaExtract(StationActionContext ctx) => new(
        StationActionId.MateriaExtract,
        manifest =>
        {
            var plans = ctx.MateriaPlanner.PlanExtractions(SafeTransactionQueue.MaxBatchSize, out var blockedByFullBag);
            if (plans.Count > 0 && ctx.MateriaPlanner.IsBlockedByMount)
            {
                return MagicSortButtonState.Disabled("Dismount", $"Dismount before extracting Materia from {plans.Count} gear item(s).");
            }

            if (plans.Count > 0)
            {
                return MagicSortButtonState.Enabled($"Extract All ({plans.Count})", $"Extract Materia from {plans.Count} gear item(s) at 100% Spiritbond.");
            }

            if (blockedByFullBag > 0)
            {
                return MagicSortButtonState.Disabled("Bag Full", "Your inventory is full (140/140). Free bag space before extracting Materia.");
            }

            return HiddenSortButton;
        },
        manifest =>
        {
            var plans = ctx.MateriaPlanner.PlanExtractions(SafeTransactionQueue.MaxBatchSize + 1, out _);
            var hasMore = StationActionHelpers.TrimToBatchLimit(plans, SafeTransactionQueue.MaxBatchSize);
            return new StationBatchPlan(ctx.MateriaPlanner.BuildExtractionSteps(plans), hasMore);
        },
        cacheTtlMs: ButtonStatePollMs);

    private static StationActionHandler BuildArmoireStore(StationActionContext ctx) => new(
        StationActionId.ArmoireStore,
        entries =>
        {
            var plans = ctx.ArmoirePlanner.PlanStores(SafeTransactionQueue.MaxBatchSize, entries, out var needsRepair, out var meldedSkipped);
            if (plans.Count > 0)
            {
                var footer = needsRepair > 0 ? $"({needsRepair} skipped, repair to 100% first)" : null;
                var tooltip = StationActionHelpers.FormatBulletList(
                    $"Store {plans.Count} item(s) in your Armoire:",
                    plans,
                    10,
                    static p => p.Name,
                    footer);
                return MagicSortButtonState.Enabled($"Magic Store ({plans.Count})", tooltip);
            }

            if (needsRepair > 0)
            {
                return MagicSortButtonState.Disabled("Repair First", $"{needsRepair} Armoire item(s) below 100% condition, repair before storing.");
            }

            if (meldedSkipped > 0)
            {
                return MagicSortButtonState.Disabled("Unmeld First", $"{meldedSkipped} Armoire item(s) have Materia melded.");
            }

            return HiddenSortButton;
        },
        entries =>
        {
            var plans = ctx.ArmoirePlanner.PlanStores(SafeTransactionQueue.MaxBatchSize + 1, entries, out var needsRepair, out _);
            var hasMore = StationActionHelpers.TrimToBatchLimit(plans, SafeTransactionQueue.MaxBatchSize);
            var emptyMsg = plans.Count == 0 && needsRepair > 0
                ? $"Magic Inventory: {needsRepair} Armoire item(s) need to be repaired to 100% first."
                : null;
            return new StationBatchPlan(ctx.ArmoirePlanner.BuildStoreSteps(plans, ctx.InvalidateButtonCache), hasMore, EmptyMessage: emptyMsg);
        },
        highlightAddonKeys: ["Cabinet"],
        cacheTtlMs: ButtonStatePollMs)
    {
        CollectHighlightSlots = (entries, slots) =>
        {
            if (!NativeUiHelpers.IsAddonOpenAndReady(ctx.GameGui, "Cabinet"))
            {
                return;
            }

            foreach (var p in ctx.ArmoirePlanner.PlanStores(SafeTransactionQueue.MaxBatchSize, entries, out _, out _))
            {
                slots.Add((p.Container, p.Slot));
            }
        },
    };

    private static StationActionHandler BuildGlamourStore(StationActionContext ctx) => new(
        StationActionId.GlamourStore,
        entries =>
        {
            var plans = ctx.GlamourPlanner.PlanStores(SafeTransactionQueue.MaxBatchSize, entries, out var blockReason);
            if (plans.Count > 0)
            {
                var tooltip = StationActionHelpers.FormatBulletList(
                    $"Store {plans.Count} item(s) in your Glamour Dresser (1 Glamour Prism each):",
                    plans,
                    10,
                    static p => p.Name);
                return MagicSortButtonState.Enabled($"Magic Store ({plans.Count})", tooltip);
            }

            return !string.IsNullOrEmpty(blockReason)
                ? MagicSortButtonState.Disabled("Magic Store", blockReason)
                : HiddenSortButton;
        },
        entries =>
        {
            var plans = ctx.GlamourPlanner.PlanStores(SafeTransactionQueue.MaxBatchSize + 1, entries, out var blockReason);
            var hasMore = StationActionHelpers.TrimToBatchLimit(plans, SafeTransactionQueue.MaxBatchSize);
            var emptyMsg = plans.Count == 0 && !string.IsNullOrEmpty(blockReason)
                ? $"Magic Inventory: {blockReason}"
                : null;
            return new StationBatchPlan(ctx.GlamourPlanner.BuildStoreSteps(plans, ctx.InvalidateButtonCache), hasMore, EmptyMessage: emptyMsg);
        },
        highlightAddonKeys: ["MiragePrismPrismBoxCrystallize"],
        cacheTtlMs: ButtonStatePollMs)
    {
        CollectHighlightSlots = (entries, slots) =>
        {
            if (!NativeUiHelpers.IsAddonOpenAndReady(ctx.GameGui, "MiragePrismPrismBoxCrystallize"))
            {
                return;
            }

            foreach (var p in ctx.GlamourPlanner.PlanStores(SafeTransactionQueue.MaxBatchSize, entries, out _))
            {
                slots.Add((p.Container, p.Slot));
            }
        },
    };

    private static string FormatMarketSellTooltip(IReadOnlyList<MarketPlanItem> plans)
    {
        long total = 0;
        foreach (var p in plans)
        {
            total += p.UnitPrice * Math.Max(1, p.Quantity);
        }

        return StationActionHelpers.FormatBulletList(
            "Items to list on the Market Board:",
            plans,
            10,
            FormatMarketPlanLine,
            $"Estimated Total: ~{total:N0}g");
    }

    private static string FormatVendorSellTooltip(IReadOnlyList<VendorPlanItem> plans)
    {
        long total = 0;
        foreach (var p in plans)
        {
            total += (long)p.NpcUnitPrice * Math.Max(1, p.Quantity);
        }

        return StationActionHelpers.FormatBulletList(
            "Items to sell to this vendor:",
            plans,
            10,
            static p =>
            {
                var lineTotal = (long)p.NpcUnitPrice * Math.Max(1, p.Quantity);
                var qtySuffix = p.Quantity > 1 ? $" x{p.Quantity}" : string.Empty;
                return $"{p.Name}{qtySuffix} ({lineTotal:N0}g)";
            },
            $"Total Vendor Gil: {total:N0}g");
    }

    private static string FormatGrandCompanyTooltip(IReadOnlyList<GrandCompanyPlanItem> plans)
    {
        long total = 0;
        foreach (var p in plans)
        {
            total += p.SealReward;
        }

        return StationActionHelpers.FormatBulletList(
            "Gear to deliver for Company Seals:",
            plans,
            10,
            static p => $"{p.Name} (+{p.SealReward:N0} seals)",
            $"Total Seals: +{total:N0}");
    }
}
