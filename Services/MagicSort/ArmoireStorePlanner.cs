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

public sealed unsafe class ArmoireStorePlanner
{
    private readonly IDataManager dataManager;
    private readonly IGameGui gameGui;
    private readonly SafeTransactionQueue transactionQueue;
    private readonly InventorySafetyEvaluator safety;
    private readonly Dictionary<uint, uint> cabinetRowByItemId = new();
    private readonly string armoireStoreConfirmLiteral;

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
        armoireStoreConfirmLiteral = NativeUiHelpers.ExtractLongestAddonLiteral(dataManager, 4650);
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

        if (!uiState->Cabinet.IsCabinetLoaded())
        {
            return results;
        }

        var gearsetIds = safety.BuildGearsetItemIds();
        var unlockedProfile = safety.BuildUnlockedJobGearProfile();
        var unsafeDuplicateBaseIds = FindUnsafeCabinetBaseIds(manager, itemSheet, gearsetIds);
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
                var baseId = ItemIdentity.NormalizeBaseItemId(rawId);
                if (!ItemIdentity.CanLockOrTagBaseId(baseId)
                    || gearsetIds.Contains(baseId)
                    || safety.IsItemTracked(baseId))
                {
                    continue;
                }

                if (!manifestEntries.TryGetValue(baseId, out var entry)
                    || entry.Route.Kind != RouteKind.Armoire)
                {
                    continue;
                }

                if (!cabinetRowByItemId.TryGetValue(baseId, out var cabinetId))
                {
                    continue;
                }

                if (uiState->Cabinet.IsItemInCabinet(cabinetId))
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
                    || InventorySafetyEvaluator.IsMateriaExtractable(itemRow, slot))
                {
                    continue;
                }

                if (slot->GetMateriaCount() > 0 || slot->Materia[0] != 0)
                {
                    meldedSkipped++;
                    continue;
                }

                if (unsafeDuplicateBaseIds.Contains(baseId))
                {
                    continue;
                }

                if (safety.ResolveEffectiveSlotRoute(entry, itemRow, slot, manifestEntries, gearsetIds, unlockedProfile)?.Kind != RouteKind.Armoire)
                {
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
                    if (!NativeUiHelpers.IsAddonOpenAndReady(gameGui, "Cabinet")
                        || NativeUiHelpers.IsAddonOpenAndReady(gameGui, "SelectYesno"))
                    {
                        return false;
                    }

                    var mgr = InventoryManager.Instance();
                    var uiState = UIState.Instance();
                    var itemSheet = dataManager.GetExcelSheet<Item>();
                    if (mgr == null || uiState == null || itemSheet == null || !uiState->Cabinet.IsCabinetLoaded())
                    {
                        return false;
                    }

                    if (uiState->Cabinet.IsItemInCabinet(captured.CabinetId))
                    {
                        dispatched = true;
                        return true;
                    }

                    var slot = InventorySafetyEvaluator.GetSlot(mgr, captured.Container, captured.Slot);
                    var itemRow = itemSheet.GetRowOrDefault(captured.BaseItemId);
                    var gearsetIds = safety.BuildGearsetItemIds();
                    var manualArmoire = HasManualArmoireOverride(captured.BaseItemId);
                    var slotDyedOrGlamoured = slot != null && (slot->GlamourId != 0 || slot->GetStain(0) != 0 || slot->GetStain(1) != 0);
                    if (slot == null
                        || itemRow is null
                        || slot->ItemId != captured.RawItemId
                        || slot->Condition < 30000
                        || slot->GetMateriaCount() > 0
                        || slot->Materia[0] != 0
                        || (!manualArmoire && slotDyedOrGlamoured)
                        || InventorySafetyEvaluator.IsMateriaExtractable(itemRow.Value, slot)
                        || gearsetIds.Contains(captured.BaseItemId)
                        || HasAnyUnsafeCopyOfBaseId(mgr, itemSheet, captured.BaseItemId, gearsetIds))
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
                            var promptText = NativeUiHelpers.CollapseWhitespace(NativeUiHelpers.ReadTextNodeValue(yesNo->PromptText));
                            var expectedName = NativeUiHelpers.NormalizeItemLabel(captured.Name);
                            var matchesLiteral = string.IsNullOrEmpty(armoireStoreConfirmLiteral)
                                || promptText.Contains(armoireStoreConfirmLiteral, StringComparison.OrdinalIgnoreCase);
                            if (string.IsNullOrEmpty(expectedName)
                                || !matchesLiteral
                                || !promptText.Contains(expectedName, StringComparison.OrdinalIgnoreCase))
                            {
                                if (yesNo->NoButton != null)
                                {
                                    NativeUiHelpers.ClickAddonButton((AtkUnitBase*)yesNo, yesNo->NoButton);
                                }

                                transactionQueue.Cancel("Stopped: unexpected confirmation prompt.", QueueBatchOutcome.AbortedOnError);
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

    private HashSet<uint> FindUnsafeCabinetBaseIds(
        InventoryManager* manager,
        Lumina.Excel.ExcelSheet<Item> itemSheet,
        HashSet<uint> gearsetIds)
    {
        var unsafeIds = new HashSet<uint>();
        void Scan(InventoryType containerType)
        {
            var c = manager->GetInventoryContainer(containerType);
            if (!InventorySafetyEvaluator.IsContainerReadable(c))
            {
                return;
            }

            for (var i = 0; i < c->Size; i++)
            {
                var slot = InventorySafetyEvaluator.GetSlot(c, i);
                if (slot == null || slot->ItemId == 0 || slot->Quantity <= 0)
                {
                    continue;
                }

                var baseId = ItemIdentity.NormalizeBaseItemId(slot->ItemId);
                if (!cabinetRowByItemId.ContainsKey(baseId))
                {
                    continue;
                }

                var row = itemSheet.GetRowOrDefault(baseId);
                var manualArmoire = HasManualArmoireOverride(baseId);
                var dyedOrGlamoured = slot->GlamourId != 0 || slot->GetStain(0) != 0 || slot->GetStain(1) != 0;
                if (row is null
                    || gearsetIds.Contains(baseId)
                    || slot->GetMateriaCount() > 0
                    || slot->Materia[0] != 0
                    || (!manualArmoire && dyedOrGlamoured)
                    || InventorySafetyEvaluator.IsMateriaExtractable(row.Value, slot))
                {
                    unsafeIds.Add(baseId);
                }
            }
        }

        foreach (var bag in InventorySafetyEvaluator.PlayerBagContainers)
        {
            Scan(bag);
        }

        foreach (var arm in InventorySafetyEvaluator.ArmouryGearContainers)
        {
            Scan(arm);
        }

        return unsafeIds;
    }

    private bool HasAnyUnsafeCopyOfBaseId(
        InventoryManager* manager,
        Lumina.Excel.ExcelSheet<Item> itemSheet,
        uint targetBaseId,
        HashSet<uint> gearsetIds)
    {
        if (gearsetIds.Contains(targetBaseId))
        {
            return true;
        }

        var row = itemSheet.GetRowOrDefault(targetBaseId);
        if (row is null)
        {
            return true;
        }

        bool Scan(InventoryType containerType)
        {
            var c = manager->GetInventoryContainer(containerType);
            if (!InventorySafetyEvaluator.IsContainerReadable(c))
            {
                return false;
            }

            for (var i = 0; i < c->Size; i++)
            {
                var slot = InventorySafetyEvaluator.GetSlot(c, i);
                if (slot == null || slot->ItemId == 0 || slot->Quantity <= 0)
                {
                    continue;
                }

                if (ItemIdentity.NormalizeBaseItemId(slot->ItemId) != targetBaseId)
                {
                    continue;
                }

                var manualArmoire = HasManualArmoireOverride(targetBaseId);
                var dyedOrGlamoured = slot->GlamourId != 0 || slot->GetStain(0) != 0 || slot->GetStain(1) != 0;
                if (slot->GetMateriaCount() > 0
                    || slot->Materia[0] != 0
                    || (!manualArmoire && dyedOrGlamoured)
                    || InventorySafetyEvaluator.IsMateriaExtractable(row.Value, slot))
                {
                    return true;
                }
            }

            return false;
        }

        foreach (var bag in InventorySafetyEvaluator.PlayerBagContainers)
        {
            if (Scan(bag))
            {
                return true;
            }
        }

        foreach (var arm in InventorySafetyEvaluator.ArmouryGearContainers)
        {
            if (Scan(arm))
            {
                return true;
            }
        }

        return false;
    }

    private bool HasManualArmoireOverride(uint baseItemId)
        => safety.HasManualRouteOverride(baseItemId, RouteKind.Armoire);

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
