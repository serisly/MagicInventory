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
        var unlockedProfile = safety.BuildUnlockedJobGearProfile();
        var armouryOpen = NativeUiHelpers.IsAddonOpenAndReady(gameGui, "ArmouryBoard");
        var knownRetainers = holdings.GetKnownRetainersForCurrentCharacter();

        var simulatedRetainerEmpty = 0;
        var retainerExistingBaseIds = new HashSet<uint>();
        var retainerPartialSlots = new Dictionary<(uint ItemId, InventoryItem.ItemFlags Flags), List<int>>();
        var retainerPartialByQuality = new HashSet<(uint BaseId, bool IsHq)>();

        static void AddPartialRoom(Dictionary<(uint, InventoryItem.ItemFlags), List<int>> map, (uint, InventoryItem.ItemFlags) key, int room)
        {
            if (room <= 0)
            {
                return;
            }

            if (!map.TryGetValue(key, out var list))
            {
                list = new List<int>(2);
                map[key] = list;
            }

            list.Add(room);
        }

        static int PeekFirstPartialRoom(Dictionary<(uint, InventoryItem.ItemFlags), List<int>> map, (uint, InventoryItem.ItemFlags) key)
            => map.TryGetValue(key, out var list) && list.Count > 0 ? list[0] : 0;

        static void ConsumeFirstPartialRoom(Dictionary<(uint, InventoryItem.ItemFlags), List<int>> map, (uint, InventoryItem.ItemFlags) key, int qty, out bool completelyMerged)
        {
            completelyMerged = false;
            if (!map.TryGetValue(key, out var list) || list.Count == 0)
            {
                return;
            }

            var room = list[0];
            if (qty < room)
            {
                list[0] = room - qty;
                completelyMerged = true;
            }
            else
            {
                list.RemoveAt(0);
                completelyMerged = qty == room;
            }
        }

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

                var rBaseId = ItemIdentity.NormalizeBaseItemId(rSlot->ItemId);
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
                    AddPartialRoom(retainerPartialSlots, (rSlot->ItemId, rSlot->Flags), rStackSize - rSlot->Quantity);
                    retainerPartialByQuality.Add((rBaseId, ItemIdentity.IsHighQuality(rSlot)));
                }
            }
        }

        var simulatedPlayerEmpty = 0;
        var playerExistingBaseIds = new HashSet<uint>();
        var playerPartialSlots = new Dictionary<(uint ItemId, InventoryItem.ItemFlags Flags), List<int>>();

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

                var pBaseId = ItemIdentity.NormalizeBaseItemId(pSlot->ItemId);
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
                    AddPartialRoom(playerPartialSlots, (pSlot->ItemId, pSlot->Flags), pStackSize - pSlot->Quantity);
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
                    playerExistingBaseIds.Add(ItemIdentity.NormalizeBaseItemId(eSlot->ItemId));
                }
            }
        }

        var missingTracked = getMissingTrackedQuantities();

        var capacityList = new List<RetainerCapacityState>(knownRetainers.Count + 1);
        var capacityById = new Dictionary<ulong, RetainerCapacityState>(knownRetainers.Count + 1);
        var liveActiveFree = simulatedRetainerEmpty;

        foreach (var kr in knownRetainers)
        {
            var captured = kr;
            var isLiveActive = kr.RetainerId == activeId;
            var cap = new RetainerCapacityState
            {
                RetainerId = kr.RetainerId,
                BaseFreeSlots = isLiveActive ? liveActiveFree : holdings.GetKnownRetainerFreeSlots(kr.RetainerId),
                HasBaseItem = isLiveActive
                    ? id => retainerExistingBaseIds.Contains(id)
                    : id => captured.Quantities.GetValueOrDefault(id) > 0,
                HasPartialStack = isLiveActive
                    ? (id, hq) => retainerPartialByQuality.Contains((id, hq))
                    : (id, hq) => holdings.HasPartialStackOnRetainer(captured.RetainerId, id, hq),
                IsKnownFullFor = isLiveActive
                    ? (id, hq, stack) => liveActiveFree <= 0 && !(stack > 1 && retainerPartialByQuality.Contains((id, hq)))
                    : (id, hq, stack) => holdings.IsRetainerKnownFullFor(captured.RetainerId, id, hq, stack),
            };

            cap.ProjectedWithdrawalFreedSlots = isLiveActive
                ? ProjectLiveActiveRetainerWithdrawals(
                    manager,
                    liveActiveFree,
                    manifestEntries,
                    itemSheet,
                    gearsetIds,
                    unlockedProfile,
                    playerExistingBaseIds,
                    missingTracked)
                : ProjectDirectWithdrawalsForRetainer(
                    kr,
                    cap.BaseFreeSlots,
                    manifestEntries,
                    itemSheet,
                    gearsetIds,
                    unlockedProfile,
                    playerUniqueBaseIds: playerExistingBaseIds,
                    missingTracked);

            capacityList.Add(cap);
            capacityById[kr.RetainerId] = cap;
        }

        if (!capacityById.ContainsKey(activeId))
        {
            var activeCap = new RetainerCapacityState
            {
                RetainerId = activeId,
                BaseFreeSlots = liveActiveFree,
                ProjectedWithdrawalFreedSlots = ProjectLiveActiveRetainerWithdrawals(
                    manager,
                    liveActiveFree,
                    manifestEntries,
                    itemSheet,
                    gearsetIds,
                    unlockedProfile,
                    playerExistingBaseIds,
                    missingTracked),
                HasBaseItem = id => retainerExistingBaseIds.Contains(id),
                HasPartialStack = (id, hq) => retainerPartialByQuality.Contains((id, hq)),
                IsKnownFullFor = (id, hq, stack) => liveActiveFree <= 0 && !(stack > 1 && retainerPartialByQuality.Contains((id, hq))),
            };
            capacityList.Add(activeCap);
            capacityById[activeId] = activeCap;
        }

        RetainerTransferPolicy.InitializeProjectedBudgets(capacityList, simulatedPlayerEmpty);

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
                var baseId = ItemIdentity.NormalizeBaseItemId(rawId);
                if (!ItemIdentity.CanLockOrTagBaseId(baseId)
                    || gearsetIds.Contains(baseId)
                    || safety.IsItemTracked(baseId))
                {
                    continue;
                }

                var itemRow = itemSheet.GetRowOrDefault(baseId);
                if (itemRow is null || ItemIdentity.IsSoulCrystal(itemRow.Value) || ItemIdentity.IsRelicRarity(itemRow.Value))
                {
                    continue;
                }

                var stackSize = (int)Math.Max(1, itemRow.Value.StackSize);
                var isHq = ItemIdentity.IsHighQuality(slot);

                if (manifestEntries.TryGetValue(baseId, out var entry))
                {
                    var effRoute = safety.ResolveEffectiveSlotRoute(entry, itemRow.Value, slot, manifestEntries, gearsetIds, unlockedProfile);
                    if (effRoute != "Retainer" || entry.TargetRetainerId == 0)
                    {
                        continue;
                    }

                    var decision = RetainerTransferPolicy.ResolveDepositTarget(
                        entry.TargetRetainerId,
                        baseId,
                        isHq,
                        itemRow.Value.IsUnique,
                        stackSize,
                        capacityList,
                        capacityById,
                        consumeBudget: true);
                    if (decision.TargetRetainerId != activeId)
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

                if (itemRow.Value.IsUnique && retainerExistingBaseIds.Contains(baseId))
                {
                    continue;
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
            var aMerges = PeekFirstPartialRoom(retainerPartialSlots, (a.RawItemId, a.Flags)) > 0 ? 1 : 0;
            var bMerges = PeekFirstPartialRoom(retainerPartialSlots, (b.RawItemId, b.Flags)) > 0 ? 1 : 0;
            return bMerges.CompareTo(aMerges);
        });

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
                var baseId = ItemIdentity.NormalizeBaseItemId(rawId);
                if (!ItemIdentity.CanLockOrTagBaseId(baseId)
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
                if (itemRow is null
                    || ItemIdentity.IsSoulCrystal(itemRow.Value)
                    || ItemIdentity.IsRelicRarity(itemRow.Value))
                {
                    continue;
                }

                if (itemRow.Value.IsUnique && playerExistingBaseIds.Contains(baseId))
                {
                    continue;
                }

                var stackSize = (int)Math.Max(1, itemRow.Value.StackSize);
                var isHq = ItemIdentity.IsHighQuality(slot);
                var shouldWithdraw = false;

                if (string.Equals(entry.Route, "Keep", StringComparison.OrdinalIgnoreCase))
                {
                    shouldWithdraw = safety.HasExplicitKeepOverride(baseId);
                }
                else
                {
                    var effRoute = safety.ResolveEffectiveSlotRoute(entry, itemRow.Value, slot, manifestEntries, gearsetIds, unlockedProfile);
                    if (effRoute != null)
                    {
                        if (RetainerBellEvaluator.IsWithdrawableRoute(effRoute))
                        {
                            shouldWithdraw = true;
                        }
                        else if (string.Equals(effRoute, "Retainer", StringComparison.OrdinalIgnoreCase)
                            && entry.TargetRetainerId != 0
                            && entry.TargetRetainerId != activeId)
                        {
                            var funnel = RetainerTransferPolicy.EvaluateCrossRetainerFunnel(
                                activeId,
                                entry.TargetRetainerId,
                                baseId,
                                hasNq: !isHq,
                                hasHq: isHq,
                                itemRow.Value.IsUnique,
                                stackSize,
                                capacityById,
                                consumeBudget: true);
                            shouldWithdraw = funnel.CanFunnel;
                        }
                    }
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
                    var baseId = ItemIdentity.NormalizeBaseItemId(rawId);
                    if (!ItemIdentity.CanLockOrTagBaseId(baseId)
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
                    if (itemRow is null
                        || ItemIdentity.IsSoulCrystal(itemRow.Value)
                        || ItemIdentity.IsRelicRarity(itemRow.Value))
                    {
                        continue;
                    }

                    var effRoute = safety.ResolveEffectiveSlotRoute(entry, itemRow.Value, slot, manifestEntries, gearsetIds, unlockedProfile);
                    if (effRoute != "Market")
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
            var aMerges = PeekFirstPartialRoom(playerPartialSlots, (a.RawItemId, a.Flags)) > 0 ? 1 : 0;
            var bMerges = PeekFirstPartialRoom(playerPartialSlots, (b.RawItemId, b.Flags)) > 0 ? 1 : 0;
            if (aMerges != bMerges)
            {
                return bMerges.CompareTo(aMerges);
            }

            var aFromRet = InventorySafetyEvaluator.IsRetainerContainer(a.SourceContainer) ? 1 : 0;
            var bFromRet = InventorySafetyEvaluator.IsRetainerContainer(b.SourceContainer) ? 1 : 0;
            return bFromRet.CompareTo(aFromRet);
        });

        var trackedWithdrawCandidates = new List<RetainerTransferCandidate>();
        if (missingTracked.Count > 0)
        {
            foreach (var retBag in InventorySafetyEvaluator.RetainerBagContainersWithCrystals)
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
                    var baseId = ItemIdentity.NormalizeBaseItemId(rawId);
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
                var aMerges = PeekFirstPartialRoom(playerPartialSlots, (a.RawItemId, a.Flags)) > 0 ? 1 : 0;
                var bMerges = PeekFirstPartialRoom(playerPartialSlots, (b.RawItemId, b.Flags)) > 0 ? 1 : 0;
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

                var isCrystal = ItemIdentity.IsCrystalItemId(cand.BaseItemId) || cand.SourceContainer == InventoryType.RetainerCrystals;
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

                    steps.Add(BuildMoveStep(
                        $"Withdraw tracked {cand.ItemName}",
                        activeId,
                        cand.SourceContainer,
                        cand.SourceSlot,
                        cand.RawItemId,
                        cand.Flags,
                        cand.StackSize,
                        InventorySafetyEvaluator.PlayerBagContainers,
                        checkSourceFlags: false,
                        routeCrystalsToCrystalBag: true));
                    continue;
                }

                var withdrawAll = needed == int.MaxValue;
                var takeQty = withdrawAll ? cand.Quantity : Math.Min(cand.Quantity, needed);
                var canStack = cand.StackSize > 1 && (cand.Flags & InventoryItem.ItemFlags.Collectable) == 0;
                var wantsSplit = !withdrawAll && takeQty < cand.Quantity && canStack;
                var canSplit = wantsSplit && simulatedRetainerEmpty >= 1;
                var effectiveQty = canSplit ? takeQty : cand.Quantity;

                var roomKey = (cand.RawItemId, cand.Flags);
                var firstPartialRoom = canStack ? PeekFirstPartialRoom(playerPartialSlots, roomKey) : 0;
                var fitsCompletelyInPartial = firstPartialRoom >= effectiveQty;
                var fitsAnyPartial = firstPartialRoom > 0;
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

                var splitTargetsEmptySlot = canSplit && !fitsCompletelyInPartial;
                if (fitsCompletelyInPartial || (fitsAnyPartial && !canSplit))
                {
                    ConsumeFirstPartialRoom(playerPartialSlots, roomKey, effectiveQty, out _);
                }
                else
                {
                    simulatedPlayerEmpty--;
                    if (canStack && effectiveQty < cand.StackSize)
                    {
                        AddPartialRoom(playerPartialSlots, roomKey, cand.StackSize - effectiveQty);
                    }
                }

                if (!canSplit && (fitsCompletelyInPartial || !fitsAnyPartial))
                {
                    simulatedRetainerEmpty++;
                }

                if (cand.IsUnique)
                {
                    playerExistingBaseIds.Add(cand.BaseItemId);
                }

                var movedQty = (!canSplit && fitsAnyPartial && !fitsCompletelyInPartial)
                    ? Math.Min(effectiveQty, firstPartialRoom)
                    : effectiveQty;

                if (!withdrawAll)
                {
                    remainingNeeded[cand.BaseItemId] = Math.Max(0, needed - movedQty);
                }

                trackedWithdrawCount++;
                var summaryNote = wantsSplit && !canSplit ? " (full stack - retainer full)" : string.Empty;
                trackedWithdrawSummary.Add($"{cand.ItemName} x{effectiveQty}{summaryNote}");
                withdrawSlots.Add((cand.SourceContainer, cand.SourceSlot));

                if (canSplit)
                {
                    AddSplitAndWithdrawSteps(steps, activeId, cand, takeQty, allowPartialMergeForSplit: !splitTargetsEmptySlot);
                }
                else
                {
                    steps.Add(BuildMoveStep(
                        $"Withdraw tracked {cand.ItemName}",
                        activeId,
                        cand.SourceContainer,
                        cand.SourceSlot,
                        cand.RawItemId,
                        cand.Flags,
                        cand.StackSize,
                        InventorySafetyEvaluator.PlayerBagContainers));
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
                var firstPartialRoom = canStack ? PeekFirstPartialRoom(retainerPartialSlots, roomKey) : 0;
                var fitsPartial = firstPartialRoom > 0;
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
                    ConsumeFirstPartialRoom(retainerPartialSlots, roomKey, cand.Quantity, out freedPlayerSlot);
                }
                else
                {
                    simulatedRetainerEmpty--;
                    freedPlayerSlot = true;
                    if (canStack && cand.Quantity < cand.StackSize)
                    {
                        AddPartialRoom(retainerPartialSlots, roomKey, cand.StackSize - cand.Quantity);
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

                steps.Add(BuildMoveStep(
                    $"Deposit {cand.ItemName}",
                    activeId,
                    cand.SourceContainer,
                    cand.SourceSlot,
                    cand.RawItemId,
                    cand.Flags,
                    cand.StackSize,
                    InventorySafetyEvaluator.RetainerBagContainers));
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
                var firstPartialRoom = canStack ? PeekFirstPartialRoom(playerPartialSlots, roomKey) : 0;
                var fitsPartial = firstPartialRoom > 0;
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
                    ConsumeFirstPartialRoom(playerPartialSlots, roomKey, cand.Quantity, out freedRetainerSlot);
                }
                else
                {
                    simulatedPlayerEmpty--;
                    freedRetainerSlot = true;
                    if (canStack && cand.Quantity < cand.StackSize)
                    {
                        AddPartialRoom(playerPartialSlots, roomKey, cand.StackSize - cand.Quantity);
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

                var stepTitle = InventorySafetyEvaluator.IsArmouryContainer(cand.SourceContainer)
                    ? $"Move {cand.ItemName} to inventory"
                    : $"Withdraw {cand.ItemName}";

                steps.Add(BuildMoveStep(
                    stepTitle,
                    activeId,
                    cand.SourceContainer,
                    cand.SourceSlot,
                    cand.RawItemId,
                    cand.Flags,
                    cand.StackSize,
                    InventorySafetyEvaluator.PlayerBagContainers));
            }
        }

        if (simulatedPlayerEmpty == 0 && depositCandidates.Count > 0 && simulatedRetainerEmpty > 0)
        {
            QueueDeposits();
            QueueTrackedWithdrawals();
            QueueWithdrawals();
        }
        else
        {
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

    private int ProjectLiveActiveRetainerWithdrawals(
        InventoryManager* manager,
        int baseFreeSlots,
        IReadOnlyDictionary<uint, MagicSortEntry> manifestEntries,
        Lumina.Excel.ExcelSheet<Item> itemSheet,
        HashSet<uint> gearsetIds,
        UnlockedJobGearProfile unlockedProfile,
        HashSet<uint> playerUniqueBaseIds,
        IReadOnlyDictionary<uint, int> missingTracked)
    {
        var projectedFreed = 0;
        var remainingTracked = missingTracked.Count > 0 ? new Dictionary<uint, int>(missingTracked) : null;

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

                var baseId = ItemIdentity.NormalizeBaseItemId(slot->ItemId);
                if (baseId == 0 || ItemIdentity.IsCrystalItemId(baseId))
                {
                    continue;
                }

                if (remainingTracked != null && remainingTracked.TryGetValue(baseId, out var needQty) && needQty > 0)
                {
                    var tRow = itemSheet.GetRowOrDefault(baseId);
                    if (tRow is not null && tRow.Value.IsUnique && playerUniqueBaseIds.Contains(baseId))
                    {
                        continue;
                    }

                    if (needQty == int.MaxValue || slot->Quantity <= needQty || baseFreeSlots == 0)
                    {
                        projectedFreed++;
                    }

                    if (needQty != int.MaxValue)
                    {
                        remainingTracked[baseId] = Math.Max(0, needQty - slot->Quantity);
                    }

                    continue;
                }

                if (manifestEntries.Count == 0
                    || !ItemIdentity.CanLockOrTagBaseId(baseId)
                    || gearsetIds.Contains(baseId)
                    || safety.IsItemTracked(baseId)
                    || !manifestEntries.TryGetValue(baseId, out var entry))
                {
                    continue;
                }

                var row = itemSheet.GetRowOrDefault(baseId);
                if (row is null
                    || ItemIdentity.IsCurrencyOrCrystal(baseId, row.Value)
                    || ItemIdentity.IsSoulCrystal(row.Value)
                    || ItemIdentity.IsRelicRarity(row.Value)
                    || (row.Value.IsUnique && playerUniqueBaseIds.Contains(baseId)))
                {
                    continue;
                }

                if (string.Equals(entry.Route, "Keep", StringComparison.OrdinalIgnoreCase))
                {
                    if (safety.HasExplicitKeepOverride(baseId))
                    {
                        projectedFreed++;
                    }

                    continue;
                }

                var effRoute = safety.ResolveEffectiveSlotRoute(entry, row.Value, slot, manifestEntries, gearsetIds, unlockedProfile);
                if (RetainerBellEvaluator.IsWithdrawableRoute(effRoute))
                {
                    projectedFreed++;
                }
            }
        }

        return projectedFreed;
    }

    private int ProjectDirectWithdrawalsForRetainer(
        KnownRetainerInfo ret,
        int baseFreeSlots,
        IReadOnlyDictionary<uint, MagicSortEntry> manifestEntries,
        Lumina.Excel.ExcelSheet<Item> itemSheet,
        HashSet<uint> gearsetIds,
        UnlockedJobGearProfile unlockedProfile,
        HashSet<uint> playerUniqueBaseIds,
        IReadOnlyDictionary<uint, int> missingTracked)
    {
        var projectedFreed = 0;
        if (missingTracked.Count > 0)
        {
            foreach (var (trackedId, needQty) in missingTracked)
            {
                var retQty = ret.Quantities.GetValueOrDefault(trackedId);
                if (needQty <= 0 || ItemIdentity.IsCrystalItemId(trackedId) || retQty <= 0)
                {
                    continue;
                }

                var tRow = itemSheet.GetRowOrDefault(trackedId);
                if (tRow is not null && tRow.Value.IsUnique && playerUniqueBaseIds.Contains(trackedId))
                {
                    continue;
                }

                if (needQty == int.MaxValue || retQty <= needQty || baseFreeSlots == 0)
                {
                    projectedFreed++;
                }
            }
        }

        if (manifestEntries.Count == 0 || ret.Quantities.Count == 0)
        {
            return projectedFreed;
        }

        foreach (var (baseId, qty) in ret.Quantities)
        {
            if (qty <= 0
                || !ItemIdentity.CanLockOrTagBaseId(baseId)
                || gearsetIds.Contains(baseId)
                || safety.IsItemTracked(baseId)
                || !manifestEntries.TryGetValue(baseId, out var entry))
            {
                continue;
            }

            var row = itemSheet.GetRowOrDefault(baseId);
            if (row is null
                || ItemIdentity.IsCurrencyOrCrystal(baseId, row.Value)
                || ItemIdentity.IsSoulCrystal(row.Value)
                || ItemIdentity.IsRelicRarity(row.Value)
                || (row.Value.IsUnique && playerUniqueBaseIds.Contains(baseId)))
            {
                continue;
            }

            if (string.Equals(entry.Route, "Keep", StringComparison.OrdinalIgnoreCase))
            {
                if (safety.HasExplicitKeepOverride(baseId))
                {
                    projectedFreed++;
                }

                continue;
            }

            var hqQty = ret.HqQuantities?.GetValueOrDefault(baseId) ?? 0;
            var effRoute = safety.ResolveEffectiveSlotRoute(
                entry,
                row.Value,
                null,
                manifestEntries,
                gearsetIds,
                unlockedProfile,
                hasNq: qty > hqQty,
                hasHq: hqQty > 0);
            if (RetainerBellEvaluator.IsWithdrawableRoute(effRoute))
            {
                projectedFreed++;
            }
        }

        return projectedFreed;
    }

    private TransactionStep BuildMoveStep(
        string stepTitle,
        ulong activeId,
        InventoryType srcContainer,
        int srcSlot,
        uint rawItemId,
        InventoryItem.ItemFlags flags,
        int stackSize,
        InventoryType[] destContainers,
        bool checkSourceFlags = true,
        bool allowPartialMerge = true,
        bool routeCrystalsToCrystalBag = false)
    {
        var preQty = 0;
        return new TransactionStep(
            stepTitle,
            ResolveActiveRetainerAddonName(),
            Dispatch: () =>
            {
                var mgr = InventoryManager.Instance();
                if (mgr == null || HoldingsService.GetActiveRetainerId() != activeId)
                {
                    return false;
                }

                if (InventorySafetyEvaluator.IsArmouryContainer(srcContainer)
                    && !NativeUiHelpers.IsAddonOpenAndReady(gameGui, "ArmouryBoard"))
                {
                    return false;
                }

                var src = InventorySafetyEvaluator.GetSlot(mgr, srcContainer, srcSlot);
                if (src == null
                    || src->ItemId != rawItemId
                    || (checkSourceFlags && src->Flags != flags)
                    || src->Quantity <= 0)
                {
                    return false;
                }

                if (!InventorySafetyEvaluator.FindDestinationSlot(
                        mgr,
                        destContainers,
                        rawItemId,
                        flags,
                        stackSize,
                        out var dstContainer,
                        out var dstSlot,
                        allowPartialMerge: allowPartialMerge,
                        routeCrystalsToCrystalBag: routeCrystalsToCrystalBag,
                        incomingQty: src->Quantity))
                {
                    return false;
                }

                preQty = src->Quantity;
                mgr->MoveItemSlot(srcContainer, (ushort)srcSlot, dstContainer, (ushort)dstSlot, true);
                return true;
            },
            HasServerAck: () =>
            {
                var mgr = InventoryManager.Instance();
                var src = mgr != null ? InventorySafetyEvaluator.GetSlot(mgr, srcContainer, srcSlot) : null;
                if (src == null || src->ItemId == 0 || src->ItemId != rawItemId || src->Quantity < preQty)
                {
                    onStepCompleted();
                    return true;
                }

                return false;
            },
            PacingProfile.StandardSlot);
    }

    private void AddSplitAndWithdrawSteps(
        List<TransactionStep> steps,
        ulong activeId,
        RetainerTransferCandidate cand,
        int splitQty,
        bool allowPartialMergeForSplit)
    {
        var capturedSrcBag = cand.SourceContainer;
        var capturedSlotIdx = cand.SourceSlot;
        var capturedRaw = cand.RawItemId;
        var capturedItemFlags = cand.Flags;
        var capturedStackSize = cand.StackSize;

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
            PacingProfile.StandardSlot,
            SkipOnFailureCount: 1));

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
                        out var dstSlot,
                        allowPartialMerge: allowPartialMergeForSplit,
                        incomingQty: src->Quantity))
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
                    onStepCompleted();
                    return true;
                }

                return false;
            },
            PacingProfile.StandardSlot));
    }

    private string ResolveActiveRetainerAddonName()
        => NativeUiHelpers.GetOpenRetainerAddonName(gameGui) ?? "InventoryRetainerLarge";
}
