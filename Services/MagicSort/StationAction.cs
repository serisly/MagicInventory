using System;
using System.Collections.Generic;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.Game;
using MagicInventory.Core;

namespace MagicInventory.Services.MagicSort;

public enum StationActionId
{
    UseAll,
    InventorySort,
    RetainerSort,
    MarketSell,
    VendorSell,
    GrandCompany,
    MateriaExtract,
    ArmoireStore,
    GlamourStore,
}

public readonly record struct StationCacheKey(ulong Primary = 0, int Secondary = 0, int Tertiary = 0, int Quaternary = 0);

public readonly record struct StationBatchPlan(
    IReadOnlyList<TransactionStep> Steps,
    bool HasMoreRemaining = false,
    string? StartedMessage = null,
    string? EmptyMessage = null,
    Action? OnBatchFinished = null)
{
    public static readonly StationBatchPlan Empty = new(Array.Empty<TransactionStep>());
}

public sealed class StationActionHandler
{
    private readonly ButtonStateCache<StationCacheKey> cache;

    public StationActionId Id { get; }
    public IReadOnlyList<string> HighlightAddonKeys { get; }
    public Func<MagicSortButtonState?>? TryGetImmediateState { get; init; }
    public Func<StationCacheKey> ResolveCacheKey { get; init; } = static () => default;
    public Func<IReadOnlyDictionary<uint, MagicSortEntry>, MagicSortButtonState> ComputeButtonState { get; }
    public Func<bool>? TryPreExecute { get; init; }
    public Func<IReadOnlyDictionary<uint, MagicSortEntry>, StationBatchPlan> BuildBatch { get; }
    public Action<IReadOnlyDictionary<uint, MagicSortEntry>, HashSet<(InventoryType Container, int Slot)>>? CollectHighlightSlots { get; init; }

    public StationActionHandler(
        StationActionId id,
        Func<IReadOnlyDictionary<uint, MagicSortEntry>, MagicSortButtonState> computeButtonState,
        Func<IReadOnlyDictionary<uint, MagicSortEntry>, StationBatchPlan> buildBatch,
        IReadOnlyList<string>? highlightAddonKeys = null,
        double cacheTtlMs = 250.0)
    {
        Id = id;
        ComputeButtonState = computeButtonState;
        BuildBatch = buildBatch;
        HighlightAddonKeys = highlightAddonKeys ?? Array.Empty<string>();
        cache = new ButtonStateCache<StationCacheKey>(cacheTtlMs);
    }

    public MagicSortButtonState GetCachedButtonState(Func<IReadOnlyDictionary<uint, MagicSortEntry>> getManifest)
    {
        var immediate = TryGetImmediateState?.Invoke();
        if (immediate != null)
        {
            return immediate;
        }

        var key = ResolveCacheKey();
        if (cache.TryGet(key, out var cached))
        {
            return cached;
        }

        return cache.Set(key, ComputeButtonState(getManifest()));
    }

    public void InvalidateCache() => cache.Invalidate();
}

public static class StationActionHelpers
{
    public static bool TrimToBatchLimit<T>(List<T> items, int maxItems)
    {
        if (maxItems <= 0)
        {
            var hadAny = items.Count > 0;
            items.Clear();
            return hadAny;
        }

        if (items.Count <= maxItems)
        {
            return false;
        }

        items.RemoveRange(maxItems, items.Count - maxItems);
        return true;
    }

    public static void AppendBulletLines<T>(
        StringBuilder sb,
        IReadOnlyList<T> items,
        int maxShown,
        Func<T, string> formatItem)
    {
        var shown = Math.Min(items.Count, maxShown);
        for (var i = 0; i < shown; i++)
        {
            sb.Append("\n  - ").Append(formatItem(items[i]));
        }

        if (items.Count > shown)
        {
            sb.Append($"\n  ... and {items.Count - shown} more");
        }
    }

    public static string FormatBulletList<T>(
        string header,
        IReadOnlyList<T> items,
        int maxShown,
        Func<T, string> formatItem,
        string? footer = null)
    {
        var sb = new StringBuilder();
        sb.Append(header);
        AppendBulletLines(sb, items, maxShown, formatItem);
        if (!string.IsNullOrEmpty(footer))
        {
            sb.Append('\n').Append(footer);
        }

        return sb.ToString();
    }
}
