using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.UI;
using KamiToolKit.Controllers;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using MagicInventory.Core;
using MagicInventory.Services;

namespace MagicInventory.UI;

public sealed unsafe class MarketBoardController : IDisposable
{
    private const uint FooterContainerNodeId = 142;
    private const float ButtonX = 311f;
    private const float ButtonY = 0f;
    private const float ButtonWidth = 168f;
    private const float ButtonHeight = 28f;

    private readonly MarketBoardBuyPlanner buyPlanner;
    private readonly AddonController<AddonItemSearch> controller;
    private readonly AddonController<AddonItemSearchResult> resultController;

    private TextButtonNode? searchMissingButton;
    private string lastLabel = string.Empty;
    private string lastTooltip = string.Empty;
    private bool? lastEnabled;
    private bool? lastVisible;

    private TextButtonNode? buyBestButton;
    private string lastBuyLabel = string.Empty;
    private string lastBuyTooltip = string.Empty;
    private bool? lastBuyEnabled;
    private bool? lastBuyVisible;

    public MarketBoardController(MarketBoardBuyPlanner buyPlanner)
    {
        this.buyPlanner = buyPlanner;

        controller = new AddonController<AddonItemSearch>
        {
            AddonName = "ItemSearch",
            OnSetup = OnSetup,
            OnRefresh = SyncState,
            OnUpdate = SyncState,
            OnFinalize = OnFinalize,
        };

        resultController = new AddonController<AddonItemSearchResult>
        {
            AddonName = "ItemSearchResult",
            OnSetup = OnResultSetup,
            OnRefresh = SyncResultState,
            OnUpdate = SyncResultState,
            OnFinalize = OnResultFinalize,
        };
    }

    public void Enable()
    {
        controller.Enable();
        resultController.Enable();
    }

    private void OnSetup(AddonItemSearch* addon)
    {
        DetachButton();

        if (addon == null)
        {
            return;
        }

        var footerContainer = addon->GetNodeById(FooterContainerNodeId);
        if (footerContainer == null)
        {
            return;
        }

        searchMissingButton = new TextButtonNode
        {
            Position = new Vector2(ButtonX, ButtonY),
            Size = new Vector2(ButtonWidth, ButtonHeight),
            String = "Magic Buy",
            IsVisible = false,
            OnClick = buyPlanner.ToggleAutoBuy,
        };

        searchMissingButton.AttachNode(footerContainer, NodePosition.AsLastChild);
        ResetCache();
        SyncState(addon);
    }

    private void SyncState(AddonItemSearch* addon)
    {
        if (addon == null)
        {
            return;
        }

        if (searchMissingButton == null)
        {
            OnSetup(addon);
            return;
        }

        var btnState = buyPlanner.GetSearchWindowBuyButtonState();
        if (lastVisible != btnState.IsVisible)
        {
            lastVisible = btnState.IsVisible;
            searchMissingButton.IsVisible = btnState.IsVisible;
        }

        if (!btnState.IsVisible)
        {
            return;
        }

        if (lastEnabled != btnState.IsEnabled)
        {
            lastEnabled = btnState.IsEnabled;
            searchMissingButton.IsEnabled = btnState.IsEnabled;
        }

        if (lastLabel != btnState.Label)
        {
            lastLabel = btnState.Label;
            searchMissingButton.String = btnState.Label;
        }

        if (lastTooltip != btnState.Tooltip)
        {
            lastTooltip = btnState.Tooltip;
            searchMissingButton.TextTooltip = btnState.Tooltip;
        }
    }

    private void OnFinalize(AddonItemSearch* _)
    {
        DetachButton();
    }

    private void DetachButton()
    {
        if (searchMissingButton != null)
        {
            searchMissingButton.DetachNode();
            searchMissingButton.Dispose();
            searchMissingButton = null;
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

    private void OnResultSetup(AddonItemSearchResult* addon)
    {
        DetachBuyButton();

        if (addon == null || addon->AtkUnitBase.RootNode == null)
        {
            return;
        }

        var (pos, size) = NativeUiHelpers.ResolveBottomMarginPosition(&addon->AtkUnitBase, 196f, 28f, preferRightSide: false);
        buyBestButton = new TextButtonNode
        {
            Position = pos,
            Size = size,
            String = "Magic Buy",
            IsVisible = false,
            OnClick = buyPlanner.ExecuteBuyBestListing,
        };
        buyBestButton.AttachNode(addon->AtkUnitBase.RootNode, NodePosition.AsLastChild);
        ResetBuyCache();
        SyncResultState(addon);
    }

    private void SyncResultState(AddonItemSearchResult* addon)
    {
        if (addon == null)
        {
            return;
        }

        if (buyBestButton == null)
        {
            OnResultSetup(addon);
            return;
        }

        var btnState = buyPlanner.GetBuyButtonState();
        if (lastBuyVisible != btnState.IsVisible)
        {
            lastBuyVisible = btnState.IsVisible;
            buyBestButton.IsVisible = btnState.IsVisible;
        }

        if (!btnState.IsVisible)
        {
            return;
        }

        if (lastBuyEnabled != btnState.IsEnabled)
        {
            lastBuyEnabled = btnState.IsEnabled;
            buyBestButton.IsEnabled = btnState.IsEnabled;
        }

        if (lastBuyLabel != btnState.Label)
        {
            lastBuyLabel = btnState.Label;
            buyBestButton.String = btnState.Label;
        }

        if (lastBuyTooltip != btnState.Tooltip)
        {
            lastBuyTooltip = btnState.Tooltip;
            buyBestButton.TextTooltip = btnState.Tooltip;
        }
    }

    private void OnResultFinalize(AddonItemSearchResult* _)
    {
        DetachBuyButton();
    }

    private void DetachBuyButton()
    {
        if (buyBestButton != null)
        {
            buyBestButton.DetachNode();
            buyBestButton.Dispose();
            buyBestButton = null;
        }

        ResetBuyCache();
    }

    private void ResetBuyCache()
    {
        lastBuyLabel = string.Empty;
        lastBuyTooltip = string.Empty;
        lastBuyEnabled = null;
        lastBuyVisible = null;
    }

    public void Dispose()
    {
        controller.Disable();
        resultController.Disable();
        DetachButton();
        DetachBuyButton();
        controller.Dispose();
        resultController.Dispose();
    }
}
