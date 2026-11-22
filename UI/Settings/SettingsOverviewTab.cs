using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using MagicInventory.Core;
using MagicInventory.Services;

namespace MagicInventory.UI.Settings;

internal sealed class SettingsOverviewTab
{
    private readonly SettingsUiContext ctx;

    public SettingsOverviewTab(SettingsUiContext ctx)
    {
        this.ctx = ctx;
    }

    public void Draw()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var entries = ctx.TriageEngine.GetCurrentEntries();
        var world = ctx.TriageEngine.ActiveWorldName;
        var dc = ctx.TriageEngine.ActiveDataCenterName;
        var retainers = ctx.Holdings.GetKnownRetainersForCurrentCharacter();

        ImGui.TextUnformatted($"World: {(string.IsNullOrEmpty(world) ? "Not logged in" : $"{world} ({dc})")}");
        ImGui.SameLine(0, 20f * scale);
        ImGui.TextColored(SettingsUiContext.ColorMuted, $"Cached prices: {ctx.Universalis.CachedEntryCount:N0}");
        ImGui.SameLine(0, 20f * scale);
        ImGui.TextColored(SettingsUiContext.ColorGold, $"Sell mode: {SettingsUiContext.FormatSellModeName(ctx.Config.SellMode)}");

        if (ctx.Universalis.LastScanUtc != DateTime.MinValue)
        {
            var age = DateTime.UtcNow - ctx.Universalis.LastScanUtc;
            var ageText = age.TotalMinutes < 1
                ? "just now"
                : (age.TotalHours < 1 ? $"{(int)age.TotalMinutes}m ago" : $"{age.TotalHours:F1}h ago");
            ImGui.TextColored(SettingsUiContext.ColorMuted, $"Prices updated {ageText}");
        }

        ImGui.Spacing();

        var isScanning = ctx.Universalis.IsScanning;
        if (isScanning)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button("Refresh Market Prices", new Vector2(185f * scale, 30f * scale)))
        {
            ctx.TriageEngine.TriggerFullScan(forceRefreshPrices: true);
        }

        if (isScanning)
        {
            ImGui.EndDisabled();
            var total = Math.Max(1, ctx.Universalis.TotalQueuedInScan);
            var done = Math.Clamp(ctx.Universalis.CompletedInScan, 0, total);
            ImGui.SameLine();
            ImGui.ProgressBar((float)done / total, new Vector2(220f * scale, 26f * scale), $"Checking {done}/{total}...");
        }
        else
        {
            ImGui.SameLine();
            if (ImGui.Button("Rescan Bags", new Vector2(125f * scale, 30f * scale)))
            {
                ctx.TriageEngine.Invalidate();
                ctx.MagicSort.InvalidateButtonCache();
                ctx.InvalidateLocalCaches();
            }
        }

        if (!string.IsNullOrEmpty(ctx.Universalis.LastError))
        {
            ImGui.TextColored(SettingsUiContext.ColorWarn, $"Market data notice: {ctx.Universalis.LastError}");
        }

        var unvisited = retainers.Where(r => !r.IsVisited).Select(r => r.Name).ToList();
        if (retainers.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(SettingsUiContext.ColorGold, "Open a Summoning Bell once so Magic Inventory can find your retainers.");
        }
        else if (unvisited.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(SettingsUiContext.ColorGold, $"Visit {string.Join(", ", unvisited)} once to see what's inside their bags.");
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
                    SettingsUiContext.ColorWarn,
                    $"Retainers are {pct}% full ({usedSlots}/{totalSlots} slots). Try Balanced or Clear Space in Settings to free room.");
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var counts = new Dictionary<RouteKind, int>();
        foreach (var e in entries.Values)
        {
            counts[e.Route.Kind] = counts.GetValueOrDefault(e.Route.Kind) + 1;
        }

        ImGui.TextUnformatted($"Your Items ({entries.Count})");
        if (ImGui.BeginTable("RouteSummaryTable", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Destination", ImGuiTableColumnFlags.WidthFixed, 130f * scale);
            ImGui.TableSetupColumn("Items", ImGuiTableColumnFlags.WidthFixed, 62f * scale);
            ImGui.TableSetupColumn("What Happens", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();

            DrawSummaryRow("Retainers", counts.GetValueOrDefault(RouteKind.Retainer), "Stored in your retainers with /m sort");
            DrawSummaryRow("Market", counts.GetValueOrDefault(RouteKind.Market), "Prepped or listed on the Market Board with /m sell");
            DrawSummaryRow("Vendor", counts.GetValueOrDefault(RouteKind.Vendor), "Sold to any Gil vendor with /m sell");
            DrawSummaryRow("GC Seals", counts.GetValueOrDefault(RouteKind.GrandCompany), "Turned in for GC Seals with /m sell");
            DrawSummaryRow("Gold Saucer", counts.GetValueOrDefault(RouteKind.GoldSaucer), "Owned duplicate Triple Triad cards to trade for MGP");
            DrawSummaryRow("Extract Materia", counts.GetValueOrDefault(RouteKind.Materia), "100% Spiritbond gear (extract materia in Materialize)");
            DrawSummaryRow("Use", counts.GetValueOrDefault(RouteKind.Use), "Untradable unowned unlockables & MGP cards (never Gear Coffers)");
            DrawSummaryRow("Armoire", counts.GetValueOrDefault(RouteKind.Armoire), "Stored in your Inn Armoire");
            DrawSummaryRow("Glamour Dresser", counts.GetValueOrDefault(RouteKind.Glamour), "Lv. 1 glamour gear for your Inn Glamour Dresser");
            DrawSummaryRow("Keep", counts.GetValueOrDefault(RouteKind.Keep), "Stays where it is (Bag, Armoury, or Equipped)");

            ImGui.EndTable();
        }

        if (ctx.MaterialTracker.TrackedItemCount > 0)
        {
            ImGui.TextColored(
                RoutePresentation.ColorTracked,
                $"Tracked materials: {ctx.MaterialTracker.TrackedItemCount} item(s) protected from selling and pulled from retainers.");
        }

        if (ctx.TriageEngine.PendingPriceCount > 0)
        {
            ImGui.TextColored(SettingsUiContext.ColorGold, $"{ctx.TriageEngine.PendingPriceCount} item(s) waiting for Market Board prices (kept safe until checked).");
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

    private static void DrawSummaryRow(string route, int count, string desc)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted(route);
        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(count.ToString());
        ImGui.TableSetColumnIndex(2);
        ImGui.TextColored(SettingsUiContext.ColorMuted, desc);
    }
}
