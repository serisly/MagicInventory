using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using MagicInventory.Core;
using MagicInventory.Services;
using MagicInventory.Services.Triage;

namespace MagicInventory.UI;

public sealed class SettingsWindow : Window
{
    private static readonly (uint RowId, string FallbackName)[] SpecialtyCategoryDefs =
    [
        (ItemIdentity.UiCatIngredient, "Ingredient"),
        (ItemIdentity.UiCatSeafood, "Seafood"),
        (ItemIdentity.UiCatMeal, "Meal"),
        (ItemIdentity.UiCatMedicine, "Medicine"),
        (ItemIdentity.UiCatReagent, "Reagent"),
        (ItemIdentity.UiCatCatalyst, "Catalyst"),
        (ItemIdentity.UiCatGardening, "Gardening"),
        (ItemIdentity.UiCatFishingTackle, "Fishing Tackle"),
        (ItemIdentity.UiCatStone, "Stone"),
        (ItemIdentity.UiCatMetal, "Metal"),
        (ItemIdentity.UiCatLumber, "Lumber"),
        (ItemIdentity.UiCatCloth, "Cloth"),
        (ItemIdentity.UiCatLeather, "Leather"),
        (ItemIdentity.UiCatBone, "Bone"),
        (ItemIdentity.UiCatPart, "Part"),
        (ItemIdentity.UiCatMateria, "Materia"),
        (ItemIdentity.UiCatDye, "Dye"),
        (ItemIdentity.UiCatFurnishing, "Furnishing"),
        (ItemIdentity.UiCatDemimateria, "Demimateria"),
        (ItemIdentity.UiCatMiscellany, "Miscellany"),
    ];

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

    private static readonly Vector4 ColorMuted = new(0.68f, 0.70f, 0.74f, 1f);
    private static readonly Vector4 ColorGood = new(0.55f, 0.88f, 0.58f, 1f);
    private static readonly Vector4 ColorGold = new(0.95f, 0.82f, 0.44f, 1f);
    private static readonly Vector4 ColorWarn = new(0.95f, 0.55f, 0.45f, 1f);

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IDataManager dataManager;
    private readonly Configuration config;
    private readonly HoldingsService holdings;
    private readonly UniversalisClient universalis;
    private readonly ItemTriageEngine triageEngine;
    private readonly MagicSortService magicSort;
    private readonly MaterialTrackerService materialTracker;
    private readonly System.Action? onResetTrackerPosition;
    private readonly (uint RowId, string Name)[] specialtyCategories;
    private readonly Dictionary<uint, string> uiCategoryNamesById;

    private string searchQuery = string.Empty;
    private int selectedFilterIndex;
    private string pendingGameSearchQuery = string.Empty;
    private DateTime lastGameSearchInputUtc = DateTime.MinValue;
    private string lastExecutedGameSearchQuery = string.Empty;
    private (uint RowId, string Name)[]? allTaggableItemsIndex;
    private List<uint> cachedGameSearchIds = new();
    private List<MagicSortEntry> cachedGameSearchResults = new();
    private DateTime lastGameSearchClassifiedUtc = DateTime.MinValue;
    private List<MagicSortEntry> cachedUnownedCustomRuleEntries = new();
    private DateTime lastUnownedCustomRulesUtc = DateTime.MinValue;
    private int lastCustomRuleCount = -1;
    private DateTime lastLocationsMapUtc = DateTime.MinValue;
    private Dictionary<uint, string> cachedLocationsMap = new();
    private ulong lastContentId;
    private int sortColumnIndex;
    private bool sortDescending;

    private List<MagicSortEntry> cachedDisplayRows = new();
    private string lastRowsQuery = string.Empty;
    private int lastRowsFilter = -1;
    private int lastRowsSortCol = -1;
    private bool lastRowsSortDesc;
    private int lastRowsEntryCount = -1;
    private DateTime lastRowsBuiltUtc = DateTime.MinValue;

    private DateTime lastPluginCheckUtc = DateTime.MinValue;
    private bool cachedHasPennyPincher;
    private bool cachedHasLifestream;

    public SettingsWindow(
        IDalamudPluginInterface pluginInterface,
        IDataManager dataManager,
        Configuration config,
        HoldingsService holdings,
        UniversalisClient universalis,
        ItemTriageEngine triageEngine,
        MagicSortService magicSort,
        MaterialTrackerService materialTracker,
        System.Action? onResetTrackerPosition = null)
        : base("Magic Inventory###MagicInventorySettings")
    {
        this.pluginInterface = pluginInterface;
        this.dataManager = dataManager;
        this.config = config;
        this.holdings = holdings;
        this.universalis = universalis;
        this.triageEngine = triageEngine;
        this.magicSort = magicSort;
        this.materialTracker = materialTracker;
        this.onResetTrackerPosition = onResetTrackerPosition;
        specialtyCategories = BuildLocalizedSpecialtyCategories(dataManager);
        uiCategoryNamesById = BuildUiCategoryNameMap(dataManager);

        Size = new Vector2(800, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    private static (uint RowId, string Name)[] BuildLocalizedSpecialtyCategories(IDataManager dataManager)
    {
        var sheet = dataManager.GetExcelSheet<ItemUICategory>();
        var list = new (uint RowId, string Name)[SpecialtyCategoryDefs.Length];
        for (var i = 0; i < SpecialtyCategoryDefs.Length; i++)
        {
            var (rowId, fallback) = SpecialtyCategoryDefs[i];
            var localized = sheet?.GetRowOrDefault(rowId)?.Name.ExtractText();
            list[i] = (rowId, string.IsNullOrWhiteSpace(localized) ? fallback : localized);
        }

        return list;
    }

    private static Dictionary<uint, string> BuildUiCategoryNameMap(IDataManager dataManager)
    {
        var map = new Dictionary<uint, string>(128);
        var sheet = dataManager.GetExcelSheet<ItemUICategory>();
        if (sheet == null)
        {
            return map;
        }

        foreach (var row in sheet)
        {
            if (row.RowId == 0)
            {
                continue;
            }

            var name = row.Name.ExtractText();
            if (!string.IsNullOrWhiteSpace(name))
            {
                map[row.RowId] = name;
            }
        }

        return map;
    }

    private void InvalidateLocalCaches()
    {
        lastLocationsMapUtc = DateTime.MinValue;
        lastGameSearchClassifiedUtc = DateTime.MinValue;
        lastUnownedCustomRulesUtc = DateTime.MinValue;
        lastCustomRuleCount = -1;
        lastRowsBuiltUtc = DateTime.MinValue;
    }

    public override void PreDraw()
    {
        var scale = ImGuiHelpers.GlobalScale;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(640f * scale, 440f * scale),
            MaximumSize = new Vector2(1450f * scale, 980f * scale),
        };
    }

    public override void Draw()
    {
        var cid = InventorySafetyEvaluator.GetCurrentContentId();
        if (cid != lastContentId)
        {
            lastContentId = cid;
            cachedLocationsMap.Clear();
            cachedGameSearchResults.Clear();
            cachedUnownedCustomRuleEntries.Clear();
            cachedDisplayRows.Clear();
            InvalidateLocalCaches();
        }

        if (!ImGui.BeginTabBar("MagicInventoryTabs"))
        {
            return;
        }

        if (ImGui.BeginTabItem("Overview"))
        {
            DrawOverviewTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Items"))
        {
            DrawItemSearchTab(cid);
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Retainers"))
        {
            DrawRetainersTab(cid);
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Settings"))
        {
            DrawSettingsTab(cid);
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void DrawOverviewTab()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var entries = triageEngine.GetCurrentEntries();
        var world = triageEngine.ActiveWorldName;
        var dc = triageEngine.ActiveDataCenterName;
        var retainers = holdings.GetKnownRetainersForCurrentCharacter();

        ImGui.TextUnformatted($"World: {(string.IsNullOrEmpty(world) ? "Not logged in" : $"{world} ({dc})")}");
        ImGui.SameLine(0, 20f * scale);
        ImGui.TextColored(ColorMuted, $"Cached prices: {universalis.CachedEntryCount:N0}");
        ImGui.SameLine(0, 20f * scale);
        ImGui.TextColored(ColorGold, $"Sell mode: {FormatSellModeName(config.SellMode)}");

        if (universalis.LastScanUtc != DateTime.MinValue)
        {
            var age = DateTime.UtcNow - universalis.LastScanUtc;
            var ageText = age.TotalMinutes < 1
                ? "just now"
                : (age.TotalHours < 1 ? $"{(int)age.TotalMinutes}m ago" : $"{age.TotalHours:F1}h ago");
            ImGui.TextColored(ColorMuted, $"Prices updated {ageText}");
        }

        ImGui.Spacing();

        var isScanning = universalis.IsScanning;
        if (isScanning)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button("Refresh Market Prices", new Vector2(185f * scale, 30f * scale)))
        {
            triageEngine.TriggerFullScan(forceRefreshPrices: true);
        }

        if (isScanning)
        {
            ImGui.EndDisabled();
            var total = Math.Max(1, universalis.TotalQueuedInScan);
            var done = Math.Clamp(universalis.CompletedInScan, 0, total);
            ImGui.SameLine();
            ImGui.ProgressBar((float)done / total, new Vector2(220f * scale, 26f * scale), $"Checking {done}/{total}...");
        }
        else
        {
            ImGui.SameLine();
            if (ImGui.Button("Rescan Bags", new Vector2(125f * scale, 30f * scale)))
            {
                triageEngine.Invalidate();
                magicSort.InvalidateButtonCache();
                InvalidateLocalCaches();
            }
        }

        if (!string.IsNullOrEmpty(universalis.LastError))
        {
            ImGui.TextColored(ColorWarn, $"Market data notice: {universalis.LastError}");
        }

        var unvisited = retainers.Where(r => !r.IsVisited).Select(r => r.Name).ToList();
        if (retainers.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(ColorGold, "Open a Summoning Bell once so Magic Inventory can find your retainers.");
        }
        else if (unvisited.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(ColorGold, $"Visit {string.Join(", ", unvisited)} once to see what's inside their bags.");
        }
        else
        {
            var totalSlots = retainers.Count * HoldingsService.MaxRetainerBagSlots;
            var usedSlots = retainers.Sum(r => r.UsedBagSlots);
            var pct = totalSlots > 0 ? (usedSlots * 100) / totalSlots : 0;
            if (pct >= 88)
            {
                ImGui.Spacing();
                ImGui.TextColored(
                    ColorWarn,
                    $"Retainers are {pct}% full ({usedSlots}/{totalSlots} slots). Try Balanced or Clear Space in Settings to free room.");
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries.Values)
        {
            counts[e.Route] = counts.GetValueOrDefault(e.Route) + 1;
        }

        ImGui.TextUnformatted($"Your Items ({entries.Count})");
        if (ImGui.BeginTable("RouteSummaryTable", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Destination", ImGuiTableColumnFlags.WidthFixed, 130f * scale);
            ImGui.TableSetupColumn("Items", ImGuiTableColumnFlags.WidthFixed, 62f * scale);
            ImGui.TableSetupColumn("What Happens", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();

            DrawSummaryRow("Retainers", counts.GetValueOrDefault("Retainer"), "Stored in your retainers with /m sort");
            DrawSummaryRow("Market", counts.GetValueOrDefault("Market"), "Prepped or listed on the Market Board with /m sell");
            DrawSummaryRow("Vendor", counts.GetValueOrDefault("Vendor"), "Sold to any Gil vendor with /m sell");
            DrawSummaryRow("GC Seals", counts.GetValueOrDefault("GrandCompany"), "Turned in for GC Seals with /m sell");
            DrawSummaryRow("Gold Saucer", counts.GetValueOrDefault("GoldSaucer"), "Owned duplicate Triple Triad cards to trade for MGP");
            DrawSummaryRow("Extract Materia", counts.GetValueOrDefault("Materia"), "100% Spiritbond gear (extract materia in Materialize)");
            DrawSummaryRow("Use", counts.GetValueOrDefault("Use"), "Untradable unowned unlockables & MGP cards (never Gear Coffers)");
            DrawSummaryRow("Armoire", counts.GetValueOrDefault("Armoire"), "Stored in your Inn Armoire");
            DrawSummaryRow("Glamour Dresser", counts.GetValueOrDefault("Glamour"), "Lv. 1 glamour gear for your Inn Glamour Dresser");
            DrawSummaryRow("Keep", counts.GetValueOrDefault("Keep"), "Stays where it is (Bag, Armoury, or Equipped)");

            ImGui.EndTable();
        }

        if (materialTracker.TrackedItemCount > 0)
        {
            ImGui.TextColored(
                RoutePresentation.ColorTracked,
                $"Tracked materials: {materialTracker.TrackedItemCount} item(s) protected from selling and pulled from retainers.");
        }

        if (triageEngine.PendingPriceCount > 0)
        {
            ImGui.TextColored(ColorGold, $"{triageEngine.PendingPriceCount} item(s) waiting for Market Board prices (kept safe until checked).");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("Commands");
        ImGui.BulletText("/m  or  /magic          - Open or close this window");
        ImGui.BulletText("/m sort                 - Sort Bags <-> Armoury, or sort Retainers at a Summoning Bell");
        ImGui.BulletText("/m sell                 - Sell at a Vendor, Grand Company, or Summoning Bell");
        ImGui.BulletText("/m buy                  - Buy tracked recipe mats at a Market Board");
        ImGui.BulletText("/m track <recipe> [qty] - Track a crafting recipe (e.g. /m track fish soup 5)");
        ImGui.BulletText("/m use                  - Use unowned untradable unlockables & MGP cards in your bags");
        ImGui.BulletText("/m stop                 - Cancel any running sort, sell, buy, use, or Quick Synth action");
        ImGui.BulletText("/m scan                 - Refresh Market Board prices");
        ImGui.BulletText("/m status               - Print current inventory, rule, and tracker counts");
    }

    private static string FormatSellModeName(SellMode mode) => mode switch
    {
        SellMode.Safe => "Safe",
        SellMode.Balanced => "Balanced",
        SellMode.Purge => "Clear Space",
        _ => "Safe",
    };

    private static void DrawSummaryRow(string route, int count, string desc)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted(route);
        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(count.ToString());
        ImGui.TableSetColumnIndex(2);
        ImGui.TextColored(ColorMuted, desc);
    }

    private void DrawItemSearchTab(ulong cid)
    {
        var scale = ImGuiHelpers.GlobalScale;
        ImGui.SetNextItemWidth(260f * scale);
        ImGui.InputTextWithHint("##ItemSearchInput", "Search item name or ID...", ref searchQuery, 128);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(175f * scale);
        ImGui.Combo("##ItemFilterCombo", ref selectedFilterIndex, FilterOptions, FilterOptions.Length);

        var filter = FilterOptions[selectedFilterIndex];
        var world = triageEngine.ActiveWorldName;
        var retainers = holdings.GetKnownRetainersForCurrentCharacter();
        var entries = triageEngine.GetCurrentEntries();
        var overrides = config.GetManualRouteOverrides(cid);
        var priorities = config.GetMarketListingPriorities(cid);
        var q = searchQuery.Trim();

        var nowUtc = DateTime.UtcNow;
        if ((nowUtc - lastLocationsMapUtc).TotalSeconds >= 2.0)
        {
            lastLocationsMapUtc = nowUtc;
            cachedLocationsMap = holdings.BuildOwnedLocationsMap();
        }

        var pendingAllItemsDebounceReady = filter == "All Game Items"
            && (q.Length >= 2 || uint.TryParse(q, out _))
            && !string.Equals(q, lastExecutedGameSearchQuery, StringComparison.OrdinalIgnoreCase)
            && (nowUtc - lastGameSearchInputUtc).TotalMilliseconds >= 200.0;

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
        ImGui.TextColored(ColorMuted, $"{rows.Count} shown");

        if (filter == "All Game Items" && q.Length < 2 && !uint.TryParse(q, out _))
        {
            ImGui.TextColored(ColorMuted, "Type at least 2 characters (or an item ID) to search all FFXIV items.");
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
                if (materialTracker.IsItemTracked(entry.ItemId))
                {
                    ImGui.SameLine(0f, 6f * scale);
                    ImGui.TextColored(RoutePresentation.ColorTracked, "[Tracked]");
                }

                ImGui.TableSetColumnIndex(1);
                var whereText = cachedLocationsMap.GetValueOrDefault(entry.ItemId, "Not owned");
                ImGui.TextColored(whereText == "Not owned" ? ColorMuted : new Vector4(0.86f, 0.88f, 0.92f, 1f), whereText);
                if (ImGui.IsItemHovered() && whereText.Length > 14)
                {
                    ImGui.SetTooltip(whereText);
                }

                ImGui.TableSetColumnIndex(2);
                var routeDisplay = RoutePresentation.FormatDestinationLabel(entry);
                ImGui.TextColored(GetRouteColor(entry.Route), routeDisplay);

                ImGui.TableSetColumnIndex(3);
                if (!entry.Untradable && !string.IsNullOrEmpty(world) && universalis.TryGetAnyCachedPrice(world, entry.ItemId, out var snap))
                {
                    if (snap.ListingCount == 0)
                    {
                        ImGui.TextColored(ColorGold, "0 listed");
                    }
                    else
                    {
                        var minP = snap.MinPriceNq > 0 ? snap.MinPriceNq : (snap.MinPriceHq > 0 ? snap.MinPriceHq : snap.MinPriceAny);
                        ImGui.TextUnformatted($"{minP:N0}g | {snap.RegularSaleVelocity:F1}/d");
                    }
                }
                else if (entry.Untradable)
                {
                    ImGui.TextColored(ColorMuted, "Untradable");
                }
                else if (ImGui.SmallButton("Check"))
                {
                    universalis.QueueMissing(world, [entry.ItemId], forceRefresh: true);
                }

                ImGui.TableSetColumnIndex(4);
                ImGui.TextColored(ColorMuted, entry.Reason);
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
            rows = BuildAllGameItemsResults(q, nowUtc);
        }
        else
        {
            rows = new List<MagicSortEntry>(entries.Count);
            foreach (var entry in entries.Values)
            {
                if (!MatchesOwnedFilter(entry, filter, overrides))
                {
                    continue;
                }

                if (!MatchesSearchQuery(entry, q))
                {
                    continue;
                }

                rows.Add(entry);
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

                var classified = triageEngine.ClassifySingleItemOnDemand(itemId, queuePriceIfMissing: false);
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

        if (string.IsNullOrEmpty(world) || !universalis.TryGetAnyCachedPrice(world, entry.ItemId, out var snap))
        {
            return -1;
        }

        if (snap.ListingCount == 0)
        {
            return 0;
        }

        return snap.MinPriceNq > 0 ? snap.MinPriceNq : (snap.MinPriceHq > 0 ? snap.MinPriceHq : snap.MinPriceAny);
    }

    private void EnsureAllTaggableItemsIndex()
    {
        if (allTaggableItemsIndex != null)
        {
            return;
        }

        var sheet = dataManager.GetExcelSheet<Item>();
        if (sheet == null)
        {
            allTaggableItemsIndex = Array.Empty<(uint, string)>();
            return;
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

        allTaggableItemsIndex = list.ToArray();
    }

    private List<MagicSortEntry> BuildAllGameItemsResults(string q, DateTime now)
    {
        var results = new List<MagicSortEntry>();
        if (q.Length < 2 && !uint.TryParse(q, out _))
        {
            return results;
        }

        if (!string.Equals(q, pendingGameSearchQuery, StringComparison.OrdinalIgnoreCase))
        {
            pendingGameSearchQuery = q;
            lastGameSearchInputUtc = now;
        }

        var queryNeedsExecution = !string.Equals(q, lastExecutedGameSearchQuery, StringComparison.OrdinalIgnoreCase);
        if (queryNeedsExecution && (now - lastGameSearchInputUtc).TotalMilliseconds >= 200.0)
        {
            lastExecutedGameSearchQuery = q;
            EnsureAllTaggableItemsIndex();
            cachedGameSearchIds = new List<uint>(64);
            var index = allTaggableItemsIndex!;
            var parsedId = uint.TryParse(q, out var exactId) ? exactId : 0u;

            for (var i = 0; i < index.Length; i++)
            {
                var (rowId, itemName) = index[i];
                if (parsedId > 0 && rowId == parsedId)
                {
                    cachedGameSearchIds.Add(rowId);
                    break;
                }

                if (itemName.Contains(q, StringComparison.OrdinalIgnoreCase))
                {
                    cachedGameSearchIds.Add(rowId);
                    if (cachedGameSearchIds.Count >= 80)
                    {
                        break;
                    }
                }
            }

            lastGameSearchClassifiedUtc = DateTime.MinValue;
        }

        if ((now - lastGameSearchClassifiedUtc).TotalMilliseconds >= 1500.0)
        {
            lastGameSearchClassifiedUtc = now;
            cachedGameSearchResults = new List<MagicSortEntry>(cachedGameSearchIds.Count);
            foreach (var id in cachedGameSearchIds)
            {
                var classified = triageEngine.ClassifySingleItemOnDemand(id, queuePriceIfMissing: false);
                if (classified != null)
                {
                    cachedGameSearchResults.Add(classified);
                }
            }
        }

        results.AddRange(cachedGameSearchResults);
        return results;
    }

    private static bool MatchesOwnedFilter(MagicSortEntry entry, string filter, Dictionary<uint, string> overrides)
    {
        return filter switch
        {
            "All Owned" => true,
            "Retainers" => string.Equals(entry.Route, "Retainer", StringComparison.OrdinalIgnoreCase),
            "GC Seals" => string.Equals(entry.Route, "GrandCompany", StringComparison.OrdinalIgnoreCase),
            "Gold Saucer" => string.Equals(entry.Route, "GoldSaucer", StringComparison.OrdinalIgnoreCase),
            "Extract Materia" => string.Equals(entry.Route, "Materia", StringComparison.OrdinalIgnoreCase),
            "Glamour Dresser" => string.Equals(entry.Route, "Glamour", StringComparison.OrdinalIgnoreCase),
            "Custom Rules" => overrides.ContainsKey(entry.ItemId),
            _ => string.Equals(entry.Route, filter, StringComparison.OrdinalIgnoreCase),
        };
    }

    private void DrawOverrideCombo(
        uint itemId,
        IReadOnlyList<KnownRetainerInfo> retainers,
        Dictionary<uint, string> overrides,
        Dictionary<uint, int> priorities)
    {
        var currentVal = overrides.GetValueOrDefault(itemId, string.Empty);
        var currentPrio = priorities.GetValueOrDefault(itemId, 0);
        var currentLabel = FormatOverrideLabel(itemId, currentVal, retainers, priorities);

        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo("##ov", currentLabel))
        {
            return;
        }

        var itemRow = dataManager.GetExcelSheet<Item>()?.GetRowOrDefault(itemId);
        var eligibility = ItemRouteEligibility.Compute(itemRow, triageEngine.IsCabinetItem(itemId));
        DrawOverrideOption(itemId, string.Empty, null, "Auto", currentVal, currentPrio);
        DrawOverrideOption(itemId, "Keep", null, "Keep", currentVal, currentPrio);

        if (eligibility.CanUse)
        {
            DrawOverrideOption(itemId, "Use", null, "Use", currentVal, currentPrio);
        }

        if (eligibility.CanMarket)
        {
            DrawOverrideOption(itemId, "Market", 1, "Market (High)", currentVal, currentPrio);
            DrawOverrideOption(itemId, "Market", 0, "Market", currentVal, currentPrio);
            DrawOverrideOption(itemId, "Market", -1, "Market (Low)", currentVal, currentPrio);
        }

        if (eligibility.CanVendor)
        {
            DrawOverrideOption(itemId, "Vendor", null, "Vendor", currentVal, currentPrio);
        }

        if (eligibility.CanGrandCompany)
        {
            DrawOverrideOption(itemId, "GrandCompany", null, "GC Seals", currentVal, currentPrio);
        }

        if (eligibility.CanGoldSaucer)
        {
            DrawOverrideOption(itemId, "GoldSaucer", null, "Gold Saucer", currentVal, currentPrio);
        }

        if (eligibility.CanGlamour)
        {
            DrawOverrideOption(itemId, "Glamour", null, "Glamour Dresser", currentVal, currentPrio);
        }

        if (eligibility.CanArmoire)
        {
            DrawOverrideOption(itemId, "Armoire", null, "Armoire", currentVal, currentPrio);
        }

        foreach (var ret in retainers)
        {
            DrawOverrideOption(itemId, $"Retainer:{ret.RetainerId}", null, ret.Name, currentVal, currentPrio);
        }

        ImGui.EndCombo();
    }

    private void DrawOverrideOption(uint itemId, string value, int? marketPriority, string label, string currentVal, int currentPrio)
    {
        var selected = string.Equals(value, currentVal, StringComparison.OrdinalIgnoreCase)
            && (!marketPriority.HasValue || marketPriority.Value == currentPrio);
        var optionId = marketPriority.HasValue ? $"{value}_{marketPriority.Value}" : value;
        if (ImGui.Selectable($"{label}##ov_{optionId}", selected))
        {
            magicSort.SetItemOverride(itemId, value, marketPriority);
            InvalidateLocalCaches();
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

    private static Vector4 GetRouteColor(string route)
        => RoutePresentation.GetRouteColor(route);

    private void DrawRetainersTab(ulong cid)
    {
        var retainers = holdings.GetKnownRetainersForCurrentCharacter();
        var profile = config.GetOrCreateCharacter(cid);

        if (retainers.Count == 0)
        {
            ImGui.TextColored(ColorGold, "No retainers found yet for this character.");
            ImGui.TextColored(ColorMuted, "Open a Summoning Bell once and visit each retainer to see their bags.");
            return;
        }

        ImGui.TextWrapped("Pick which categories each retainer should hold. Anything unchecked follows where you already keep most of that type, and overflows to another retainer with free space when full.");
        ImGui.Spacing();

        var ownerByCatId = new Dictionary<uint, (ulong RetainerId, string Name)>();
        foreach (var r in retainers)
        {
            if (profile.RetainerSpecialtyCategoryIds.TryGetValue(r.RetainerId, out var catIds))
            {
                foreach (var catId in catIds)
                {
                    ownerByCatId[catId] = (r.RetainerId, r.Name);
                }
            }
        }

        foreach (var ret in retainers)
        {
            ImGui.PushID(ret.RetainerId.ToString());
            var status = !ret.IsVisited
                ? "Not visited yet"
                : (ret.UsedBagSlots >= HoldingsService.MaxRetainerBagSlots ? $"{ret.UsedBagSlots}/175 slots (FULL)" : $"{ret.UsedBagSlots}/175 slots");
            var header = $"{ret.Name}  ({status})  |  Market: {ret.MarketItemCount}/20###ret_{ret.RetainerId}";
            if (ImGui.CollapsingHeader(header, ImGuiTreeNodeFlags.DefaultOpen))
            {
                var drewTopLine = false;
                if (ret.CategoryCounts.Count > 0)
                {
                    var topCats = ret.CategoryCounts
                        .OrderByDescending(kv => kv.Value)
                        .Take(6)
                        .Select(kv =>
                        {
                            var catName = uiCategoryNamesById.GetValueOrDefault(kv.Key, $"Category #{kv.Key}");
                            return $"{catName} ({kv.Value})";
                        });
                    ImGui.TextColored(ColorMuted, $"Mostly holds: {string.Join(", ", topCats)}");
                    drewTopLine = true;
                }

                profile.RetainerSpecialtyCategoryIds.TryGetValue(ret.RetainerId, out var assigned);
                var hasCustom = assigned is { Count: > 0 };
                if (hasCustom)
                {
                    if (drewTopLine)
                    {
                        ImGui.SameLine();
                    }

                    if (ImGui.SmallButton("Reset to Auto"))
                    {
                        profile.RetainerSpecialtyCategoryIds.Remove(ret.RetainerId);
                        profile.RetainerSpecialties.Remove(ret.RetainerId);
                        config.Save(pluginInterface);
                        triageEngine.Invalidate();
                        magicSort.InvalidateButtonCache();
                        InvalidateLocalCaches();
                    }
                }

                ImGui.Spacing();
                const int cols = 4;
                if (ImGui.BeginTable($"SpecTable_{ret.RetainerId}", cols, ImGuiTableFlags.SizingStretchSame))
                {
                    for (var i = 0; i < specialtyCategories.Length; i++)
                    {
                        if (i % cols == 0)
                        {
                            ImGui.TableNextRow();
                        }

                        ImGui.TableSetColumnIndex(i % cols);
                        var (catId, cat) = specialtyCategories[i];
                        var isChecked = assigned != null && assigned.Contains(catId);
                        var ownedByOther = !isChecked
                            && ownerByCatId.TryGetValue(catId, out var otherOwner)
                            && otherOwner.RetainerId != ret.RetainerId;
                        var label = ownedByOther ? $"{cat}*" : cat;

                        if (ImGui.Checkbox($"{label}##spec_{catId}", ref isChecked))
                        {
                            var set = Configuration.GetOrCreateRetainerSpecialtyCategoryIds(profile, ret.RetainerId);
                            if (isChecked)
                            {
                                foreach (var otherRetId in profile.RetainerSpecialtyCategoryIds.Keys.ToList())
                                {
                                    if (otherRetId != ret.RetainerId)
                                    {
                                        profile.RetainerSpecialtyCategoryIds[otherRetId].Remove(catId);
                                    }
                                }

                                set.Add(catId);
                            }
                            else
                            {
                                set.Remove(catId);
                            }

                            config.Save(pluginInterface);
                            triageEngine.Invalidate();
                            magicSort.InvalidateButtonCache();
                            InvalidateLocalCaches();
                        }

                        if (ownedByOther && ImGui.IsItemHovered())
                        {
                            ImGui.SetTooltip($"Currently assigned to {ownerByCatId[catId].Name}. Checking this moves {cat} to {ret.Name}.");
                        }
                    }

                    ImGui.EndTable();
                }

                ImGui.Spacing();
            }

            ImGui.PopID();
        }
    }

    private void RefreshInstalledPluginsCache()
    {
        var now = DateTime.UtcNow;
        if ((now - lastPluginCheckUtc).TotalSeconds < 10.0)
        {
            return;
        }

        lastPluginCheckUtc = now;
        cachedHasPennyPincher = false;
        cachedHasLifestream = false;
        foreach (var p in pluginInterface.InstalledPlugins)
        {
            if (!p.IsLoaded)
            {
                continue;
            }

            if (string.Equals(p.InternalName, "PennyPincher", StringComparison.OrdinalIgnoreCase))
            {
                cachedHasPennyPincher = true;
            }
            else if (string.Equals(p.InternalName, "Lifestream", StringComparison.OrdinalIgnoreCase))
            {
                cachedHasLifestream = true;
            }
        }
    }

    private void DrawSettingsTab(ulong cid)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var inputWidth = 165f * scale;
        var indentWidth = 18f * scale;
        var triageChanged = false;
        var uiOnlyChanged = false;

        RefreshInstalledPluginsCache();

        ImGui.TextUnformatted("Sell Mode");
        ImGui.TextColored(ColorMuted, "Controls how aggressively /m sell tags items for Vendors, Grand Company, and the Market Board.");
        ImGui.TextColored(ColorMuted, "Note: switching Sell Mode resets the Advanced price thresholds below to that preset.");
        ImGui.Spacing();

        var mode = config.SellMode;
        if (ImGui.RadioButton("Safe: junk and clear Market wins only", mode == SellMode.Safe))
        {
            config.ApplySellModePresets(SellMode.Safe);
            triageChanged = true;
        }

        ImGui.TextColored(ColorMuted, "    Sells Allagan pieces, old white gear, cheap city-vendor clutter, green dungeon gear, and high-value Market items.");

        if (ImGui.RadioButton("Balanced: clear low-value clutter and good Market flips", mode == SellMode.Balanced))
        {
            config.ApplySellModePresets(SellMode.Balanced);
            triageChanged = true;
        }

        ImGui.TextColored(ColorMuted, "    Also turns in outgeared blue gear and sells cheap common drops while keeping crafting mats and rare items.");

        if (ImGui.RadioButton("Clear Space: sell cheap items you can easily re-buy", mode == SellMode.Purge))
        {
            config.ApplySellModePresets(SellMode.Purge);
            triageChanged = true;
        }

        ImGui.TextColored(ColorMuted, "    Liquidates cheap city-vendor mats and abundant low-price Market items to free bag & retainer slots.");
        ImGui.Spacing();
        ImGui.TextColored(ColorGood, "Always kept safe in every mode: tracked mats, gearsets, food, potions, glamour, housing, relics, and rare (0-listed) items.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("Market Board");
        ImGui.SetNextItemWidth(inputWidth);
        var undercut = config.MarketUndercutGil;
        if (ImGui.InputInt("Undercut by (Gil)", ref undercut, 1, 10))
        {
            config.MarketUndercutGil = Math.Clamp(undercut, 0, 10_000);
            triageChanged = true;
        }

        ImGui.SetNextItemWidth(inputWidth);
        var maxBuy = config.BuyMaxGilPerPurchase;
        if (ImGui.InputInt("Max Gil per Magic Buy purchase", ref maxBuy, 5_000, 25_000))
        {
            config.BuyMaxGilPerPurchase = Math.Clamp(maxBuy, 1_000, 10_000_000);
            uiOnlyChanged = true;
        }

        ImGui.SetNextItemWidth(inputWidth);
        var outlier = config.BuyOutlierMultiplier;
        if (ImGui.InputFloat("Skip listings above (x recent price)", ref outlier, 0.1f, 0.5f, "%.1f"))
        {
            config.BuyOutlierMultiplier = Math.Clamp(outlier, 1.2f, 10f);
            uiOnlyChanged = true;
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Market Listing Workflow");
        var workflowMode = config.MarketReviewBeforeConfirm
            ? 0
            : (config.MarketOneListingPerClick ? 1 : 2);

        if (ImGui.RadioButton("Review before confirming (Recommended)", workflowMode == 0))
        {
            config.MarketReviewBeforeConfirm = true;
            uiOnlyChanged = true;
        }

        ImGui.TextColored(ColorMuted, "    Prep Sell opens 1 item and fills the undercut price; you inspect and click Confirm yourself.");

        if (workflowMode == 0)
        {
            ImGui.Indent(indentWidth);
            var autoCompareInReview = config.MarketAutoComparePricesInReview;
            if (ImGui.Checkbox("Auto-open Compare Prices in Review Mode", ref autoCompareInReview))
            {
                config.MarketAutoComparePricesInReview = autoCompareInReview;
                uiOnlyChanged = true;
            }

            ImGui.Unindent(indentWidth);
        }

        if (ImGui.RadioButton("Auto-list 1 item per click", workflowMode == 1))
        {
            config.MarketReviewBeforeConfirm = false;
            config.MarketOneListingPerClick = true;
            uiOnlyChanged = true;
        }

        if (ImGui.RadioButton("Auto-list batch (up to 8 per click)", workflowMode == 2))
        {
            config.MarketReviewBeforeConfirm = false;
            config.MarketOneListingPerClick = false;
            uiOnlyChanged = true;
        }

        ImGui.Spacing();
        var autoFillSell = config.MarketAutoFillRetainerSell;
        if (ImGui.Checkbox("Auto-fill undercut price when opening Retainer Sell / Compare Prices manually", ref autoFillSell))
        {
            config.MarketAutoFillRetainerSell = autoFillSell;
            uiOnlyChanged = true;
        }

        ImGui.TextColored(ColorMuted, "    Sets the asking price from live Compare Prices (excluding your own retainers) or world history.");

        if (cachedHasPennyPincher && config.MarketAutoFillRetainerSell)
        {
            ImGui.TextColored(ColorGold, "    PennyPincher is also loaded. Turn off auto-fill in one of them so they do not fight over prices.");
        }

        var promptStale = config.MarketPromptStaleListings;
        if (ImGui.Checkbox("Prompt to remove or reprice stale / unrealistic active listings", ref promptStale))
        {
            config.MarketPromptStaleListings = promptStale;
            uiOnlyChanged = true;
        }

        ImGui.TextColored(ColorMuted, "    Flags dead, below-vendor, or heavily overpriced listings in Retainer Sell List.");

        ImGui.SetNextItemWidth(inputWidth);
        var dailyLimit = config.MarketDailyActionLimit;
        if (ImGui.InputInt("Daily auto-list / buy limit (0 = Unlimited)", ref dailyLimit, 5, 10))
        {
            config.MarketDailyActionLimit = Math.Clamp(dailyLimit, 0, 200);
            uiOnlyChanged = true;
        }

        var usedToday = config.GetUsedMarketActionsToday(cid);
        var dailyLimitNote = config.MarketDailyActionLimit <= 0
            ? $"    Used today (this character): {usedToday} (Unlimited). Review Mode never consumes daily quota."
            : $"    Used today (this character): {usedToday}/{config.MarketDailyActionLimit}. Review Mode never consumes daily quota.";
        ImGui.TextColored(ColorMuted, dailyLimitNote);

        var lifestreamOptIn = config.EnableLifestreamWorldHopPrompt;
        if (ImGui.Checkbox("Ask to travel via Lifestream if an item isn't listed on my Current World", ref lifestreamOptIn))
        {
            config.EnableLifestreamWorldHopPrompt = lifestreamOptIn;
            uiOnlyChanged = true;
        }

        ImGui.TextColored(
            cachedHasLifestream ? ColorGood : ColorMuted,
            cachedHasLifestream
                ? "    Lifestream installed (only travels after you click Travel)."
                : "    Lifestream not installed (shows cheapest DC world in chat instead).");

        ImGui.BeginDisabled(!config.EnableLifestreamWorldHopPrompt);
        ImGui.Indent(indentWidth);
        ImGui.SetNextItemWidth(inputWidth);
        var minWorldHopSavings = config.WorldHopMinSavingsGil;
        if (ImGui.InputInt("Min Gil savings to suggest world travel", ref minWorldHopSavings, 1_000, 5_000))
        {
            config.WorldHopMinSavingsGil = Math.Clamp(minWorldHopSavings, 0, 1_000_000);
            uiOnlyChanged = true;
        }

        ImGui.Unindent(indentWidth);
        ImGui.EndDisabled();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("Display");
        var hoverHighlights = config.EnableHoverHighlights;
        if (ImGui.Checkbox("Highlight matching items when hovering buttons or right-click options", ref hoverHighlights))
        {
            config.EnableHoverHighlights = hoverHighlights;
            uiOnlyChanged = true;
        }

        if (ImGui.Button("Reset Tracker Position"))
        {
            config.TrackerX = 24f;
            config.TrackerY = 120f;
            onResetTrackerPosition?.Invoke();
            uiOnlyChanged = true;
        }

        ImGui.SameLine();
        ImGui.TextColored(ColorMuted, "Moves the Material Tracker back onto the screen.");

        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Advanced price thresholds"))
        {
            ImGui.TextColored(
                ColorMuted,
                "High-value items scale liquidity automatically: 10k-50k stacks allow slower sales (up to 21d); 50k+ allow 0.10/day (up to 30d).");
            ImGui.Spacing();

            ImGui.SetNextItemWidth(inputWidth);
            var minStack = config.MarketMinStackValue;
            if (ImGui.InputInt("Min stack value for Market (Gil)", ref minStack, 100, 500))
            {
                config.MarketMinStackValue = Math.Clamp(minStack, 100, 1_000_000);
                triageChanged = true;
            }

            ImGui.SetNextItemWidth(inputWidth);
            var minUnit = config.MarketMinUnitPrice;
            if (ImGui.InputInt("Min single-item price for Market (Gil)", ref minUnit, 100, 500))
            {
                config.MarketMinUnitPrice = Math.Clamp(minUnit, 50, 1_000_000);
                triageChanged = true;
            }

            ImGui.SetNextItemWidth(inputWidth);
            var floorUnit = config.MarketMinFloorUnitPrice;
            if (ImGui.InputInt("Min unit floor for small stacks (Gil)", ref floorUnit, 10, 50))
            {
                config.MarketMinFloorUnitPrice = Math.Clamp(floorUnit, 1, 10_000);
                triageChanged = true;
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Blocks cheap single items from wasting MB slots. Bulk stacks meeting 'Min stack value for Market' are still allowed.");
            }

            ImGui.SetNextItemWidth(inputWidth);
            var minVel = config.MarketMinVelocity;
            if (ImGui.InputFloat("Min daily sales for Market", ref minVel, 0.05f, 0.25f, "%.2f"))
            {
                config.MarketMinVelocity = Math.Clamp(minVel, 0.01f, 50f);
                triageChanged = true;
            }

            ImGui.SetNextItemWidth(inputWidth);
            var maxDays = config.MarketMaxDaysSinceLastSale;
            if (ImGui.InputInt("Max days since last sale for Market", ref maxDays, 1, 7))
            {
                config.MarketMaxDaysSinceLastSale = Math.Clamp(maxDays, 1, 90);
                triageChanged = true;
            }

            ImGui.SetNextItemWidth(inputWidth);
            var maxCompDays = config.MarketMaxCompetitionDays;
            if (ImGui.InputInt("Max competitive backlog (days) before oversupplied", ref maxCompDays, 5, 15))
            {
                config.MarketMaxCompetitionDays = Math.Clamp(maxCompDays, 5, 180);
                triageChanged = true;
            }

            ImGui.SetNextItemWidth(inputWidth);
            var stalePct = config.StaleListingUndercutPct;
            if (ImGui.InputInt("Flag active listing if overpriced by (%)", ref stalePct, 5, 10))
            {
                config.StaleListingUndercutPct = Math.Clamp(stalePct, 5, 200);
                uiOnlyChanged = true;
            }

            ImGui.SetNextItemWidth(inputWidth);
            var vendorMaxStack = config.VendorMaxStackValue;
            if (ImGui.InputInt("Max stack value to sell to Vendor (Gil)", ref vendorMaxStack, 50, 200))
            {
                config.VendorMaxStackValue = Math.Clamp(vendorMaxStack, 0, 50_000);
                triageChanged = true;
            }

            ImGui.SetNextItemWidth(inputWidth);
            var vendorMaxVel = config.VendorMaxVelocity;
            if (ImGui.InputFloat("Max daily sales to sell to Vendor", ref vendorMaxVel, 0.05f, 0.25f, "%.2f"))
            {
                config.VendorMaxVelocity = Math.Clamp(vendorMaxVel, 0f, 10f);
                triageChanged = true;
            }
        }

        ImGui.Spacing();
        if (ImGui.Button("Reset to Defaults"))
        {
            config.ApplySellModePresets(SellMode.Safe);
            config.MarketUndercutGil = 1;
            config.MarketMinFloorUnitPrice = 50;
            config.BuyMaxGilPerPurchase = 50_000;
            config.BuyOutlierMultiplier = 2.5f;
            config.EnableLifestreamWorldHopPrompt = false;
            config.WorldHopMinSavingsGil = 5_000;
            config.EnableHoverHighlights = true;
            config.MarketReviewBeforeConfirm = true;
            config.MarketAutoComparePricesInReview = true;
            config.MarketAutoFillRetainerSell = true;
            config.MarketPromptStaleListings = true;
            config.StaleListingUndercutPct = 25;
            config.MarketOneListingPerClick = true;
            config.MarketDailyActionLimit = 20;
            triageChanged = true;
        }

        ImGui.SameLine();
        ImGui.TextColored(ColorMuted, "Custom item overrides are never reset.");

        if (triageChanged)
        {
            config.Save(pluginInterface);
            triageEngine.Invalidate();
            magicSort.InvalidateButtonCache();
            InvalidateLocalCaches();
        }
        else if (uiOnlyChanged)
        {
            config.Save(pluginInterface);
            magicSort.InvalidateButtonCache();
        }
    }
}
