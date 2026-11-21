using System;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Controllers;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using MagicInventory.Services;

namespace MagicInventory.UI;

/// <summary>
/// Embeds a single unified Magic Sort button into InventoryRetainerLarge / InventoryRetainer.
/// Handles both tracked material withdrawals (exact-quantity split or All) and retainer sorting
/// in one loop next to native Entrust Duplicates.
/// </summary>
public sealed unsafe class RetainerController : IDisposable
{
    private readonly RetainerAddonBinding largeBinding;
    private readonly RetainerAddonBinding standardBinding;

    public RetainerController(
        MagicSortService magicSort,
        InventoryHighlightController highlightController,
        InventoryRouteOverlay routeOverlay)
    {
        largeBinding = new RetainerAddonBinding("InventoryRetainerLarge", magicSort, highlightController, routeOverlay);
        standardBinding = new RetainerAddonBinding("InventoryRetainer", magicSort, highlightController, routeOverlay);
    }

    public void Enable()
    {
        largeBinding.Enable();
        standardBinding.Enable();
    }

    public void Dispose()
    {
        largeBinding.Dispose();
        standardBinding.Dispose();
    }

    private sealed class RetainerAddonBinding : IDisposable
    {
        private readonly record struct FooterBounds(float LeftX, float RightLimit, float Y, float Height);

        private readonly string addonName;
        private readonly MagicSortService magicSort;
        private readonly InventoryHighlightController highlightController;
        private readonly InventoryRouteOverlay routeOverlay;
        private readonly AddonController<AtkUnitBase> controller;

        private TextButtonNode? magicSortButton;
        private FooterBounds footerBounds;

        private string lastSortLabel = string.Empty;
        private string lastSortTooltip = string.Empty;
        private bool? lastSortEnabled;
        private bool? lastSortVisible;

        public RetainerAddonBinding(
            string addonName,
            MagicSortService magicSort,
            InventoryHighlightController highlightController,
            InventoryRouteOverlay routeOverlay)
        {
            this.addonName = addonName;
            this.magicSort = magicSort;
            this.highlightController = highlightController;
            this.routeOverlay = routeOverlay;
            controller = new AddonController<AtkUnitBase>
            {
                AddonName = addonName,
                OnSetup = OnSetup,
                OnRefresh = SyncState,
                OnUpdate = SyncState,
                OnFinalize = OnFinalize,
            };
        }

        public void Enable() => controller.Enable();

        private void OnSetup(AtkUnitBase* addon)
        {
            DetachButton();

            if (addon == null || addon->RootNode == null)
            {
                return;
            }

            footerBounds = ResolveFooterBounds(addon);
            var available = footerBounds.RightLimit - footerBounds.LeftX;
            var pos = available >= 96f
                ? new Vector2(footerBounds.LeftX, footerBounds.Y)
                : new Vector2(12f, 30f);
            var size = available >= 96f
                ? new Vector2(MathF.Floor(Math.Clamp(available, 96f, 138f)), footerBounds.Height)
                : new Vector2(120f, 24f);

            magicSortButton = new TextButtonNode
            {
                Position = pos,
                Size = size,
                String = "Magic Sort",
                IsVisible = false,
                OnClick = () =>
                {
                    highlightController.SetHoveredRetainerSortAddon(addonName, false);
                    routeOverlay.SetHoveredInventorySortAddon(addonName, false);
                    magicSort.ExecuteRetainerSort();
                },
            };
            magicSortButton.AddEvent(AtkEventType.MouseOver, () =>
            {
                highlightController.SetHoveredRetainerSortAddon(addonName, true);
                routeOverlay.SetHoveredInventorySortAddon(addonName, true);
            });
            magicSortButton.AddEvent(AtkEventType.MouseOut, () =>
            {
                highlightController.SetHoveredRetainerSortAddon(addonName, false);
                routeOverlay.SetHoveredInventorySortAddon(addonName, false);
            });
            magicSortButton.AttachNode(addon->RootNode, NodePosition.AsLastChild);

            ResetCache();
            SyncState(addon);
        }

        private static FooterBounds ResolveFooterBounds(AtkUnitBase* addon)
        {
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

            return new FooterBounds(leftX, rightLimit, footerY, buttonH);
        }

        private void SyncState(AtkUnitBase* addon)
        {
            if (addon == null)
            {
                return;
            }

            if (magicSortButton == null)
            {
                OnSetup(addon);
                return;
            }

            var sortState = magicSort.GetRetainerSortButtonState();

            if (lastSortVisible != sortState.IsVisible)
            {
                lastSortVisible = sortState.IsVisible;
                magicSortButton.IsVisible = sortState.IsVisible;
                if (!sortState.IsVisible)
                {
                    highlightController.SetHoveredRetainerSortAddon(addonName, false);
                    routeOverlay.SetHoveredInventorySortAddon(addonName, false);
                }
            }

            if (!sortState.IsVisible)
            {
                return;
            }

            if (lastSortEnabled != sortState.IsEnabled)
            {
                lastSortEnabled = sortState.IsEnabled;
                magicSortButton.IsEnabled = sortState.IsEnabled;
                if (!sortState.IsEnabled)
                {
                    highlightController.SetHoveredRetainerSortAddon(addonName, false);
                    routeOverlay.SetHoveredInventorySortAddon(addonName, false);
                }
            }

            if (lastSortLabel != sortState.Label)
            {
                lastSortLabel = sortState.Label;
                magicSortButton.String = sortState.Label;
            }

            var sortTip = sortState.Tooltip ?? string.Empty;
            if (lastSortTooltip != sortTip)
            {
                lastSortTooltip = sortTip;
                magicSortButton.TextTooltip = sortTip;
            }
        }

        private void OnFinalize(AtkUnitBase* _)
        {
            DetachButton();
        }

        private void DetachButton()
        {
            highlightController.SetHoveredRetainerSortAddon(addonName, false);
            routeOverlay.SetHoveredInventorySortAddon(addonName, false);
            if (magicSortButton != null)
            {
                magicSortButton.DetachNode();
                magicSortButton.Dispose();
                magicSortButton = null;
            }

            ResetCache();
        }

        private void ResetCache()
        {
            lastSortLabel = string.Empty;
            lastSortTooltip = string.Empty;
            lastSortEnabled = null;
            lastSortVisible = null;
        }

        public void Dispose()
        {
            controller.Disable();
            DetachButton();
            controller.Dispose();
        }
    }
}
