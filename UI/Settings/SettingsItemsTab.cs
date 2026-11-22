using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Lumina.Excel.Sheets;
using MagicInventory.Core;
using MagicInventory.Services;

namespace MagicInventory.UI.Settings;

internal sealed class SettingsItemsTab
{
    private static readonly string[] FilterOptions =
    [
        "All Owned",
        "All Game Items",
        "Market",
        "Retainers",
        "Vendor",
        "GC Seals",
        "Gold Saucer",
        "Extract Materia",
        "Use",
        "Armoire",
        "Glamour Dresser",
        "Keep",
        "Custom Rules",
    ];

    private readonly SettingsUiContext ctx;
    private readonly GameItemSearchIndex gameSearchIndex;

    private string searchQuery = string.Empty;
    private int selectedFilterIndex;
    private List<MagicSortEntry> cachedUnownedCustomRuleEntries = new();
    private DateTime lastUnownedCustomRulesUtc = DateTime.MinValue;
    private int lastCustomRuleCount = -1;
    private DateTime lastLocationsMapUtc = DateTime.MinValue;
    private Dictionary<uint, string> cachedLocationsMap = new();
    private int sortColumnIndex;
    private bool sortDescending;

    private List<MagicSortEntry> cachedDisplayRows = new();
    private string lastRowsQuery = string.Empty;
    private int lastRowsFilter = -1;
    private int lastRowsSortCol = -1;
    private bool lastRowsSortDesc;
    private int lastRowsEntryCount = -1;
    private DateTime lastRowsBuiltUtc = DateTime.MinValue;

    public SettingsItemsTab(SettingsUiContext ctx)
    {
        this.ctx = ctx;
        gameSearchIndex = new GameItemSearchIndex(ctx.DataManager);
    }

    public void InvalidateCaches()
    {
        lastLocationsMapUtc = DateTime.MinValue;
        gameSearchIndex.InvalidateClassification();
        lastUnownedCustomRulesUtc = DateTime.MinValue;
        lastCustomRuleCount = -1;
        lastRowsBuiltUtc = DateTime.MinValue;
    }

    public void ResetForCharacterChange()
    {
        cachedLocationsMap.Clear();
        gameSearchIndex.ResetResults();
        cachedUnownedCustomRuleEntries.Clear();
        cachedDisplayRows.Clear();
        InvalidateCaches();
    }

    public void Draw(ulong cid)
    {
        var scale = ImGuiHelpers.GlobalScale;
        ImGui.SetNextItemWidth(260f * scale);
        ImGui.InputTextWithHint("##ItemSearchInput", "Search item name or ID...", ref searchQuery, 128);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(175f * scale);
        ImGui.Combo("##ItemFilterCombo", ref selectedFilterIndex, FilterOptions, FilterOptions.Length);

        var filter = FilterOptions[selectedFilterIndex];
        var world = ctx.TriageEngine.ActiveWorldName;
        var retainers = ctx.Holdings.GetKnownRetainersForCurrentCharacter();
        var entries = ctx.TriageEngine.GetCurrentEntries();
        var overrides = ctx.Config.GetManualRouteOverrides(cid);
        var priorities = ctx.Config.GetMarketListingPriorities(cid);
        var q = searchQuery.Trim();

        var nowUtc = DateTime.UtcNow;
        if ((nowUtc - lastLocationsMapUtc).TotalSeconds >= 2.0)
        {
            lastLocationsMapUtc = nowUtc;
            cachedLocationsMap = ctx.Holdings.BuildOwnedLocationsMap();
        }

        var pendingAllItemsDebounceReady = filter == "All Game Items" && gameSearchIndex.IsDebounceReady(q, nowUtc);
        var needsRowRebuild = lastRowsFilter != selectedFilterIndex
            || !string.Equals(lastRowsQuery, q, StringComparison.OrdinalIgnoreCase)
            || lastRowsSortCol != sortColumnIndex
            || lastRowsSortDesc != sortDescending
            || lastRowsEntryCount != entries.Count
            || pendingAllItemsDebounceReady
            || (nowUtc - lastRowsBuiltUtc).TotalMilliseconds >= 1000.0;

        if (needsRowRebuild)
        {
            RebuildDisplayRows(filter, q, world, retainers, entries, overrides, priorities, nowUtc);
        }

        var rows = cachedDisplayRows;

        ImGui.SameLine();
        ImGui.TextColored(SettingsUiContext.ColorMuted, $"{rows.Count} shown");

        if (filter == "All Game Items" && !GameItemSearchIndex.IsQueryExecutable(q))
        {
            ImGui.TextColored(SettingsUiContext.ColorMuted, "Type at least 2 characters (or an item ID) to search all FFXIV items.");
        }

        ImGui.Spacing();

        var tableFlags = ImGuiTableFlags.Borders
            | ImGuiTableFlags.RowBg
            | ImGuiTableFlags.ScrollY
            | ImGuiTableFlags.Resizable
            | ImGuiTableFlags.Sortable;
        if (!ImGui.BeginTable("ItemSearchTable", 6, tableFlags, new Vector2(0, -1)))
        {
            return;
        }

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.DefaultSort, 1.25f, 0);
        ImGui.TableSetupColumn("Where", ImGuiTableColumnFlags.WidthFixed, 118f * scale, 1);
        ImGui.TableSetupColumn("Destination", ImGuiTableColumnFlags.WidthFixed, 108f * scale, 2);
        ImGui.TableSetupColumn("Market", ImGuiTableColumnFlags.WidthFixed, 105f * scale, 3);
        ImGui.TableSetupColumn("Why", ImGuiTableColumnFlags.WidthStretch, 1.45f, 4);
        ImGui.TableSetupColumn("Rule", ImGuiTableColumnFlags.WidthFixed, 134f * scale, 5);
        ImGui.TableHeadersRow();

        var sortSpecs = ImGui.TableGetSortSpecs();
        if (sortSpecs.SpecsCount > 0 && sortSpecs.SpecsDirty)
        {
            var spec = sortSpecs.Specs;
            sortColumnIndex = spec.ColumnIndex;
            sortDescending = spec.SortDirection == ImGuiSortDirection.Descending;
            sortSpecs.SpecsDirty = false;
            RebuildDisplayRows(filter, q, world, retainers, entries, overrides, priorities, nowUtc);
            rows = cachedDisplayRows;
        }

        var clipper = ImGui.ImGuiListClipper();
        clipper.Begin(rows.Count);
        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var entry = rows[i];
                ImGui.PushID((int)entry.ItemId);
                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(entry.Name);
                if (ctx.MaterialTracker.IsItemTracked(entry.ItemId))
                {
                    ImGui.SameLine(0f, 6f * scale);
                    ImGui.TextColored(RoutePresentation.ColorTracked, "[Tracked]");
                }

                ImGui.TableSetColumnIndex(1);
                var whereText = cachedLocationsMap.GetValueOrDefault(entry.ItemId, "Not owned");
                ImGui.TextColored(whereText == "Not owned" ? SettingsUiContext.ColorMuted : new Vector4(0.86f, 0.88f, 0.92f, 1f), whereText);
                if (ImGui.IsItemHovered() && whereText.Length > 14)
                {
                    ImGui.SetTooltip(whereText);
                }

                ImGui.TableSetColumnIndex(2);
                var routeDisplay = RoutePresentation.FormatDestinationLabel(entry);
                ImGui.TextColored(RoutePresentation.GetRouteColor(entry.Route), routeDisplay);

                ImGui.TableSetColumnIndex(3);
                if (!entry.Untradable && !string.IsNullOrEmpty(world) && ctx.Universalis.TryGetAnyCachedPrice(world, entry.ItemId, out var snap))
                {
                    if (snap.ListingCount == 0)
                    {
                        ImGui.TextColored(SettingsUiContext.ColorGold, "0 listed");
                    }
                    else
                    {
                        var minP = snap.MinPriceNq > 0 ? snap.MinPriceNq : (snap.MinPriceHq > 0 ? snap.MinPriceHq : snap.MinPriceAny);
                        ImGui.TextUnformatted($"{minP:N0}g | {snap.RegularSaleVelocity:F1}/d");
                    }
                }
                else if (entry.Untradable)
                {
                    ImGui.TextColored(SettingsUiContext.ColorMuted, "Untradable");
                }
                else if (ImGui.SmallButton("Check"))
                {
                    ctx.Universalis.QueueMissing(world, [entry.ItemId], forceRefresh: true);
                }

                ImGui.TableSetColumnIndex(4);
                ImGui.TextColored(SettingsUiContext.ColorMuted, entry.Reason);
                if (ImGui.IsItemHovered() && !string.IsNullOrEmpty(entry.Reason))
                {
                    ImGui.SetTooltip(entry.Reason);
                }

                ImGui.TableSetColumnIndex(5);
                DrawOverrideCombo(entry.ItemId, retainers, overrides, priorities);

                ImGui.PopID();
            }
        }

        clipper.Destroy();
        ImGui.EndTable();
    }

    private void RebuildDisplayRows(
        string filter,
        string q,
        string world,
        IReadOnlyList<KnownRetainerInfo> retainers,
        IReadOnlyDictionary<uint, MagicSortEntry> entries,
        Dictionary<uint, string> overrides,
        Dictionary<uint, int> priorities,
        DateTime nowUtc)
    {
        lastRowsFilter = selectedFilterIndex;
        lastRowsQuery = q;
        lastRowsSortCol = sortColumnIndex;
        lastRowsSortDesc = sortDescending;
        lastRowsEntryCount = entries.Count;
        lastRowsBuiltUtc = nowUtc;

        List<MagicSortEntry> rows;
        if (filter == "All Game Items")
        {
            rows = gameSearchIndex.Query(q, nowUtc, id => ctx.TriageEngine.ClassifySingleItemOnDemand(id, queuePriceIfMissing: false));
        }
        else
        {
            rows = new List<MagicSortEntry>(entries.Count);
            foreach (var entry in entries.Values)
            {
                if (MatchesOwnedFilter(entry, filter, overrides) && MatchesSearchQuery(entry, q))
                {
                    rows.Add(entry);
                }
            }

            if (filter == "Custom Rules")
            {
                foreach (var unownedEntry in BuildUnownedCustomRuleEntries(entries, overrides))
                {
                    if (MatchesSearchQuery(unownedEntry, q))
                    {
                        rows.Add(unownedEntry);
                    }
                }
            }
        }

        SortItemRows(rows, world, retainers, overrides, priorities);
        cachedDisplayRows = rows;
    }

    private static bool MatchesSearchQuery(MagicSortEntry entry, string trimmedQuery)
    {
        if (string.IsNullOrEmpty(trimmedQuery))
        {
            return true;
        }

        return entry.Name.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase)
            || entry.ItemId.ToString().Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase);
    }

    private List<MagicSortEntry> BuildUnownedCustomRuleEntries(
        IReadOnlyDictionary<uint, MagicSortEntry> ownedEntries,
        Dictionary<uint, string> overrides)
    {
        var now = DateTime.UtcNow;
        if (overrides.Count != lastCustomRuleCount
            || (now - lastUnownedCustomRulesUtc).TotalMilliseconds >= 1500.0)
        {
            lastCustomRuleCount = overrides.Count;
            lastUnownedCustomRulesUtc = now;
            cachedUnownedCustomRuleEntries = new List<MagicSortEntry>();
            foreach (var itemId in overrides.Keys)
            {
                if (ownedEntries.ContainsKey(itemId))
                {
                    continue;
                }

                var classified = ctx.TriageEngine.ClassifySingleItemOnDemand(itemId, queuePriceIfMissing: false);
                if (classified != null)
                {
                    cachedUnownedCustomRuleEntries.Add(classified);
                }
            }
        }

        return cachedUnownedCustomRuleEntries;
    }

    private void SortItemRows(
        List<MagicSortEntry> rows,
        string world,
        IReadOnlyList<KnownRetainerInfo> retainers,
        Dictionary<uint, string> overrides,
        Dictionary<uint, int> priorities)
    {
        rows.Sort((a, b) =>
        {
            var cmp = sortColumnIndex switch
            {
                1 => string.Compare(
                    cachedLocationsMap.GetValueOrDefault(a.ItemId, string.Empty),
                    cachedLocationsMap.GetValueOrDefault(b.ItemId, string.Empty),
                    StringComparison.OrdinalIgnoreCase),
                2 => string.Compare(RoutePresentation.FormatDestinationLabel(a), RoutePresentation.FormatDestinationLabel(b), StringComparison.OrdinalIgnoreCase),
                3 => GetSortableMarketPrice(a, world).CompareTo(GetSortableMarketPrice(b, world)),
                4 => string.Compare(a.Reason, b.Reason, StringComparison.OrdinalIgnoreCase),
                5 => string.Compare(
                    FormatOverrideLabel(a.ItemId, overrides.GetValueOrDefault(a.ItemId, string.Empty), retainers, priorities),
                    FormatOverrideLabel(b.ItemId, overrides.GetValueOrDefault(b.ItemId, string.Empty), retainers, priorities),
                    StringComparison.OrdinalIgnoreCase),
                _ => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
            };

            if (cmp == 0)
            {
                cmp = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            }

            return sortDescending ? -cmp : cmp;
        });
    }

    private int GetSortableMarketPrice(MagicSortEntry entry, string world)
    {
        if (entry.Untradable)
        {
            return -2;
        }

        if (string.IsNullOrEmpty(world) || !ctx.Universalis.TryGetAnyCachedPrice(world, entry.ItemId, out var snap))
        {
            return -1;
        }

        if (snap.ListingCount == 0)
        {
            return 0;
        }

        return snap.MinPriceNq > 0 ? snap.MinPriceNq : (snap.MinPriceHq > 0 ? snap.MinPriceHq : snap.MinPriceAny);
    }

    private static bool MatchesOwnedFilter(MagicSortEntry entry, string filter, Dictionary<uint, string> overrides)
    {
        return filter switch
        {
            "All Owned" => true,
            "Retainers" => entry.Route.Kind == RouteKind.Retainer,
            "Market" => entry.Route.Kind == RouteKind.Market,
            "Vendor" => entry.Route.Kind == RouteKind.Vendor,
            "GC Seals" => entry.Route.Kind == RouteKind.GrandCompany,
            "Gold Saucer" => entry.Route.Kind == RouteKind.GoldSaucer,
            "Extract Materia" => entry.Route.Kind == RouteKind.Materia,
            "Use" => entry.Route.Kind == RouteKind.Use,
            "Armoire" => entry.Route.Kind == RouteKind.Armoire,
            "Glamour Dresser" => entry.Route.Kind == RouteKind.Glamour,
            "Keep" => entry.Route.Kind == RouteKind.Keep,
            "Custom Rules" => overrides.ContainsKey(entry.ItemId),
            _ => ItemRoute.TryParse(filter, out var parsed) && entry.Route.Kind == parsed.Kind,
        };
    }

    private void DrawOverrideCombo(
        uint itemId,
        IReadOnlyList<KnownRetainerInfo> retainers,
        Dictionary<uint, string> overrides,
        Dictionary<uint, int> priorities)
    {
        var currentVal = overrides.GetValueOrDefault(itemId, string.Empty);
        ItemRoute? currentRoute = ItemRoute.TryParse(currentVal, out var parsedRoute) ? parsedRoute : null;
        var currentPrio = priorities.GetValueOrDefault(itemId, 0);
        var currentLabel = FormatOverrideLabel(itemId, currentVal, retainers, priorities);

        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo("##ov", currentLabel))
        {
            return;
        }

        var itemRow = ctx.DataManager.GetExcelSheet<Item>()?.GetRowOrDefault(itemId);
        var eligibility = ItemRouteEligibility.Compute(itemRow, ctx.TriageEngine.IsCabinetItem(itemId));
        DrawOverrideOption(itemId, null, null, "Auto", currentRoute, currentPrio);
        DrawOverrideOption(itemId, ItemRoute.Keep, null, "Keep", currentRoute, currentPrio);

        if (eligibility.CanUse)
        {
            DrawOverrideOption(itemId, ItemRoute.Use, null, "Use", currentRoute, currentPrio);
        }

        if (eligibility.CanMarket)
        {
            DrawOverrideOption(itemId, ItemRoute.Market, 1, "Market (High)", currentRoute, currentPrio);
            DrawOverrideOption(itemId, ItemRoute.Market, 0, "Market", currentRoute, currentPrio);
            DrawOverrideOption(itemId, ItemRoute.Market, -1, "Market (Low)", currentRoute, currentPrio);
        }

        if (eligibility.CanVendor)
        {
            DrawOverrideOption(itemId, ItemRoute.Vendor, null, "Vendor", currentRoute, currentPrio);
        }

        if (eligibility.CanGrandCompany)
        {
            DrawOverrideOption(itemId, ItemRoute.GrandCompany, null, "GC Seals", currentRoute, currentPrio);
        }

        if (eligibility.CanGoldSaucer)
        {
            DrawOverrideOption(itemId, ItemRoute.GoldSaucer, null, "Gold Saucer", currentRoute, currentPrio);
        }

        if (eligibility.CanGlamour)
        {
            DrawOverrideOption(itemId, ItemRoute.Glamour, null, "Glamour Dresser", currentRoute, currentPrio);
        }

        if (eligibility.CanArmoire)
        {
            DrawOverrideOption(itemId, ItemRoute.Armoire, null, "Armoire", currentRoute, currentPrio);
        }

        foreach (var ret in retainers)
        {
            DrawOverrideOption(itemId, ItemRoute.ForRetainer(ret.RetainerId), null, ret.Name, currentRoute, currentPrio);
        }

        ImGui.EndCombo();
    }

    private void DrawOverrideOption(uint itemId, ItemRoute? route, int? marketPriority, string label, ItemRoute? currentRoute, int currentPrio)
    {
        var selected = route == currentRoute
            && (!marketPriority.HasValue || marketPriority.Value == currentPrio);
        var routeKey = route?.ToConfigString() ?? "Auto";
        var optionId = marketPriority.HasValue ? $"{routeKey}_{marketPriority.Value}" : routeKey;
        if (ImGui.Selectable($"{label}##ov_{optionId}", selected))
        {
            ctx.MagicSort.SetItemOverride(itemId, route, marketPriority);
            ctx.InvalidateLocalCaches();
        }
    }

    private static string FormatOverrideLabel(
        uint itemId,
        string value,
        IReadOnlyList<KnownRetainerInfo> retainers,
        Dictionary<uint, int> priorities)
    {
        return RoutePresentation.FormatOverrideSpec(
            value,
            rid => retainers.FirstOrDefault(r => r.RetainerId == rid)?.Name,
            priorities.GetValueOrDefault(itemId, 0));
    }
}
