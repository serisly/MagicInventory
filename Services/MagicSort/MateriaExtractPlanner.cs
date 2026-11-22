using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using MagicInventory.Core;

namespace MagicInventory.Services.MagicSort;

public sealed unsafe class MateriaExtractPlanner
{
    private const double ExtractionTimeoutSeconds = 7.5;

    private readonly IDataManager dataManager;
    private readonly IGameGui gameGui;
    private readonly ICondition condition;
    private readonly SafeTransactionQueue transactionQueue;
    private readonly System.Action onStepCompleted;

    public MateriaExtractPlanner(
        IDataManager dataManager,
        IGameGui gameGui,
        ICondition condition,
        SafeTransactionQueue transactionQueue,
        System.Action onStepCompleted)
    {
        this.dataManager = dataManager;
        this.gameGui = gameGui;
        this.condition = condition;
        this.transactionQueue = transactionQueue;
        this.onStepCompleted = onStepCompleted;
    }

    public bool IsBlockedByMount => condition[ConditionFlag.Mounted] || condition[ConditionFlag.RidingPillion];

    public List<MateriaExtractPlanItem> PlanExtractions(int maxItems, out int blockedByFullBag)
    {
        blockedByFullBag = 0;
        var blockedCount = 0;
        var results = new List<MateriaExtractPlanItem>(Math.Max(0, maxItems));
        var manager = InventoryManager.Instance();
        var itemSheet = dataManager.GetExcelSheet<Item>();
        if (manager == null || itemSheet == null || maxItems <= 0)
        {
            return results;
        }

        var emptyBagSlots = (int)manager->GetEmptySlotsInBag();
        var agent = AgentMaterialize.Instance();
        var seenSlots = new HashSet<(InventoryType Container, int Slot)>();

        if (agent != null && agent->ItemCount > 0 && agent->ItemArraySorted != null)
        {
            for (var i = 0; i < agent->ItemCount && results.Count < maxItems; i++)
            {
                var entry = agent->ItemArraySorted[i];
                if (entry == null || entry->Item == null)
                {
                    continue;
                }

                TryAddCandidate(entry->Item, entry->Item->Container, entry->Item->Slot);
            }
        }

        foreach (var containerType in InventorySafetyEvaluator.MaterializeSourceContainers)
        {
            if (results.Count >= maxItems)
            {
                break;
            }

            var container = manager->GetInventoryContainer(containerType);
            if (!InventorySafetyEvaluator.IsContainerReadable(container))
            {
                continue;
            }

            for (var slotIdx = 0; slotIdx < container->Size && results.Count < maxItems; slotIdx++)
            {
                var slot = container->GetInventorySlot(slotIdx);
                if (slot != null)
                {
                    TryAddCandidate(slot, containerType, slotIdx);
                }
            }
        }

        blockedByFullBag = blockedCount;
        return results;

        void TryAddCandidate(InventoryItem* slot, InventoryType containerType, int slotIdx)
        {
            if (slot == null || slot->ItemId == 0 || slot->Quantity <= 0 || slot->SpiritbondOrCollectability < 10000)
            {
                return;
            }

            if (!seenSlots.Add((containerType, slotIdx)))
            {
                return;
            }

            var baseId = ItemIdentity.NormalizeBaseItemId(slot->ItemId);
            if (!ItemIdentity.CanLockOrTagBaseId(baseId))
            {
                return;
            }

            var itemRow = itemSheet.GetRowOrDefault(baseId);
            if (itemRow is null || !InventorySafetyEvaluator.IsMateriaExtractable(itemRow.Value, slot))
            {
                return;
            }

            if (emptyBagSlots <= 0)
            {
                blockedCount++;
                return;
            }

            emptyBagSlots--;
            results.Add(new MateriaExtractPlanItem(
                containerType,
                slotIdx,
                slot->ItemId,
                baseId,
                itemRow.Value.Name.ExtractText()));
        }
    }

    public List<TransactionStep> BuildExtractionSteps(IReadOnlyList<MateriaExtractPlanItem> plans)
    {
        var steps = new List<TransactionStep>(plans.Count);
        foreach (var plan in plans)
        {
            var captured = plan;
            var dialogConfirmed = false;
            var dialogReadyAtUtc = DateTime.MinValue;

            steps.Add(new TransactionStep(
                $"Extract materia from {captured.Name}",
                "Materialize",
                Dispatch: () =>
                {
                    if (condition[ConditionFlag.Mounted] || condition[ConditionFlag.RidingPillion])
                    {
                        transactionQueue.Cancel("Dismount before extracting materia.", QueueBatchOutcome.AbortedOnError);
                        return false;
                    }

                    if (!NativeUiHelpers.IsAddonOpenAndReady(gameGui, "Materialize")
                        || condition[ConditionFlag.Occupied39]
                        || condition[ConditionFlag.InCombat]
                        || condition[ConditionFlag.Casting]
                        || condition[ConditionFlag.BoundByDuty])
                    {
                        return false;
                    }

                    var manager = InventoryManager.Instance();
                    var eventFramework = EventFramework.Instance();
                    if (manager == null || eventFramework == null)
                    {
                        return false;
                    }

                    if (manager->GetEmptySlotsInBag() < 1)
                    {
                        transactionQueue.Cancel("Stopped: inventory is full (140/140).", QueueBatchOutcome.AbortedOnError);
                        return false;
                    }

                    var slot = InventorySafetyEvaluator.GetSlot(manager, captured.Container, captured.Slot);
                    var itemRow = dataManager.GetExcelSheet<Item>()?.GetRowOrDefault(captured.BaseItemId);
                    if (slot == null
                        || itemRow is null
                        || slot->ItemId != captured.RawItemId
                        || !InventorySafetyEvaluator.IsMateriaExtractable(itemRow.Value, slot))
                    {
                        return false;
                    }

                    dialogConfirmed = false;
                    dialogReadyAtUtc = DateTime.MinValue;
                    eventFramework->MaterializeItem(slot, MaterializeEntryId.Retrieve);
                    return true;
                },
                HasServerAck: () =>
                {
                    if (!dialogConfirmed)
                    {
                        var dialog = (AddonMaterializeDialog*)NativeUiHelpers.GetReadyAddon(gameGui, "MaterializeDialog");
                        if (dialog != null)
                        {
                            var now = DateTime.UtcNow;
                            if (dialogReadyAtUtc == DateTime.MinValue)
                            {
                                var a = Random.Shared.Next(380, 681);
                                var b = Random.Shared.Next(380, 681);
                                dialogReadyAtUtc = now.AddMilliseconds((a + b) / 2);
                                return false;
                            }

                            if (now < dialogReadyAtUtc)
                            {
                                return false;
                            }

                            if (!DoesMaterializeDialogMatchItem(dialog, captured.Name))
                            {
                                var cancelBtn = dialog->AtkUnitBase.GetComponentButtonById(7);
                                if (cancelBtn != null)
                                {
                                    NativeUiHelpers.ClickAddonButton((AtkUnitBase*)dialog, cancelBtn);
                                }

                                transactionQueue.Cancel("Item mismatch in Materia Extraction dialog.", QueueBatchOutcome.AbortedOnError);
                                return false;
                            }

                            var yesBtn = dialog->YesButton != null
                                ? dialog->YesButton
                                : dialog->AtkUnitBase.GetComponentButtonById(6);
                            if (NativeUiHelpers.ClickAddonButton((AtkUnitBase*)dialog, yesBtn))
                            {
                                dialogConfirmed = true;
                            }

                            return false;
                        }
                    }

                    if (condition[ConditionFlag.Occupied39]
                        || NativeUiHelpers.IsAddonOpenAndReady(gameGui, "MaterializeDialog"))
                    {
                        return false;
                    }

                    var manager = InventoryManager.Instance();
                    var slot = manager != null ? InventorySafetyEvaluator.GetSlot(manager, captured.Container, captured.Slot) : null;
                    if (slot != null && slot->ItemId == captured.RawItemId && slot->SpiritbondOrCollectability < 10000)
                    {
                        onStepCompleted();
                        return true;
                    }

                    return false;
                },
                PacingProfile.MateriaExtraction,
                SecondaryRequiredAddon: "MaterializeDialog",
                TimeoutSeconds: ExtractionTimeoutSeconds));
        }

        return steps;
    }

    private static bool DoesMaterializeDialogMatchItem(AddonMaterializeDialog* dialog, string expectedName)
    {
        if (dialog == null || dialog->ItemName == null || string.IsNullOrWhiteSpace(expectedName))
        {
            return false;
        }

        var displayed = NativeUiHelpers.NormalizeItemLabel(NativeUiHelpers.ReadTextNodeValue(dialog->ItemName));
        if (string.IsNullOrEmpty(displayed))
        {
            return false;
        }

        var cleanExpected = NativeUiHelpers.NormalizeItemLabel(expectedName);
        return string.Equals(displayed, cleanExpected, StringComparison.OrdinalIgnoreCase);
    }
}
