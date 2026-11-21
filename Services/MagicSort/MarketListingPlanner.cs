using System;
using System.Collections.Generic;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using MagicInventory.Core;

namespace MagicInventory.Services.MagicSort;

public sealed unsafe class MarketListingPlanner
{
    public const int MaxRetainerMarketListings = 20;

    private readonly IDataManager dataManager;
    private readonly IGameGui gameGui;
    private readonly SafeTransactionQueue transactionQueue;
    private readonly InventorySafetyEvaluator safety;

    public MarketListingPlanner(
        IDataManager dataManager,
        IGameGui gameGui,
        SafeTransactionQueue transactionQueue,
        InventorySafetyEvaluator safety)
    {
        this.dataManager = dataManager;
        this.gameGui = gameGui;
        this.transactionQueue = transactionQueue;
        this.safety = safety;
    }

    public int GetOpenMarketSourceMask()
    {
        var mask = 0;
        if (IsAnyPlayerInventoryAddonOpen())
        {
            mask |= 1;
        }

        if (IsAnyRetainerInventoryAddonOpen())
        {
            mask |= 2;
        }

        if (NativeUiHelpers.IsAddonOpenAndReady(gameGui, "ArmouryBoard"))
        {
            mask |= 4;
        }

        return mask;
    }

    public List<MarketPlanItem> PlanListings(int maxItems, IReadOnlyDictionary<uint, MagicSortEntry> manifestEntries)
    {
        var containers = ResolveOpenMarketSourceContainers();
        return PlanListingsInContainers(containers, maxItems, manifestEntries);
    }

    public List<MarketPlanItem> PlanBagMarketPreview(IReadOnlyDictionary<uint, MagicSortEntry> manifestEntries)
        => PlanListingsInContainers(InventorySafetyEvaluator.PlayerBagContainers, int.MaxValue, manifestEntries);

    public int CountArmouryMarketItems(int maxItems, IReadOnlyDictionary<uint, MagicSortEntry> manifestEntries)
        => PlanListingsInContainers(InventorySafetyEvaluator.ArmouryGearContainers, maxItems, manifestEntries).Count;

    private List<MarketPlanItem> PlanListingsInContainers(
        IReadOnlyList<InventoryType> containers,
        int maxItems,
        IReadOnlyDictionary<uint, MagicSortEntry> manifestEntries)
    {
        var results = new List<MarketPlanItem>(Math.Clamp(maxItems, 0, 64));
        var manager = InventoryManager.Instance();
        var itemSheet = dataManager.GetExcelSheet<Item>();
        if (manager == null || itemSheet == null || manifestEntries.Count == 0 || maxItems <= 0 || containers.Count == 0)
        {
            return results;
        }

        var candidates = new List<MarketPlanItem>(32);
        var gearsetIds = safety.BuildGearsetItemIds();
        var unlockedTop = safety.BuildUnlockedJobTopIlvl();

        foreach (var containerType in containers)
        {
            var container = manager->GetInventoryContainer(containerType);
            if (!InventorySafetyEvaluator.IsContainerReadable(container))
            {
                continue;
            }

            for (var slotIdx = 0; slotIdx < container->Size; slotIdx++)
            {
                var slot = container->GetInventorySlot(slotIdx);
                if (slot == null || slot->ItemId == 0 || slot->Quantity <= 0)
                {
                    continue;
                }

                var rawId = slot->ItemId;
                var baseId = ItemIdentity.NormalizeBaseItemId(rawId);
                if (!manifestEntries.TryGetValue(baseId, out var entry)
                    || !string.Equals(entry.Route, "Market", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var itemRow = itemSheet.GetRowOrDefault(baseId);
                if (itemRow is null
                    || !safety.IsSafeToDisposeOrList(slot, itemRow.Value, "Market", manifestEntries, gearsetIds, unlockedTop))
                {
                    continue;
                }

                var isHq = ItemIdentity.IsHighQuality(slot);
                // Fail-closed: HQ uses ONLY TargetPriceHq; NQ uses ONLY TargetPriceNq (never cross-list NQ at HQ price!)
                var unitPrice = isHq ? entry.TargetPriceHq : entry.TargetPriceNq;

                var minAllowed = Math.Max(2L, itemRow.Value.PriceLow + 1L);
                if (unitPrice < minAllowed || unitPrice > 999_999_999L)
                {
                    continue;
                }

                candidates.Add(new MarketPlanItem(
                    containerType,
                    slotIdx,
                    rawId,
                    slot->Flags,
                    baseId,
                    itemRow.Value.Name.ExtractText(),
                    slot->Quantity,
                    unitPrice));
            }
        }

        // List higher-priority Market items first, then higher total stack value first
        candidates.Sort((a, b) =>
        {
            var prioCmp = safety.GetMarketPriority(b.BaseItemId).CompareTo(safety.GetMarketPriority(a.BaseItemId));
            if (prioCmp != 0)
            {
                return prioCmp;
            }

            var valA = a.UnitPrice * Math.Max(1, a.Quantity);
            var valB = b.UnitPrice * Math.Max(1, b.Quantity);
            return valB.CompareTo(valA);
        });

        for (var i = 0; i < candidates.Count && results.Count < maxItems; i++)
        {
            results.Add(candidates[i]);
        }

        return results;
    }

    public List<TransactionStep> BuildListingSteps(
        IReadOnlyList<MarketPlanItem> plans,
        Func<IReadOnlyDictionary<uint, MagicSortEntry>> getManifest,
        bool reviewOnly = false)
    {
        var stepsPerItem = reviewOnly ? 2 : 3;
        var steps = new List<TransactionStep>(plans.Count * stepsPerItem);
        foreach (var plan in plans)
        {
            var captured = plan;
            var preQty = 0;
            var preMarketCount = 0;
            var targetUnitPrice = Math.Clamp((int)captured.UnitPrice, 2, 999_999_999);

            steps.Add(new TransactionStep(
                $"Open {captured.Name} listing",
                "RetainerSellList",
                Dispatch: () =>
                {
                    if (!IsContainerWindowOpenForMarketSell(captured.Container))
                    {
                        return false;
                    }

                    var manager = InventoryManager.Instance();
                    var retainerMgr = RetainerManager.Instance();
                    var activeRetainer = retainerMgr != null && retainerMgr->IsReady ? retainerMgr->GetActiveRetainer() : null;
                    if (manager == null || activeRetainer == null)
                    {
                        return false;
                    }

                    var liveMarketCount = InventorySafetyEvaluator.GetLiveRetainerMarketCount(manager, activeRetainer);
                    if (liveMarketCount >= MaxRetainerMarketListings)
                    {
                        transactionQueue.Cancel("Retainer reached 20/20 market listings.", QueueBatchOutcome.Succeeded);
                        return false;
                    }

                    var slot = InventorySafetyEvaluator.GetSlot(manager, captured.Container, captured.Slot);
                    var itemRow = dataManager.GetExcelSheet<Item>()?.GetRowOrDefault(captured.BaseItemId);
                    if (slot == null
                        || itemRow is null
                        || slot->ItemId != captured.RawItemId
                        || slot->Flags != captured.Flags
                        || slot->Quantity <= 0)
                    {
                        return false;
                    }

                    var gearsetIds = safety.BuildGearsetItemIds();
                    var unlockedTop = safety.BuildUnlockedJobTopIlvl();
                    if (!safety.IsSafeToDisposeOrList(slot, itemRow.Value, "Market", getManifest(), gearsetIds, unlockedTop))
                    {
                        return false;
                    }

                    var minAllowed = Math.Max(2L, itemRow.Value.PriceLow + 1L);
                    if (targetUnitPrice < minAllowed)
                    {
                        return false;
                    }

                    preQty = slot->Quantity;
                    preMarketCount = liveMarketCount;

                    var agent = AgentRetainer.Instance();
                    if (agent == null)
                    {
                        return false;
                    }

                    agent->OpenRetainerSell(captured.Container, (ushort)captured.Slot);
                    return true;
                },
                HasServerAck: () => NativeUiHelpers.IsAddonOpenAndReady(gameGui, "RetainerSell"),
                PacingProfile.MarketFormEntry,
                SecondaryRequiredAddon: "RetainerSell",
                SkipOnFailureCount: reviewOnly ? 1 : 2));

            steps.Add(new TransactionStep(
                $"Set price for {captured.Name} ({captured.UnitPrice:N0}g)",
                "RetainerSell",
                Dispatch: () =>
                {
                    var sellAddon = (AddonRetainerSell*)NativeUiHelpers.GetReadyAddon(gameGui, "RetainerSell");
                    if (sellAddon == null || sellAddon->AskingPrice == null)
                    {
                        return false;
                    }

                    if (!DoesRetainerSellMatchItem(sellAddon, captured.Name))
                    {
                        transactionQueue.Cancel("Item mismatch in market listing dialog.", QueueBatchOutcome.AbortedOnError);
                        return false;
                    }

                    sellAddon->AskingPrice->SetValue(targetUnitPrice);

                    if (sellAddon->Quantity != null && preQty > 0)
                    {
                        sellAddon->Quantity->SetValue(preQty);
                    }

                    return true;
                },
                HasServerAck: () =>
                {
                    var sellAddon = (AddonRetainerSell*)NativeUiHelpers.GetReadyAddon(gameGui, "RetainerSell");
                    return sellAddon != null && DoesAskingPriceMatch(sellAddon->AskingPrice, targetUnitPrice);
                },
                PacingProfile.MarketFormEntry,
                SecondaryRequiredAddon: "RetainerSellList",
                SkipOnFailureCount: reviewOnly ? 0 : 1));

            if (reviewOnly)
            {
                // Review mode: stop with RetainerSell open & price prefilled so the user inspects and clicks Confirm themselves.
                continue;
            }

            steps.Add(new TransactionStep(
                $"Confirm {captured.Name} for {captured.UnitPrice:N0}g",
                "RetainerSell",
                Dispatch: () =>
                {
                    var sellAddon = (AddonRetainerSell*)NativeUiHelpers.GetReadyAddon(gameGui, "RetainerSell");
                    if (sellAddon == null || sellAddon->AskingPrice == null)
                    {
                        return false;
                    }

                    if (!DoesRetainerSellMatchItem(sellAddon, captured.Name))
                    {
                        transactionQueue.Cancel("Item mismatch in market listing dialog.", QueueBatchOutcome.AbortedOnError);
                        return false;
                    }

                    if (!DoesAskingPriceMatch(sellAddon->AskingPrice, targetUnitPrice))
                    {
                        transactionQueue.Cancel($"Asking price mismatch for {captured.Name} (expected {targetUnitPrice:N0}g).", QueueBatchOutcome.AbortedOnError);
                        return false;
                    }

                    var confirmButton = sellAddon->Confirm != null
                        ? sellAddon->Confirm
                        : sellAddon->AtkUnitBase.GetComponentButtonById(21);
                    return NativeUiHelpers.ClickAddonButton((AtkUnitBase*)sellAddon, confirmButton);
                },
                HasServerAck: () =>
                {
                    if (NativeUiHelpers.IsAddonOpenAndReady(gameGui, "RetainerSell"))
                    {
                        return false;
                    }

                    var manager = InventoryManager.Instance();
                    var retainerMgr = RetainerManager.Instance();
                    var activeRetainer = retainerMgr != null && retainerMgr->IsReady ? retainerMgr->GetActiveRetainer() : null;
                    if (manager != null && InventorySafetyEvaluator.GetLiveRetainerMarketCount(manager, activeRetainer) > preMarketCount)
                    {
                        return true;
                    }

                    var slot = manager != null ? InventorySafetyEvaluator.GetSlot(manager, captured.Container, captured.Slot) : null;
                    return slot == null || slot->ItemId != captured.RawItemId || slot->Flags != captured.Flags || slot->Quantity < preQty;
                },
                PacingProfile.MarketListing,
                SecondaryRequiredAddon: "RetainerSellList"));
        }

        return steps;
    }

    private bool IsAnyPlayerInventoryAddonOpen()
    {
        return NativeUiHelpers.IsAddonOpenAndReady(gameGui, "InventoryLarge")
            || NativeUiHelpers.IsAddonOpenAndReady(gameGui, "InventoryExpansion")
            || NativeUiHelpers.IsAddonOpenAndReady(gameGui, "Inventory");
    }

    private bool IsAnyRetainerInventoryAddonOpen()
    {
        return NativeUiHelpers.IsAddonOpenAndReady(gameGui, "InventoryRetainerLarge")
            || NativeUiHelpers.IsAddonOpenAndReady(gameGui, "InventoryRetainer");
    }

    private List<InventoryType> ResolveOpenMarketSourceContainers()
    {
        var list = new List<InventoryType>(11);
        if (IsAnyPlayerInventoryAddonOpen())
        {
            list.AddRange(InventorySafetyEvaluator.PlayerBagContainers);
        }

        if (IsAnyRetainerInventoryAddonOpen())
        {
            list.AddRange(InventorySafetyEvaluator.RetainerBagContainers);
        }

        return list;
    }

    private bool IsContainerWindowOpenForMarketSell(InventoryType container)
    {
        return InventorySafetyEvaluator.IsRetainerContainer(container)
            ? IsAnyRetainerInventoryAddonOpen()
            : IsAnyPlayerInventoryAddonOpen();
    }

    private static bool DoesRetainerSellMatchItem(AddonRetainerSell* sellAddon, string expectedName)
    {
        if (sellAddon == null || string.IsNullOrWhiteSpace(expectedName))
        {
            return false;
        }

        var nameNode = sellAddon->ItemName != null
            ? sellAddon->ItemName
            : sellAddon->AtkUnitBase.GetTextNodeById(7);
        if (nameNode == null)
        {
            return false;
        }

        var displayed = NativeUiHelpers.NormalizeItemLabel(NativeUiHelpers.ReadTextNodeValue(nameNode));
        if (string.IsNullOrEmpty(displayed))
        {
            return false;
        }

        var cleanExpected = NativeUiHelpers.NormalizeItemLabel(expectedName);
        return string.Equals(displayed, cleanExpected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool DoesAskingPriceMatch(AtkComponentNumericInput* input, int expectedPrice)
    {
        if (input == null || expectedPrice <= 0)
        {
            return false;
        }

        if (input->AtkTextNode == null)
        {
            return false;
        }

        var raw = NativeUiHelpers.ReadTextNodeValue(input->AtkTextNode);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        long parsed = 0;
        var sawDigit = false;
        foreach (var ch in raw)
        {
            if (ch is >= '0' and <= '9')
            {
                sawDigit = true;
                parsed = parsed * 10 + (ch - '0');
                if (parsed > 999_999_999L)
                {
                    return false;
                }
            }
        }

        return sawDigit && parsed == expectedPrice;
    }
}
