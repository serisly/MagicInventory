using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using MagicInventory.Core;
using MagicInventory.Services;

namespace MagicInventory.UI.Settings;

internal sealed class GameItemSearchIndex
{
    private const double DebounceMs = 200.0;
    private const double ClassificationTtlMs = 1500.0;
    private const int MaxMatches = 80;

    private readonly Func<(uint RowId, string Name)[]> buildIndex;
    private (uint RowId, string Name)[]? index;
    private string pendingQuery = string.Empty;
    private DateTime lastInputUtc = DateTime.MinValue;
    private string lastExecutedQuery = string.Empty;
    private List<uint> cachedIds = new();
    private List<MagicSortEntry> cachedResults = new();
    private DateTime lastClassifiedUtc = DateTime.MinValue;

    public GameItemSearchIndex(IDataManager dataManager)
        : this(() => BuildFromDataManager(dataManager))
    {
    }

    internal GameItemSearchIndex(Func<(uint RowId, string Name)[]> buildIndex)
    {
        this.buildIndex = buildIndex;
    }

    public static bool IsQueryExecutable(string trimmedQuery)
        => trimmedQuery.Length >= 2 || uint.TryParse(trimmedQuery, out _);

    public bool IsDebounceReady(string trimmedQuery, DateTime nowUtc)
        => IsQueryExecutable(trimmedQuery)
            && !string.Equals(trimmedQuery, lastExecutedQuery, StringComparison.OrdinalIgnoreCase)
            && (nowUtc - lastInputUtc).TotalMilliseconds >= DebounceMs;

    public void InvalidateClassification()
    {
        lastClassifiedUtc = DateTime.MinValue;
    }

    public void ResetResults()
    {
        cachedResults.Clear();
        lastClassifiedUtc = DateTime.MinValue;
    }

    public List<MagicSortEntry> Query(
        string trimmedQuery,
        DateTime nowUtc,
        Func<uint, MagicSortEntry?> classifyItem)
    {
        var results = new List<MagicSortEntry>();
        if (!IsQueryExecutable(trimmedQuery))
        {
            return results;
        }

        if (!string.Equals(trimmedQuery, pendingQuery, StringComparison.OrdinalIgnoreCase))
        {
            pendingQuery = trimmedQuery;
            lastInputUtc = nowUtc;
        }

        var queryNeedsExecution = !string.Equals(trimmedQuery, lastExecutedQuery, StringComparison.OrdinalIgnoreCase);
        if (queryNeedsExecution && (nowUtc - lastInputUtc).TotalMilliseconds >= DebounceMs)
        {
            lastExecutedQuery = trimmedQuery;
            index ??= buildIndex();
            cachedIds = Search(index, trimmedQuery, MaxMatches);
            lastClassifiedUtc = DateTime.MinValue;
        }

        if ((nowUtc - lastClassifiedUtc).TotalMilliseconds >= ClassificationTtlMs)
        {
            lastClassifiedUtc = nowUtc;
            cachedResults = new List<MagicSortEntry>(cachedIds.Count);
            foreach (var id in cachedIds)
            {
                var classified = classifyItem(id);
                if (classified != null)
                {
                    cachedResults.Add(classified);
                }
            }
        }

        results.AddRange(cachedResults);
        return results;
    }

    internal static List<uint> Search((uint RowId, string Name)[] items, string trimmedQuery, int maxResults = MaxMatches)
    {
        var matches = new List<uint>(Math.Min(maxResults, 64));
        if (!IsQueryExecutable(trimmedQuery) || maxResults <= 0)
        {
            return matches;
        }

        var parsedId = uint.TryParse(trimmedQuery, out var exactId) ? exactId : 0u;
        for (var i = 0; i < items.Length; i++)
        {
            var (rowId, itemName) = items[i];
            if (parsedId > 0 && rowId == parsedId)
            {
                matches.Add(rowId);
                break;
            }

            if (itemName.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(rowId);
                if (matches.Count >= maxResults)
                {
                    break;
                }
            }
        }

        return matches;
    }

    private static (uint RowId, string Name)[] BuildFromDataManager(IDataManager dataManager)
    {
        var sheet = dataManager.GetExcelSheet<Item>();
        if (sheet == null)
        {
            return Array.Empty<(uint, string)>();
        }

        var list = new List<(uint, string)>(32_000);
        foreach (var row in sheet)
        {
            if (!ItemIdentity.CanLockOrTagItem(row.RowId, row))
            {
                continue;
            }

            var itemName = row.Name.ExtractText();
            if (!string.IsNullOrEmpty(itemName))
            {
                list.Add((row.RowId, itemName));
            }
        }

        return list.ToArray();
    }
}
