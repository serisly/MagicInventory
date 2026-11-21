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

/// <summary>
/// Pure in-plugin C# item triage engine (replaces external Python triage_magic_sort.py).
/// Uses live Lumina sheets, PlayerState, RaptureGearsetModule, native HoldingsService,
/// and polite background Universalis pricing with strict fail-safe PendingPrice -> Keep.
/// Zero hardcoded character IDs, world names, or retainer names.
/// </summary>
public sealed unsafe class ItemTriageEngine : IDisposable
{
    private const double SnapshotTtlMs = 1500.0;

    private static readonly string[] ActiveUtilityTokens =
    [
        "Gysahl Greens",
        "Dark Matter",
        "Glamour Prism",
        "Glamour Dispeller",
        "Clear Prism",
        "Cordial",
        "Aetheryte Ticket",
        "Durability Draught",
        "Spiritbond Potion",
        "Fantasia",
        "Phoenix Down",
        "Engineering Manual",
        "Survival Manual",
        "Company-issue",
    ];

    private static readonly string[] QuestKeyRelicTokens =
    [
        "Ceremony Invitation",
        "Atma of ",
        "Unidentifiable ",
        "Luminous Fire Crystal",
        "Luminous Wind Crystal",
        "Luminous Lightning Crystal",
        "Luminous Ice Crystal",
        "Luminous Earth Crystal",
        "Luminous Water Crystal",
        "Proto Armor Identification Key",
        "Soul of the Crafter",
        "Jandelaine's Token",
        "Gold Saucer Ticket",
    ];

    private static readonly string[] CofferUnlockTokens =
    [
        " Coffer",
        " Whistle",
        " Barding",
        "Ballroom Etiquette",
        "Modern Aesthetics",
        "Framer's Kit",
        "Fashion Accessories",
        "Parasol",
    ];

    // CraftType 0..7 -> ClassJob RowId (CRP=8..CUL=15)
    private static readonly uint[] CraftTypeToClassJobId = [8, 9, 10, 11, 12, 13, 14, 15];

    private static readonly HashSet<uint> CrafterGathererJobIds = [8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18];

    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly IDataManager dataManager;
    private readonly Configuration configuration;
    private readonly HoldingsService holdings;
    private readonly InventorySafetyEvaluator safety;
    private readonly UniversalisClient universalis;

    private readonly Dictionary<uint, uint> cabinetRowByItemId = new();
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
        var gearsets = safety.BuildGearsetItemIds();
        var savedGearsetJobs = BuildSavedGearsetJobIds();
        var unlockedTop = safety.BuildUnlockedJobTopIlvl();
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
            savedGearsetJobs,
            unlockedTop,
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

        var retainers = holdings.GetKnownRetainersForCurrentCharacter();
        var equipped = CollectEquippedIds();
        var gearsets = safety.BuildGearsetItemIds();
        var savedGearsetJobs = BuildSavedGearsetJobIds();
        var unlockedTop = safety.BuildUnlockedJobTopIlvl();
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
                savedGearsetJobs,
                unlockedTop,
                upcomingCrafts,
                missingPriceIds);

            nextEntries[baseId] = entry;
        }

        // Automatically enqueue newly looted / unpriced tradable items for polite background lookup
        if (missingPriceIds.Count > 0 && !string.IsNullOrWhiteSpace(world))
        {
            universalis.QueueMissing(world, missingPriceIds);
        }

        cachedEntries = nextEntries;
        PendingPriceCount = missingPriceIds.Count;
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

                    // Track equipment instance counts (excluding EquippedItems) so one melded/glamoured/soulbound
                    // copy does not taint clean duplicate copies; per-slot safety always guards each individual slot.
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

        // Also include every item stored across the character's known retainers
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
        HashSet<uint> savedGearsetJobIds,
        Dictionary<uint, int> unlockedJobTopIlvl,
        HashSet<uint> upcomingCraftIngredients,
        List<uint> missingPriceIds,
        bool ignoreManualOverride = false)
    {
        var name = itemRow.Name.ExtractText();
        if (string.IsNullOrWhiteSpace(name))
        {
            name = $"Item #{baseId}";
        }

        var uiCat = itemRow.ItemUICategory.ValueNullable?.Name.ExtractText() ?? string.Empty;
        var priceLow = (long)itemRow.PriceLow;
        var untradable = itemRow.IsUntradable;

        // Fetch cached market snapshot (if marketable)
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

        var nqMinRaw = mkt != null
            ? (mkt.MinPriceNq > 0 ? mkt.MinPriceNq : (mkt.SchemaVersion < UniversalisClient.CurrentSnapshotSchemaVersion ? mkt.MinPriceAny : 0))
            : 0;
        var targetNq = mkt != null && isMarketable
            ? MarketViabilityEvaluator.ComputeUndercutPrice(nqMinRaw, mkt.RecentHistoryMedian, priceLow, configuration.MarketUndercutGil)
            : 0L;
        // Fail-closed for HQ items: compute HQ listing price ONLY from HQ minimum price or HQ history median
        // (NEVER cross-price HQ from NQ prices or NQ history!)
        var targetHq = mkt != null && isMarketable && itemRow.CanBeHq && (mkt.MinPriceHq > 0 || mkt.RecentHistoryMedianHq > 0)
            ? MarketViabilityEvaluator.ComputeUndercutPrice(mkt.MinPriceHq, mkt.RecentHistoryMedianHq, priceLow, configuration.MarketUndercutGil)
            : 0L;

        // 0. Explicit Manual User Override (always wins over automatic rules)
        if (!ignoreManualOverride
            && configuration.ManualRouteOverrides.TryGetValue(baseId, out var overrideSpec)
            && !string.IsNullOrWhiteSpace(overrideSpec))
        {
            if (overrideSpec.StartsWith("Retainer:", StringComparison.OrdinalIgnoreCase)
                && ulong.TryParse(overrideSpec["Retainer:".Length..], out var ovRetId))
            {
                // Only apply retainer override if that retainer actually belongs to THIS character (prevents alt ghost IDs!)
                var retMatch = retainers.FirstOrDefault(r => r.RetainerId == ovRetId);
                if (retMatch != null)
                {
                    return new MagicSortEntry(
                        baseId,
                        name,
                        "Retainer",
                        ovRetId,
                        retMatch.Name,
                        0,
                        0,
                        priceLow,
                        untradable,
                        $"Override: {retMatch.Name}");
                }
            }
            else
            {
                var isMarketOverride = string.Equals(overrideSpec, "Market", StringComparison.OrdinalIgnoreCase);
                if (isMarketOverride && mkt == null && isMarketable && !string.IsNullOrWhiteSpace(world))
                {
                    missingPriceIds.Add(baseId);
                }

                var overrideLabel = RoutePresentation.GetDisplayLabel(overrideSpec);
                return new MagicSortEntry(
                    baseId,
                    name,
                    overrideSpec,
                    0,
                    string.Empty,
                    isMarketOverride ? targetNq : 0,
                    isMarketOverride ? targetHq : 0,
                    priceLow,
                    untradable,
                    $"Override: {overrideLabel}");
            }
        }

        // 1. Currencies & Elemental Crystals (locale-safe; NEVER uses FilterGroup == 16!)
        if (ItemIdentity.IsCurrencyOrCrystal(baseId, itemRow))
        {
            return KeepEntry(baseId, name, priceLow, untradable, "Currency or crystal");
        }

        // 2. 100% Spiritbond Materia Extraction (checked first so you never miss free materia before sorting/selling)
        var isEquip = itemRow.EquipSlotCategory.RowId > 0;
        var isCabinet = cabinetRowByItemId.TryGetValue(baseId, out var cabinetRowId);
        var alreadyInArmoire = isCabinet && IsAlreadyStoredInCabinet(cabinetRowId);

        if (isEquip && obs.AnyExtractableMateria)
        {
            return new MagicSortEntry(
                baseId, name, "Materia", 0, string.Empty, 0, 0, priceLow, untradable,
                "100% Spiritbond — extract free materia");
        }

        // 2b. Ironclad Equipment Protection

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

        if (itemRow.Rarity >= 4)
        {
            return KeepEntry(baseId, name, priceLow, untradable, "Relic gear");
        }

        // 3. Armoire-eligible gear not yet stored in Armoire (checked BEFORE upgrade rules,
        //    but NEVER route items where every owned copy is dyed/glamoured/melded because Armoire strips dyes!)
        if (isCabinet && !alreadyInArmoire && !obs.AllGlamoured && !obs.AllMelded)
        {
            return new MagicSortEntry(
                baseId, name, "Armoire", 0, string.Empty, 0, 0, priceLow, untradable,
                "Can be stored in your Inn Armoire");
        }

        // 4. Lv 1 glamour & racial starter outfits -> Glamour Dresser (checked BEFORE upgrade rules & priceLow == 0)
        if (InventorySafetyEvaluator.IsLv1GlamourOrStarterGear(itemRow))
        {
            return new MagicSortEntry(
                baseId, name, "Glamour", 0, string.Empty, 0, 0, priceLow, untradable,
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

        if (isEquip && IsUnlockedCrafterGathererTool(itemRow, unlockedJobTopIlvl))
        {
            return KeepEntry(baseId, name, priceLow, untradable, "Crafter / Gatherer tool");
        }

        if (isEquip && IsUnlockedJobBisOrUpgrade(itemRow, unlockedJobTopIlvl, savedGearsetJobIds))
        {
            return KeepEntry(baseId, name, priceLow, untradable, "Gear upgrade / leveling gear");
        }

        if (isEquip && priceLow == 0 && !isCabinet)
        {
            return KeepEntry(baseId, name, priceLow, untradable, "Special / starter gear");
        }

        // 5. Unlockables, Coffers, Food/Potions/Tinctures, Utility Consumables, Quest/Relic/Key Items
        if (ItemIdentity.ProtectedConsumableCategoryIds.Contains(itemRow.ItemUICategory.RowId))
        {
            return KeepEntry(baseId, name, priceLow, untradable, string.IsNullOrEmpty(uiCat) ? "Food / Potion" : uiCat);
        }

        var unlockStatus = QueryItemActionUnlockStatus(baseId, itemRow);
        if (IsSafeAutoUseItem(itemRow, name, unlockStatus, out var useReason))
        {
            return new MagicSortEntry(baseId, name, "Use", 0, string.Empty, 0, 0, priceLow, untradable, useReason);
        }

        if (IsUnlockableOrCoffer(itemRow, name, uiCat))
        {
            if (ItemIdentity.IsTripleTriadCard(itemRow))
            {
                return new MagicSortEntry(
                    baseId, name, "GoldSaucer", 0, string.Empty, 0, 0, priceLow, untradable,
                    "Duplicate card — trade for MGP at Gold Saucer");
            }

            // Confirmed already-unlocked tradable duplicates (e.g., duplicate Minions / Orchestrion Rolls / Mounts)
            // fall through to Step 10 so realistic value routes to Market (or Retainer if slow), while never vendoring.
            var isConfirmedDuplicateTradableUnlockable = unlockStatus == 1
                && isMarketable
                && !name.Contains(" Coffer", StringComparison.OrdinalIgnoreCase);
            if (!isConfirmedDuplicateTradableUnlockable)
            {
                return KeepEntry(baseId, name, priceLow, untradable, string.IsNullOrEmpty(uiCat) ? "Unlockable" : uiCat);
            }
        }

        if (IsActiveUtilityConsumable(name))
        {
            return KeepEntry(baseId, name, priceLow, untradable, "Utility item");
        }

        if (IsQuestOrKeyItem(itemRow, name, uiCat))
        {
            return KeepEntry(baseId, name, priceLow, untradable, "Quest / Relic item");
        }

        var stackSize = (int)Math.Max(1, itemRow.StackSize);
        var preferHq = obs.HasHq && !obs.HasNq;

        // 6. Protected Housing Furnishings (FilterGroup 14/34 or Housing UI Categories incl. Table, Construction Permit, Rug, Painting) -> Retainer storage ONLY
        if (ItemIdentity.IsHousingItem(itemRow))
        {
            return RouteToBestRetainer(
                baseId, name, uiCat, preferHq, stackSize, priceLow, untradable, retainers, profile,
                overrideReasonPrefix: "Housing item");
        }

        var sellMode = configuration.SellMode;

        // 7. Grand Company Expert Delivery
        //    Safe mode: Green (Rarity 2) outgeared dungeon gear only.
        //    Balanced / Purge: Green + confirmed-outgeared Blue (Rarity 3) gear.
        var allowBlueGc = sellMode != SellMode.Safe;
        if (untradable
            && isEquip
            && (itemRow.Rarity is 2 or 7 || (allowBlueGc && itemRow.Rarity == 3))
            && itemRow.LevelItem.RowId >= 15
            && itemRow.LevelEquip >= 15
            && (itemRow.Rarity != 3 || IsConfirmedOutgearedByUnlockedJob(itemRow, unlockedJobTopIlvl)))
        {
            return new MagicSortEntry(
                baseId, name, "GrandCompany", 0, string.Empty, 0, 0, priceLow, untradable,
                $"Outgeared dungeon gear (iLvl {itemRow.LevelItem.RowId})");
        }

        // 8. Allagan Currency Pieces (always instant NPC Gil, even offline; IDs 5823..5827 work on all client locales)
        if ((baseId is >= 5823 and <= 5827
                || (name.Contains("Allagan ", StringComparison.Ordinal) && name.Contains("Piece", StringComparison.Ordinal)))
            && priceLow > 0)
        {
            return new MagicSortEntry(
                baseId, name, "Vendor", 0, string.Empty, 0, 0, priceLow, untradable,
                $"Allagan piece ({priceLow:N0}g at vendor)");
        }

        // 9. Obsolete untradable normal gear (never Lv 1 glamour, never HQ)
        if (untradable && priceLow > 0 && isEquip && itemRow.Rarity == 1 && !obs.HasHq && itemRow.LevelItem.RowId > 1)
        {
            return new MagicSortEntry(
                baseId, name, "Vendor", 0, string.Empty, 0, 0, priceLow, untradable,
                $"Old normal gear (iLvl {itemRow.LevelItem.RowId}, {priceLow:N0}g)");
        }

        MarketViabilityResult? nqViabilityForStep12 = null;

        // 10. Marketable Market vs Vendor vs Retainer — FAIL-SAFE PendingPrice -> Keep!
        if (isMarketable)
        {
            if (mkt == null)
            {
                if (!string.IsNullOrWhiteSpace(world))
                {
                    missingPriceIds.Add(baseId);
                }

                // Cold-start / offline fail-safe: NEVER route unpriced tradable items to Vendor or Market!
                return KeepEntry(baseId, name, priceLow, untradable, "Checking Market Board price...");
            }

            // Evaluate NQ and HQ independently so mixed NQ+HQ stacks never misprice HQ as NQ or auto-vendor valuable HQ mats
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
            var velocity = primaryViability.EffectiveVelocity;
            var stackVal = primaryViability.RealisticStackValue;
            var totalRetainerQty = retainers.Sum(r => r.Quantities.GetValueOrDefault(baseId));
            var listingCount = mkt.ListingCount; // -1 = legacy cache (unknown), 0 = scarce/out of stock, >0 = active listings
            var isZeroListings = listingCount == 0;
            var isEasyCityShop = easyCityVendorItemIds.Contains(baseId);
            var isAnyRecipeIngredient = globalRecipeIngredientIds.Contains(baseId);
            var isUpcomingCraft = upcomingCraftIngredients.Contains(baseId);

            var uiCatId = itemRow.ItemUICategory.RowId;
            var isProtectedNoAutoMarket = ItemIdentity.ProtectedStoreNoAutoMarketCategoryIds.Contains(uiCatId);
            var isProtectedNoVendor = isProtectedNoAutoMarket
                || ItemIdentity.ProtectedNoVendorCategoryIds.Contains(uiCatId);

            // 10a. Realistic high-value tradable surplus -> Market (checked BEFORE NQ Vendor so mixed NQ+HQ
            //      with viable HQ lists the HQ stack instead of routing the whole item to Vendor!)
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
                // Zero out whichever quality failed viability so MarketListingPlanner / safety only lists the viable quality!
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
                    "Market",
                    0,
                    string.Empty,
                    effTargetNq,
                    effTargetHq,
                    priceLow,
                    untradable,
                    reason);
            }

            // 10b. Vendor Junk Rules (NEVER vendor 0-listing scarce items, ANY HQ items, upcoming crafts, or protected categories!)
            if (hasFreshPrice
                && !isUpcomingCraft
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
                    // Purge (Clear Space): sell trivially re-buyable city-vendor items (guarded by total re-buy cost too!)
                    // or abundantly available & cheap Market Board items (5+ listings, <= 200g ea)
                    var cityRebuyTotal = (long)itemRow.PriceMid * Math.Max(obs.TotalQty, 1);
                    if (isEasyCityShop
                        && itemRow.PriceMid > 0
                        && itemRow.PriceMid <= 500
                        && cityRebuyTotal < configuration.VendorMaxStackValue
                        && nqStackVal < configuration.VendorMaxStackValue)
                    {
                        return new MagicSortEntry(
                            baseId, name, "Vendor", 0, string.Empty, 0, 0, priceLow, untradable,
                            $"Sold by city vendor ({itemRow.PriceMid:N0}g — easy to re-buy)");
                    }

                    if (listingCount >= 5 && nqUnit > 0 && nqUnit <= 200 && nqStackVal < configuration.VendorMaxStackValue)
                    {
                        return new MagicSortEntry(
                            baseId, name, "Vendor", 0, string.Empty, 0, 0, priceLow, untradable,
                            $"Cheap & abundant on MB (~{nqUnit:N0}g, {listingCount} listed)");
                    }
                }

                // Dead-market junk check — uses NQ velocity & realistic price
                var isDeadMarketPrice = nqUnit > 0
                    && nqUnit <= Math.Max(priceLow * 2L, 30L)
                    && nqStackVal < configuration.VendorMaxStackValue
                    && nqVel < configuration.VendorMaxVelocity;

                if (isDeadMarketPrice)
                {
                    // Safe mode requires either easy city vendor OR active MB supply (3+ listings) with zero recipe use
                    var safeAllowed = isEasyCityShop || (listingCount >= 3 && !isAnyRecipeIngredient);
                    var balancedAllowed = listingCount > 0;

                    if ((sellMode == SellMode.Safe && safeAllowed)
                        || (sellMode != SellMode.Safe && balancedAllowed))
                    {
                        var reason = isEasyCityShop
                            ? $"City vendor item ({itemRow.PriceMid:N0}g, MB ~{nqUnit:N0}g)"
                            : $"Low MB value (~{nqUnit:N0}g, {nqVel:F1}/day)";
                        return new MagicSortEntry(baseId, name, "Vendor", 0, string.Empty, 0, 0, priceLow, untradable, reason);
                    }
                }
            }

            // Note scarce 0-listing items clearly when routing to retainer
            if (isZeroListings && !isEasyCityShop)
            {
                return RouteToBestRetainer(
                    baseId, name, uiCat, preferHq, stackSize, priceLow, untradable, retainers, profile,
                    overrideReasonPrefix: "Rare — 0 on Market Board");
            }

            // Explain clearly when an item had high listing price but failed liquidity/frequency checks
            if (primaryViability.IsDeadOrIlliquid
                && !isProtectedNoAutoMarket
                && (primaryViability.ServerCheapest >= configuration.MarketMinUnitPrice
                    || (long)primaryViability.ServerCheapest * Math.Max(obs.TotalQty, 1) >= configuration.MarketMinStackValue))
            {
                return RouteToBestRetainer(
                    baseId, name, uiCat, preferHq, stackSize, priceLow, untradable, retainers, profile,
                    overrideReasonPrefix: $"Stored ({primaryViability.Reason})");
            }
        }

        // 11. Blue rarity tradable/special equipment -> Keep
        if (isEquip && itemRow.Rarity == 3)
        {
            return KeepEntry(baseId, name, priceLow, untradable, "Blue raid / tome gear");
        }

        // 12. Obsolete normal NQ equipment -> Vendor
        //     NEVER auto-vendor Lv 1 / iLvl 1 glamour or unpriced/valuable tradable gear!
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
                    baseId, name, "Vendor", 0, string.Empty, 0, 0, priceLow, untradable,
                    $"Old normal gear (iLvl {itemRow.LevelItem.RowId}, {priceLow:N0}g)");
            }
        }

        // 13. Surplus storage -> Best matching retainer (0/1/N retainer & fullness aware)
        return RouteToBestRetainer(
            baseId, name, uiCat, preferHq, stackSize, priceLow, untradable, retainers, profile,
            overrideReasonPrefix: null);
    }

    private static MagicSortEntry KeepEntry(uint baseId, string name, long priceLow, bool untradable, string reason)
        => new(baseId, name, "Keep", 0, string.Empty, 0, 0, priceLow, untradable, reason);

    private MagicSortEntry RouteToBestRetainer(
        uint baseId,
        string name,
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
            return KeepEntry(baseId, name, priceLow, untradable, "Open a Summoning Bell once to find your retainers");
        }

        if (retainers.Count == 1)
        {
            var only = retainers[0];
            var reason = overrideReasonPrefix ?? $"Stored with {only.Name}";
            return new MagicSortEntry(baseId, name, "Retainer", only.RetainerId, only.Name, 0, 0, priceLow, untradable, reason);
        }

        // 1. Explicit user-configured specialty in Settings (case-insensitive)
        if (!string.IsNullOrEmpty(uiCat))
        {
            foreach (var ret in retainers)
            {
                if (profile.RetainerSpecialties.TryGetValue(ret.RetainerId, out var specs)
                    && specs != null
                    && specs.Contains(uiCat, StringComparer.OrdinalIgnoreCase))
                {
                    var label = overrideReasonPrefix ?? $"{uiCat} ({ret.Name})";
                    return new MagicSortEntry(baseId, name, "Retainer", ret.RetainerId, ret.Name, 0, 0, priceLow, untradable, label);
                }
            }
        }

        var hasUnvisitedRetainers = retainers.Any(r => !r.IsVisited);

        // Compute majority category owner upfront so a tiny misplaced partial stack on a full retainer
        // does not trap items away from an uncrowded majority-category owner.
        KnownRetainerInfo? bestCatOwner = null;
        var bestCatScore = 0;
        var secondBestCatScore = 0;
        if (!string.IsNullOrEmpty(uiCat))
        {
            foreach (var ret in retainers)
            {
                var score = ret.CategoryCounts.GetValueOrDefault(uiCat);
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
            && bestCatScore >= secondBestCatScore * 2;

        // 2. Existing partial stack merge wins when it costs 0 new slots (unless another retainer with space owns the clear majority category)
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
                && (!hasClearMajorityCat || mergeOwner.RetainerId == bestCatOwner!.RetainerId || bestCatOwner.UsedBagSlots >= HoldingsService.MaxRetainerBagSlots))
            {
                var reason = overrideReasonPrefix ?? $"Stacks with {mergeOwner.Name}";
                return new MagicSortEntry(baseId, name, "Retainer", mergeOwner.RetainerId, mergeOwner.Name, 0, 0, priceLow, untradable, reason);
            }
        }

        // 3. Clear majority specialty (>= 3 items AND >= 2x runner-up -> converges cleanly with zero ping-pong)
        if (hasClearMajorityCat && bestCatOwner != null)
        {
            var label = overrideReasonPrefix ?? $"{uiCat} ({bestCatOwner.Name})";
            return new MagicSortEntry(baseId, name, "Retainer", bestCatOwner.RetainerId, bestCatOwner.Name, 0, 0, priceLow, untradable, label);
        }

        // 4. Existing stack match (keeps split-category items stable)
        KnownRetainerInfo? stackOwner = null;
        var largestStack = 0;
        foreach (var ret in retainers)
        {
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
            return new MagicSortEntry(baseId, name, "Retainer", stackOwner.RetainerId, stackOwner.Name, 0, 0, priceLow, untradable, label);
        }

        // Cold-start guard: wait until all retainers have been visited once before guessing where new items go
        if (hasUnvisitedRetainers)
        {
            return KeepEntry(baseId, name, priceLow, untradable, "Visit each retainer once to map their bags");
        }

        // 5. Soft category match (>= 2 items) — factor in retainer fullness so nearly-full retainers aren't overloaded
        const int crowdedThreshold = 158; // 90% of 175
        var hasUncrowded = retainers.Any(r => r.UsedBagSlots < crowdedThreshold);
        if (bestCatOwner != null && bestCatScore >= 2 && (bestCatOwner.UsedBagSlots < crowdedThreshold || !hasUncrowded))
        {
            var label = overrideReasonPrefix ?? $"{uiCat} ({bestCatOwner.Name})";
            return new MagicSortEntry(baseId, name, "Retainer", bestCatOwner.RetainerId, bestCatOwner.Name, 0, 0, priceLow, untradable, label);
        }

        // 6. Fullness-aware default placement: prefer retainers with the most free space
        var candidates = hasUncrowded
            ? retainers.Where(r => r.UsedBagSlots < crowdedThreshold).ToList()
            : retainers.Where(r => r.UsedBagSlots < HoldingsService.MaxRetainerBagSlots).ToList();
        if (candidates.Count == 0)
        {
            candidates = retainers.ToList();
        }

        var chosen = candidates.OrderBy(r => r.UsedBagSlots).First();
        var defaultReason = overrideReasonPrefix ?? $"Stored with {chosen.Name}";
        return new MagicSortEntry(baseId, name, "Retainer", chosen.RetainerId, chosen.Name, 0, 0, priceLow, untradable, defaultReason);
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

    private static bool IsSafeAutoUseItem(Item itemRow, string name, long unlockStatus, out string reason)
    {
        reason = string.Empty;
        if (itemRow.ItemAction.RowId == 0
            || itemRow.EquipSlotCategory.RowId > 0
            || ItemIdentity.ProtectedConsumableCategoryIds.Contains(itemRow.ItemUICategory.RowId)
            || IsActiveUtilityConsumable(name))
        {
            return false;
        }

        if (unlockStatus == 2)
        {
            reason = "Unlockable";
            return true;
        }

        if (unlockStatus == 1)
        {
            return false;
        }

        // Only auto-open UNTRADABLE coffers (never crack open tradable million-gil glamour/venture coffers!)
        if (itemRow.IsUntradable && name.Contains(" Coffer", StringComparison.OrdinalIgnoreCase))
        {
            reason = "Coffer";
            return true;
        }

        if (name.Contains("MGP ", StringComparison.OrdinalIgnoreCase) && name.Contains("Card", StringComparison.OrdinalIgnoreCase))
        {
            reason = "MGP Card";
            return true;
        }

        return false;
    }

    private bool IsUnlockedCrafterGathererTool(Item itemRow, Dictionary<uint, int> unlockedJobTopIlvl)
    {
        if (itemRow.EquipSlotCategory.RowId is not (1 or 2 or 13 or 14))
        {
            return false;
        }

        var ps = PlayerState.Instance();
        var classJobSheet = dataManager.GetExcelSheet<ClassJob>();
        if (ps == null || classJobSheet == null)
        {
            return false;
        }

        var category = itemRow.ClassJobCategory.Value;
        var itemIlvl = (int)itemRow.LevelItem.RowId;
        var equipLevel = itemRow.LevelEquip;

        foreach (var jobId in CrafterGathererJobIds)
        {
            var job = classJobSheet.GetRowOrDefault(jobId);
            if (job is null || job.Value.ExpArrayIndex < 0 || job.Value.ExpArrayIndex >= ps->ClassJobLevels.Length)
            {
                continue;
            }

            var jobLevel = ps->ClassJobLevels[job.Value.ExpArrayIndex];
            if (jobLevel <= 0)
            {
                continue;
            }

            if (!InventorySafetyEvaluator.CanJobUseCategory(category, jobId))
            {
                continue;
            }

            var topIlvl = unlockedJobTopIlvl.GetValueOrDefault(jobId, 0);
            // Keep top/current tools and near-future tools (<= jobLevel + 4), not obsolete low-level spares
            if ((equipLevel > jobLevel && equipLevel <= jobLevel + 4) || topIlvl == 0 || itemIlvl >= topIlvl - 10)
            {
                return true;
            }
        }

        return false;
    }

    private bool IsUnlockedJobBisOrUpgrade(
        Item itemRow,
        Dictionary<uint, int> unlockedJobTopIlvl,
        HashSet<uint> gearsetJobs)
    {
        var ps = PlayerState.Instance();
        var classJobSheet = dataManager.GetExcelSheet<ClassJob>();
        if (ps == null || classJobSheet == null)
        {
            return false;
        }

        var currentJobId = (uint)ps->CurrentClassJobId;
        var category = itemRow.ClassJobCategory.Value;
        var itemIlvl = (int)itemRow.LevelItem.RowId;
        var equipLevel = itemRow.LevelEquip;

        foreach (var (jobId, topIlvl) in unlockedJobTopIlvl)
        {
            var job = classJobSheet.GetRowOrDefault(jobId);
            if (job is null || job.Value.ExpArrayIndex < 0 || job.Value.ExpArrayIndex >= ps->ClassJobLevels.Length)
            {
                continue;
            }

            var jobLevel = ps->ClassJobLevels[job.Value.ExpArrayIndex];
            if (jobLevel <= 0)
            {
                continue;
            }

            var isDoHOrDoL = CrafterGathererJobIds.Contains(jobId);
            var isActiveJob = jobId == currentJobId || gearsetJobs.Contains(jobId);
            var jobExp = job.Value.ExpArrayIndex < ps->ClassJobExperience.Length
                ? ps->ClassJobExperience[job.Value.ExpArrayIndex]
                : 0;

            var startLv = Math.Max(1, (int)job.Value.StartingLevel);
            if (!isActiveJob && !isDoHOrDoL && (jobLevel <= 1 || (jobExp == 0 && jobLevel <= startLv)))
            {
                continue;
            }

            if (!InventorySafetyEvaluator.CanJobUseCategory(category, jobId))
            {
                continue;
            }

            // Near-future leveling gear (up to +4 levels ahead of an active/leveled job)
            if (equipLevel > jobLevel && equipLevel <= jobLevel + 4)
            {
                return true;
            }

            // Current BiS / upgrade: must be usable now and within 5 iLvl of this job's best gear (and non-zero topIlvl)
            if (equipLevel <= jobLevel && topIlvl > 0 && itemIlvl >= topIlvl - 5)
            {
                return true;
            }
        }

        return false;
    }

    private static HashSet<uint> BuildSavedGearsetJobIds()
    {
        var set = new HashSet<uint>();
        var gm = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureGearsetModule.Instance();
        if (gm == null)
        {
            return set;
        }

        for (var i = 0; i < gm->Entries.Length; i++)
        {
            var entry = gm->GetGearset(i);
            if (entry != null && entry->Flags.HasFlag(FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureGearsetModule.GearsetFlag.Exists))
            {
                set.Add(entry->ClassJob);
            }
        }

        return set;
    }

    private bool IsConfirmedOutgearedByUnlockedJob(Item itemRow, Dictionary<uint, int> unlockedJobTopIlvl)
    {
        var ps = PlayerState.Instance();
        var classJobSheet = dataManager.GetExcelSheet<ClassJob>();
        if (ps == null || classJobSheet == null)
        {
            return false;
        }

        var category = itemRow.ClassJobCategory.Value;
        var itemIlvl = (int)itemRow.LevelItem.RowId;
        var equipLevel = itemRow.LevelEquip;

        foreach (var (jobId, topIlvl) in unlockedJobTopIlvl)
        {
            var job = classJobSheet.GetRowOrDefault(jobId);
            if (job is null || job.Value.ExpArrayIndex < 0 || job.Value.ExpArrayIndex >= ps->ClassJobLevels.Length)
            {
                continue;
            }

            var jobLevel = ps->ClassJobLevels[job.Value.ExpArrayIndex];
            if (jobLevel < equipLevel)
            {
                continue;
            }

            if (InventorySafetyEvaluator.CanJobUseCategory(category, jobId) && topIlvl > 0 && itemIlvl < topIlvl - 5)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsUnlockableOrCoffer(Item itemRow, string name, string uiCat)
    {
        if (ItemIdentity.IsUnlockableCategory(itemRow))
        {
            return true;
        }

        foreach (var token in CofferUnlockTokens)
        {
            if (name.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return itemRow.ItemAction.RowId > 0
            && itemRow.EquipSlotCategory.RowId == 0
            && !ItemIdentity.CraftingStapleCategoryIds.Contains(itemRow.ItemUICategory.RowId)
            && !ItemIdentity.ProtectedConsumableCategoryIds.Contains(itemRow.ItemUICategory.RowId);
    }

    private static bool IsActiveUtilityConsumable(string name)
    {
        foreach (var token in ActiveUtilityTokens)
        {
            if (name.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsQuestOrKeyItem(Item itemRow, string name, string uiCat)
    {
        foreach (var token in QuestKeyRelicTokens)
        {
            if (name.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return itemRow.IsUntradable
            && itemRow.PriceLow == 0
            && itemRow.EquipSlotCategory.RowId == 0
            && itemRow.ItemUICategory.RowId == ItemIdentity.UiCatOther
            && itemRow.ItemSearchCategory.RowId == 0;
    }

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
        // 1. Collect all global crafting ingredients so Safe mode never vendors useful craft mats
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

        // 2. Identify main-city & housing-ward NPCs (TerritoryIntendedUse: 0 = City, 13 = Housing Ward, 14 = Indoor Housing)
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
                if (useId is 0 or 13 or 14)
                {
                    easyCityNpcIds.Add(lvl.Object.RowId);
                }
            }
        }

        // 3. Map main-city / housing NPCs to their quest-free GilShops
        var easyCityShopIds = new HashSet<uint>();
        var gilShopSheet = dataManager.GetExcelSheet<GilShop>();
        var enpcSheet = dataManager.GetExcelSheet<ENpcBase>();
        if (gilShopSheet != null && enpcSheet != null)
        {
            foreach (var npcId in easyCityNpcIds)
            {
                var npc = enpcSheet.GetRowOrDefault(npcId);
                if (npc is null)
                {
                    continue;
                }

                foreach (var dataRef in npc.Value.ENpcData)
                {
                    var shopId = dataRef.RowId;
                    if (shopId == 0 || !gilShopSheet.TryGetRow(shopId, out var shop))
                    {
                        continue;
                    }

                    if (shop.Quest.RowId == 0)
                    {
                        easyCityShopIds.Add(shopId);
                    }
                }
            }
        }

        // 4. Populate easyCityVendorItemIds from SubrowExcelSheet<GilShopItem>
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
            if (recLevel < levels[ct] || recLevel > levels[ct] + 15)
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
