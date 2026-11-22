using System.Globalization;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using MagicInventory.Core;

namespace MagicInventory.Services;

public sealed record RetainerHolding(ulong RetainerId, string RetainerName, int Quantity);

public sealed record ItemHoldingSummary(
    uint ItemId,
    int BagCount,
    IReadOnlyList<RetainerHolding> Retainers,
    int SaddlebagCount = 0)
{
    public int TotalRetainerCount => Retainers.Sum(r => r.Quantity);

    public int TotalExternalCount => TotalRetainerCount + SaddlebagCount;
}

public sealed record KnownRetainerInfo(
    ulong RetainerId,
    string Name,
    int UsedBagSlots,
    int MarketItemCount,
    bool IsVisited,
    IReadOnlyDictionary<uint, int> Quantities,
    IReadOnlyDictionary<uint, int> CategoryCounts,
    IReadOnlyDictionary<uint, int>? HqQuantities = null);

public sealed unsafe class HoldingsService : IDisposable
{
    public const int MaxRetainerBagSlots = 175;

    private static readonly InventoryType[] SaddlebagContainers =
    [
        InventoryType.SaddleBag1,
        InventoryType.SaddleBag2,
        InventoryType.PremiumSaddleBag1,
        InventoryType.PremiumSaddleBag2,
    ];

    private sealed record LiveRetainerSnapshot(
        ulong CharacterContentId,
        DateTime CapturedUtc,
        string Name,
        int UsedBagSlots,
        HashSet<(uint BaseItemId, bool IsHq)> PartialStacks,
        Dictionary<uint, int> Quantities,
        Dictionary<uint, int> CategoryCounts,
        Dictionary<uint, int> HqQuantities);

    private readonly string nativeCachePath;
    private readonly string inventoryToolsJsonPath;
    private readonly string inventoriesCsvPath;
    private readonly object nativeCacheSaveGate = new();
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;

    private long saveGeneration;
    private volatile bool nativeCacheDirty;
    private DateTime lastExternalPollUtc = DateTime.MinValue;
    private DateTime lastCsvWriteUtc = DateTime.MinValue;
    private DateTime lastJsonWriteUtc = DateTime.MinValue;
    private DateTime lastLiveCaptureUtc = DateTime.MinValue;

    private Dictionary<ulong, string> ownerNames = new();
    private Dictionary<uint, List<RetainerHolding>> cachedExternalHoldings = new();
    private Dictionary<ulong, Dictionary<uint, int>> cachedCsvQuantitiesByOwner = new();
    private Dictionary<ulong, Dictionary<uint, int>> cachedCsvCategoryIdsByOwner = new();
    private Dictionary<(ulong CharacterId, uint ItemId), int> cachedSaddlebagByChar = new();
    private Dictionary<ulong, Dictionary<uint, int>> cachedRetainerHqQuantities = new();
    private Dictionary<ulong, int> cachedRetainerUsedSlots = new();
    private Dictionary<ulong, HashSet<(uint BaseItemId, bool IsHq)>> cachedRetainerPartialStacks = new();
    private readonly Dictionary<ulong, LiveRetainerSnapshot> liveRetainerSnapshots = new();

    private static readonly IReadOnlyDictionary<uint, int> EmptyCategoryMap = new Dictionary<uint, int>(0);
    private static readonly IReadOnlyDictionary<uint, int> EmptyQuantityMap = new Dictionary<uint, int>(0);

    public HoldingsService(IDalamudPluginInterface pluginInterface, IDataManager dataManager, IPluginLog log)
    {
        this.dataManager = dataManager;
        this.log = log;
        nativeCachePath = Path.Combine(pluginInterface.ConfigDirectory.FullName, "retainer_cache.json");
        var configRoot = pluginInterface.ConfigDirectory.Parent?.FullName ?? string.Empty;
        inventoryToolsJsonPath = Path.Combine(configRoot, "InventoryTools.json");
        inventoriesCsvPath = Path.Combine(configRoot, "InventoryTools", "inventories.csv");

        LoadNativeCache();
    }

    public void Dispose()
    {
        if (nativeCacheDirty)
        {
            SaveNativeCache(synchronous: true);
        }
    }

    public event System.Action? RetainerSnapshotUpdated;

    public void CaptureActiveRetainerIfOpen(bool isRetainerAddonOpen, bool force = false)
    {
        if (!isRetainerAddonOpen)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (!force && (now - lastLiveCaptureUtc).TotalMilliseconds < 250)
        {
            return;
        }

        var rm = RetainerManager.Instance();
        if (rm == null || !rm->IsReady)
        {
            return;
        }

        var active = rm->GetActiveRetainer();
        if (active == null || active->RetainerId == 0)
        {
            return;
        }

        var manager = InventoryManager.Instance();
        if (manager == null)
        {
            return;
        }

        foreach (var containerType in InventorySafetyEvaluator.RetainerBagContainers)
        {
            var c = manager->GetInventoryContainer(containerType);
            if (c == null || c->Items == null || c->Size <= 0 || !c->IsLoaded)
            {
                return;
            }
        }

        lastLiveCaptureUtc = now;

        var retainerId = active->RetainerId;
        var rawName = active->NameString;
        var retainerName = !string.IsNullOrWhiteSpace(rawName)
            ? rawName
            : ownerNames.GetValueOrDefault(retainerId, $"Retainer {retainerId % 10000}");

        var itemSheet = dataManager.GetExcelSheet<Item>();
        var usedBagSlots = 0;
        var partialStacks = new HashSet<(uint BaseItemId, bool IsHq)>();
        var counts = new Dictionary<uint, int>();
        var hqCounts = new Dictionary<uint, int>();
        var categoryCounts = new Dictionary<uint, int>();
        foreach (var containerType in InventorySafetyEvaluator.RetainerBagContainersWithCrystals)
        {
            var container = manager->GetInventoryContainer(containerType);
            if (container == null || container->Items == null || container->Size <= 0 || !container->IsLoaded)
            {
                continue;
            }

            var isRegularBagPage = containerType != InventoryType.RetainerCrystals;
            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->Items + i;
                if (slot == null || slot->Quantity <= 0 || slot->ItemId == 0)
                {
                    continue;
                }

                var normalized = ItemIdentity.NormalizeBaseItemId(slot->ItemId);
                if (normalized == 0)
                {
                    continue;
                }

                counts[normalized] = counts.GetValueOrDefault(normalized) + slot->Quantity;
                var isHq = ItemIdentity.IsHighQuality(slot);
                if (isHq)
                {
                    hqCounts[normalized] = hqCounts.GetValueOrDefault(normalized) + slot->Quantity;
                }

                if (isRegularBagPage)
                {
                    usedBagSlots++;
                    var row = itemSheet?.GetRowOrDefault(normalized);
                    if (row is not null)
                    {
                        var uiCatId = row.Value.ItemUICategory.RowId;
                        if (uiCatId != 0)
                        {
                            categoryCounts[uiCatId] = categoryCounts.GetValueOrDefault(uiCatId) + 1;
                        }
                    }

                    var stackSize = row is not null ? (int)Math.Max(1, row.Value.StackSize) : 1;
                    if (stackSize > 1
                        && !ItemIdentity.IsCollectable(slot)
                        && slot->Quantity < stackSize)
                    {
                        partialStacks.Add((normalized, isHq));
                    }
                }
            }
        }

        var contentId = InventorySafetyEvaluator.GetCurrentContentId();
        var changed = !liveRetainerSnapshots.TryGetValue(retainerId, out var prev)
            || prev.UsedBagSlots != usedBagSlots
            || prev.Name != retainerName
            || !DictionariesEqual(prev.Quantities, counts)
            || !DictionariesEqual(prev.HqQuantities, hqCounts)
            || !prev.PartialStacks.SetEquals(partialStacks);

        liveRetainerSnapshots[retainerId] = new LiveRetainerSnapshot(
            contentId,
            now,
            retainerName,
            usedBagSlots,
            partialStacks,
            counts,
            categoryCounts,
            hqCounts);

        if (changed)
        {
            SaveNativeCache();
            RetainerSnapshotUpdated?.Invoke();
        }
    }

    private static bool DictionariesEqual(Dictionary<uint, int> a, Dictionary<uint, int> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        foreach (var (k, v) in a)
        {
            if (!b.TryGetValue(k, out var bv) || bv != v)
            {
                return false;
            }
        }

        return true;
    }

    public IReadOnlyList<KnownRetainerInfo> GetKnownRetainersForCurrentCharacter()
    {
        RefreshRetainerCacheIfChanged();
        var cid = InventorySafetyEvaluator.GetCurrentContentId();
        var results = new List<KnownRetainerInfo>(10);
        var seen = new HashSet<ulong>();

        var rm = RetainerManager.Instance();
        var rmReady = rm != null && rm->IsReady;
        if (rmReady)
        {
            var count = rm->GetRetainerCount();
            for (uint i = 0; i < count; i++)
            {
                var ret = rm->GetRetainerBySortedIndex(i);
                if (ret == null || ret->RetainerId == 0)
                {
                    continue;
                }

                seen.Add(ret->RetainerId);
                if (!ret->Available)
                {
                    continue;
                }

                var name = string.IsNullOrWhiteSpace(ret->NameString)
                    ? ownerNames.GetValueOrDefault(ret->RetainerId, $"Retainer {i + 1}")
                    : ret->NameString;

                if (liveRetainerSnapshots.TryGetValue(ret->RetainerId, out var snap))
                {
                    results.Add(new KnownRetainerInfo(
                        ret->RetainerId,
                        name,
                        snap.UsedBagSlots,
                        ret->MarketItemCount,
                        IsVisited: true,
                        snap.Quantities,
                        snap.CategoryCounts,
                        snap.HqQuantities));
                }
                else
                {
                    var csvCounts = cachedCsvQuantitiesByOwner.TryGetValue(ret->RetainerId, out var qMap) ? qMap : EmptyQuantityMap;
                    var csvCategories = cachedCsvCategoryIdsByOwner.TryGetValue(ret->RetainerId, out var cMap) ? cMap : EmptyCategoryMap;
                    var hasCsv = csvCounts.Count > 0 || cachedRetainerUsedSlots.ContainsKey(ret->RetainerId);
                    var used = cachedRetainerUsedSlots.GetValueOrDefault(ret->RetainerId, 0);
                    cachedRetainerHqQuantities.TryGetValue(ret->RetainerId, out var csvHq);
                    results.Add(new KnownRetainerInfo(
                        ret->RetainerId,
                        name,
                        used,
                        ret->MarketItemCount,
                        IsVisited: hasCsv,
                        csvCounts,
                        csvCategories,
                        csvHq));
                }
            }
        }

        if (!rmReady)
        {
            foreach (var (retId, snap) in liveRetainerSnapshots)
            {
                if (seen.Contains(retId))
                {
                    continue;
                }

                if (cid != 0 && snap.CharacterContentId != 0 && snap.CharacterContentId != cid)
                {
                    continue;
                }

                seen.Add(retId);
                results.Add(new KnownRetainerInfo(
                    retId,
                    snap.Name,
                    snap.UsedBagSlots,
                    0,
                    IsVisited: true,
                    snap.Quantities,
                    snap.CategoryCounts,
                    snap.HqQuantities));
            }
        }

        return results;
    }

    public HashSet<uint> GetAllRetainerOwnedItemIdsForCurrentCharacter()
    {
        var ids = new HashSet<uint>();
        foreach (var ret in GetKnownRetainersForCurrentCharacter())
        {
            foreach (var itemId in ret.Quantities.Keys)
            {
                if (itemId > 0)
                {
                    ids.Add(itemId);
                }
            }
        }

        return ids;
    }

    public bool IsRetainerKnownFullFor(ulong retainerId, uint baseItemId, bool isHq, int stackSize)
    {
        if (retainerId == 0)
        {
            return false;
        }

        RefreshRetainerCacheIfChanged();
        var normalized = ItemIdentity.NormalizeBaseItemId(baseItemId);

        if (liveRetainerSnapshots.TryGetValue(retainerId, out var snap))
        {
            if (snap.UsedBagSlots < MaxRetainerBagSlots)
            {
                return false;
            }

            return stackSize <= 1 || !snap.PartialStacks.Contains((normalized, isHq));
        }

        if (cachedRetainerUsedSlots.TryGetValue(retainerId, out var csvUsed))
        {
            if (csvUsed < MaxRetainerBagSlots)
            {
                return false;
            }

            if (stackSize > 1
                && cachedRetainerPartialStacks.TryGetValue(retainerId, out var csvPartials)
                && csvPartials.Contains((normalized, isHq)))
            {
                return false;
            }

            return true;
        }

        return false;
    }

    public bool IsRetainerVisited(ulong retainerId)
    {
        if (retainerId == 0)
        {
            return false;
        }

        RefreshRetainerCacheIfChanged();
        return liveRetainerSnapshots.ContainsKey(retainerId)
            || cachedRetainerUsedSlots.ContainsKey(retainerId);
    }

    public int GetKnownRetainerFreeSlots(ulong retainerId)
    {
        if (retainerId == 0)
        {
            return 0;
        }

        RefreshRetainerCacheIfChanged();
        if (liveRetainerSnapshots.TryGetValue(retainerId, out var snap))
        {
            return Math.Max(0, MaxRetainerBagSlots - snap.UsedBagSlots);
        }

        if (cachedRetainerUsedSlots.TryGetValue(retainerId, out var csvUsed))
        {
            return Math.Max(0, MaxRetainerBagSlots - csvUsed);
        }

        return MaxRetainerBagSlots;
    }

    public bool HasPartialStackOnRetainer(ulong retainerId, uint baseItemId, bool isHq)
    {
        if (retainerId == 0)
        {
            return false;
        }

        RefreshRetainerCacheIfChanged();
        var normalized = ItemIdentity.NormalizeBaseItemId(baseItemId);

        if (liveRetainerSnapshots.TryGetValue(retainerId, out var snap))
        {
            return snap.PartialStacks.Contains((normalized, isHq));
        }

        return cachedRetainerPartialStacks.TryGetValue(retainerId, out var csvPartials)
            && csvPartials.Contains((normalized, isHq));
    }

    public ItemHoldingSummary GetHoldingSummary(uint baseItemId)
    {
        var normalized = ItemIdentity.NormalizeBaseItemId(baseItemId);
        RefreshRetainerCacheIfChanged();

        var bagCount = GetLiveBagCount(normalized);
        var currentCid = InventorySafetyEvaluator.GetCurrentContentId();

        var currentCharRetainerIds = new HashSet<ulong>();
        var unavailableRetainerIds = new HashSet<ulong>();
        var rm = RetainerManager.Instance();
        var rmReady = rm != null && rm->IsReady;
        if (rmReady)
        {
            var count = rm->GetRetainerCount();
            for (uint i = 0; i < count; i++)
            {
                var ret = rm->GetRetainerBySortedIndex(i);
                if (ret == null || ret->RetainerId == 0)
                {
                    continue;
                }

                if (ret->Available)
                {
                    currentCharRetainerIds.Add(ret->RetainerId);
                }
                else
                {
                    unavailableRetainerIds.Add(ret->RetainerId);
                }
            }
        }
        else
        {
            foreach (var (retId, snap) in liveRetainerSnapshots)
            {
                if (unavailableRetainerIds.Contains(retId))
                {
                    continue;
                }

                if (currentCid == 0 || snap.CharacterContentId == 0 || snap.CharacterContentId == currentCid)
                {
                    currentCharRetainerIds.Add(retId);
                }
            }
        }

        var merged = new List<RetainerHolding>(4);
        if (currentCharRetainerIds.Count > 0 && cachedExternalHoldings.TryGetValue(normalized, out var cachedList))
        {
            foreach (var entry in cachedList)
            {
                if (liveRetainerSnapshots.ContainsKey(entry.RetainerId)
                    || unavailableRetainerIds.Contains(entry.RetainerId)
                    || !currentCharRetainerIds.Contains(entry.RetainerId))
                {
                    continue;
                }

                merged.Add(entry);
            }
        }

        var saddlebagCount = GetLiveSaddlebagCount(normalized, out var anySaddlebagLoaded);
        if (!anySaddlebagLoaded && currentCid != 0 && cachedSaddlebagByChar.TryGetValue((currentCid, normalized), out var sbQty))
        {
            saddlebagCount = sbQty;
        }

        foreach (var (retainerId, snap) in liveRetainerSnapshots)
        {
            if (unavailableRetainerIds.Contains(retainerId))
            {
                continue;
            }

            if (rmReady && !currentCharRetainerIds.Contains(retainerId))
            {
                continue;
            }

            if (!rmReady && currentCid != 0 && snap.CharacterContentId != 0 && snap.CharacterContentId != currentCid)
            {
                continue;
            }

            if (snap.Quantities.TryGetValue(normalized, out var liveQty) && liveQty > 0)
            {
                merged.Add(new RetainerHolding(retainerId, snap.Name, liveQty));
            }
        }

        return new ItemHoldingSummary(normalized, bagCount, merged, saddlebagCount);
    }

    public string FormatExternalOwnership(uint baseItemId, int maxChars = 26)
    {
        var summary = GetHoldingSummary(baseItemId);
        if (summary.Retainers.Count == 0 && summary.SaddlebagCount <= 0)
        {
            return string.Empty;
        }

        var parts = summary.Retainers.Select(r => $"{r.RetainerName}: {r.Quantity}").ToList();
        if (summary.SaddlebagCount > 0)
        {
            parts.Add($"Saddlebag: {summary.SaddlebagCount}");
        }

        var joined = string.Join(", ", parts);
        return joined.Length <= maxChars
            ? joined
            : $"Stored: {summary.TotalExternalCount}";
    }

    public Dictionary<uint, string> BuildOwnedLocationsMap()
    {
        var partsByItem = new Dictionary<uint, List<string>>(512);
        var bagCounts = new Dictionary<uint, int>(140);
        var armouryCounts = new Dictionary<uint, int>(128);
        var equippedIds = new HashSet<uint>(16);

        var mgr = InventoryManager.Instance();
        if (mgr != null)
        {
            var eq = mgr->GetInventoryContainer(InventoryType.EquippedItems);
            if (eq != null && eq->Items != null)
            {
                for (var i = 0; i < eq->Size; i++)
                {
                    var s = eq->GetInventorySlot(i);
                    if (s != null && s->ItemId != 0 && s->Quantity > 0)
                    {
                        equippedIds.Add(ItemIdentity.NormalizeBaseItemId(s->ItemId));
                    }
                }
            }

            foreach (var armType in InventorySafetyEvaluator.ArmouryGearContainers)
            {
                var c = mgr->GetInventoryContainer(armType);
                if (c == null || c->Items == null)
                {
                    continue;
                }

                for (var i = 0; i < c->Size; i++)
                {
                    var s = c->GetInventorySlot(i);
                    if (s != null && s->ItemId != 0 && s->Quantity > 0)
                    {
                        var id = ItemIdentity.NormalizeBaseItemId(s->ItemId);
                        armouryCounts[id] = armouryCounts.GetValueOrDefault(id) + (int)s->Quantity;
                    }
                }
            }

            foreach (var bagType in InventorySafetyEvaluator.PlayerBagContainersWithCrystals)
            {
                var c = mgr->GetInventoryContainer(bagType);
                if (!InventorySafetyEvaluator.IsContainerReadable(c))
                {
                    continue;
                }

                for (var i = 0; i < c->Size; i++)
                {
                    var s = c->GetInventorySlot(i);
                    if (s != null && s->ItemId != 0 && s->Quantity > 0)
                    {
                        var id = ItemIdentity.NormalizeBaseItemId(s->ItemId);
                        bagCounts[id] = bagCounts.GetValueOrDefault(id) + (int)s->Quantity;
                    }
                }
            }

            var liveSaddlebagCounts = new Dictionary<uint, int>();
            var anySbLoaded = false;
            foreach (var sbType in SaddlebagContainers)
            {
                var c = mgr->GetInventoryContainer(sbType);
                if (!InventorySafetyEvaluator.IsContainerReadable(c))
                {
                    continue;
                }

                anySbLoaded = true;
                for (var i = 0; i < c->Size; i++)
                {
                    var s = c->GetInventorySlot(i);
                    if (s != null && s->ItemId != 0 && s->Quantity > 0)
                    {
                        var id = ItemIdentity.NormalizeBaseItemId(s->ItemId);
                        liveSaddlebagCounts[id] = liveSaddlebagCounts.GetValueOrDefault(id) + (int)s->Quantity;
                    }
                }
            }

            if (anySbLoaded)
            {
                foreach (var (id, qty) in liveSaddlebagCounts)
                {
                    AddLoc(id, qty > 1 ? $"Saddlebag ({qty})" : "Saddlebag");
                }
            }
            else
            {
                var cid = InventorySafetyEvaluator.GetCurrentContentId();
                if (cid != 0)
                {
                    foreach (var ((ownerCid, id), qty) in cachedSaddlebagByChar)
                    {
                        if (ownerCid == cid && id != 0 && qty > 0)
                        {
                            AddLoc(id, qty > 1 ? $"Saddlebag ({qty})" : "Saddlebag");
                        }
                    }
                }
            }
        }

        void AddLoc(uint id, string label)
        {
            if (!partsByItem.TryGetValue(id, out var list))
            {
                list = new List<string>(2);
                partsByItem[id] = list;
            }

            list.Add(label);
        }

        foreach (var id in equippedIds)
        {
            AddLoc(id, "Equipped");
        }

        foreach (var (id, qty) in armouryCounts)
        {
            AddLoc(id, qty > 1 ? $"Armoury ({qty})" : "Armoury");
        }

        foreach (var (id, qty) in bagCounts)
        {
            AddLoc(id, qty > 1 ? $"Bag ({qty})" : "Bag");
        }

        foreach (var ret in GetKnownRetainersForCurrentCharacter())
        {
            foreach (var (id, qty) in ret.Quantities)
            {
                if (id != 0 && qty > 0)
                {
                    AddLoc(id, qty > 1 ? $"{ret.Name} ({qty})" : ret.Name);
                }
            }
        }

        var result = new Dictionary<uint, string>(partsByItem.Count);
        foreach (var (id, list) in partsByItem)
        {
            result[id] = string.Join(", ", list);
        }

        return result;
    }

    public static int GetLiveBagCount(uint baseItemId)
        => CountInContainers(baseItemId, InventorySafetyEvaluator.PlayerBagContainersWithCrystals);

    public static int GetLiveActiveRetainerCount(uint baseItemId)
        => CountInContainers(baseItemId, InventorySafetyEvaluator.RetainerBagContainersWithCrystals);

    public static string? GetActiveRetainerName()
    {
        var rm = RetainerManager.Instance();
        if (rm == null || !rm->IsReady)
        {
            return null;
        }

        var active = rm->GetActiveRetainer();
        if (active == null || active->RetainerId == 0)
        {
            return null;
        }

        var name = active->NameString;
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    public static ulong GetActiveRetainerId()
    {
        var rm = RetainerManager.Instance();
        if (rm == null || !rm->IsReady)
        {
            return 0;
        }

        var active = rm->GetActiveRetainer();
        return active != null ? active->RetainerId : 0;
    }

    private static int GetLiveSaddlebagCount(uint baseItemId, out bool anyContainerLoaded)
    {
        anyContainerLoaded = false;
        var manager = InventoryManager.Instance();
        if (manager == null || baseItemId == 0)
        {
            return 0;
        }

        var normalized = ItemIdentity.NormalizeBaseItemId(baseItemId);
        var total = 0;
        foreach (var sbType in SaddlebagContainers)
        {
            var container = manager->GetInventoryContainer(sbType);
            if (!InventorySafetyEvaluator.IsContainerReadable(container))
            {
                continue;
            }

            anyContainerLoaded = true;
            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->Items + i;
                if (slot != null && slot->Quantity > 0 && ItemIdentity.NormalizeBaseItemId(slot->ItemId) == normalized)
                {
                    total += slot->Quantity;
                }
            }
        }

        return total;
    }

    private static int CountInContainers(uint baseItemId, ReadOnlySpan<InventoryType> containers)
    {
        var manager = InventoryManager.Instance();
        if (manager == null || baseItemId == 0)
        {
            return 0;
        }

        var normalized = ItemIdentity.NormalizeBaseItemId(baseItemId);
        var total = 0;
        foreach (var containerType in containers)
        {
            var container = manager->GetInventoryContainer(containerType);
            if (!InventorySafetyEvaluator.IsContainerReadable(container))
            {
                continue;
            }

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->Items + i;
                if (slot != null && slot->Quantity > 0 && ItemIdentity.NormalizeBaseItemId(slot->ItemId) == normalized)
                {
                    total += slot->Quantity;
                }
            }
        }

        return total;
    }

    private void RefreshRetainerCacheIfChanged()
    {
        var now = DateTime.UtcNow;
        if ((now - lastExternalPollUtc).TotalMilliseconds < 2000.0)
        {
            return;
        }

        lastExternalPollUtc = now;

        try
        {
            if (File.Exists(inventoryToolsJsonPath))
            {
                var jsonWrite = File.GetLastWriteTimeUtc(inventoryToolsJsonPath);
                if (jsonWrite != lastJsonWriteUtc)
                {
                    LoadRetainerNames();
                    lastJsonWriteUtc = jsonWrite;
                    lastCsvWriteUtc = DateTime.MinValue;
                }
            }

            if (File.Exists(inventoriesCsvPath))
            {
                var csvWrite = File.GetLastWriteTimeUtc(inventoriesCsvPath);
                if (csvWrite != lastCsvWriteUtc)
                {
                    LoadRetainerCsv();
                    lastCsvWriteUtc = csvWrite;
                }
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Skipped Allagan Tools cache refresh while file was busy.");
        }
    }

    private void LoadRetainerNames()
    {
        using var stream = new FileStream(inventoryToolsJsonPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var doc = JsonDocument.Parse(stream);
        if (!doc.RootElement.TryGetProperty("SavedCharacters", out var savedChars) || savedChars.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var map = new Dictionary<ulong, string>();
        foreach (var prop in savedChars.EnumerateObject())
        {
            if (!ulong.TryParse(prop.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                continue;
            }

            if (prop.Value.ValueKind == JsonValueKind.Object
                && prop.Value.TryGetProperty("Name", out var nameEl)
                && nameEl.ValueKind == JsonValueKind.String)
            {
                var name = nameEl.GetString();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    map[id] = name;
                }
            }
        }

        ownerNames = map;
    }

    public static List<string> SplitCsvLine(string line)
    {
        var fields = new List<string>(26);
        var i = 0;
        var len = line.Length;
        while (i <= len)
        {
            if (i < len && line[i] == '"')
            {
                i++;
                var sb = new System.Text.StringBuilder();
                while (i < len)
                {
                    if (line[i] == '"')
                    {
                        if (i + 1 < len && line[i + 1] == '"')
                        {
                            sb.Append('"');
                            i += 2;
                        }
                        else
                        {
                            i++;
                            break;
                        }
                    }
                    else
                    {
                        sb.Append(line[i++]);
                    }
                }

                fields.Add(sb.ToString());
                if (i < len && line[i] == ',')
                {
                    i++;
                }
                else
                {
                    break;
                }
            }
            else
            {
                var start = i;
                while (i < len && line[i] != ',')
                {
                    i++;
                }

                fields.Add(line.Substring(start, i - start));
                if (i < len && line[i] == ',')
                {
                    i++;
                }
                else
                {
                    break;
                }
            }
        }

        return fields;
    }

    private void LoadRetainerCsv()
    {
        var itemSheet = dataManager.GetExcelSheet<Item>();
        var totalsByItemAndOwner = new Dictionary<(uint ItemId, ulong OwnerId), int>();
        var quantitiesByOwner = new Dictionary<ulong, Dictionary<uint, int>>();
        var categoryIdsByOwner = new Dictionary<ulong, Dictionary<uint, int>>();
        var nextSaddlebagByChar = new Dictionary<(ulong CharacterId, uint ItemId), int>();
        var hqByOwner = new Dictionary<ulong, Dictionary<uint, int>>();
        var usedSlotsByOwner = new Dictionary<ulong, int>();
        var partialsByOwner = new Dictionary<ulong, HashSet<(uint BaseItemId, bool IsHq)>>();

        using var stream = new FileStream(inventoriesCsvPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var cols = SplitCsvLine(line);
            if (cols.Count < 24)
            {
                continue;
            }

            if (!int.TryParse(cols[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var container))
            {
                continue;
            }

            var isRegularRetainerPage = container is >= 10000 and <= 10006;
            var isRetainerBag = isRegularRetainerPage || container == 12001;
            var isSaddlebag = container is 4000 or 4001 or 4100 or 4101;
            if (!isRetainerBag && !isSaddlebag)
            {
                continue;
            }

            if (!uint.TryParse(cols[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var itemId) || itemId == 0)
            {
                continue;
            }

            if (!int.TryParse(cols[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var qty) || qty <= 0)
            {
                continue;
            }

            if (!ulong.TryParse(cols[23], NumberStyles.None, CultureInfo.InvariantCulture, out var ownerId) || ownerId == 0)
            {
                continue;
            }

            var normalized = ItemIdentity.NormalizeBaseItemId(itemId);
            if (normalized == 0)
            {
                continue;
            }

            if (isSaddlebag)
            {
                var sbKey = (ownerId, normalized);
                nextSaddlebagByChar[sbKey] = nextSaddlebagByChar.GetValueOrDefault(sbKey) + qty;
                continue;
            }

            var key = (normalized, ownerId);
            totalsByItemAndOwner[key] = totalsByItemAndOwner.GetValueOrDefault(key) + qty;

            if (!quantitiesByOwner.TryGetValue(ownerId, out var ownerQtyMap))
            {
                ownerQtyMap = new Dictionary<uint, int>();
                quantitiesByOwner[ownerId] = ownerQtyMap;
            }

            ownerQtyMap[normalized] = ownerQtyMap.GetValueOrDefault(normalized) + qty;

            if (isRegularRetainerPage)
            {
                usedSlotsByOwner[ownerId] = usedSlotsByOwner.GetValueOrDefault(ownerId) + 1;
                var flagsVal = cols.Count > 6
                    && int.TryParse(cols[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedFlags)
                    ? parsedFlags
                    : 0;
                var isHq = ItemIdentity.IsHighQuality(itemId, (InventoryItem.ItemFlags)flagsVal);
                if (isHq)
                {
                    if (!hqByOwner.TryGetValue(ownerId, out var hqDict))
                    {
                        hqDict = new Dictionary<uint, int>();
                        hqByOwner[ownerId] = hqDict;
                    }

                    hqDict[normalized] = hqDict.GetValueOrDefault(normalized) + qty;
                }

                var row = itemSheet?.GetRowOrDefault(normalized);
                if (row is not null && row.Value.ItemUICategory.RowId != 0)
                {
                    if (!categoryIdsByOwner.TryGetValue(ownerId, out var catDict))
                    {
                        catDict = new Dictionary<uint, int>();
                        categoryIdsByOwner[ownerId] = catDict;
                    }

                    var catId = row.Value.ItemUICategory.RowId;
                    catDict[catId] = catDict.GetValueOrDefault(catId) + 1;
                }

                var stackSize = row is not null ? (int)Math.Max(1, row.Value.StackSize) : 1;
                var isCollectable = ItemIdentity.IsCollectable(itemId, (InventoryItem.ItemFlags)flagsVal);
                if (stackSize > 1 && !isCollectable && qty < stackSize)
                {
                    if (!partialsByOwner.TryGetValue(ownerId, out var set))
                    {
                        set = new HashSet<(uint BaseItemId, bool IsHq)>();
                        partialsByOwner[ownerId] = set;
                    }

                    set.Add((normalized, isHq));
                }
            }
        }

        var nextHoldings = new Dictionary<uint, List<RetainerHolding>>();
        foreach (var ((itemId, ownerId), qty) in totalsByItemAndOwner)
        {
            var name = ownerNames.GetValueOrDefault(ownerId, $"Retainer {ownerId % 10000}");
            if (!nextHoldings.TryGetValue(itemId, out var list))
            {
                list = new List<RetainerHolding>();
                nextHoldings[itemId] = list;
            }

            list.Add(new RetainerHolding(ownerId, name, qty));
        }

        cachedExternalHoldings = nextHoldings;
        cachedCsvQuantitiesByOwner = quantitiesByOwner;
        cachedCsvCategoryIdsByOwner = categoryIdsByOwner;
        cachedSaddlebagByChar = nextSaddlebagByChar;
        cachedRetainerHqQuantities = hqByOwner;
        cachedRetainerUsedSlots = usedSlotsByOwner;
        cachedRetainerPartialStacks = partialsByOwner;
    }

    private sealed class NativeRetainerDto
    {
        public ulong CharacterContentId { get; set; }
        public ulong RetainerId { get; set; }
        public string Name { get; set; } = string.Empty;
        public DateTime CapturedUtc { get; set; }
        public int UsedBagSlots { get; set; }
        public Dictionary<uint, int> Quantities { get; set; } = new();
        public Dictionary<uint, int>? HqQuantities { get; set; }
        public List<string> PartialStackKeys { get; set; } = new();
        public Dictionary<uint, int>? CategoryIdCounts { get; set; }
        public Dictionary<string, int>? CategoryCounts { get; set; }
    }

    private void LoadNativeCache()
    {
        if (!File.Exists(nativeCachePath))
        {
            return;
        }

        try
        {
            var dtos = JsonSerializer.Deserialize<List<NativeRetainerDto>>(File.ReadAllText(nativeCachePath));
            if (dtos == null)
            {
                return;
            }

            Dictionary<string, uint>? catNameToId = null;
            var uiCatSheet = dataManager.GetExcelSheet<ItemUICategory>();
            if (uiCatSheet != null)
            {
                catNameToId = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in uiCatSheet)
                {
                    var n = row.Name.ExtractText();
                    if (row.RowId != 0 && !string.IsNullOrWhiteSpace(n))
                    {
                        catNameToId.TryAdd(n, row.RowId);
                    }
                }
            }

            foreach (var dto in dtos)
            {
                if (dto.RetainerId == 0)
                {
                    continue;
                }

                var partials = new HashSet<(uint BaseItemId, bool IsHq)>();
                foreach (var key in dto.PartialStackKeys)
                {
                    var parts = key.Split(':');
                    if (parts.Length == 2 && uint.TryParse(parts[0], out var id))
                    {
                        partials.Add((id, parts[1] == "1"));
                    }
                }

                var catIdCounts = dto.CategoryIdCounts != null
                    ? new Dictionary<uint, int>(dto.CategoryIdCounts)
                    : new Dictionary<uint, int>();
                if (catIdCounts.Count == 0 && dto.CategoryCounts != null && catNameToId != null)
                {
                    foreach (var (legacyName, count) in dto.CategoryCounts)
                    {
                        if (catNameToId.TryGetValue(legacyName, out var catId))
                        {
                            catIdCounts[catId] = catIdCounts.GetValueOrDefault(catId) + count;
                        }
                    }
                }

                liveRetainerSnapshots[dto.RetainerId] = new LiveRetainerSnapshot(
                    dto.CharacterContentId,
                    dto.CapturedUtc,
                    dto.Name,
                    dto.UsedBagSlots,
                    partials,
                    dto.Quantities ?? new Dictionary<uint, int>(),
                    catIdCounts,
                    dto.HqQuantities ?? new Dictionary<uint, int>());
            }
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Skipped unreadable native retainer_cache.json.");
        }
    }

    private void SaveNativeCache(bool synchronous = false)
    {
        try
        {
            var dtos = new List<NativeRetainerDto>(liveRetainerSnapshots.Count);
            foreach (var (retId, snap) in liveRetainerSnapshots)
            {
                dtos.Add(new NativeRetainerDto
                {
                    CharacterContentId = snap.CharacterContentId,
                    RetainerId = retId,
                    Name = snap.Name,
                    CapturedUtc = snap.CapturedUtc,
                    UsedBagSlots = snap.UsedBagSlots,
                    Quantities = new Dictionary<uint, int>(snap.Quantities),
                    HqQuantities = new Dictionary<uint, int>(snap.HqQuantities),
                    PartialStackKeys = snap.PartialStacks.Select(p => $"{p.BaseItemId}:{(p.IsHq ? 1 : 0)}").ToList(),
                    CategoryIdCounts = new Dictionary<uint, int>(snap.CategoryCounts),
                });
            }

            var targetPath = nativeCachePath;
            var gen = System.Threading.Interlocked.Increment(ref saveGeneration);
            nativeCacheDirty = true;

            void WriteAction()
            {
                lock (nativeCacheSaveGate)
                {
                    if (!synchronous && gen != System.Threading.Volatile.Read(ref saveGeneration))
                    {
                        return;
                    }

                    try
                    {
                        var dir = Path.GetDirectoryName(targetPath);
                        if (!string.IsNullOrEmpty(dir))
                        {
                            Directory.CreateDirectory(dir);
                        }

                        var temp = targetPath + ".tmp";
                        File.WriteAllText(temp, JsonSerializer.Serialize(dtos));
                        File.Move(temp, targetPath, overwrite: true);
                        if (gen == System.Threading.Volatile.Read(ref saveGeneration))
                        {
                            nativeCacheDirty = false;
                        }
                    }
                    catch (Exception ex)
                    {
                        log.Debug(ex, "Failed to save native retainer_cache.json.");
                    }
                }
            }

            if (synchronous)
            {
                WriteAction();
            }
            else
            {
                _ = System.Threading.Tasks.Task.Run(WriteAction);
            }
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Failed to snapshot native retainer_cache.json.");
        }
    }
}
