using System;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using MagicInventory.Core;
using MagicInventory.Services;
using MagicInventory.Services.MagicSort;

namespace MagicInventory.UI;

public sealed unsafe class RetainerController : IDisposable
{
    private readonly NativeAddonButtonGroup<AtkUnitBase> largeGroup;
    private readonly NativeAddonButtonGroup<AtkUnitBase> standardGroup;

    public RetainerController(
        MagicSortService magicSort,
        InventoryRouteOverlay routeOverlay)
    {
        largeGroup = CreateBinding("InventoryRetainerLarge", magicSort, routeOverlay);
        standardGroup = CreateBinding("InventoryRetainer", magicSort, routeOverlay);
    }

    private static NativeAddonButtonGroup<AtkUnitBase> CreateBinding(
        string addonName,
        MagicSortService magicSort,
        InventoryRouteOverlay routeOverlay)
    {
        return new NativeAddonButtonGroup<AtkUnitBase>(
            addonName,
            static a => a,
            new NativeAddonButtonGroup<AtkUnitBase>.ButtonSpec(
                "Magic Sort",
                () => magicSort.GetButtonState(StationActionId.RetainerSort),
                () => magicSort.ExecuteAction(StationActionId.RetainerSort),
                static addon => addon->RootNode,
                ResolveButtonPlacement,
                hovered => routeOverlay.SetHoveredRetainerSortAddon(addonName, hovered)));
    }

    private static (Vector2 Position, Vector2 Size) ResolveButtonPlacement(AtkUnitBase* addon)
    {
        if (addon == null || addon->RootNode == null)
        {
            return (new Vector2(164f, 340f), new Vector2(120f, 26f));
        }

        var rootW = Math.Max(260f, addon->RootNode->Width);
        var rootH = Math.Max(240f, addon->RootNode->Height);
        var leftX = 164f;
        var footerY = Math.Max(200f, rootH - 40f);
        var buttonH = 28f;
        AtkResNode* entrustRes = null;

        var entrustBtn = addon->GetComponentButtonById(61);
        if (entrustBtn != null && entrustBtn->AtkComponentBase.OwnerNode != null)
        {
            var res = &entrustBtn->AtkComponentBase.OwnerNode->AtkResNode;
            if (res->ParentNode == addon->RootNode && res->Y > 150f && res->Width >= 80)
            {
                entrustRes = res;
                leftX = res->X + res->Width + 6f;
                footerY = res->Y;
                buttonH = res->Height;
            }
        }

        var uld = addon->UldManager;
        if (entrustRes == null && uld.NodeList != null)
        {
            for (var i = 0; i < uld.NodeListCount; i++)
            {
                var node = uld.NodeList[i];
                if (node == null || node->ParentNode != addon->RootNode)
                {
                    continue;
                }

                if ((int)node->Type >= 1000 && node->X <= 32f && node->Y > rootH - 80f && node->Width >= 80)
                {
                    entrustRes = node;
                    leftX = node->X + node->Width + 6f;
                    footerY = node->Y;
                    buttonH = node->Height;
                    break;
                }
            }
        }

        var rightLimit = rootW - 14f;
        var gilNode = addon->GetNodeById(64);
        if (gilNode != null && gilNode->ParentNode == addon->RootNode && gilNode->X > leftX)
        {
            rightLimit = Math.Min(rightLimit, gilNode->X - 6f);
        }

        if (uld.NodeList != null)
        {
            for (var i = 0; i < uld.NodeListCount; i++)
            {
                var node = uld.NodeList[i];
                if (node == null || node == entrustRes || node->ParentNode != addon->RootNode || !node->IsVisible())
                {
                    continue;
                }

                if (node->NodeId >= 100_000 || node->Width <= 8f || node->Width > rootW * 0.7f || node->Height > 64f)
                {
                    continue;
                }

                var verticalOverlap = node->Y < footerY + buttonH && (node->Y + node->Height) > footerY;
                if (verticalOverlap && node->X > leftX + 20f)
                {
                    rightLimit = Math.Min(rightLimit, node->X - 6f);
                }
            }
        }

        var available = rightLimit - leftX;
        if (available >= 96f)
        {
            return (
                new Vector2(leftX, footerY),
                new Vector2(MathF.Floor(Math.Clamp(available, 96f, 138f)), buttonH));
        }

        const float compactW = 108f;
        const float compactH = 22f;
        var compactX = Math.Max(92f, rootW - 60f - compactW);
        return (new Vector2(compactX, 8f), new Vector2(compactW, compactH));
    }

    public void Enable()
    {
        largeGroup.Enable();
        standardGroup.Enable();
    }

    public void Dispose()
    {
        largeGroup.Dispose();
        standardGroup.Dispose();
    }
}
