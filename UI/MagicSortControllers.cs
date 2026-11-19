using System;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Controllers;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using MagicInventory.Core;
using MagicInventory.Services;
using MagicInventory.Services.MagicSort;

namespace MagicInventory.UI;

public sealed unsafe class MagicSortControllers : IDisposable
{
    private readonly MagicSortAddonBinding<AtkUnitBase> retainerListSortController;
    private readonly MagicSortAddonBinding<AtkUnitBase> retainerListSellController;
    private readonly MagicSortAddonBinding<AtkUnitBase> retainerSellListController;
    private readonly MagicSortAddonBinding<AddonShop> shopController;
    private readonly MagicSortAddonBinding<AddonGrandCompanySupplyList> grandCompanyController;
    private readonly MagicSortAddonBinding<AtkUnitBase> inventoryExpansionUseController;
    private readonly MagicSortAddonBinding<AtkUnitBase> inventoryLargeUseController;
    private readonly MagicSortAddonBinding<AtkUnitBase> inventoryNormalUseController;

    public MagicSortControllers(
        MagicSortService magicSort,
        BellSweepService bellSweep,
        InventoryHighlightController highlightController)
    {
        retainerListSortController = new MagicSortAddonBinding<AtkUnitBase>(
            "RetainerList",
            bellSweep.GetBellSortButtonState,
            bellSweep.ToggleBellSort,
            addon => addon->RootNode,
            addon =>
            {
                const float btnW = 128f;
                const float btnH = 28f;
                var rootW = addon != null && addon->RootNode != null ? Math.Max(320f, addon->RootNode->Width) : 560f;
                var rootH = addon != null && addon->RootNode != null ? Math.Max(220f, addon->RootNode->Height) : 360f;
                // Place side-by-side left of Magic Sell on bottom-right so native "Items for Sale / Venture / Comment" labels stay clear
                var x = Math.Max(16f, rootW - 18f - btnW - 8f - btnW);
                var y = Math.Max(16f, rootH - 50f);
                return (new Vector2(x, y), new Vector2(btnW, btnH));
            });

        retainerListSellController = new MagicSortAddonBinding<AtkUnitBase>(
            "RetainerList",
            bellSweep.GetBellSellButtonState,
            bellSweep.ToggleBellSell,
            addon => addon->RootNode,
            addon =>
            {
                const float btnW = 128f;
                const float btnH = 28f;
                var rootW = addon != null && addon->RootNode != null ? Math.Max(320f, addon->RootNode->Width) : 560f;
                var rootH = addon != null && addon->RootNode != null ? Math.Max(220f, addon->RootNode->Height) : 360f;
                var x = Math.Max(16f, rootW - 18f - btnW);
                var y = Math.Max(16f, rootH - 50f);
                return (new Vector2(x, y), new Vector2(btnW, btnH));
            });

        retainerSellListController = new MagicSortAddonBinding<AtkUnitBase>(
            "RetainerSellList",
            magicSort.GetMarketSortButtonState,
            magicSort.ExecuteMarketSort,
            addon => addon->RootNode,
            addon => NativeUiHelpers.ResolveBottomMarginPosition(addon, 134f, 28f, preferRightSide: true),
            hovered => highlightController.SetHoveredSellAddon("RetainerSellList", hovered));

        shopController = new MagicSortAddonBinding<AddonShop>(
            "Shop",
            magicSort.GetVendorSortButtonState,
            magicSort.ExecuteVendorSort,
            addon => addon->AtkUnitBase.RootNode,
            addon => NativeUiHelpers.ResolveBottomMarginPosition(&addon->AtkUnitBase, 134f, 28f, preferRightSide: false),
            hovered => highlightController.SetHoveredSellAddon("Shop", hovered));

        grandCompanyController = new MagicSortAddonBinding<AddonGrandCompanySupplyList>(
            "GrandCompanySupplyList",
            magicSort.GetGrandCompanySortButtonState,
            magicSort.ExecuteGrandCompanySort,
            addon => addon->AtkUnitBase.RootNode,
            ResolveGrandCompanyPosition,
            hovered => highlightController.SetHoveredSellAddon("GrandCompanySupplyList", hovered));

        inventoryExpansionUseController = new MagicSortAddonBinding<AtkUnitBase>(
            "InventoryExpansion",
            magicSort.GetUseAllButtonState,
            magicSort.ExecuteUseAll,
            addon => addon->RootNode,
            addon => NativeUiHelpers.ResolveBottomMarginPosition(addon, 110f, 26f, preferRightSide: false),
            hovered => highlightController.SetHoveredSellAddon("InventoryExpansion", hovered));

        inventoryLargeUseController = new MagicSortAddonBinding<AtkUnitBase>(
            "InventoryLarge",
            magicSort.GetUseAllButtonState,
            magicSort.ExecuteUseAll,
            addon => addon->RootNode,
            addon => NativeUiHelpers.ResolveBottomMarginPosition(addon, 110f, 26f, preferRightSide: false),
            hovered => highlightController.SetHoveredSellAddon("InventoryLarge", hovered));

        inventoryNormalUseController = new MagicSortAddonBinding<AtkUnitBase>(
            "Inventory",
            magicSort.GetUseAllButtonState,
            magicSort.ExecuteUseAll,
            addon => addon->RootNode,
            addon => NativeUiHelpers.ResolveBottomMarginPosition(addon, 110f, 26f, preferRightSide: false),
            hovered => highlightController.SetHoveredSellAddon("Inventory", hovered));
    }

    public void Enable()
    {
        retainerListSortController.Enable();
        retainerListSellController.Enable();
        retainerSellListController.Enable();
        shopController.Enable();
        grandCompanyController.Enable();
        inventoryExpansionUseController.Enable();
        inventoryLargeUseController.Enable();
        inventoryNormalUseController.Enable();
    }

    public void Dispose()
    {
        retainerListSortController.Dispose();
        retainerListSellController.Dispose();
        retainerSellListController.Dispose();
        shopController.Dispose();
        grandCompanyController.Dispose();
        inventoryExpansionUseController.Dispose();
        inventoryLargeUseController.Dispose();
        inventoryNormalUseController.Dispose();
    }

    private static (Vector2 Position, Vector2 Size) ResolveGrandCompanyPosition(AddonGrandCompanySupplyList* addon)
    {
        const float width = 136f;
        const float height = 28f;
        var unit = &addon->AtkUnitBase;
        if (unit->RootNode == null)
        {
            return (new Vector2(18f, 380f), new Vector2(width, height));
        }

        var rootW = unit->RootNode->Width;
        var sealX = float.MaxValue;
        if (addon->SealCountTextNode != null)
        {
            sealX = Math.Min(sealX, addon->SealCountTextNode->AtkResNode.X);
        }

        if (addon->SealIconNode != null)
        {
            sealX = Math.Min(sealX, addon->SealIconNode->AtkResNode.X);
        }

        var preferRightSide = sealX < rootW * 0.5f;
        return NativeUiHelpers.ResolveBottomMarginPosition(unit, width, height, preferRightSide);
    }

    private sealed class MagicSortAddonBinding<TAddon> : IDisposable
        where TAddon : unmanaged
    {
        public delegate AtkResNode* RootNodeResolver(TAddon* addon);
        public delegate (Vector2 Position, Vector2 Size) PlacementResolver(TAddon* addon);

        private readonly Func<MagicSortButtonState> getState;
        private readonly Action onClick;
        private readonly RootNodeResolver getRootNode;
        private readonly PlacementResolver resolvePlacement;
        private readonly Action<bool>? onHoverChanged;
        private readonly AddonController<TAddon> controller;

        private TextButtonNode? button;
        private string lastLabel = string.Empty;
        private string lastTooltip = string.Empty;
        private bool? lastEnabled;
        private bool? lastVisible;

        public MagicSortAddonBinding(
            string addonName,
            Func<MagicSortButtonState> getState,
            Action onClick,
            RootNodeResolver getRootNode,
            PlacementResolver resolvePlacement,
            Action<bool>? onHoverChanged = null)
        {
            this.getState = getState;
            this.onClick = onClick;
            this.getRootNode = getRootNode;
            this.resolvePlacement = resolvePlacement;
            this.onHoverChanged = onHoverChanged;

            controller = new AddonController<TAddon>
            {
                AddonName = addonName,
                OnSetup = OnSetup,
                OnRefresh = SyncState,
                OnUpdate = SyncState,
                OnFinalize = OnFinalize,
            };
        }

        public void Enable() => controller.Enable();

        private void OnSetup(TAddon* addon)
        {
            DetachButton();

            if (addon == null)
            {
                return;
            }

            var rootNode = getRootNode(addon);
            if (rootNode == null)
            {
                return;
            }

            var (pos, size) = resolvePlacement(addon);
            button = new TextButtonNode
            {
                Position = pos,
                Size = size,
                String = "Magic Sort",
                IsVisible = false,
                OnClick = onClick,
            };
            if (onHoverChanged != null)
            {
                button.AddEvent(AtkEventType.MouseOver, () => onHoverChanged(true));
                button.AddEvent(AtkEventType.MouseOut, () => onHoverChanged(false));
            }

            button.AttachNode(rootNode, NodePosition.AsLastChild);

            ResetCache();
            SyncState(addon);
        }

        private void SyncState(TAddon* addon)
        {
            if (addon == null)
            {
                return;
            }

            // Persistent addons like InventoryExpansion stay allocated while hidden; attach lazily on first visible update
            if (button == null)
            {
                OnSetup(addon);
                return;
            }

            var state = getState();
            if (lastVisible != state.IsVisible)
            {
                lastVisible = state.IsVisible;
                button.IsVisible = state.IsVisible;
                if (!state.IsVisible)
                {
                    onHoverChanged?.Invoke(false);
                }
            }

            if (!state.IsVisible)
            {
                return;
            }

            if (lastEnabled != state.IsEnabled)
            {
                lastEnabled = state.IsEnabled;
                button.IsEnabled = state.IsEnabled;
            }

            if (lastLabel != state.Label)
            {
                lastLabel = state.Label;
                button.String = state.Label;
            }

            var tip = state.Tooltip ?? string.Empty;
            if (lastTooltip != tip)
            {
                lastTooltip = tip;
                button.TextTooltip = tip;
            }
        }

        private void OnFinalize(TAddon* _)
        {
            DetachButton();
        }

        private void DetachButton()
        {
            onHoverChanged?.Invoke(false);
            if (button != null)
            {
                button.DetachNode();
                button.Dispose();
                button = null;
            }

            ResetCache();
        }

        private void ResetCache()
        {
            lastLabel = string.Empty;
            lastTooltip = string.Empty;
            lastEnabled = null;
            lastVisible = null;
        }

        public void Dispose()
        {
            controller.Disable();
            DetachButton();
            controller.Dispose();
        }
    }
}
