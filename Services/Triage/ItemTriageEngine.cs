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

    private static readonly HashSet<string> CulinaryBotanyDefaultCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Ingredient",
        "Seafood",
        "Meal",
        "Medicine",
        "Reagent",
        "Catalyst",
        "Gardening",
        "Fishing Tackle",
    };

    private static readonly HashSet<string> MaterialsDefaultCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Stone",
        "Metal",
        "Lumber",
        "Cloth",
        "Leather",
        "Bone",
        "Part",
        "Materia",
        "Dye",
        "Furnishing",
        "Demimateria",
        "Miscellany",
        "Other",
    };

    private static readonly HashSet<string> UnlockableCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Minion",
        "Orchestrion Roll",
        "Triple Triad Card",
        "Outfits",
    };

    private static readonly HashSet<string> HousingCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Furnishing",
        "Tabletop",
        "Wall-mounted",
        "Rug",
        "Outdoor Furnishing",
        "Interior Fixtures",
        "Exterior Fixtures",
        "Roof",
        "Exterior Wall",
        "Window",
        "Door",
        "Roof Decoration",
        "Exterior Wall Decoration",
        "Placard",
        "Fence",
        "Interior Wall",
        "Flooring",
        "Ceiling Light",
        "Painting",
    };

    private static readonly HashSet<string> ProtectedNoVendorCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Fishing Tackle",
        "Materia",
        "Dye",
        "Catalyst",
    };

    private static readonly HashSet<string> CraftingStapleCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Ingredient",
        "Reagent",
        "Seafood",
        "Meal",
    };

    private static readonly HashSet<uint> ProtectedConsumableCategoryIds = [44, 46]; // Medicine (Tinctures/Potions/Cordials), Meal (Food)
    private static readonly HashSet<uint> ProtectedStoreNoAutoMarketCategoryIds = [33, 55, 58, 60]; // Fishing Tackle, Dye, Materia, Catalyst
    private static readonly HashSet<uint> ProtectedUnlockableCategoryIds = [80, 81, 94]; // Triple Triad Card, Minion, Orchestrion Roll

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
        "Luminous ",
        "Proto Armor Identification Key",
        "Soul of the Crafter",
        "Jandelaine's Token",
        "Gold Saucer Ticket",
    ];

    private static readonly string[] CofferUnlockTokens =
    [
        " Coffer",
        " Whistle",
        " Horn",
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
    private readonly HashSet<uint> anyGilVendorItemIds = new();
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
    public bool IsCabinetItem(uint baseItemId) => cabinetRowByItemId.ContainsKey(HoldingsService.NormalizeBaseItemId(baseItemId));

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
            if (id <= 20)
            {
                continue;
            }

            var row = itemSheet.GetRowOrDefault(id);
            if (row is not null && !row.Value.IsUntradable)
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
        var normalized = HoldingsService.NormalizeBaseItemId(baseItemId);
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
        var totalQty = 0;
        var hasNq = false;
        var hasHq = false;
        var anyMelded = false;
        var anyGlamoured = false;
        var anyExtractableMateria = false;
        var equipCount = 0;
        var soulboundCount = 0;

        var itemRow = dataManager.GetExcelSheet<Item>()?.GetRowOrDefault(normalizedBaseId);
        var manager = InventoryManager.Instance();
        if (manager != null)
        {
            void Scan(InventoryType type, bool isEquipped)
            {
                var container = manager->GetInventoryContainer(type);
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

                    if (HoldingsService.NormalizeBaseItemId(slot->ItemId) != normalizedBaseId)
                    {
                        continue;
                    }

                    totalQty += (int)slot->Quantity;
                    var isHq = slot->ItemId >= 1_000_000 || slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
                    if (isHq)
                    {
                        hasHq = true;
                    }
                    else
                    {
                        hasNq = true;
                    }

                    if (slot->GetMateriaCount() > 0 || slot->Materia[0] != 0)
                    {
                        anyMelded = true;
                    }

                    if (slot->GlamourId != 0 || slot->GetStain(0) != 0 || slot->GetStain(1) != 0)
                    {
                        anyGlamoured = true;
                    }

                    if (itemRow is not null && InventorySafetyEvaluator.IsMateriaExtractable(itemRow.Value, slot))
                    {
                        anyExtractableMateria = true;
                    }

                    if (!isEquipped)
                    {
                        equipCount++;
                        if (slot->SpiritbondOrCollectability > 0)
                        {
                            soulboundCount++;
                        }
                    }
                }
            }

            Scan(InventoryType.EquippedItems, isEquipped: true);

            foreach (var bag in InventorySafetyEvaluator.PlayerBagContainers)
            {
                Scan(bag, isEquipped: false);
            }

            foreach (var arm in InventorySafetyEvaluator.ArmouryGearContainers)
            {
                Scan(arm, isEquipped: false);
            }

            Scan(InventoryType.ArmorySoulCrystal, isEquipped: false);
        }

        foreach (var ret in retainers)
        {
            if (ret.Quantities.TryGetValue(normalizedBaseId, out var retQty) && retQty > 0)
            {
                totalQty += retQty;
                hasNq = true;
            }
        }

        if (totalQty <= 0)
        {
            return new ItemObservation(
                TotalQty: 1,
                HasNq: true,
                HasHq: false,
                AnyMelded: false,
                AnyGlamoured: false,
                AllEquipSoulbound: false,
                AnyExtractableMateria: false);
        }

        return new ItemObservation(
            TotalQty: totalQty,
            HasNq: hasNq,
            HasHq: hasHq,
            AnyMelded: anyMelded,
            AnyGlamoured: anyGlamoured,
            AllEquipSoulbound: equipCount > 0 && soulboundCount == equipCount,
            AnyExtractableMateria: anyExtractableMateria);
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
        bool HasNq,
        bool HasHq,
        bool AnyMelded,
        bool AnyGlamoured,
        bool AllEquipSoulbound,
        bool AnyExtractableMateria);

    private Dictionary<uint, ItemObservation> BuildItemObservations(IReadOnlyList<KnownRetainerInfo> retainers)
    {
        var qtyById = new Dictionary<uint, int>();
        var hasNq = new HashSet<uint>();
        var hasHq = new HashSet<uint>();
        var anyMelded = new HashSet<uint>();
        var anyGlamoured = new HashSet<uint>();
        var anyExtractableMateria = new HashSet<uint>();
        var equipCount = new Dictionary<uint, int>();
        var soulboundCount = new Dictionary<uint, int>();

        var itemSheet = dataManager.GetExcelSheet<Item>();
        var manager = InventoryManager.Instance();
        if (manager != null)
        {
            Span<InventoryType> activeContainers =
            [
                InventoryType.Inventory1,
                InventoryType.Inventory2,
                InventoryType.Inventory3,
                InventoryType.Inventory4,
                InventoryType.EquippedItems,
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
                InventoryType.ArmorySoulCrystal,
            ];

            foreach (var cType in activeContainers)
            {
                var container = manager->GetInventoryContainer(cType);
                if (!InventorySafetyEvaluator.IsContainerReadable(container))
                {
                    continue;
                }

                for (var i = 0; i < container->Size; i++)
                {
                    var slot = InventorySafetyEvaluator.GetSlot(container, i);
                    if (slot == null || slot->ItemId == 0 || slot->Quantity <= 0)
                    {
                        continue;
                    }

                    var baseId = HoldingsService.NormalizeBaseItemId(slot->ItemId);
                    qtyById[baseId] = qtyById.GetValueOrDefault(baseId) + slot->Quantity;

                    var isHq = slot->ItemId >= 1_000_000 || slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
                    if (isHq)
                    {
                        hasHq.Add(baseId);
                    }
                    else
                    {
                        hasNq.Add(baseId);
                    }

                    if (slot->GetMateriaCount() > 0 || slot->Materia[0] != 0)
                    {
                        anyMelded.Add(baseId);
                    }

                    if (slot->GlamourId != 0 || slot->GetStain(0) != 0 || slot->GetStain(1) != 0)
                    {
                        anyGlamoured.Add(baseId);
                    }

                    if (slot->SpiritbondOrCollectability >= 10000 && itemSheet != null)
                    {
                        var sbRow = itemSheet.GetRowOrDefault(baseId);
                        if (sbRow is not null && InventorySafetyEvaluator.IsMateriaExtractable(sbRow.Value, slot))
                        {
                            anyExtractableMateria.Add(baseId);
                        }
                    }

                    if (cType != InventoryType.EquippedItems)
                    {
                        equipCount[baseId] = equipCount.GetValueOrDefault(baseId) + 1;
                        if (slot->SpiritbondOrCollectability > 0)
                        {
                            soulboundCount[baseId] = soulboundCount.GetValueOrDefault(baseId) + 1;
                        }
                    }
                }
            }
        }

        // Also include every item stored across the character's known retainers
        foreach (var ret in retainers)
        {
            foreach (var (itemId, qty) in ret.Quantities)
            {
                if (itemId == 0 || qty <= 0)
                {
                    continue;
                }

                qtyById[itemId] = qtyById.GetValueOrDefault(itemId) + qty;
                hasNq.Add(itemId);
            }
        }

        var result = new Dictionary<uint, ItemObservation>(qtyById.Count);
        foreach (var (id, totalQty) in qtyById)
        {
            var eCount = equipCount.GetValueOrDefault(id);
            var sbCount = soulboundCount.GetValueOrDefault(id);
            result[id] = new ItemObservation(
                totalQty,
                hasNq.Contains(id),
                hasHq.Contains(id),
                anyMelded.Contains(id),
                anyGlamoured.Contains(id),
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

        // Fetch cached market snapshot (if tradable)
        MarketPriceSnapshot? mkt = null;
        var hasFreshPrice = false;
        if (!untradable && baseId > 20 && !string.IsNullOrWhiteSpace(world))
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

        var targetNq = mkt != null && !untradable
            ? ComputeTargetPrice(mkt.MinPriceNq > 0 ? mkt.MinPriceNq : mkt.MinPriceAny, mkt.RecentHistoryMedian, priceLow)
            : 0L;
        // Fail-closed for HQ items: ONLY compute an HQ listing price when Universalis has a real HQ minimum price!
        var targetHq = mkt != null && !untradable && itemRow.CanBeHq && mkt.MinPriceHq > 0
            ? ComputeTargetPrice(mkt.MinPriceHq, mkt.RecentHistoryMedian, priceLow)
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
                var overrideLabel = overrideSpec switch
                {
                    "GrandCompany" => "GC Seals",
                    "Glamour" => "Glamour Dresser",
                    "GoldSaucer" => "Gold Saucer",
                    "Materia" => "Extract Materia",
                    _ => overrideSpec,
                };
                return new MagicSortEntry(
                    baseId,
                    name,
                    overrideSpec,
                    0,
                    string.Empty,
                    string.Equals(overrideSpec, "Market", StringComparison.OrdinalIgnoreCase) ? targetNq : 0,
                    string.Equals(overrideSpec, "Market", StringComparison.OrdinalIgnoreCase) ? targetHq : 0,
                    priceLow,
                    untradable,
                    $"Override: {overrideLabel}");
            }
        }

        // 1. Currencies & Elemental Crystals
        if (baseId <= 19 || string.Equals(uiCat, "Currency", StringComparison.OrdinalIgnoreCase))
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

        if (itemRow.EquipSlotCategory.RowId == 17 || itemRow.ItemUICategory.RowId == 62)
        {
            return KeepEntry(baseId, name, priceLow, untradable, "Job Stone");
        }

        if (itemRow.Rarity >= 4)
        {
            return KeepEntry(baseId, name, priceLow, untradable, "Relic gear");
        }

        // 3. Armoire-eligible gear not yet stored in Armoire (checked BEFORE upgrade rules)
        if (isCabinet && !alreadyInArmoire)
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

        if (isEquip && obs.AnyMelded)
        {
            return KeepEntry(baseId, name, priceLow, untradable, "Melded gear");
        }

        if (isEquip && obs.AnyGlamoured)
        {
            return KeepEntry(baseId, name, priceLow, untradable, "Glamoured or dyed gear");
        }

        if (isEquip && IsUnlockedCrafterGathererTool(itemRow, unlockedJobTopIlvl))
        {
            return KeepEntry(baseId, name, priceLow, untradable, "Crafter / Gatherer tool");
        }

        if (isEquip && IsUnlockedJobBisOrUpgrade(itemRow, unlockedJobTopIlvl))
        {
            return KeepEntry(baseId, name, priceLow, untradable, "Gear upgrade / leveling gear");
        }

        if (isEquip && priceLow == 0 && !isCabinet)
        {
            return KeepEntry(baseId, name, priceLow, untradable, "Special / starter gear");
        }

        // 5. Unlockables, Coffers, Food/Potions/Tinctures, Utility Consumables, Quest/Relic/Key Items
        if (ProtectedConsumableCategoryIds.Contains(itemRow.ItemUICategory.RowId))
        {
            return KeepEntry(baseId, name, priceLow, untradable, string.IsNullOrEmpty(uiCat) ? "Food / Potion" : uiCat);
        }

        if (IsSafeAutoUseItem(baseId, itemRow, name, uiCat, out var useReason))
        {
            return new MagicSortEntry(baseId, name, "Use", 0, string.Empty, 0, 0, priceLow, untradable, useReason);
        }

        if (IsUnlockableOrCoffer(itemRow, name, uiCat))
        {
            if (string.Equals(uiCat, "Triple Triad Card", StringComparison.OrdinalIgnoreCase))
            {
                return new MagicSortEntry(
                    baseId, name, "GoldSaucer", 0, string.Empty, 0, 0, priceLow, untradable,
                    "Duplicate card — trade for MGP at Gold Saucer");
            }

            return KeepEntry(baseId, name, priceLow, untradable, string.IsNullOrEmpty(uiCat) ? "Unlockable" : uiCat);
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

        // 6. Protected Housing Furnishings (FilterGroup 14 or Housing UI Category) -> Retainer storage ONLY
        if (itemRow.FilterGroup == 14 || HousingCategories.Contains(uiCat))
        {
            return RouteToBestRetainer(
                baseId, name, uiCat, isEquip, preferHq, stackSize, priceLow, untradable, retainers, profile,
                preferMaterialsRetainer: true,
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

        // 9. Obsolete untradable normal gear
        if (untradable && priceLow > 0 && isEquip && itemRow.Rarity == 1)
        {
            return new MagicSortEntry(
                baseId, name, "Vendor", 0, string.Empty, 0, 0, priceLow, untradable,
                $"Old normal gear (iLvl {itemRow.LevelItem.RowId}, {priceLow:N0}g)");
        }

        // 10. Tradable Market vs Vendor vs Retainer — FAIL-SAFE PendingPrice -> Keep!
        if (!untradable)
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

            var unitPrice = ComputeRealisticUnitPrice(mkt, preferHq);
            var velocity = mkt.RegularSaleVelocity;
            var stackVal = (long)unitPrice * Math.Max(obs.TotalQty, 1);
            var totalRetainerQty = retainers.Sum(r => r.Quantities.GetValueOrDefault(baseId));
            var listingCount = mkt.ListingCount; // -1 = legacy cache (unknown), 0 = scarce/out of stock, >0 = active listings
            var isZeroListings = listingCount == 0;
            var isEasyCityShop = easyCityVendorItemIds.Contains(baseId);
            var isAnyRecipeIngredient = globalRecipeIngredientIds.Contains(baseId);
            var isUpcomingCraft = upcomingCraftIngredients.Contains(baseId);

            var isProtectedStoreCat = ProtectedStoreNoAutoMarketCategoryIds.Contains(itemRow.ItemUICategory.RowId)
                || ProtectedNoVendorCategories.Contains(uiCat);

            // 10a. Vendor Junk Rules (NEVER vendor 0-listing scarce items, HQ gear, upcoming crafts, or protected categories!)
            if (hasFreshPrice
                && !isUpcomingCraft
                && !isZeroListings
                && priceLow > 0
                && !(isEquip && obs.HasHq)
                && !isProtectedStoreCat)
            {
                if (sellMode == SellMode.Purge)
                {
                    // Purge (Clear Space): sell anything trivially re-buyable at a main city/housing vendor (<= 500g)
                    // or abundantly available & cheap on the Market Board (5+ listings, <= 200g ea)
                    if (isEasyCityShop && itemRow.PriceMid > 0 && itemRow.PriceMid <= 500 && stackVal < configuration.VendorMaxStackValue)
                    {
                        return new MagicSortEntry(
                            baseId, name, "Vendor", 0, string.Empty, 0, 0, priceLow, untradable,
                            $"Sold by city vendor ({itemRow.PriceMid:N0}g — easy to re-buy)");
                    }

                    if (listingCount >= 5 && unitPrice > 0 && unitPrice <= 200 && stackVal < configuration.VendorMaxStackValue)
                    {
                        return new MagicSortEntry(
                            baseId, name, "Vendor", 0, string.Empty, 0, 0, priceLow, untradable,
                            $"Cheap & abundant on MB (~{unitPrice:N0}g, {listingCount} listed)");
                    }
                }

                // Dead-market junk check
                var isDeadMarketPrice = unitPrice > 0
                    && unitPrice <= Math.Max(priceLow * 2L, 30L)
                    && stackVal < configuration.VendorMaxStackValue
                    && velocity < configuration.VendorMaxVelocity;

                if (isDeadMarketPrice && totalRetainerQty == 0)
                {
                    // Safe mode requires either easy city vendor OR active MB supply (3+ listings) with zero recipe use
                    var safeAllowed = isEasyCityShop || (listingCount >= 3 && !isAnyRecipeIngredient);
                    var balancedAllowed = listingCount != 0;

                    if ((sellMode == SellMode.Safe && safeAllowed)
                        || (sellMode != SellMode.Safe && balancedAllowed))
                    {
                        var reason = isEasyCityShop
                            ? $"City vendor item ({itemRow.PriceMid:N0}g, MB ~{unitPrice:N0}g)"
                            : $"Low MB value (~{unitPrice:N0}g, {velocity:F1}/day)";
                        return new MagicSortEntry(baseId, name, "Vendor", 0, string.Empty, 0, 0, priceLow, untradable, reason);
                    }
                }
            }

            // 10b. High-value tradable surplus -> Market
            //      Never auto-list Materia, Dyes, Catalysts, or Fishing Tackle (store on retainer unless manually overridden).
            var isCraftingStaple = sellMode != SellMode.Purge
                && (totalRetainerQty > 0 || isUpcomingCraft)
                && CraftingStapleCategories.Contains(uiCat)
                && unitPrice < 1500;

            var purgeCraftAllowed = sellMode != SellMode.Purge || !isUpcomingCraft;

            if (!obs.AllEquipSoulbound
                && !isProtectedStoreCat
                && !isCraftingStaple
                && purgeCraftAllowed
                && unitPrice >= configuration.MarketMinFloorUnitPrice
                && (stackVal >= configuration.MarketMinStackValue || unitPrice >= configuration.MarketMinUnitPrice)
                && velocity >= configuration.MarketMinVelocity)
            {
                return new MagicSortEntry(
                    baseId,
                    name,
                    "Market",
                    0,
                    string.Empty,
                    targetNq,
                    targetHq,
                    priceLow,
                    untradable,
                    $"Worth selling (~{unitPrice:N0}g ea, ~{stackVal:N0}g total)");
            }

            // Note scarce 0-listing items clearly when routing to retainer
            if (isZeroListings && !isEasyCityShop)
            {
                return RouteToBestRetainer(
                    baseId, name, uiCat, isEquip, preferHq, stackSize, priceLow, untradable, retainers, profile,
                    preferMaterialsRetainer: false,
                    overrideReasonPrefix: "Rare — 0 on Market Board");
            }
        }

        // 11. Blue rarity tradable/special equipment -> Keep
        if (isEquip && itemRow.Rarity == 3)
        {
            return KeepEntry(baseId, name, priceLow, untradable, "Blue raid / tome gear");
        }

        // 12. Obsolete normal NQ equipment -> Vendor
        if (isEquip && itemRow.Rarity == 1 && priceLow > 0 && !obs.HasHq)
        {
            return new MagicSortEntry(
                baseId, name, "Vendor", 0, string.Empty, 0, 0, priceLow, untradable,
                $"Old normal gear (iLvl {itemRow.LevelItem.RowId}, {priceLow:N0}g)");
        }

        // 13. Surplus storage -> Best matching retainer (0/1/N retainer & fullness aware)
        return RouteToBestRetainer(
            baseId, name, uiCat, isEquip, preferHq, stackSize, priceLow, untradable, retainers, profile,
            preferMaterialsRetainer: false,
            overrideReasonPrefix: null);
    }

    private static MagicSortEntry KeepEntry(uint baseId, string name, long priceLow, bool untradable, string reason)
        => new(baseId, name, "Keep", 0, string.Empty, 0, 0, priceLow, untradable, reason);

    private MagicSortEntry RouteToBestRetainer(
        uint baseId,
        string name,
        string uiCat,
        bool isEquip,
        bool isHq,
        int stackSize,
        long priceLow,
        bool untradable,
        IReadOnlyList<KnownRetainerInfo> retainers,
        CharacterProfile profile,
        bool preferMaterialsRetainer,
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

        // 1. Explicit user-configured specialty in Settings (keeps intended retainer even if full; Sort overflows automatically)
        if (!string.IsNullOrEmpty(uiCat))
        {
            foreach (var ret in retainers)
            {
                if (profile.RetainerSpecialties.TryGetValue(ret.RetainerId, out var specs)
                    && specs.Contains(uiCat))
                {
                    var label = overrideReasonPrefix ?? $"{uiCat} ({ret.Name})";
                    return new MagicSortEntry(baseId, name, "Retainer", ret.RetainerId, ret.Name, 0, 0, priceLow, untradable, label);
                }
            }
        }

        var hasUnvisitedRetainers = retainers.Any(r => !r.IsVisited);

        // 2. Existing partial stack merge ALWAYS wins even on a 175/175 retainer (costs 0 new slots!)
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

            if (mergeOwner != null)
            {
                var reason = overrideReasonPrefix ?? $"Stacks with {mergeOwner.Name}";
                return new MagicSortEntry(baseId, name, "Retainer", mergeOwner.RetainerId, mergeOwner.Name, 0, 0, priceLow, untradable, reason);
            }
        }

        // 3. Clear majority specialty (>= 3 items AND >= 2x runner-up -> converges cleanly with zero ping-pong)
        //    Keeps true intended retainer even if currently full so items naturally return home once space opens!
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

            if (!hasUnvisitedRetainers && bestCatOwner != null && bestCatScore >= 3 && bestCatScore >= secondBestCatScore * 2)
            {
                var label = overrideReasonPrefix ?? $"{uiCat} ({bestCatOwner.Name})";
                return new MagicSortEntry(baseId, name, "Retainer", bestCatOwner.RetainerId, bestCatOwner.Name, 0, 0, priceLow, untradable, label);
            }
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

    private static bool IsSafeAutoUseItem(uint baseId, Item itemRow, string name, string uiCat, out string reason)
    {
        reason = string.Empty;
        if (itemRow.ItemAction.RowId == 0
            || itemRow.EquipSlotCategory.RowId > 0
            || ProtectedConsumableCategoryIds.Contains(itemRow.ItemUICategory.RowId)
            || IsActiveUtilityConsumable(name))
        {
            return false;
        }

        var uiState = UIState.Instance();
        if (uiState != null)
        {
            var exdRow = ExdModule.GetItemRowById(baseId);
            if (exdRow != null)
            {
                var unlockStatus = uiState->IsItemActionUnlocked(exdRow);
                if (unlockStatus == 2)
                {
                    reason = "Unlockable";
                    return true;
                }

                if (unlockStatus == 1)
                {
                    return false;
                }
            }
        }

        if (name.Contains(" Coffer", StringComparison.OrdinalIgnoreCase))
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

    private long ComputeTargetPrice(int rawMin, int histMedian, long priceLow)
    {
        if (rawMin <= 0 && histMedian <= 0)
        {
            return 0;
        }

        var floorPrice = Math.Max(
            Math.Max(priceLow * 2L, histMedian > 0 ? (long)(histMedian * 0.55) : 0L),
            Math.Max(2L, configuration.MarketMinFloorUnitPrice));

        long effective = rawMin > 0 ? rawMin : histMedian;
        if (rawMin > 0 && histMedian > 0 && rawMin > histMedian * 4L)
        {
            effective = (long)(histMedian * 1.5);
        }

        var undercutStep = Math.Max(0, configuration.MarketUndercutGil);
        var undercut = effective > floorPrice + undercutStep ? effective - undercutStep : floorPrice;
        return Math.Max(undercut, floorPrice);
    }

    private static int ComputeRealisticUnitPrice(MarketPriceSnapshot mkt, bool preferHq)
    {
        var raw = preferHq && mkt.MinPriceHq > 0
            ? mkt.MinPriceHq
            : (mkt.MinPriceNq > 0 ? mkt.MinPriceNq : mkt.MinPriceAny);
        var med = mkt.RecentHistoryMedian;
        if (raw > 0 && med > 0)
        {
            return raw > med * 4 ? (int)(med * 1.5) : raw;
        }

        return raw > 0 ? raw : med;
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

            var abbr = job.Value.Abbreviation.ExtractText();
            if (!InventorySafetyEvaluator.CanJobUseCategory(category, abbr))
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

    private bool IsUnlockedJobBisOrUpgrade(Item itemRow, Dictionary<uint, int> unlockedJobTopIlvl)
    {
        var ps = PlayerState.Instance();
        var classJobSheet = dataManager.GetExcelSheet<ClassJob>();
        if (ps == null || classJobSheet == null)
        {
            return false;
        }

        var gearsetJobs = BuildSavedGearsetJobIds();
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

            // Ignore untouched Lv 1 unlocks (e.g. ROG Lv 1 with 0 XP and no gearset) and untouched 0-XP expansion unlocks
            if (!isActiveJob && !isDoHOrDoL && (jobLevel <= 1 || (jobExp == 0 && jobLevel is 30 or 50 or 60 or 70 or 80)))
            {
                continue;
            }

            var abbr = job.Value.Abbreviation.ExtractText();
            if (!InventorySafetyEvaluator.CanJobUseCategory(category, abbr))
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

        for (var i = 0; i < 100; i++)
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

            var abbr = job.Value.Abbreviation.ExtractText();
            if (InventorySafetyEvaluator.CanJobUseCategory(category, abbr) && itemIlvl < topIlvl - 5)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsUnlockableOrCoffer(Item itemRow, string name, string uiCat)
    {
        if (ProtectedUnlockableCategoryIds.Contains(itemRow.ItemUICategory.RowId) || UnlockableCategories.Contains(uiCat))
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
            && !CraftingStapleCategories.Contains(uiCat)
            && !string.Equals(uiCat, "Medicine", StringComparison.OrdinalIgnoreCase);
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
            && string.Equals(uiCat, "Other", StringComparison.OrdinalIgnoreCase)
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

        // 4. Populate easyCityVendorItemIds & anyGilVendorItemIds from SubrowExcelSheet<GilShopItem>
        var gilShopItems = dataManager.GetSubrowExcelSheet<GilShopItem>();
        if (gilShopItems != null)
        {
            foreach (var shopGroup in gilShopItems)
            {
                var isEasyShop = easyCityShopIds.Contains(shopGroup.RowId);
                foreach (var entry in shopGroup)
                {
                    var itemId = entry.Item.RowId;
                    if (itemId == 0)
                    {
                        continue;
                    }

                    anyGilVendorItemIds.Add(itemId);
                    if (!isEasyShop)
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
        var sigChars = new char[8];
        for (var ct = 0; ct < 8; ct++)
        {
            var job = classJobSheet.GetRowOrDefault(CraftTypeToClassJobId[ct]);
            if (job is not null && job.Value.ExpArrayIndex >= 0 && job.Value.ExpArrayIndex < ps->ClassJobLevels.Length)
            {
                levels[ct] = ps->ClassJobLevels[job.Value.ExpArrayIndex];
            }

            sigChars[ct] = (char)Math.Clamp(levels[ct] + 32, 32, 126);
        }

        var sig = new string(sigChars);
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

        foreach (var cType in InventorySafetyEvaluator.PlayerBagContainers.Concat(InventorySafetyEvaluator.ArmouryGearContainers))
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
                    ids.Add(HoldingsService.NormalizeBaseItemId(slot->ItemId));
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
                ids.Add(HoldingsService.NormalizeBaseItemId(slot->ItemId));
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
