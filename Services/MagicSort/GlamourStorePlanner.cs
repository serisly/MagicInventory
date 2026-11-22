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

public sealed unsafe class GlamourStorePlanner
{
    private const uint GlamourPrismItemId = 21800;
    private const int DresserCapacity = 800;
    private const uint StoreEventId = 14;

    private readonly IDataManager dataManager;
    private readonly IGameGui gameGui;
    private readonly SafeTransactionQueue transactionQueue;
    private readonly InventorySafetyEvaluator safety;
    private readonly string glamourSingleConfirmLiteral;
    private readonly string glamourOutfitConfirmLiteral;

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
        glamourSingleConfirmLiteral = NativeUiHelpers.ExtractLongestAddonLiteral(dataManager, 11994);
        glamourOutfitConfirmLiteral = NativeUiHelpers.ExtractLongestAddonLiteral(dataManager, 11995);
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

        var data = agent->Data;
        if (!data->IsAsyncLoadComplete || data->IsPopulatingList || !data->IsPopulatingComplete)
        {
            return results;
        }

        var prisms = mgr->GetInventoryItemCount(GlamourPrismItemId, false, false, false);
        var freeSpace = DresserCapacity - data->UsedSlots;
        var budget = Math.Max(0, Math.Min(maxItems, Math.Min(prisms, freeSpace)));

        var gearsetIds = safety.BuildGearsetItemIds();
        var unlockedProfile = safety.BuildUnlockedJobGearProfile();
        var count = Math.Min((int)data->CrystallizeItemCount, data->CrystallizeItems.Length);
        var foundAnyCandidate = false;
        for (var i = 0; i < count; i++)
        {
            if (budget > 0 && results.Count >= budget)
            {
                break;
            }

            var ci = data->CrystallizeItems[i];
            if (ci.ItemId == 0 || ci.Slot < 0)
            {
                continue;
            }

            var slot = InventorySafetyEvaluator.GetSlot(mgr, ci.Inventory, ci.Slot);
            if (slot == null || slot->ItemId == 0 || slot->Quantity <= 0)
            {
                continue;
            }

            var baseId = ItemIdentity.NormalizeBaseItemId(slot->ItemId);
            if (!ItemIdentity.CanLockOrTagBaseId(baseId)
                || gearsetIds.Contains(baseId)
                || safety.IsItemTracked(baseId))
            {
                continue;
            }

            if (!manifestEntries.TryGetValue(baseId, out var entry)
                || entry.Route.Kind != RouteKind.Glamour)
            {
                continue;
            }

            var row = itemSheet.GetRowOrDefault(baseId);
            if (row is null)
            {
                continue;
            }

            var itemRow = row.Value;
            if (ItemIdentity.IsSoulCrystal(itemRow)
                || safety.ResolveEffectiveSlotRoute(entry, itemRow, slot, manifestEntries, gearsetIds, unlockedProfile)?.Kind != RouteKind.Glamour)
            {
                continue;
            }

            foundAnyCandidate = true;
            if (budget <= 0)
            {
                break;
            }

            results.Add(new GlamourPlanItem(ci.Inventory, ci.Slot, slot->ItemId, baseId, itemRow.Name.ExtractText()));
        }

        if (results.Count == 0 && foundAnyCandidate)
        {
            if (prisms <= 0)
            {
                blockReason = "No Glamour Prisms left.";
            }
            else if (freeSpace <= 0)
            {
                blockReason = "Glamour Dresser is full (800/800).";
            }
        }

        return results;
    }

    private bool IsGlamourDresserConfirmPrompt(string prompt)
    {
        var normalized = NativeUiHelpers.CollapseWhitespace(prompt);
        if (string.IsNullOrEmpty(normalized))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(glamourSingleConfirmLiteral)
            && normalized.Contains(glamourSingleConfirmLiteral, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(glamourOutfitConfirmLiteral)
            && normalized.Contains(glamourOutfitConfirmLiteral, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
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
                    if (NativeUiHelpers.IsAddonOpenAndReady(gameGui, "SelectYesno"))
                    {
                        return false;
                    }

                    var addon = NativeUiHelpers.GetReadyAddon(gameGui, "MiragePrismPrismBoxCrystallize");
                    var mgr = InventoryManager.Instance();
                    var agent = AgentMiragePrismPrismBox.Instance();
                    if (addon == null
                        || mgr == null
                        || agent == null
                        || agent->Data == null
                        || !agent->Data->IsAsyncLoadComplete
                        || agent->Data->IsPopulatingList
                        || !agent->Data->IsPopulatingComplete)
                    {
                        return false;
                    }

                    if (mgr->GetInventoryItemCount(GlamourPrismItemId, false, false, false) <= 0
                        || DresserCapacity - agent->Data->UsedSlots <= 0)
                    {
                        return false;
                    }

                    var itemSheet = dataManager.GetExcelSheet<Item>();
                    var itemRow = itemSheet?.GetRowOrDefault(captured.BaseItemId);
                    var gearsetIds = safety.BuildGearsetItemIds();
                    var slot = InventorySafetyEvaluator.GetSlot(mgr, captured.Container, captured.Slot);
                    if (slot == null
                        || itemRow is null
                        || slot->ItemId != captured.RawItemId
                        || slot->Quantity <= 0
                        || slot->GetMateriaCount() > 0
                        || slot->Materia[0] != 0
                        || InventorySafetyEvaluator.IsMateriaExtractable(itemRow.Value, slot)
                        || gearsetIds.Contains(captured.BaseItemId)
                        || safety.IsItemTracked(captured.BaseItemId))
                    {
                        return false;
                    }

                    var data = agent->Data;
                    var count = Math.Min((int)data->CrystallizeItemCount, data->CrystallizeItems.Length);
                    var listIndex = -1;
                    for (var i = 0; i < count; i++)
                    {
                        var ci = data->CrystallizeItems[i];
                        if (ci.Inventory == captured.Container
                            && ci.Slot == captured.Slot
                            && ItemIdentity.NormalizeBaseItemId(ci.ItemId) == captured.BaseItemId)
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
                            if (!IsGlamourDresserConfirmPrompt(prompt))
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
                                yesReadyUtc = now.AddMilliseconds(SafeTransactionQueue.SampleDialogConfirmDelayMs());
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
