using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using MagicInventory.Core;

namespace MagicInventory.Services.MagicSort;

public sealed unsafe class VendorSellPlanner
{
    private readonly IDataManager dataManager;
    private readonly IGameGui gameGui;
    private readonly SafeTransactionQueue transactionQueue;
    private readonly InventorySafetyEvaluator safety;
    private readonly string sellContextText;
    private readonly string sellConfirmPromptText;
    private readonly string[] unsafeSellPromptTexts;

    public VendorSellPlanner(
        IDataManager dataManager,
        IGameGui gameGui,
        SafeTransactionQueue transactionQueue,
        InventorySafetyEvaluator safety)
    {
        this.dataManager = dataManager;
        this.gameGui = gameGui;
        this.transactionQueue = transactionQueue;
        this.safety = safety;

        var addonSheet = this.dataManager.GetExcelSheet<Addon>();
        var rawSell = addonSheet?.GetRowOrDefault(93)?.Text.ExtractText();
        sellContextText = string.IsNullOrWhiteSpace(rawSell) ? "Sell" : rawSell.Trim();

        var rawConfirm = addonSheet?.GetRowOrDefault(4477)?.Text.ExtractText();
        sellConfirmPromptText = string.IsNullOrWhiteSpace(rawConfirm)
            ? "Are you certain you wish to sell this item?"
            : rawConfirm.Trim();

        var unsafeList = new List<string>(3);
        foreach (var rowId in new uint[] { 397, 398, 399 })
        {
            var text = addonSheet?.GetRowOrDefault(rowId)?.Text.ExtractText();
            if (!string.IsNullOrWhiteSpace(text))
            {
                unsafeList.Add(text.Trim());
            }
        }

        unsafeSellPromptTexts = unsafeList.ToArray();
    }

    public List<VendorPlanItem> PlanSales(
        int maxItems,
        bool includeArmoury,
        IReadOnlyDictionary<uint, MagicSortEntry> manifestEntries)
    {
        var results = new List<VendorPlanItem>(Math.Max(0, maxItems));
        var manager = InventoryManager.Instance();
        var itemSheet = dataManager.GetExcelSheet<Item>();
        if (manager == null || itemSheet == null || manifestEntries.Count == 0 || maxItems <= 0)
        {
            return results;
        }

        var gearsetIds = safety.BuildGearsetItemIds();
        var unlockedTop = safety.BuildUnlockedJobTopIlvl();
        var containers = includeArmoury
            ? InventorySafetyEvaluator.PlayerBagContainers.Concat(InventorySafetyEvaluator.ArmouryGearContainers)
            : InventorySafetyEvaluator.PlayerBagContainers;

        foreach (var bag in containers)
        {
            if (results.Count >= maxItems)
            {
                break;
            }

            var container = manager->GetInventoryContainer(bag);
            if (!InventorySafetyEvaluator.IsContainerReadable(container))
            {
                continue;
            }

            for (var slotIdx = 0; slotIdx < container->Size && results.Count < maxItems; slotIdx++)
            {
                var slot = container->GetInventorySlot(slotIdx);
                if (slot == null || slot->ItemId == 0 || slot->Quantity <= 0)
                {
                    continue;
                }

                var rawId = slot->ItemId;
                var baseId = HoldingsService.NormalizeBaseItemId(rawId);
                if (!manifestEntries.ContainsKey(baseId))
                {
                    continue;
                }

                var itemRow = itemSheet.GetRowOrDefault(baseId);
                if (itemRow is null)
                {
                    continue;
                }

                if (!safety.IsSafeToDisposeOrList(slot, itemRow.Value, "Vendor", manifestEntries, gearsetIds, unlockedTop))
                {
                    continue;
                }

                results.Add(new VendorPlanItem(
                    bag,
                    slotIdx,
                    rawId,
                    slot->Flags,
                    baseId,
                    itemRow.Value.Name.ExtractText(),
                    slot->Quantity,
                    itemRow.Value.PriceLow));
            }
        }

        return results;
    }

    public int CountArmouryVendorItems(int maxItems, IReadOnlyDictionary<uint, MagicSortEntry> manifestEntries)
    {
        var manager = InventoryManager.Instance();
        var itemSheet = dataManager.GetExcelSheet<Item>();
        if (manager == null || itemSheet == null || manifestEntries.Count == 0 || maxItems <= 0)
        {
            return 0;
        }

        var gearsetIds = safety.BuildGearsetItemIds();
        var unlockedTop = safety.BuildUnlockedJobTopIlvl();
        var count = 0;

        foreach (var armType in InventorySafetyEvaluator.ArmouryGearContainers)
        {
            var container = manager->GetInventoryContainer(armType);
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

                var baseId = HoldingsService.NormalizeBaseItemId(slot->ItemId);
                if (!manifestEntries.ContainsKey(baseId))
                {
                    continue;
                }

                var itemRow = itemSheet.GetRowOrDefault(baseId);
                if (itemRow is null)
                {
                    continue;
                }

                if (safety.IsSafeToDisposeOrList(slot, itemRow.Value, "Vendor", manifestEntries, gearsetIds, unlockedTop))
                {
                    count++;
                    if (count >= maxItems)
                    {
                        return count;
                    }
                }
            }
        }

        return count;
    }

    public List<TransactionStep> BuildSellSteps(
        IReadOnlyList<VendorPlanItem> plans,
        Func<IReadOnlyDictionary<uint, MagicSortEntry>> getManifest)
    {
        var steps = new List<TransactionStep>(plans.Count * 2);
        foreach (var plan in plans)
        {
            var captured = plan;
            var preQty = 0;
            uint preGil = 0;
            var yesNoConfirmed = false;
            var yesNoReadyAtUtc = DateTime.MinValue;

            steps.Add(new TransactionStep(
                $"Open menu for {captured.Name}",
                "Shop",
                Dispatch: () =>
                {
                    if (!NativeUiHelpers.IsAddonOpenAndReady(gameGui, "Shop")
                        || NativeUiHelpers.IsAddonOpenAndReady(gameGui, "SelectYesno"))
                    {
                        return false;
                    }

                    if (InventorySafetyEvaluator.IsArmouryContainer(captured.Container)
                        && !NativeUiHelpers.IsAddonOpenAndReady(gameGui, "ArmouryBoard"))
                    {
                        return false;
                    }

                    var manager = InventoryManager.Instance();
                    var contextAgent = AgentInventoryContext.Instance();
                    if (manager == null || contextAgent == null)
                    {
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
                    if (!safety.IsSafeToDisposeOrList(slot, itemRow.Value, "Vendor", getManifest(), gearsetIds, unlockedTop))
                    {
                        return false;
                    }

                    preQty = slot->Quantity;
                    preGil = manager->GetGil();
                    yesNoConfirmed = false;
                    yesNoReadyAtUtc = DateTime.MinValue;

                    CloseOpenContextMenu();
                    contextAgent->OpenForItemSlot(captured.Container, captured.Slot, 0, 0);
                    return true;
                },
                HasServerAck: () =>
                {
                    var ctx = AgentInventoryContext.Instance();
                    return ctx != null
                        && GetOpenContextAddon(ctx) != null
                        && ctx->TargetInventoryId == captured.Container
                        && ctx->TargetInventorySlotId == captured.Slot;
                },
                PacingProfile.MarketFormEntry,
                SkipOnFailureCount: 1));

            steps.Add(new TransactionStep(
                $"Sell {captured.Name}",
                "Shop",
                Dispatch: () =>
                {
                    if (!NativeUiHelpers.IsAddonOpenAndReady(gameGui, "Shop")
                        || NativeUiHelpers.IsAddonOpenAndReady(gameGui, "SelectYesno")
                        || (InventorySafetyEvaluator.IsArmouryContainer(captured.Container)
                            && !NativeUiHelpers.IsAddonOpenAndReady(gameGui, "ArmouryBoard")))
                    {
                        CloseOpenContextMenu();
                        return false;
                    }

                    var ctx = AgentInventoryContext.Instance();
                    if (ctx == null
                        || ctx->TargetInventoryId != captured.Container
                        || ctx->TargetInventorySlotId != captured.Slot)
                    {
                        CloseOpenContextMenu();
                        return false;
                    }

                    var manager = InventoryManager.Instance();
                    if (manager == null)
                    {
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
                        CloseOpenContextMenu();
                        return false;
                    }

                    var gearsetIds = safety.BuildGearsetItemIds();
                    var unlockedTop = safety.BuildUnlockedJobTopIlvl();
                    if (!safety.IsSafeToDisposeOrList(slot, itemRow.Value, "Vendor", getManifest(), gearsetIds, unlockedTop))
                    {
                        CloseOpenContextMenu();
                        return false;
                    }

                    preQty = slot->Quantity;
                    preGil = manager->GetGil();
                    yesNoConfirmed = false;
                    yesNoReadyAtUtc = DateTime.MinValue;
                    return ClickSellInOpenContextMenu();
                },
                HasServerAck: () =>
                {
                    var yesNo = (AddonSelectYesno*)NativeUiHelpers.GetReadyAddon(gameGui, "SelectYesno");
                    if (yesNo != null)
                    {
                        if (!yesNoConfirmed)
                        {
                            var now = DateTime.UtcNow;
                            if (yesNoReadyAtUtc == DateTime.MinValue)
                            {
                                yesNoReadyAtUtc = now.AddMilliseconds(SampleHumanReactionMs(380, 680));
                                return false;
                            }

                            if (now < yesNoReadyAtUtc)
                            {
                                return false;
                            }

                            if (!IsSafeVendorSellConfirmPrompt(yesNo))
                            {
                                if (yesNo->NoButton != null)
                                {
                                    NativeUiHelpers.ClickAddonButton((AtkUnitBase*)yesNo, yesNo->NoButton);
                                }

                                transactionQueue.Cancel("Unexpected confirmation prompt while selling.", QueueBatchOutcome.AbortedOnError);
                                return false;
                            }

                            if (NativeUiHelpers.ClickAddonButton((AtkUnitBase*)yesNo, yesNo->YesButton))
                            {
                                yesNoConfirmed = true;
                            }
                        }

                        return false;
                    }

                    var manager = InventoryManager.Instance();
                    if (manager == null)
                    {
                        return false;
                    }

                    var slot = InventorySafetyEvaluator.GetSlot(manager, captured.Container, captured.Slot);
                    if (slot == null || slot->ItemId != captured.RawItemId || slot->Flags != captured.Flags || slot->Quantity < preQty)
                    {
                        return true;
                    }

                    return manager->GetGil() > preGil;
                },
                PacingProfile.VendorOrTurnIn,
                SecondaryRequiredAddon: "SelectYesno"));
        }

        return steps;
    }

    private bool IsSafeVendorSellConfirmPrompt(AddonSelectYesno* yesNo)
    {
        if (yesNo == null || yesNo->PromptText == null)
        {
            return false;
        }

        var prompt = NativeUiHelpers.ReadTextNodeValue(yesNo->PromptText);
        if (string.IsNullOrEmpty(prompt))
        {
            return false;
        }

        foreach (var unsafePrompt in unsafeSellPromptTexts)
        {
            if (prompt.Contains(unsafePrompt, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return prompt.Contains(sellConfirmPromptText, StringComparison.OrdinalIgnoreCase);
    }

    private static int SampleHumanReactionMs(int minMs, int maxMs)
    {
        var a = Random.Shared.Next(minMs, maxMs + 1);
        var b = Random.Shared.Next(minMs, maxMs + 1);
        return (a + b) / 2;
    }

    private static AtkUnitBase* GetOpenContextAddon(AgentInventoryContext* contextAgent)
    {
        if (contextAgent == null)
        {
            return null;
        }

        var unitManager = RaptureAtkUnitManager.Instance();
        var addonId = contextAgent->AgentInterface.GetAddonId();
        var contextAddon = unitManager != null && addonId != 0
            ? unitManager->GetAddonById((ushort)addonId)
            : null;
        return contextAddon != null && contextAddon->IsVisible ? contextAddon : null;
    }

    private static void CloseOpenContextMenu()
    {
        var contextAgent = AgentInventoryContext.Instance();
        var contextAddon = GetOpenContextAddon(contextAgent);
        if (contextAgent != null)
        {
            contextAgent->AgentInterface.Hide();
        }

        if (contextAddon != null)
        {
            contextAddon->Close(false);
        }
    }

    private bool ClickSellInOpenContextMenu()
    {
        var contextAgent = AgentInventoryContext.Instance();
        var contextAddon = GetOpenContextAddon(contextAgent);
        if (contextAgent == null || contextAddon == null)
        {
            return false;
        }

        var startIdx = contextAgent->ContexItemStartIndex;
        var count = contextAgent->ContextItemCount;
        var matchedIndex = -1;

        for (var i = 0; i < count; i++)
        {
            var paramIdx = startIdx + i;
            if ((uint)paramIdx >= (uint)contextAgent->EventParams.Length)
            {
                break;
            }

            var param = contextAgent->EventParams[paramIdx];
            if (param.Type is not (AtkValueType.String or AtkValueType.ManagedString) || param.String.Value == null)
            {
                continue;
            }

            var label = MemoryHelper.ReadSeStringNullTerminated((nint)param.String.Value).TextValue.Trim();
            if (string.Equals(label, sellContextText, StringComparison.OrdinalIgnoreCase))
            {
                if (!contextAgent->IsContextItemDisabled(i))
                {
                    matchedIndex = i;
                }

                break;
            }
        }

        if (matchedIndex < 0)
        {
            contextAgent->AgentInterface.Hide();
            contextAddon->Close(false);
            return false;
        }

        var args = stackalloc AtkValue[5];
        args[0] = NativeUiHelpers.IntValue(0);
        args[1] = NativeUiHelpers.IntValue(matchedIndex);
        args[2] = NativeUiHelpers.UIntValue(0);
        args[3] = NativeUiHelpers.IntValue(0);
        args[4] = NativeUiHelpers.IntValue(0);
        contextAddon->FireCallback(5, args, false);
        contextAgent->AgentInterface.Hide();
        contextAddon->Close(false);
        return true;
    }
}
