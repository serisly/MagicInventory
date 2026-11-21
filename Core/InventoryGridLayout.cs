using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MagicInventory.Core;

/// <summary>
/// Single source of truth for mapping physical FFXIV InventoryType + Slot coordinates
/// to visible UI grid addons/node IDs (Player Bags, Armoury Chest, and Retainer Grids)
/// and computing accurate screen-space bounding boxes for AtkResNodes.
/// </summary>
public static unsafe class InventoryGridLayout
{
    public const int VisualBagSlotsPerPage = 35;
    public const int VisualRetainerSlotsPerTab = 35;
    public const int VisualRetainerTabCount = 5;
    public const int PhysicalRetainerSlotsPerPage = 25;
    public const uint GridSlotBaseNodeId = 3;
    public const uint ArmourySlotBaseNodeId = 71;

    public static readonly string[] ExpansionGridAddons =
    [
        "InventoryGrid0E",
        "InventoryGrid1E",
        "InventoryGrid2E",
        "InventoryGrid3E",
    ];

    public static readonly string[] LargeGridAddons =
    [
        "InventoryGrid0",
        "InventoryGrid1",
    ];

    public static readonly string[] RetainerGridAddons =
    [
        "RetainerGrid0",
        "RetainerGrid1",
        "RetainerGrid2",
        "RetainerGrid3",
        "RetainerGrid4",
    ];

    public static readonly InventoryType[] ArmouryTabContainers =
    [
        InventoryType.ArmoryMainHand,    // RadioButton Node 7
        InventoryType.ArmoryHead,        // RadioButton Node 8
        InventoryType.ArmoryBody,        // RadioButton Node 9
        InventoryType.ArmoryHands,       // RadioButton Node 10
        InventoryType.ArmoryLegs,        // RadioButton Node 11
        InventoryType.ArmoryFeets,       // RadioButton Node 12
        InventoryType.ArmoryOffHand,     // RadioButton Node 13
        InventoryType.ArmoryEar,         // RadioButton Node 14
        InventoryType.ArmoryNeck,        // RadioButton Node 15
        InventoryType.ArmoryWrist,       // RadioButton Node 16
        InventoryType.ArmoryRings,       // RadioButton Node 17
        InventoryType.ArmorySoulCrystal, // RadioButton Node 18
    ];

    public readonly record struct VisualGridSlot(
        string AddonName,
        uint NodeId,
        InventoryType Container,
        int PhysicalSlot);

    public static int GetActiveArmouryTabIndex(IGameGui gameGui)
    {
        var armoury = NativeUiHelpers.GetReadyAddon(gameGui, "ArmouryBoard");
        if (armoury == null || !armoury->IsVisible)
        {
            return -1;
        }

        var buttons = new List<(float X, float Y, bool Selected)>(12);
        for (uint nodeId = 7; nodeId <= 18; nodeId++)
        {
            var node = armoury->GetNodeById(nodeId);
            if (node == null)
            {
                continue;
            }

            var radio = node->GetAsAtkComponentRadioButton();
            if (radio == null)
            {
                continue;
            }

            buttons.Add((node->X, node->Y, radio->IsSelected));
        }

        if (buttons.Count != 12)
        {
            for (var i = 0; i < buttons.Count; i++)
            {
                if (buttons[i].Selected)
                {
                    return i;
                }
            }

            return -1;
        }

        // Split into left column (6 smallest X) and right column (6 largest X), each sorted top-to-bottom by Y
        buttons.Sort((a, b) => a.X.CompareTo(b.X));
        var left = buttons.GetRange(0, 6);
        var right = buttons.GetRange(6, 6);
        left.Sort((a, b) => a.Y.CompareTo(b.Y));
        right.Sort((a, b) => a.Y.CompareTo(b.Y));

        for (var i = 0; i < 6; i++)
        {
            if (left[i].Selected)
            {
                return i;
            }
        }

        for (var i = 0; i < 6; i++)
        {
            if (right[i].Selected)
            {
                return 6 + i;
            }
        }

        return -1;
    }

    public static InventoryType? GetActiveArmouryTabContainer(IGameGui gameGui)
    {
        var idx = GetActiveArmouryTabIndex(gameGui);
        return idx >= 0 && idx < ArmouryTabContainers.Length ? ArmouryTabContainers[idx] : null;
    }

    public static ItemOrderModuleSorter* GetArmourySorter(ItemOrderModule* orderModule, int tabIdx)
    {
        if (orderModule == null)
        {
            return null;
        }

        return tabIdx switch
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

    /// <summary>
    /// Enumerates all currently visible player-bag visual slots (InventoryExpansion, InventoryLarge, or Inventory).
    /// </summary>
    public static void CollectVisiblePlayerBagSlots(IGameGui gameGui, List<VisualGridSlot> output)
    {
        var visualEntries = new List<(int Page, int Slot, InventoryType Container, int PhysicalSlot)>(140);
        var orderModule = ItemOrderModule.Instance();
        var sorter = orderModule != null ? orderModule->InventorySorter : null;

        if (sorter != null && sorter->Items.Count > 0)
        {
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
                if (physType == InventoryType.Invalid)
                {
                    continue;
                }

                visualEntries.Add((v / VisualBagSlotsPerPage, v % VisualBagSlotsPerPage, physType, entryPtr->Slot));
            }
        }
        else
        {
            for (var p = 0; p < 4; p++)
            {
                var physType = p switch
                {
                    0 => InventoryType.Inventory1,
                    1 => InventoryType.Inventory2,
                    2 => InventoryType.Inventory3,
                    _ => InventoryType.Inventory4,
                };
                for (var s = 0; s < VisualBagSlotsPerPage; s++)
                {
                    visualEntries.Add((p, s, physType, s));
                }
            }
        }

        if (NativeUiHelpers.IsAddonOpenAndReady(gameGui, "InventoryExpansion"))
        {
            foreach (var (page, slot, container, physSlot) in visualEntries)
            {
                if (page is >= 0 and < 4)
                {
                    output.Add(new VisualGridSlot(ExpansionGridAddons[page], (uint)(slot + GridSlotBaseNodeId), container, physSlot));
                }
            }
        }
        else if (NativeUiHelpers.IsAddonOpenAndReady(gameGui, "InventoryLarge"))
        {
            var invLarge = NativeUiHelpers.GetReadyAddon(gameGui, "InventoryLarge");
            var tabOffset = invLarge != null && invLarge->AtkValuesCount > 0 && invLarge->AtkValues[0].Int == 1 ? 2 : 0;
            foreach (var (page, slot, container, physSlot) in visualEntries)
            {
                var relPage = page - tabOffset;
                if (relPage is >= 0 and < 2)
                {
                    output.Add(new VisualGridSlot(LargeGridAddons[relPage], (uint)(slot + GridSlotBaseNodeId), container, physSlot));
                }
            }
        }
        else if (NativeUiHelpers.IsAddonOpenAndReady(gameGui, "Inventory"))
        {
            var inv = NativeUiHelpers.GetReadyAddon(gameGui, "Inventory");
            var activeTab = inv != null && inv->AtkValuesCount > 0 ? inv->AtkValues[0].Int : 0;
            foreach (var (page, slot, container, physSlot) in visualEntries)
            {
                if (page == activeTab)
                {
                    output.Add(new VisualGridSlot("InventoryGrid", (uint)(slot + GridSlotBaseNodeId), container, physSlot));
                }
            }
        }
    }

    /// <summary>
    /// Enumerates all currently visible ArmouryBoard visual slots for the active Armoury tab.
    /// </summary>
    public static void CollectVisibleArmourySlots(IGameGui gameGui, List<VisualGridSlot> output)
    {
        var tabIdx = GetActiveArmouryTabIndex(gameGui);
        if (tabIdx < 0 || tabIdx >= ArmouryTabContainers.Length)
        {
            return;
        }

        var activeContainer = ArmouryTabContainers[tabIdx];
        var orderModule = ItemOrderModule.Instance();
        var sorter = GetArmourySorter(orderModule, tabIdx);

        if (sorter != null && sorter->Items.Count > 0)
        {
            for (var v = 0; v < sorter->Items.Count && v < 50; v++)
            {
                var entryPtr = sorter->Items[v].Value;
                if (entryPtr == null)
                {
                    continue;
                }

                output.Add(new VisualGridSlot("ArmouryBoard", (uint)(ArmourySlotBaseNodeId + v), activeContainer, entryPtr->Slot));
            }
        }
        else
        {
            for (var s = 0; s < 50; s++)
            {
                output.Add(new VisualGridSlot("ArmouryBoard", (uint)(ArmourySlotBaseNodeId + s), activeContainer, s));
            }
        }
    }

    /// <summary>
    /// Enumerates all currently visible Retainer grid visual slots (RetainerGrid0..4 or RetainerGrid).
    /// Always divides the flat visual index by 35 (5 visual tabs × 35 slots = 175), NOT sorter->ItemsPerPage (which can be 25).
    /// </summary>
    public static void CollectVisibleRetainerSlots(IGameGui gameGui, List<VisualGridSlot> output)
    {
        var hasNamedGrids = false;
        Span<bool> gridReady = stackalloc bool[VisualRetainerTabCount];
        for (var i = 0; i < VisualRetainerTabCount; i++)
        {
            gridReady[i] = NativeUiHelpers.IsAddonOpenAndReady(gameGui, RetainerGridAddons[i]);
            if (gridReady[i])
            {
                hasNamedGrids = true;
            }
        }

        var singleGridReady = !hasNamedGrids && NativeUiHelpers.IsAddonOpenAndReady(gameGui, "RetainerGrid");
        if (!hasNamedGrids && !singleGridReady)
        {
            return;
        }

        var activeSingleTab = 0;
        if (singleGridReady)
        {
            var retAddon = NativeUiHelpers.GetReadyAddon(gameGui, "InventoryRetainer");
            activeSingleTab = retAddon != null && retAddon->AtkValuesCount > 0 ? retAddon->AtkValues[0].Int : 0;
        }

        var orderModule = ItemOrderModule.Instance();
        var sorter = orderModule != null ? orderModule->GetActiveRetainerSorter() : null;

        if (sorter != null && sorter->Items.Count > 0)
        {
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
                if (physType == InventoryType.Invalid)
                {
                    continue;
                }

                // FFXIV renders retainer bags across 5 visual tabs of 35 slots (175 total).
                var visualTab = v / VisualRetainerSlotsPerTab;
                var visualSlot = v % VisualRetainerSlotsPerTab;
                EmitRetainerVisualSlot(visualTab, visualSlot, physType, entryPtr->Slot, hasNamedGrids, gridReady, singleGridReady, activeSingleTab, output);
            }
        }
        else
        {
            // Linear fallback: 7 physical pages × 25 slots -> 5 visual tabs × 35 slots
            for (var p = 0; p < 7; p++)
            {
                var physType = p switch
                {
                    0 => InventoryType.RetainerPage1,
                    1 => InventoryType.RetainerPage2,
                    2 => InventoryType.RetainerPage3,
                    3 => InventoryType.RetainerPage4,
                    4 => InventoryType.RetainerPage5,
                    5 => InventoryType.RetainerPage6,
                    _ => InventoryType.RetainerPage7,
                };

                for (var s = 0; s < PhysicalRetainerSlotsPerPage; s++)
                {
                    var flatIndex = p * PhysicalRetainerSlotsPerPage + s;
                    var visualTab = flatIndex / VisualRetainerSlotsPerTab;
                    var visualSlot = flatIndex % VisualRetainerSlotsPerTab;
                    EmitRetainerVisualSlot(visualTab, visualSlot, physType, s, hasNamedGrids, gridReady, singleGridReady, activeSingleTab, output);
                }
            }
        }
    }

    private static void EmitRetainerVisualSlot(
        int visualTab,
        int visualSlot,
        InventoryType physType,
        int physSlot,
        bool hasNamedGrids,
        ReadOnlySpan<bool> gridReady,
        bool singleGridReady,
        int activeSingleTab,
        List<VisualGridSlot> output)
    {
        if (hasNamedGrids)
        {
            if (visualTab is >= 0 and < VisualRetainerTabCount && gridReady[visualTab])
            {
                output.Add(new VisualGridSlot(RetainerGridAddons[visualTab], (uint)(visualSlot + GridSlotBaseNodeId), physType, physSlot));
            }
        }
        else if (singleGridReady && visualTab == activeSingleTab)
        {
            output.Add(new VisualGridSlot("RetainerGrid", (uint)(visualSlot + GridSlotBaseNodeId), physType, physSlot));
        }
    }

    /// <summary>
    /// Maps AgentItemDetail visual bag tab codes (48..51) back to physical Inventory1..4 + physical slot
    /// using ItemOrderModule.InventorySorter.
    /// </summary>
    public static void NormalizeVisualBagSlot(ref InventoryType? container, ref short? slot)
    {
        if (!container.HasValue || !slot.HasValue || slot.Value < 0)
        {
            return;
        }

        var rawType = (int)container.Value;
        if (rawType is < 48 or > 51)
        {
            return;
        }

        var visualTab = rawType - 48;
        var visualSlot = (int)slot.Value;
        var orderModule = ItemOrderModule.Instance();
        var sorter = orderModule != null ? orderModule->InventorySorter : null;
        if (sorter != null && sorter->Items.Count > 0)
        {
            var perPage = sorter->ItemsPerPage > 0 ? sorter->ItemsPerPage : 35;
            var visualIndex = visualTab * perPage + visualSlot;
            if (visualIndex >= 0 && visualIndex < sorter->Items.Count)
            {
                var entryPtr = sorter->Items[visualIndex].Value;
                if (entryPtr != null)
                {
                    container = entryPtr->Page switch
                    {
                        0 => InventoryType.Inventory1,
                        1 => InventoryType.Inventory2,
                        2 => InventoryType.Inventory3,
                        3 => InventoryType.Inventory4,
                        _ => container,
                    };
                    slot = (short)entryPtr->Slot;
                    return;
                }
            }
        }

        container = visualTab switch
        {
            0 => InventoryType.Inventory1,
            1 => InventoryType.Inventory2,
            2 => InventoryType.Inventory3,
            3 => InventoryType.Inventory4,
            _ => container,
        };
    }

    /// <summary>
    /// Computes accurate screen-space bounding box for an AtkResNode inside an AtkUnitBase,
    /// properly applying each ancestor's scale to accumulated child offsets (NOT the child's own scale to its own X/Y).
    /// </summary>
    public static bool TryGetNodeScreenRect(
        AtkUnitBase* addon,
        AtkResNode* node,
        Vector2 vpPos,
        out Vector2 min,
        out Vector2 max)
    {
        min = Vector2.Zero;
        max = Vector2.Zero;
        if (addon == null || node == null)
        {
            return false;
        }

        var posX = node->X;
        var posY = node->Y;
        var scaleX = node->ScaleX;
        var scaleY = node->ScaleY;
        var cur = node->ParentNode;
        var guard = 0;
        while (cur != null && cur->ParentNode != null && guard++ < 64)
        {
            posX = posX * cur->ScaleX + cur->X;
            posY = posY * cur->ScaleY + cur->Y;
            scaleX *= cur->ScaleX;
            scaleY *= cur->ScaleY;
            cur = cur->ParentNode;
        }

        var addonScale = addon->Scale > 0.01f ? addon->Scale : 1f;
        var left = vpPos.X + addon->X + posX * addonScale;
        var top = vpPos.Y + addon->Y + posY * addonScale;
        var width = node->Width * scaleX * addonScale;
        var height = node->Height * scaleY * addonScale;
        if (width < 4f || height < 4f)
        {
            return false;
        }

        min = new Vector2(left, top);
        max = new Vector2(left + width, top + height);
        return true;
    }
}
