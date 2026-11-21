using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using MagicInventory.Core;
using MagicInventory.Services;

namespace MagicInventory.UI;

/// <summary>
/// Hover-only slot highlighter across native InventoryGrid* and RetainerGrid* addons:
/// - Magic Sell (RetainerSellList, Shop, GrandCompanySupplyList): highlights bag items to be sold/listed (gold).
/// - Inventory Use All (InventoryExpansion / InventoryLarge / Inventory): highlights usable bag items (gold).
/// - Retainer Magic Sort (InventoryRetainerLarge, InventoryRetainer): highlights player bag deposits (gold)
///   AND retainer bag withdrawals (teal) simultaneously on hover.
/// - Context Menu Override Hover: highlights all sibling items matching the hovered override destination.
/// </summary>
public sealed unsafe class InventoryHighlightController : IDisposable
{
    private const double PollIntervalMs = 100.0;

    // Warm gold for items leaving player bags (sell / list / deposit into retainer)
    private static readonly (short R, short G, short B) DepositOrSellTint = (72, 46, -24);

    // Crisp teal for items leaving retainer bags (tracked materials / Market / Vendor / misplaced withdrawals)
    private static readonly (short R, short G, short B) RetainerWithdrawTint = (-18, 64, 76);

    private static readonly string[] AllGridAddons =
    [
        "InventoryGrid0E",
        "InventoryGrid1E",
        "InventoryGrid2E",
        "InventoryGrid3E",
        "InventoryGrid0",
        "InventoryGrid1",
        "InventoryGrid",
        "RetainerGrid0",
        "RetainerGrid1",
        "RetainerGrid2",
        "RetainerGrid3",
        "RetainerGrid4",
        "RetainerGrid",
        "ArmouryBoard",
    ];

    private readonly IFramework framework;
    private readonly IGameGui gameGui;
    private readonly Configuration configuration;
    private readonly MagicSortService magicSort;

    private readonly Dictionary<(string AddonName, uint NodeId), (short R, short G, short B)> activeTints = new();
    private DateTime lastPollUtc = DateTime.MinValue;
    private bool clearedOrphansOnStartup;
    private string? hoveredSellAddon;
    private string? hoveredRetainerSortAddon;
    private string? activeContextSubmenuTitle;
    private IReadOnlyList<string?>? activeContextRouteFilters;
    private DateTime contextSubmenuArmedUtc = DateTime.MinValue;

    public InventoryHighlightController(
        IFramework framework,
        IGameGui gameGui,
        Configuration configuration,
        MagicSortService magicSort)
    {
        this.framework = framework;
        this.gameGui = gameGui;
        this.configuration = configuration;
        this.magicSort = magicSort;
        this.framework.Update += OnFrameworkUpdate;
    }

    public void SetActiveContextSubmenuRoutes(string? submenuTitle, IReadOnlyList<string?>? routeFiltersByIndex)
    {
        activeContextSubmenuTitle = submenuTitle;
        activeContextRouteFilters = routeFiltersByIndex;
        contextSubmenuArmedUtc = DateTime.UtcNow;
        if (routeFiltersByIndex == null || routeFiltersByIndex.Count == 0)
        {
            RefreshHighlights();
        }
    }

    public void SetHoveredSellAddon(string addonName, bool isHovered)
    {
        if (isHovered)
        {
            hoveredSellAddon = addonName;
        }
        else if (string.Equals(hoveredSellAddon, addonName, StringComparison.Ordinal))
        {
            hoveredSellAddon = null;
        }

        RefreshHighlights();
    }

    public void SetHoveredRetainerSortAddon(string addonName, bool isHovered)
    {
        if (isHovered)
        {
            hoveredRetainerSortAddon = addonName;
        }
        else if (string.Equals(hoveredRetainerSortAddon, addonName, StringComparison.Ordinal))
        {
            hoveredRetainerSortAddon = null;
        }

        RefreshHighlights();
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        var now = DateTime.UtcNow;
        var intervalMs = activeContextRouteFilters != null ? 50.0 : PollIntervalMs;
        if ((now - lastPollUtc).TotalMilliseconds < intervalMs)
        {
            return;
        }

        lastPollUtc = now;

        if (!clearedOrphansOnStartup)
        {
            clearedOrphansOnStartup = true;
            SweepOrphanedHighlights();
        }

        RefreshHighlights();
    }

    private void RefreshHighlights()
    {
        if (!configuration.EnableHoverHighlights)
        {
            if (activeTints.Count > 0)
            {
                ClearAllHighlights();
            }

            return;
        }

        if (hoveredSellAddon != null && !NativeUiHelpers.IsAddonOpenAndReady(gameGui, hoveredSellAddon))
        {
            hoveredSellAddon = null;
        }

        if (hoveredRetainerSortAddon != null && !NativeUiHelpers.IsAddonOpenAndReady(gameGui, hoveredRetainerSortAddon))
        {
            hoveredRetainerSortAddon = null;
        }

        var desiredNodes = new Dictionary<(string AddonName, uint NodeId), (short R, short G, short B)>();

        if (TryResolveHoveredContextMenuRouteFilter(out var contextRouteFilter))
        {
            magicSort.GetSlotsMatchingRouteFilter(
                contextRouteFilter,
                out var ctxPlayerSlots,
                out var ctxRetainerSlots,
                out var ctxArmourySlots);
            if (ctxPlayerSlots.Count > 0)
            {
                foreach (var key in ResolvePlayerVisualHighlightNodes(ctxPlayerSlots))
                {
                    desiredNodes[key] = DepositOrSellTint;
                }
            }

            if (ctxArmourySlots.Count > 0)
            {
                foreach (var key in ResolveArmouryVisualHighlightNodes(ctxArmourySlots))
                {
                    desiredNodes[key] = DepositOrSellTint;
                }
            }

            if (ctxRetainerSlots.Count > 0)
            {
                foreach (var key in ResolveRetainerVisualHighlightNodes(ctxRetainerSlots))
                {
                    desiredNodes[key] = RetainerWithdrawTint;
                }
            }
        }
        else if (hoveredRetainerSortAddon != null)
        {
            magicSort.GetActiveRetainerSortHighlightSlots(
                hoveredRetainerSortAddon,
                out var depositSlots,
                out var withdrawSlots);

            if (depositSlots.Count > 0)
            {
                foreach (var key in ResolvePlayerVisualHighlightNodes(depositSlots))
                {
                    desiredNodes[key] = DepositOrSellTint;
                }

                foreach (var key in ResolveArmouryVisualHighlightNodes(depositSlots))
                {
                    desiredNodes[key] = DepositOrSellTint;
                }
            }

            if (withdrawSlots.Count > 0)
            {
                foreach (var key in ResolveRetainerVisualHighlightNodes(withdrawSlots))
                {
                    desiredNodes[key] = RetainerWithdrawTint;
                }

                foreach (var key in ResolveArmouryVisualHighlightNodes(withdrawSlots))
                {
                    desiredNodes[key] = RetainerWithdrawTint;
                }
            }
        }
        else if (hoveredSellAddon != null)
        {
            var physicalTargets = magicSort.GetActiveSellHighlightSlots(hoveredSellAddon);
            if (physicalTargets.Count > 0)
            {
                foreach (var key in ResolvePlayerVisualHighlightNodes(physicalTargets))
                {
                    desiredNodes[key] = DepositOrSellTint;
                }

                foreach (var key in ResolveArmouryVisualHighlightNodes(physicalTargets))
                {
                    desiredNodes[key] = DepositOrSellTint;
                }
            }
        }

        if (desiredNodes.Count == 0)
        {
            if (activeTints.Count > 0)
            {
                ClearAllHighlights();
            }

            return;
        }

        // Restore any previously highlighted nodes no longer in the active set
        if (activeTints.Count > 0)
        {
            var stale = new List<(string AddonName, uint NodeId)>();
            foreach (var key in activeTints.Keys)
            {
                if (!desiredNodes.ContainsKey(key))
                {
                    stale.Add(key);
                }
            }

            foreach (var key in stale)
            {
                var orig = activeTints[key];
                RestoreNodeTint(key.AddonName, key.NodeId, orig);
                activeTints.Remove(key);
            }
        }

        // Apply native highlight tint to all target slots
        foreach (var (key, tint) in desiredNodes)
        {
            var addon = NativeUiHelpers.GetReadyAddon(gameGui, key.AddonName);
            if (addon == null)
            {
                continue;
            }

            var node = addon->GetNodeById(key.NodeId);
            if (node == null || !node->IsVisible())
            {
                continue;
            }

            if (!activeTints.ContainsKey(key))
            {
                activeTints[key] = (node->AddRed, node->AddGreen, node->AddBlue);
            }

            node->AddRed = tint.R;
            node->AddGreen = tint.G;
            node->AddBlue = tint.B;
        }
    }

    private bool TryResolveHoveredContextMenuRouteFilter(out string routeFilter)
    {
        routeFilter = string.Empty;
        var routes = activeContextRouteFilters;
        if (routes == null || routes.Count == 0)
        {
            return false;
        }

        var elapsedMs = (DateTime.UtcNow - contextSubmenuArmedUtc).TotalMilliseconds;

        // Native FFXIV submenus opened via IContextMenu.OpenSubmenu live in "AddonContextSub" (not "ContextMenu").
        var menu = NativeUiHelpers.GetReadyAddon(gameGui, "AddonContextSub");
        if (menu == null || !menu->IsVisible)
        {
            // Keep armed briefly across the 1-2 frame close/reopen transition of OpenSubmenu.
            if (elapsedMs > 450.0)
            {
                activeContextRouteFilters = null;
                activeContextSubmenuTitle = null;
            }

            return false;
        }

        // Verify the open AddonContextSub belongs to our expected submenu title (NodeId 2).
        if (!string.IsNullOrEmpty(activeContextSubmenuTitle))
        {
            var titleNode = menu->GetNodeById(2);
            var titleTextNode = titleNode != null ? titleNode->GetAsAtkTextNode() : null;
            if (titleTextNode != null)
            {
                var actualTitle = titleTextNode->NodeText.ToString().Trim();
                if (!string.Equals(actualTitle, activeContextSubmenuTitle, StringComparison.OrdinalIgnoreCase))
                {
                    if (elapsedMs > 450.0)
                    {
                        activeContextRouteFilters = null;
                        activeContextSubmenuTitle = null;
                    }

                    return false;
                }
            }
        }

        // Locate the AtkComponentList inside AddonContextSub (typically NodeId 4).
        AtkComponentList* list = null;
        var node4 = menu->GetNodeById(4);
        if (node4 != null)
        {
            list = node4->GetAsAtkComponentList();
        }

        if (list == null)
        {
            for (var n = 0; n < menu->UldManager.NodeListCount; n++)
            {
                var node = menu->UldManager.NodeList[n];
                if (node == null)
                {
                    continue;
                }

                var candidate = node->GetAsAtkComponentList();
                if (candidate != null && candidate->ItemRendererList != null && candidate->ListLength > 0)
                {
                    list = candidate;
                    break;
                }
            }
        }

        if (list == null || list->ItemRendererList == null || list->ListLength <= 0)
        {
            return false;
        }

        // Note: AddonContextSub pools ~21 ListItemRenderers; only indices 0..routes.Count-1 are active rows.
        var activeRowCount = Math.Min(routes.Count, list->ListLength);
        var hoveredIdx = list->HoveredItemIndex;

        // Fallback: hit-test live cursor position against each active row's screen rect
        if (hoveredIdx < 0 || hoveredIdx >= activeRowCount)
        {
            var mouse = ImGui.GetMousePos();
            var vpPos = ImGui.GetMainViewport().Pos;
            var addonScale = menu->Scale > 0.01f ? menu->Scale : 1f;

            for (var i = 0; i < activeRowCount; i++)
            {
                var renderer = list->ItemRendererList[i].AtkComponentListItemRenderer;
                if (renderer == null || renderer->OwnerNode == null)
                {
                    continue;
                }

                var owner = (AtkResNode*)renderer->OwnerNode;
                if (!owner->IsVisible())
                {
                    continue;
                }

                if (!InventoryGridLayout.TryGetNodeScreenRect(menu, owner, vpPos, out var min, out var max))
                {
                    continue;
                }

                if (mouse.X >= min.X && mouse.X <= max.X && mouse.Y >= min.Y && mouse.Y <= max.Y)
                {
                    hoveredIdx = i;
                    break;
                }
            }
        }

        // Only highlight while actively hovering a row (do NOT fall back to sticky SelectedItemIndex on unhover!)
        if (hoveredIdx < 0 || hoveredIdx >= activeRowCount)
        {
            return false;
        }

        var matched = routes[hoveredIdx];
        if (string.IsNullOrWhiteSpace(matched))
        {
            return false;
        }

        routeFilter = matched;
        return true;
    }

    /// <summary>
    /// Detects the active ArmouryBoard tab (0..11) by reading its 12 RadioButton nodes (NodeId 7..18),
    /// ordered spatially into Left Column (6 top-to-bottom) and Right Column (6 top-to-bottom) so it is
    /// completely independent of client language or ULD node ID order.
    /// </summary>
    public static int GetActiveArmouryTabIndex(IGameGui gameGui)
        => InventoryGridLayout.GetActiveArmouryTabIndex(gameGui);

    public static InventoryType? GetActiveArmouryTabContainer(IGameGui gameGui)
        => InventoryGridLayout.GetActiveArmouryTabContainer(gameGui);

    private HashSet<(string AddonName, uint NodeId)> ResolveArmouryVisualHighlightNodes(
        HashSet<(InventoryType Container, int Slot)> physicalTargets)
    {
        var result = new HashSet<(string AddonName, uint NodeId)>();
        var visible = new List<InventoryGridLayout.VisualGridSlot>(50);
        InventoryGridLayout.CollectVisibleArmourySlots(gameGui, visible);
        foreach (var vs in visible)
        {
            if (physicalTargets.Contains((vs.Container, vs.PhysicalSlot)))
            {
                result.Add((vs.AddonName, vs.NodeId));
            }
        }

        return result;
    }

    private HashSet<(string AddonName, uint NodeId)> ResolvePlayerVisualHighlightNodes(
        HashSet<(InventoryType Container, int Slot)> physicalTargets)
    {
        var result = new HashSet<(string AddonName, uint NodeId)>();
        var visible = new List<InventoryGridLayout.VisualGridSlot>(140);
        InventoryGridLayout.CollectVisiblePlayerBagSlots(gameGui, visible);
        foreach (var vs in visible)
        {
            if (physicalTargets.Contains((vs.Container, vs.PhysicalSlot)))
            {
                result.Add((vs.AddonName, vs.NodeId));
            }
        }

        return result;
    }

    private HashSet<(string AddonName, uint NodeId)> ResolveRetainerVisualHighlightNodes(
        HashSet<(InventoryType Container, int Slot)> physicalTargets)
    {
        var result = new HashSet<(string AddonName, uint NodeId)>();
        var visible = new List<InventoryGridLayout.VisualGridSlot>(175);
        InventoryGridLayout.CollectVisibleRetainerSlots(gameGui, visible);
        foreach (var vs in visible)
        {
            if (physicalTargets.Contains((vs.Container, vs.PhysicalSlot)))
            {
                result.Add((vs.AddonName, vs.NodeId));
            }
        }

        return result;
    }

    private void RestoreNodeTint(string addonName, uint nodeId, (short R, short G, short B) orig)
    {
        // FFXIV hides InventoryGrid* / RetainerGrid* addons instead of destroying them,
        // so we MUST restore tints even while IsVisible == false.
        var ptr = gameGui.GetAddonByName(addonName);
        if (ptr.IsNull)
        {
            return;
        }

        var addon = (AtkUnitBase*)ptr.Address;
        if (addon == null)
        {
            return;
        }

        var node = addon->GetNodeById(nodeId);
        if (node == null)
        {
            return;
        }

        node->AddRed = orig.R;
        node->AddGreen = orig.G;
        node->AddBlue = orig.B;
    }

    private void SweepOrphanedHighlights()
    {
        foreach (var addonName in AllGridAddons)
        {
            var ptr = gameGui.GetAddonByName(addonName);
            if (ptr.IsNull)
            {
                continue;
            }

            var addon = (AtkUnitBase*)ptr.Address;
            if (addon == null)
            {
                continue;
            }

            uint startNode = string.Equals(addonName, "ArmouryBoard", StringComparison.Ordinal) ? 71u : 3u;
            uint endNode = string.Equals(addonName, "ArmouryBoard", StringComparison.Ordinal) ? 121u : 38u;
            for (uint nodeId = startNode; nodeId < endNode; nodeId++)
            {
                var node = addon->GetNodeById(nodeId);
                if (node == null)
                {
                    continue;
                }

                var isGold = node->AddRed == DepositOrSellTint.R
                    && node->AddGreen == DepositOrSellTint.G
                    && node->AddBlue == DepositOrSellTint.B;
                var isTeal = node->AddRed == RetainerWithdrawTint.R
                    && node->AddGreen == RetainerWithdrawTint.G
                    && node->AddBlue == RetainerWithdrawTint.B;
                if (isGold || isTeal)
                {
                    node->AddRed = 0;
                    node->AddGreen = 0;
                    node->AddBlue = 0;
                }
            }
        }
    }

    private void ClearAllHighlights()
    {
        foreach (var (key, orig) in activeTints)
        {
            RestoreNodeTint(key.AddonName, key.NodeId, orig);
        }

        activeTints.Clear();
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        ClearAllHighlights();
    }
}
