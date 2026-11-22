using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using MagicInventory.Core;
using MagicInventory.Services.Triage;

namespace MagicInventory.Services.MagicSort;

public enum StaleListingSeverity
{
    UnrealisticRemove = 0,
    OverpricedReprice = 1,
}

public sealed record ActiveListingAuditItem(
    ulong RetainerId,
    int MarketSlot,
    uint RawItemId,
    uint BaseItemId,
    InventoryItem.ItemFlags Flags,
    bool IsHq,
    bool IsUnique,
    int Quantity,
    int StackSize,
    string Name,
    long ListedUnitPrice,
    long NpcPriceLow,
    StaleListingSeverity Severity,
    string Reason,
    MarketViabilityResult Viability,
    TimeSpan SnapshotAge);

public sealed unsafe class ActiveListingAuditor
{
    private const double PollIntervalMs = 500.0;

    private readonly IDataManager dataManager;
    private readonly IGameGui gameGui;
    private readonly IChatGui chatGui;
    private readonly Configuration configuration;
    private readonly UniversalisClient universalis;
    private readonly ItemTriageEngine triageEngine;
    private readonly SafeTransactionQueue transactionQueue;

    private readonly HashSet<(ulong RetainerId, int Slot, uint RawItemId, long ListedPrice)> dismissed = new();
    private readonly List<ActiveListingAuditItem> cachedFindings = new();
    private ulong cachedRetainerId;
    private DateTime lastAuditUtc = DateTime.MinValue;

    public ActiveListingAuditor(
        IDataManager dataManager,
        IGameGui gameGui,
        IChatGui chatGui,
        Configuration configuration,
        UniversalisClient universalis,
        ItemTriageEngine triageEngine,
        SafeTransactionQueue transactionQueue)
    {
        this.dataManager = dataManager;
        this.gameGui = gameGui;
        this.chatGui = chatGui;
        this.configuration = configuration;
        this.universalis = universalis;
        this.triageEngine = triageEngine;
        this.transactionQueue = transactionQueue;
    }

    public bool IsBusy => transactionQueue.IsBusy;

    public void Invalidate() => lastAuditUtc = DateTime.MinValue;

    public void DismissForSession(ActiveListingAuditItem item)
    {
        dismissed.Add((item.RetainerId, item.MarketSlot, item.RawItemId, item.ListedUnitPrice));
        lastAuditUtc = DateTime.MinValue;
    }

    public IReadOnlyList<ActiveListingAuditItem> GetCurrentRetainerFindings()
    {
        if (!configuration.MarketPromptStaleListings
            || !NativeUiHelpers.IsAddonOpenAndReady(gameGui, "RetainerSellList"))
        {
            return Array.Empty<ActiveListingAuditItem>();
        }

        var activeId = HoldingsService.GetActiveRetainerId();
        if (activeId == 0)
        {
            return Array.Empty<ActiveListingAuditItem>();
        }

        var now = DateTime.UtcNow;
        if (activeId == cachedRetainerId && (now - lastAuditUtc).TotalMilliseconds < PollIntervalMs)
        {
            return cachedFindings;
        }

        cachedRetainerId = activeId;
        lastAuditUtc = now;
        cachedFindings.Clear();

        var manager = InventoryManager.Instance();
        var itemSheet = dataManager.GetExcelSheet<Item>();
        if (manager == null || itemSheet == null)
        {
            return cachedFindings;
        }

        var marketContainer = manager->GetInventoryContainer(InventoryType.RetainerMarket);
        if (!InventorySafetyEvaluator.IsContainerReadable(marketContainer))
        {
            return cachedFindings;
        }

        var world = triageEngine.ActiveWorldName;
        var pricesSpan = manager->RetainerMarketPrices;
        var missingIds = new List<uint>();

        for (var slotIdx = 0; slotIdx < marketContainer->Size; slotIdx++)
        {
            var slot = InventorySafetyEvaluator.GetSlot(marketContainer, slotIdx);
            if (slot == null || slot->ItemId == 0 || slot->Quantity <= 0)
            {
                continue;
            }

            var rawId = slot->ItemId;
            var baseId = ItemIdentity.NormalizeBaseItemId(rawId);
            if (baseId == 0)
            {
                continue;
            }

            var row = itemSheet.GetRowOrDefault(baseId);
            if (row is null)
            {
                continue;
            }

            var listedPrice = slotIdx < pricesSpan.Length ? (long)pricesSpan[slotIdx] : 0L;
            if (dismissed.Contains((activeId, slotIdx, rawId, listedPrice)))
            {
                continue;
            }

            MarketPriceSnapshot? mkt = null;
            var hasFresh = false;
            if (!string.IsNullOrWhiteSpace(world))
            {
                if (universalis.TryGetFreshPrice(world, baseId, out var fresh))
                {
                    mkt = fresh;
                    hasFresh = true;
                }
                else
                {
                    missingIds.Add(baseId);
                }
            }

            if (mkt == null || !hasFresh)
            {
                continue;
            }

            var isHq = ItemIdentity.IsHighQuality(slot);
            var viability = MarketViabilityEvaluator.Evaluate(mkt, isHq, slot->Quantity, row.Value.PriceLow, configuration, now);
            if (!viability.HasData)
            {
                continue;
            }

            if (TryClassifyFinding(viability, listedPrice, slot->Quantity, row.Value.PriceLow, out var severity, out var reason))
            {
                var snapAge = now > mkt.FetchedUtc ? now - mkt.FetchedUtc : TimeSpan.Zero;
                cachedFindings.Add(new ActiveListingAuditItem(
                    activeId,
                    slotIdx,
                    rawId,
                    baseId,
                    slot->Flags,
                    isHq,
                    row.Value.IsUnique,
                    slot->Quantity,
                    (int)Math.Max(1, row.Value.StackSize),
                    row.Value.Name.ExtractText(),
                    listedPrice,
                    row.Value.PriceLow,
                    severity,
                    reason,
                    viability,
                    snapAge));
            }
        }

        if (missingIds.Count > 0 && !string.IsNullOrWhiteSpace(world))
        {
            universalis.QueueMissing(world, missingIds);
        }

        cachedFindings.Sort((a, b) =>
        {
            var sevCmp = a.Severity.CompareTo(b.Severity);
            return sevCmp != 0 ? sevCmp : b.ListedUnitPrice.CompareTo(a.ListedUnitPrice);
        });

        return cachedFindings;
    }

    public bool TryClassifyFinding(
        MarketViabilityResult v,
        long listedUnitPrice,
        int quantity,
        long npcPriceLow,
        out StaleListingSeverity severity,
        out string reason)
    {
        severity = StaleListingSeverity.UnrealisticRemove;
        reason = string.Empty;

        if (v.IsDeadOrIlliquid)
        {
            severity = StaleListingSeverity.UnrealisticRemove;
            reason = v.Reason;
            return true;
        }

        var breakeven = MarketViabilityEvaluator.ComputeVendorBreakevenPrice(npcPriceLow);
        if ((npcPriceLow > 0 && v.RealisticUnitPrice < breakeven)
            || (!v.IsRealisticToSell
                && v.RealisticStackValue < configuration.VendorMaxStackValue
                && v.RealisticUnitPrice < configuration.MarketMinUnitPrice))
        {
            severity = StaleListingSeverity.UnrealisticRemove;
            reason = npcPriceLow > 0 && v.RealisticUnitPrice < breakeven
                ? $"MB (~{v.RealisticUnitPrice:N0}g) nets <= vendor ({npcPriceLow:N0}g)"
                : $"Market dropped (~{v.RealisticUnitPrice:N0}g ea)";
            return true;
        }

        var safeQty = Math.Max(1, quantity);
        if (listedUnitPrice > 0
            && v.SuggestedUndercutPrice >= 2
            && v.SuggestedUndercutPrice < listedUnitPrice)
        {
            var unitDelta = listedUnitPrice - v.SuggestedUndercutPrice;
            var stackDelta = unitDelta * safeQty;
            if (unitDelta >= 500 || stackDelta >= 5_000)
            {
                var refPrice = v.ServerCheapest > 0 ? v.ServerCheapest : v.SoldMedian;
                var pctThreshold = 1.0 + Math.Clamp(configuration.StaleListingUndercutPct, 5, 200) / 100.0;
                if (refPrice > 0
                    && listedUnitPrice >= refPrice * pctThreshold
                    && ((listedUnitPrice - refPrice) >= 500 || (listedUnitPrice - refPrice) * safeQty >= 5_000))
                {
                    var pctAbove = (int)Math.Round(((listedUnitPrice - refPrice) / (double)refPrice) * 100.0);
                    severity = StaleListingSeverity.OverpricedReprice;
                    reason = $"Overpriced (+{pctAbove}% vs est. {refPrice:N0}g)";
                    return true;
                }

                if (v.SoldMedian > 0
                    && listedUnitPrice > v.SoldMedian * 2.5
                    && ((listedUnitPrice - v.SoldMedian) >= 500 || (listedUnitPrice - v.SoldMedian) * safeQty >= 5_000))
                {
                    severity = StaleListingSeverity.OverpricedReprice;
                    reason = $"Far above sold median ({v.SoldMedian:N0}g)";
                    return true;
                }
            }
        }

        return false;
    }

    private static bool PlayerAlreadyOwnsUniqueItem(InventoryManager* mgr, uint baseItemId)
    {
        if (mgr == null || baseItemId == 0)
        {
            return false;
        }

        foreach (var cType in InventorySafetyEvaluator.MaterializeSourceContainers)
        {
            var container = mgr->GetInventoryContainer(cType);
            if (!InventorySafetyEvaluator.IsContainerReadable(container))
            {
                continue;
            }

            for (var i = 0; i < container->Size; i++)
            {
                var slot = InventorySafetyEvaluator.GetSlot(container, i);
                if (slot != null && slot->Quantity > 0 && ItemIdentity.NormalizeBaseItemId(slot->ItemId) == baseItemId)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static int FindLiveMarketSlot(
        InventoryManager* mgr,
        int preferredSlot,
        uint rawItemId,
        InventoryItem.ItemFlags expectedFlags,
        int expectedQty,
        long expectedListedPrice)
    {
        var container = mgr->GetInventoryContainer(InventoryType.RetainerMarket);
        if (!InventorySafetyEvaluator.IsContainerReadable(container))
        {
            return -1;
        }

        bool Matches(int idx)
        {
            var s = InventorySafetyEvaluator.GetSlot(container, idx);
            if (s == null || s->ItemId != rawItemId || s->Flags != expectedFlags || s->Quantity != expectedQty)
            {
                return false;
            }

            var livePrices = mgr->RetainerMarketPrices;
            if (expectedListedPrice > 0 && idx < livePrices.Length && (long)livePrices[idx] != expectedListedPrice)
            {
                return false;
            }

            return true;
        }

        if (preferredSlot >= 0 && preferredSlot < container->Size && Matches(preferredSlot))
        {
            return preferredSlot;
        }

        for (var i = 0; i < container->Size; i++)
        {
            if (Matches(i))
            {
                return i;
            }
        }

        return -1;
    }

    private static int CountLiveMarketItems(InventoryManager* mgr)
    {
        var container = mgr->GetInventoryContainer(InventoryType.RetainerMarket);
        if (!InventorySafetyEvaluator.IsContainerReadable(container))
        {
            return 0;
        }

        var count = 0;
        for (var i = 0; i < container->Size; i++)
        {
            var s = InventorySafetyEvaluator.GetSlot(container, i);
            if (s != null && s->ItemId != 0 && s->Quantity > 0)
            {
                count++;
            }
        }

        return count;
    }

    public void QueueRepriceReview(ActiveListingAuditItem item)
    {
        if (transactionQueue.IsBusy)
        {
            chatGui.Print("Wait for the active operation to finish first.");
            return;
        }

        if (NativeUiHelpers.IsAddonOpenAndReady(gameGui, "RetainerSell"))
        {
            chatGui.Print("Close the open price window before repricing another listing.");
            return;
        }

        var targetPrice = (int)Math.Clamp(item.Viability.SuggestedUndercutPrice, 2L, 999_999_999L);
        var activeId = item.RetainerId;
        var slotIdx = item.MarketSlot;
        var rawId = item.RawItemId;
        var expectedFlags = item.Flags;
        var expectedHq = item.IsHq;
        var itemName = item.Name;
        var autoCompare = configuration.MarketAutoComparePricesInReview;

        var steps = new List<TransactionStep>(2)
        {
            new TransactionStep(
                $"Open {itemName} price editor",
                "RetainerSellList",
                Dispatch: () =>
                {
                    if (NativeUiHelpers.IsAddonOpenAndReady(gameGui, "RetainerSell")
                        || HoldingsService.GetActiveRetainerId() != activeId)
                    {
                        return false;
                    }

                    var mgr = InventoryManager.Instance();
                    var liveSlot = mgr != null ? FindLiveMarketSlot(mgr, slotIdx, rawId, expectedFlags, item.Quantity, item.ListedUnitPrice) : -1;
                    if (liveSlot < 0)
                    {
                        return false;
                    }

                    var agent = AgentRetainer.Instance();
                    if (agent == null)
                    {
                        return false;
                    }

                    agent->OpenRetainerSell(InventoryType.RetainerMarket, (ushort)liveSlot);
                    return true;
                },
                HasServerAck: () =>
                {
                    var sellAddon = (AddonRetainerSell*)NativeUiHelpers.GetReadyAddon(gameGui, "RetainerSell");
                    return sellAddon != null && RetainerSellAddonHelper.MatchesItem(sellAddon, itemName, expectedHq);
                },
                PacingProfile.MarketFormEntry,
                SecondaryRequiredAddon: "RetainerSell",
                SkipOnFailureCount: 1),
            new TransactionStep(
                $"Prefill {itemName} at {targetPrice:N0}g",
                "RetainerSell",
                Dispatch: () =>
                {
                    var sellAddon = (AddonRetainerSell*)NativeUiHelpers.GetReadyAddon(gameGui, "RetainerSell");
                    if (sellAddon == null
                        || sellAddon->AskingPrice == null
                        || !RetainerSellAddonHelper.MatchesItem(sellAddon, itemName, expectedHq))
                    {
                        return false;
                    }

                    if (targetPrice >= 2)
                    {
                        sellAddon->AskingPrice->SetValue(targetPrice);
                    }

                    if (autoCompare)
                    {
                        RetainerSellAddonHelper.TryClickComparePrices(sellAddon);
                    }

                    return true;
                },
                HasServerAck: () =>
                {
                    var sellAddon = (AddonRetainerSell*)NativeUiHelpers.GetReadyAddon(gameGui, "RetainerSell");
                    return sellAddon != null
                        && RetainerSellAddonHelper.MatchesItem(sellAddon, itemName, expectedHq)
                        && (targetPrice < 2
                            || (RetainerSellAddonHelper.TryReadAskingPrice(sellAddon, out var livePrice) && livePrice == targetPrice));
                },
                PacingProfile.MarketFormEntry,
                SecondaryRequiredAddon: "RetainerSellList"),
        };

        if (!transactionQueue.TryEnqueueBatch(steps, out var failReason, Invalidate) && !string.IsNullOrWhiteSpace(failReason))
        {
            chatGui.Print(failReason);
        }
    }

    public void QueueRemoveListings(IReadOnlyList<ActiveListingAuditItem> items)
    {
        if (transactionQueue.IsBusy)
        {
            chatGui.Print("Wait for the active operation to finish first.");
            return;
        }

        if (NativeUiHelpers.IsAddonOpenAndReady(gameGui, "RetainerSell"))
        {
            chatGui.Print("Close the open price window before removing listings.");
            return;
        }

        if (items.Count == 0)
        {
            return;
        }

        var count = Math.Min(items.Count, SafeTransactionQueue.MaxBatchSize);
        var steps = new List<TransactionStep>(count);
        for (var i = 0; i < count; i++)
        {
            var captured = items[i];
            var preMarketCount = 0;
            steps.Add(new TransactionStep(
                $"Remove stale listing: {captured.Name}",
                "RetainerSellList",
                Dispatch: () =>
                {
                    if (NativeUiHelpers.IsAddonOpenAndReady(gameGui, "RetainerSell")
                        || HoldingsService.GetActiveRetainerId() != captured.RetainerId)
                    {
                        return false;
                    }

                    var mgr = InventoryManager.Instance();
                    if (mgr == null)
                    {
                        return false;
                    }

                    var liveSlot = FindLiveMarketSlot(mgr, captured.MarketSlot, captured.RawItemId, captured.Flags, captured.Quantity, captured.ListedUnitPrice);
                    if (liveSlot < 0)
                    {
                        return false;
                    }

                    var src = InventorySafetyEvaluator.GetSlot(mgr, InventoryType.RetainerMarket, liveSlot);
                    if (src == null || src->Quantity <= 0)
                    {
                        return false;
                    }

                    preMarketCount = CountLiveMarketItems(mgr);
                    var qty = (uint)src->Quantity;
                    var isCrystal = ItemIdentity.IsCrystalItemId(captured.BaseItemId);
                    var canMoveToPlayer = isCrystal
                        ? HasCrystalSlotRoom(mgr, InventoryType.Crystals, captured.BaseItemId, src->Quantity)
                        : (!captured.IsUnique || !PlayerAlreadyOwnsUniqueItem(mgr, captured.BaseItemId))
                            && InventorySafetyEvaluator.FindDestinationSlot(
                                mgr,
                                InventorySafetyEvaluator.PlayerBagContainers,
                                captured.RawItemId,
                                captured.Flags,
                                captured.StackSize,
                                out _,
                                out _,
                                routeCrystalsToCrystalBag: false,
                                allowPartialMerge: false,
                                incomingQty: src->Quantity);

                    if (canMoveToPlayer)
                    {
                        mgr->MoveFromRetainerMarketToPlayerInventory(InventoryType.RetainerMarket, (ushort)liveSlot, qty);
                        return true;
                    }

                    var canMoveToRetainer = isCrystal
                        ? HasCrystalSlotRoom(mgr, InventoryType.RetainerCrystals, captured.BaseItemId, src->Quantity)
                        : (!captured.IsUnique || !RetainerAlreadyOwnsUniqueItem(mgr, captured.BaseItemId))
                            && InventorySafetyEvaluator.FindDestinationSlot(
                                mgr,
                                InventorySafetyEvaluator.RetainerBagContainers,
                                captured.RawItemId,
                                captured.Flags,
                                captured.StackSize,
                                out _,
                                out _,
                                routeCrystalsToCrystalBag: false,
                                allowPartialMerge: false,
                                incomingQty: src->Quantity);

                    if (canMoveToRetainer)
                    {
                        mgr->MoveFromRetainerMarketToRetainerInventory(InventoryType.RetainerMarket, (ushort)liveSlot, qty);
                        return true;
                    }

                    transactionQueue.Cancel("No free bag or retainer slot to remove listing.", QueueBatchOutcome.AbortedOnError);
                    return false;
                },
                HasServerAck: () =>
                {
                    var mgr = InventoryManager.Instance();
                    if (mgr == null)
                    {
                        return false;
                    }

                    if (CountLiveMarketItems(mgr) < preMarketCount)
                    {
                        Invalidate();
                        return true;
                    }

                    return false;
                },
                PacingProfile.StandardSlot));
        }

        if (!transactionQueue.TryEnqueueBatch(steps, out var failReason, Invalidate) && !string.IsNullOrWhiteSpace(failReason))
        {
            chatGui.Print(failReason);
        }
    }

    private static bool HasCrystalSlotRoom(InventoryManager* mgr, InventoryType crystalBagType, uint crystalBaseId, int incomingQty)
    {
        var bag = mgr->GetInventoryContainer(crystalBagType);
        var slotIdx = (int)(crystalBaseId - 2);
        var slot = InventorySafetyEvaluator.GetSlot(bag, slotIdx);
        if (slot == null)
        {
            return false;
        }

        var currentQty = slot->ItemId == crystalBaseId ? slot->Quantity : 0;
        return currentQty + Math.Max(1, incomingQty) <= 9999;
    }

    private static bool RetainerAlreadyOwnsUniqueItem(InventoryManager* mgr, uint baseItemId)
    {
        foreach (var containerType in InventorySafetyEvaluator.RetainerBagContainers)
        {
            var container = mgr->GetInventoryContainer(containerType);
            if (!InventorySafetyEvaluator.IsContainerReadable(container))
            {
                continue;
            }

            for (var i = 0; i < container->Size; i++)
            {
                var slot = InventorySafetyEvaluator.GetSlot(container, i);
                if (slot != null && slot->ItemId != 0 && slot->Quantity > 0
                    && ItemIdentity.NormalizeBaseItemId(slot->ItemId) == baseItemId)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
