using System;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.UI;
using MagicInventory.Core;
using MagicInventory.Services;

namespace MagicInventory.UI;

public sealed unsafe class MarketBoardController : IDisposable
{
    private const uint FooterContainerNodeId = 142;

    private readonly NativeAddonButtonGroup<AddonItemSearch> searchGroup;
    private readonly NativeAddonButtonGroup<AddonItemSearchResult> resultGroup;

    public MarketBoardController(MarketBoardBuyPlanner buyPlanner)
    {
        searchGroup = new NativeAddonButtonGroup<AddonItemSearch>(
            "ItemSearch",
            static a => &a->AtkUnitBase,
            new NativeAddonButtonGroup<AddonItemSearch>.ButtonSpec(
                "Magic Buy",
                buyPlanner.GetSearchWindowBuyButtonState,
                buyPlanner.ToggleAutoBuy,
                static addon => addon->GetNodeById(FooterContainerNodeId),
                static _ => (new Vector2(311f, 0f), new Vector2(168f, 28f))));

        resultGroup = new NativeAddonButtonGroup<AddonItemSearchResult>(
            "ItemSearchResult",
            static a => &a->AtkUnitBase,
            new NativeAddonButtonGroup<AddonItemSearchResult>.ButtonSpec(
                "Magic Buy",
                buyPlanner.GetBuyButtonState,
                buyPlanner.ExecuteBuyBestListing,
                static addon => addon->AtkUnitBase.RootNode,
                static addon => NativeUiHelpers.ResolveBottomMarginPosition(&addon->AtkUnitBase, 196f, 28f, preferRightSide: false)));
    }

    public void Enable()
    {
        searchGroup.Enable();
        resultGroup.Enable();
    }

    public void Dispose()
    {
        searchGroup.Dispose();
        resultGroup.Dispose();
    }
}
