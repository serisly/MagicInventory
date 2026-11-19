using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using MagicInventory.Core;

namespace MagicInventory.Services.MagicSort;

public sealed record ArmoirePlanItem(
    InventoryType Container,
    int Slot,
    uint RawItemId,
    uint BaseItemId,
    uint CabinetId,
    string Name);

/// <summary>
/// Safely stores eligible "Armoire"-tagged gear from Bags and Armoury Chest into the Inn Armoire
/// via native Cabinet.StoreCabinetItem with 100%-condition, unmelded, and Server-ACK verification.
/// </summary>
public sealed unsafe class ArmoireStorePlanner
{
    private readonly IDataManager dataManager;
    private readonly IGameGui gameGui;
    private readonly SafeTransactionQueue transactionQueue;
    private readonly InventorySafetyEvaluator safety;
    private readonly Dictionary<uint, uint> cabinetRowByItemId = new();

    public ArmoireStorePlanner(
        IDataManager dataManager,
        IGameGui gameGui,
        SafeTransactionQueue transactionQueue,
        InventorySafetyEvaluator safety)
    {
        this.dataManager = dataManager;
        this.gameGui = gameGui;
        this.transactionQueue = transactionQueue;
        this.safety = safety;
        LoadCabinetLookup();
    }

    public List<ArmoirePlanItem> PlanStores(
        int maxItems,
        IReadOnlyDictionary<uint, MagicSortEntry> manifestEntries,
        out int needsRepairCount,
        out int meldedSkippedCount)
    {
        needsRepairCount = 0;
        meldedSkippedCount = 0;
        var results = new List<ArmoirePlanItem>(Math.Max(0, maxItems));
        var manager = InventoryManager.Instance();
        var uiState = UIState.Instance();
        var itemSheet = dataManager.GetExcelSheet<Item>();
        if (manager == null || uiState == null || itemSheet == null || manifestEntries.Count == 0 || maxItems <= 0)
        {
            return results;
        }

        var cabinetLoaded = uiState->Cabinet.IsCabinetLoaded();
        var gearsetIds = safety.BuildGearsetItemIds();
        var seenCabinetIds = new HashSet<uint>();

        var repairNeeded = 0;
        var meldedSkipped = 0;

        void ScanContainer(InventoryType containerType)
        {
            var container = manager->GetInventoryContainer(containerType);
            if (!InventorySafetyEvaluator.IsContainerReadable(container))
            {
                return;
            }

            for (var i = 0; i < container->Size && results.Count < maxItems; i++)
            {
                var slot = InventorySafetyEvaluator.GetSlot(container, i);
                if (slot == null || slot->ItemId == 0 || slot->Quantity <= 0)
                {
                    continue;
                }

                var rawId = slot->ItemId;
                var baseId = HoldingsService.NormalizeBaseItemId(rawId);
                if (!InventorySafetyEvaluator.CanLockOrTagBaseId(baseId)
                    || gearsetIds.Contains(baseId)
                    || safety.IsItemTracked(baseId))
                {
                    continue;
                }

                if (!manifestEntries.TryGetValue(baseId, out var entry)
                    || !string.Equals(entry.Route, "Armoire", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!cabinetRowByItemId.TryGetValue(baseId, out var cabinetId))
                {
                    continue;
                }

                if (cabinetLoaded && uiState->Cabinet.IsItemInCabinet(cabinetId))
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
                    meldedSkipped++;
                    continue;
                }

                if (slot->Condition < 30000)
                {
                    repairNeeded++;
                    continue;
                }

                if (!seenCabinetIds.Add(cabinetId))
                {
                    continue;
                }

                results.Add(new ArmoirePlanItem(
                    containerType,
                    i,
                    rawId,
                    baseId,
                    cabinetId,
                    row.Value.Name.ExtractText()));
            }
        }

        foreach (var bag in InventorySafetyEvaluator.PlayerBagContainers)
        {
            ScanContainer(bag);
        }

        foreach (var arm in InventorySafetyEvaluator.ArmouryGearContainers)
        {
            ScanContainer(arm);
        }

        needsRepairCount = repairNeeded;
        meldedSkippedCount = meldedSkipped;
        return results;
    }

    public List<TransactionStep> BuildStoreSteps(IReadOnlyList<ArmoirePlanItem> plans, System.Action onStepCompleted)
    {
        var steps = new List<TransactionStep>(plans.Count);
        foreach (var plan in plans)
        {
            var captured = plan;
            var dispatched = false;
            var yesClicked = false;
            var yesReadyUtc = DateTime.MinValue;

            steps.Add(new TransactionStep(
                $"Store {captured.Name} in Armoire",
                RequiredAddon: "Cabinet",
                Dispatch: () =>
                {
                    if (!NativeUiHelpers.IsAddonOpenAndReady(gameGui, "Cabinet"))
                    {
                        return false;
                    }

                    var mgr = InventoryManager.Instance();
                    var uiState = UIState.Instance();
                    if (mgr == null || uiState == null)
                    {
                        return false;
                    }

                    if (uiState->Cabinet.IsCabinetLoaded() && uiState->Cabinet.IsItemInCabinet(captured.CabinetId))
                    {
                        dispatched = true;
                        return true;
                    }

                    var slot = InventorySafetyEvaluator.GetSlot(mgr, captured.Container, captured.Slot);
                    if (slot == null || slot->ItemId != captured.RawItemId || slot->Condition < 30000)
                    {
                        return false;
                    }

                    yesClicked = false;
                    yesReadyUtc = DateTime.MinValue;
                    var ok = uiState->Cabinet.StoreCabinetItem(captured.CabinetId);
                    if (!ok)
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
                            // Only confirm a prompt that is clearly about this item / the armoire; never blind-click Yes.
                            var promptText = yesNo->PromptText != null ? yesNo->PromptText->NodeText.ToString() : string.Empty;
                            if (!promptText.Contains(captured.Name, StringComparison.OrdinalIgnoreCase)
                                && !promptText.Contains("armoire", StringComparison.OrdinalIgnoreCase))
                            {
                                transactionQueue.Cancel("Stopped: unexpected confirmation prompt.", QueueBatchOutcome.AbortedOnError);
                                return false;
                            }

                            var now = DateTime.UtcNow;
                            if (yesReadyUtc == DateTime.MinValue)
                            {
                                var a = Random.Shared.Next(360, 621);
                                var b = Random.Shared.Next(360, 621);
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

                    var uiState = UIState.Instance();
                    if (uiState != null && uiState->Cabinet.IsCabinetLoaded() && uiState->Cabinet.IsItemInCabinet(captured.CabinetId))
                    {
                        onStepCompleted();
                        return true;
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

    private void LoadCabinetLookup()
    {
        var sheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Cabinet>();
        if (sheet == null)
        {
            return;
        }

        foreach (var row in sheet)
        {
            var itemId = row.Item.RowId;
            if (itemId > 0 && !cabinetRowByItemId.ContainsKey(itemId))
            {
                cabinetRowByItemId[itemId] = row.RowId;
            }
        }
    }
}
