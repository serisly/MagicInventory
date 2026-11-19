using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using MagicInventory.Core;

namespace MagicInventory.Services.MagicSort;

public sealed record GlamourPlanItem(
    InventoryType Container,
    int Slot,
    uint RawItemId,
    uint BaseItemId,
    string Name);

/// <summary>
/// Stores "Glamour"-tagged gear into the Glamour Dresser via the Glamour Creation window
/// (MiragePrismPrismBoxCrystallize). Live-captured payload: callback (UInt 14, UInt listIndex, UInt 0)
/// followed by a SelectYesno confirmation. The confirmation text must name the exact item before
/// Yes is clicked; otherwise No is clicked and the batch stops (fail-closed).
/// </summary>
public sealed unsafe class GlamourStorePlanner
{
    private const uint GlamourPrismItemId = 21800;
    private const int DresserCapacity = 800;
    private const uint StoreEventId = 14;

    private readonly IDataManager dataManager;
    private readonly IGameGui gameGui;
    private readonly SafeTransactionQueue transactionQueue;
    private readonly InventorySafetyEvaluator safety;

    public GlamourStorePlanner(
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

    public List<GlamourPlanItem> PlanStores(
        int maxItems,
        IReadOnlyDictionary<uint, MagicSortEntry> manifestEntries,
        out string blockReason)
    {
        blockReason = string.Empty;
        var results = new List<GlamourPlanItem>(Math.Max(0, maxItems));
        var mgr = InventoryManager.Instance();
        var agent = AgentMiragePrismPrismBox.Instance();
        var itemSheet = dataManager.GetExcelSheet<Item>();
        if (mgr == null || agent == null || agent->Data == null || itemSheet == null || manifestEntries.Count == 0 || maxItems <= 0)
        {
            return results;
        }

        var prisms = mgr->GetInventoryItemCount(GlamourPrismItemId, false, false, false);
        var freeSpace = DresserCapacity - agent->Data->UsedSlots;
        var budget = Math.Min(maxItems, Math.Min(prisms, freeSpace));
        if (prisms <= 0)
        {
            blockReason = "No Glamour Prisms left.";
            return results;
        }

        if (freeSpace <= 0)
        {
            blockReason = "Glamour Dresser is full (800/800).";
            return results;
        }

        var gearsetIds = safety.BuildGearsetItemIds();
        var data = agent->Data;
        var count = Math.Min((int)data->CrystallizeItemCount, data->CrystallizeItems.Length);
        for (var i = 0; i < count && results.Count < budget; i++)
        {
            var ci = data->CrystallizeItems[i];
            if (ci.ItemId == 0 || ci.Slot < 0)
            {
                continue;
            }

            var slot = InventorySafetyEvaluator.GetSlot(mgr, ci.Inventory, ci.Slot);
            if (slot == null || slot->ItemId == 0)
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

            if (!manifestEntries.TryGetValue(baseId, out var entry)
                || !string.Equals(entry.Route, "Glamour", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var row = itemSheet.GetRowOrDefault(baseId);
            if (row is null || InventorySafetyEvaluator.IsMateriaExtractable(row.Value, slot))
            {
                continue;
            }

            if (slot->GetMateriaCount() > 0 || slot->Materia[0] != 0)
            {
                continue;
            }

            results.Add(new GlamourPlanItem(ci.Inventory, ci.Slot, slot->ItemId, baseId, row.Value.Name.ExtractText()));
        }

        return results;
    }

    public List<TransactionStep> BuildStoreSteps(IReadOnlyList<GlamourPlanItem> plans, System.Action onStepCompleted)
    {
        var steps = new List<TransactionStep>(plans.Count);
        foreach (var plan in plans)
        {
            var captured = plan;
            var dispatched = false;
            var yesClicked = false;
            var yesReadyUtc = DateTime.MinValue;

            steps.Add(new TransactionStep(
                $"Store {captured.Name} in Glamour Dresser",
                RequiredAddon: "MiragePrismPrismBoxCrystallize",
                Dispatch: () =>
                {
                    var addon = NativeUiHelpers.GetReadyAddon(gameGui, "MiragePrismPrismBoxCrystallize");
                    var mgr = InventoryManager.Instance();
                    var agent = AgentMiragePrismPrismBox.Instance();
                    if (addon == null || mgr == null || agent == null || agent->Data == null)
                    {
                        return false;
                    }

                    var slot = InventorySafetyEvaluator.GetSlot(mgr, captured.Container, captured.Slot);
                    if (slot == null || slot->ItemId != captured.RawItemId)
                    {
                        return false;
                    }

                    // Resolve the CURRENT list index for this exact container/slot (list shifts after each store).
                    var data = agent->Data;
                    var count = Math.Min((int)data->CrystallizeItemCount, data->CrystallizeItems.Length);
                    var listIndex = -1;
                    for (var i = 0; i < count; i++)
                    {
                        var ci = data->CrystallizeItems[i];
                        if (ci.Inventory == captured.Container && ci.Slot == captured.Slot)
                        {
                            listIndex = i;
                            break;
                        }
                    }

                    if (listIndex < 0)
                    {
                        return false;
                    }

                    yesClicked = false;
                    yesReadyUtc = DateTime.MinValue;
                    var args = stackalloc AtkValue[3];
                    args[0] = NativeUiHelpers.UIntValue(StoreEventId);
                    args[1] = NativeUiHelpers.UIntValue((uint)listIndex);
                    args[2] = NativeUiHelpers.UIntValue(0);
                    if (!NativeUiHelpers.FireCallback(addon, true, new ReadOnlySpan<AtkValue>(args, 3)))
                    {
                        return false;
                    }

                    dispatched = true;
                    return true;
                },
                HasServerAck: () =>
                {
                    if (!dispatched)
                    {
                        return false;
                    }

                    if (!yesClicked)
                    {
                        var yesNo = (AddonSelectYesno*)NativeUiHelpers.GetReadyAddon(gameGui, "SelectYesno");
                        if (yesNo != null && yesNo->YesButton != null && yesNo->YesButton->IsEnabled)
                        {
                            var prompt = NativeUiHelpers.ReadTextNodeValue(yesNo->PromptText);
                            if (!prompt.Contains("glamour dresser", StringComparison.OrdinalIgnoreCase))
                            {
                                if (yesNo->NoButton != null)
                                {
                                    NativeUiHelpers.ClickAddonButton((AtkUnitBase*)yesNo, yesNo->NoButton);
                                }

                                transactionQueue.Cancel($"Stopped: Unexpected confirmation dialog while storing '{captured.Name}' (saw: '{prompt}').", QueueBatchOutcome.AbortedOnError);
                                return false;
                            }

                            var now = DateTime.UtcNow;
                            if (yesReadyUtc == DateTime.MinValue)
                            {
                                var a = Random.Shared.Next(380, 681);
                                var b = Random.Shared.Next(380, 681);
                                yesReadyUtc = now.AddMilliseconds((a + b) / 2);
                                return false;
                            }

                            if (now < yesReadyUtc)
                            {
                                return false;
                            }

                            if (NativeUiHelpers.ClickAddonButton((AtkUnitBase*)yesNo, yesNo->YesButton))
                            {
                                yesClicked = true;
                            }

                            return false;
                        }
                    }

                    if (NativeUiHelpers.IsAddonOpenAndReady(gameGui, "SelectYesno"))
                    {
                        return false;
                    }

                    var mgr = InventoryManager.Instance();
                    var slot = mgr != null ? InventorySafetyEvaluator.GetSlot(mgr, captured.Container, captured.Slot) : null;
                    if (slot == null || slot->ItemId != captured.RawItemId || slot->Quantity <= 0)
                    {
                        onStepCompleted();
                        return true;
                    }

                    return false;
                },
                PacingProfile.VendorOrTurnIn,
                SecondaryRequiredAddon: "SelectYesno",
                TimeoutSeconds: 6.0));
        }

        return steps;
    }
}
