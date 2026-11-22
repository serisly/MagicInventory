using System;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using MagicInventory.Core;
using MagicInventory.Services;
using MagicInventory.Services.MagicSort;

namespace MagicInventory.UI;

public sealed unsafe class MagicSortControllers : IDisposable
{
    private readonly NativeAddonButtonGroup<AtkUnitBase> retainerListGroup;
    private readonly NativeAddonButtonGroup<AtkUnitBase> retainerSellListGroup;
    private readonly NativeAddonButtonGroup<AddonShop> shopGroup;
    private readonly NativeAddonButtonGroup<AddonGrandCompanySupplyList> grandCompanyGroup;
    private readonly NativeAddonButtonGroup<AtkUnitBase> materializeGroup;
    private readonly NativeAddonButtonGroup<AtkUnitBase> cabinetGroup;
    private readonly NativeAddonButtonGroup<AtkUnitBase> glamourDresserGroup;
    private readonly NativeAddonButtonGroup<AtkUnitBase> inventoryExpansionGroup;
    private readonly NativeAddonButtonGroup<AtkUnitBase> inventoryLargeGroup;
    private readonly NativeAddonButtonGroup<AtkUnitBase> inventoryNormalGroup;

    public MagicSortControllers(
        MagicSortService magicSort,
        BellSweepService bellSweep,
        InventoryRouteOverlay routeOverlay)
    {
        retainerListGroup = new NativeAddonButtonGroup<AtkUnitBase>(
            "RetainerList",
            static a => a,
            new NativeAddonButtonGroup<AtkUnitBase>.ButtonSpec(
                "Magic Sort",
                bellSweep.GetBellSortButtonState,
                bellSweep.ToggleBellSort,
                static addon => addon->RootNode,
                static addon =>
                {
                    const float btnW = 128f;
                    const float btnH = 28f;
                    var rootW = addon != null && addon->RootNode != null ? Math.Max(320f, addon->RootNode->Width) : 560f;
                    var rootH = addon != null && addon->RootNode != null ? Math.Max(220f, addon->RootNode->Height) : 360f;
                    var x = Math.Max(16f, rootW - 18f - btnW - 8f - btnW);
                    var y = Math.Max(16f, rootH - 50f);
                    return (new Vector2(x, y), new Vector2(btnW, btnH));
                },
                hovered => routeOverlay.SetHoveredSellAddon("RetainerList:Sort", hovered)),
            new NativeAddonButtonGroup<AtkUnitBase>.ButtonSpec(
                "Magic Sell",
                bellSweep.GetBellSellButtonState,
                bellSweep.ToggleBellSell,
                static addon => addon->RootNode,
                static addon =>
                {
                    const float btnW = 128f;
                    const float btnH = 28f;
                    var rootW = addon != null && addon->RootNode != null ? Math.Max(320f, addon->RootNode->Width) : 560f;
                    var rootH = addon != null && addon->RootNode != null ? Math.Max(220f, addon->RootNode->Height) : 360f;
                    var x = Math.Max(16f, rootW - 18f - btnW);
                    var y = Math.Max(16f, rootH - 50f);
                    return (new Vector2(x, y), new Vector2(btnW, btnH));
                },
                hovered => routeOverlay.SetHoveredSellAddon("RetainerList:Sell", hovered)));

        retainerSellListGroup = new NativeAddonButtonGroup<AtkUnitBase>(
            "RetainerSellList",
            static a => a,
            new NativeAddonButtonGroup<AtkUnitBase>.ButtonSpec(
                "Magic Sell",
                magicSort.GetMarketSortButtonState,
                magicSort.ExecuteMarketSort,
                static addon => addon->RootNode,
                static addon => NativeUiHelpers.ResolveBottomMarginPosition(addon, 138f, 28f, preferRightSide: true),
                hovered => routeOverlay.SetHoveredSellAddon("RetainerSellList", hovered)));

        shopGroup = new NativeAddonButtonGroup<AddonShop>(
            "Shop",
            static a => &a->AtkUnitBase,
            new NativeAddonButtonGroup<AddonShop>.ButtonSpec(
                "Magic Sell",
                magicSort.GetVendorSortButtonState,
                magicSort.ExecuteVendorSort,
                static addon => addon->AtkUnitBase.RootNode,
                static addon => NativeUiHelpers.ResolveBottomMarginPosition(&addon->AtkUnitBase, 134f, 28f, preferRightSide: false),
                hovered => routeOverlay.SetHoveredSellAddon("Shop", hovered)));

        grandCompanyGroup = new NativeAddonButtonGroup<AddonGrandCompanySupplyList>(
            "GrandCompanySupplyList",
            static a => &a->AtkUnitBase,
            new NativeAddonButtonGroup<AddonGrandCompanySupplyList>.ButtonSpec(
                "Magic Sell",
                magicSort.GetGrandCompanySortButtonState,
                magicSort.ExecuteGrandCompanySort,
                static addon => addon->AtkUnitBase.RootNode,
                ResolveGrandCompanyPosition,
                hovered => routeOverlay.SetHoveredSellAddon("GrandCompanySupplyList", hovered)));

        materializeGroup = new NativeAddonButtonGroup<AtkUnitBase>(
            "Materialize",
            static a => a,
            new NativeAddonButtonGroup<AtkUnitBase>.ButtonSpec(
                "Extract All",
                magicSort.GetMateriaExtractButtonState,
                magicSort.ExecuteMateriaExtractSort,
                static addon => addon->RootNode,
                static addon => NativeUiHelpers.ResolveBottomMarginPosition(addon, 134f, 28f, preferRightSide: false)));

        cabinetGroup = new NativeAddonButtonGroup<AtkUnitBase>(
            "Cabinet",
            static a => a,
            new NativeAddonButtonGroup<AtkUnitBase>.ButtonSpec(
                "Magic Store",
                magicSort.GetArmoireStoreButtonState,
                magicSort.ExecuteArmoireStore,
                static addon => addon->RootNode,
                static addon => NativeUiHelpers.ResolveBottomMarginPosition(addon, 134f, 28f, preferRightSide: true),
                hovered => routeOverlay.SetHoveredSellAddon("Cabinet", hovered)));

        glamourDresserGroup = new NativeAddonButtonGroup<AtkUnitBase>(
            "MiragePrismPrismBoxCrystallize",
            static a => a,
            new NativeAddonButtonGroup<AtkUnitBase>.ButtonSpec(
                "Magic Store",
                magicSort.GetGlamourDresserButtonState,
                magicSort.ExecuteGlamourDresserStore,
                static addon => addon->RootNode,
                static addon => NativeUiHelpers.ResolveBottomMarginPosition(addon, 126f, 26f, preferRightSide: false),
                hovered =>
                {
                    routeOverlay.SetHoveredSellAddon("MiragePrismPrismBoxCrystallize", hovered);
                    routeOverlay.SetGlamourListHover(hovered);
                }));

        inventoryExpansionGroup = CreateInventoryGroup("InventoryExpansion", magicSort, routeOverlay);
        inventoryLargeGroup = CreateInventoryGroup("InventoryLarge", magicSort, routeOverlay);
        inventoryNormalGroup = CreateInventoryGroup("Inventory", magicSort, routeOverlay);
    }

    private static NativeAddonButtonGroup<AtkUnitBase> CreateInventoryGroup(
        string addonName,
        MagicSortService magicSort,
        InventoryRouteOverlay routeOverlay)
    {
        return new NativeAddonButtonGroup<AtkUnitBase>(
            addonName,
            static a => a,
            new NativeAddonButtonGroup<AtkUnitBase>.ButtonSpec(
                "Magic Sort",
                magicSort.GetInventorySortButtonState,
                magicSort.ExecuteInventorySort,
                static addon => addon->RootNode,
                ResolveInventorySortPosition,
                hovered => routeOverlay.SetHoveredInventorySortAddon(addonName, hovered)),
            new NativeAddonButtonGroup<AtkUnitBase>.ButtonSpec(
                "Use All",
                magicSort.GetUseAllButtonState,
                magicSort.ExecuteUseAll,
                static addon => addon->RootNode,
                ResolveInventoryUsePosition,
                hovered => routeOverlay.SetHoveredSellAddon(addonName, hovered)));
    }

    private static (Vector2 SortPos, Vector2 SortSize, Vector2 UsePos, Vector2 UseSize) ResolveCompactInventoryHeaderLayout(float rootW)
    {
        var rightLimit = Math.Max(140f, rootW - 62f);
        var avail = Math.Max(110f, rightLimit - 66f);
        const float gap = 3f;
        var sortW = Math.Clamp((avail - gap) * 0.54f, 64f, 94f);
        var useW = Math.Clamp(avail - gap - sortW, 56f, 82f);
        var useX = Math.Max(4f, rightLimit - useW);
        var sortX = Math.Max(4f, useX - gap - sortW);
        return (new Vector2(sortX, 8f), new Vector2(sortW, 22f), new Vector2(useX, 8f), new Vector2(useW, 22f));
    }

    private static (Vector2 Position, Vector2 Size) ResolveInventorySortPosition(AtkUnitBase* addon)
    {
        var rootW = addon != null && addon->RootNode != null ? addon->RootNode->Width : 560f;
        if (rootW < 420f)
        {
            var layout = ResolveCompactInventoryHeaderLayout(rootW);
            return (layout.SortPos, layout.SortSize);
        }

        var (basePos, _) = NativeUiHelpers.ResolveBottomMarginPosition(addon, 224f, 26f, preferRightSide: false);
        return (basePos, new Vector2(114f, 26f));
    }

    private static (Vector2 Position, Vector2 Size) ResolveInventoryUsePosition(AtkUnitBase* addon)
    {
        var rootW = addon != null && addon->RootNode != null ? addon->RootNode->Width : 560f;
        if (rootW < 420f)
        {
            var layout = ResolveCompactInventoryHeaderLayout(rootW);
            return (layout.UsePos, layout.UseSize);
        }

        var (basePos, _) = NativeUiHelpers.ResolveBottomMarginPosition(addon, 224f, 26f, preferRightSide: false);
        var useX = Math.Clamp(basePos.X + 120f, 12f, Math.Max(12f, rootW - 104f - 12f));
        return (new Vector2(useX, basePos.Y), new Vector2(104f, 26f));
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

    public void Enable()
    {
        retainerListGroup.Enable();
        retainerSellListGroup.Enable();
        shopGroup.Enable();
        grandCompanyGroup.Enable();
        materializeGroup.Enable();
        cabinetGroup.Enable();
        glamourDresserGroup.Enable();
        inventoryExpansionGroup.Enable();
        inventoryLargeGroup.Enable();
        inventoryNormalGroup.Enable();
    }

    public void Dispose()
    {
        retainerListGroup.Dispose();
        retainerSellListGroup.Dispose();
        shopGroup.Dispose();
        grandCompanyGroup.Dispose();
        materializeGroup.Dispose();
        cabinetGroup.Dispose();
        glamourDresserGroup.Dispose();
        inventoryExpansionGroup.Dispose();
        inventoryLargeGroup.Dispose();
        inventoryNormalGroup.Dispose();
    }
}
