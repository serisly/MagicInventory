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

namespace MagicInventory.Core;

public sealed unsafe class InventorySafetyEvaluator
{
    public static readonly InventoryType[] PlayerBagContainers =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
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

    public static readonly InventoryType[] ArmouryGearContainers =
    [
        InventoryType.ArmoryMainHand,
        InventoryType.ArmoryOffHand,
        InventoryType.ArmoryHead,
        InventoryType.ArmoryBody,
        InventoryType.ArmoryHands,
        InventoryType.ArmoryWaist,
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
    private Dictionary<uint, int> cachedUnlockedJobTopIlvl = new();

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

    public bool IsItemLocked(uint rawItemId)
    {
        var baseId = HoldingsService.NormalizeBaseItemId(rawItemId);
        if (baseId == 0)
        {
            return false;
        }

        var profile = configuration.GetOrCreateCharacter(GetCurrentContentId());
        return profile.LockedItemIds.Contains(baseId);
    }

    public bool IsItemTracked(uint rawItemId)
    {
        var baseId = HoldingsService.NormalizeBaseItemId(rawItemId);
        return baseId != 0 && isItemInActiveTracker(baseId);
    }

    public int GetMarketPriority(uint baseItemId)
        => configuration.MarketListingPriorities.GetValueOrDefault(HoldingsService.NormalizeBaseItemId(baseItemId), 0);

    public static bool CanLockOrTagBaseId(uint baseItemId)
        => ItemIdentity.CanLockOrTagBaseId(baseItemId);

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
            or InventoryType.ArmoryWaist
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
        bool routeCrystalsToCrystalBag = false)
    {
        var baseId = HoldingsService.NormalizeBaseItemId(exactRawItemId);
        if (routeCrystalsToCrystalBag && baseId is >= 2 and <= 19)
        {
            dstContainer = InventoryType.Crystals;
            dstSlot = (int)(baseId - 2);
            return true;
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
                        && slot->Quantity < stackSize)
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
        // Only treat untradable iLvl 1 Lv-1 equippable gear (starter/event glamour) as Lv 1 glamour/starter.
        // Never trap XP bonus rings (iLvl 30/60), tradable high-value Lv 1 crafted glamour, or BLU Soul Crystal.
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
                    ids.Add(HoldingsService.NormalizeBaseItemId(rawId));
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

        // Ignore untouched Lv 1 class unlocks and untouched 0-XP expansion unlocks (using ClassJob.StartingLevel)
        var startLv = Math.Max(1, (int)job.StartingLevel);
        return jobLevel <= 1 || (jobExp == 0 && jobLevel <= startLv);
    }

    public Dictionary<uint, int> BuildUnlockedJobTopIlvl()
    {
        var now = DateTime.UtcNow;
        var cid = GetCurrentContentId();
        if (cid == cachedJobIlvlCharacterId && (now - lastJobIlvlCacheUtc).TotalMilliseconds < CacheTtlMs)
        {
            return cachedUnlockedJobTopIlvl;
        }

        var result = new Dictionary<uint, int>();
        var ps = PlayerState.Instance();
        var gearsets = RaptureGearsetModule.Instance();
        var classJobSheet = dataManager.GetExcelSheet<ClassJob>();
        var itemSheet = dataManager.GetExcelSheet<Item>();
        if (ps == null || classJobSheet == null || itemSheet == null)
        {
            return result;
        }

        var jobIlvlsFromSets = new Dictionary<uint, int>();
        if (gearsets != null)
        {
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

                var setIlvl = (int)entry.ItemLevel;
                if (setIlvl <= 0)
                {
                    for (var s = 0; s < entry.Items.Length; s++)
                    {
                        var rawId = entry.Items[s].ItemId;
                        if (rawId == 0)
                        {
                            continue;
                        }

                        var row = itemSheet.GetRowOrDefault(HoldingsService.NormalizeBaseItemId(rawId));
                        if (row is not null)
                        {
                            setIlvl = Math.Max(setIlvl, (int)row.Value.LevelItem.RowId);
                        }
                    }
                }

                jobIlvlsFromSets[entry.ClassJob] = Math.Max(jobIlvlsFromSets.GetValueOrDefault(entry.ClassJob), setIlvl);
            }
        }

        var manager = InventoryManager.Instance();
        if (manager != null && ps->CurrentClassJobId != 0)
        {
            var equipped = manager->GetInventoryContainer(InventoryType.EquippedItems);
            if (equipped != null && equipped->Items != null)
            {
                var sum = 0;
                var count = 0;
                for (var s = 0; s < equipped->Size; s++)
                {
                    var slot = equipped->GetInventorySlot(s);
                    if (slot == null || slot->ItemId == 0)
                    {
                        continue;
                    }

                    var row = itemSheet.GetRowOrDefault(HoldingsService.NormalizeBaseItemId(slot->ItemId));
                    if (row is not null && row.Value.LevelItem.RowId > 0 && !ItemIdentity.IsSoulCrystal(row.Value))
                    {
                        sum += (int)row.Value.LevelItem.RowId;
                        count++;
                    }
                }

                if (count > 0)
                {
                    var avg = sum / count;
                    jobIlvlsFromSets[ps->CurrentClassJobId] = Math.Max(jobIlvlsFromSets.GetValueOrDefault(ps->CurrentClassJobId), avg);
                }
            }
        }

        var ownedSoulCrystalJobs = ScanOwnedSoulCrystalJobs(classJobSheet, itemSheet);

        foreach (var job in classJobSheet)
        {
            if (job.RowId == 0 || job.ExpArrayIndex < 0 || job.ExpArrayIndex >= ps->ClassJobLevels.Length)
            {
                continue;
            }

            if (ps->ClassJobLevels[job.ExpArrayIndex] <= 0)
            {
                continue;
            }

            if (jobIlvlsFromSets.TryGetValue(job.RowId, out var directIlvl))
            {
                result[job.RowId] = directIlvl;
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
                    }
                }

                if (!hasChildSet && IsUntouchedUnlockJob(ps, job, hasGearset: false))
                {
                    continue;
                }

                result[job.RowId] = hasChildSet ? inherited : 0;
            }
            else if (ownedSoulCrystalJobs.Contains(job.RowId))
            {
                if (IsUntouchedUnlockJob(ps, job, hasGearset: false))
                {
                    continue;
                }

                result[job.RowId] = 0;
            }
        }

        cachedJobIlvlCharacterId = cid;
        cachedUnlockedJobTopIlvl = result;
        lastJobIlvlCacheUtc = now;
        return result;
    }

    public string? ResolveEffectiveSlotRoute(
        MagicSortEntry entry,
        Item itemRow,
        InventoryItem* liveItem,
        IReadOnlyDictionary<uint, MagicSortEntry> manifestEntries,
        HashSet<uint> gearsetIds,
        Dictionary<uint, int> unlockedJobTopIlvl)
    {
        if (string.Equals(entry.Route, "Keep", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (liveItem == null)
        {
            if (string.Equals(entry.Route, "Market", StringComparison.OrdinalIgnoreCase) && itemRow.IsUntradable)
            {
                return null;
            }

            if (entry.Route is "Vendor" or "GrandCompany" && IsLv1GlamourOrStarterGear(itemRow))
            {
                return null;
            }

            return entry.Route;
        }

        if (entry.Route is "Market")
        {
            if (IsSafeToDisposeOrList(liveItem, itemRow, "Market", manifestEntries, gearsetIds, unlockedJobTopIlvl))
            {
                return "Market";
            }

            if (itemRow.EquipSlotCategory.RowId > 0
                && liveItem->SpiritbondOrCollectability is > 0 and < 10000
                && itemRow.Rarity == 1
                && IsSafeToDisposeOrList(liveItem, itemRow, "Vendor", manifestEntries, gearsetIds, unlockedJobTopIlvl))
            {
                return "Vendor";
            }

            return null;
        }

        if (entry.Route is "Vendor" or "GrandCompany")
        {
            return IsSafeToDisposeOrList(liveItem, itemRow, entry.Route, manifestEntries, gearsetIds, unlockedJobTopIlvl)
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

            return "Armoire";
        }

        if (entry.Route is "Use")
        {
            return itemRow.ItemAction.RowId > 0 ? "Use" : null;
        }

        if (entry.Route is "Glamour")
        {
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
        Dictionary<uint, int> unlockedJobTopIlvl)
    {
        if (slot == null || slot->ItemId == 0 || slot->Quantity <= 0)
        {
            return false;
        }

        var baseId = HoldingsService.NormalizeBaseItemId(slot->ItemId);
        if (!CanLockOrTagBaseId(baseId) || IsItemTracked(baseId))
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

        if (ItemIdentity.IsSoulCrystal(itemRow) || itemRow.Rarity >= 4 || ItemIdentity.IsCollectable(slot))
        {
            return false;
        }

        // Only treat a manual override as permitting disposal when the override itself targets this disposal route
        // (prevents another character's "Retainer:<id>" override from disabling disposal safety guards on alts).
        var hasManualDisposalOverride = configuration.ManualRouteOverrides.TryGetValue(baseId, out var overrideSpec)
            && string.Equals(overrideSpec, targetRoute, StringComparison.OrdinalIgnoreCase);

        // Hard-protect Housing Furnishings (FilterGroup 14/34 + all housing categories) from disposal/market unless explicitly overridden
        if (!hasManualDisposalOverride && ItemIdentity.IsHousingItem(itemRow))
        {
            return false;
        }

        // Hard-protect Glamoured or Dyed gear (e.g. Jet Black / Pure White dye) from disposal/market unless explicitly overridden
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

        // Protect HQ crafted equipment from NPC vendor disposal (matching GC HQ protection)
        var isHq = ItemIdentity.IsHighQuality(slot);
        if (!hasManualDisposalOverride && targetRoute == "Vendor" && itemRow.EquipSlotCategory.RowId > 0 && isHq)
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

        if (targetRoute is "Vendor" or "GrandCompany" && itemRow.EquipSlotCategory.RowId > 0)
        {
            var ps = PlayerState.Instance();
            var classJobSheet = dataManager.GetExcelSheet<ClassJob>();
            var category = itemRow.ClassJobCategory.Value;
            if (ps == null || classJobSheet == null)
            {
                return false;
            }

            var confirmedOutgearedForBlue = false;
            foreach (var job in classJobSheet)
            {
                if (job.RowId == 0 || job.ExpArrayIndex < 0 || job.ExpArrayIndex >= ps->ClassJobLevels.Length)
                {
                    continue;
                }

                if (!unlockedJobTopIlvl.TryGetValue(job.RowId, out var topIlvl))
                {
                    continue;
                }

                var jobLevel = ps->ClassJobLevels[job.ExpArrayIndex];
                if (jobLevel <= 0)
                {
                    continue;
                }

                if (!CanJobUseCategory(category, job.RowId))
                {
                    continue;
                }

                // Protect near-future leveling gear (up to +4 levels ahead) and current upgrades/BiS (within 5 iLvl)
                if ((itemRow.LevelEquip > jobLevel && itemRow.LevelEquip <= jobLevel + 4)
                    || (itemRow.LevelEquip <= jobLevel && topIlvl > 0 && (int)itemRow.LevelItem.RowId >= topIlvl - 5))
                {
                    return false;
                }

                // Only confirm outgeared for Blue raid/tome gear when a job can actually equip it AND clearly outgears it
                if (jobLevel >= itemRow.LevelEquip && topIlvl > 0 && (int)itemRow.LevelItem.RowId < topIlvl - 5)
                {
                    confirmedOutgearedForBlue = true;
                }
            }

            // Never auto-dispose Blue (Rarity 3) Raid/Tome/Artifact gear unless an unlocked job actually outgears it
            if (!hasManualDisposalOverride && itemRow.Rarity == 3 && !confirmedOutgearedForBlue)
            {
                return false;
            }
        }

        if (targetRoute == "GrandCompany")
        {
            if (isHq || (!hasManualDisposalOverride && !itemRow.IsUntradable))
            {
                return false;
            }
        }
        else if (targetRoute == "Vendor")
        {
            if (!manifestEntries.TryGetValue(baseId, out var entry)
                || itemRow.PriceLow == 0
                || itemRow.IsUnique)
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

    public static bool CanJobUseCategory(ClassJobCategory category, string abbr)
    {
        return abbr switch
        {
            "GLA" => category.GLA,
            "PGL" => category.PGL,
            "MRD" => category.MRD,
            "LNC" => category.LNC,
            "ARC" => category.ARC,
            "CNJ" => category.CNJ,
            "THM" => category.THM,
            "CRP" => category.CRP,
            "BSM" => category.BSM,
            "ARM" => category.ARM,
            "GSM" => category.GSM,
            "LTW" => category.LTW,
            "WVR" => category.WVR,
            "ALC" => category.ALC,
            "CUL" => category.CUL,
            "MIN" => category.MIN,
            "BTN" => category.BTN,
            "FSH" => category.FSH,
            "PLD" => category.PLD,
            "MNK" => category.MNK,
            "WAR" => category.WAR,
            "DRG" => category.DRG,
            "BRD" => category.BRD,
            "WHM" => category.WHM,
            "BLM" => category.BLM,
            "ACN" => category.ACN,
            "SMN" => category.SMN,
            "SCH" => category.SCH,
            "ROG" => category.ROG,
            "NIN" => category.NIN,
            "MCH" => category.MCH,
            "DRK" => category.DRK,
            "AST" => category.AST,
            "SAM" => category.SAM,
            "RDM" => category.RDM,
            "BLU" => category.BLU,
            "GNB" => category.GNB,
            "DNC" => category.DNC,
            "RPR" => category.RPR,
            "SGE" => category.SGE,
            "VPR" => category.VPR,
            "PCT" => category.PCT,
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

                var itemRow = itemSheet.GetRowOrDefault(HoldingsService.NormalizeBaseItemId(slot->ItemId));
                if (itemRow is null || itemRow.Value.ItemUICategory.RowId != 62)
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

    /// <summary>
    /// Returns the true live count of items currently listed on the active retainer's market (0..20).
    /// Trusts the live RetainerMarket container whenever it is loaded (RetainerManager.MarketItemCount stays stale
    /// after sales or cancellations until exiting back to RetainerList).
    /// </summary>
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
