using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using MagicInventory.Services;
using MagicInventory.Services.MagicSort;
using MagicInventory.Services.Triage;

namespace MagicInventory.Core;

public enum EquipSlotKey : byte
{
    None = 0,
    MainHand = 1,
    OffHand = 2,
    Head = 3,
    Body = 4,
    Gloves = 5,
    Legs = 6,
    Feet = 7,
    Ears = 8,
    Neck = 9,
    Wrists = 10,
    Ring = 11,
}

public sealed class UnlockedJobGearProfile
{
    public Dictionary<uint, int> JobBaselineIlvl { get; } = new();
    public Dictionary<uint, int> JobLevels { get; } = new();
    public Dictionary<(uint JobId, EquipSlotKey Slot), int> JobSlotIlvl { get; } = new();
    public HashSet<uint> SavedGearsetJobIds { get; } = new();

    public int GetEffectiveSlotIlvl(uint jobId, EquipSlotKey slot)
    {
        if (slot != EquipSlotKey.None && JobSlotIlvl.TryGetValue((jobId, slot), out var slotIlvl) && slotIlvl > 0)
        {
            return slotIlvl;
        }

        return JobBaselineIlvl.GetValueOrDefault(jobId, 0);
    }
}

public readonly record struct EquipmentRelevance(
    bool IsUpgradeOrLevelingForUnlockedJob,
    bool IsConfirmedOutgearedByUnlockedJob,
    bool IsRelevantCrafterGathererTool,
    string ProtectReason);

public sealed unsafe class InventorySafetyEvaluator
{
    public static readonly InventoryType[] PlayerBagContainers =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    ];

    public static readonly InventoryType[] PlayerBagContainersWithCrystals =
    [
        ..PlayerBagContainers,
        InventoryType.Crystals,
    ];

    public static readonly InventoryType[] RetainerBagContainers =
    [
        InventoryType.RetainerPage1,
        InventoryType.RetainerPage2,
        InventoryType.RetainerPage3,
        InventoryType.RetainerPage4,
        InventoryType.RetainerPage5,
        InventoryType.RetainerPage6,
        InventoryType.RetainerPage7,
    ];

    public static readonly InventoryType[] RetainerBagContainersWithCrystals =
    [
        ..RetainerBagContainers,
        InventoryType.RetainerCrystals,
    ];

    public static readonly InventoryType[] ArmouryGearContainers =
    [
        InventoryType.ArmoryMainHand,
        InventoryType.ArmoryOffHand,
        InventoryType.ArmoryHead,
        InventoryType.ArmoryBody,
        InventoryType.ArmoryHands,
        InventoryType.ArmoryLegs,
        InventoryType.ArmoryFeets,
        InventoryType.ArmoryEar,
        InventoryType.ArmoryNeck,
        InventoryType.ArmoryWrist,
        InventoryType.ArmoryRings,
    ];

    public static readonly InventoryType[] MaterializeSourceContainers =
    [
        InventoryType.EquippedItems,
        ..ArmouryGearContainers,
        ..PlayerBagContainers,
    ];

    private const double CacheTtlMs = 2000.0;

    private readonly IDataManager dataManager;
    private readonly Configuration configuration;
    private readonly Func<uint, bool> isItemInActiveTracker;

    private ulong cachedGearsetCharacterId;
    private DateTime lastGearsetCacheUtc = DateTime.MinValue;
    private HashSet<uint> cachedGearsetIds = new();
    private ulong cachedJobIlvlCharacterId;
    private DateTime lastJobIlvlCacheUtc = DateTime.MinValue;
    private UnlockedJobGearProfile cachedJobGearProfile = new();

    public InventorySafetyEvaluator(
        IDataManager dataManager,
        Configuration configuration,
        Func<uint, bool> isItemInActiveTracker)
    {
        this.dataManager = dataManager;
        this.configuration = configuration;
        this.isItemInActiveTracker = isItemInActiveTracker;
    }

    public static ulong GetCurrentContentId()
    {
        var ps = PlayerState.Instance();
        return ps != null ? ps->ContentId : 0UL;
    }

    public void InvalidateCaches()
    {
        lastGearsetCacheUtc = DateTime.MinValue;
        lastJobIlvlCacheUtc = DateTime.MinValue;
    }

    public bool IsItemTracked(uint rawItemId)
    {
        var baseId = ItemIdentity.NormalizeBaseItemId(rawItemId);
        return baseId != 0 && isItemInActiveTracker(baseId);
    }

    public int GetMarketPriority(uint baseItemId)
        => configuration.GetMarketListingPriority(GetCurrentContentId(), ItemIdentity.NormalizeBaseItemId(baseItemId));

    public bool HasManualRouteOverride(uint rawItemId, string expectedRoute)
    {
        var baseId = ItemIdentity.NormalizeBaseItemId(rawItemId);
        return baseId != 0
            && configuration.TryGetManualRouteOverride(GetCurrentContentId(), baseId, out var spec)
            && string.Equals(spec, expectedRoute, StringComparison.OrdinalIgnoreCase);
    }

    public bool HasExplicitKeepOverride(uint rawItemId)
        => HasManualRouteOverride(rawItemId, "Keep");

    public static EquipSlotKey ResolveEquipSlotKey(Item itemRow)
    {
        if (itemRow.EquipSlotCategory.RowId == 0)
        {
            return EquipSlotKey.None;
        }

        var cat = itemRow.EquipSlotCategory.Value;
        if (cat.MainHand == 1) return EquipSlotKey.MainHand;
        if (cat.OffHand == 1) return EquipSlotKey.OffHand;
        if (cat.Head == 1) return EquipSlotKey.Head;
        if (cat.Body == 1) return EquipSlotKey.Body;
        if (cat.Gloves == 1) return EquipSlotKey.Gloves;
        if (cat.Legs == 1) return EquipSlotKey.Legs;
        if (cat.Feet == 1) return EquipSlotKey.Feet;
        if (cat.Ears == 1) return EquipSlotKey.Ears;
        if (cat.Neck == 1) return EquipSlotKey.Neck;
        if (cat.Wrists == 1) return EquipSlotKey.Wrists;
        if (cat.FingerL == 1 || cat.FingerR == 1) return EquipSlotKey.Ring;
        return EquipSlotKey.None;
    }

    public static bool IsRetainerContainer(InventoryType? container)
    {
        return container is InventoryType.RetainerPage1
            or InventoryType.RetainerPage2
            or InventoryType.RetainerPage3
            or InventoryType.RetainerPage4
            or InventoryType.RetainerPage5
            or InventoryType.RetainerPage6
            or InventoryType.RetainerPage7;
    }

    public static InventoryType? ResolveArmouryContainerForItem(Item itemRow)
    {
        if (itemRow.EquipSlotCategory.RowId == 0)
        {
            return null;
        }

        var cat = itemRow.EquipSlotCategory.Value;
        if (cat.MainHand == 1) return InventoryType.ArmoryMainHand;
        if (cat.OffHand == 1) return InventoryType.ArmoryOffHand;
        if (cat.Head == 1) return InventoryType.ArmoryHead;
        if (cat.Body == 1) return InventoryType.ArmoryBody;
        if (cat.Gloves == 1) return InventoryType.ArmoryHands;
        if (cat.Legs == 1) return InventoryType.ArmoryLegs;
        if (cat.Feet == 1) return InventoryType.ArmoryFeets;
        if (cat.Ears == 1) return InventoryType.ArmoryEar;
        if (cat.Neck == 1) return InventoryType.ArmoryNeck;
        if (cat.Wrists == 1) return InventoryType.ArmoryWrist;
        if (cat.FingerL == 1 || cat.FingerR == 1) return InventoryType.ArmoryRings;
        if (cat.SoulCrystal == 1) return InventoryType.ArmorySoulCrystal;
        return null;
    }

    public static bool IsArmouryContainer(InventoryType container)
    {
        return container is InventoryType.ArmoryMainHand
            or InventoryType.ArmoryOffHand
            or InventoryType.ArmoryHead
            or InventoryType.ArmoryBody
            or InventoryType.ArmoryHands
            or InventoryType.ArmoryLegs
            or InventoryType.ArmoryFeets
            or InventoryType.ArmoryEar
            or InventoryType.ArmoryNeck
            or InventoryType.ArmoryWrist
            or InventoryType.ArmoryRings
            or InventoryType.ArmorySoulCrystal;
    }

    public static bool IsExpertDeliveryContainer(InventoryType container)
    {
        return container is InventoryType.Inventory1
            or InventoryType.Inventory2
            or InventoryType.Inventory3
            or InventoryType.Inventory4
            || IsArmouryContainer(container);
    }

    public static bool IsContainerReadable(InventoryContainer* container)
        => container != null && container->Items != null && container->Size > 0 && container->IsLoaded;

    public static InventoryItem* GetSlot(InventoryContainer* container, int slotIdx)
    {
        if (!IsContainerReadable(container) || slotIdx < 0 || slotIdx >= container->Size)
        {
            return null;
        }

        return container->Items + slotIdx;
    }

    public static InventoryItem* GetSlot(InventoryManager* manager, InventoryType containerType, int slotIdx)
        => manager != null ? GetSlot(manager->GetInventoryContainer(containerType), slotIdx) : null;

    public static bool FindDestinationSlot(
        InventoryManager* manager,
        ReadOnlySpan<InventoryType> containers,
        uint exactRawItemId,
        InventoryItem.ItemFlags exactFlags,
        int stackSize,
        out InventoryType dstContainer,
        out int dstSlot,
        bool routeCrystalsToCrystalBag = false,
        bool allowPartialMerge = true,
        int incomingQty = 1)
    {
        var baseId = ItemIdentity.NormalizeBaseItemId(exactRawItemId);
        var requiredQty = Math.Max(1, incomingQty);
        if (routeCrystalsToCrystalBag && ItemIdentity.IsCrystalItemId(baseId))
        {
            var crystalBag = manager->GetInventoryContainer(InventoryType.Crystals);
            var cSlotIdx = (int)(baseId - 2);
            var cSlot = GetSlot(crystalBag, cSlotIdx);
            if (cSlot != null)
            {
                var currentQty = cSlot->ItemId == baseId ? cSlot->Quantity : 0;
                var hasCrystalRoom = allowPartialMerge
                    ? currentQty < 9999
                    : currentQty + requiredQty <= 9999;
                if (hasCrystalRoom)
                {
                    dstContainer = InventoryType.Crystals;
                    dstSlot = cSlotIdx;
                    return true;
                }
            }

            dstContainer = default;
            dstSlot = -1;
            return false;
        }

        var canStack = stackSize > 1 && (exactFlags & InventoryItem.ItemFlags.Collectable) == 0;
        if (canStack)
        {
            foreach (var type in containers)
            {
                var container = manager->GetInventoryContainer(type);
                if (!IsContainerReadable(container))
                {
                    continue;
                }

                for (var i = 0; i < container->Size; i++)
                {
                    var slot = GetSlot(container, i);
                    if (slot != null
                        && slot->ItemId == baseId
                        && slot->Flags == exactFlags
                        && slot->Quantity > 0
                        && (allowPartialMerge ? slot->Quantity < stackSize : slot->Quantity + requiredQty <= stackSize))
                    {
                        dstContainer = type;
                        dstSlot = i;
                        return true;
                    }
                }
            }
        }

        foreach (var type in containers)
        {
            var container = manager->GetInventoryContainer(type);
            if (!IsContainerReadable(container))
            {
                continue;
            }

            for (var i = 0; i < container->Size; i++)
            {
                var slot = GetSlot(container, i);
                if (slot != null && (slot->ItemId == 0 || slot->Quantity <= 0))
                {
                    dstContainer = type;
                    dstSlot = i;
                    return true;
                }
            }
        }

        dstContainer = default;
        dstSlot = -1;
        return false;
    }

    public static bool IsMateriaExtractable(Item itemRow, InventoryItem* slot)
    {
        return slot != null
            && slot->ItemId != 0
            && slot->Quantity > 0
            && itemRow.EquipSlotCategory.RowId > 0
            && itemRow.MaterializeType != 0
            && slot->SpiritbondOrCollectability >= 10000;
    }

    public static bool IsLv1GlamourOrStarterGear(Item itemRow)
    {
        return ItemIdentity.IsEquipment(itemRow)
            && itemRow.LevelEquip == 1
            && itemRow.LevelItem.RowId <= 1
            && itemRow.IsUntradable;
    }

    public static bool IsEligibleForLiveRetainerDuplicateDeposit(Item itemRow)
    {
        return itemRow.EquipSlotCategory.RowId == 0
            && itemRow.StackSize > 1
            && !itemRow.IsUnique
            && !ItemIdentity.IsCurrencyOrCrystal(itemRow.RowId, itemRow)
            && !ItemIdentity.ProtectedConsumableCategoryIds.Contains(itemRow.ItemUICategory.RowId)
            && !ItemIdentity.IsSoulCrystal(itemRow)
            && itemRow.ItemUICategory.RowId != ItemIdentity.UiCatOther;
    }

    public HashSet<uint> BuildGearsetItemIds()
    {
        var now = DateTime.UtcNow;
        var cid = GetCurrentContentId();
        if (cid == cachedGearsetCharacterId && (now - lastGearsetCacheUtc).TotalMilliseconds < CacheTtlMs)
        {
            return cachedGearsetIds;
        }

        var ids = new HashSet<uint>();
        var gearsets = RaptureGearsetModule.Instance();
        if (gearsets == null)
        {
            return ids;
        }

        for (var i = 0; i < gearsets->Entries.Length; i++)
        {
            if (!gearsets->IsValidGearset(i))
            {
                continue;
            }

            ref readonly var entry = ref gearsets->Entries[i];
            for (var s = 0; s < entry.Items.Length; s++)
            {
                var rawId = entry.Items[s].ItemId;
                if (rawId != 0)
                {
                    ids.Add(ItemIdentity.NormalizeBaseItemId(rawId));
                }
            }
        }

        cachedGearsetCharacterId = cid;
        cachedGearsetIds = ids;
        lastGearsetCacheUtc = now;
        return ids;
    }

    public static bool IsUntouchedUnlockJob(PlayerState* ps, ClassJob job, bool hasGearset)
    {
        if (ps == null || job.RowId == 0 || job.ExpArrayIndex < 0 || job.ExpArrayIndex >= ps->ClassJobLevels.Length)
        {
            return true;
        }

        var jobLevel = ps->ClassJobLevels[job.ExpArrayIndex];
        if (jobLevel <= 0)
        {
            return true;
        }

        var isDoHOrDoL = job.RowId is >= 8 and <= 18;
        var isCurrent = job.RowId == (uint)ps->CurrentClassJobId;
        if (hasGearset || isCurrent || isDoHOrDoL)
        {
            return false;
        }

        var jobExp = job.ExpArrayIndex < ps->ClassJobExperience.Length
            ? ps->ClassJobExperience[job.ExpArrayIndex]
            : 0;

        var startLv = Math.Max(1, (int)job.StartingLevel);
        return jobLevel <= 1 || (jobExp == 0 && jobLevel <= startLv);
    }

    public static EquipmentRelevance EvaluateEquipmentRelevance(
        UnlockedJobGearProfile profile,
        EquipSlotKey slotKey,
        int equipLevel,
        int itemIlvl,
        bool isCrafterGathererTool,
        Func<uint, bool> canJobUse)
    {
        var isUpgradeOrLeveling = false;
        var isConfirmedOutgeared = false;
        var isRelevantTool = false;
        var protectReason = string.Empty;

        foreach (var (jobId, baselineIlvl) in profile.JobBaselineIlvl)
        {
            if (!profile.JobLevels.TryGetValue(jobId, out var jobLevel) || jobLevel <= 0)
            {
                continue;
            }

            if (!canJobUse(jobId))
            {
                continue;
            }

            var slotTopIlvl = profile.GetEffectiveSlotIlvl(jobId, slotKey);
            var hasSavedGearset = profile.SavedGearsetJobIds.Contains(jobId);

            if (isCrafterGathererTool)
            {
                if (equipLevel > jobLevel && equipLevel <= jobLevel + 4)
                {
                    isRelevantTool = true;
                    isUpgradeOrLeveling = true;
                    if (string.IsNullOrEmpty(protectReason))
                    {
                        protectReason = $"Leveling tool (Lv.{equipLevel})";
                    }
                }
                else if (equipLevel <= jobLevel
                    && ((slotTopIlvl == 0 && (hasSavedGearset || jobLevel > 1))
                        || (slotTopIlvl > 0 && itemIlvl >= slotTopIlvl - 10)))
                {
                    isRelevantTool = true;
                    isUpgradeOrLeveling = true;
                    if (string.IsNullOrEmpty(protectReason))
                    {
                        protectReason = "Crafter/Gatherer tool";
                    }
                }
            }

            if (equipLevel > jobLevel && equipLevel <= jobLevel + 4)
            {
                isUpgradeOrLeveling = true;
                if (string.IsNullOrEmpty(protectReason))
                {
                    protectReason = $"Leveling gear (Lv.{equipLevel})";
                }
            }
            else if (equipLevel <= jobLevel && slotTopIlvl > 0 && itemIlvl >= slotTopIlvl - 5)
            {
                isUpgradeOrLeveling = true;
                if (string.IsNullOrEmpty(protectReason))
                {
                    protectReason = itemIlvl > slotTopIlvl ? "Gear upgrade" : "Current gear";
                }
            }

            if (equipLevel <= jobLevel && slotTopIlvl > 0 && itemIlvl < slotTopIlvl - 5)
            {
                isConfirmedOutgeared = true;
            }
        }

        return new EquipmentRelevance(
            isUpgradeOrLeveling,
            isConfirmedOutgeared,
            isRelevantTool,
            protectReason);
    }

    public static EquipmentRelevance EvaluateEquipmentRelevance(UnlockedJobGearProfile profile, Item itemRow)
    {
        if (itemRow.EquipSlotCategory.RowId == 0)
        {
            return default;
        }

        var category = itemRow.ClassJobCategory.Value;
        return EvaluateEquipmentRelevance(
            profile,
            ResolveEquipSlotKey(itemRow),
            itemRow.LevelEquip,
            (int)itemRow.LevelItem.RowId,
            ItemIdentity.IsCrafterOrGathererTool(itemRow),
            jobId => CanJobUseCategory(category, jobId));
    }

    public UnlockedJobGearProfile BuildUnlockedJobGearProfile()
    {
        var now = DateTime.UtcNow;
        var cid = GetCurrentContentId();
        if (cid == cachedJobIlvlCharacterId && (now - lastJobIlvlCacheUtc).TotalMilliseconds < CacheTtlMs)
        {
            return cachedJobGearProfile;
        }

        var profile = new UnlockedJobGearProfile();
        var ps = PlayerState.Instance();
        var gearsets = RaptureGearsetModule.Instance();
        var classJobSheet = dataManager.GetExcelSheet<ClassJob>();
        var itemSheet = dataManager.GetExcelSheet<Item>();
        if (ps == null || classJobSheet == null || itemSheet == null)
        {
            return profile;
        }

        var jobIlvlsFromSets = new Dictionary<uint, int>();
        var slotIlvlByJob = new Dictionary<(uint JobId, EquipSlotKey Slot), int>();

        void RecordSetItems(uint jobId, ReadOnlySpan<uint> rawItemIds, int explicitSetIlvl)
        {
            var ringIlvlA = 0;
            var ringIlvlB = 0;
            var sum = 0;
            var count = 0;

            for (var s = 0; s < rawItemIds.Length; s++)
            {
                var rawId = rawItemIds[s];
                if (rawId == 0)
                {
                    continue;
                }

                var row = itemSheet.GetRowOrDefault(ItemIdentity.NormalizeBaseItemId(rawId));
                if (row is null || row.Value.LevelItem.RowId == 0 || ItemIdentity.IsSoulCrystal(row.Value))
                {
                    continue;
                }

                var ilvl = (int)row.Value.LevelItem.RowId;
                var slotKey = ResolveEquipSlotKey(row.Value);
                if (slotKey == EquipSlotKey.Ring)
                {
                    if (ringIlvlA == 0)
                    {
                        ringIlvlA = ilvl;
                    }
                    else
                    {
                        ringIlvlB = ilvl;
                    }
                }
                else if (slotKey != EquipSlotKey.None)
                {
                    var key = (jobId, slotKey);
                    slotIlvlByJob[key] = Math.Max(slotIlvlByJob.GetValueOrDefault(key), ilvl);
                }

                sum += ilvl;
                count++;
            }

            if (ringIlvlA > 0)
            {
                var effectiveRingIlvl = ringIlvlB > 0 ? Math.Min(ringIlvlA, ringIlvlB) : ringIlvlA;
                var ringKey = (jobId, EquipSlotKey.Ring);
                slotIlvlByJob[ringKey] = Math.Max(slotIlvlByJob.GetValueOrDefault(ringKey), effectiveRingIlvl);
            }

            var baseline = explicitSetIlvl > 0 ? explicitSetIlvl : (count > 0 ? sum / count : 0);
            if (baseline > 0)
            {
                jobIlvlsFromSets[jobId] = Math.Max(jobIlvlsFromSets.GetValueOrDefault(jobId), baseline);
            }
        }

        if (gearsets != null)
        {
            Span<uint> setItemBuf = stackalloc uint[14];
            for (var i = 0; i < gearsets->Entries.Length; i++)
            {
                if (!gearsets->IsValidGearset(i))
                {
                    continue;
                }

                ref readonly var entry = ref gearsets->Entries[i];
                if (entry.ClassJob == 0)
                {
                    continue;
                }

                profile.SavedGearsetJobIds.Add(entry.ClassJob);
                var len = Math.Min(setItemBuf.Length, entry.Items.Length);
                for (var s = 0; s < len; s++)
                {
                    setItemBuf[s] = entry.Items[s].ItemId;
                }

                RecordSetItems(entry.ClassJob, setItemBuf[..len], entry.ItemLevel);
            }
        }

        var manager = InventoryManager.Instance();
        if (manager != null && ps->CurrentClassJobId != 0)
        {
            var equipped = manager->GetInventoryContainer(InventoryType.EquippedItems);
            if (equipped != null && equipped->Items != null && equipped->Size > 0)
            {
                var len = Math.Min(14, (int)equipped->Size);
                Span<uint> eqItemBuf = stackalloc uint[14];
                for (var s = 0; s < len; s++)
                {
                    var slot = equipped->GetInventorySlot(s);
                    eqItemBuf[s] = slot != null ? slot->ItemId : 0u;
                }

                RecordSetItems(ps->CurrentClassJobId, eqItemBuf[..len], explicitSetIlvl: 0);
            }
        }

        var ownedSoulCrystalJobs = ScanOwnedSoulCrystalJobs(classJobSheet, itemSheet);

        foreach (var job in classJobSheet)
        {
            if (job.RowId == 0 || job.ExpArrayIndex < 0 || job.ExpArrayIndex >= ps->ClassJobLevels.Length)
            {
                continue;
            }

            var jobLv = (int)ps->ClassJobLevels[job.ExpArrayIndex];
            if (jobLv <= 0)
            {
                continue;
            }

            profile.JobLevels[job.RowId] = jobLv;

            if (jobIlvlsFromSets.TryGetValue(job.RowId, out var directIlvl))
            {
                profile.JobBaselineIlvl[job.RowId] = directIlvl;
                foreach (var (slotKeyPair, ilvl) in slotIlvlByJob)
                {
                    if (slotKeyPair.JobId == job.RowId)
                    {
                        profile.JobSlotIlvl[slotKeyPair] = ilvl;
                    }
                }

                continue;
            }

            if (job.ClassJobParent.RowId == job.RowId)
            {
                var inherited = 0;
                var hasChildSet = false;
                foreach (var other in classJobSheet)
                {
                    if (other.RowId != job.RowId
                        && other.ClassJobParent.RowId == job.RowId
                        && jobIlvlsFromSets.TryGetValue(other.RowId, out var childIlvl))
                    {
                        hasChildSet = true;
                        inherited = Math.Max(inherited, childIlvl);
                        foreach (var (slotKeyPair, ilvl) in slotIlvlByJob)
                        {
                            if (slotKeyPair.JobId == other.RowId)
                            {
                                var parentKey = (job.RowId, slotKeyPair.Slot);
                                profile.JobSlotIlvl[parentKey] = Math.Max(profile.JobSlotIlvl.GetValueOrDefault(parentKey), ilvl);
                            }
                        }
                    }
                }

                if (!hasChildSet && IsUntouchedUnlockJob(ps, job, hasGearset: false))
                {
                    continue;
                }

                profile.JobBaselineIlvl[job.RowId] = hasChildSet ? inherited : 0;
            }
            else if (ownedSoulCrystalJobs.Contains(job.RowId))
            {
                if (IsUntouchedUnlockJob(ps, job, hasGearset: false))
                {
                    continue;
                }

                profile.JobBaselineIlvl[job.RowId] = 0;
            }
        }

        cachedJobIlvlCharacterId = cid;
        cachedJobGearProfile = profile;
        lastJobIlvlCacheUtc = now;
        return profile;
    }

    public string? ResolveEffectiveSlotRoute(
        MagicSortEntry entry,
        Item itemRow,
        InventoryItem* liveItem,
        IReadOnlyDictionary<uint, MagicSortEntry> manifestEntries,
        HashSet<uint> gearsetIds,
        UnlockedJobGearProfile jobProfile,
        bool hasNq = true,
        bool hasHq = false)
    {
        if (string.Equals(entry.Route, "Keep", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var cid = GetCurrentContentId();
        var hasManualRouteOverride = configuration.TryGetManualRouteOverride(cid, entry.ItemId, out var manualSpec)
            && string.Equals(manualSpec, entry.Route, StringComparison.OrdinalIgnoreCase);

        if (liveItem == null)
        {
            if (gearsetIds.Contains(entry.ItemId) || IsItemTracked(entry.ItemId))
            {
                return null;
            }

            if (string.Equals(entry.Route, "Materia", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (!hasManualRouteOverride
                && entry.Route is "Market" or "Vendor" or "GrandCompany"
                && ItemIdentity.IsHousingItem(itemRow))
            {
                return null;
            }

            if (string.Equals(entry.Route, "Market", StringComparison.OrdinalIgnoreCase))
            {
                if (!ItemIdentity.IsMarketable(itemRow))
                {
                    return null;
                }

                var breakeven = MarketViabilityEvaluator.ComputeVendorBreakevenPrice(itemRow.PriceLow);
                var nqOk = hasNq && entry.TargetPriceNq >= breakeven;
                var hqOk = hasHq && entry.TargetPriceHq >= breakeven;
                if (!nqOk && !hqOk)
                {
                    return null;
                }
            }

            if (string.Equals(entry.Route, "Vendor", StringComparison.OrdinalIgnoreCase))
            {
                if (itemRow.PriceLow == 0
                    || (!hasManualRouteOverride && hasHq && !hasNq)
                    || (!hasManualRouteOverride && IsLv1GlamourOrStarterGear(itemRow)))
                {
                    return null;
                }
            }

            if (string.Equals(entry.Route, "GrandCompany", StringComparison.OrdinalIgnoreCase))
            {
                if ((!hasManualRouteOverride && hasHq && !hasNq)
                    || (!hasManualRouteOverride && !itemRow.IsUntradable)
                    || (!hasManualRouteOverride && IsLv1GlamourOrStarterGear(itemRow))
                    || itemRow.PriceLow == 0)
                {
                    return null;
                }
            }

            if (entry.Route is "Vendor" or "GrandCompany"
                && !hasManualRouteOverride
                && itemRow.EquipSlotCategory.RowId > 0)
            {
                var rel = EvaluateEquipmentRelevance(jobProfile, itemRow);
                if (rel.IsUpgradeOrLevelingForUnlockedJob
                    || (itemRow.Rarity == 3 && !rel.IsConfirmedOutgearedByUnlockedJob))
                {
                    return null;
                }
            }

            if (string.Equals(entry.Route, "Use", StringComparison.OrdinalIgnoreCase) && itemRow.ItemAction.RowId == 0)
            {
                return null;
            }

            return entry.Route;
        }

        if (entry.Route is "Market")
        {
            if (IsSafeToDisposeOrList(liveItem, itemRow, "Market", manifestEntries, gearsetIds, jobProfile))
            {
                var isHq = ItemIdentity.IsHighQuality(liveItem);
                var unitPrice = isHq ? entry.TargetPriceHq : entry.TargetPriceNq;
                var breakeven = MarketViabilityEvaluator.ComputeVendorBreakevenPrice(itemRow.PriceLow);
                if (unitPrice >= breakeven && unitPrice <= 999_999_999L)
                {
                    if (!hasManualRouteOverride)
                    {
                        var slotStackVal = (long)unitPrice * Math.Max(1, (int)liveItem->Quantity);
                        if (slotStackVal < configuration.MarketMinStackValue && unitPrice < configuration.MarketMinUnitPrice)
                        {
                            return null;
                        }
                    }

                    return "Market";
                }

                return null;
            }

            if (itemRow.EquipSlotCategory.RowId > 0
                && liveItem->SpiritbondOrCollectability is > 0 and < 10000
                && itemRow.Rarity == 1
                && IsSafeToDisposeOrList(liveItem, itemRow, "Vendor", manifestEntries, gearsetIds, jobProfile))
            {
                return "Vendor";
            }

            return null;
        }

        if (entry.Route is "Vendor" or "GrandCompany")
        {
            return IsSafeToDisposeOrList(liveItem, itemRow, entry.Route, manifestEntries, gearsetIds, jobProfile)
                ? entry.Route
                : null;
        }

        if (entry.Route is "Armoire")
        {
            if (liveItem->GetMateriaCount() > 0
                || liveItem->Materia[0] != 0
                || IsMateriaExtractable(itemRow, liveItem))
            {
                return null;
            }

            if (!hasManualRouteOverride
                && (liveItem->GlamourId != 0 || liveItem->GetStain(0) != 0 || liveItem->GetStain(1) != 0))
            {
                return null;
            }

            return "Armoire";
        }

        if (entry.Route is "Use")
        {
            return itemRow.ItemAction.RowId > 0 ? "Use" : null;
        }

        if (entry.Route is "Glamour")
        {
            if (liveItem->GetMateriaCount() > 0
                || liveItem->Materia[0] != 0
                || IsMateriaExtractable(itemRow, liveItem))
            {
                return null;
            }

            return "Glamour";
        }

        if (entry.Route is "GoldSaucer")
        {
            return "GoldSaucer";
        }

        if (entry.Route is "Materia")
        {
            return IsMateriaExtractable(itemRow, liveItem) ? "Materia" : null;
        }

        if (entry.Route is "Retainer")
        {
            if (IsMateriaExtractable(itemRow, liveItem))
            {
                return null;
            }

            return "Retainer";
        }

        return null;
    }

    public bool IsSafeToDisposeOrList(
        InventoryItem* slot,
        Item itemRow,
        string targetRoute,
        IReadOnlyDictionary<uint, MagicSortEntry> manifestEntries,
        HashSet<uint> gearsetIds,
        UnlockedJobGearProfile jobProfile)
    {
        if (slot == null || slot->ItemId == 0 || slot->Quantity <= 0)
        {
            return false;
        }

        var baseId = ItemIdentity.NormalizeBaseItemId(slot->ItemId);
        if (!ItemIdentity.CanLockOrTagBaseId(baseId) || IsItemTracked(baseId))
        {
            return false;
        }

        if (gearsetIds.Contains(baseId))
        {
            return false;
        }

        if (slot->Flags.HasFlag(InventoryItem.ItemFlags.Collectable))
        {
            return false;
        }

        if (ItemIdentity.IsSoulCrystal(itemRow) || ItemIdentity.IsRelicRarity(itemRow) || ItemIdentity.IsCollectable(slot))
        {
            return false;
        }

        var cid = GetCurrentContentId();
        var hasManualDisposalOverride = configuration.TryGetManualRouteOverride(cid, baseId, out var overrideSpec)
            && string.Equals(overrideSpec, targetRoute, StringComparison.OrdinalIgnoreCase);

        if (!hasManualDisposalOverride && ItemIdentity.IsHousingItem(itemRow))
        {
            return false;
        }

        if (!hasManualDisposalOverride
            && itemRow.EquipSlotCategory.RowId > 0
            && (slot->GlamourId != 0 || slot->GetStain(0) != 0 || slot->GetStain(1) != 0))
        {
            return false;
        }

        if (targetRoute is "Vendor" or "GrandCompany" && !hasManualDisposalOverride && IsLv1GlamourOrStarterGear(itemRow))
        {
            return false;
        }

        var isHq = ItemIdentity.IsHighQuality(slot);
        if (!hasManualDisposalOverride && targetRoute == "Vendor" && isHq)
        {
            return false;
        }

        if (slot->GetMateriaCount() > 0 || slot->Materia[0] != 0)
        {
            return false;
        }

        if (IsMateriaExtractable(itemRow, slot))
        {
            return false;
        }

        if (targetRoute is "Vendor" or "GrandCompany"
            && !hasManualDisposalOverride
            && itemRow.EquipSlotCategory.RowId > 0)
        {
            var rel = EvaluateEquipmentRelevance(jobProfile, itemRow);
            if (rel.IsUpgradeOrLevelingForUnlockedJob)
            {
                return false;
            }

            if (itemRow.Rarity == 3 && !rel.IsConfirmedOutgearedByUnlockedJob)
            {
                return false;
            }
        }

        if (targetRoute == "GrandCompany")
        {
            if (itemRow.PriceLow == 0 || (!hasManualDisposalOverride && (isHq || !itemRow.IsUntradable)))
            {
                return false;
            }
        }
        else if (targetRoute == "Vendor")
        {
            if (!manifestEntries.TryGetValue(baseId, out var entry)
                || itemRow.PriceLow == 0)
            {
                return false;
            }

            var isVendorRoute = string.Equals(entry.Route, "Vendor", StringComparison.OrdinalIgnoreCase);
            var isBoundMarketWhiteGear = !hasManualDisposalOverride
                && string.Equals(entry.Route, "Market", StringComparison.OrdinalIgnoreCase)
                && itemRow.EquipSlotCategory.RowId > 0
                && itemRow.Rarity == 1
                && slot->SpiritbondOrCollectability is > 0 and < 10000;
            if (!isVendorRoute && !isBoundMarketWhiteGear)
            {
                return false;
            }
        }
        else if (targetRoute == "Market")
        {
            if (!ItemIdentity.IsMarketable(itemRow))
            {
                return false;
            }

            if (itemRow.EquipSlotCategory.RowId > 0 && slot->SpiritbondOrCollectability > 0)
            {
                return false;
            }
        }

        return true;
    }

    public static bool CanJobUseCategory(ClassJobCategory category, uint classJobRowId)
    {
        return classJobRowId switch
        {
            1 => category.GLA,
            2 => category.PGL,
            3 => category.MRD,
            4 => category.LNC,
            5 => category.ARC,
            6 => category.CNJ,
            7 => category.THM,
            8 => category.CRP,
            9 => category.BSM,
            10 => category.ARM,
            11 => category.GSM,
            12 => category.LTW,
            13 => category.WVR,
            14 => category.ALC,
            15 => category.CUL,
            16 => category.MIN,
            17 => category.BTN,
            18 => category.FSH,
            19 => category.PLD,
            20 => category.MNK,
            21 => category.WAR,
            22 => category.DRG,
            23 => category.BRD,
            24 => category.WHM,
            25 => category.BLM,
            26 => category.ACN,
            27 => category.SMN,
            28 => category.SCH,
            29 => category.ROG,
            30 => category.NIN,
            31 => category.MCH,
            32 => category.DRK,
            33 => category.AST,
            34 => category.SAM,
            35 => category.RDM,
            36 => category.BLU,
            37 => category.GNB,
            38 => category.DNC,
            39 => category.RPR,
            40 => category.SGE,
            41 => category.VPR,
            42 => category.PCT,
            _ => false,
        };
    }

    private static HashSet<uint> ScanOwnedSoulCrystalJobs(
        Lumina.Excel.ExcelSheet<ClassJob> classJobSheet,
        Lumina.Excel.ExcelSheet<Item> itemSheet)
    {
        var owned = new HashSet<uint>();
        var manager = InventoryManager.Instance();
        if (manager == null)
        {
            return owned;
        }

        ReadOnlySpan<InventoryType> containers =
        [
            InventoryType.ArmorySoulCrystal,
            InventoryType.EquippedItems,
            InventoryType.Inventory1,
            InventoryType.Inventory2,
            InventoryType.Inventory3,
            InventoryType.Inventory4,
        ];

        foreach (var cType in containers)
        {
            var container = manager->GetInventoryContainer(cType);
            if (container == null || container->Items == null)
            {
                continue;
            }

            for (var s = 0; s < container->Size; s++)
            {
                var slot = container->GetInventorySlot(s);
                if (slot == null || slot->ItemId == 0)
                {
                    continue;
                }

                var itemRow = itemSheet.GetRowOrDefault(ItemIdentity.NormalizeBaseItemId(slot->ItemId));
                if (itemRow is null || !ItemIdentity.IsSoulCrystal(itemRow.Value))
                {
                    continue;
                }

                var category = itemRow.Value.ClassJobCategory.Value;
                foreach (var job in classJobSheet)
                {
                    if (job.RowId != 0 && job.ClassJobParent.RowId != job.RowId && CanJobUseCategory(category, job.RowId))
                    {
                        owned.Add(job.RowId);
                    }
                }
            }
        }

        return owned;
    }

    public static int GetLiveRetainerMarketCount(InventoryManager* manager, RetainerManager.Retainer* activeRetainer)
    {
        var cached = activeRetainer != null ? (int)activeRetainer->MarketItemCount : 0;
        if (manager == null)
        {
            return cached;
        }

        var marketContainer = manager->GetInventoryContainer(InventoryType.RetainerMarket);
        if (!IsContainerReadable(marketContainer))
        {
            return cached;
        }

        var liveCount = 0;
        for (var i = 0; i < marketContainer->Size; i++)
        {
            var slot = marketContainer->Items + i;
            if (slot != null && slot->ItemId != 0 && slot->Quantity > 0)
            {
                liveCount++;
            }
        }

        return liveCount;
    }
}
