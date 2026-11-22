using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.Exd;
using Lumina.Excel.Sheets;
using MagicInventory.Core;

namespace MagicInventory.Services.MagicSort;

public sealed record UsePlanItem(
    InventoryType Container,
    int Slot,
    uint RawItemId,
    uint BaseItemId,
    string Name,
    int Quantity,
    bool IsCoffer);

public sealed unsafe class UseItemsPlanner
{
    private const int MaxUsesPerClick = 10;

    private readonly IDataManager dataManager;
    private readonly ICondition condition;
    private readonly SafeTransactionQueue transactionQueue;
    private readonly InventorySafetyEvaluator safety;

    public UseItemsPlanner(
        IDataManager dataManager,
        ICondition condition,
        SafeTransactionQueue transactionQueue,
        InventorySafetyEvaluator safety)
    {
        this.dataManager = dataManager;
        this.condition = condition;
        this.transactionQueue = transactionQueue;
        this.safety = safety;
    }

    public List<UsePlanItem> PlanUses(int maxItems, IReadOnlyDictionary<uint, MagicSortEntry> manifestEntries)
    {
        var results = new List<UsePlanItem>(Math.Max(0, maxItems));
        var manager = InventoryManager.Instance();
        var itemSheet = dataManager.GetExcelSheet<Item>();
        if (manager == null || itemSheet == null || manifestEntries.Count == 0 || maxItems <= 0)
        {
            return results;
        }

        var gearsetIds = safety.BuildGearsetItemIds();
        var unlockedProfile = safety.BuildUnlockedJobGearProfile();
        var emptyBagSlots = manager->GetEmptySlotsInBag();
        var seenUnlockableIds = new HashSet<uint>();

        foreach (var containerType in InventorySafetyEvaluator.PlayerBagContainers)
        {
            if (containerType == InventoryType.Crystals)
            {
                continue;
            }

            var container = manager->GetInventoryContainer(containerType);
            if (!InventorySafetyEvaluator.IsContainerReadable(container))
            {
                continue;
            }

            for (var slotIdx = 0; slotIdx < container->Size && results.Count < maxItems; slotIdx++)
            {
                var slot = InventorySafetyEvaluator.GetSlot(container, slotIdx);
                if (slot == null || slot->ItemId == 0 || slot->Quantity <= 0)
                {
                    continue;
                }

                var rawId = slot->ItemId;
                var baseId = ItemIdentity.NormalizeBaseItemId(rawId);
                if (!ItemIdentity.CanLockOrTagBaseId(baseId)
                    || safety.IsItemTracked(baseId))
                {
                    continue;
                }

                if (!manifestEntries.TryGetValue(baseId, out var entry)
                    || !string.Equals(entry.Route, "Use", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var itemRow = itemSheet.GetRowOrDefault(baseId);
                if (itemRow is null || itemRow.Value.ItemAction.RowId == 0)
                {
                    continue;
                }

                var effRoute = safety.ResolveEffectiveSlotRoute(entry, itemRow.Value, slot, manifestEntries, gearsetIds, unlockedProfile);
                if (!string.Equals(effRoute, "Use", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var uiState = UIState.Instance();
                if (uiState != null)
                {
                    var exdRow = ExdModule.GetItemRowById(baseId);
                    if (exdRow != null)
                    {
                        var unlockStatus = uiState->IsItemActionUnlocked(exdRow);
                        if (unlockStatus is 1 or 3)
                        {
                            continue;
                        }

                        if (unlockStatus == 2 && !seenUnlockableIds.Add(baseId))
                        {
                            continue;
                        }
                    }
                }

                var name = itemRow.Value.Name.ExtractText();
                var isCoffer = ItemIdentity.IsCofferOrContainer(itemRow.Value);
                if (isCoffer)
                {
                    if (emptyBagSlots < 1)
                    {
                        continue;
                    }

                    emptyBagSlots--;
                }

                results.Add(new UsePlanItem(
                    containerType,
                    slotIdx,
                    rawId,
                    baseId,
                    name,
                    slot->Quantity,
                    isCoffer));
            }
        }

        return results;
    }

    public List<(InventoryType Container, int Slot)> GetPlannedUseSlots(IReadOnlyDictionary<uint, MagicSortEntry> manifestEntries)
    {
        var items = PlanUses(MaxUsesPerClick, manifestEntries);
        var slots = new List<(InventoryType, int)>(items.Count);
        foreach (var item in items)
        {
            slots.Add((item.Container, item.Slot));
        }

        return slots;
    }

    public int CountPlannedUses(IReadOnlyDictionary<uint, MagicSortEntry> manifestEntries)
        => PlanUses(MaxUsesPerClick, manifestEntries).Count;

    public List<TransactionStep> BuildSteps(
        IReadOnlyDictionary<uint, MagicSortEntry> manifestEntries,
        out int itemCount,
        out string statusMessage)
    {
        itemCount = 0;
        statusMessage = string.Empty;

        if (condition[ConditionFlag.InCombat]
            || condition[ConditionFlag.Casting]
            || condition[ConditionFlag.BoundByDuty]
            || condition[ConditionFlag.OccupiedInEvent]
            || condition[ConditionFlag.OccupiedInCutSceneEvent])
        {
            statusMessage = "Cannot use items right now.";
            return new List<TransactionStep>();
        }

        var plan = PlanUses(MaxUsesPerClick, manifestEntries);
        if (plan.Count == 0)
        {
            statusMessage = "No usable items in your bags.";
            return new List<TransactionStep>();
        }

        itemCount = plan.Count;
        var steps = new List<TransactionStep>(plan.Count);
        foreach (var item in plan)
        {
            var captured = item;
            var expectedQtyBefore = captured.Quantity;
            var expectedTotalBefore = 0;
            var usedIsHq = false;
            var dispatched = false;
            var skipped = false;

            steps.Add(new TransactionStep(
                $"Use {captured.Name}",
                RequiredAddon: string.Empty,
                Dispatch: () =>
                {
                    if (condition[ConditionFlag.InCombat] || condition[ConditionFlag.Casting])
                    {
                        return false;
                    }

                    var mgr = InventoryManager.Instance();
                    var actionMgr = ActionManager.Instance();
                    if (mgr == null || actionMgr == null)
                    {
                        return false;
                    }

                    var container = mgr->GetInventoryContainer(captured.Container);
                    var slot = InventorySafetyEvaluator.GetSlot(container, captured.Slot);
                    if (slot == null || slot->ItemId != captured.RawItemId || slot->Quantity <= 0)
                    {
                        skipped = true;
                        return true;
                    }

                    var uiState = UIState.Instance();
                    if (uiState != null)
                    {
                        var exdRow = ExdModule.GetItemRowById(captured.BaseItemId);
                        if (exdRow != null)
                        {
                            var unlockStatus = uiState->IsItemActionUnlocked(exdRow);
                            if (unlockStatus is 1 or 3)
                            {
                                skipped = true;
                                return true;
                            }
                        }
                    }

                    if (captured.IsCoffer && mgr->GetEmptySlotsInBag() < 1)
                    {
                        transactionQueue.Cancel("Stopped: no free bag space to open coffer.", QueueBatchOutcome.AbortedOnError);
                        return false;
                    }

                    expectedQtyBefore = slot->Quantity;
                    usedIsHq = ItemIdentity.IsHighQuality(slot);
                    expectedTotalBefore = mgr->GetInventoryItemCount(captured.BaseItemId, usedIsHq, false, false);
                    var actionItemId = ItemIdentity.ToActionItemId(captured.BaseItemId, usedIsHq);
                    var ok = actionMgr->UseAction(ActionType.Item, actionItemId, 0xE0000000, 65535);
                    if (!ok)
                    {
                        return false;
                    }

                    dispatched = true;
                    return true;
                },
                HasServerAck: () =>
                {
                    if (skipped)
                    {
                        return true;
                    }

                    if (!dispatched || condition[ConditionFlag.Casting])
                    {
                        return false;
                    }

                    var mgr = InventoryManager.Instance();
                    if (mgr == null)
                    {
                        return false;
                    }

                    if (expectedTotalBefore > 0
                        && mgr->GetInventoryItemCount(captured.BaseItemId, usedIsHq, false, false) < expectedTotalBefore)
                    {
                        return true;
                    }

                    var container = mgr->GetInventoryContainer(captured.Container);
                    var slot = InventorySafetyEvaluator.GetSlot(container, captured.Slot);
                    return slot == null || slot->ItemId != captured.RawItemId || slot->Quantity < expectedQtyBefore;
                },
                Pacing: PacingProfile.StandardSlot,
                TimeoutSeconds: 6.5,
                AllowCastingDuringAck: true));
        }

        return steps;
    }
}
