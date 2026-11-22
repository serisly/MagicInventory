using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MagicInventory.Core;

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

        Span<(float X, float Y, bool Selected)> buttons = stackalloc (float, float, bool)[12];
        var count = 0;
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

            if (count < 12)
            {
                buttons[count++] = (node->X, node->Y, radio->IsSelected);
            }
        }

        if (count != 12)
        {
            return -1;
        }

        SortByX(buttons);
        var left = buttons[..6];
        var right = buttons[6..];
        SortByY(left);
        SortByY(right);

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

        static void SortByX(Span<(float X, float Y, bool Selected)> span)
        {
            for (var i = 1; i < span.Length; i++)
            {
                var key = span[i];
                var j = i - 1;
                while (j >= 0 && span[j].X > key.X)
                {
                    span[j + 1] = span[j];
                    j--;
                }

                span[j + 1] = key;
            }
        }

        static void SortByY(Span<(float X, float Y, bool Selected)> span)
        {
            for (var i = 1; i < span.Length; i++)
            {
                var key = span[i];
                var j = i - 1;
                while (j >= 0 && span[j].Y > key.Y)
                {
                    span[j + 1] = span[j];
                    j--;
                }

                span[j + 1] = key;
            }
        }
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
        var manager = InventoryManager.Instance();
        var physContainer = manager != null ? manager->GetInventoryContainer(activeContainer) : null;
        var containerCap = physContainer != null && physContainer->Size > 0
            ? Math.Min((int)physContainer->Size, 50)
            : 50;

        if (sorter != null && sorter->Items.Count > 0)
        {
            for (var v = 0; v < sorter->Items.Count && v < containerCap; v++)
            {
                var entryPtr = sorter->Items[v].Value;
                if (entryPtr == null || entryPtr->Slot >= containerCap)
                {
                    continue;
                }

                output.Add(new VisualGridSlot("ArmouryBoard", (uint)(ArmourySlotBaseNodeId + v), activeContainer, entryPtr->Slot));
            }
        }
        else
        {
            for (var s = 0; s < containerCap; s++)
            {
                output.Add(new VisualGridSlot("ArmouryBoard", (uint)(ArmourySlotBaseNodeId + s), activeContainer, s));
            }
        }
    }

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

                var visualTab = v / VisualRetainerSlotsPerTab;
                var visualSlot = v % VisualRetainerSlotsPerTab;
                EmitRetainerVisualSlot(visualTab, visualSlot, physType, entryPtr->Slot, hasNamedGrids, gridReady, singleGridReady, activeSingleTab, output);
            }
        }
        else
        {
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

        var posX = node->X + node->OriginX * (1f - node->ScaleX);
        var posY = node->Y + node->OriginY * (1f - node->ScaleY);
        var scaleX = node->ScaleX;
        var scaleY = node->ScaleY;
        var cur = node->ParentNode;
        var guard = 0;
        while (cur != null && cur->ParentNode != null && guard++ < 64)
        {
            posX = posX * cur->ScaleX + cur->X + cur->OriginX * (1f - cur->ScaleX);
            posY = posY * cur->ScaleY + cur->Y + cur->OriginY * (1f - cur->ScaleY);
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

    public static bool TryGetAddonScreenRect(AtkUnitBase* addon, Vector2 vpPos, out Vector2 min, out Vector2 max)
    {
        min = Vector2.Zero;
        max = Vector2.Zero;
        if (addon == null || addon->RootNode == null || !addon->IsVisible || !addon->RootNode->IsVisible())
        {
            return false;
        }

        var scale = addon->Scale > 0.01f ? addon->Scale : 1f;
        var w = addon->RootNode->Width * scale;
        var h = addon->RootNode->Height * scale;
        if (w < 8f || h < 8f)
        {
            return false;
        }

        min = new Vector2(vpPos.X + addon->X, vpPos.Y + addon->Y);
        max = new Vector2(min.X + w, min.Y + h);
        return true;
    }

    public static void CollectHigherZAddonRects(
        AtkUnitBase* targetAddon,
        Vector2 vpPos,
        Vector2 vpSize,
        List<(Vector2 Min, Vector2 Max)> output)
    {
        output.Clear();
        if (targetAddon == null)
        {
            return;
        }

        var unitManager = RaptureAtkUnitManager.Instance();
        if (unitManager == null)
        {
            return;
        }

        var targetLayer = targetAddon->DepthLayer;
        var targetOrder = targetAddon->DrawOrderIndex;
        var targetHost = targetAddon->HostId != 0 ? targetAddon->HostId : targetAddon->Id;

        var list = unitManager->AllLoadedUnitsList;
        var count = Math.Min((int)list.Count, list.Entries.Length);
        for (var i = 0; i < count; i++)
        {
            var other = list.Entries[i].Value;
            if (other == null
                || other == targetAddon
                || other->Id == targetHost
                || (other->HostId != 0 && other->HostId == targetHost))
            {
                continue;
            }

            if (other->DepthLayer < targetLayer
                || (other->DepthLayer == targetLayer && other->DrawOrderIndex <= targetOrder))
            {
                continue;
            }

            if (!TryGetAddonScreenRect(other, vpPos, out var min, out var max))
            {
                continue;
            }

            var w = max.X - min.X;
            var h = max.Y - min.Y;
            if (vpSize.X > 0f && vpSize.Y > 0f && w >= vpSize.X * 0.88f && h >= vpSize.Y * 0.88f)
            {
                continue;
            }

            if (other->WindowCollisionNode == null && other->DepthLayer <= targetLayer)
            {
                continue;
            }

            output.Add((min, max));
        }
    }
}
