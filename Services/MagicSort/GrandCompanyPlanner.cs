using System;
using System.Collections.Generic;
using System.Globalization;
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
    private const double PostDeliverListFallbackMs = 800.0;

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

    public int CountEligibleItemsInBagsAndArmoury(
        int maxItems,
        IReadOnlyDictionary<uint, MagicSortEntry> manifestEntries,
        int selectedFilter,
        out bool atSealCap)
    {
        atSealCap = false;
        var manager = InventoryManager.Instance();
        var itemSheet = dataManager.GetExcelSheet<Item>();
        if (manager == null || itemSheet == null || manifestEntries.Count == 0 || maxItems <= 0)
        {
            return 0;
        }

        var ps = PlayerState.Instance();
        if (ps != null && ps->GrandCompany != 0)
        {
            var cur = manager->GetCompanySeals(ps->GrandCompany);
            var max = manager->GetMaxCompanySeals(ps->GrandCompany);
            if (max > 0 && cur >= max)
            {
                atSealCap = true;
            }
        }

        var gearsetIds = safety.BuildGearsetItemIds();
        var unlockedProfile = safety.BuildUnlockedJobGearProfile();
        var count = 0;

        var containers = selectedFilter == 2
            ? InventorySafetyEvaluator.PlayerBagContainers
            : InventorySafetyEvaluator.PlayerBagContainers.Concat(InventorySafetyEvaluator.ArmouryGearContainers);

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

                var baseId = ItemIdentity.NormalizeBaseItemId(slot->ItemId);
                if (!manifestEntries.TryGetValue(baseId, out var entry)
                    || entry.Route.Kind != RouteKind.GrandCompany)
                {
                    continue;
                }

                var itemRow = itemSheet.GetRowOrDefault(baseId);
                if (itemRow is null)
                {
                    continue;
                }

                if (safety.IsSafeToDisposeOrList(slot, itemRow.Value, RouteKind.GrandCompany, manifestEntries, gearsetIds, unlockedProfile))
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
        var unlockedProfile = safety.BuildUnlockedJobGearProfile();
        var unsafeDuplicateBaseIds = FindUnsafeDuplicateBaseIds(agent, manager, itemSheet, manifestEntries, gearsetIds, unlockedProfile);
        var orderedIndices = BuildCandidateExpertDeliveryIndices(agent, supplyAddon, manager, gearsetIds);

        foreach (var idx in orderedIndices)
        {
            if (results.Count >= maxItems)
            {
                break;
            }

            var gcItem = agent->ItemArray + idx;
            var baseId = ItemIdentity.NormalizeBaseItemId(gcItem->ItemId);
            if (unsafeDuplicateBaseIds.Contains(baseId)
                || !manifestEntries.TryGetValue(baseId, out var entry)
                || entry.Route.Kind != RouteKind.GrandCompany)
            {
                continue;
            }

            var slot = InventorySafetyEvaluator.GetSlot(manager, gcItem->Inventory, gcItem->Slot);
            var itemRow = itemSheet.GetRowOrDefault(baseId);
            if (slot == null || slot->Quantity <= 0 || itemRow is null || ItemIdentity.NormalizeBaseItemId(slot->ItemId) != baseId)
            {
                continue;
            }

            if (!safety.IsSafeToDisposeOrList(slot, itemRow.Value, RouteKind.GrandCompany, manifestEntries, gearsetIds, unlockedProfile))
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
                continue;
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
            var slotClearedAtUtc = DateTime.MinValue;
            var preTotalBaseCount = 0;
            var preListLength = 0;

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

                    if (NativeUiHelpers.IsAddonOpenAndReady(gameGui, "GrandCompanySupplyReward")
                        || NativeUiHelpers.IsAddonOpenAndReady(gameGui, "SelectYesno"))
                    {
                        return false;
                    }

                    var currentSeals = manager->GetCompanySeals(grandCompany);
                    var maxSeals = manager->GetMaxCompanySeals(grandCompany);
                    if (currentSeals + (uint)Math.Max(0, captured.SealReward) > maxSeals)
                    {
                        transactionQueue.Cancel("Stopped before Grand Company seal cap.", QueueBatchOutcome.Succeeded);
                        return false;
                    }

                    var liveSlot = InventorySafetyEvaluator.GetSlot(manager, captured.Container, captured.Slot);
                    if (liveSlot == null || liveSlot->Quantity <= 0 || liveSlot->ItemId != captured.RawItemId)
                    {
                        return false;
                    }

                    var visibleRow = ResolveExpertDeliveryRowIndex(
                        listAddon,
                        manager,
                        captured.Container,
                        captured.Slot,
                        captured.RawItemId,
                        captured.Name,
                        captured.SealReward);
                    if (visibleRow < 0)
                    {
                        return false;
                    }

                    preListLength = listAddon->ExpertDeliveryList != null ? listAddon->ExpertDeliveryList->ListLength : 0;
                    var args = stackalloc AtkValue[3];
                    args[0] = NativeUiHelpers.IntValue(1);
                    args[1] = NativeUiHelpers.IntValue(visibleRow);
                    args[2] = NativeUiHelpers.IntValue(0);
                    return NativeUiHelpers.FireCallback((AtkUnitBase*)listAddon, true, new ReadOnlySpan<AtkValue>(args, 3));
                },
                HasServerAck: () => NativeUiHelpers.IsAddonOpenAndReady(gameGui, "GrandCompanySupplyReward"),
                PacingProfile.StandardSlot,
                SecondaryRequiredAddon: "GrandCompanySupplyReward",
                SkipOnFailureCount: 1));

            steps.Add(new TransactionStep(
                $"Deliver {captured.Name}",
                "GrandCompanySupplyReward",
                Dispatch: () =>
                {
                    slotClearedAtUtc = DateTime.MinValue;
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
                        CloseRewardModal(rewardAddon);
                        transactionQueue.Cancel("Stopped before Grand Company seal cap.", QueueBatchOutcome.Succeeded);
                        return false;
                    }

                    var slot = InventorySafetyEvaluator.GetSlot(manager, captured.Container, captured.Slot);
                    var itemRow = dataManager.GetExcelSheet<Item>()?.GetRowOrDefault(captured.BaseItemId);
                    if (slot == null || slot->Quantity <= 0 || itemRow is null || slot->ItemId != captured.RawItemId)
                    {
                        CloseRewardModal(rewardAddon);
                        return false;
                    }

                    var gearsetIds = safety.BuildGearsetItemIds();
                    var unlockedProfile = safety.BuildUnlockedJobGearProfile();
                    if (!safety.IsSafeToDisposeOrList(slot, itemRow.Value, RouteKind.GrandCompany, getManifest(), gearsetIds, unlockedProfile))
                    {
                        CloseRewardModal(rewardAddon);
                        transactionQueue.Cancel("Item no longer satisfies safety checks.", QueueBatchOutcome.AbortedOnError);
                        return false;
                    }

                    var expectedHq = ItemIdentity.IsHighQuality(slot);
                    if (!DoesRewardAddonMatchItem(rewardAddon, captured.Name, expectedHq, out var actualDisplayed))
                    {
                        CloseRewardModal(rewardAddon);
                        transactionQueue.Cancel(
                            $"Selection mismatch in Grand Company turn-in (expected '{captured.Name}', saw '{actualDisplayed}').",
                            QueueBatchOutcome.AbortedOnError);
                        return false;
                    }

                    preTotalBaseCount = CountBaseItemInBagsAndArmoury(manager, captured.BaseItemId);
                    var deliverButton = rewardAddon->DeliverButton != null
                        ? rewardAddon->DeliverButton
                        : rewardAddon->AtkUnitBase.GetComponentButtonById(38);
                    if (!NativeUiHelpers.ClickAddonButton((AtkUnitBase*)rewardAddon, deliverButton))
                    {
                        CloseRewardModal(rewardAddon);
                        return false;
                    }

                    return true;
                },
                HasServerAck: () =>
                {
                    var yesNo = (AddonSelectYesno*)NativeUiHelpers.GetReadyAddon(gameGui, "SelectYesno");
                    if (yesNo != null)
                    {
                        if (yesNo->NoButton != null)
                        {
                            NativeUiHelpers.ClickAddonButton((AtkUnitBase*)yesNo, yesNo->NoButton);
                        }

                        var prompt = NativeUiHelpers.ReadTextNodeValue(yesNo->PromptText);
                        transactionQueue.Cancel(
                            string.IsNullOrWhiteSpace(prompt)
                                ? "Stopped: Unexpected Grand Company confirmation prompt."
                                : $"Stopped: Grand Company warning ({prompt}).",
                            QueueBatchOutcome.AbortedOnError);
                        return false;
                    }

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
                    var slotGone = slot == null || slot->ItemId != captured.RawItemId || slot->Quantity <= 0;
                    var totalReduced = preTotalBaseCount > 0 && CountBaseItemInBagsAndArmoury(manager, captured.BaseItemId) < preTotalBaseCount;
                    if (!slotGone && !totalReduced)
                    {
                        return false;
                    }

                    var listAddon = (AddonGrandCompanySupplyList*)NativeUiHelpers.GetReadyAddon(gameGui, "GrandCompanySupplyList");
                    if (listAddon == null || listAddon->ExpertDeliveryList == null)
                    {
                        return false;
                    }

                    if (preListLength > 0 && listAddon->ExpertDeliveryList->ListLength < preListLength)
                    {
                        return true;
                    }

                    var now = DateTime.UtcNow;
                    if (slotClearedAtUtc == DateTime.MinValue)
                    {
                        slotClearedAtUtc = now;
                        return false;
                    }

                    return (now - slotClearedAtUtc).TotalMilliseconds >= PostDeliverListFallbackMs;
                },
                PacingProfile.VendorOrTurnIn,
                SecondaryRequiredAddon: "GrandCompanySupplyList"));
        }

        return steps;
    }

    private static void CloseRewardModal(AddonGrandCompanySupplyReward* rewardAddon)
    {
        if (rewardAddon == null)
        {
            return;
        }

        var cancelBtn = rewardAddon->CancelButton != null
            ? rewardAddon->CancelButton
            : rewardAddon->AtkUnitBase.GetComponentButtonById(39);
        if (cancelBtn != null)
        {
            NativeUiHelpers.ClickAddonButton((AtkUnitBase*)rewardAddon, cancelBtn);
        }
    }

    private HashSet<uint> FindUnsafeDuplicateBaseIds(
        AgentGrandCompanySupply* agent,
        InventoryManager* manager,
        Lumina.Excel.ExcelSheet<Item> itemSheet,
        IReadOnlyDictionary<uint, MagicSortEntry> manifestEntries,
        HashSet<uint> gearsetIds,
        UnlockedJobGearProfile unlockedProfile)
    {
        var unsafeIds = new HashSet<uint>();
        if (agent == null || agent->ItemArray == null)
        {
            return unsafeIds;
        }

        for (var idx = 11; idx < agent->NumItems; idx++)
        {
            var gcItem = agent->ItemArray + idx;
            if (gcItem->ItemId == 0 || gcItem->SealReward <= 0)
            {
                continue;
            }

            if (!InventorySafetyEvaluator.IsExpertDeliveryContainer(gcItem->Inventory))
            {
                continue;
            }

            var baseId = ItemIdentity.NormalizeBaseItemId(gcItem->ItemId);
            var slot = InventorySafetyEvaluator.GetSlot(manager, gcItem->Inventory, gcItem->Slot);
            var itemRow = itemSheet.GetRowOrDefault(baseId);
            if (slot == null || slot->Quantity <= 0 || itemRow is null || ItemIdentity.NormalizeBaseItemId(slot->ItemId) != baseId)
            {
                continue;
            }

            if (!safety.IsSafeToDisposeOrList(slot, itemRow.Value, RouteKind.GrandCompany, manifestEntries, gearsetIds, unlockedProfile))
            {
                unsafeIds.Add(baseId);
            }
        }

        return unsafeIds;
    }

    private static int CountBaseItemInBagsAndArmoury(InventoryManager* manager, uint baseItemId)
    {
        var total = 0;
        foreach (var containerType in InventorySafetyEvaluator.PlayerBagContainers.Concat(InventorySafetyEvaluator.ArmouryGearContainers))
        {
            var c = manager->GetInventoryContainer(containerType);
            if (!InventorySafetyEvaluator.IsContainerReadable(c))
            {
                continue;
            }

            for (var i = 0; i < c->Size; i++)
            {
                var s = InventorySafetyEvaluator.GetSlot(c, i);
                if (s != null && s->Quantity > 0 && ItemIdentity.NormalizeBaseItemId(s->ItemId) == baseItemId)
                {
                    total += s->Quantity;
                }
            }
        }

        return total;
    }

    private int ResolveExpertDeliveryRowIndex(
        AddonGrandCompanySupplyList* supplyAddon,
        InventoryManager* manager,
        InventoryType targetContainer,
        int targetSlot,
        uint targetRawItemId,
        string expectedName,
        int expectedSealReward)
    {
        var agent = AgentGrandCompanySupply.Instance();
        if (agent == null || agent->ItemArray == null || supplyAddon == null || supplyAddon->ExpertDeliveryList == null)
        {
            return -1;
        }

        var list = supplyAddon->ExpertDeliveryList;
        var cleanExpected = NativeUiHelpers.NormalizeItemLabel(expectedName);
        var sealText = expectedSealReward.ToString(CultureInfo.InvariantCulture);
        var sealTextFormatted = expectedSealReward.ToString("N0", CultureInfo.InvariantCulture);

        if (supplyAddon->SelectedSortBy is 0 or 1)
        {
            var gearsetIds = safety.BuildGearsetItemIds();
            var orderedIndices = BuildOrderedExpertDeliveryAgentIndices(agent, supplyAddon, manager, gearsetIds);
            var targetBaseId = ItemIdentity.NormalizeBaseItemId(targetRawItemId);

            if (orderedIndices.Count == list->ListLength)
            {
                for (var visibleRow = 0; visibleRow < orderedIndices.Count; visibleRow++)
                {
                    var gcItem = agent->ItemArray + orderedIndices[visibleRow];
                    if (gcItem->Inventory != targetContainer
                        || gcItem->Slot != targetSlot
                        || ItemIdentity.NormalizeBaseItemId(gcItem->ItemId) != targetBaseId)
                    {
                        continue;
                    }

                    var rendererMatchesOrOffscreen = true;
                    if (list->ItemRendererList != null && list->AllocatedItemRendererListLength > 0 && !string.IsNullOrEmpty(cleanExpected))
                    {
                        for (var r = 0; r < list->AllocatedItemRendererListLength; r++)
                        {
                            var renderer = list->ItemRendererList[r].AtkComponentListItemRenderer;
                            if (renderer != null
                                && renderer->ListItemIndex == visibleRow
                                && renderer->AtkComponentButton.AtkComponentBase.OwnerNode != null
                                && renderer->AtkComponentButton.AtkComponentBase.OwnerNode->IsVisible())
                            {
                                rendererMatchesOrOffscreen = DoesRendererMatchItemName(renderer, cleanExpected);
                                break;
                            }
                        }
                    }

                    if (rendererMatchesOrOffscreen)
                    {
                        return visibleRow;
                    }
                }
            }
        }

        if (list->ItemRendererList != null && list->AllocatedItemRendererListLength > 0 && !string.IsNullOrEmpty(cleanExpected))
        {
            var sealMatch = -1;
            var sealMatchCount = 0;
            var nameOnlyMatch = -1;
            var nameOnlyCount = 0;

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

                if (!DoesRendererMatchItemName(renderer, cleanExpected))
                {
                    continue;
                }

                if (DoesRendererContainExactText(renderer, sealText) || DoesRendererContainExactText(renderer, sealTextFormatted))
                {
                    sealMatch = renderer->ListItemIndex;
                    sealMatchCount++;
                }
                else
                {
                    nameOnlyMatch = renderer->ListItemIndex;
                    nameOnlyCount++;
                }
            }

            if (sealMatchCount == 1)
            {
                return sealMatch;
            }

            if (sealMatchCount == 0 && nameOnlyCount == 1)
            {
                return nameOnlyMatch;
            }
        }

        return -1;
    }

    private static List<int> BuildCandidateExpertDeliveryIndices(
        AgentGrandCompanySupply* agent,
        AddonGrandCompanySupplyList* supplyAddon,
        InventoryManager* manager,
        HashSet<uint> gearsetIds)
    {
        var filter = supplyAddon->SelectedFilter;
        var visibleLabels = CollectVisibleRendererLabels(supplyAddon->ExpertDeliveryList);
        var baseIndices = supplyAddon->SelectedSortBy is 0 or 1
            ? BuildOrderedExpertDeliveryAgentIndices(agent, supplyAddon, manager, gearsetIds)
            : CollectFilteredExpertDeliveryAgentIndices(agent, manager, filter, gearsetIds);

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
            if (!string.IsNullOrEmpty(name) && MatchesAnyVisibleLabel(name, visibleLabels))
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

    private static bool MatchesAnyVisibleLabel(string fullItemName, HashSet<string> visibleLabels)
    {
        if (visibleLabels.Contains(fullItemName))
        {
            return true;
        }

        foreach (var label in visibleLabels)
        {
            if (IsElidedNameMatch(label, fullItemName))
            {
                return true;
            }
        }

        return false;
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
            if (renderer == null
                || renderer->ListItemIndex < 0
                || renderer->ListItemIndex >= list->ListLength
                || renderer->AtkComponentButton.AtkComponentBase.OwnerNode == null
                || !renderer->AtkComponentButton.AtkComponentBase.OwnerNode->IsVisible())
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
                if (node == null || node->Type != NodeType.Text || !node->IsVisible())
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
        InventoryManager* manager,
        int filter,
        HashSet<uint> gearsetIds)
    {
        var indices = new List<int>(Math.Max(0, agent->NumItems - 11));
        for (var idx = 11; idx < agent->NumItems; idx++)
        {
            var gcItem = agent->ItemArray + idx;
            if (IsVisibleExpertDeliveryItem(gcItem, manager, filter, gearsetIds))
            {
                indices.Add(idx);
            }
        }

        return indices;
    }

    private static List<int> BuildOrderedExpertDeliveryAgentIndices(
        AgentGrandCompanySupply* agent,
        AddonGrandCompanySupplyList* supplyAddon,
        InventoryManager* manager,
        HashSet<uint> gearsetIds)
    {
        var sortBy = supplyAddon->SelectedSortBy;
        var indices = CollectFilteredExpertDeliveryAgentIndices(agent, manager, supplyAddon->SelectedFilter, gearsetIds);

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

    private static bool DoesRendererMatchItemName(AtkComponentListItemRenderer* renderer, string cleanExpected)
    {
        if (renderer->AtkComponentButton.ButtonTextNode != null)
        {
            var btnText = NativeUiHelpers.NormalizeItemLabel(
                NativeUiHelpers.ReadTextNodeValue(renderer->AtkComponentButton.ButtonTextNode));
            if (string.Equals(btnText, cleanExpected, StringComparison.OrdinalIgnoreCase)
                || IsElidedNameMatch(btnText, cleanExpected))
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
            if (node == null || node->Type != NodeType.Text || !node->IsVisible())
            {
                continue;
            }

            var nodeStr = NativeUiHelpers.NormalizeItemLabel(
                NativeUiHelpers.ReadTextNodeValue(node->GetAsAtkTextNode()));
            if (string.Equals(nodeStr, cleanExpected, StringComparison.OrdinalIgnoreCase)
                || IsElidedNameMatch(nodeStr, cleanExpected))
            {
                return true;
            }
        }

        return false;
    }

    private static bool DoesRendererContainExactText(AtkComponentListItemRenderer* renderer, string exact)
    {
        var uld = renderer->AtkComponentButton.AtkComponentBase.UldManager;
        if (uld.NodeList == null)
        {
            return false;
        }

        for (var i = 0; i < uld.NodeListCount; i++)
        {
            var node = uld.NodeList[i];
            if (node == null || node->Type != NodeType.Text || !node->IsVisible())
            {
                continue;
            }

            var text = NativeUiHelpers.ReadTextNodeValue(node->GetAsAtkTextNode());
            if (string.Equals(text, exact, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsElidedNameMatch(string displayedText, string fullItemName)
    {
        if (displayedText.Length < 10 || fullItemName.Length <= displayedText.Length)
        {
            return false;
        }

        ReadOnlySpan<char> prefix;
        if (displayedText.EndsWith("...", StringComparison.Ordinal))
        {
            prefix = displayedText.AsSpan(0, displayedText.Length - 3).TrimEnd();
        }
        else if (displayedText.EndsWith('\u2026'))
        {
            prefix = displayedText.AsSpan(0, displayedText.Length - 1).TrimEnd();
        }
        else
        {
            return false;
        }

        return prefix.Length >= 8 && fullItemName.AsSpan().StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVisibleExpertDeliveryItem(
        GrandCompanyItem* item,
        InventoryManager* manager,
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

        if (manager != null)
        {
            var liveSlot = InventorySafetyEvaluator.GetSlot(manager, item->Inventory, item->Slot);
            var baseId = ItemIdentity.NormalizeBaseItemId(item->ItemId);
            if (liveSlot == null || liveSlot->Quantity <= 0 || ItemIdentity.NormalizeBaseItemId(liveSlot->ItemId) != baseId)
            {
                return false;
            }
        }

        if (selectedFilter == 1 && gearsetIds.Contains(ItemIdentity.NormalizeBaseItemId(item->ItemId)))
        {
            return false;
        }

        if (selectedFilter == 2 && InventorySafetyEvaluator.IsArmouryContainer(item->Inventory))
        {
            return false;
        }

        return true;
    }

    private static bool DoesRewardAddonMatchItem(
        AddonGrandCompanySupplyReward* rewardAddon,
        string expectedName,
        bool expectedHq,
        out string displayedName)
    {
        displayedName = string.Empty;
        if (rewardAddon == null || rewardAddon->AtkUnitBase.AtkValuesCount <= 4 || string.IsNullOrWhiteSpace(expectedName))
        {
            return false;
        }

        var val = rewardAddon->AtkUnitBase.AtkValues[4];
        if (val.Type is not (AtkValueType.String or AtkValueType.ManagedString) || val.String.Value == null)
        {
            return false;
        }

        var rawDisplayed = MemoryHelper.ReadSeStringNullTerminated((nint)val.String.Value).TextValue.Trim();
        var displayedHq = rawDisplayed.Contains('\uE03C');
        if (displayedHq != expectedHq)
        {
            displayedName = rawDisplayed;
            return false;
        }

        displayedName = NativeUiHelpers.NormalizeItemLabel(rawDisplayed);
        var cleanExpected = NativeUiHelpers.NormalizeItemLabel(expectedName);
        return string.Equals(displayedName, cleanExpected, StringComparison.OrdinalIgnoreCase);
    }
}
