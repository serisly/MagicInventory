using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.Exd;
using Lumina.Excel.Sheets;
using MagicInventory.Core;
using MagicInventory.Services.MagicSort;

namespace MagicInventory.Services.Triage;

public sealed unsafe class ItemTriageEngine : IDisposable
{
    private const double SnapshotTtlMs = 1500.0;

    private static readonly uint[] CraftTypeToClassJobId = [8, 9, 10, 11, 12, 13, 14, 15];

    private static readonly HashSet<uint> StarterCityTerritoryIds = [128, 129, 130, 131, 132, 133];

    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly IDataManager dataManager;
    private readonly Configuration configuration;
    private readonly HoldingsService holdings;
    private readonly InventorySafetyEvaluator safety;
    private readonly UniversalisClient universalis;

    private readonly Dictionary<uint, uint> cabinetRowByItemId = new();
    private readonly Dictionary<string, uint> uiCategoryIdByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<uint> easyCityVendorItemIds = new();
    private readonly HashSet<uint> globalRecipeIngredientIds = new();

    private Dictionary<uint, MagicSortEntry> cachedEntries = new();
    private DateTime lastSnapshotUtc = DateTime.MinValue;
    private ulong lastSnapshotCharId;
    private volatile bool invalidated = true;

    private ulong cachedCraftCharId;
    private string cachedCraftLevelSignature = string.Empty;
    private HashSet<uint> cachedUpcomingCraftIngredients = new();

    public ItemTriageEngine(
        IClientState clientState,
        IObjectTable objectTable,
        IDataManager dataManager,
        Configuration configuration,
        HoldingsService holdings,
        InventorySafetyEvaluator safety,
        UniversalisClient universalis)
    {
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.dataManager = dataManager;
        this.configuration = configuration;
        this.holdings = holdings;
        this.safety = safety;
        this.universalis = universalis;

        LoadCabinetLookup();
        LoadUiCategoryLookup();
        LoadShopAndRecipeLookups();

        this.universalis.PricesUpdated += OnExternalDataUpdated;
        this.holdings.RetainerSnapshotUpdated += OnExternalDataUpdated;
    }

    public event System.Action? TriageUpdated;

    public int ClassifiedCount => cachedEntries.Count;
    public int PendingPriceCount { get; private set; }
    public string ActiveWorldName => ResolveHomeWorldName();
    public string ActiveDataCenterName => ResolveDataCenterName();
    public bool IsCabinetItem(uint baseItemId) => cabinetRowByItemId.ContainsKey(ItemIdentity.NormalizeBaseItemId(baseItemId));

    public void Invalidate()
    {
        invalidated = true;
        safety.InvalidateCaches();
    }

    public int TriggerFullScan(bool forceRefreshPrices)
    {
        var world = ResolveHomeWorldName();
        if (string.IsNullOrWhiteSpace(world))
        {
            return 0;
        }

        var itemSheet = dataManager.GetExcelSheet<Item>();
        if (itemSheet == null)
        {
            return 0;
        }

        var ownedIds = CollectOwnedBaseItemIds();
        var tradableIds = new List<uint>(ownedIds.Count);
        foreach (var id in ownedIds)
        {
            if (!ItemIdentity.CanLockOrTagBaseId(id))
            {
                continue;
            }

            var row = itemSheet.GetRowOrDefault(id);
            if (row is not null && ItemIdentity.IsMarketable(row.Value) && !ItemIdentity.IsCurrencyOrCrystal(id, row.Value))
            {
                tradableIds.Add(id);
            }
        }

        Invalidate();
        return universalis.QueueMissing(world, tradableIds, forceRefresh: forceRefreshPrices);
    }

    public IReadOnlyDictionary<uint, MagicSortEntry> GetCurrentEntries()
    {
        var now = DateTime.UtcNow;
        var cid = InventorySafetyEvaluator.GetCurrentContentId();
        if (!invalidated
            && cid == lastSnapshotCharId
            && cachedEntries.Count > 0
            && (now - lastSnapshotUtc).TotalMilliseconds < SnapshotTtlMs)
        {
            return cachedEntries;
        }

        RebuildSnapshot(cid, now);
        return cachedEntries;
    }

    public MagicSortEntry? ClassifySingleItemOnDemand(
        uint baseItemId,
        bool queuePriceIfMissing = false,
        bool ignoreManualOverride = false)
    {
        var normalized = ItemIdentity.NormalizeBaseItemId(baseItemId);
        if (normalized == 0)
        {
            return null;
        }

        if (!ignoreManualOverride)
        {
            var entries = GetCurrentEntries();
            if (entries.TryGetValue(normalized, out var existing))
            {
                return existing;
            }
        }

        var itemSheet = dataManager.GetExcelSheet<Item>();
        var row = itemSheet?.GetRowOrDefault(normalized);
        if (row is null)
        {
            return null;
        }

        var world = ResolveHomeWorldName();
        var retainers = holdings.GetKnownRetainersForCurrentCharacter();
        var cid = InventorySafetyEvaluator.GetCurrentContentId();
        var profile = configuration.GetOrCreateCharacter(cid);
        Configuration.MigrateLegacyRetainerSpecialties(profile, ResolveUiCategoryIdByName);
        var gearsets = safety.BuildGearsetItemIds();
        var jobProfile = safety.BuildUnlockedJobGearProfile();
        var equipped = CollectEquippedIds();
        var upcomingCrafts = GetUpcomingCraftIngredients(cid);
        var missingPrices = new List<uint>();

        var context = BuildSingleItemObservation(normalized, retainers);

        var entry = ClassifyItem(
            normalized,
            row.Value,
            context,
            world,
            profile,
            retainers,
            equipped,
            gearsets,
            jobProfile,
            upcomingCrafts,
            missingPrices,
            ignoreManualOverride);

        if (queuePriceIfMissing && missingPrices.Count > 0 && !string.IsNullOrWhiteSpace(world))
        {
            universalis.QueueMissing(world, missingPrices);
        }

        return entry;
    }

    private ItemObservation BuildSingleItemObservation(uint normalizedBaseId, IReadOnlyList<KnownRetainerInfo> retainers)
    {
        var map = BuildItemObservations(retainers, filterBaseId: normalizedBaseId);
        return map.TryGetValue(normalizedBaseId, out var obs)
            ? obs
            : new ItemObservation(
                TotalQty: 1,
                NqQty: 1,
                HqQty: 0,
                AllMelded: false,
                AllGlamoured: false,
                AllEquipSoulbound: false,
                AnyExtractableMateria: false);
    }

    private void OnExternalDataUpdated()
    {
        invalidated = true;
        TriageUpdated?.Invoke();
    }

    private void RebuildSnapshot(ulong contentId, DateTime now)
    {
        var itemSheet = dataManager.GetExcelSheet<Item>();
        if (!clientState.IsLoggedIn || itemSheet == null)
        {
            cachedEntries = new Dictionary<uint, MagicSortEntry>();
            PendingPriceCount = 0;
            lastSnapshotUtc = now;
            lastSnapshotCharId = contentId;
            invalidated = false;
            return;
        }

        var world = ResolveHomeWorldName();
        var localPlayer = objectTable.LocalPlayer;
        var profile = configuration.GetOrCreateCharacter(
            contentId,
            localPlayer?.Name.TextValue,
            world);
        Configuration.MigrateLegacyRetainerSpecialties(profile, ResolveUiCategoryIdByName);

        var retainers = holdings.GetKnownRetainersForCurrentCharacter();
        var equipped = CollectEquippedIds();
        var gearsets = safety.BuildGearsetItemIds();
        var jobProfile = safety.BuildUnlockedJobGearProfile();
        var upcomingCrafts = GetUpcomingCraftIngredients(contentId);

        var observations = BuildItemObservations(retainers);
        var nextEntries = new Dictionary<uint, MagicSortEntry>(observations.Count);
        var missingPriceIds = new List<uint>();

        foreach (var (baseId, obs) in observations)
        {
            var row = itemSheet.GetRowOrDefault(baseId);
            if (row is null)
            {
                continue;
            }

            var entry = ClassifyItem(
                baseId,
                row.Value,
                obs,
                world,
                profile,
                retainers,
                equipped,
                gearsets,
                jobProfile,
                upcomingCrafts,
                missingPriceIds);

            nextEntries[baseId] = entry;
        }

        var actionableMissing = 0;
        if (missingPriceIds.Count > 0 && !string.IsNullOrWhiteSpace(world))
        {
            universalis.QueueMissing(world, missingPriceIds);
            foreach (var id in missingPriceIds)
            {
                if (!universalis.IsOnNegativeCooldown(world, id))
                {
                    actionableMissing++;
                }
            }
        }

        cachedEntries = nextEntries;
        PendingPriceCount = actionableMissing;
        lastSnapshotUtc = now;
        lastSnapshotCharId = contentId;
        invalidated = false;
    }

    private sealed record ItemObservation(
        int TotalQty,
        int NqQty,
        int HqQty,
        bool AllMelded,
        bool AllGlamoured,
        bool AllEquipSoulbound,
        bool AnyExtractableMateria)
    {
        public bool HasNq => NqQty > 0;
        public bool HasHq => HqQty > 0;
    }

    private Dictionary<uint, ItemObservation> BuildItemObservations(
        IReadOnlyList<KnownRetainerInfo> retainers,
        uint? filterBaseId = null)
    {
        var qtyById = new Dictionary<uint, int>();
        var nqQtyById = new Dictionary<uint, int>();
        var hqQtyById = new Dictionary<uint, int>();
        var meldedCount = new Dictionary<uint, int>();
        var glamouredCount = new Dictionary<uint, int>();
        var anyExtractableMateria = new HashSet<uint>();
        var equipCount = new Dictionary<uint, int>();
        var soulboundCount = new Dictionary<uint, int>();

        var itemSheet = dataManager.GetExcelSheet<Item>();
        var manager = InventoryManager.Instance();
        if (manager != null)
        {
            void ScanContainer(InventoryType cType)
            {
                var container = manager->GetInventoryContainer(cType);
                if (!InventorySafetyEvaluator.IsContainerReadable(container))
                {
                    return;
                }

                for (var i = 0; i < container->Size; i++)
                {
                    var slot = InventorySafetyEvaluator.GetSlot(container, i);
                    if (slot == null || slot->ItemId == 0 || slot->Quantity <= 0)
                    {
                        continue;
                    }

                    var baseId = ItemIdentity.NormalizeBaseItemId(slot->ItemId);
                    if (baseId == 0 || (filterBaseId.HasValue && baseId != filterBaseId.Value))
                    {
                        continue;
                    }

                    var slotQty = (int)slot->Quantity;
                    qtyById[baseId] = qtyById.GetValueOrDefault(baseId) + slotQty;

                    if (ItemIdentity.IsHighQuality(slot))
                    {
                        hqQtyById[baseId] = hqQtyById.GetValueOrDefault(baseId) + slotQty;
                    }
                    else
                    {
                        nqQtyById[baseId] = nqQtyById.GetValueOrDefault(baseId) + slotQty;
                    }

                    var row = itemSheet?.GetRowOrDefault(baseId);
                    var isEquipRow = row is not null && row.Value.EquipSlotCategory.RowId > 0;

                    if (isEquipRow && slot->SpiritbondOrCollectability >= 10000
                        && InventorySafetyEvaluator.IsMateriaExtractable(row!.Value, slot))
                    {
                        anyExtractableMateria.Add(baseId);
                    }

                    if (isEquipRow && cType != InventoryType.EquippedItems)
                    {
                        equipCount[baseId] = equipCount.GetValueOrDefault(baseId) + 1;

                        if (slot->GetMateriaCount() > 0 || slot->Materia[0] != 0)
                        {
                            meldedCount[baseId] = meldedCount.GetValueOrDefault(baseId) + 1;
                        }

                        if (slot->GlamourId != 0 || slot->GetStain(0) != 0 || slot->GetStain(1) != 0)
                        {
                            glamouredCount[baseId] = glamouredCount.GetValueOrDefault(baseId) + 1;
                        }

                        if (slot->SpiritbondOrCollectability > 0)
                        {
                            soulboundCount[baseId] = soulboundCount.GetValueOrDefault(baseId) + 1;
                        }
                    }
                }
            }

            foreach (var bag in InventorySafetyEvaluator.PlayerBagContainers)
            {
                ScanContainer(bag);
            }

            ScanContainer(InventoryType.EquippedItems);

            foreach (var arm in InventorySafetyEvaluator.ArmouryGearContainers)
            {
                ScanContainer(arm);
            }

            ScanContainer(InventoryType.ArmorySoulCrystal);
        }

        void AddRetainerItem(uint itemId, int qty, int hqQty)
        {
            if (itemId == 0 || qty <= 0)
            {
                return;
            }

            var clampedHq = Math.Clamp(hqQty, 0, qty);
            var nqQty = qty - clampedHq;
            qtyById[itemId] = qtyById.GetValueOrDefault(itemId) + qty;
            if (clampedHq > 0)
            {
                hqQtyById[itemId] = hqQtyById.GetValueOrDefault(itemId) + clampedHq;
            }

            if (nqQty > 0)
            {
                nqQtyById[itemId] = nqQtyById.GetValueOrDefault(itemId) + nqQty;
            }

            var row = itemSheet?.GetRowOrDefault(itemId);
            if (row is not null && row.Value.EquipSlotCategory.RowId > 0)
            {
                equipCount[itemId] = equipCount.GetValueOrDefault(itemId) + qty;
            }
        }

        foreach (var ret in retainers)
        {
            if (filterBaseId.HasValue)
            {
                var targetId = filterBaseId.Value;
                if (ret.Quantities.TryGetValue(targetId, out var targetQty) && targetQty > 0)
                {
                    AddRetainerItem(targetId, targetQty, ret.HqQuantities?.GetValueOrDefault(targetId) ?? 0);
                }

                continue;
            }

            foreach (var (itemId, qty) in ret.Quantities)
            {
                AddRetainerItem(itemId, qty, ret.HqQuantities?.GetValueOrDefault(itemId) ?? 0);
            }
        }

        var result = new Dictionary<uint, ItemObservation>(qtyById.Count);
        foreach (var (id, totalQty) in qtyById)
        {
            var eCount = equipCount.GetValueOrDefault(id);
            var mCount = meldedCount.GetValueOrDefault(id);
            var gCount = glamouredCount.GetValueOrDefault(id);
            var sbCount = soulboundCount.GetValueOrDefault(id);
            result[id] = new ItemObservation(
                totalQty,
                nqQtyById.GetValueOrDefault(id),
                hqQtyById.GetValueOrDefault(id),
                eCount > 0 && mCount == eCount,
                eCount > 0 && gCount == eCount,
                eCount > 0 && sbCount == eCount,
                anyExtractableMateria.Contains(id));
        }

        return result;
    }

    private MagicSortEntry ClassifyItem(
        uint baseId,
        Item itemRow,
        ItemObservation obs,
        string world,
        CharacterProfile profile,
        IReadOnlyList<KnownRetainerInfo> retainers,
        HashSet<uint> equippedIds,
        HashSet<uint> gearsetIds,
        UnlockedJobGearProfile jobProfile,
        HashSet<uint> upcomingCraftIngredients,
        List<uint> missingPriceIds,
        bool ignoreManualOverride = false)
    {
        var name = itemRow.Name.ExtractText();
        if (string.IsNullOrWhiteSpace(name))
        {
            name = $"Item #{baseId}";
        }

        var uiCatId = itemRow.ItemUICategory.RowId;
        var uiCat = itemRow.ItemUICategory.ValueNullable?.Name.ExtractText() ?? string.Empty;
        var priceLow = (long)itemRow.PriceLow;
        var untradable = itemRow.IsUntradable;

        MarketPriceSnapshot? mkt = null;
        var hasFreshPrice = false;
        var isMarketable = ItemIdentity.IsMarketable(itemRow);
        if (isMarketable && ItemIdentity.CanLockOrTagBaseId(baseId) && !string.IsNullOrWhiteSpace(world))
        {
            if (universalis.TryGetFreshPrice(world, baseId, out var fresh))
            {
                mkt = fresh;
                hasFreshPrice = true;
            }
            else if (universalis.TryGetAnyCachedPrice(world, baseId, out var stale))
            {
                mkt = stale;
                missingPriceIds.Add(baseId);
            }
        }

        if (!ignoreManualOverride
            && profile.ManualRouteOverrides.TryGetValue(baseId, out var overrideSpec)
            && ItemRoute.TryParse(overrideSpec, out var overrideRoute))
        {
            if (overrideRoute.IsSpecificRetainer)
            {
                var retMatch = retainers.FirstOrDefault(r => r.RetainerId == overrideRoute.RetainerId);
                if (retMatch != null)
                {
                    return new MagicSortEntry(
                        baseId,
                        name,
                        overrideRoute,
                        retMatch.Name,
                        0,
                        0,
                        priceLow,
                        untradable,
                        $"Override: {retMatch.Name}");
                }
            }
            else if (!overrideRoute.IsRetainer)
            {
                long ovTargetNq = 0;
                long ovTargetHq = 0;
                if (overrideRoute.Kind == RouteKind.Market && isMarketable)
                {
                    if (mkt == null && !string.IsNullOrWhiteSpace(world))
                    {
                        missingPriceIds.Add(baseId);
                    }
                    else if (mkt != null)
                    {
                        var nqMinRaw = mkt.MinPriceNq > 0 ? mkt.MinPriceNq : (mkt.SchemaVersion < 2 ? mkt.MinPriceAny : 0);
                        ovTargetNq = MarketViabilityEvaluator.ComputeUndercutPrice(nqMinRaw, mkt.RecentHistoryMedian, priceLow, configuration.MarketUndercutGil);
                        if (itemRow.CanBeHq && (mkt.MinPriceHq > 0 || mkt.RecentHistoryMedianHq > 0))
                        {
                            ovTargetHq = MarketViabilityEvaluator.ComputeUndercutPrice(mkt.MinPriceHq, mkt.RecentHistoryMedianHq, priceLow, configuration.MarketUndercutGil);
                        }
                    }
                }

                var overrideLabel = RoutePresentation.GetDisplayLabel(overrideRoute);
                return new MagicSortEntry(
                    baseId,
                    name,
                    overrideRoute,
                    string.Empty,
                    ovTargetNq,
                    ovTargetHq,
                    priceLow,
                    untradable,
                    $"Override: {overrideLabel}");
            }
        }

        if (ItemIdentity.IsCurrencyOrCrystal(baseId, itemRow))
        {
            return KeepEntry(baseId, name, priceLow, untradable, "Currency or crystal");
        }

        var isEquip = itemRow.EquipSlotCategory.RowId > 0;
        var isCabinet = cabinetRowByItemId.TryGetValue(baseId, out var cabinetRowId);
        var alreadyInArmoire = isCabinet && IsAlreadyStoredInCabinet(cabinetRowId);

        if (isEquip && obs.AnyExtractableMateria)
        {
            return new MagicSortEntry(
                baseId, name, ItemRoute.Materia, string.Empty, 0, 0, priceLow, untradable,
                "100% Spiritbond (extract materia)");
        }

        if (equippedIds.Contains(baseId))
        {
            return KeepEntry(baseId, name, priceLow, untradable, "Equipped");
        }

        if (gearsetIds.Contains(baseId))
        {
            return KeepEntry(baseId, name, priceLow, untradable, "In a Gearset");
        }

        if (itemRow.EquipSlotCategory.RowId == 17 || ItemIdentity.IsSoulCrystal(itemRow))
        {
            return KeepEntry(baseId, name, priceLow, untradable, "Job Stone");
        }

        if (ItemIdentity.IsRelicRarity(itemRow))
        {
            return KeepEntry(baseId, name, priceLow, untradable, "Relic gear");
        }

        if (isCabinet && !alreadyInArmoire && !obs.AllGlamoured && !obs.AllMelded)
        {
            return new MagicSortEntry(
                baseId, name, ItemRoute.Armoire, string.Empty, 0, 0, priceLow, untradable,
                "Can be stored in your Inn Armoire");
        }

        if (InventorySafetyEvaluator.IsLv1GlamourOrStarterGear(itemRow))
        {
            return new MagicSortEntry(
                baseId, name, ItemRoute.Glamour, string.Empty, 0, 0, priceLow, untradable,
                "Lv. 1 glamour gear (Glamour Dresser)");
        }

        if (isEquip && obs.AllMelded)
        {
            return KeepEntry(baseId, name, priceLow, untradable, "Melded gear");
        }

        if (isEquip && obs.AllGlamoured)
        {
            return KeepEntry(baseId, name, priceLow, untradable, "Glamoured or dyed gear");
        }

        var equipRelevance = isEquip
            ? InventorySafetyEvaluator.EvaluateEquipmentRelevance(jobProfile, itemRow)
            : default;

        if (isEquip && equipRelevance.IsRelevantCrafterGathererTool)
        {
            return KeepEntry(baseId, name, priceLow, untradable, "Crafter / Gatherer tool");
        }

        if (isEquip && equipRelevance.IsUpgradeOrLevelingForUnlockedJob)
        {
            return KeepEntry(
                baseId,
                name,
                priceLow,
                untradable,
                string.IsNullOrEmpty(equipRelevance.ProtectReason) ? "Gear upgrade / leveling gear" : equipRelevance.ProtectReason);
        }

        if (isEquip && priceLow == 0)
        {
            return KeepEntry(baseId, name, priceLow, untradable, "Special / starter gear");
        }

        if (ItemIdentity.ProtectedConsumableCategoryIds.Contains(uiCatId))
        {
            return KeepEntry(baseId, name, priceLow, untradable, string.IsNullOrEmpty(uiCat) ? "Food / Potion" : uiCat);
        }

        var unlockStatus = QueryItemActionUnlockStatus(baseId, itemRow);
        if (IsSafeAutoUseItem(baseId, itemRow, unlockStatus, out var useReason))
        {
            return new MagicSortEntry(baseId, name, ItemRoute.Use, string.Empty, 0, 0, priceLow, untradable, useReason);
        }

        if (IsUnlockableOrCoffer(itemRow))
        {
            if (ItemIdentity.IsTripleTriadCard(itemRow))
            {
                if (unlockStatus == 1)
                {
                    return new MagicSortEntry(
                        baseId, name, ItemRoute.GoldSaucer, string.Empty, 0, 0, priceLow, untradable,
                        "Duplicate card (trade for MGP at Gold Saucer)");
                }

                return KeepEntry(baseId, name, priceLow, untradable, "Triple Triad Card");
            }

            var isConfirmedDuplicateTradableUnlockable = unlockStatus == 1
                && isMarketable
                && !ItemIdentity.IsCofferOrContainer(itemRow);
            if (!isConfirmedDuplicateTradableUnlockable)
            {
                var keepLabel = ItemIdentity.IsCofferOrContainer(itemRow)
                    ? "Coffer / Container"
                    : (string.IsNullOrEmpty(uiCat) ? "Unlockable" : uiCat);
                return KeepEntry(baseId, name, priceLow, untradable, keepLabel);
            }
        }

        if (ItemIdentity.IsProtectedUtilityItem(baseId, itemRow))
        {
            return KeepEntry(baseId, name, priceLow, untradable, "Utility item");
        }

        if (IsQuestOrKeyItem(itemRow))
        {
            return KeepEntry(baseId, name, priceLow, untradable, "Quest / Relic item");
        }

        var stackSize = (int)Math.Max(1, itemRow.StackSize);
        var preferHq = obs.HasHq && !obs.HasNq;

        if (ItemIdentity.IsHousingItem(itemRow))
        {
            return RouteToBestRetainer(
                baseId, name, uiCatId, uiCat, preferHq, stackSize, priceLow, untradable, retainers, profile,
                overrideReasonPrefix: "Housing item");
        }

        var sellMode = configuration.SellMode;

        var allowBlueGc = sellMode != SellMode.Safe;
        if (untradable
            && isEquip
            && priceLow > 0
            && (itemRow.Rarity is 2 or 7 || (allowBlueGc && itemRow.Rarity == 3))
            && itemRow.LevelItem.RowId >= 15
            && itemRow.LevelEquip >= 15
            && (itemRow.Rarity != 3 || equipRelevance.IsConfirmedOutgearedByUnlockedJob))
        {
            return new MagicSortEntry(
                baseId, name, ItemRoute.GrandCompany, string.Empty, 0, 0, priceLow, untradable,
                $"Outgeared dungeon gear (iLvl {itemRow.LevelItem.RowId})");
        }

        if (baseId is >= 5823 and <= 5827 && priceLow > 0)
        {
            return new MagicSortEntry(
                baseId, name, ItemRoute.Vendor, string.Empty, 0, 0, priceLow, untradable,
                $"Allagan piece ({priceLow:N0}g at vendor)");
        }

        if (untradable && priceLow > 0 && isEquip && itemRow.Rarity == 1 && !obs.HasHq && itemRow.LevelItem.RowId > 1)
        {
            return new MagicSortEntry(
                baseId, name, ItemRoute.Vendor, string.Empty, 0, 0, priceLow, untradable,
                $"Old normal gear (iLvl {itemRow.LevelItem.RowId}, {priceLow:N0}g)");
        }

        MarketViabilityResult? nqViabilityForStep12 = null;

        if (isMarketable)
        {
            if (mkt == null || !hasFreshPrice)
            {
                if (!string.IsNullOrWhiteSpace(world))
                {
                    missingPriceIds.Add(baseId);
                }

                return KeepEntry(
                    baseId,
                    name,
                    priceLow,
                    untradable,
                    mkt == null ? "Checking Market Board price..." : "Refreshing Market Board price...",
                    isPendingRefresh: true);
            }

            var hasNqViability = obs.HasNq;
            var hasHqViability = obs.HasHq;
            var viabilityNq = hasNqViability
                ? MarketViabilityEvaluator.Evaluate(mkt, isHq: false, obs.NqQty, priceLow, configuration)
                : default;
            var viabilityHq = hasHqViability
                ? MarketViabilityEvaluator.Evaluate(mkt, isHq: true, obs.HqQty, priceLow, configuration)
                : default;
            nqViabilityForStep12 = hasNqViability ? viabilityNq : null;

            var nqRealistic = hasNqViability && viabilityNq.IsRealisticToSell;
            var hqRealistic = hasHqViability && viabilityHq.IsRealisticToSell;
            var primaryViability = hqRealistic && !nqRealistic
                ? viabilityHq
                : (preferHq ? viabilityHq : viabilityNq);

            var unitPrice = primaryViability.RealisticUnitPrice;
            var totalRetainerQty = retainers.Sum(r => r.Quantities.GetValueOrDefault(baseId));
            var listingCount = mkt.ListingCount;
            var isZeroListings = listingCount == 0;
            var isEasyCityShop = easyCityVendorItemIds.Contains(baseId);
            var isAnyRecipeIngredient = globalRecipeIngredientIds.Contains(baseId);
            var isUpcomingCraft = upcomingCraftIngredients.Contains(baseId);

            var isProtectedNoAutoMarket = ItemIdentity.ProtectedStoreNoAutoMarketCategoryIds.Contains(uiCatId);
            var isProtectedNoVendor = isProtectedNoAutoMarket
                || ItemIdentity.ProtectedNoVendorCategoryIds.Contains(uiCatId);

            var isCraftingStaple = (isUpcomingCraft || (sellMode == SellMode.Safe && totalRetainerQty > 0))
                && ItemIdentity.CraftingStapleCategoryIds.Contains(uiCatId)
                && unitPrice < 1500;

            var purgeCraftAllowed = sellMode != SellMode.Purge || !isUpcomingCraft;

            if (!obs.AllEquipSoulbound
                && !isProtectedNoAutoMarket
                && !isCraftingStaple
                && !isZeroListings
                && purgeCraftAllowed
                && (nqRealistic || hqRealistic))
            {
                var effTargetNq = nqRealistic ? viabilityNq.SuggestedUndercutPrice : 0L;
                var effTargetHq = hqRealistic ? viabilityHq.SuggestedUndercutPrice : 0L;
                var reason = (nqRealistic, hqRealistic) switch
                {
                    (true, true) => viabilityHq.RealisticStackValue > viabilityNq.RealisticStackValue
                        ? viabilityHq.Reason
                        : viabilityNq.Reason,
                    (false, true) => $"HQ: {viabilityHq.Reason}",
                    _ => viabilityNq.Reason,
                };

                return new MagicSortEntry(
                    baseId,
                    name,
                    ItemRoute.Market,
                    string.Empty,
                    effTargetNq,
                    effTargetHq,
                    priceLow,
                    untradable,
                    reason);
            }

            if (!isUpcomingCraft
                && !isZeroListings
                && priceLow > 0
                && !obs.HasHq
                && !isProtectedNoVendor
                && hasNqViability)
            {
                var nqUnit = viabilityNq.RealisticUnitPrice;
                var nqVel = viabilityNq.EffectiveVelocity;
                var nqStackVal = viabilityNq.RealisticStackValue;

                if (sellMode == SellMode.Purge)
                {
                    var cityRebuyTotal = (long)itemRow.PriceMid * Math.Max(obs.TotalQty, 1);
                    if (isEasyCityShop
                        && itemRow.PriceMid > 0
                        && itemRow.PriceMid <= 500
                        && cityRebuyTotal < configuration.VendorMaxStackValue
                        && nqStackVal < configuration.VendorMaxStackValue)
                    {
                        return new MagicSortEntry(
                            baseId, name, ItemRoute.Vendor, string.Empty, 0, 0, priceLow, untradable,
                            $"Sold by city vendor ({itemRow.PriceMid:N0}g, easy to re-buy)");
                    }

                    if (listingCount >= 5 && nqUnit > 0 && nqUnit <= 200 && nqStackVal < configuration.VendorMaxStackValue)
                    {
                        return new MagicSortEntry(
                            baseId, name, ItemRoute.Vendor, string.Empty, 0, 0, priceLow, untradable,
                            $"Cheap & abundant on MB (~{nqUnit:N0}g, {listingCount} listed)");
                    }
                }

                var isDeadMarketPrice = nqUnit > 0
                    && nqUnit <= Math.Max(priceLow * 2L, 30L)
                    && nqStackVal < configuration.VendorMaxStackValue
                    && nqVel < configuration.VendorMaxVelocity;

                if (isDeadMarketPrice)
                {
                    var safeAllowed = isEasyCityShop || (listingCount >= 3 && !isAnyRecipeIngredient);
                    var balancedAllowed = listingCount > 0;

                    if ((sellMode == SellMode.Safe && safeAllowed)
                        || (sellMode != SellMode.Safe && balancedAllowed))
                    {
                        var reason = isEasyCityShop
                            ? $"City vendor item ({itemRow.PriceMid:N0}g, MB ~{nqUnit:N0}g)"
                            : $"Low MB value (~{nqUnit:N0}g, {nqVel:F1}/day)";
                        return new MagicSortEntry(baseId, name, ItemRoute.Vendor, string.Empty, 0, 0, priceLow, untradable, reason);
                    }
                }
            }

            if (isZeroListings && !isEasyCityShop)
            {
                return RouteToBestRetainer(
                    baseId, name, uiCatId, uiCat, preferHq, stackSize, priceLow, untradable, retainers, profile,
                    overrideReasonPrefix: "Rare (0 on Market Board)");
            }

            if (primaryViability.IsDeadOrIlliquid
                && !isProtectedNoAutoMarket
                && (primaryViability.ServerCheapest >= configuration.MarketMinUnitPrice
                    || (long)primaryViability.ServerCheapest * Math.Max(obs.TotalQty, 1) >= configuration.MarketMinStackValue))
            {
                return RouteToBestRetainer(
                    baseId, name, uiCatId, uiCat, preferHq, stackSize, priceLow, untradable, retainers, profile,
                    overrideReasonPrefix: $"Stored ({primaryViability.Reason})");
            }
        }

        if (isEquip && itemRow.Rarity == 3)
        {
            return KeepEntry(baseId, name, priceLow, untradable, "Blue raid / tome gear");
        }

        if (isEquip
            && itemRow.Rarity == 1
            && priceLow > 0
            && !obs.HasHq
            && itemRow.LevelItem.RowId > 1
            && itemRow.LevelEquip > 1)
        {
            var canVendorTradableGear = !isMarketable;
            if (isMarketable && hasFreshPrice && mkt != null && mkt.ListingCount > 0 && nqViabilityForStep12.HasValue)
            {
                var v = nqViabilityForStep12.Value;
                var cheapCeiling = Math.Max(priceLow * 2L, 30L);
                canVendorTradableGear = v.RealisticUnitPrice > 0
                    && v.RealisticUnitPrice <= cheapCeiling
                    && (v.SoldMedian <= 0 || v.SoldMedian <= cheapCeiling);
            }

            if (canVendorTradableGear)
            {
                return new MagicSortEntry(
                    baseId, name, ItemRoute.Vendor, string.Empty, 0, 0, priceLow, untradable,
                    $"Old normal gear (iLvl {itemRow.LevelItem.RowId}, {priceLow:N0}g)");
            }
        }

        return RouteToBestRetainer(
            baseId, name, uiCatId, uiCat, preferHq, stackSize, priceLow, untradable, retainers, profile,
            overrideReasonPrefix: null);
    }

    private static MagicSortEntry KeepEntry(
        uint baseId,
        string name,
        long priceLow,
        bool untradable,
        string reason,
        bool isPendingRefresh = false,
        bool needsRetainerVisit = false)
        => new(baseId, name, ItemRoute.Keep, string.Empty, 0, 0, priceLow, untradable, reason, isPendingRefresh, needsRetainerVisit);

    private static MagicSortEntry RetainerEntry(
        uint baseId,
        string name,
        KnownRetainerInfo retainer,
        long priceLow,
        bool untradable,
        string reason)
        => new(baseId, name, ItemRoute.ForRetainer(retainer.RetainerId), retainer.Name, 0, 0, priceLow, untradable, reason);

    private MagicSortEntry RouteToBestRetainer(
        uint baseId,
        string name,
        uint uiCatId,
        string uiCat,
        bool isHq,
        int stackSize,
        long priceLow,
        bool untradable,
        IReadOnlyList<KnownRetainerInfo> retainers,
        CharacterProfile profile,
        string? overrideReasonPrefix)
    {
        if (retainers.Count == 0)
        {
            return KeepEntry(baseId, name, priceLow, untradable, "Open a Summoning Bell once to find your retainers", isPendingRefresh: true, needsRetainerVisit: true);
        }

        if (retainers.Count == 1)
        {
            var only = retainers[0];
            var reason = overrideReasonPrefix ?? $"Stored with {only.Name}";
            return RetainerEntry(baseId, name, only, priceLow, untradable, reason);
        }

        bool HasRoomOrMerge(KnownRetainerInfo ret)
            => !ret.IsVisited
                || ret.UsedBagSlots < HoldingsService.MaxRetainerBagSlots
                || (stackSize > 1 && holdings.HasPartialStackOnRetainer(ret.RetainerId, baseId, isHq));

        if (uiCatId != 0)
        {
            foreach (var ret in retainers)
            {
                if (profile.RetainerSpecialtyCategoryIds.TryGetValue(ret.RetainerId, out var specs)
                    && specs != null
                    && specs.Contains(uiCatId)
                    && HasRoomOrMerge(ret))
                {
                    var label = overrideReasonPrefix ?? $"{uiCat} ({ret.Name})";
                    return RetainerEntry(baseId, name, ret, priceLow, untradable, label);
                }
            }
        }

        var hasUnvisitedRetainers = retainers.Any(r => !r.IsVisited);

        KnownRetainerInfo? bestCatOwner = null;
        var bestCatScore = 0;
        var secondBestCatScore = 0;
        if (uiCatId != 0)
        {
            foreach (var ret in retainers)
            {
                var score = ret.CategoryCounts.GetValueOrDefault(uiCatId);
                if (score > bestCatScore)
                {
                    secondBestCatScore = bestCatScore;
                    bestCatScore = score;
                    bestCatOwner = ret;
                }
                else if (score > secondBestCatScore)
                {
                    secondBestCatScore = score;
                }
            }
        }

        var hasClearMajorityCat = !hasUnvisitedRetainers
            && bestCatOwner != null
            && bestCatScore >= 3
            && bestCatScore >= secondBestCatScore * 2
            && HasRoomOrMerge(bestCatOwner);

        if (stackSize > 1)
        {
            KnownRetainerInfo? mergeOwner = null;
            var bestMergeQty = 0;
            foreach (var ret in retainers)
            {
                if (holdings.HasPartialStackOnRetainer(ret.RetainerId, baseId, isHq))
                {
                    var qty = ret.Quantities.GetValueOrDefault(baseId);
                    if (qty > bestMergeQty)
                    {
                        bestMergeQty = qty;
                        mergeOwner = ret;
                    }
                }
            }

            if (mergeOwner != null
                && (!hasClearMajorityCat || mergeOwner.RetainerId == bestCatOwner!.RetainerId))
            {
                var reason = overrideReasonPrefix ?? $"Stacks with {mergeOwner.Name}";
                return RetainerEntry(baseId, name, mergeOwner, priceLow, untradable, reason);
            }
        }

        if (hasClearMajorityCat && bestCatOwner != null)
        {
            var label = overrideReasonPrefix ?? $"{uiCat} ({bestCatOwner.Name})";
            return RetainerEntry(baseId, name, bestCatOwner, priceLow, untradable, label);
        }

        KnownRetainerInfo? stackOwner = null;
        var largestStack = 0;
        foreach (var ret in retainers)
        {
            if (!HasRoomOrMerge(ret))
            {
                continue;
            }

            var qty = ret.Quantities.GetValueOrDefault(baseId);
            if (qty > largestStack)
            {
                largestStack = qty;
                stackOwner = ret;
            }
        }

        if (stackOwner != null)
        {
            var label = overrideReasonPrefix ?? $"Stored with {stackOwner.Name}";
            return RetainerEntry(baseId, name, stackOwner, priceLow, untradable, label);
        }

        if (hasUnvisitedRetainers)
        {
            return KeepEntry(baseId, name, priceLow, untradable, "Visit each retainer once to map their bags", isPendingRefresh: true, needsRetainerVisit: true);
        }

        const int crowdedThreshold = 158;
        var hasUncrowded = retainers.Any(r => r.UsedBagSlots < crowdedThreshold);
        if (bestCatOwner != null
            && bestCatScore >= 2
            && HasRoomOrMerge(bestCatOwner)
            && (bestCatOwner.UsedBagSlots < crowdedThreshold || !hasUncrowded))
        {
            var label = overrideReasonPrefix ?? $"{uiCat} ({bestCatOwner.Name})";
            return RetainerEntry(baseId, name, bestCatOwner, priceLow, untradable, label);
        }

        var candidates = hasUncrowded
            ? retainers.Where(r => r.UsedBagSlots < crowdedThreshold).ToList()
            : retainers.Where(r => r.UsedBagSlots < HoldingsService.MaxRetainerBagSlots).ToList();
        if (candidates.Count == 0)
        {
            candidates = retainers.ToList();
        }

        var chosen = candidates.OrderBy(r => r.UsedBagSlots).First();
        var defaultReason = overrideReasonPrefix ?? $"Stored with {chosen.Name}";
        return RetainerEntry(baseId, name, chosen, priceLow, untradable, defaultReason);
    }

    private static long QueryItemActionUnlockStatus(uint baseId, Item itemRow)
    {
        if (itemRow.ItemAction.RowId == 0)
        {
            return 0;
        }

        var uiState = UIState.Instance();
        if (uiState == null)
        {
            return 0;
        }

        var exdRow = ExdModule.GetItemRowById(baseId);
        return exdRow != null ? uiState->IsItemActionUnlocked(exdRow) : 0;
    }

    public static bool EvaluateAutoUsePolicy(
        bool isUntradable,
        bool isMarketable,
        bool hasItemAction,
        bool isEquipment,
        bool isProtectedConsumable,
        bool isProtectedUtility,
        bool isMgpCard,
        bool isCofferOrContainer,
        long unlockStatus,
        out string reason)
    {
        reason = string.Empty;
        if (!hasItemAction || isEquipment || isProtectedConsumable || isProtectedUtility || isCofferOrContainer)
        {
            return false;
        }

        if (isMgpCard)
        {
            reason = "MGP Card";
            return true;
        }

        if (unlockStatus == 2 && isUntradable && !isMarketable)
        {
            reason = "Unlockable";
            return true;
        }

        return false;
    }

    private static bool IsSafeAutoUseItem(uint baseId, Item itemRow, long unlockStatus, out string reason)
        => EvaluateAutoUsePolicy(
            isUntradable: itemRow.IsUntradable,
            isMarketable: ItemIdentity.IsMarketable(itemRow),
            hasItemAction: itemRow.ItemAction.RowId != 0,
            isEquipment: itemRow.EquipSlotCategory.RowId > 0,
            isProtectedConsumable: ItemIdentity.ProtectedConsumableCategoryIds.Contains(itemRow.ItemUICategory.RowId),
            isProtectedUtility: ItemIdentity.IsProtectedUtilityItem(baseId, itemRow),
            isMgpCard: ItemIdentity.IsMgpCard(itemRow),
            isCofferOrContainer: ItemIdentity.IsCofferOrContainer(itemRow),
            unlockStatus: unlockStatus,
            out reason);

    private static bool IsUnlockableOrCoffer(Item itemRow)
    {
        if (ItemIdentity.IsUnlockableCategory(itemRow) || ItemIdentity.IsCofferOrContainer(itemRow))
        {
            return true;
        }

        return itemRow.ItemAction.RowId > 0
            && itemRow.EquipSlotCategory.RowId == 0
            && !ItemIdentity.CraftingStapleCategoryIds.Contains(itemRow.ItemUICategory.RowId)
            && !ItemIdentity.ProtectedConsumableCategoryIds.Contains(itemRow.ItemUICategory.RowId);
    }

    private static bool IsQuestOrKeyItem(Item itemRow)
    {
        return itemRow.IsUntradable
            && itemRow.PriceLow == 0
            && itemRow.EquipSlotCategory.RowId == 0
            && itemRow.ItemUICategory.RowId == ItemIdentity.UiCatOther
            && itemRow.ItemSearchCategory.RowId == 0;
    }

    private void LoadUiCategoryLookup()
    {
        var sheet = dataManager.GetExcelSheet<ItemUICategory>();
        if (sheet == null)
        {
            return;
        }

        foreach (var row in sheet)
        {
            var n = row.Name.ExtractText();
            if (row.RowId != 0 && !string.IsNullOrWhiteSpace(n))
            {
                uiCategoryIdByName.TryAdd(n, row.RowId);
            }
        }
    }

    private uint? ResolveUiCategoryIdByName(string name)
        => !string.IsNullOrWhiteSpace(name) && uiCategoryIdByName.TryGetValue(name, out var id) ? id : null;

    private static bool IsAlreadyStoredInCabinet(uint cabinetRowId)
    {
        var uiState = UIState.Instance();
        return uiState != null
            && uiState->Cabinet.IsCabinetLoaded()
            && uiState->Cabinet.IsItemInCabinet(cabinetRowId);
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

    private void LoadShopAndRecipeLookups()
    {
        var recipeSheet = dataManager.GetExcelSheet<Recipe>();
        if (recipeSheet != null)
        {
            foreach (var recipe in recipeSheet)
            {
                foreach (var ing in recipe.Ingredient)
                {
                    if (ing.RowId > 0)
                    {
                        globalRecipeIngredientIds.Add(ing.RowId);
                    }
                }
            }
        }

        var easyCityNpcIds = new HashSet<uint>();
        var levelSheet = dataManager.GetExcelSheet<Level>();
        if (levelSheet != null)
        {
            foreach (var lvl in levelSheet)
            {
                if (lvl.Type != 8 || lvl.Object.RowId == 0)
                {
                    continue;
                }

                var terr = lvl.Territory.ValueNullable;
                if (terr is null)
                {
                    continue;
                }

                var useId = terr.Value.TerritoryIntendedUse.RowId;
                if (useId is 13 or 14 || StarterCityTerritoryIds.Contains(terr.Value.RowId))
                {
                    easyCityNpcIds.Add(lvl.Object.RowId);
                }
            }
        }

        var easyCityShopIds = new HashSet<uint>();
        var gilShopSheet = dataManager.GetExcelSheet<GilShop>();
        var topicSelectSheet = dataManager.GetExcelSheet<TopicSelect>();
        var preHandlerSheet = dataManager.GetExcelSheet<PreHandler>();
        var enpcSheet = dataManager.GetExcelSheet<ENpcBase>();
        if (gilShopSheet != null && enpcSheet != null)
        {
            void TryRecordShop(uint targetId)
            {
                if (targetId == 0)
                {
                    return;
                }

                if (gilShopSheet.TryGetRow(targetId, out var directShop) && directShop.Quest.RowId == 0)
                {
                    easyCityShopIds.Add(targetId);
                    return;
                }

                if (preHandlerSheet != null && preHandlerSheet.TryGetRow(targetId, out var pre))
                {
                    var innerId = pre.Target.RowId;
                    if (innerId != 0 && gilShopSheet.TryGetRow(innerId, out var preShop) && preShop.Quest.RowId == 0)
                    {
                        easyCityShopIds.Add(innerId);
                    }
                }
            }

            foreach (var npcId in easyCityNpcIds)
            {
                var npc = enpcSheet.GetRowOrDefault(npcId);
                if (npc is null)
                {
                    continue;
                }

                foreach (var dataRef in npc.Value.ENpcData)
                {
                    var rawId = dataRef.RowId;
                    if (rawId == 0)
                    {
                        continue;
                    }

                    TryRecordShop(rawId);

                    if (topicSelectSheet != null && topicSelectSheet.TryGetRow(rawId, out var topic))
                    {
                        foreach (var sub in topic.Shop)
                        {
                            TryRecordShop(sub.RowId);
                        }
                    }
                }
            }
        }

        var gilShopItems = dataManager.GetSubrowExcelSheet<GilShopItem>();
        if (gilShopItems != null)
        {
            foreach (var shopGroup in gilShopItems)
            {
                if (!easyCityShopIds.Contains(shopGroup.RowId))
                {
                    continue;
                }

                foreach (var entry in shopGroup)
                {
                    var itemId = entry.Item.RowId;
                    if (itemId == 0)
                    {
                        continue;
                    }

                    var hasUnlockGate = entry.AchievementRequired.RowId != 0;
                    if (!hasUnlockGate)
                    {
                        foreach (var q in entry.QuestRequired)
                        {
                            if (q.RowId != 0)
                            {
                                hasUnlockGate = true;
                                break;
                            }
                        }
                    }

                    if (!hasUnlockGate)
                    {
                        easyCityVendorItemIds.Add(itemId);
                    }
                }
            }
        }
    }

    private HashSet<uint> GetUpcomingCraftIngredients(ulong contentId)
    {
        var ps = PlayerState.Instance();
        var classJobSheet = dataManager.GetExcelSheet<ClassJob>();
        var recipeSheet = dataManager.GetExcelSheet<Recipe>();
        if (ps == null || classJobSheet == null || recipeSheet == null)
        {
            return cachedUpcomingCraftIngredients;
        }

        var levels = new int[8];
        for (var ct = 0; ct < 8; ct++)
        {
            var job = classJobSheet.GetRowOrDefault(CraftTypeToClassJobId[ct]);
            if (job is not null && job.Value.ExpArrayIndex >= 0 && job.Value.ExpArrayIndex < ps->ClassJobLevels.Length)
            {
                levels[ct] = ps->ClassJobLevels[job.Value.ExpArrayIndex];
            }
        }

        var sig = string.Join(',', levels);
        if (contentId == cachedCraftCharId && sig == cachedCraftLevelSignature)
        {
            return cachedUpcomingCraftIngredients;
        }

        var set = new HashSet<uint>();
        foreach (var recipe in recipeSheet)
        {
            var ct = (int)recipe.CraftType.RowId;
            if (ct is < 0 or >= 8 || levels[ct] <= 0)
            {
                continue;
            }

            var recLevel = recipe.RecipeLevelTable.ValueNullable?.ClassJobLevel ?? 0;
            var minProtectedLevel = Math.Max(1, levels[ct] - 10);
            if (recLevel < minProtectedLevel || recLevel > levels[ct] + 15)
            {
                continue;
            }

            var count = Math.Min(recipe.Ingredient.Count, recipe.AmountIngredient.Count);
            for (var i = 0; i < count; i++)
            {
                if (recipe.AmountIngredient[i] == 0)
                {
                    continue;
                }

                var ingId = recipe.Ingredient[i].RowId;
                if (ingId > 20)
                {
                    set.Add(ingId);
                }
            }
        }

        cachedCraftCharId = contentId;
        cachedCraftLevelSignature = sig;
        cachedUpcomingCraftIngredients = set;
        return set;
    }

    private HashSet<uint> CollectOwnedBaseItemIds()
    {
        var ids = holdings.GetAllRetainerOwnedItemIdsForCurrentCharacter();
        var manager = InventoryManager.Instance();
        if (manager == null)
        {
            return ids;
        }

        foreach (var cType in InventorySafetyEvaluator.PlayerBagContainers
            .Concat(InventorySafetyEvaluator.ArmouryGearContainers)
            .Append(InventoryType.RetainerMarket))
        {
            var container = manager->GetInventoryContainer(cType);
            if (!InventorySafetyEvaluator.IsContainerReadable(container))
            {
                continue;
            }

            for (var i = 0; i < container->Size; i++)
            {
                var slot = InventorySafetyEvaluator.GetSlot(container, i);
                if (slot != null && slot->ItemId != 0 && slot->Quantity > 0)
                {
                    ids.Add(ItemIdentity.NormalizeBaseItemId(slot->ItemId));
                }
            }
        }

        return ids;
    }

    private static HashSet<uint> CollectEquippedIds()
    {
        var ids = new HashSet<uint>();
        var manager = InventoryManager.Instance();
        if (manager == null)
        {
            return ids;
        }

        var eq = manager->GetInventoryContainer(InventoryType.EquippedItems);
        if (!InventorySafetyEvaluator.IsContainerReadable(eq))
        {
            return ids;
        }

        for (var i = 0; i < eq->Size; i++)
        {
            var slot = InventorySafetyEvaluator.GetSlot(eq, i);
            if (slot != null && slot->ItemId != 0 && slot->Quantity > 0)
            {
                ids.Add(ItemIdentity.NormalizeBaseItemId(slot->ItemId));
            }
        }

        return ids;
    }

    private string ResolveHomeWorldName()
    {
        var player = objectTable.LocalPlayer;
        if (player == null)
        {
            return string.Empty;
        }

        return player.HomeWorld.ValueNullable?.Name.ExtractText() ?? string.Empty;
    }

    private string ResolveDataCenterName()
    {
        var player = objectTable.LocalPlayer;
        if (player == null)
        {
            return string.Empty;
        }

        return player.HomeWorld.ValueNullable?.DataCenter.ValueNullable?.Name.ExtractText() ?? string.Empty;
    }

    public void Dispose()
    {
        universalis.PricesUpdated -= OnExternalDataUpdated;
        holdings.RetainerSnapshotUpdated -= OnExternalDataUpdated;
    }
}
