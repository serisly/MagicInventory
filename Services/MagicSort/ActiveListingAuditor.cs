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
    int Quantity,
    int StackSize,
    string Name,
    long ListedUnitPrice,
    long NpcPriceLow,
    StaleListingSeverity Severity,
    string Reason,
    MarketViabilityResult Viability);

/// <summary>
/// Audits active listings in InventoryType.RetainerMarket when RetainerSellList is open,
/// flagging stale, illiquid, below-vendor, or heavily overpriced listings and providing
/// explicit user-triggered Remove or Review-Reprice actions via SafeTransactionQueue.
/// </summary>
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

            // Fail-closed: NEVER flag active listings for removal or repricing based on stale/72h-old cache;
            // wait for fresh Universalis data queued above.
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

            if (TryClassifyFinding(viability, listedPrice, row.Value.PriceLow, out var severity, out var reason))
            {
                cachedFindings.Add(new ActiveListingAuditItem(
                    activeId,
                    slotIdx,
                    rawId,
                    baseId,
                    slot->Flags,
                    isHq,
                    slot->Quantity,
                    (int)Math.Max(1, row.Value.StackSize),
                    row.Value.Name.ExtractText(),
                    listedPrice,
                    row.Value.PriceLow,
                    severity,
                    reason,
                    viability));
            }
        }

        if (missingIds.Count > 0 && !string.IsNullOrWhiteSpace(world))
        {
            universalis.QueueMissing(world, missingIds);
        }

        // Show Unrealistic/Remove findings first, then heavily overpriced listings
        cachedFindings.Sort((a, b) =>
        {
            var sevCmp = a.Severity.CompareTo(b.Severity);
            return sevCmp != 0 ? sevCmp : b.ListedUnitPrice.CompareTo(a.ListedUnitPrice);
        });

        return cachedFindings;
    }

    private bool TryClassifyFinding(
        MarketViabilityResult v,
        long listedUnitPrice,
        long npcPriceLow,
        out StaleListingSeverity severity,
        out string reason)
    {
        severity = StaleListingSeverity.UnrealisticRemove;
        reason = string.Empty;

        // 1. Dead / illiquid / zero-recent-sales / severely oversupplied -> Remove
        if (v.IsDeadOrIlliquid)
        {
            severity = StaleListingSeverity.UnrealisticRemove;
            reason = v.Reason;
            return true;
        }

        // 2. Market collapsed below 5%-tax vendor break-even / floor / minimum stack value -> Remove
        var breakeven = MarketViabilityEvaluator.ComputeVendorBreakevenPrice(npcPriceLow);
        if ((npcPriceLow > 0 && v.RealisticUnitPrice < breakeven)
            || v.RealisticUnitPrice < configuration.MarketMinFloorUnitPrice
            || (v.RealisticStackValue < configuration.VendorMaxStackValue && v.RealisticUnitPrice < configuration.MarketMinUnitPrice))
        {
            severity = StaleListingSeverity.UnrealisticRemove;
            reason = npcPriceLow > 0 && v.RealisticUnitPrice < breakeven
                ? $"MB (~{v.RealisticUnitPrice:N0}g) nets <= vendor ({npcPriceLow:N0}g)"
                : $"Market dropped (~{v.RealisticUnitPrice:N0}g ea)";
            return true;
        }

        // 3. Viable item, but your listing is wildly overpriced / heavily undercut -> Reprice
        //    ONLY suggest reprice when SuggestedUndercutPrice is strictly lower by >= 500g (never raise price or loop!)
        if (listedUnitPrice > 0
            && v.SuggestedUndercutPrice >= 2
            && v.SuggestedUndercutPrice < listedUnitPrice
            && (listedUnitPrice - v.SuggestedUndercutPrice) >= 500)
        {
            var refPrice = v.ServerCheapest > 0 ? v.ServerCheapest : v.SoldMedian;
            var pctThreshold = 1.0 + Math.Clamp(configuration.StaleListingUndercutPct, 5, 200) / 100.0;
            if (refPrice > 0 && listedUnitPrice >= refPrice * pctThreshold && (listedUnitPrice - refPrice) >= 500)
            {
                var pctAbove = (int)Math.Round(((listedUnitPrice - refPrice) / (double)refPrice) * 100.0);
                severity = StaleListingSeverity.OverpricedReprice;
                reason = $"Overpriced (+{pctAbove}% vs {refPrice:N0}g)";
                return true;
            }

            if (v.SoldMedian > 0 && listedUnitPrice > v.SoldMedian * 2.5 && (listedUnitPrice - v.SoldMedian) >= 500)
            {
                severity = StaleListingSeverity.OverpricedReprice;
                reason = $"Far above sold median ({v.SoldMedian:N0}g)";
                return true;
            }
        }

        return false;
    }

    private static bool DoesRetainerSellMatchItem(AddonRetainerSell* sellAddon, string expectedName, bool expectedHq)
    {
        if (sellAddon == null || sellAddon->ItemName == null)
        {
            return false;
        }

        var rawText = NativeUiHelpers.ReadTextNodeValue(sellAddon->ItemName);
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return false;
        }

        var addonIsHq = rawText.Contains('\uE03C');
        if (addonIsHq != expectedHq)
        {
            return false;
        }

        return string.Equals(
            NativeUiHelpers.NormalizeItemLabel(rawText),
            NativeUiHelpers.NormalizeItemLabel(expectedName),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasExactCapacityInContainers(
        InventoryManager* mgr,
        InventoryType[] containers,
        uint rawItemId,
        InventoryItem.ItemFlags flags,
        int stackSize,
        int neededQty)
    {
        if (mgr == null || neededQty <= 0)
        {
            return false;
        }

        foreach (var cType in containers)
        {
            var container = mgr->GetInventoryContainer(cType);
            if (!InventorySafetyEvaluator.IsContainerReadable(container))
            {
                continue;
            }

            for (var i = 0; i < container->Size; i++)
            {
                var slot = InventorySafetyEvaluator.GetSlot(container, i);
                if (slot == null)
                {
                    continue;
                }

                if (slot->ItemId == 0)
                {
                    return true;
                }

                if (stackSize > 1
                    && slot->ItemId == rawItemId
                    && slot->Flags == flags
                    && slot->Quantity > 0
                    && slot->Quantity + neededQty <= stackSize)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Opens RetainerSell for an active listing and prefills the suggested undercut price
    /// so the user can inspect (or view Compare Prices) and Confirm manually. Never auto-confirms.
    /// </summary>
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
                    var slot = mgr != null ? InventorySafetyEvaluator.GetSlot(mgr, InventoryType.RetainerMarket, slotIdx) : null;
                    if (slot == null || slot->ItemId != rawId || slot->Flags != expectedFlags || slot->Quantity <= 0)
                    {
                        return false;
                    }

                    var agent = AgentRetainer.Instance();
                    if (agent == null)
                    {
                        return false;
                    }

                    agent->OpenRetainerSell(InventoryType.RetainerMarket, (ushort)slotIdx);
                    return true;
                },
                HasServerAck: () =>
                {
                    var sellAddon = (AddonRetainerSell*)NativeUiHelpers.GetReadyAddon(gameGui, "RetainerSell");
                    return sellAddon != null && DoesRetainerSellMatchItem(sellAddon, itemName, expectedHq);
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
                        || !DoesRetainerSellMatchItem(sellAddon, itemName, expectedHq))
                    {
                        return false;
                    }

                    if (targetPrice >= 2)
                    {
                        sellAddon->AskingPrice->SetValue(targetPrice);
                    }

                    if (autoCompare)
                    {
                        var compareBtn = sellAddon->ComparePrices != null
                            ? sellAddon->ComparePrices
                            : sellAddon->AtkUnitBase.GetComponentButtonById(4);
                        if (compareBtn != null && compareBtn->IsEnabled)
                        {
                            NativeUiHelpers.ClickAddonButton((AtkUnitBase*)sellAddon, compareBtn);
                        }
                    }

                    return true;
                },
                HasServerAck: () =>
                {
                    var sellAddon = (AddonRetainerSell*)NativeUiHelpers.GetReadyAddon(gameGui, "RetainerSell");
                    return sellAddon != null
                        && sellAddon->AskingPrice != null
                        && DoesRetainerSellMatchItem(sellAddon, itemName, expectedHq)
                        && (targetPrice < 2 || sellAddon->AskingPrice->Value == targetPrice);
                },
                PacingProfile.MarketFormEntry,
                SecondaryRequiredAddon: "RetainerSellList"),
        };

        if (!transactionQueue.TryEnqueueBatch(steps, out var failReason, Invalidate) && !string.IsNullOrWhiteSpace(failReason))
        {
            chatGui.Print(failReason);
        }
    }

    /// <summary>
    /// Removes user-selected stale/unrealistic listings from RetainerMarket back to Player Bags
    /// (or Retainer Bags if player bags are full) using native MoveFromRetainerMarketTo* APIs,
    /// 1-in-flight with server ACK and human jitter.
    /// </summary>
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

                    var src = InventorySafetyEvaluator.GetSlot(mgr, InventoryType.RetainerMarket, captured.MarketSlot);
                    if (src == null || src->ItemId != captured.RawItemId || src->Flags != captured.Flags || src->Quantity <= 0)
                    {
                        return false;
                    }

                    var qty = (uint)src->Quantity;
                    if (HasExactCapacityInContainers(mgr, InventorySafetyEvaluator.PlayerBagContainers, captured.RawItemId, captured.Flags, captured.StackSize, src->Quantity))
                    {
                        mgr->MoveFromRetainerMarketToPlayerInventory(InventoryType.RetainerMarket, (ushort)captured.MarketSlot, qty);
                        return true;
                    }

                    if (HasExactCapacityInContainers(mgr, InventorySafetyEvaluator.RetainerBagContainers, captured.RawItemId, captured.Flags, captured.StackSize, src->Quantity))
                    {
                        mgr->MoveFromRetainerMarketToRetainerInventory(InventoryType.RetainerMarket, (ushort)captured.MarketSlot, qty);
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

                    var container = mgr->GetInventoryContainer(InventoryType.RetainerMarket);
                    if (!InventorySafetyEvaluator.IsContainerReadable(container))
                    {
                        return false;
                    }

                    var src = InventorySafetyEvaluator.GetSlot(container, captured.MarketSlot);
                    if (src != null && (src->ItemId == 0 || src->ItemId != captured.RawItemId || src->Quantity <= 0))
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
}
