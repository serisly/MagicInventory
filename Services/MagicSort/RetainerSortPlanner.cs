using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using MagicInventory.Core;

namespace MagicInventory.Services.MagicSort;

public sealed unsafe class RetainerSortPlanner
{
    private readonly record struct RetainerTransferCandidate(
        InventoryType SourceContainer,
        int SourceSlot,
        uint RawItemId,
        uint BaseItemId,
        InventoryItem.ItemFlags Flags,
        int Quantity,
        int StackSize,
        bool IsUnique,
        string ItemName);

    private readonly IDataManager dataManager;
    private readonly IGameGui gameGui;
    private readonly HoldingsService holdings;
    private readonly InventorySafetyEvaluator safety;
    private readonly Func<Dictionary<uint, int>> getMissingTrackedQuantities;
    private readonly System.Action onStepCompleted;

    public RetainerSortPlanner(
        IDataManager dataManager,
        IGameGui gameGui,
        HoldingsService holdings,
        InventorySafetyEvaluator safety,
        Func<Dictionary<uint, int>> getMissingTrackedQuantities,
        System.Action onStepCompleted)
    {
        this.dataManager = dataManager;
        this.gameGui = gameGui;
        this.holdings = holdings;
        this.safety = safety;
        this.getMissingTrackedQuantities = getMissingTrackedQuantities;
        this.onStepCompleted = onStepCompleted;
    }

    public RetainerSortPlan BuildPlan(int maxCount, IReadOnlyDictionary<uint, MagicSortEntry> manifestEntries)
    {
        holdings.CaptureActiveRetainerIfOpen(true);

        var activeId = HoldingsService.GetActiveRetainerId();
        var manager = InventoryManager.Instance();
        var itemSheet = dataManager.GetExcelSheet<Item>();
        if (activeId == 0 || manager == null || itemSheet == null)
        {
            return new RetainerSortPlan(
                new List<TransactionStep>(),
                0,
                0,
                0,
                0,
                0,
                false,
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<(InventoryType, int)>(),
                Array.Empty<(InventoryType, int)>());
        }

        var gearsetIds = safety.BuildGearsetItemIds();
        var unlockedTop = safety.BuildUnlockedJobTopIlvl();
        var armouryOpen = NativeUiHelpers.IsAddonOpenAndReady(gameGui, "ArmouryBoard");

        var simulatedRetainerEmpty = 0;
        var retainerExistingBaseIds = new HashSet<uint>();
        var retainerPartialRoom = new Dictionary<(uint ItemId, InventoryItem.ItemFlags Flags), int>();

        foreach (var retType in InventorySafetyEvaluator.RetainerBagContainers)
        {
            var retContainer = manager->GetInventoryContainer(retType);
            if (!InventorySafetyEvaluator.IsContainerReadable(retContainer))
            {
                continue;
            }

            for (var i = 0; i < retContainer->Size; i++)
            {
                var rSlot = InventorySafetyEvaluator.GetSlot(retContainer, i);
                if (rSlot == null)
                {
                    continue;
                }

                if (rSlot->ItemId == 0 || rSlot->Quantity <= 0)
                {
                    simulatedRetainerEmpty++;
                    continue;
                }

                var rBaseId = HoldingsService.NormalizeBaseItemId(rSlot->ItemId);
                retainerExistingBaseIds.Add(rBaseId);

                var rRow = itemSheet.GetRowOrDefault(rBaseId);
                if (rRow is null)
                {
                    continue;
                }

                var rStackSize = (int)Math.Max(1, rRow.Value.StackSize);
                if (rStackSize > 1
                    && (rSlot->Flags & InventoryItem.ItemFlags.Collectable) == 0
                    && rSlot->Quantity < rStackSize)
                {
                    var key = (rSlot->ItemId, rSlot->Flags);
                    retainerPartialRoom[key] = retainerPartialRoom.GetValueOrDefault(key) + (rStackSize - rSlot->Quantity);
                }
            }
        }

        var simulatedPlayerEmpty = 0;
        var playerExistingBaseIds = new HashSet<uint>();
        var playerPartialRoom = new Dictionary<(uint ItemId, InventoryItem.ItemFlags Flags), int>();

        foreach (var bag in InventorySafetyEvaluator.PlayerBagContainers)
        {
            var container = manager->GetInventoryContainer(bag);
            if (!InventorySafetyEvaluator.IsContainerReadable(container))
            {
                continue;
            }

            for (var i = 0; i < container->Size; i++)
            {
                var pSlot = InventorySafetyEvaluator.GetSlot(container, i);
                if (pSlot == null)
                {
                    continue;
                }

                if (pSlot->ItemId == 0 || pSlot->Quantity <= 0)
                {
                    simulatedPlayerEmpty++;
                    continue;
                }

                var pBaseId = HoldingsService.NormalizeBaseItemId(pSlot->ItemId);
                playerExistingBaseIds.Add(pBaseId);

                var pRow = itemSheet.GetRowOrDefault(pBaseId);
                if (pRow is null)
                {
                    continue;
                }

                var pStackSize = (int)Math.Max(1, pRow.Value.StackSize);
                if (pStackSize > 1
                    && (pSlot->Flags & InventoryItem.ItemFlags.Collectable) == 0
                    && pSlot->Quantity < pStackSize)
                {
                    var key = (pSlot->ItemId, pSlot->Flags);
                    playerPartialRoom[key] = playerPartialRoom.GetValueOrDefault(key) + (pStackSize - pSlot->Quantity);
                }
            }
        }

        foreach (var equipContainerType in InventorySafetyEvaluator.ArmouryGearContainers.Append(InventoryType.EquippedItems))
        {
            var equipContainer = manager->GetInventoryContainer(equipContainerType);
            if (equipContainer == null || equipContainer->Items == null)
            {
                continue;
            }

            for (var i = 0; i < equipContainer->Size; i++)
            {
                var eSlot = equipContainer->GetInventorySlot(i);
                if (eSlot != null && eSlot->ItemId != 0 && eSlot->Quantity > 0)
                {
                    playerExistingBaseIds.Add(HoldingsService.NormalizeBaseItemId(eSlot->ItemId));
                }
            }
        }

        var depositSources = armouryOpen
            ? InventorySafetyEvaluator.PlayerBagContainers.Concat(InventorySafetyEvaluator.ArmouryGearContainers)
            : InventorySafetyEvaluator.PlayerBagContainers;

        var depositCandidates = new List<RetainerTransferCandidate>();
        foreach (var srcBag in depositSources)
        {
            var container = manager->GetInventoryContainer(srcBag);
            if (!InventorySafetyEvaluator.IsContainerReadable(container))
            {
                continue;
            }

            var fromArmoury = InventorySafetyEvaluator.IsArmouryContainer(srcBag);
            for (var slotIdx = 0; slotIdx < container->Size; slotIdx++)
            {
                var slot = InventorySafetyEvaluator.GetSlot(container, slotIdx);
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

                var itemRow = itemSheet.GetRowOrDefault(baseId);
                if (itemRow is null || itemRow.Value.ItemUICategory.RowId == 62 || itemRow.Value.Rarity >= 4)
                {
                    continue;
                }

                if (itemRow.Value.IsUnique && retainerExistingBaseIds.Contains(baseId))
                {
                    continue;
                }

                var stackSize = (int)Math.Max(1, itemRow.Value.StackSize);
                var isHq = rawId >= 1_000_000 || slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);

                if (manifestEntries.TryGetValue(baseId, out var entry))
                {
                    var effRoute = safety.ResolveEffectiveSlotRoute(entry, itemRow.Value, slot, manifestEntries, gearsetIds, unlockedTop);
                    if (effRoute != "Retainer")
                    {
                        continue;
                    }

                    var isPrimaryTarget = entry.TargetRetainerId == activeId;
                    var isOverflowTarget = entry.TargetRetainerId != 0
                        && entry.TargetRetainerId != activeId
                        && holdings.IsRetainerKnownFullFor(entry.TargetRetainerId, baseId, isHq, stackSize);
                    if (!isPrimaryTarget && !isOverflowTarget)
                    {
                        continue;
                    }
                }
                else
                {
                    if (fromArmoury
                        || !retainerExistingBaseIds.Contains(baseId)
                        || !InventorySafetyEvaluator.IsEligibleForLiveRetainerDuplicateDeposit(itemRow.Value))
                    {
                        continue;
                    }
                }

                depositCandidates.Add(new RetainerTransferCandidate(
                    srcBag,
                    slotIdx,
                    rawId,
                    baseId,
                    slot->Flags,
                    slot->Quantity,
                    stackSize,
                    itemRow.Value.IsUnique,
                    itemRow.Value.Name.ExtractText()));
            }
        }

        depositCandidates.Sort((a, b) =>
        {
            var aMerges = retainerPartialRoom.GetValueOrDefault((a.RawItemId, a.Flags)) > 0 ? 1 : 0;
            var bMerges = retainerPartialRoom.GetValueOrDefault((b.RawItemId, b.Flags)) > 0 ? 1 : 0;
            return bMerges.CompareTo(aMerges);
        });

        // Pre-compute destination retainer free-slot budgets AFTER accounting for player-bag items already
        // queued to deposit into those retainers, so cross-retainer funneling NEVER pulls an item out of
        // this retainer only to leave it stranded in the player's bag because the target retainer filled up.
        var destFreeSlotBudget = new Dictionary<ulong, int>();
        foreach (var srcBag in InventorySafetyEvaluator.PlayerBagContainers)
        {
            var bagContainer = manager->GetInventoryContainer(srcBag);
            if (!InventorySafetyEvaluator.IsContainerReadable(bagContainer))
            {
                continue;
            }

            for (var sIdx = 0; sIdx < bagContainer->Size; sIdx++)
            {
                var pSlot = InventorySafetyEvaluator.GetSlot(bagContainer, sIdx);
                if (pSlot == null || pSlot->ItemId == 0 || pSlot->Quantity <= 0)
                {
                    continue;
                }

                var pBase = ItemIdentity.NormalizeBaseItemId(pSlot->ItemId);
                if (!ItemIdentity.CanLockOrTagBaseId(pBase)
                    || gearsetIds.Contains(pBase)
                    || safety.IsItemTracked(pBase))
                {
                    continue;
                }

                if (!manifestEntries.TryGetValue(pBase, out var pEntry)
                    || !string.Equals(pEntry.Route, "Retainer", StringComparison.OrdinalIgnoreCase)
                    || pEntry.TargetRetainerId == 0
                    || pEntry.TargetRetainerId == activeId)
                {
                    continue;
                }

                var pRow = itemSheet.GetRowOrDefault(pBase);
                if (pRow is null
                    || ItemIdentity.IsSoulCrystal(pRow.Value)
                    || pRow.Value.Rarity >= 4)
                {
                    continue;
                }

                if (!string.Equals(
                        safety.ResolveEffectiveSlotRoute(pEntry, pRow.Value, pSlot, manifestEntries, gearsetIds, unlockedTop),
                        "Retainer",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var pStack = (int)Math.Max(1, pRow.Value.StackSize);
                var pHq = ItemIdentity.IsHighQuality(pSlot);
                if (pStack > 1 && holdings.HasPartialStackOnRetainer(pEntry.TargetRetainerId, pBase, pHq))
                {
                    continue;
                }

                if (!destFreeSlotBudget.TryGetValue(pEntry.TargetRetainerId, out var rem))
                {
                    rem = holdings.GetKnownRetainerFreeSlots(pEntry.TargetRetainerId);
                }

                destFreeSlotBudget[pEntry.TargetRetainerId] = Math.Max(0, rem - 1);
            }
        }

        var withdrawCandidates = new List<RetainerTransferCandidate>();
        foreach (var retBag in InventorySafetyEvaluator.RetainerBagContainers)
        {
            var container = manager->GetInventoryContainer(retBag);
            if (!InventorySafetyEvaluator.IsContainerReadable(container))
            {
                continue;
            }

            for (var slotIdx = 0; slotIdx < container->Size; slotIdx++)
            {
                var slot = InventorySafetyEvaluator.GetSlot(container, slotIdx);
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

                if (!manifestEntries.TryGetValue(baseId, out var entry))
                {
                    continue;
                }

                var itemRow = itemSheet.GetRowOrDefault(baseId);
                if (itemRow is null)
                {
                    continue;
                }

                if (itemRow.Value.IsUnique && playerExistingBaseIds.Contains(baseId))
                {
                    continue;
                }

                var effRoute = safety.ResolveEffectiveSlotRoute(entry, itemRow.Value, slot, manifestEntries, gearsetIds, unlockedTop);
                if (effRoute == null)
                {
                    continue;
                }

                var stackSize = (int)Math.Max(1, itemRow.Value.StackSize);
                var isHq = rawId >= 1_000_000 || slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);

                var shouldWithdraw = false;
                switch (effRoute)
                {
                    case "Market":
                    case "Vendor":
                    case "GrandCompany":
                    case "GoldSaucer":
                    case "Materia":
                    case "Use":
                    case "Armoire":
                    case "Glamour":
                        shouldWithdraw = true;
                        break;
                    case "Retainer":
                        if (entry.TargetRetainerId != 0 && entry.TargetRetainerId != activeId)
                        {
                            var mergesIntoPartial = stackSize > 1
                                && holdings.HasPartialStackOnRetainer(entry.TargetRetainerId, baseId, isHq);
                            if (mergesIntoPartial)
                            {
                                shouldWithdraw = true;
                            }
                            else
                            {
                                if (!destFreeSlotBudget.TryGetValue(entry.TargetRetainerId, out var remSlots))
                                {
                                    remSlots = holdings.GetKnownRetainerFreeSlots(entry.TargetRetainerId);
                                }

                                if (remSlots > 0)
                                {
                                    destFreeSlotBudget[entry.TargetRetainerId] = remSlots - 1;
                                    shouldWithdraw = true;
                                }
                            }
                        }

                        break;
                }

                if (!shouldWithdraw)
                {
                    continue;
                }

                withdrawCandidates.Add(new RetainerTransferCandidate(
                    retBag,
                    slotIdx,
                    rawId,
                    baseId,
                    slot->Flags,
                    slot->Quantity,
                    stackSize,
                    itemRow.Value.IsUnique,
                    itemRow.Value.Name.ExtractText()));
            }
        }

        if (armouryOpen)
        {
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

                    var rawId = slot->ItemId;
                    var baseId = HoldingsService.NormalizeBaseItemId(rawId);
                    if (!InventorySafetyEvaluator.CanLockOrTagBaseId(baseId)
                        || gearsetIds.Contains(baseId)
                        || safety.IsItemTracked(baseId))
                    {
                        continue;
                    }

                    if (!manifestEntries.TryGetValue(baseId, out var entry))
                    {
                        continue;
                    }

                    var itemRow = itemSheet.GetRowOrDefault(baseId);
                    if (itemRow is null)
                    {
                        continue;
                    }

                    var effRoute = safety.ResolveEffectiveSlotRoute(entry, itemRow.Value, slot, manifestEntries, gearsetIds, unlockedTop);
                    if (effRoute is not ("Market" or "Armoire" or "Glamour"))
                    {
                        continue;
                    }

                    withdrawCandidates.Add(new RetainerTransferCandidate(
                        armType,
                        slotIdx,
                        rawId,
                        baseId,
                        slot->Flags,
                        slot->Quantity,
                        (int)Math.Max(1, itemRow.Value.StackSize),
                        itemRow.Value.IsUnique,
                        itemRow.Value.Name.ExtractText()));
                }
            }
        }

        withdrawCandidates.Sort((a, b) =>
        {
            var aMerges = playerPartialRoom.GetValueOrDefault((a.RawItemId, a.Flags)) > 0 ? 1 : 0;
            var bMerges = playerPartialRoom.GetValueOrDefault((b.RawItemId, b.Flags)) > 0 ? 1 : 0;
            if (aMerges != bMerges)
            {
                return bMerges.CompareTo(aMerges);
            }

            var aFromRet = InventorySafetyEvaluator.IsRetainerContainer(a.SourceContainer) ? 1 : 0;
            var bFromRet = InventorySafetyEvaluator.IsRetainerContainer(b.SourceContainer) ? 1 : 0;
            return bFromRet.CompareTo(aFromRet);
        });

        var missingTracked = getMissingTrackedQuantities();
        var trackedWithdrawCandidates = new List<RetainerTransferCandidate>();
        if (missingTracked.Count > 0)
        {
            foreach (var retBag in HoldingsService.RetainerBagContainers)
            {
                var container = manager->GetInventoryContainer(retBag);
                if (!InventorySafetyEvaluator.IsContainerReadable(container))
                {
                    continue;
                }

                for (var slotIdx = 0; slotIdx < container->Size; slotIdx++)
                {
                    var slot = InventorySafetyEvaluator.GetSlot(container, slotIdx);
                    if (slot == null || slot->ItemId == 0 || slot->Quantity <= 0)
                    {
                        continue;
                    }

                    var rawId = slot->ItemId;
                    var baseId = HoldingsService.NormalizeBaseItemId(rawId);
                    if (!missingTracked.TryGetValue(baseId, out var neededQty) || neededQty <= 0)
                    {
                        continue;
                    }

                    var itemRow = itemSheet.GetRowOrDefault(baseId);
                    if (itemRow is null)
                    {
                        continue;
                    }

                    if (itemRow.Value.IsUnique && playerExistingBaseIds.Contains(baseId))
                    {
                        continue;
                    }

                    trackedWithdrawCandidates.Add(new RetainerTransferCandidate(
                        retBag,
                        slotIdx,
                        rawId,
                        baseId,
                        slot->Flags,
                        slot->Quantity,
                        (int)Math.Max(1, itemRow.Value.StackSize),
                        itemRow.Value.IsUnique,
                        itemRow.Value.Name.ExtractText()));
                }
            }

            trackedWithdrawCandidates.Sort((a, b) =>
            {
                var aMerges = playerPartialRoom.GetValueOrDefault((a.RawItemId, a.Flags)) > 0 ? 1 : 0;
                var bMerges = playerPartialRoom.GetValueOrDefault((b.RawItemId, b.Flags)) > 0 ? 1 : 0;
                if (aMerges != bMerges)
                {
                    return bMerges.CompareTo(aMerges);
                }

                var aNeed = missingTracked.GetValueOrDefault(a.BaseItemId);
                var bNeed = missingTracked.GetValueOrDefault(b.BaseItemId);
                var aExactOrSmaller = aNeed != int.MaxValue && a.Quantity <= aNeed ? 1 : 0;
                var bExactOrSmaller = bNeed != int.MaxValue && b.Quantity <= bNeed ? 1 : 0;
                if (aExactOrSmaller != bExactOrSmaller)
                {
                    return bExactOrSmaller.CompareTo(aExactOrSmaller);
                }

                return a.Quantity.CompareTo(b.Quantity);
            });
        }

        var steps = new List<TransactionStep>(maxCount);
        var depositCount = 0;
        var withdrawCount = 0;
        var trackedWithdrawCount = 0;
        var blockedByFullRetainer = 0;
        var blockedByFullBag = 0;
        var hasMoreRemaining = false;
        var trackedWithdrawSummary = new List<string>();
        var depositSummary = new List<string>();
        var sortWithdrawSummary = new List<string>();
        var depositSlots = new List<(InventoryType Container, int Slot)>();
        var withdrawSlots = new List<(InventoryType Container, int Slot)>();

        void QueueTrackedWithdrawals()
        {
            var remainingNeeded = new Dictionary<uint, int>(missingTracked);
            foreach (var cand in trackedWithdrawCandidates)
            {
                if (!remainingNeeded.TryGetValue(cand.BaseItemId, out var needed) || needed <= 0)
                {
                    continue;
                }

                if (cand.IsUnique && playerExistingBaseIds.Contains(cand.BaseItemId))
                {
                    continue;
                }

                var isCrystal = cand.BaseItemId is >= 2 and <= 19 || cand.SourceContainer == InventoryType.RetainerCrystals;
                if (isCrystal)
                {
                    var crystalBag = manager->GetInventoryContainer(InventoryType.Crystals);
                    var crystalSlot = crystalBag != null ? InventorySafetyEvaluator.GetSlot(crystalBag, (int)(cand.BaseItemId - 2)) : null;
                    if (crystalSlot != null && crystalSlot->Quantity >= 9999)
                    {
                        continue;
                    }

                    if (steps.Count >= maxCount)
                    {
                        hasMoreRemaining = true;
                        break;
                    }

                    trackedWithdrawCount++;
                    trackedWithdrawSummary.Add($"{cand.ItemName} x{cand.Quantity}");
                    withdrawSlots.Add((cand.SourceContainer, cand.SourceSlot));
                    remainingNeeded[cand.BaseItemId] = 0;

                    var capturedSrc = cand.SourceContainer;
                    var capturedSlot = cand.SourceSlot;
                    var capturedRawId = cand.RawItemId;
                    var capturedFlags = cand.Flags;
                    var capturedStack = cand.StackSize;
                    var preQty = 0;

                    steps.Add(new TransactionStep(
                        $"Withdraw tracked {cand.ItemName}",
                        ResolveActiveRetainerAddonName(),
                        Dispatch: () =>
                        {
                            var mgr = InventoryManager.Instance();
                            if (mgr == null || HoldingsService.GetActiveRetainerId() != activeId)
                            {
                                return false;
                            }

                            var src = InventorySafetyEvaluator.GetSlot(mgr, capturedSrc, capturedSlot);
                            if (src == null || src->ItemId != capturedRawId || src->Quantity <= 0)
                            {
                                return false;
                            }

                            if (!InventorySafetyEvaluator.FindDestinationSlot(
                                    mgr,
                                    InventorySafetyEvaluator.PlayerBagContainers,
                                    capturedRawId,
                                    capturedFlags,
                                    capturedStack,
                                    out var dstBag,
                                    out var dstSlot,
                                    routeCrystalsToCrystalBag: true))
                            {
                                return false;
                            }

                            preQty = src->Quantity;
                            mgr->MoveItemSlot(capturedSrc, (ushort)capturedSlot, dstBag, (ushort)dstSlot, true);
                            return true;
                        },
                        HasServerAck: () =>
                        {
                            var mgr = InventoryManager.Instance();
                            var src = mgr != null ? InventorySafetyEvaluator.GetSlot(mgr, capturedSrc, capturedSlot) : null;
                            if (src == null || src->ItemId == 0 || src->ItemId != capturedRawId || src->Quantity < preQty)
                            {
                                holdings.CaptureActiveRetainerIfOpen(true);
                                onStepCompleted();
                                return true;
                            }

                            return false;
                        },
                        PacingProfile.StandardSlot));
                    continue;
                }

                var withdrawAll = needed == int.MaxValue;
                var takeQty = withdrawAll ? cand.Quantity : Math.Min(cand.Quantity, needed);
                var canStack = cand.StackSize > 1 && (cand.Flags & InventoryItem.ItemFlags.Collectable) == 0;
                var wantsSplit = !withdrawAll && takeQty < cand.Quantity && canStack;
                var canSplit = wantsSplit && simulatedRetainerEmpty >= 1;
                var effectiveQty = canSplit ? takeQty : cand.Quantity;

                var roomKey = (cand.RawItemId, cand.Flags);
                var freeInPartials = canStack ? playerPartialRoom.GetValueOrDefault(roomKey) : 0;
                var fitsCompletelyInPartial = freeInPartials >= effectiveQty;
                var fitsAnyPartial = freeInPartials > 0;
                if (!fitsCompletelyInPartial && simulatedPlayerEmpty <= 0 && (!fitsAnyPartial || canSplit))
                {
                    blockedByFullBag++;
                    continue;
                }

                var requiredSteps = canSplit ? 2 : 1;
                if (steps.Count + requiredSteps > maxCount)
                {
                    hasMoreRemaining = true;
                    break;
                }

                if (fitsCompletelyInPartial)
                {
                    playerPartialRoom[roomKey] = freeInPartials - effectiveQty;
                }
                else if (fitsAnyPartial && !canSplit)
                {
                    playerPartialRoom[roomKey] = 0;
                }
                else
                {
                    simulatedPlayerEmpty--;
                    if (canStack && effectiveQty < cand.StackSize)
                    {
                        playerPartialRoom[roomKey] = cand.StackSize - effectiveQty;
                    }
                }

                if (!canSplit && fitsCompletelyInPartial || (!canSplit && !fitsAnyPartial))
                {
                    simulatedRetainerEmpty++;
                }

                if (cand.IsUnique)
                {
                    playerExistingBaseIds.Add(cand.BaseItemId);
                }

                var movedQty = (!canSplit && fitsAnyPartial && !fitsCompletelyInPartial)
                    ? Math.Min(effectiveQty, freeInPartials)
                    : effectiveQty;

                if (!withdrawAll)
                {
                    remainingNeeded[cand.BaseItemId] = Math.Max(0, needed - movedQty);
                }

                trackedWithdrawCount++;
                var summaryNote = wantsSplit && !canSplit ? " (full stack - retainer full)" : string.Empty;
                trackedWithdrawSummary.Add($"{cand.ItemName} x{effectiveQty}{summaryNote}");
                withdrawSlots.Add((cand.SourceContainer, cand.SourceSlot));

                var capturedSrcBag = cand.SourceContainer;
                var capturedSlotIdx = cand.SourceSlot;
                var capturedRaw = cand.RawItemId;
                var capturedItemFlags = cand.Flags;
                var capturedStackSize = cand.StackSize;

                if (canSplit)
                {
                    var splitQty = takeQty;
                    var preSourceQty = 0;
                    var preOccupied = new HashSet<(InventoryType Container, int Slot)>();
                    var splitFound = false;
                    InventoryType splitContainer = default;
                    var splitSlot = -1;
                    var preSplitQty = 0;

                    steps.Add(new TransactionStep(
                        $"Split {cand.ItemName} (x{splitQty})",
                        ResolveActiveRetainerAddonName(),
                        Dispatch: () =>
                        {
                            var mgr = InventoryManager.Instance();
                            if (mgr == null || HoldingsService.GetActiveRetainerId() != activeId)
                            {
                                return false;
                            }

                            var src = InventorySafetyEvaluator.GetSlot(mgr, capturedSrcBag, capturedSlotIdx);
                            if (src == null || src->ItemId != capturedRaw || src->Flags != capturedItemFlags || src->Quantity <= splitQty)
                            {
                                return false;
                            }

                            preOccupied.Clear();
                            var hasEmptyRetSlot = false;
                            foreach (var retType in InventorySafetyEvaluator.RetainerBagContainers)
                            {
                                var rc = mgr->GetInventoryContainer(retType);
                                if (!InventorySafetyEvaluator.IsContainerReadable(rc))
                                {
                                    continue;
                                }

                                for (var i = 0; i < rc->Size; i++)
                                {
                                    var rs = InventorySafetyEvaluator.GetSlot(rc, i);
                                    if (rs != null && rs->ItemId != 0 && rs->Quantity > 0)
                                    {
                                        preOccupied.Add((retType, i));
                                    }
                                    else if (rs != null)
                                    {
                                        hasEmptyRetSlot = true;
                                    }
                                }
                            }

                            if (!hasEmptyRetSlot)
                            {
                                return false;
                            }

                            preSourceQty = src->Quantity;
                            splitFound = false;
                            splitSlot = -1;
                            mgr->SplitItem(capturedSrcBag, (ushort)capturedSlotIdx, splitQty);
                            return true;
                        },
                        HasServerAck: () =>
                        {
                            var mgr = InventoryManager.Instance();
                            var src = mgr != null ? InventorySafetyEvaluator.GetSlot(mgr, capturedSrcBag, capturedSlotIdx) : null;
                            if (src == null || src->ItemId != capturedRaw || src->Quantity >= preSourceQty)
                            {
                                return false;
                            }

                            foreach (var retType in InventorySafetyEvaluator.RetainerBagContainers)
                            {
                                var rc = mgr->GetInventoryContainer(retType);
                                if (!InventorySafetyEvaluator.IsContainerReadable(rc))
                                {
                                    continue;
                                }

                                for (var i = 0; i < rc->Size; i++)
                                {
                                    if (preOccupied.Contains((retType, i)))
                                    {
                                        continue;
                                    }

                                    var rs = InventorySafetyEvaluator.GetSlot(rc, i);
                                    if (rs != null
                                        && rs->ItemId == capturedRaw
                                        && rs->Flags == capturedItemFlags
                                        && rs->Quantity > 0)
                                    {
                                        splitContainer = retType;
                                        splitSlot = i;
                                        splitFound = true;
                                        return true;
                                    }
                                }
                            }

                            return false;
                        },
                        PacingProfile.StandardSlot));

                    steps.Add(new TransactionStep(
                        $"Withdraw tracked {cand.ItemName} (x{splitQty})",
                        ResolveActiveRetainerAddonName(),
                        Dispatch: () =>
                        {
                            var mgr = InventoryManager.Instance();
                            if (mgr == null || !splitFound || splitSlot < 0 || HoldingsService.GetActiveRetainerId() != activeId)
                            {
                                return false;
                            }

                            var src = InventorySafetyEvaluator.GetSlot(mgr, splitContainer, splitSlot);
                            if (src == null || src->ItemId != capturedRaw || src->Flags != capturedItemFlags || src->Quantity <= 0)
                            {
                                return false;
                            }

                            if (!InventorySafetyEvaluator.FindDestinationSlot(
                                    mgr,
                                    InventorySafetyEvaluator.PlayerBagContainers,
                                    capturedRaw,
                                    capturedItemFlags,
                                    capturedStackSize,
                                    out var dstBag,
                                    out var dstSlot))
                            {
                                return false;
                            }

                            preSplitQty = src->Quantity;
                            mgr->MoveItemSlot(splitContainer, (ushort)splitSlot, dstBag, (ushort)dstSlot, true);
                            return true;
                        },
                        HasServerAck: () =>
                        {
                            var mgr = InventoryManager.Instance();
                            var src = mgr != null ? InventorySafetyEvaluator.GetSlot(mgr, splitContainer, splitSlot) : null;
                            if (src == null || src->ItemId == 0 || src->ItemId != capturedRaw || src->Quantity < preSplitQty)
                            {
                                holdings.CaptureActiveRetainerIfOpen(true);
                                onStepCompleted();
                                return true;
                            }

                            return false;
                        },
                        PacingProfile.StandardSlot));
                }
                else
                {
                    var preQty = 0;
                    steps.Add(new TransactionStep(
                        $"Withdraw tracked {cand.ItemName}",
                        ResolveActiveRetainerAddonName(),
                        Dispatch: () =>
                        {
                            var mgr = InventoryManager.Instance();
                            if (mgr == null || HoldingsService.GetActiveRetainerId() != activeId)
                            {
                                return false;
                            }

                            var src = InventorySafetyEvaluator.GetSlot(mgr, capturedSrcBag, capturedSlotIdx);
                            if (src == null || src->ItemId != capturedRaw || src->Flags != capturedItemFlags || src->Quantity <= 0)
                            {
                                return false;
                            }

                            if (!InventorySafetyEvaluator.FindDestinationSlot(
                                    mgr,
                                    InventorySafetyEvaluator.PlayerBagContainers,
                                    capturedRaw,
                                    capturedItemFlags,
                                    capturedStackSize,
                                    out var dstBag,
                                    out var dstSlot))
                            {
                                return false;
                            }

                            preQty = src->Quantity;
                            mgr->MoveItemSlot(capturedSrcBag, (ushort)capturedSlotIdx, dstBag, (ushort)dstSlot, true);
                            return true;
                        },
                        HasServerAck: () =>
                        {
                            var mgr = InventoryManager.Instance();
                            var src = mgr != null ? InventorySafetyEvaluator.GetSlot(mgr, capturedSrcBag, capturedSlotIdx) : null;
                            if (src == null || src->ItemId == 0 || src->ItemId != capturedRaw || src->Quantity < preQty)
                            {
                                holdings.CaptureActiveRetainerIfOpen(true);
                                onStepCompleted();
                                return true;
                            }

                            return false;
                        },
                        PacingProfile.StandardSlot));
                }
            }
        }

        void QueueDeposits()
        {
            foreach (var cand in depositCandidates)
            {
                if (cand.IsUnique && retainerExistingBaseIds.Contains(cand.BaseItemId))
                {
                    continue;
                }

                var canStack = cand.StackSize > 1 && (cand.Flags & InventoryItem.ItemFlags.Collectable) == 0;
                var roomKey = (cand.RawItemId, cand.Flags);
                var freeInPartials = canStack ? retainerPartialRoom.GetValueOrDefault(roomKey) : 0;
                var fitsPartial = freeInPartials > 0;
                if (!fitsPartial && simulatedRetainerEmpty <= 0)
                {
                    blockedByFullRetainer++;
                    continue;
                }

                if (steps.Count >= maxCount)
                {
                    hasMoreRemaining = true;
                    break;
                }

                var freedPlayerSlot = false;
                if (fitsPartial)
                {
                    if (cand.Quantity <= freeInPartials)
                    {
                        retainerPartialRoom[roomKey] = freeInPartials - cand.Quantity;
                        freedPlayerSlot = true;
                    }
                    else
                    {
                        retainerPartialRoom[roomKey] = 0;
                    }
                }
                else
                {
                    simulatedRetainerEmpty--;
                    freedPlayerSlot = true;
                    if (canStack && cand.Quantity < cand.StackSize)
                    {
                        retainerPartialRoom[roomKey] = cand.StackSize - cand.Quantity;
                    }
                }

                if (freedPlayerSlot && !InventorySafetyEvaluator.IsArmouryContainer(cand.SourceContainer))
                {
                    simulatedPlayerEmpty++;
                }

                if (cand.IsUnique)
                {
                    retainerExistingBaseIds.Add(cand.BaseItemId);
                }

                depositCount++;
                depositSummary.Add(cand.Quantity > 1 ? $"{cand.ItemName} x{cand.Quantity}" : cand.ItemName);
                depositSlots.Add((cand.SourceContainer, cand.SourceSlot));
                var capturedBag = cand.SourceContainer;
                var capturedSlot = cand.SourceSlot;
                var capturedRawId = cand.RawItemId;
                var capturedFlags = cand.Flags;
                var capturedStack = cand.StackSize;
                var preQty = 0;

                steps.Add(new TransactionStep(
                    $"Deposit {cand.ItemName}",
                    ResolveActiveRetainerAddonName(),
                    Dispatch: () =>
                    {
                        var mgr = InventoryManager.Instance();
                        if (mgr == null || HoldingsService.GetActiveRetainerId() != activeId)
                        {
                            return false;
                        }

                        if (InventorySafetyEvaluator.IsArmouryContainer(capturedBag)
                            && !NativeUiHelpers.IsAddonOpenAndReady(gameGui, "ArmouryBoard"))
                        {
                            return false;
                        }

                        var src = InventorySafetyEvaluator.GetSlot(mgr, capturedBag, capturedSlot);
                        if (src == null || src->ItemId != capturedRawId || src->Flags != capturedFlags || src->Quantity <= 0)
                        {
                            return false;
                        }

                        if (!InventorySafetyEvaluator.FindDestinationSlot(
                                mgr,
                                InventorySafetyEvaluator.RetainerBagContainers,
                                capturedRawId,
                                capturedFlags,
                                capturedStack,
                                out var dstContainer,
                                out var dstSlot))
                        {
                            return false;
                        }

                        preQty = src->Quantity;
                        mgr->MoveItemSlot(capturedBag, (ushort)capturedSlot, dstContainer, (ushort)dstSlot, true);
                        return true;
                    },
                    HasServerAck: () =>
                    {
                        var mgr = InventoryManager.Instance();
                        var src = mgr != null ? InventorySafetyEvaluator.GetSlot(mgr, capturedBag, capturedSlot) : null;
                        if (src == null || src->ItemId == 0 || src->ItemId != capturedRawId || src->Quantity < preQty)
                        {
                            holdings.CaptureActiveRetainerIfOpen(true);
                            onStepCompleted();
                            return true;
                        }

                        return false;
                    },
                    PacingProfile.StandardSlot));
            }
        }

        void QueueWithdrawals()
        {
            foreach (var cand in withdrawCandidates)
            {
                if (cand.IsUnique
                    && !InventorySafetyEvaluator.IsArmouryContainer(cand.SourceContainer)
                    && playerExistingBaseIds.Contains(cand.BaseItemId))
                {
                    continue;
                }

                var canStack = cand.StackSize > 1 && (cand.Flags & InventoryItem.ItemFlags.Collectable) == 0;
                var roomKey = (cand.RawItemId, cand.Flags);
                var freeInPartials = canStack ? playerPartialRoom.GetValueOrDefault(roomKey) : 0;
                var fitsPartial = freeInPartials > 0;
                if (!fitsPartial && simulatedPlayerEmpty <= 0)
                {
                    blockedByFullBag++;
                    continue;
                }

                if (steps.Count >= maxCount)
                {
                    hasMoreRemaining = true;
                    break;
                }

                var freedRetainerSlot = false;
                if (fitsPartial)
                {
                    if (cand.Quantity <= freeInPartials)
                    {
                        playerPartialRoom[roomKey] = freeInPartials - cand.Quantity;
                        freedRetainerSlot = true;
                    }
                    else
                    {
                        playerPartialRoom[roomKey] = 0;
                    }
                }
                else
                {
                    simulatedPlayerEmpty--;
                    freedRetainerSlot = true;
                    if (canStack && cand.Quantity < cand.StackSize)
                    {
                        playerPartialRoom[roomKey] = cand.StackSize - cand.Quantity;
                    }
                }

                if (freedRetainerSlot && InventorySafetyEvaluator.IsRetainerContainer(cand.SourceContainer))
                {
                    simulatedRetainerEmpty++;
                }

                if (cand.IsUnique)
                {
                    playerExistingBaseIds.Add(cand.BaseItemId);
                }

                withdrawCount++;
                sortWithdrawSummary.Add(cand.Quantity > 1 ? $"{cand.ItemName} x{cand.Quantity}" : cand.ItemName);
                withdrawSlots.Add((cand.SourceContainer, cand.SourceSlot));
                var capturedSrc = cand.SourceContainer;
                var capturedSlot = cand.SourceSlot;
                var capturedRawId = cand.RawItemId;
                var capturedFlags = cand.Flags;
                var capturedStack = cand.StackSize;
                var preQty = 0;
                var stepTitle = InventorySafetyEvaluator.IsArmouryContainer(capturedSrc)
                    ? $"Move {cand.ItemName} to inventory"
                    : $"Withdraw {cand.ItemName}";

                steps.Add(new TransactionStep(
                    stepTitle,
                    ResolveActiveRetainerAddonName(),
                    Dispatch: () =>
                    {
                        var mgr = InventoryManager.Instance();
                        if (mgr == null || HoldingsService.GetActiveRetainerId() != activeId)
                        {
                            return false;
                        }

                        if (InventorySafetyEvaluator.IsArmouryContainer(capturedSrc)
                            && !NativeUiHelpers.IsAddonOpenAndReady(gameGui, "ArmouryBoard"))
                        {
                            return false;
                        }

                        var src = InventorySafetyEvaluator.GetSlot(mgr, capturedSrc, capturedSlot);
                        if (src == null || src->ItemId != capturedRawId || src->Flags != capturedFlags || src->Quantity <= 0)
                        {
                            return false;
                        }

                        if (!InventorySafetyEvaluator.FindDestinationSlot(
                                mgr,
                                InventorySafetyEvaluator.PlayerBagContainers,
                                capturedRawId,
                                capturedFlags,
                                capturedStack,
                                out var dstBag,
                                out var dstSlot))
                        {
                            return false;
                        }

                        preQty = src->Quantity;
                        mgr->MoveItemSlot(capturedSrc, (ushort)capturedSlot, dstBag, (ushort)dstSlot, true);
                        return true;
                    },
                    HasServerAck: () =>
                    {
                        var mgr = InventoryManager.Instance();
                        var src = mgr != null ? InventorySafetyEvaluator.GetSlot(mgr, capturedSrc, capturedSlot) : null;
                        if (src == null || src->ItemId == 0 || src->ItemId != capturedRawId || src->Quantity < preQty)
                        {
                            holdings.CaptureActiveRetainerIfOpen(true);
                            onStepCompleted();
                            return true;
                        }

                        return false;
                    },
                    PacingProfile.StandardSlot));
            }
        }

        QueueTrackedWithdrawals();

        var runWithdrawalsFirst = simulatedRetainerEmpty < depositCandidates.Count
            && simulatedPlayerEmpty > 0
            && withdrawCandidates.Count > 0;

        if (runWithdrawalsFirst)
        {
            QueueWithdrawals();
            QueueDeposits();
        }
        else
        {
            QueueDeposits();
            QueueWithdrawals();
        }

        return new RetainerSortPlan(
            steps,
            depositCount,
            withdrawCount,
            trackedWithdrawCount,
            blockedByFullRetainer,
            blockedByFullBag,
            hasMoreRemaining,
            trackedWithdrawSummary,
            depositSummary,
            sortWithdrawSummary,
            depositSlots,
            withdrawSlots);
    }

    private string ResolveActiveRetainerAddonName()
    {
        return NativeUiHelpers.IsAddonOpenAndReady(gameGui, "InventoryRetainerLarge")
            ? "InventoryRetainerLarge"
            : "InventoryRetainer";
    }
}
