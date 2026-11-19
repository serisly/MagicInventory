using System;
using System.Collections.Generic;
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
/// - Retainer Magic Sort (InventoryRetainerLarge, InventoryRetainer): highlights player bag deposits (gold)
///   AND retainer bag withdrawals (teal) simultaneously on hover.
/// Uses ItemOrderModule sorters so highlights match visual /isort order 1:1 and restores hidden grids cleanly.
/// </summary>
public sealed unsafe class InventoryHighlightController : IDisposable
{
    private const double PollIntervalMs = 200.0;

    // Warm gold for items leaving player bags (sell / list / deposit into retainer)
    private static readonly (short R, short G, short B) DepositOrSellTint = (72, 46, -24);

    // Crisp teal for items leaving retainer bags (tracked materials / Market / Vendor / misplaced withdrawals)
    private static readonly (short R, short G, short B) RetainerWithdrawTint = (-18, 64, 76);

    private static readonly string[] ExpansionGridAddons =
    [
        "InventoryGrid0E",
        "InventoryGrid1E",
        "InventoryGrid2E",
        "InventoryGrid3E",
    ];

    private static readonly string[] LargeGridAddons =
    [
        "InventoryGrid0",
        "InventoryGrid1",
    ];

    private static readonly string[] RetainerGridAddons =
    [
        "RetainerGrid0",
        "RetainerGrid1",
        "RetainerGrid2",
        "RetainerGrid3",
        "RetainerGrid4",
    ];

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
        if ((now - lastPollUtc).TotalMilliseconds < PollIntervalMs)
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

        if (hoveredRetainerSortAddon != null)
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
            }

            if (withdrawSlots.Count > 0)
            {
                foreach (var key in ResolveRetainerVisualHighlightNodes(withdrawSlots))
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

    private HashSet<(string AddonName, uint NodeId)> ResolvePlayerVisualHighlightNodes(
        HashSet<(InventoryType Container, int Slot)> physicalTargets)
    {
        var result = new HashSet<(string AddonName, uint NodeId)>();

        // Build visual slot list (page 0..3, slot 0..34) from ItemOrderModule.InventorySorter
        var visualCoords = new List<(int Page, int Slot)>(physicalTargets.Count);
        var orderModule = ItemOrderModule.Instance();
        var sorter = orderModule != null ? orderModule->InventorySorter : null;

        if (sorter != null && sorter->Items.Count > 0)
        {
            var perPage = sorter->ItemsPerPage > 0 ? sorter->ItemsPerPage : 35;
            for (var v = 0; v < sorter->Items.Count; v++)
            {
                var entryPtr = sorter->Items[v].Value;
                if (entryPtr == null)
                {
                    continue;
                }

                var physType = entryPtr->Page switch
                {
                    0 => InventoryType.Inventory1,
                    1 => InventoryType.Inventory2,
                    2 => InventoryType.Inventory3,
                    3 => InventoryType.Inventory4,
                    _ => InventoryType.Invalid,
                };

                if (physType != InventoryType.Invalid && physicalTargets.Contains((physType, entryPtr->Slot)))
                {
                    visualCoords.Add((v / perPage, v % perPage));
                }
            }
        }
        else
        {
            foreach (var (container, slot) in physicalTargets)
            {
                var page = container switch
                {
                    InventoryType.Inventory1 => 0,
                    InventoryType.Inventory2 => 1,
                    InventoryType.Inventory3 => 2,
                    InventoryType.Inventory4 => 3,
                    _ => -1,
                };
                if (page >= 0 && slot is >= 0 and < 35)
                {
                    visualCoords.Add((page, slot));
                }
            }
        }

        if (visualCoords.Count == 0)
        {
            return result;
        }

        if (NativeUiHelpers.IsAddonOpenAndReady(gameGui, "InventoryExpansion"))
        {
            foreach (var (page, slot) in visualCoords)
            {
                if (page is >= 0 and < 4)
                {
                    result.Add((ExpansionGridAddons[page], (uint)(slot + 3)));
                }
            }
        }
        else if (NativeUiHelpers.IsAddonOpenAndReady(gameGui, "InventoryLarge"))
        {
            var invLarge = NativeUiHelpers.GetReadyAddon(gameGui, "InventoryLarge");
            var tabOffset = invLarge != null && invLarge->AtkValuesCount > 0 && invLarge->AtkValues[0].Int == 1 ? 2 : 0;
            foreach (var (page, slot) in visualCoords)
            {
                var relPage = page - tabOffset;
                if (relPage is >= 0 and < 2)
                {
                    result.Add((LargeGridAddons[relPage], (uint)(slot + 3)));
                }
            }
        }
        else if (NativeUiHelpers.IsAddonOpenAndReady(gameGui, "Inventory"))
        {
            var inv = NativeUiHelpers.GetReadyAddon(gameGui, "Inventory");
            var activeTab = inv != null && inv->AtkValuesCount > 0 ? inv->AtkValues[0].Int : 0;
            foreach (var (page, slot) in visualCoords)
            {
                if (page == activeTab)
                {
                    result.Add(("InventoryGrid", (uint)(slot + 3)));
                }
            }
        }

        return result;
    }

    private HashSet<(string AddonName, uint NodeId)> ResolveRetainerVisualHighlightNodes(
        HashSet<(InventoryType Container, int Slot)> physicalTargets)
    {
        var result = new HashSet<(string AddonName, uint NodeId)>();

        // Build visual slot list (page 0..4, slot 0..34) from ItemOrderModule.GetActiveRetainerSorter()
        var visualCoords = new List<(int Page, int Slot)>(physicalTargets.Count);
        var orderModule = ItemOrderModule.Instance();
        var sorter = orderModule != null ? orderModule->GetActiveRetainerSorter() : null;

        if (sorter != null && sorter->Items.Count > 0)
        {
            var perPage = sorter->ItemsPerPage > 0 ? sorter->ItemsPerPage : 35;
            for (var v = 0; v < sorter->Items.Count; v++)
            {
                var entryPtr = sorter->Items[v].Value;
                if (entryPtr == null)
                {
                    continue;
                }

                var physType = entryPtr->Page switch
                {
                    0 => InventoryType.RetainerPage1,
                    1 => InventoryType.RetainerPage2,
                    2 => InventoryType.RetainerPage3,
                    3 => InventoryType.RetainerPage4,
                    4 => InventoryType.RetainerPage5,
                    5 => InventoryType.RetainerPage6,
                    6 => InventoryType.RetainerPage7,
                    _ => InventoryType.Invalid,
                };

                if (physType != InventoryType.Invalid && physicalTargets.Contains((physType, entryPtr->Slot)))
                {
                    visualCoords.Add((v / perPage, v % perPage));
                }
            }
        }
        else
        {
            // Fallback linear mapping: 7 physical pages x 25 slots -> 5 visual pages x 35 slots
            foreach (var (container, slot) in physicalTargets)
            {
                var rawPage = container switch
                {
                    InventoryType.RetainerPage1 => 0,
                    InventoryType.RetainerPage2 => 1,
                    InventoryType.RetainerPage3 => 2,
                    InventoryType.RetainerPage4 => 3,
                    InventoryType.RetainerPage5 => 4,
                    InventoryType.RetainerPage6 => 5,
                    InventoryType.RetainerPage7 => 6,
                    _ => -1,
                };
                if (rawPage >= 0 && slot is >= 0 and < 25)
                {
                    var flatIndex = rawPage * 25 + slot;
                    visualCoords.Add((flatIndex / 35, flatIndex % 35));
                }
            }
        }

        if (visualCoords.Count == 0)
        {
            return result;
        }

        // RetainerGrid0..4 (visible per active retainer view/tab in InventoryRetainerLarge & InventoryRetainer)
        var matchedNamedGrid = false;
        foreach (var (page, slot) in visualCoords)
        {
            if (page is >= 0 and < 5 && NativeUiHelpers.IsAddonOpenAndReady(gameGui, RetainerGridAddons[page]))
            {
                result.Add((RetainerGridAddons[page], (uint)(slot + 3)));
                matchedNamedGrid = true;
            }
        }

        // Fallback if single RetainerGrid addon is used
        if (!matchedNamedGrid && NativeUiHelpers.IsAddonOpenAndReady(gameGui, "RetainerGrid"))
        {
            var retAddon = NativeUiHelpers.GetReadyAddon(gameGui, "InventoryRetainer");
            var activeTab = retAddon != null && retAddon->AtkValuesCount > 0 ? retAddon->AtkValues[0].Int : 0;
            foreach (var (page, slot) in visualCoords)
            {
                if (page == activeTab)
                {
                    result.Add(("RetainerGrid", (uint)(slot + 3)));
                }
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

            for (uint nodeId = 3; nodeId < 38; nodeId++)
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
