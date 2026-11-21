using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace MagicInventory.Services.Triage;

public sealed record MarketPriceSnapshot(
    uint ItemId,
    int MinPriceNq,
    int MinPriceHq,
    int MinPriceAny,
    int RecentHistoryMedian,
    float RegularSaleVelocity,
    DateTime FetchedUtc,
    int ListingCount = -1,
    int RecentHistoryMedianHq = 0);

public sealed record DcCheapestListing(
    uint ItemId,
    string WorldName,
    int UnitPrice,
    int Quantity,
    bool IsHq);

/// <summary>
/// Polite, background Universalis v2 market client with 6-hour disk TTL cache,
/// 95-item batching, 1 req/sec pacing, and exponential backoff on 429/5xx.
/// Strictly ignores currentAveragePrice; prices solely from minPriceNQ/HQ,
/// regularSaleVelocity, and recentHistory median.
/// </summary>
public sealed class UniversalisClient : IDisposable
{
    public static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);
    private const int BatchSize = 95;
    private const int MinDelayBetweenRequestsMs = 1050;

    private readonly string cacheFilePath;
    private readonly IPluginLog log;
    private readonly HttpClient httpClient;
    private readonly CancellationTokenSource cts = new();

    private readonly object diskSaveGate = new();
    private readonly ConcurrentDictionary<(string World, uint ItemId), MarketPriceSnapshot> cache = new();
    private readonly ConcurrentDictionary<(string World, uint ItemId), byte> queuedKeys = new();
    private readonly ConcurrentQueue<(string World, uint ItemId)> pendingQueue = new();
    private readonly SemaphoreSlim wakeSignal = new(0);
    private readonly Task workerTask;

    private int totalQueuedInCurrentScan;
    private int completedInCurrentScan;
    private volatile bool isScanning;
    private string lastError = string.Empty;
    private DateTime lastScanUtc = DateTime.MinValue;
    private volatile bool cacheDirty;

    public UniversalisClient(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.log = log;
        cacheFilePath = Path.Combine(pluginInterface.ConfigDirectory.FullName, "universalis_cache.json");

        httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("MagicInventory/1.0 (FFXIV Dalamud)");
        httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        LoadDiskCache();
        workerTask = Task.Run(() => WorkerLoopAsync(cts.Token));
    }

    public event Action? PricesUpdated;

    public bool IsScanning => isScanning || !pendingQueue.IsEmpty;
    public int TotalQueuedInScan => Volatile.Read(ref totalQueuedInCurrentScan);
    public int CompletedInScan => Volatile.Read(ref completedInCurrentScan);
    public int CachedEntryCount => cache.Count;
    public DateTime LastScanUtc => lastScanUtc;
    public string LastError => lastError;

    public bool TryGetFreshPrice(string world, uint baseItemId, out MarketPriceSnapshot snapshot)
    {
        snapshot = default!;
        if (string.IsNullOrWhiteSpace(world) || baseItemId == 0)
        {
            return false;
        }

        if (!cache.TryGetValue((world, baseItemId), out var entry))
        {
            return false;
        }

        if ((DateTime.UtcNow - entry.FetchedUtc) > CacheTtl)
        {
            return false;
        }

        snapshot = entry;
        return true;
    }

    public bool TryGetAnyCachedPrice(string world, uint baseItemId, out MarketPriceSnapshot snapshot)
    {
        snapshot = default!;
        if (string.IsNullOrWhiteSpace(world) || baseItemId == 0)
        {
            return false;
        }

        return cache.TryGetValue((world, baseItemId), out snapshot!);
    }

    public int QueueMissing(string world, IEnumerable<uint> baseItemIds, bool forceRefresh = false)
    {
        if (string.IsNullOrWhiteSpace(world))
        {
            return 0;
        }

        var now = DateTime.UtcNow;
        var added = 0;
        foreach (var id in baseItemIds)
        {
            if (id <= 20)
            {
                continue;
            }

            var key = (world, id);
            if (!forceRefresh && cache.TryGetValue(key, out var existing) && (now - existing.FetchedUtc) <= CacheTtl)
            {
                continue;
            }

            if (queuedKeys.TryAdd(key, 0))
            {
                pendingQueue.Enqueue(key);
                added++;
            }
        }

        if (added > 0)
        {
            if (!isScanning && pendingQueue.Count == added)
            {
                Volatile.Write(ref totalQueuedInCurrentScan, added);
                Volatile.Write(ref completedInCurrentScan, 0);
            }
            else
            {
                Interlocked.Add(ref totalQueuedInCurrentScan, added);
            }

            try
            {
                wakeSignal.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }

        return added;
    }

    public async Task<DcCheapestListing?> FetchDataCenterCheapestAsync(
        string dataCenterName,
        uint baseItemId,
        string? excludeWorld = null,
        CancellationToken externalCt = default)
    {
        if (string.IsNullOrWhiteSpace(dataCenterName) || baseItemId <= 20)
        {
            return null;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, externalCt);
        var url = $"https://universalis.app/api/v2/{Uri.EscapeDataString(dataCenterName)}/{baseItemId}?listings=10&entries=0";
        try
        {
            using var resp = await httpClient.GetAsync(url, linked.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: linked.Token).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("listings", out var listingsEl)
                || listingsEl.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            DcCheapestListing? best = null;
            foreach (var item in listingsEl.EnumerateArray())
            {
                var worldName = item.TryGetProperty("worldName", out var wEl) ? wEl.GetString() ?? string.Empty : string.Empty;
                if (string.IsNullOrWhiteSpace(worldName))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(excludeWorld)
                    && string.Equals(worldName, excludeWorld, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var price = item.TryGetProperty("pricePerUnit", out var pEl) && pEl.TryGetInt32(out var p) ? p : 0;
                var qty = item.TryGetProperty("quantity", out var qEl) && qEl.TryGetInt32(out var q) ? q : 0;
                var isHq = item.TryGetProperty("hq", out var hqEl) && hqEl.ValueKind == JsonValueKind.True;
                if (price <= 0 || qty <= 0)
                {
                    continue;
                }

                if (best == null || price < best.UnitPrice)
                {
                    best = new DcCheapestListing(baseItemId, worldName, price, qty, isHq);
                }
            }

            return best;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            log.Debug(ex, $"Universalis DC lookup failed for item {baseItemId} on {dataCenterName}.");
            return null;
        }
    }

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        var lastRequestUtc = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (pendingQueue.IsEmpty)
                {
                    isScanning = false;
                    if (cacheDirty)
                    {
                        cacheDirty = false;
                        SaveDiskCache();
                    }

                    await wakeSignal.WaitAsync(ct).ConfigureAwait(false);
                    continue;
                }

                isScanning = true;

                // Dequeue up to BatchSize items belonging to the same world
                string? batchWorld = null;
                var batchIds = new List<uint>(BatchSize);
                var requeue = new List<(string World, uint ItemId)>();

                while (batchIds.Count < BatchSize && pendingQueue.TryDequeue(out var next))
                {
                    if (batchWorld == null)
                    {
                        batchWorld = next.World;
                        batchIds.Add(next.ItemId);
                    }
                    else if (string.Equals(batchWorld, next.World, StringComparison.Ordinal))
                    {
                        batchIds.Add(next.ItemId);
                    }
                    else
                    {
                        requeue.Add(next);
                    }
                }

                foreach (var r in requeue)
                {
                    pendingQueue.Enqueue(r);
                }

                if (batchWorld == null || batchIds.Count == 0)
                {
                    continue;
                }

                var elapsedMs = (DateTime.UtcNow - lastRequestUtc).TotalMilliseconds;
                if (elapsedMs < MinDelayBetweenRequestsMs)
                {
                    var waitMs = (int)Math.Ceiling(MinDelayBetweenRequestsMs - elapsedMs);
                    await Task.Delay(waitMs, ct).ConfigureAwait(false);
                }

                lastRequestUtc = DateTime.UtcNow;
                var updated = await FetchBatchWithBackoffAsync(batchWorld, batchIds, ct).ConfigureAwait(false);

                foreach (var id in batchIds)
                {
                    queuedKeys.TryRemove((batchWorld, id), out _);
                }

                Interlocked.Add(ref completedInCurrentScan, batchIds.Count);

                if (updated > 0)
                {
                    lastScanUtc = DateTime.UtcNow;
                    cacheDirty = true;
                    PricesUpdated?.Invoke();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                log.Warning(ex, "Universalis background worker encountered an error.");
                await Task.Delay(2000, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<int> FetchBatchWithBackoffAsync(string world, List<uint> itemIds, CancellationToken ct)
    {
        var idsJoined = string.Join(',', itemIds);
        var url = $"https://universalis.app/api/v2/{Uri.EscapeDataString(world)}/{idsJoined}?listings=5&entries=5";

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var resp = await httpClient.GetAsync(url, ct).ConfigureAwait(false);
                if (resp.StatusCode == HttpStatusCode.TooManyRequests
                    || (int)resp.StatusCode >= 500)
                {
                    lastError = $"HTTP {(int)resp.StatusCode} from Universalis (retrying...)";
                    var retryAfterMs = resp.Headers.RetryAfter?.Delta is { } delta
                        ? (int)Math.Clamp(delta.TotalMilliseconds, 1000, 30000)
                        : (2000 << attempt);
                    await Task.Delay(retryAfterMs, ct).ConfigureAwait(false);
                    continue;
                }

                if (resp.StatusCode == HttpStatusCode.NotFound)
                {
                    // Short 15-minute cooldown instead of infinite 1.5s re-poll loop or 6-hour poisoning
                    var shortExpiryFetch = DateTime.UtcNow - CacheTtl + TimeSpan.FromMinutes(15);
                    foreach (var requestedId in itemIds)
                    {
                        cache.TryAdd((world, requestedId), new MarketPriceSnapshot(requestedId, 0, 0, 0, 0, 0f, shortExpiryFetch, -1));
                    }

                    lastError = string.Empty;
                    return 0;
                }

                if (!resp.IsSuccessStatusCode)
                {
                    lastError = $"Universalis returned HTTP {(int)resp.StatusCode}.";
                    return 0;
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

                var now = DateTime.UtcNow;
                var updated = 0;
                var returnedIds = new HashSet<uint>();

                if (doc.RootElement.TryGetProperty("items", out var itemsEl) && itemsEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in itemsEl.EnumerateObject())
                    {
                        if (uint.TryParse(prop.Name, out var id) && ParseItemSnapshot(id, prop.Value, now, out var snap))
                        {
                            cache[(world, id)] = snap;
                            returnedIds.Add(id);
                            updated++;
                        }
                    }
                }
                else if (doc.RootElement.TryGetProperty("itemID", out var singleIdEl)
                    && singleIdEl.TryGetUInt32(out var singleId)
                    && ParseItemSnapshot(singleId, doc.RootElement, now, out var singleSnap))
                {
                    cache[(world, singleId)] = singleSnap;
                    returnedIds.Add(singleId);
                    updated++;
                }

                // Short 15-minute cooldown for unreturned batch IDs (ListingCount = -1 unknown, NOT 6-hour poisoned 0-listing)
                var missingFetchUtc = now - CacheTtl + TimeSpan.FromMinutes(15);
                foreach (var requestedId in itemIds)
                {
                    if (!returnedIds.Contains(requestedId))
                    {
                        cache[(world, requestedId)] = new MarketPriceSnapshot(requestedId, 0, 0, 0, 0, 0f, missingFetchUtc, -1);
                        updated++;
                    }
                }

                lastError = string.Empty;
                return updated;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                if (attempt < 2)
                {
                    await Task.Delay(1500 << attempt, ct).ConfigureAwait(false);
                }
            }
        }

        return 0;
    }

    private static bool ParseItemSnapshot(uint itemId, JsonElement el, DateTime fetchedUtc, out MarketPriceSnapshot snapshot)
    {
        var minNq = ReadInt(el, "minPriceNQ");
        var minHq = ReadInt(el, "minPriceHQ");
        var minAny = ReadInt(el, "minPrice");
        var velocity = ReadFloat(el, "regularSaleVelocity");
        var (medianNq, medianHq) = ComputeRecentHistoryMedians(el);

        int listingCount;
        if (el.TryGetProperty("listingsCount", out var lcEl) && lcEl.ValueKind == JsonValueKind.Number && lcEl.TryGetInt32(out var lc))
        {
            listingCount = Math.Max(0, lc);
        }
        else if (el.TryGetProperty("listings", out var listEl) && listEl.ValueKind == JsonValueKind.Array)
        {
            listingCount = listEl.GetArrayLength();
        }
        else
        {
            listingCount = minAny > 0 ? 1 : 0;
        }

        snapshot = new MarketPriceSnapshot(itemId, minNq, minHq, minAny, medianNq, velocity, fetchedUtc, listingCount, medianHq);
        return true;
    }

    private static (int MedianNq, int MedianHq) ComputeRecentHistoryMedians(JsonElement el)
    {
        if (!el.TryGetProperty("recentHistory", out var histEl) || histEl.ValueKind != JsonValueKind.Array)
        {
            return (0, 0);
        }

        var nqPrices = new List<int>(8);
        var hqPrices = new List<int>(8);
        var allPrices = new List<int>(8);
        foreach (var entry in histEl.EnumerateArray())
        {
            var price = ReadInt(entry, "pricePerUnit");
            if (price <= 0)
            {
                continue;
            }

            allPrices.Add(price);
            var isHq = entry.TryGetProperty("hq", out var hqEl) && hqEl.ValueKind == JsonValueKind.True;
            if (isHq)
            {
                hqPrices.Add(price);
            }
            else
            {
                nqPrices.Add(price);
            }
        }

        static int Median(List<int> list)
        {
            if (list.Count == 0)
            {
                return 0;
            }

            list.Sort();
            var mid = list.Count / 2;
            return (list.Count & 1) == 1
                ? list[mid]
                : (list[mid - 1] + list[mid]) / 2;
        }

        var medNq = nqPrices.Count > 0 ? Median(nqPrices) : Median(allPrices);
        var medHq = Median(hqPrices);
        return (medNq, medHq);
    }

    private static int ReadInt(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var el))
        {
            return 0;
        }

        if (el.ValueKind == JsonValueKind.Number)
        {
            if (el.TryGetInt32(out var i))
            {
                return i;
            }

            if (el.TryGetDouble(out var d))
            {
                return (int)Math.Round(d);
            }
        }

        return 0;
    }

    private static float ReadFloat(JsonElement obj, string prop)
    {
        if (obj.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetSingle(out var f))
        {
            return f;
        }

        return 0f;
    }

    private sealed class DiskCacheDto
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, Dictionary<uint, MarketPriceSnapshot>> Worlds { get; set; } = new();
    }

    private void LoadDiskCache()
    {
        if (!File.Exists(cacheFilePath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(cacheFilePath);
            var dto = JsonSerializer.Deserialize<DiskCacheDto>(json);
            if (dto?.Worlds == null)
            {
                return;
            }

            var cutoff = DateTime.UtcNow - TimeSpan.FromDays(3);
            foreach (var (world, items) in dto.Worlds)
            {
                if (string.IsNullOrWhiteSpace(world) || items == null)
                {
                    continue;
                }

                foreach (var (id, snap) in items)
                {
                    if (id > 0 && snap != null && snap.FetchedUtc >= cutoff)
                    {
                        cache[(world, id)] = snap;
                        if (snap.FetchedUtc > lastScanUtc)
                        {
                            lastScanUtc = snap.FetchedUtc;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Skipped unreadable Universalis disk cache.");
        }
    }

    private void SaveDiskCache()
    {
        lock (diskSaveGate)
        {
            try
            {
                var dir = Path.GetDirectoryName(cacheFilePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var cutoff = DateTime.UtcNow.AddDays(-3);
                var dto = new DiskCacheDto();
                foreach (var ((world, id), snap) in cache)
                {
                    if (snap.FetchedUtc < cutoff)
                    {
                        cache.TryRemove((world, id), out _);
                        continue;
                    }

                    if (!dto.Worlds.TryGetValue(world, out var map))
                    {
                        map = new Dictionary<uint, MarketPriceSnapshot>();
                        dto.Worlds[world] = map;
                    }

                    map[id] = snap;
                }

                var tempPath = cacheFilePath + ".tmp";
                File.WriteAllText(tempPath, JsonSerializer.Serialize(dto));
                File.Move(tempPath, cacheFilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                log.Debug(ex, "Failed to persist Universalis disk cache.");
            }
        }
    }

    public void Dispose()
    {
        cts.Cancel();
        try
        {
            wakeSignal.Release();
        }
        catch
        {
        }

        var stopped = false;
        try
        {
            stopped = workerTask.Wait(TimeSpan.FromSeconds(2.5));
        }
        catch
        {
            stopped = true;
        }

        if (cacheDirty)
        {
            cacheDirty = false;
            SaveDiskCache();
        }

        if (stopped)
        {
            httpClient.Dispose();
            wakeSignal.Dispose();
            cts.Dispose();
        }
    }
}
