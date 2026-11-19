using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using MagicInventory.Core;
using MagicInventory.Services;
using MagicInventory.Services.Triage;

namespace MagicInventory.UI;

/// <summary>
/// On-demand Settings & Item Search window for Magic Inventory.
/// Hidden by default; opens via /magic, /m, or Dalamud's Plugin Config gear icon.
/// </summary>
public sealed class SettingsWindow : Window
{
    private static readonly string[] SpecialtyCategories =
    [
        "Ingredient",
        "Seafood",
        "Meal",
        "Medicine",
        "Reagent",
        "Catalyst",
        "Gardening",
        "Fishing Tackle",
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

    private string searchQuery = string.Empty;
    private int selectedFilterIndex;
    private string lastGameSearchQuery = string.Empty;
    private List<uint> cachedGameSearchIds = new();
    private DateTime lastLocationsMapUtc = DateTime.MinValue;
    private Dictionary<uint, string> cachedLocationsMap = new();
    private int sortColumnIndex;
    private bool sortDescending;

    public SettingsWindow(
        IDalamudPluginInterface pluginInterface,
        IDataManager dataManager,
        Configuration config,
        HoldingsService holdings,
        UniversalisClient universalis,
        ItemTriageEngine triageEngine,
        MagicSortService magicSort)
        : base("Magic Inventory###MagicInventorySettings")
    {
        this.pluginInterface = pluginInterface;
        this.dataManager = dataManager;
        this.config = config;
        this.holdings = holdings;
        this.universalis = universalis;
        this.triageEngine = triageEngine;
        this.magicSort = magicSort;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(640, 440),
            MaximumSize = new Vector2(1400, 960),
        };
        Size = new Vector2(780, 540);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
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
            DrawItemSearchTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Retainers"))
        {
            DrawRetainersTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Settings"))
        {
            DrawSettingsTab();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void DrawOverviewTab()
    {
        var entries = triageEngine.GetCurrentEntries();
        var world = triageEngine.ActiveWorldName;
        var dc = triageEngine.ActiveDataCenterName;
        var retainers = holdings.GetKnownRetainersForCurrentCharacter();

        ImGui.TextUnformatted($"World: {(string.IsNullOrEmpty(world) ? "Not logged in" : $"{world} ({dc})")}");
        ImGui.SameLine(0, 20f);
        ImGui.TextColored(ColorMuted, $"Cached prices: {universalis.CachedEntryCount:N0}");
        ImGui.SameLine(0, 20f);
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

        if (ImGui.Button("Refresh Market Prices", new Vector2(185, 30)))
        {
            triageEngine.TriggerFullScan(forceRefreshPrices: true);
        }

        if (isScanning)
        {
            ImGui.EndDisabled();
            var total = Math.Max(1, universalis.TotalQueuedInScan);
            var done = Math.Clamp(universalis.CompletedInScan, 0, total);
            ImGui.SameLine();
            ImGui.ProgressBar((float)done / total, new Vector2(220, 26), $"Checking {done}/{total}...");
        }
        else
        {
            ImGui.SameLine();
            if (ImGui.Button("Rescan Bags", new Vector2(120, 30)))
            {
                triageEngine.Invalidate();
                magicSort.InvalidateButtonCache();
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
            ImGui.TableSetupColumn("Destination", ImGuiTableColumnFlags.WidthFixed, 125f);
            ImGui.TableSetupColumn("Items", ImGuiTableColumnFlags.WidthFixed, 60f);
            ImGui.TableSetupColumn("What Happens", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();

            DrawSummaryRow("Retainers", counts.GetValueOrDefault("Retainer"), "Stored in your retainers with /m sort");
            DrawSummaryRow("Market", counts.GetValueOrDefault("Market"), "Listed on the Market Board with /m sell");
            DrawSummaryRow("Vendor", counts.GetValueOrDefault("Vendor"), "Sold to any Gil vendor with /m sell");
            DrawSummaryRow("GC Seals", counts.GetValueOrDefault("GrandCompany"), "Turned in for GC Seals with /m sell");
            DrawSummaryRow("Gold Saucer", counts.GetValueOrDefault("GoldSaucer"), "Duplicate Triple Triad cards to trade for MGP at Gold Saucer");
            DrawSummaryRow("Extract Materia", counts.GetValueOrDefault("Materia"), "100% Spiritbond gear — extract free materia in Materialize");
            DrawSummaryRow("Use", counts.GetValueOrDefault("Use"), "Stays in bags — use all at once with Use All");
            DrawSummaryRow("Armoire", counts.GetValueOrDefault("Armoire"), "Can be stored in your Inn Armoire");
            DrawSummaryRow("Glamour Dresser", counts.GetValueOrDefault("Glamour"), "Lv. 1 glamour gear for your Inn Glamour Dresser");
            DrawSummaryRow("Keep", counts.GetValueOrDefault("Keep"), "Stays where it is (Bag, Armoury, or Equipped)");

            ImGui.EndTable();
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
        ImGui.BulletText("/m use                  - Use unlockables & coffers in your bags");
        ImGui.BulletText("/m stop                 - Cancel any running sort, sell, buy, or use action");
        ImGui.BulletText("/m scan                 - Refresh Market Board prices");
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

    private void DrawItemSearchTab()
    {
        ImGui.SetNextItemWidth(260f);
        ImGui.InputTextWithHint("##ItemSearchInput", "Search item name or ID...", ref searchQuery, 128);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(170f);
        ImGui.Combo("##ItemFilterCombo", ref selectedFilterIndex, FilterOptions, FilterOptions.Length);

        var filter = FilterOptions[selectedFilterIndex];
        var world = triageEngine.ActiveWorldName;
        var retainers = holdings.GetKnownRetainersForCurrentCharacter();
        var entries = triageEngine.GetCurrentEntries();

        List<MagicSortEntry> rows;
        if (filter == "All Game Items")
        {
            rows = BuildAllGameItemsResults(searchQuery);
            if (searchQuery.Trim().Length < 2)
            {
                ImGui.TextColored(ColorMuted, "Type at least 2 characters (or an item ID) to search all FFXIV items.");
            }
        }
        else
        {
            var q = searchQuery.Trim();
            rows = new List<MagicSortEntry>(entries.Count);
            foreach (var entry in entries.Values)
            {
                if (!MatchesOwnedFilter(entry, filter))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(q)
                    && !entry.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                    && !entry.ItemId.ToString().Contains(q, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                rows.Add(entry);
            }

            rows.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        }

        var nowUtc = DateTime.UtcNow;
        if ((nowUtc - lastLocationsMapUtc).TotalSeconds >= 2.0)
        {
            lastLocationsMapUtc = nowUtc;
            cachedLocationsMap = holdings.BuildOwnedLocationsMap();
        }

        ImGui.SameLine();
        ImGui.TextColored(ColorMuted, $"{rows.Count} shown");

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
        ImGui.TableSetupColumn("Where", ImGuiTableColumnFlags.WidthFixed, 115f, 1);
        ImGui.TableSetupColumn("Destination", ImGuiTableColumnFlags.WidthFixed, 105f, 2);
        ImGui.TableSetupColumn("Market", ImGuiTableColumnFlags.WidthFixed, 100f, 3);
        ImGui.TableSetupColumn("Why", ImGuiTableColumnFlags.WidthStretch, 1.45f, 4);
        ImGui.TableSetupColumn("Rule", ImGuiTableColumnFlags.WidthFixed, 130f, 5);
        ImGui.TableHeadersRow();

        var sortSpecs = ImGui.TableGetSortSpecs();
        if (sortSpecs.SpecsCount > 0)
        {
            var spec = sortSpecs.Specs;
            sortColumnIndex = spec.ColumnIndex;
            sortDescending = spec.SortDirection == ImGuiSortDirection.Descending;
            if (sortSpecs.SpecsDirty)
            {
                sortSpecs.SpecsDirty = false;
            }
        }

        SortItemRows(rows, world, retainers);

        for (var i = 0; i < rows.Count; i++)
        {
            var entry = rows[i];
            ImGui.PushID((int)entry.ItemId);
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(entry.Name);

            ImGui.TableSetColumnIndex(1);
            var whereText = cachedLocationsMap.GetValueOrDefault(entry.ItemId, "Not owned");
            ImGui.TextColored(whereText == "Not owned" ? ColorMuted : new Vector4(0.86f, 0.88f, 0.92f, 1f), whereText);
            if (ImGui.IsItemHovered() && whereText.Length > 14)
            {
                ImGui.SetTooltip(whereText);
            }

            ImGui.TableSetColumnIndex(2);
            var routeDisplay = FormatDestinationLabel(entry);
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
            DrawOverrideCombo(entry.ItemId, retainers);

            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private static string FormatDestinationLabel(MagicSortEntry entry)
    {
        if (entry.Route == "Retainer" && !string.IsNullOrEmpty(entry.TargetRetainerName))
        {
            return entry.TargetRetainerName;
        }

        return entry.Route switch
        {
            "GrandCompany" => "GC Seals",
            "GoldSaucer" => "Gold Saucer",
            "Materia" => "Extract Materia",
            "Glamour" => "Glamour Dresser",
            _ => entry.Route,
        };
    }

    private void SortItemRows(List<MagicSortEntry> rows, string world, IReadOnlyList<KnownRetainerInfo> retainers)
    {
        rows.Sort((a, b) =>
        {
            var cmp = sortColumnIndex switch
            {
                1 => string.Compare(
                    cachedLocationsMap.GetValueOrDefault(a.ItemId, string.Empty),
                    cachedLocationsMap.GetValueOrDefault(b.ItemId, string.Empty),
                    StringComparison.OrdinalIgnoreCase),
                2 => string.Compare(FormatDestinationLabel(a), FormatDestinationLabel(b), StringComparison.OrdinalIgnoreCase),
                3 => GetSortableMarketPrice(a, world).CompareTo(GetSortableMarketPrice(b, world)),
                4 => string.Compare(a.Reason, b.Reason, StringComparison.OrdinalIgnoreCase),
                5 => string.Compare(
                    FormatOverrideLabel(a.ItemId, config.ManualRouteOverrides.GetValueOrDefault(a.ItemId, string.Empty), retainers),
                    FormatOverrideLabel(b.ItemId, config.ManualRouteOverrides.GetValueOrDefault(b.ItemId, string.Empty), retainers),
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

    private List<MagicSortEntry> BuildAllGameItemsResults(string rawQuery)
    {
        var q = rawQuery.Trim();
        var results = new List<MagicSortEntry>();
        if (q.Length < 2 && !uint.TryParse(q, out _))
        {
            return results;
        }

        if (!string.Equals(q, lastGameSearchQuery, StringComparison.OrdinalIgnoreCase))
        {
            lastGameSearchQuery = q;
            cachedGameSearchIds = new List<uint>(64);
            var sheet = dataManager.GetExcelSheet<Item>();
            if (sheet != null)
            {
                var parsedId = uint.TryParse(q, out var exactId) ? exactId : 0u;
                foreach (var row in sheet)
                {
                    if (row.RowId <= 20)
                    {
                        continue;
                    }

                    if (parsedId > 0 && row.RowId == parsedId)
                    {
                        cachedGameSearchIds.Add(row.RowId);
                        break;
                    }

                    var itemName = row.Name.ExtractText();
                    if (!string.IsNullOrEmpty(itemName)
                        && itemName.Contains(q, StringComparison.OrdinalIgnoreCase))
                    {
                        cachedGameSearchIds.Add(row.RowId);
                        if (cachedGameSearchIds.Count >= 80)
                        {
                            break;
                        }
                    }
                }
            }
        }

        foreach (var id in cachedGameSearchIds)
        {
            var classified = triageEngine.ClassifySingleItemOnDemand(id, queuePriceIfMissing: false);
            if (classified != null)
            {
                results.Add(classified);
            }
        }

        return results;
    }

    private bool MatchesOwnedFilter(MagicSortEntry entry, string filter)
    {
        return filter switch
        {
            "All Owned" => true,
            "Retainers" => string.Equals(entry.Route, "Retainer", StringComparison.OrdinalIgnoreCase),
            "GC Seals" => string.Equals(entry.Route, "GrandCompany", StringComparison.OrdinalIgnoreCase),
            "Gold Saucer" => string.Equals(entry.Route, "GoldSaucer", StringComparison.OrdinalIgnoreCase),
            "Extract Materia" => string.Equals(entry.Route, "Materia", StringComparison.OrdinalIgnoreCase),
            "Glamour Dresser" => string.Equals(entry.Route, "Glamour", StringComparison.OrdinalIgnoreCase),
            "Custom Rules" => config.ManualRouteOverrides.ContainsKey(entry.ItemId),
            _ => string.Equals(entry.Route, filter, StringComparison.OrdinalIgnoreCase),
        };
    }

    private void DrawOverrideCombo(uint itemId, IReadOnlyList<KnownRetainerInfo> retainers)
    {
        var currentVal = config.ManualRouteOverrides.GetValueOrDefault(itemId, string.Empty);
        var currentPrio = config.MarketListingPriorities.GetValueOrDefault(itemId, 0);
        var currentLabel = FormatOverrideLabel(itemId, currentVal, retainers);

        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo("##ov", currentLabel))
        {
            return;
        }

        var itemRow = dataManager.GetExcelSheet<Item>()?.GetRowOrDefault(itemId);
        DrawOverrideOption(itemId, string.Empty, null, "Auto", currentVal, currentPrio);
        DrawOverrideOption(itemId, "Keep", null, "Keep", currentVal, currentPrio);

        if (itemRow is { ItemAction.RowId: > 0 })
        {
            DrawOverrideOption(itemId, "Use", null, "Use", currentVal, currentPrio);
        }

        if (itemRow is null || !itemRow.Value.IsUntradable)
        {
            DrawOverrideOption(itemId, "Market", 1, "Market (High)", currentVal, currentPrio);
            DrawOverrideOption(itemId, "Market", 0, "Market", currentVal, currentPrio);
            DrawOverrideOption(itemId, "Market", -1, "Market (Low)", currentVal, currentPrio);
        }

        if (itemRow is null || (itemRow.Value.PriceLow > 0 && !itemRow.Value.IsUnique))
        {
            DrawOverrideOption(itemId, "Vendor", null, "Vendor", currentVal, currentPrio);
        }

        if (itemRow is { EquipSlotCategory.RowId: > 0, Rarity: 2 or 3 or 7 } && itemRow.Value.LevelItem.RowId >= 15)
        {
            DrawOverrideOption(itemId, "GrandCompany", null, "GC Seals", currentVal, currentPrio);
        }

        if (itemRow is not null
            && string.Equals(itemRow.Value.ItemUICategory.ValueNullable?.Name.ExtractText(), "Triple Triad Card", StringComparison.OrdinalIgnoreCase))
        {
            DrawOverrideOption(itemId, "GoldSaucer", null, "Gold Saucer", currentVal, currentPrio);
        }

        if (itemRow is { EquipSlotCategory.RowId: > 0 })
        {
            DrawOverrideOption(itemId, "Glamour", null, "Glamour Dresser", currentVal, currentPrio);
        }

        if (triageEngine.IsCabinetItem(itemId))
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
        if (ImGui.Selectable(label, selected))
        {
            magicSort.SetItemOverride(itemId, value, marketPriority);
        }
    }

    private string FormatOverrideLabel(uint itemId, string value, IReadOnlyList<KnownRetainerInfo> retainers)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "Auto";
        }

        if (value.StartsWith("Retainer:", StringComparison.OrdinalIgnoreCase)
            && ulong.TryParse(value["Retainer:".Length..], out var rid))
        {
            var match = retainers.FirstOrDefault(r => r.RetainerId == rid);
            return match != null ? match.Name : "Retainer";
        }

        if (string.Equals(value, "Market", StringComparison.OrdinalIgnoreCase))
        {
            var prio = config.MarketListingPriorities.GetValueOrDefault(itemId, 0);
            return prio > 0 ? "Market (High)" : (prio < 0 ? "Market (Low)" : "Market");
        }

        return value switch
        {
            "GrandCompany" => "GC Seals",
            "GoldSaucer" => "Gold Saucer",
            "Materia" => "Extract Materia",
            "Glamour" => "Glamour Dresser",
            _ => value,
        };
    }

    private static Vector4 GetRouteColor(string route)
    {
        return route switch
        {
            "Market" => ColorGold,
            "Retainer" => new Vector4(0.58f, 0.80f, 0.96f, 1f),
            "Vendor" => ColorWarn,
            "GrandCompany" => ColorGood,
            "GoldSaucer" => new Vector4(0.98f, 0.74f, 0.30f, 1f),
            "Materia" => new Vector4(0.96f, 0.62f, 0.96f, 1f),
            "Use" => new Vector4(0.55f, 0.92f, 0.88f, 1f),
            "Armoire" => new Vector4(0.78f, 0.72f, 0.95f, 1f),
            "Glamour" => new Vector4(0.92f, 0.68f, 0.90f, 1f),
            _ => ColorMuted,
        };
    }

    private void DrawRetainersTab()
    {
        var retainers = holdings.GetKnownRetainersForCurrentCharacter();
        var cid = InventorySafetyEvaluator.GetCurrentContentId();
        var profile = config.GetOrCreateCharacter(cid);

        if (retainers.Count == 0)
        {
            ImGui.TextColored(ColorGold, "No retainers found yet for this character.");
            ImGui.TextColored(ColorMuted, "Open a Summoning Bell once and visit each retainer to see their bags.");
            return;
        }

        ImGui.TextWrapped("Pick which categories each retainer should hold. Anything unchecked follows where you already keep most of that type, and overflows to another retainer with free space when full.");
        ImGui.Spacing();

        foreach (var ret in retainers)
        {
            ImGui.PushID(ret.RetainerId.ToString());
            var status = !ret.IsVisited
                ? "Not visited yet"
                : (ret.UsedBagSlots >= HoldingsService.MaxRetainerBagSlots ? $"{ret.UsedBagSlots}/175 slots (FULL)" : $"{ret.UsedBagSlots}/175 slots");
            var header = $"{ret.Name}  —  {status}  |  Market: {ret.MarketItemCount}/20###ret_{ret.RetainerId}";
            if (ImGui.CollapsingHeader(header, ImGuiTreeNodeFlags.DefaultOpen))
            {
                var drewTopLine = false;
                if (ret.CategoryCounts.Count > 0)
                {
                    var topCats = ret.CategoryCounts
                        .OrderByDescending(kv => kv.Value)
                        .Take(6)
                        .Select(kv => $"{kv.Key} ({kv.Value})");
                    ImGui.TextColored(ColorMuted, $"Mostly holds: {string.Join(", ", topCats)}");
                    drewTopLine = true;
                }

                profile.RetainerSpecialties.TryGetValue(ret.RetainerId, out var assigned);
                var hasCustom = assigned is { Count: > 0 };
                if (hasCustom)
                {
                    if (drewTopLine)
                    {
                        ImGui.SameLine();
                    }

                    if (ImGui.SmallButton("Reset to Auto"))
                    {
                        profile.RetainerSpecialties.Remove(ret.RetainerId);
                        config.Save(pluginInterface);
                        triageEngine.Invalidate();
                        magicSort.InvalidateButtonCache();
                    }
                }

                ImGui.Spacing();
                var cols = 4;
                if (ImGui.BeginTable($"SpecTable_{ret.RetainerId}", cols, ImGuiTableFlags.SizingStretchSame))
                {
                    for (var i = 0; i < SpecialtyCategories.Length; i++)
                    {
                        if (i % cols == 0)
                        {
                            ImGui.TableNextRow();
                        }

                        ImGui.TableSetColumnIndex(i % cols);
                        var cat = SpecialtyCategories[i];
                        var isChecked = assigned != null && assigned.Contains(cat);
                        if (ImGui.Checkbox(cat, ref isChecked))
                        {
                            if (!profile.RetainerSpecialties.TryGetValue(ret.RetainerId, out var set))
                            {
                                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                profile.RetainerSpecialties[ret.RetainerId] = set;
                            }

                            if (isChecked)
                            {
                                // Enforce exclusive category ownership across retainers so two retainers never conflict
                                foreach (var (otherRetId, otherSet) in profile.RetainerSpecialties)
                                {
                                    if (otherRetId != ret.RetainerId)
                                    {
                                        otherSet.Remove(cat);
                                    }
                                }

                                set.Add(cat);
                            }
                            else
                            {
                                set.Remove(cat);
                            }

                            config.Save(pluginInterface);
                            triageEngine.Invalidate();
                            magicSort.InvalidateButtonCache();
                        }
                    }

                    ImGui.EndTable();
                }

                ImGui.Spacing();
            }

            ImGui.PopID();
        }
    }

    private void DrawSettingsTab()
    {
        var changed = false;

        ImGui.TextUnformatted("Sell Mode");
        ImGui.TextColored(ColorMuted, "Controls how aggressively /m sell tags items for Vendors, Grand Company, and the Market Board.");
        ImGui.Spacing();

        var mode = config.SellMode;
        if (ImGui.RadioButton("Safe  —  Junk & clear Market wins only", mode == SellMode.Safe))
        {
            config.ApplySellModePresets(SellMode.Safe);
            changed = true;
        }

        ImGui.TextColored(ColorMuted, "    Sells Allagan pieces, old white gear, cheap city-vendor clutter, green dungeon gear, and high-value Market items.");

        if (ImGui.RadioButton("Balanced  —  Clear low-value clutter & good Market flips", mode == SellMode.Balanced))
        {
            config.ApplySellModePresets(SellMode.Balanced);
            changed = true;
        }

        ImGui.TextColored(ColorMuted, "    Also turns in outgeared blue gear and sells cheap common drops while keeping crafting mats and rare items.");

        if (ImGui.RadioButton("Clear Space  —  Sell anything cheap you can easily re-buy", mode == SellMode.Purge))
        {
            config.ApplySellModePresets(SellMode.Purge);
            changed = true;
        }

        ImGui.TextColored(ColorMuted, "    Liquidates cheap city-vendor mats and abundant low-price Market items to free bag & retainer slots.");
        ImGui.Spacing();
        ImGui.TextColored(ColorGood, "Always kept safe in every mode: tracked mats, gearsets, food, potions, glamour, housing, relics, and rare (0-listed) items.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("Market Board");
        ImGui.SetNextItemWidth(160f);
        var undercut = config.MarketUndercutGil;
        if (ImGui.InputInt("Undercut by (Gil)", ref undercut, 1, 10))
        {
            config.MarketUndercutGil = Math.Clamp(undercut, 0, 10_000);
            changed = true;
        }

        ImGui.SetNextItemWidth(160f);
        var maxBuy = config.BuyMaxGilPerPurchase;
        if (ImGui.InputInt("Max Gil per Magic Buy purchase", ref maxBuy, 5_000, 25_000))
        {
            config.BuyMaxGilPerPurchase = Math.Clamp(maxBuy, 1_000, 10_000_000);
            changed = true;
        }

        ImGui.SetNextItemWidth(160f);
        var outlier = config.BuyOutlierMultiplier;
        if (ImGui.InputFloat("Skip listings above (x recent price)", ref outlier, 0.1f, 0.5f, "%.1f"))
        {
            config.BuyOutlierMultiplier = Math.Clamp(outlier, 1.2f, 10f);
            changed = true;
        }

        var hasLifestream = pluginInterface.InstalledPlugins.Any(p =>
            string.Equals(p.InternalName, "Lifestream", StringComparison.OrdinalIgnoreCase) && p.IsLoaded);
        var lifestreamOptIn = config.EnableLifestreamWorldHopPrompt;
        if (ImGui.Checkbox("Ask to travel via Lifestream if an item isn't listed on my Home World", ref lifestreamOptIn))
        {
            config.EnableLifestreamWorldHopPrompt = lifestreamOptIn;
            changed = true;
        }

        ImGui.TextColored(
            hasLifestream ? ColorGood : ColorMuted,
            hasLifestream
                ? "    Lifestream installed — only travels after you click Travel."
                : "    Lifestream not installed — shows the cheapest DC world in chat instead.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("Display");
        var hoverHighlights = config.EnableHoverHighlights;
        if (ImGui.Checkbox("Highlight matching items when hovering buttons or right-click options", ref hoverHighlights))
        {
            config.EnableHoverHighlights = hoverHighlights;
            changed = true;
        }

        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Advanced price thresholds"))
        {
            ImGui.SetNextItemWidth(160f);
            var minStack = config.MarketMinStackValue;
            if (ImGui.InputInt("Min stack value for Market (Gil)", ref minStack, 100, 500))
            {
                config.MarketMinStackValue = Math.Clamp(minStack, 100, 1_000_000);
                changed = true;
            }

            ImGui.SetNextItemWidth(160f);
            var minUnit = config.MarketMinUnitPrice;
            if (ImGui.InputInt("Min single-item price for Market (Gil)", ref minUnit, 100, 500))
            {
                config.MarketMinUnitPrice = Math.Clamp(minUnit, 50, 1_000_000);
                changed = true;
            }

            ImGui.SetNextItemWidth(160f);
            var minVel = config.MarketMinVelocity;
            if (ImGui.InputFloat("Min daily sales for Market", ref minVel, 0.05f, 0.25f, "%.2f"))
            {
                config.MarketMinVelocity = Math.Clamp(minVel, 0.01f, 50f);
                changed = true;
            }

            ImGui.SetNextItemWidth(160f);
            var vendorMaxStack = config.VendorMaxStackValue;
            if (ImGui.InputInt("Max stack value to sell to Vendor (Gil)", ref vendorMaxStack, 50, 200))
            {
                config.VendorMaxStackValue = Math.Clamp(vendorMaxStack, 0, 50_000);
                changed = true;
            }

            ImGui.SetNextItemWidth(160f);
            var vendorMaxVel = config.VendorMaxVelocity;
            if (ImGui.InputFloat("Max daily sales to sell to Vendor", ref vendorMaxVel, 0.05f, 0.25f, "%.2f"))
            {
                config.VendorMaxVelocity = Math.Clamp(vendorMaxVel, 0f, 10f);
                changed = true;
            }
        }

        ImGui.Spacing();
        if (ImGui.Button("Reset to Defaults"))
        {
            config.ApplySellModePresets(SellMode.Safe);
            config.MarketUndercutGil = 1;
            config.BuyMaxGilPerPurchase = 50_000;
            config.BuyOutlierMultiplier = 2.5f;
            config.EnableLifestreamWorldHopPrompt = false;
            config.EnableHoverHighlights = true;
            changed = true;
        }

        ImGui.SameLine();
        ImGui.TextColored(ColorMuted, "Custom item overrides are never reset.");

        if (changed)
        {
            config.Save(pluginInterface);
            triageEngine.Invalidate();
            magicSort.InvalidateButtonCache();
        }
    }
}
