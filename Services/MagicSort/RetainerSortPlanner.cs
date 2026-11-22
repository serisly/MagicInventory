using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel;
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

    private static readonly RetainerSortPlan EmptyPlan = new(
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

    private sealed class TransferPlanBuilder
    {
        public ulong ActiveRetainerId { get; }
        public int MaxSteps { get; }
        public SlotSpaceTracker RetainerSpace { get; }
        public SlotSpaceTracker PlayerSpace { get; }
        public List<TransactionStep> Steps { get; }
        public List<string> TrackedWithdrawSummary { get; } = new();
        public List<string> DepositSummary { get; } = new();
        public List<string> SortWithdrawSummary { get; } = new();
        public List<(InventoryType Container, int Slot)> DepositSlots { get; } = new();
        public List<(InventoryType Container, int Slot)> WithdrawSlots { get; } = new();
        public int DepositCount { get; set; }
        public int WithdrawCount { get; set; }
        public int TrackedWithdrawCount { get; set; }
        public int BlockedByFullRetainer { get; set; }
        public int BlockedByFullBag { get; set; }
        public bool HasMoreRemaining { get; set; }

        public TransferPlanBuilder(ulong activeRetainerId, int maxSteps, SlotSpaceTracker retainerSpace, SlotSpaceTracker playerSpace)
        {
            ActiveRetainerId = activeRetainerId;
            MaxSteps = maxSteps;
            RetainerSpace = retainerSpace;
            PlayerSpace = playerSpace;
            Steps = new List<TransactionStep>(maxSteps);
        }

        public RetainerSortPlan Build() => new(
            Steps,
            DepositCount,
            WithdrawCount,
            TrackedWithdrawCount,
            BlockedByFullRetainer,
            BlockedByFullBag,
            HasMoreRemaining,
            TrackedWithdrawSummary,
            DepositSummary,
            SortWithdrawSummary,
            DepositSlots,
            WithdrawSlots);
    }

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
            return EmptyPlan;
        }

        var gearsetIds = safety.BuildGearsetItemIds();
        var unlockedProfile = safety.BuildUnlockedJobGearProfile();
        var armouryOpen = NativeUiHelpers.IsAddonOpenAndReady(gameGui, "ArmouryBoard");
        var knownRetainers = holdings.GetKnownRetainersForCurrentCharacter();

        var retainerSpace = new SlotSpaceTracker();
        ScanLiveContainers(manager, itemSheet, InventorySafetyEvaluator.RetainerBagContainers, retainerSpace);

        var playerSpace = new SlotSpaceTracker();
        ScanLiveContainers(manager, itemSheet, InventorySafetyEvaluator.PlayerBagContainers, playerSpace);
        ScanOwnedBaseIds(manager, InventorySafetyEvaluator.ArmouryGearContainers.Append(InventoryType.EquippedItems), playerSpace);

        var missingTracked = getMissingTrackedQuantities();
        BuildRetainerCapacities(
            manager,
            activeId,
            knownRetainers,
            retainerSpace,
            playerSpace,
            manifestEntries,
            itemSheet,
            gearsetIds,
            unlockedProfile,
            missingTracked,
            out var capacityList,
            out var capacityById);

        var depositCandidates = CollectDepositCandidates(
            manager,
            activeId,
            armouryOpen,
            retainerSpace,
            manifestEntries,
            itemSheet,
            gearsetIds,
            unlockedProfile,
            capacityList,
            capacityById);

        var withdrawCandidates = CollectWithdrawCandidates(
            manager,
            activeId,
            armouryOpen,
            playerSpace,
            manifestEntries,
            itemSheet,
            gearsetIds,
            unlockedProfile,
            capacityById);

        var trackedWithdrawCandidates = CollectTrackedWithdrawCandidates(
            manager,
            playerSpace,
            itemSheet,
            missingTracked);

        var state = new TransferPlanBuilder(activeId, maxCount, retainerSpace, playerSpace);

        if (playerSpace.EmptySlots == 0 && depositCandidates.Count > 0 && retainerSpace.EmptySlots > 0)
        {
            QueueDeposits(state, depositCandidates);
            QueueTrackedWithdrawals(state, manager, trackedWithdrawCandidates, missingTracked);
            QueueWithdrawals(state, withdrawCandidates);
        }
        else
        {
            QueueTrackedWithdrawals(state, manager, trackedWithdrawCandidates, missingTracked);

            var runWithdrawalsFirst = retainerSpace.EmptySlots < depositCandidates.Count
                && playerSpace.EmptySlots > 0
                && withdrawCandidates.Count > 0;

            if (runWithdrawalsFirst)
            {
                QueueWithdrawals(state, withdrawCandidates);
                QueueDeposits(state, depositCandidates);
            }
            else
            {
                QueueDeposits(state, depositCandidates);
                QueueWithdrawals(state, withdrawCandidates);
            }
        }

        return state.Build();
    }

    private static void ScanLiveContainers(
        InventoryManager* manager,
        ExcelSheet<Item> itemSheet,
        InventoryType[] containers,
        SlotSpaceTracker tracker)
    {
        foreach (var containerType in containers)
        {
            var container = manager->GetInventoryContainer(containerType);
            if (!InventorySafetyEvaluator.IsContainerReadable(container))
            {
                continue;
            }

            for (var i = 0; i < container->Size; i++)
            {
                var slot = InventorySafetyEvaluator.GetSlot(container, i);
                if (slot == null)
                {
                    continue;
                }

                if (slot->ItemId == 0 || slot->Quantity <= 0)
                {
                    tracker.RecordEmptySlot();
                    continue;
                }

                var baseId = ItemIdentity.NormalizeBaseItemId(slot->ItemId);
                var row = itemSheet.GetRowOrDefault(baseId);
                if (row is null)
                {
                    tracker.RecordExistingBaseId(baseId);
                    continue;
                }

                var stackSize = (int)Math.Max(1, row.Value.StackSize);
                tracker.RecordOccupiedSlot(slot->ItemId, baseId, slot->Flags, slot->Quantity, stackSize, ItemIdentity.IsHighQuality(slot));
            }
        }
    }

    private static void ScanOwnedBaseIds(
        InventoryManager* manager,
        IEnumerable<InventoryType> containers,
        SlotSpaceTracker tracker)
    {
        foreach (var containerType in containers)
        {
            var container = manager->GetInventoryContainer(containerType);
            if (container == null || container->Items == null)
            {
                continue;
            }

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot != null && slot->ItemId != 0 && slot->Quantity > 0)
                {
                    tracker.RecordExistingBaseId(ItemIdentity.NormalizeBaseItemId(slot->ItemId));
                }
            }
        }
    }

    private void BuildRetainerCapacities(
        InventoryManager* manager,
        ulong activeId,
        IReadOnlyList<KnownRetainerInfo> knownRetainers,
        SlotSpaceTracker retainerSpace,
        SlotSpaceTracker playerSpace,
        IReadOnlyDictionary<uint, MagicSortEntry> manifestEntries,
        ExcelSheet<Item> itemSheet,
        HashSet<uint> gearsetIds,
        UnlockedJobGearProfile unlockedProfile,
        IReadOnlyDictionary<uint, int> missingTracked,
        out List<RetainerCapacityState> capacityList,
        out Dictionary<ulong, RetainerCapacityState> capacityById)
    {
        capacityList = new List<RetainerCapacityState>(knownRetainers.Count + 1);
        capacityById = new Dictionary<ulong, RetainerCapacityState>(knownRetainers.Count + 1);
        var liveActiveFree = retainerSpace.EmptySlots;

        RetainerCapacityState CreateLiveActiveCapacity(ulong retId) => new()
        {
            RetainerId = retId,
            BaseFreeSlots = liveActiveFree,
            ProjectedWithdrawalFreedSlots = ProjectLiveActiveRetainerWithdrawals(
                manager,
                liveActiveFree,
                manifestEntries,
                itemSheet,
                gearsetIds,
                unlockedProfile,
                playerSpace,
                missingTracked),
            HasBaseItem = retainerSpace.HasBaseItem,
            HasPartialStack = retainerSpace.HasPartialStackForQuality,
            IsKnownFullFor = (id, hq, stack) => liveActiveFree <= 0 && !(stack > 1 && retainerSpace.HasPartialStackForQuality(id, hq)),
        };

        foreach (var kr in knownRetainers)
        {
            var cap = kr.RetainerId == activeId
                ? CreateLiveActiveCapacity(kr.RetainerId)
                : new RetainerCapacityState
                {
                    RetainerId = kr.RetainerId,
                    BaseFreeSlots = holdings.GetKnownRetainerFreeSlots(kr.RetainerId),
                    HasBaseItem = id => kr.Quantities.GetValueOrDefault(id) > 0,
                    HasPartialStack = (id, hq) => holdings.HasPartialStackOnRetainer(kr.RetainerId, id, hq),
                    IsKnownFullFor = (id, hq, stack) => holdings.IsRetainerKnownFullFor(kr.RetainerId, id, hq, stack),
                    ProjectedWithdrawalFreedSlots = ProjectDirectWithdrawalsForRetainer(
                        kr,
                        holdings.GetKnownRetainerFreeSlots(kr.RetainerId),
                        manifestEntries,
                        itemSheet,
                        gearsetIds,
                        unlockedProfile,
                        playerSpace,
                        missingTracked),
                };

            capacityList.Add(cap);
            capacityById[kr.RetainerId] = cap;
        }

        if (!capacityById.ContainsKey(activeId))
        {
            var activeCap = CreateLiveActiveCapacity(activeId);
            capacityList.Add(activeCap);
            capacityById[activeId] = activeCap;
        }

        RetainerTransferPolicy.InitializeProjectedBudgets(capacityList, playerSpace.EmptySlots);
    }

    private List<RetainerTransferCandidate> CollectDepositCandidates(
        InventoryManager* manager,
        ulong activeId,
        bool armouryOpen,
        SlotSpaceTracker retainerSpace,
        IReadOnlyDictionary<uint, MagicSortEntry> manifestEntries,
        ExcelSheet<Item> itemSheet,
        HashSet<uint> gearsetIds,
        UnlockedJobGearProfile unlockedProfile,
        List<RetainerCapacityState> capacityList,
        Dictionary<ulong, RetainerCapacityState> capacityById)
    {
        var depositSources = armouryOpen
            ? InventorySafetyEvaluator.PlayerBagContainers.Concat(InventorySafetyEvaluator.ArmouryGearContainers)
            : InventorySafetyEvaluator.PlayerBagContainers;

        var candidates = new List<RetainerTransferCandidate>();
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
                    if (!effRoute.HasValue || !effRoute.Value.IsSpecificRetainer)
                    {
                        continue;
                    }

                    var decision = RetainerTransferPolicy.ResolveDepositTarget(
                        effRoute.Value.RetainerId,
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
                else if (fromArmoury
                    || !retainerSpace.HasBaseItem(baseId)
                    || !InventorySafetyEvaluator.IsEligibleForLiveRetainerDuplicateDeposit(itemRow.Value))
                {
                    continue;
                }

                if (itemRow.Value.IsUnique && retainerSpace.HasBaseItem(baseId))
                {
                    continue;
                }

                candidates.Add(new RetainerTransferCandidate(
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

        candidates.Sort((a, b) =>
        {
            var aMerges = retainerSpace.HasPartialRoom(a.RawItemId, a.Flags) ? 1 : 0;
            var bMerges = retainerSpace.HasPartialRoom(b.RawItemId, b.Flags) ? 1 : 0;
            return bMerges.CompareTo(aMerges);
        });

        return candidates;
    }

    private List<RetainerTransferCandidate> CollectWithdrawCandidates(
        InventoryManager* manager,
        ulong activeId,
        bool armouryOpen,
        SlotSpaceTracker playerSpace,
        IReadOnlyDictionary<uint, MagicSortEntry> manifestEntries,
        ExcelSheet<Item> itemSheet,
        HashSet<uint> gearsetIds,
        UnlockedJobGearProfile unlockedProfile,
        Dictionary<ulong, RetainerCapacityState> capacityById)
    {
        var candidates = new List<RetainerTransferCandidate>();
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
                    || safety.IsItemTracked(baseId)
                    || !manifestEntries.TryGetValue(baseId, out var entry))
                {
                    continue;
                }

                var itemRow = itemSheet.GetRowOrDefault(baseId);
                if (itemRow is null
                    || ItemIdentity.IsSoulCrystal(itemRow.Value)
                    || ItemIdentity.IsRelicRarity(itemRow.Value)
                    || (itemRow.Value.IsUnique && playerSpace.HasBaseItem(baseId)))
                {
                    continue;
                }

                var stackSize = (int)Math.Max(1, itemRow.Value.StackSize);
                var isHq = ItemIdentity.IsHighQuality(slot);
                var shouldWithdraw = false;

                if (entry.Route.IsKeep)
                {
                    shouldWithdraw = safety.HasExplicitKeepOverride(baseId);
                }
                else
                {
                    var effRoute = safety.ResolveEffectiveSlotRoute(entry, itemRow.Value, slot, manifestEntries, gearsetIds, unlockedProfile);
                    if (effRoute.HasValue)
                    {
                        if (RetainerBellEvaluator.IsWithdrawableRoute(effRoute))
                        {
                            shouldWithdraw = true;
                        }
                        else if (effRoute.Value.IsSpecificRetainer && effRoute.Value.RetainerId != activeId)
                        {
                            var funnel = RetainerTransferPolicy.EvaluateCrossRetainerFunnel(
                                activeId,
                                effRoute.Value.RetainerId,
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

                candidates.Add(new RetainerTransferCandidate(
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
                    var slot = InventorySafetyEvaluator.GetSlot(container, slotIdx);
                    if (slot == null || slot->ItemId == 0 || slot->Quantity <= 0)
                    {
                        continue;
                    }

                    var rawId = slot->ItemId;
                    var baseId = ItemIdentity.NormalizeBaseItemId(rawId);
                    if (!ItemIdentity.CanLockOrTagBaseId(baseId)
                        || gearsetIds.Contains(baseId)
                        || safety.IsItemTracked(baseId)
                        || !manifestEntries.TryGetValue(baseId, out var entry))
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
                    if (effRoute?.Kind != RouteKind.Market)
                    {
                        continue;
                    }

                    candidates.Add(new RetainerTransferCandidate(
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

        candidates.Sort((a, b) =>
        {
            var aMerges = playerSpace.HasPartialRoom(a.RawItemId, a.Flags) ? 1 : 0;
            var bMerges = playerSpace.HasPartialRoom(b.RawItemId, b.Flags) ? 1 : 0;
            if (aMerges != bMerges)
            {
                return bMerges.CompareTo(aMerges);
            }

            var aFromRet = InventorySafetyEvaluator.IsRetainerContainer(a.SourceContainer) ? 1 : 0;
            var bFromRet = InventorySafetyEvaluator.IsRetainerContainer(b.SourceContainer) ? 1 : 0;
            return bFromRet.CompareTo(aFromRet);
        });

        return candidates;
    }

    private static List<RetainerTransferCandidate> CollectTrackedWithdrawCandidates(
        InventoryManager* manager,
        SlotSpaceTracker playerSpace,
        ExcelSheet<Item> itemSheet,
        IReadOnlyDictionary<uint, int> missingTracked)
    {
        var candidates = new List<RetainerTransferCandidate>();
        if (missingTracked.Count == 0)
        {
            return candidates;
        }

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
                if (itemRow is null || (itemRow.Value.IsUnique && playerSpace.HasBaseItem(baseId)))
                {
                    continue;
                }

                candidates.Add(new RetainerTransferCandidate(
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

        candidates.Sort((a, b) =>
        {
            var aMerges = playerSpace.HasPartialRoom(a.RawItemId, a.Flags) ? 1 : 0;
            var bMerges = playerSpace.HasPartialRoom(b.RawItemId, b.Flags) ? 1 : 0;
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

        return candidates;
    }

    private void QueueTrackedWithdrawals(
        TransferPlanBuilder state,
        InventoryManager* manager,
        List<RetainerTransferCandidate> candidates,
        IReadOnlyDictionary<uint, int> missingTracked)
    {
        var remainingNeeded = new Dictionary<uint, int>(missingTracked);
        foreach (var cand in candidates)
        {
            if (!remainingNeeded.TryGetValue(cand.BaseItemId, out var needed) || needed <= 0)
            {
                continue;
            }

            if (cand.IsUnique && state.PlayerSpace.HasBaseItem(cand.BaseItemId))
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

                if (state.Steps.Count >= state.MaxSteps)
                {
                    state.HasMoreRemaining = true;
                    break;
                }

                state.TrackedWithdrawCount++;
                state.TrackedWithdrawSummary.Add($"{cand.ItemName} x{cand.Quantity}");
                state.WithdrawSlots.Add((cand.SourceContainer, cand.SourceSlot));
                remainingNeeded[cand.BaseItemId] = 0;

                state.Steps.Add(BuildMoveStep(
                    $"Withdraw tracked {cand.ItemName}",
                    state.ActiveRetainerId,
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
            var canSplit = wantsSplit && state.RetainerSpace.EmptySlots >= 1;
            var effectiveQty = canSplit ? takeQty : cand.Quantity;

            if (!state.PlayerSpace.CanReceive(cand.RawItemId, cand.Flags, effectiveQty, cand.StackSize, allowPartialRemainder: !canSplit))
            {
                state.BlockedByFullBag++;
                continue;
            }

            var requiredSteps = canSplit ? 2 : 1;
            if (state.Steps.Count + requiredSteps > state.MaxSteps)
            {
                state.HasMoreRemaining = true;
                break;
            }

            var outcome = state.PlayerSpace.TryCommitReceive(
                cand.RawItemId,
                cand.BaseItemId,
                cand.Flags,
                effectiveQty,
                cand.StackSize,
                cand.IsUnique,
                allowPartialRemainder: !canSplit);
            if (!outcome.Accepted)
            {
                state.BlockedByFullBag++;
                continue;
            }

            if (!canSplit && outcome.FreedSourceSlot)
            {
                state.RetainerSpace.FreeSlot();
            }

            if (!withdrawAll)
            {
                remainingNeeded[cand.BaseItemId] = Math.Max(0, needed - outcome.MovedQuantity);
            }

            state.TrackedWithdrawCount++;
            var summaryNote = wantsSplit && !canSplit ? " (full stack - retainer full)" : string.Empty;
            state.TrackedWithdrawSummary.Add($"{cand.ItemName} x{effectiveQty}{summaryNote}");
            state.WithdrawSlots.Add((cand.SourceContainer, cand.SourceSlot));

            if (canSplit)
            {
                AddSplitAndWithdrawSteps(state.Steps, state.ActiveRetainerId, cand, takeQty, allowPartialMergeForSplit: !outcome.SplitTargetsEmptySlot);
            }
            else
            {
                state.Steps.Add(BuildMoveStep(
                    $"Withdraw tracked {cand.ItemName}",
                    state.ActiveRetainerId,
                    cand.SourceContainer,
                    cand.SourceSlot,
                    cand.RawItemId,
                    cand.Flags,
                    cand.StackSize,
                    InventorySafetyEvaluator.PlayerBagContainers));
            }
        }
    }

    private void QueueDeposits(TransferPlanBuilder state, List<RetainerTransferCandidate> candidates)
    {
        foreach (var cand in candidates)
        {
            if (cand.IsUnique && state.RetainerSpace.HasBaseItem(cand.BaseItemId))
            {
                continue;
            }

            if (!state.RetainerSpace.CanReceive(cand.RawItemId, cand.Flags, cand.Quantity, cand.StackSize))
            {
                state.BlockedByFullRetainer++;
                continue;
            }

            if (state.Steps.Count >= state.MaxSteps)
            {
                state.HasMoreRemaining = true;
                break;
            }

            var outcome = state.RetainerSpace.TryCommitReceive(
                cand.RawItemId,
                cand.BaseItemId,
                cand.Flags,
                cand.Quantity,
                cand.StackSize,
                cand.IsUnique);
            if (!outcome.Accepted)
            {
                state.BlockedByFullRetainer++;
                continue;
            }

            if (outcome.FreedSourceSlot && !InventorySafetyEvaluator.IsArmouryContainer(cand.SourceContainer))
            {
                state.PlayerSpace.FreeSlot();
            }

            state.DepositCount++;
            state.DepositSummary.Add(cand.Quantity > 1 ? $"{cand.ItemName} x{cand.Quantity}" : cand.ItemName);
            state.DepositSlots.Add((cand.SourceContainer, cand.SourceSlot));

            state.Steps.Add(BuildMoveStep(
                $"Deposit {cand.ItemName}",
                state.ActiveRetainerId,
                cand.SourceContainer,
                cand.SourceSlot,
                cand.RawItemId,
                cand.Flags,
                cand.StackSize,
                InventorySafetyEvaluator.RetainerBagContainers));
        }
    }

    private void QueueWithdrawals(TransferPlanBuilder state, List<RetainerTransferCandidate> candidates)
    {
        foreach (var cand in candidates)
        {
            if (cand.IsUnique
                && !InventorySafetyEvaluator.IsArmouryContainer(cand.SourceContainer)
                && state.PlayerSpace.HasBaseItem(cand.BaseItemId))
            {
                continue;
            }

            if (!state.PlayerSpace.CanReceive(cand.RawItemId, cand.Flags, cand.Quantity, cand.StackSize))
            {
                state.BlockedByFullBag++;
                continue;
            }

            if (state.Steps.Count >= state.MaxSteps)
            {
                state.HasMoreRemaining = true;
                break;
            }

            var outcome = state.PlayerSpace.TryCommitReceive(
                cand.RawItemId,
                cand.BaseItemId,
                cand.Flags,
                cand.Quantity,
                cand.StackSize,
                cand.IsUnique);
            if (!outcome.Accepted)
            {
                state.BlockedByFullBag++;
                continue;
            }

            if (outcome.FreedSourceSlot && InventorySafetyEvaluator.IsRetainerContainer(cand.SourceContainer))
            {
                state.RetainerSpace.FreeSlot();
            }

            state.WithdrawCount++;
            state.SortWithdrawSummary.Add(cand.Quantity > 1 ? $"{cand.ItemName} x{cand.Quantity}" : cand.ItemName);
            state.WithdrawSlots.Add((cand.SourceContainer, cand.SourceSlot));

            var stepTitle = InventorySafetyEvaluator.IsArmouryContainer(cand.SourceContainer)
                ? $"Move {cand.ItemName} to inventory"
                : $"Withdraw {cand.ItemName}";

            state.Steps.Add(BuildMoveStep(
                stepTitle,
                state.ActiveRetainerId,
                cand.SourceContainer,
                cand.SourceSlot,
                cand.RawItemId,
                cand.Flags,
                cand.StackSize,
                InventorySafetyEvaluator.PlayerBagContainers));
        }
    }

    private int ProjectLiveActiveRetainerWithdrawals(
        InventoryManager* manager,
        int baseFreeSlots,
        IReadOnlyDictionary<uint, MagicSortEntry> manifestEntries,
        ExcelSheet<Item> itemSheet,
        HashSet<uint> gearsetIds,
        UnlockedJobGearProfile unlockedProfile,
        SlotSpaceTracker playerSpace,
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
                    if (tRow is not null && tRow.Value.IsUnique && playerSpace.HasBaseItem(baseId))
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
                    || (row.Value.IsUnique && playerSpace.HasBaseItem(baseId)))
                {
                    continue;
                }

                if (entry.Route.IsKeep)
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
        ExcelSheet<Item> itemSheet,
        HashSet<uint> gearsetIds,
        UnlockedJobGearProfile unlockedProfile,
        SlotSpaceTracker playerSpace,
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
                if (tRow is not null && tRow.Value.IsUnique && playerSpace.HasBaseItem(trackedId))
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
                || (row.Value.IsUnique && playerSpace.HasBaseItem(baseId)))
            {
                continue;
            }

            if (entry.Route.IsKeep)
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
