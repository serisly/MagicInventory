using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using MagicInventory.Core;
using MagicInventory.Services;
using MagicInventory.Services.Triage;

namespace MagicInventory.UI;

/// <summary>
/// Renders a crisp, non-intrusive X-ray destination preview (tiny badge + soft highlight)
/// over every non-staying slot in open Bags and Armoury Chest while hovering the Inventory Magic Sort button.
/// </summary>
public sealed unsafe class InventoryRouteOverlay : IDisposable
{
    private readonly record struct SlotBadge(string Label, Vector4 Color);

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

    private static readonly InventoryType[] ArmouryTabContainers =
    [
        InventoryType.ArmoryMainHand,
        InventoryType.ArmoryHead,
        InventoryType.ArmoryBody,
        InventoryType.ArmoryHands,
        InventoryType.ArmoryLegs,
        InventoryType.ArmoryFeets,
        InventoryType.ArmoryOffHand,
        InventoryType.ArmoryEar,
        InventoryType.ArmoryNeck,
        InventoryType.ArmoryWrist,
        InventoryType.ArmoryRings,
        InventoryType.ArmorySoulCrystal,
    ];

    private static readonly Vector4 ColorRetainer = new(0.56f, 0.80f, 0.98f, 1.0f);
    private static readonly Vector4 ColorMarket = new(0.98f, 0.82f, 0.36f, 1.0f);
    private static readonly Vector4 ColorVendor = new(0.96f, 0.56f, 0.44f, 1.0f);
    private static readonly Vector4 ColorGc = new(0.52f, 0.90f, 0.58f, 1.0f);
    private static readonly Vector4 ColorUse = new(0.46f, 0.94f, 0.90f, 1.0f);
    private static readonly Vector4 ColorGlamour = new(0.94f, 0.66f, 0.92f, 1.0f);
    private static readonly Vector4 ColorArmoire = new(0.78f, 0.70f, 0.98f, 1.0f);
    private static readonly Vector4 ColorGoldSaucer = new(0.98f, 0.74f, 0.30f, 1.0f);
    private static readonly Vector4 ColorMateria = new(0.96f, 0.62f, 0.96f, 1.0f);
    private static readonly Vector4 ColorArmouryMove = new(0.45f, 0.88f, 0.80f, 1.0f);

    private readonly IGameGui gameGui;
    private readonly IDataManager dataManager;
    private readonly Configuration configuration;
    private readonly InventorySafetyEvaluator safety;
    private readonly MagicSortService magicSort;

    private string? hoveredInventoryAddon;
    private DateTime lastCacheUtc = DateTime.MinValue;
    private readonly Dictionary<(string AddonName, uint NodeId), SlotBadge> cachedBadges = new();

    public InventoryRouteOverlay(
        IGameGui gameGui,
        IDataManager dataManager,
        Configuration configuration,
        InventorySafetyEvaluator safety,
        MagicSortService magicSort)
    {
        this.gameGui = gameGui;
        this.dataManager = dataManager;
        this.configuration = configuration;
        this.safety = safety;
        this.magicSort = magicSort;
    }

    public void SetHoveredInventorySortAddon(string addonName, bool isHovered)
    {
        if (isHovered)
        {
            hoveredInventoryAddon = addonName;
            lastCacheUtc = DateTime.MinValue;
        }
        else if (string.Equals(hoveredInventoryAddon, addonName, StringComparison.Ordinal))
        {
            hoveredInventoryAddon = null;
            cachedBadges.Clear();
        }
    }

    public void Draw()
    {
        if (hoveredInventoryAddon == null || !configuration.EnableHoverHighlights)
        {
            return;
        }

        if (!NativeUiHelpers.IsAddonOpenAndReady(gameGui, hoveredInventoryAddon))
        {
            hoveredInventoryAddon = null;
            cachedBadges.Clear();
            return;
        }

        var now = DateTime.UtcNow;
        if ((now - lastCacheUtc).TotalMilliseconds >= 150.0)
        {
            lastCacheUtc = now;
            RebuildBadgeMap();
        }

        if (cachedBadges.Count == 0)
        {
            return;
        }

        var dl = ImGui.GetForegroundDrawList();
        var vpPos = ImGui.GetMainViewport().Pos;
        var font = ImGui.GetFont();

        foreach (var ((addonName, nodeId), badge) in cachedBadges)
        {
            var addon = NativeUiHelpers.GetReadyAddon(gameGui, addonName);
            if (addon == null)
            {
                continue;
            }

            var node = addon->GetNodeById(nodeId);
            if (node == null || !node->IsVisible())
            {
                continue;
            }

            var addonScale = addon->Scale > 0.01f ? addon->Scale : 1f;
            var rx = 0f;
            var ry = 0f;
            var sx = 1f;
            var sy = 1f;
            var cur = node;
            while (cur != null && cur->ParentNode != null)
            {
                rx = rx * cur->ScaleX + cur->X;
                ry = ry * cur->ScaleY + cur->Y;
                sx *= cur->ScaleX;
                sy *= cur->ScaleY;
                cur = cur->ParentNode;
            }

            var left = vpPos.X + addon->X + rx * addonScale;
            var top = vpPos.Y + addon->Y + ry * addonScale;
            var width = node->Width * sx * addonScale;
            var height = node->Height * sy * addonScale;
            if (width < 12f || height < 12f)
            {
                continue;
            }

            var min = new Vector2(left + 1f, top + 1f);
            var max = new Vector2(left + width - 1f, top + height - 1f);

            // Soft highlight tint + subtle border
            var washCol = ImGui.ColorConvertFloat4ToU32(new Vector4(badge.Color.X, badge.Color.Y, badge.Color.Z, 0.20f));
            var borderCol = ImGui.ColorConvertFloat4ToU32(new Vector4(badge.Color.X, badge.Color.Y, badge.Color.Z, 0.68f));
            dl.AddRectFilled(min, max, washCol, 4f);
            dl.AddRect(min, max, borderCol, 4f, ImDrawFlags.None, 1.25f);

            // Tiny compact label pill along bottom of the slot
            var baseFontSize = MathF.Max(1f, ImGui.GetFontSize());
            var fontSize = Math.Clamp(height * 0.25f, 10f, 12.5f);
            var scale = fontSize / baseFontSize;
            var label = FitLabelToWidth(scale, badge.Label, width + 4f);
            var textSize = ImGui.CalcTextSize(label) * scale;

            var pillW = MathF.Min(width + 4f, textSize.X + 6f);
            var pillH = textSize.Y + 2f;
            var pillX = left + (width - pillW) * 0.5f;
            var pillY = top + height - pillH - 1f;

            var pillBg = ImGui.ColorConvertFloat4ToU32(new Vector4(0.06f, 0.06f, 0.08f, 0.88f));
            var textCol = ImGui.ColorConvertFloat4ToU32(badge.Color);

            dl.AddRectFilled(
                new Vector2(pillX, pillY),
                new Vector2(pillX + pillW, pillY + pillH),
                pillBg,
                2.5f);
            dl.AddText(
                font,
                fontSize,
                new Vector2(pillX + (pillW - textSize.X) * 0.5f, pillY + 1f),
                textCol,
                label);
        }
    }

    private void RebuildBadgeMap()
    {
        cachedBadges.Clear();
        var manager = InventoryManager.Instance();
        var itemSheet = dataManager.GetExcelSheet<Item>();
        var entries = magicSort.GetLatestManifest();
        if (manager == null || itemSheet == null || entries.Count == 0)
        {
            return;
        }

        var gearsetIds = safety.BuildGearsetItemIds();
        var unlockedTop = safety.BuildUnlockedJobTopIlvl();

        // Precompute which Armoury tabs have at least one free slot (for Bag -> Armoury preview)
        var armouryHasSpace = new Dictionary<InventoryType, bool>();
        foreach (var armType in InventorySafetyEvaluator.ArmouryGearContainers)
        {
            armouryHasSpace[armType] = HasEmptySlot(manager, armType);
        }

        armouryHasSpace[InventoryType.ArmorySoulCrystal] = HasEmptySlot(manager, InventoryType.ArmorySoulCrystal);

        // 1. Player Bags
        var playerBadges = new Dictionary<(InventoryType Container, int Slot), SlotBadge>();
        foreach (var bag in InventorySafetyEvaluator.PlayerBagContainers)
        {
            var container = manager->GetInventoryContainer(bag);
            if (!InventorySafetyEvaluator.IsContainerReadable(container))
            {
                continue;
            }

            for (var i = 0; i < container->Size; i++)
            {
                var slot = InventorySafetyEvaluator.GetSlot(container, i);
                if (slot == null || slot->ItemId == 0 || slot->Quantity <= 0)
                {
                    continue;
                }

                var badge = ResolveSlotBadge(
                    slot,
                    inArmoury: false,
                    itemSheet,
                    entries,
                    gearsetIds,
                    unlockedTop,
                    armouryHasSpace);
                if (badge.HasValue)
                {
                    playerBadges[(bag, i)] = badge.Value;
                }
            }
        }

        if (playerBadges.Count > 0)
        {
            MapPlayerBadgesToVisualNodes(playerBadges);
        }

        // 2. Visible Armoury Chest tab (if open)
        var activeTab = InventoryHighlightController.GetActiveArmouryTabIndex(gameGui);
        if (activeTab >= 0 && activeTab < ArmouryTabContainers.Length)
        {
            var armType = ArmouryTabContainers[activeTab];
            var container = manager->GetInventoryContainer(armType);
            if (InventorySafetyEvaluator.IsContainerReadable(container))
            {
                var armBadges = new Dictionary<int, SlotBadge>();
                for (var i = 0; i < container->Size; i++)
                {
                    var slot = InventorySafetyEvaluator.GetSlot(container, i);
                    if (slot == null || slot->ItemId == 0 || slot->Quantity <= 0)
                    {
                        continue;
                    }

                    var badge = ResolveSlotBadge(
                        slot,
                        inArmoury: true,
                        itemSheet,
                        entries,
                        gearsetIds,
                        unlockedTop,
                        armouryHasSpace);
                    if (badge.HasValue)
                    {
                        armBadges[i] = badge.Value;
                    }
                }

                if (armBadges.Count > 0)
                {
                    MapArmouryBadgesToVisualNodes(activeTab, armBadges);
                }
            }
        }
    }

    private SlotBadge? ResolveSlotBadge(
        InventoryItem* slot,
        bool inArmoury,
        Lumina.Excel.ExcelSheet<Item> itemSheet,
        IReadOnlyDictionary<uint, MagicSortEntry> entries,
        HashSet<uint> gearsetIds,
        Dictionary<uint, int> unlockedTop,
        Dictionary<InventoryType, bool> armouryHasSpace)
    {
        var baseId = HoldingsService.NormalizeBaseItemId(slot->ItemId);
        if (!MagicSortService.CanLockOrTagBaseId(baseId))
        {
            return null;
        }

        var row = itemSheet.GetRowOrDefault(baseId);
        if (row is null || row.Value.FilterGroup is 11 or 16)
        {
            return null;
        }

        if (InventorySafetyEvaluator.IsMateriaExtractable(row.Value, slot))
        {
            return new SlotBadge("Materia", ColorMateria);
        }

        string effectiveRoute = "Keep";
        MagicSortEntry? entry = null;

        if (!safety.IsItemTracked(baseId)
            && !gearsetIds.Contains(baseId)
            && row.Value.ItemUICategory.RowId != 62
            && row.Value.Rarity < 4
            && entries.TryGetValue(baseId, out entry)
            && !string.Equals(entry.Route, "Keep", StringComparison.OrdinalIgnoreCase))
        {
            effectiveRoute = safety.ResolveEffectiveSlotRoute(entry, row.Value, slot, entries, gearsetIds, unlockedTop) ?? "Keep";
        }

        if (string.Equals(effectiveRoute, "Keep", StringComparison.OrdinalIgnoreCase))
        {
            if (!inArmoury && row.Value.EquipSlotCategory.RowId > 0)
            {
                var targetArm = InventorySafetyEvaluator.ResolveArmouryContainerForItem(row.Value);
                if (targetArm.HasValue && armouryHasSpace.GetValueOrDefault(targetArm.Value))
                {
                    return new SlotBadge("Armoury", ColorArmouryMove);
                }
            }

            // Stays right where it is (Bag or Armoury) -> no badge, no highlight
            return null;
        }

        return effectiveRoute switch
        {
            "Retainer" => new SlotBadge(
                string.IsNullOrWhiteSpace(entry?.TargetRetainerName) ? "Retainer" : entry.TargetRetainerName,
                ColorRetainer),
            "Market" => new SlotBadge("Market", ColorMarket),
            "Vendor" => new SlotBadge("Vendor", ColorVendor),
            "GrandCompany" => new SlotBadge("GC", ColorGc),
            "Use" => new SlotBadge("Use", ColorUse),
            "Glamour" => new SlotBadge("Glam", ColorGlamour),
            "Armoire" => new SlotBadge("Armoire", ColorArmoire),
            "GoldSaucer" => new SlotBadge("Saucer", ColorGoldSaucer),
            "Materia" => new SlotBadge("Materia", ColorMateria),
            _ => null,
        };
    }

    private void MapPlayerBadgesToVisualNodes(Dictionary<(InventoryType Container, int Slot), SlotBadge> playerBadges)
    {
        var visualEntries = new List<(int Page, int Slot, SlotBadge Badge)>(playerBadges.Count);
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

                if (physType != InventoryType.Invalid && playerBadges.TryGetValue((physType, entryPtr->Slot), out var badge))
                {
                    visualEntries.Add((v / perPage, v % perPage, badge));
                }
            }
        }
        else
        {
            foreach (var ((container, slot), badge) in playerBadges)
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
                    visualEntries.Add((page, slot, badge));
                }
            }
        }

        if (NativeUiHelpers.IsAddonOpenAndReady(gameGui, "InventoryExpansion"))
        {
            foreach (var (page, slot, badge) in visualEntries)
            {
                if (page is >= 0 and < 4)
                {
                    cachedBadges[(ExpansionGridAddons[page], (uint)(slot + 3))] = badge;
                }
            }
        }
        else if (NativeUiHelpers.IsAddonOpenAndReady(gameGui, "InventoryLarge"))
        {
            var invLarge = NativeUiHelpers.GetReadyAddon(gameGui, "InventoryLarge");
            var tabOffset = invLarge != null && invLarge->AtkValuesCount > 0 && invLarge->AtkValues[0].Int == 1 ? 2 : 0;
            foreach (var (page, slot, badge) in visualEntries)
            {
                var relPage = page - tabOffset;
                if (relPage is >= 0 and < 2)
                {
                    cachedBadges[(LargeGridAddons[relPage], (uint)(slot + 3))] = badge;
                }
            }
        }
        else if (NativeUiHelpers.IsAddonOpenAndReady(gameGui, "Inventory"))
        {
            var inv = NativeUiHelpers.GetReadyAddon(gameGui, "Inventory");
            var activeTab = inv != null && inv->AtkValuesCount > 0 ? inv->AtkValues[0].Int : 0;
            foreach (var (page, slot, badge) in visualEntries)
            {
                if (page == activeTab)
                {
                    cachedBadges[("InventoryGrid", (uint)(slot + 3))] = badge;
                }
            }
        }
    }

    private void MapArmouryBadgesToVisualNodes(int tabIdx, Dictionary<int, SlotBadge> armBadges)
    {
        var orderModule = ItemOrderModule.Instance();
        ItemOrderModuleSorter* sorter = null;
        if (orderModule != null)
        {
            sorter = tabIdx switch
            {
                0 => orderModule->ArmouryMainHandSorter,
                1 => orderModule->ArmouryHeadSorter,
                2 => orderModule->ArmouryBodySorter,
                3 => orderModule->ArmouryHandsSorter,
                4 => orderModule->ArmouryLegsSorter,
                5 => orderModule->ArmouryFeetSorter,
                6 => orderModule->ArmouryOffHandSorter,
                7 => orderModule->ArmouryEarsSorter,
                8 => orderModule->ArmouryNeckSorter,
                9 => orderModule->ArmouryWristsSorter,
                10 => orderModule->ArmouryRingsSorter,
                11 => orderModule->ArmourySoulCrystalSorter,
                _ => null,
            };
        }

        if (sorter != null && sorter->Items.Count > 0)
        {
            for (var v = 0; v < sorter->Items.Count && v < 50; v++)
            {
                var entryPtr = sorter->Items[v].Value;
                if (entryPtr != null && armBadges.TryGetValue(entryPtr->Slot, out var badge))
                {
                    cachedBadges[("ArmouryBoard", (uint)(71 + v))] = badge;
                }
            }
        }
        else
        {
            foreach (var (slot, badge) in armBadges)
            {
                if (slot is >= 0 and < 50)
                {
                    cachedBadges[("ArmouryBoard", (uint)(71 + slot))] = badge;
                }
            }
        }
    }

    private static bool HasEmptySlot(InventoryManager* mgr, InventoryType containerType)
    {
        var c = mgr->GetInventoryContainer(containerType);
        if (!InventorySafetyEvaluator.IsContainerReadable(c))
        {
            return false;
        }

        for (var i = 0; i < c->Size; i++)
        {
            var s = c->GetInventorySlot(i);
            if (s == null || s->ItemId == 0 || s->Quantity <= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static string FitLabelToWidth(float scale, string label, float maxWidth)
    {
        if (ImGui.CalcTextSize(label).X * scale <= maxWidth)
        {
            return label;
        }

        for (var len = label.Length - 1; len >= 2; len--)
        {
            var trimmed = label[..len];
            if (ImGui.CalcTextSize(trimmed).X * scale <= maxWidth)
            {
                return trimmed;
            }
        }

        return label.Length > 2 ? label[..2] : label;
    }

    public void Dispose()
    {
        hoveredInventoryAddon = null;
        cachedBadges.Clear();
    }
}
