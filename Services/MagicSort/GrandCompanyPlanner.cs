using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using MagicInventory.Core;

namespace MagicInventory.Services.MagicSort;

public sealed unsafe class GrandCompanyPlanner
{
    private readonly IDataManager dataManager;
    private readonly IGameGui gameGui;
    private readonly SafeTransactionQueue transactionQueue;
    private readonly InventorySafetyEvaluator safety;

    public GrandCompanyPlanner(
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

    public int CountEligibleItemsInBagsAndArmoury(int maxItems, IReadOnlyDictionary<uint, MagicSortEntry> manifestEntries)
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

        foreach (var containerType in InventorySafetyEvaluator.PlayerBagContainers.Concat(InventorySafetyEvaluator.ArmouryGearContainers))
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

                var baseId = HoldingsService.NormalizeBaseItemId(slot->ItemId);
                if (!manifestEntries.TryGetValue(baseId, out var entry)
                    || !string.Equals(entry.Route, "GrandCompany", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var itemRow = itemSheet.GetRowOrDefault(baseId);
                if (itemRow is null)
                {
                    continue;
                }

                if (safety.IsSafeToDisposeOrList(slot, itemRow.Value, "GrandCompany", manifestEntries, gearsetIds, unlockedTop))
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

    public List<GrandCompanyPlanItem> PlanTurnIns(
        int maxItems,
        IReadOnlyDictionary<uint, MagicSortEntry> manifestEntries,
        out bool blockedBySealCap)
    {
        blockedBySealCap = false;
        var results = new List<GrandCompanyPlanItem>(Math.Max(0, maxItems));
        var agent = AgentGrandCompanySupply.Instance();
        var supplyAddon = (AddonGrandCompanySupplyList*)NativeUiHelpers.GetReadyAddon(gameGui, "GrandCompanySupplyList");
        var manager = InventoryManager.Instance();
        var ps = PlayerState.Instance();
        var itemSheet = dataManager.GetExcelSheet<Item>();
        if (agent == null || supplyAddon == null || supplyAddon->SelectedTab != 2
            || manager == null || ps == null || itemSheet == null || agent->ItemArray == null)
        {
            return results;
        }

        var gc = ps->GrandCompany;
        if (gc == 0)
        {
            return results;
        }

        var currentSeals = manager->GetCompanySeals(gc);
        var maxSeals = manager->GetMaxCompanySeals(gc);
        long projectedSeals = currentSeals;

        var gearsetIds = safety.BuildGearsetItemIds();
        var unlockedTop = safety.BuildUnlockedJobTopIlvl();
        var unsafeDuplicateBaseIds = FindUnsafeDuplicateBaseIds(agent, manager, itemSheet, manifestEntries, gearsetIds, unlockedTop);
        var orderedIndices = BuildCandidateExpertDeliveryIndices(agent, supplyAddon, gearsetIds);

        foreach (var idx in orderedIndices)
        {
            if (results.Count >= maxItems)
            {
                break;
            }

            var gcItem = agent->ItemArray + idx;
            var baseId = HoldingsService.NormalizeBaseItemId(gcItem->ItemId);
            if (unsafeDuplicateBaseIds.Contains(baseId)
                || !manifestEntries.TryGetValue(baseId, out var entry)
                || !string.Equals(entry.Route, "GrandCompany", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var slot = InventorySafetyEvaluator.GetSlot(manager, gcItem->Inventory, gcItem->Slot);
            var itemRow = itemSheet.GetRowOrDefault(baseId);
            if (slot == null || itemRow is null || HoldingsService.NormalizeBaseItemId(slot->ItemId) != baseId)
            {
                continue;
            }

            if (!safety.IsSafeToDisposeOrList(slot, itemRow.Value, "GrandCompany", manifestEntries, gearsetIds, unlockedTop))
            {
                continue;
            }

            if (gcItem->SealReward <= 0)
            {
                continue;
            }

            if (projectedSeals + gcItem->SealReward > maxSeals)
            {
                blockedBySealCap = true;
                break;
            }

            projectedSeals += gcItem->SealReward;
            results.Add(new GrandCompanyPlanItem(
                gcItem->Inventory,
                gcItem->Slot,
                slot->ItemId,
                baseId,
                itemRow.Value.Name.ExtractText(),
                gcItem->SealReward));
        }

        return results;
    }

    public List<TransactionStep> BuildTurnInSteps(
        IReadOnlyList<GrandCompanyPlanItem> plans,
        byte grandCompany,
        Func<IReadOnlyDictionary<uint, MagicSortEntry>> getManifest)
    {
        var steps = new List<TransactionStep>(plans.Count * 2);
        foreach (var plan in plans)
        {
            var captured = plan;
            uint preSeals = 0;

            steps.Add(new TransactionStep(
                $"Select {captured.Name}",
                "GrandCompanySupplyList",
                Dispatch: () =>
                {
                    var manager = InventoryManager.Instance();
                    var listAddon = (AddonGrandCompanySupplyList*)NativeUiHelpers.GetReadyAddon(gameGui, "GrandCompanySupplyList");
                    if (manager == null || listAddon == null || listAddon->SelectedTab != 2)
                    {
                        return false;
                    }

                    var currentSeals = manager->GetCompanySeals(grandCompany);
                    var maxSeals = manager->GetMaxCompanySeals(grandCompany);
                    if (currentSeals + (uint)Math.Max(0, captured.SealReward) > maxSeals)
                    {
                        transactionQueue.Cancel("Stopped before Grand Company seal cap.");
                        return false;
                    }

                    var visibleRow = ResolveExpertDeliveryRowIndex(
                        listAddon,
                        captured.Container,
                        captured.Slot,
                        captured.RawItemId,
                        captured.Name);
                    if (visibleRow < 0)
                    {
                        return false;
                    }

                    var args = stackalloc AtkValue[3];
                    args[0] = NativeUiHelpers.IntValue(1);
                    args[1] = NativeUiHelpers.IntValue(visibleRow);
                    args[2] = NativeUiHelpers.IntValue(0);
                    return NativeUiHelpers.FireCallback((AtkUnitBase*)listAddon, false, new ReadOnlySpan<AtkValue>(args, 3));
                },
                HasServerAck: () => NativeUiHelpers.IsAddonOpenAndReady(gameGui, "GrandCompanySupplyReward"),
                PacingProfile.StandardSlot,
                SecondaryRequiredAddon: "GrandCompanySupplyReward"));

            steps.Add(new TransactionStep(
                $"Deliver {captured.Name}",
                "GrandCompanySupplyReward",
                Dispatch: () =>
                {
                    var manager = InventoryManager.Instance();
                    var rewardAddon = (AddonGrandCompanySupplyReward*)NativeUiHelpers.GetReadyAddon(gameGui, "GrandCompanySupplyReward");
                    if (manager == null || rewardAddon == null)
                    {
                        return false;
                    }

                    var currentSeals = manager->GetCompanySeals(grandCompany);
                    var maxSeals = manager->GetMaxCompanySeals(grandCompany);
                    if (currentSeals + (uint)Math.Max(0, captured.SealReward) > maxSeals)
                    {
                        transactionQueue.Cancel("Stopped before Grand Company seal cap.");
                        return false;
                    }

                    var slot = InventorySafetyEvaluator.GetSlot(manager, captured.Container, captured.Slot);
                    var itemRow = dataManager.GetExcelSheet<Item>()?.GetRowOrDefault(captured.BaseItemId);
                    if (slot == null || itemRow is null || slot->ItemId != captured.RawItemId)
                    {
                        return false;
                    }

                    var gearsetIds = safety.BuildGearsetItemIds();
                    var unlockedTop = safety.BuildUnlockedJobTopIlvl();
                    if (!safety.IsSafeToDisposeOrList(slot, itemRow.Value, "GrandCompany", getManifest(), gearsetIds, unlockedTop))
                    {
                        transactionQueue.Cancel("Item no longer satisfies safety checks.");
                        return false;
                    }

                    if (!DoesRewardAddonMatchItem(rewardAddon, captured.Name))
                    {
                        transactionQueue.Cancel($"Selection changed in Grand Company turn-in window (expected '{captured.Name}').");
                        return false;
                    }

                    preSeals = currentSeals;
                    var deliverButton = rewardAddon->DeliverButton != null
                        ? rewardAddon->DeliverButton
                        : rewardAddon->AtkUnitBase.GetComponentButtonById(38);
                    return NativeUiHelpers.ClickAddonButton((AtkUnitBase*)rewardAddon, deliverButton);
                },
                HasServerAck: () =>
                {
                    if (NativeUiHelpers.IsAddonOpenAndReady(gameGui, "GrandCompanySupplyReward"))
                    {
                        return false;
                    }

                    var manager = InventoryManager.Instance();
                    if (manager == null)
                    {
                        return false;
                    }

                    var slot = InventorySafetyEvaluator.GetSlot(manager, captured.Container, captured.Slot);
                    return slot == null || slot->ItemId != captured.RawItemId || slot->Quantity <= 0;
                },
                PacingProfile.VendorOrTurnIn,
                SecondaryRequiredAddon: "GrandCompanySupplyList"));
        }

        return steps;
    }

    private HashSet<uint> FindUnsafeDuplicateBaseIds(
        AgentGrandCompanySupply* agent,
        InventoryManager* manager,
        Lumina.Excel.ExcelSheet<Item> itemSheet,
        IReadOnlyDictionary<uint, MagicSortEntry> manifestEntries,
        HashSet<uint> gearsetIds,
        Dictionary<uint, int> unlockedTop)
    {
        var unsafeIds = new HashSet<uint>();
        if (agent == null || agent->ItemArray == null)
        {
            return unsafeIds;
        }

        for (var idx = 11; idx < agent->NumItems; idx++)
        {
            var gcItem = agent->ItemArray + idx;
            if (gcItem->TurnInAvailable == 0 || gcItem->ItemId == 0 || gcItem->SealReward <= 0)
            {
                continue;
            }

            if (!InventorySafetyEvaluator.IsExpertDeliveryContainer(gcItem->Inventory))
            {
                continue;
            }

            var baseId = HoldingsService.NormalizeBaseItemId(gcItem->ItemId);
            if (!manifestEntries.TryGetValue(baseId, out var entry)
                || !string.Equals(entry.Route, "GrandCompany", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var slot = InventorySafetyEvaluator.GetSlot(manager, gcItem->Inventory, gcItem->Slot);
            var itemRow = itemSheet.GetRowOrDefault(baseId);
            if (slot == null
                || itemRow is null
                || !safety.IsSafeToDisposeOrList(slot, itemRow.Value, "GrandCompany", manifestEntries, gearsetIds, unlockedTop))
            {
                unsafeIds.Add(baseId);
            }
        }

        return unsafeIds;
    }

    private int ResolveExpertDeliveryRowIndex(
        AddonGrandCompanySupplyList* supplyAddon,
        InventoryType targetContainer,
        int targetSlot,
        uint targetRawItemId,
        string expectedName)
    {
        var agent = AgentGrandCompanySupply.Instance();
        if (agent == null || agent->ItemArray == null || supplyAddon == null || supplyAddon->ExpertDeliveryList == null)
        {
            return -1;
        }

        var list = supplyAddon->ExpertDeliveryList;
        var cleanExpected = NativeUiHelpers.NormalizeItemLabel(expectedName);

        // 1. Primary: Read rendered AtkComponentListItemRenderer rows directly via typed ListItemIndex & text nodes
        if (list->ItemRendererList != null && list->AllocatedItemRendererListLength > 0 && !string.IsNullOrEmpty(cleanExpected))
        {
            for (var r = 0; r < list->AllocatedItemRendererListLength; r++)
            {
                var renderer = list->ItemRendererList[r].AtkComponentListItemRenderer;
                if (renderer == null
                    || renderer->ListItemIndex < 0
                    || renderer->ListItemIndex >= list->ListLength
                    || renderer->AtkComponentButton.AtkComponentBase.OwnerNode == null
                    || !renderer->AtkComponentButton.AtkComponentBase.OwnerNode->IsVisible())
                {
                    continue;
                }

                if (DoesRendererContainText(renderer, cleanExpected))
                {
                    return renderer->ListItemIndex;
                }
            }
        }

        // 2. Fallback: Only when SelectedSortBy is one of the two known iLvl sorts (0 = Desc, 1 = Asc)
        if (supplyAddon->SelectedSortBy is not (0 or 1))
        {
            return -1;
        }

        var gearsetIds = safety.BuildGearsetItemIds();
        var orderedIndices = BuildOrderedExpertDeliveryAgentIndices(agent, supplyAddon, gearsetIds);
        var targetBaseId = HoldingsService.NormalizeBaseItemId(targetRawItemId);

        for (var visibleRow = 0; visibleRow < orderedIndices.Count && visibleRow < list->ListLength; visibleRow++)
        {
            var gcItem = agent->ItemArray + orderedIndices[visibleRow];
            if (gcItem->Inventory == targetContainer
                && gcItem->Slot == targetSlot
                && HoldingsService.NormalizeBaseItemId(gcItem->ItemId) == targetBaseId)
            {
                return visibleRow;
            }
        }

        return -1;
    }

    private static List<int> BuildCandidateExpertDeliveryIndices(
        AgentGrandCompanySupply* agent,
        AddonGrandCompanySupplyList* supplyAddon,
        HashSet<uint> gearsetIds)
    {
        var filter = supplyAddon->SelectedFilter;
        var visibleLabels = CollectVisibleRendererLabels(supplyAddon->ExpertDeliveryList);
        var baseIndices = supplyAddon->SelectedSortBy is 0 or 1
            ? BuildOrderedExpertDeliveryAgentIndices(agent, supplyAddon, gearsetIds)
            : CollectFilteredExpertDeliveryAgentIndices(agent, filter, gearsetIds);

        if (visibleLabels.Count == 0)
        {
            return supplyAddon->SelectedSortBy is 0 or 1 ? baseIndices : new List<int>();
        }

        var prioritized = new List<int>(baseIndices.Count);
        var deferred = new List<int>(baseIndices.Count);

        foreach (var idx in baseIndices)
        {
            var gcItem = agent->ItemArray + idx;
            var name = NativeUiHelpers.NormalizeItemLabel(gcItem->ItemName.ToString());
            if (!string.IsNullOrEmpty(name) && visibleLabels.Contains(name))
            {
                prioritized.Add(idx);
            }
            else if (supplyAddon->SelectedSortBy is 0 or 1)
            {
                deferred.Add(idx);
            }
        }

        prioritized.AddRange(deferred);
        return prioritized;
    }

    private static HashSet<string> CollectVisibleRendererLabels(AtkComponentList* list)
    {
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (list == null || list->ItemRendererList == null || list->AllocatedItemRendererListLength <= 0)
        {
            return labels;
        }

        for (var r = 0; r < list->AllocatedItemRendererListLength; r++)
        {
            var renderer = list->ItemRendererList[r].AtkComponentListItemRenderer;
            if (renderer == null || renderer->ListItemIndex < 0 || renderer->ListItemIndex >= list->ListLength)
            {
                continue;
            }

            if (renderer->AtkComponentButton.ButtonTextNode != null)
            {
                var btnText = NativeUiHelpers.NormalizeItemLabel(
                    NativeUiHelpers.ReadTextNodeValue(renderer->AtkComponentButton.ButtonTextNode));
                if (!string.IsNullOrEmpty(btnText))
                {
                    labels.Add(btnText);
                }
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

                var nodeStr = NativeUiHelpers.NormalizeItemLabel(
                    NativeUiHelpers.ReadTextNodeValue(node->GetAsAtkTextNode()));
                if (!string.IsNullOrEmpty(nodeStr))
                {
                    labels.Add(nodeStr);
                }
            }
        }

        return labels;
    }

    private static List<int> CollectFilteredExpertDeliveryAgentIndices(
        AgentGrandCompanySupply* agent,
        int filter,
        HashSet<uint> gearsetIds)
    {
        var indices = new List<int>(Math.Max(0, agent->NumItems - 11));
        for (var idx = 11; idx < agent->NumItems; idx++)
        {
            var gcItem = agent->ItemArray + idx;
            if (IsVisibleExpertDeliveryItem(gcItem, filter, gearsetIds))
            {
                indices.Add(idx);
            }
        }

        return indices;
    }

    private static List<int> BuildOrderedExpertDeliveryAgentIndices(
        AgentGrandCompanySupply* agent,
        AddonGrandCompanySupplyList* supplyAddon,
        HashSet<uint> gearsetIds)
    {
        var sortBy = supplyAddon->SelectedSortBy;
        var indices = CollectFilteredExpertDeliveryAgentIndices(agent, supplyAddon->SelectedFilter, gearsetIds);

        indices.Sort((a, b) =>
        {
            var left = agent->ItemArray + a;
            var right = agent->ItemArray + b;
            return sortBy switch
            {
                1 => left->ItemLevel != right->ItemLevel
                    ? left->ItemLevel.CompareTo(right->ItemLevel)
                    : left->Position.CompareTo(right->Position),
                _ => left->ItemLevel != right->ItemLevel
                    ? right->ItemLevel.CompareTo(left->ItemLevel)
                    : left->Position.CompareTo(right->Position),
            };
        });

        return indices;
    }

    private static bool DoesRendererContainText(AtkComponentListItemRenderer* renderer, string cleanExpected)
    {
        if (renderer->AtkComponentButton.ButtonTextNode != null)
        {
            var btnText = NativeUiHelpers.NormalizeItemLabel(
                NativeUiHelpers.ReadTextNodeValue(renderer->AtkComponentButton.ButtonTextNode));
            if (string.Equals(btnText, cleanExpected, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        var uld = renderer->AtkComponentButton.AtkComponentBase.UldManager;
        if (uld.NodeList == null)
        {
            return false;
        }

        for (var i = 0; i < uld.NodeListCount; i++)
        {
            var node = uld.NodeList[i];
            if (node == null || node->Type != NodeType.Text)
            {
                continue;
            }

            var nodeStr = NativeUiHelpers.NormalizeItemLabel(
                NativeUiHelpers.ReadTextNodeValue(node->GetAsAtkTextNode()));
            if (string.Equals(nodeStr, cleanExpected, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsVisibleExpertDeliveryItem(
        GrandCompanyItem* item,
        int selectedFilter,
        HashSet<uint> gearsetIds)
    {
        if (item == null || item->TurnInAvailable == 0 || item->ItemId == 0 || item->SealReward <= 0)
        {
            return false;
        }

        if (!InventorySafetyEvaluator.IsExpertDeliveryContainer(item->Inventory))
        {
            return false;
        }

        if (selectedFilter == 1 && gearsetIds.Contains(HoldingsService.NormalizeBaseItemId(item->ItemId)))
        {
            return false;
        }

        if (selectedFilter == 2 && InventorySafetyEvaluator.IsArmouryContainer(item->Inventory))
        {
            return false;
        }

        return true;
    }

    private static bool DoesRewardAddonMatchItem(AddonGrandCompanySupplyReward* rewardAddon, string expectedName)
    {
        if (rewardAddon == null || rewardAddon->AtkUnitBase.AtkValuesCount <= 4 || string.IsNullOrWhiteSpace(expectedName))
        {
            return false;
        }

        var val = rewardAddon->AtkUnitBase.AtkValues[4];
        if (val.Type is not (AtkValueType.String or AtkValueType.ManagedString) || val.String.Value == null)
        {
            return false;
        }

        var displayedName = NativeUiHelpers.NormalizeItemLabel(
            MemoryHelper.ReadSeStringNullTerminated((nint)val.String.Value).TextValue);
        return string.Equals(displayedName, NativeUiHelpers.NormalizeItemLabel(expectedName), StringComparison.OrdinalIgnoreCase);
    }
}
