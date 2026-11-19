using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using MagicInventory.Core;
using MagicInventory.Services;
using MagicInventory.Services.Triage;

namespace MagicInventory.UI;

/// <summary>
/// Hover-cascading FFXIV-styled flyout menu anchored beside the "Magic Inventory" entry in
/// FFXIV's native ContextMenu. Opens automatically on hover (zero clicks required to cascade
/// into Store / Sell / Track) and drives live sibling slot highlighting on hover.
/// </summary>
public sealed unsafe class ContextFlyoutWindow : IDisposable
{
    private enum SubmenuKind
    {
        None,
        Store,
        Sell,
        Track,
    }

    private const float RowMinWidth = 178f;
    private const float SubmenuMinWidth = 170f;
    private const float RowHeight = 24f;
    private const float LeftGutterWidth = 18f;
    private const float RightArrowGutterWidth = 16f;
    private const double HoverCloseGraceMs = 280.0;

    private static readonly Vector4 ColorWindowBg = new(0.13f, 0.13f, 0.14f, 0.96f);
    private static readonly Vector4 ColorBorder = new(0.50f, 0.45f, 0.34f, 0.92f);
    private static readonly Vector4 ColorHoverBg = new(0.38f, 0.32f, 0.19f, 0.92f);
    private static readonly Vector4 ColorActiveBg = new(0.47f, 0.40f, 0.23f, 0.96f);
    private static readonly Vector4 ColorText = new(0.94f, 0.92f, 0.86f, 1.0f);
    private static readonly Vector4 ColorGoldAccent = new(0.96f, 0.80f, 0.36f, 1.0f);
    private static readonly Vector4 ColorArrow = new(0.84f, 0.78f, 0.62f, 0.95f);
    private static readonly Vector4 ColorSeparator = new(0.45f, 0.41f, 0.32f, 0.55f);

    private readonly IGameGui gameGui;
    private readonly IDataManager dataManager;
    private readonly IChatGui chatGui;
    private readonly HoldingsService holdings;
    private readonly InventorySafetyEvaluator safety;
    private readonly ItemTriageEngine triageEngine;
    private readonly MagicSortService magicSort;
    private readonly MaterialTrackerService materialTracker;
    private readonly InventoryHighlightController highlightController;

    private uint armedBaseItemId;
    private string armedItemName = string.Empty;
    private bool armedFromArmoury;

    private bool isFlyoutOpen;
    private bool isPinnedFromClick;
    private SubmenuKind activeSubmenu = SubmenuKind.None;
    private float submenuAnchorY;

    private Vector2 anchorPos;
    private float anchorMenuLeft;
    private float anchorMenuRight;
    private DateTime lastInteractionUtc = DateTime.MinValue;

    private Vector2 panel1Pos;
    private Vector2 panel1Size;
    private Vector2 panel2Pos;
    private Vector2 panel2Size;
    private bool panel2DrawnLastFrame;

    // Short-lived cached item metadata so hovering at 120 FPS never recomputes triage per frame
    private uint cachedMetaItemId;
    private DateTime cachedMetaUtc = DateTime.MinValue;
    private Item? cachedItemRow;
    private bool cachedCanTag;
    private bool cachedIsManual;
    private MagicSortEntry? cachedCurrentEntry;
    private string cachedAutoLabel = "Bag";
    private string cachedAutoRouteFilter = "Keep";
    private bool cachedIsLocked;
    private bool cachedIsItemTracked;
    private bool cachedHasRecipe;
    private bool cachedIsCabinet;
    private int cachedMarketPriority;
    private IReadOnlyList<KnownRetainerInfo> cachedRetainers = Array.Empty<KnownRetainerInfo>();

    public ContextFlyoutWindow(
        IGameGui gameGui,
        IDataManager dataManager,
        IChatGui chatGui,
        HoldingsService holdings,
        InventorySafetyEvaluator safety,
        ItemTriageEngine triageEngine,
        MagicSortService magicSort,
        MaterialTrackerService materialTracker,
        InventoryHighlightController highlightController)
    {
        this.gameGui = gameGui;
        this.dataManager = dataManager;
        this.chatGui = chatGui;
        this.holdings = holdings;
        this.safety = safety;
        this.triageEngine = triageEngine;
        this.magicSort = magicSort;
        this.materialTracker = materialTracker;
        this.highlightController = highlightController;
    }

    public void ArmForItem(uint baseItemId, string itemName, bool fromArmoury)
    {
        armedBaseItemId = HoldingsService.NormalizeBaseItemId(baseItemId);
        armedItemName = itemName;
        armedFromArmoury = fromArmoury;
        isFlyoutOpen = false;
        isPinnedFromClick = false;
        activeSubmenu = SubmenuKind.None;
        panel2DrawnLastFrame = false;
        cachedMetaItemId = 0;
        highlightController.SetHoveredRouteFilter(null);
    }

    public void PinOpenFromClick()
    {
        if (armedBaseItemId == 0)
        {
            return;
        }

        if (anchorPos == Vector2.Zero)
        {
            var mouse = ImGui.GetMousePos();
            anchorPos = new Vector2(mouse.X + 8f, mouse.Y - 12f);
            anchorMenuLeft = mouse.X - 140f;
            anchorMenuRight = mouse.X + 8f;
        }

        isFlyoutOpen = true;
        isPinnedFromClick = true;
        lastInteractionUtc = DateTime.UtcNow;
    }

    public void Draw()
    {
        if (armedBaseItemId == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var mouse = ImGui.GetMousePos();
        var menuFound = TryLocateMagicContextRow(
            out var menuLeft,
            out var menuRight,
            out var rowTop,
            out var rowHeight,
            out var isNativeRowHovered,
            out var isOtherNativeRowHovered);

        if (menuFound)
        {
            anchorMenuLeft = menuLeft;
            anchorMenuRight = menuRight;
            anchorPos = new Vector2(menuRight - 2f, rowTop - 6f);

            var mouseOverPanels = isFlyoutOpen && IsMouseInFlyoutOrBridge(mouse, menuLeft, menuRight, rowTop, rowHeight);

            if (isNativeRowHovered)
            {
                isFlyoutOpen = true;
                isPinnedFromClick = false;
                lastInteractionUtc = now;
            }
            else if (isFlyoutOpen)
            {
                if (mouseOverPanels)
                {
                    lastInteractionUtc = now;
                }
                else if (isOtherNativeRowHovered)
                {
                    CloseFlyoutOnly();
                }
                else if ((now - lastInteractionUtc).TotalMilliseconds > HoverCloseGraceMs)
                {
                    CloseFlyoutOnly();
                }
            }
        }
        else if (isPinnedFromClick && isFlyoutOpen)
        {
            var mouseOverPanels = IsMouseInDrawnPanels(mouse, padding: 6f);
            if (ImGui.IsKeyPressed(ImGuiKey.Escape))
            {
                ResetAll();
                return;
            }

            if (!mouseOverPanels
                && (ImGui.IsMouseClicked(ImGuiMouseButton.Left) || ImGui.IsMouseClicked(ImGuiMouseButton.Right)))
            {
                ResetAll();
                return;
            }
        }
        else
        {
            ResetAll();
            return;
        }

        if (!isFlyoutOpen)
        {
            highlightController.SetHoveredRouteFilter(null);
            return;
        }

        EnsureMetadataFresh(now);

        string? hoveredRouteFilter = null;
        var closeAllAfterAction = false;

        PushFlyoutStyle();
        try
        {
            DrawPrimaryPanel(ref hoveredRouteFilter, ref closeAllAfterAction);

            if (!closeAllAfterAction && activeSubmenu != SubmenuKind.None)
            {
                DrawSubmenuPanel(ref hoveredRouteFilter, ref closeAllAfterAction);
            }
            else
            {
                panel2DrawnLastFrame = false;
                panel2Size = Vector2.Zero;
            }
        }
        finally
        {
            PopFlyoutStyle();
        }

        if (closeAllAfterAction)
        {
            CloseNativeContextMenu();
            ResetAll();
            return;
        }

        highlightController.SetHoveredRouteFilter(hoveredRouteFilter);
    }

    private void DrawPrimaryPanel(ref string? hoveredRouteFilter, ref bool closeAllAfterAction)
    {
        var vp = ImGui.GetMainViewport();
        var vpPos = vp.Pos;
        var vpSize = vp.Size;

        var estWidth = panel1Size.X > 40f ? panel1Size.X : RowMinWidth + 16f;
        var estHeight = panel1Size.Y > 40f ? panel1Size.Y : 220f;

        var drawX = anchorPos.X;
        if (drawX + estWidth > vpPos.X + vpSize.X - 6f)
        {
            drawX = MathF.Max(vpPos.X + 6f, anchorMenuLeft - estWidth + 2f);
        }

        var drawY = Math.Clamp(anchorPos.Y, vpPos.Y + 6f, MathF.Max(vpPos.Y + 6f, vpPos.Y + vpSize.Y - estHeight - 6f));

        ImGui.SetNextWindowPos(new Vector2(drawX, drawY), ImGuiCond.Always);
        var flags = ImGuiWindowFlags.NoTitleBar
            | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.NoScrollWithMouse
            | ImGuiWindowFlags.AlwaysAutoResize
            | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoFocusOnAppearing;

        if (!ImGui.Begin("##MagicInventoryContextFlyout", flags))
        {
            ImGui.End();
            return;
        }

        panel1Pos = ImGui.GetWindowPos();
        panel1Size = ImGui.GetWindowSize();

        var currentRoute = cachedCurrentEntry?.Route ?? "Keep";
        var itemRow = cachedItemRow;
        var canMarket = itemRow is null || !itemRow.Value.IsUntradable;
        var canVendor = itemRow is null || (itemRow.Value.PriceLow > 0 && !itemRow.Value.IsUnique);
        var canGc = itemRow is { EquipSlotCategory.RowId: > 0, Rarity: 2 or 3 or 7 } && itemRow.Value.LevelItem.RowId >= 15;

        // Compute consistent content width so checkmarks, labels, and submenu arrows align cleanly
        var rowWidth = RowMinWidth;
        var autoLabel = $"Auto ({cachedAutoLabel})";
        rowWidth = MathF.Max(rowWidth, LeftGutterWidth + ImGui.CalcTextSize(autoLabel).X + RightArrowGutterWidth + 14f);

        if (cachedCanTag)
        {
            if (DrawMenuRow(autoLabel, rowWidth, isChecked: !cachedIsManual, hasArrow: false, isSubmenuActive: false, out var autoHovered, out _))
            {
                magicSort.SetItemOverride(armedBaseItemId, string.Empty);
                chatGui.Print($"Set {armedItemName} -> {autoLabel}.");
                closeAllAfterAction = true;
            }

            if (autoHovered)
            {
                activeSubmenu = SubmenuKind.None;
                hoveredRouteFilter = cachedAutoRouteFilter;
            }

            ImGui.Separator();

            if (DrawMenuRow("Keep in Bag", rowWidth, isChecked: cachedIsManual && string.Equals(currentRoute, "Keep", StringComparison.OrdinalIgnoreCase), hasArrow: false, isSubmenuActive: false, out var keepHovered, out _))
            {
                ApplyOverride("Keep", null, "Keep in Bag");
                closeAllAfterAction = true;
            }

            if (keepHovered)
            {
                activeSubmenu = SubmenuKind.None;
                hoveredRouteFilter = "Keep";
            }

            if (itemRow is { ItemAction.RowId: > 0 })
            {
                if (DrawMenuRow("Use", rowWidth, isChecked: cachedIsManual && string.Equals(currentRoute, "Use", StringComparison.OrdinalIgnoreCase), hasArrow: false, isSubmenuActive: false, out var useHovered, out _))
                {
                    ApplyOverride("Use", null, "Use");
                    closeAllAfterAction = true;
                }

                if (useHovered)
                {
                    activeSubmenu = SubmenuKind.None;
                    hoveredRouteFilter = "Use";
                }
            }

            if (cachedRetainers.Count > 0)
            {
                var storeChecked = cachedIsManual && string.Equals(currentRoute, "Retainer", StringComparison.OrdinalIgnoreCase);
                DrawMenuRow("Store", rowWidth, isChecked: storeChecked, hasArrow: true, isSubmenuActive: activeSubmenu == SubmenuKind.Store, out var storeHovered, out var storeMinY);
                if (storeHovered)
                {
                    activeSubmenu = SubmenuKind.Store;
                    submenuAnchorY = storeMinY;
                    hoveredRouteFilter = "Retainer";
                }
            }

            if (canMarket || canVendor || canGc)
            {
                var sellChecked = cachedIsManual
                    && (string.Equals(currentRoute, "Market", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(currentRoute, "Vendor", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(currentRoute, "GrandCompany", StringComparison.OrdinalIgnoreCase));
                DrawMenuRow("Sell", rowWidth, isChecked: sellChecked, hasArrow: true, isSubmenuActive: activeSubmenu == SubmenuKind.Sell, out var sellHovered, out var sellMinY);
                if (sellHovered)
                {
                    activeSubmenu = SubmenuKind.Sell;
                    submenuAnchorY = sellMinY;
                    hoveredRouteFilter = "Sell";
                }
            }

            if (itemRow is { EquipSlotCategory.RowId: > 0 })
            {
                if (DrawMenuRow("Glamour Dresser", rowWidth, isChecked: cachedIsManual && string.Equals(currentRoute, "Glamour", StringComparison.OrdinalIgnoreCase), hasArrow: false, isSubmenuActive: false, out var glamHovered, out _))
                {
                    ApplyOverride("Glamour", null, "Glamour Dresser");
                    closeAllAfterAction = true;
                }

                if (glamHovered)
                {
                    activeSubmenu = SubmenuKind.None;
                    hoveredRouteFilter = "Glamour";
                }
            }

            if (cachedIsCabinet)
            {
                if (DrawMenuRow("Armoire", rowWidth, isChecked: cachedIsManual && string.Equals(currentRoute, "Armoire", StringComparison.OrdinalIgnoreCase), hasArrow: false, isSubmenuActive: false, out var armoireHovered, out _))
                {
                    ApplyOverride("Armoire", null, "Armoire");
                    closeAllAfterAction = true;
                }

                if (armoireHovered)
                {
                    activeSubmenu = SubmenuKind.None;
                    hoveredRouteFilter = "Armoire";
                }
            }

            ImGui.Separator();
        }

        DrawMenuRow("Track", rowWidth, isChecked: cachedIsItemTracked, hasArrow: true, isSubmenuActive: activeSubmenu == SubmenuKind.Track, out var trackHovered, out var trackMinY);
        if (trackHovered)
        {
            activeSubmenu = SubmenuKind.Track;
            submenuAnchorY = trackMinY;
        }

        if (cachedCanTag)
        {
            var lockLabel = cachedIsLocked ? "Unlock Item" : "Lock Item";
            if (DrawMenuRow(lockLabel, rowWidth, isChecked: cachedIsLocked, hasArrow: false, isSubmenuActive: false, out var lockHovered, out _))
            {
                if (magicSort.ToggleItemLock(armedBaseItemId, out var locked))
                {
                    chatGui.Print(locked ? $"Locked {armedItemName}." : $"Unlocked {armedItemName}.");
                }

                closeAllAfterAction = true;
            }

            if (lockHovered)
            {
                activeSubmenu = SubmenuKind.None;
            }
        }

        panel1Pos = ImGui.GetWindowPos();
        panel1Size = ImGui.GetWindowSize();
        ImGui.End();
    }

    private void DrawSubmenuPanel(ref string? hoveredRouteFilter, ref bool closeAllAfterAction)
    {
        var vp = ImGui.GetMainViewport();
        var vpPos = vp.Pos;
        var vpSize = vp.Size;

        var estWidth = panel2Size.X > 40f ? panel2Size.X : SubmenuMinWidth + 16f;
        var estHeight = panel2Size.Y > 40f ? panel2Size.Y : 130f;

        var drawX = panel1Pos.X + panel1Size.X - 2f;
        if (drawX + estWidth > vpPos.X + vpSize.X - 6f)
        {
            drawX = MathF.Max(vpPos.X + 6f, panel1Pos.X - estWidth + 2f);
        }

        var drawY = Math.Clamp(submenuAnchorY - 6f, vpPos.Y + 6f, MathF.Max(vpPos.Y + 6f, vpPos.Y + vpSize.Y - estHeight - 6f));

        ImGui.SetNextWindowPos(new Vector2(drawX, drawY), ImGuiCond.Always);
        var flags = ImGuiWindowFlags.NoTitleBar
            | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.NoScrollWithMouse
            | ImGuiWindowFlags.AlwaysAutoResize
            | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoFocusOnAppearing;

        if (!ImGui.Begin("##MagicInventoryContextSubmenu", flags))
        {
            ImGui.End();
            return;
        }

        panel2DrawnLastFrame = true;
        var currentRoute = cachedCurrentEntry?.Route ?? "Keep";
        var currentRetainerId = cachedCurrentEntry?.TargetRetainerId ?? 0UL;
        var itemRow = cachedItemRow;
        var subWidth = SubmenuMinWidth;

        switch (activeSubmenu)
        {
            case SubmenuKind.Store:
            {
                foreach (var ret in cachedRetainers)
                {
                    subWidth = MathF.Max(subWidth, LeftGutterWidth + ImGui.CalcTextSize(ret.Name).X + 20f);
                }

                foreach (var ret in cachedRetainers)
                {
                    var isChecked = cachedIsManual
                        && string.Equals(currentRoute, "Retainer", StringComparison.OrdinalIgnoreCase)
                        && currentRetainerId == ret.RetainerId;
                    if (DrawMenuRow(ret.Name, subWidth, isChecked, hasArrow: false, isSubmenuActive: false, out var hovered, out _))
                    {
                        ApplyOverride($"Retainer:{ret.RetainerId}", null, ret.Name);
                        closeAllAfterAction = true;
                    }

                    if (hovered)
                    {
                        hoveredRouteFilter = $"Retainer:{ret.RetainerId}";
                    }
                }

                break;
            }

            case SubmenuKind.Sell:
            {
                var canMarket = itemRow is null || !itemRow.Value.IsUntradable;
                var canVendor = itemRow is null || (itemRow.Value.PriceLow > 0 && !itemRow.Value.IsUnique);
                var canGc = itemRow is { EquipSlotCategory.RowId: > 0, Rarity: 2 or 3 or 7 } && itemRow.Value.LevelItem.RowId >= 15;
                subWidth = MathF.Max(subWidth, LeftGutterWidth + ImGui.CalcTextSize("Market (High Priority)").X + 20f);

                if (canMarket)
                {
                    var isMarket = cachedIsManual && string.Equals(currentRoute, "Market", StringComparison.OrdinalIgnoreCase);

                    if (DrawMenuRow("Market (High Priority)", subWidth, isMarket && cachedMarketPriority > 0, hasArrow: false, isSubmenuActive: false, out var hiHover, out _))
                    {
                        ApplyOverride("Market", 1, "Market (High Priority)");
                        closeAllAfterAction = true;
                    }

                    if (hiHover)
                    {
                        hoveredRouteFilter = "Market";
                    }

                    if (DrawMenuRow("Market", subWidth, isMarket && cachedMarketPriority == 0, hasArrow: false, isSubmenuActive: false, out var normHover, out _))
                    {
                        ApplyOverride("Market", 0, "Market");
                        closeAllAfterAction = true;
                    }

                    if (normHover)
                    {
                        hoveredRouteFilter = "Market";
                    }

                    if (DrawMenuRow("Market (Low Priority)", subWidth, isMarket && cachedMarketPriority < 0, hasArrow: false, isSubmenuActive: false, out var loHover, out _))
                    {
                        ApplyOverride("Market", -1, "Market (Low Priority)");
                        closeAllAfterAction = true;
                    }

                    if (loHover)
                    {
                        hoveredRouteFilter = "Market";
                    }
                }

                if (canVendor)
                {
                    if (DrawMenuRow("Vendor", subWidth, cachedIsManual && string.Equals(currentRoute, "Vendor", StringComparison.OrdinalIgnoreCase), hasArrow: false, isSubmenuActive: false, out var vHover, out _))
                    {
                        ApplyOverride("Vendor", null, "Vendor");
                        closeAllAfterAction = true;
                    }

                    if (vHover)
                    {
                        hoveredRouteFilter = "Vendor";
                    }
                }

                if (canGc)
                {
                    if (DrawMenuRow("GC Seals", subWidth, cachedIsManual && string.Equals(currentRoute, "GrandCompany", StringComparison.OrdinalIgnoreCase), hasArrow: false, isSubmenuActive: false, out var gcHover, out _))
                    {
                        ApplyOverride("GrandCompany", null, "GC Seals");
                        closeAllAfterAction = true;
                    }

                    if (gcHover)
                    {
                        hoveredRouteFilter = "GrandCompany";
                    }
                }

                break;
            }

            case SubmenuKind.Track:
            {
                var itemLabel = cachedIsItemTracked ? "Untrack Item" : "Item";
                if (DrawMenuRow(itemLabel, subWidth, cachedIsItemTracked, hasArrow: false, isSubmenuActive: false, out _, out _))
                {
                    materialTracker.ToggleSingleItem(armedBaseItemId);
                    chatGui.Print(cachedIsItemTracked
                        ? $"Stopped tracking {armedItemName}."
                        : $"Tracking item: {armedItemName}.");
                    closeAllAfterAction = true;
                }

                if (cachedHasRecipe)
                {
                    if (DrawMenuRow("Recipe", subWidth, isChecked: false, hasArrow: false, isSubmenuActive: false, out _, out _))
                    {
                        materialTracker.TrackRecipeForResultItem(armedBaseItemId, out var msg);
                        if (!string.IsNullOrWhiteSpace(msg))
                        {
                            chatGui.Print(msg);
                        }

                        closeAllAfterAction = true;
                    }
                }

                break;
            }
        }

        panel2Pos = ImGui.GetWindowPos();
        panel2Size = ImGui.GetWindowSize();
        ImGui.End();
    }

    private static bool DrawMenuRow(
        string label,
        float width,
        bool isChecked,
        bool hasArrow,
        bool isSubmenuActive,
        out bool isHovered,
        out float rowMinY)
    {
        var cursor = ImGui.GetCursorScreenPos();
        rowMinY = cursor.Y;
        var rowSize = new Vector2(width, RowHeight);

        ImGui.PushID(label);
        var clicked = ImGui.InvisibleButton("##row", rowSize);
        isHovered = ImGui.IsItemHovered(ImGuiHoveredFlags.RectOnly);
        var isHeld = ImGui.IsItemActive();
        ImGui.PopID();

        var dl = ImGui.GetWindowDrawList();
        var rectMin = cursor;
        var rectMax = cursor + rowSize;

        if (isHeld)
        {
            dl.AddRectFilled(rectMin, rectMax, ImGui.ColorConvertFloat4ToU32(ColorActiveBg), 3f);
        }
        else if (isHovered || isSubmenuActive)
        {
            dl.AddRectFilled(rectMin, rectMax, ImGui.ColorConvertFloat4ToU32(ColorHoverBg), 3f);
        }

        if (isChecked)
        {
            // Vector checkmark in left gutter (zero font-glyph dependency)
            var checkCol = ImGui.ColorConvertFloat4ToU32(ColorGoldAccent);
            var cy = rectMin.Y + RowHeight * 0.5f;
            var p1 = new Vector2(rectMin.X + 4.5f, cy + 0.5f);
            var p2 = new Vector2(rectMin.X + 7.5f, cy + 3.5f);
            var p3 = new Vector2(rectMin.X + 13.0f, cy - 3.5f);
            dl.AddLine(p1, p2, checkCol, 1.8f);
            dl.AddLine(p2, p3, checkCol, 1.8f);
        }

        var textSize = ImGui.CalcTextSize(label);
        var textPos = new Vector2(rectMin.X + LeftGutterWidth + 2f, rectMin.Y + (RowHeight - textSize.Y) * 0.5f);
        var textCol = ImGui.ColorConvertFloat4ToU32(isChecked && !isHovered ? ColorGoldAccent : ColorText);
        dl.AddText(textPos, textCol, label);

        if (hasArrow)
        {
            var arrowCol = ImGui.ColorConvertFloat4ToU32(isHovered || isSubmenuActive ? ColorGoldAccent : ColorArrow);
            var cx = rectMax.X - 8f;
            var cy = rectMin.Y + RowHeight * 0.5f;
            dl.AddTriangleFilled(
                new Vector2(cx - 2.5f, cy - 4.0f),
                new Vector2(cx - 2.5f, cy + 4.0f),
                new Vector2(cx + 3.0f, cy),
                arrowCol);
        }

        return clicked;
    }

    private void ApplyOverride(string routeSpec, int? marketPriority, string displayLabel)
    {
        magicSort.SetItemOverride(armedBaseItemId, routeSpec, marketPriority);
        chatGui.Print($"Set {armedItemName} -> {displayLabel}.");
    }

    private void EnsureMetadataFresh(DateTime now)
    {
        if (cachedMetaItemId == armedBaseItemId && (now - cachedMetaUtc).TotalMilliseconds < 250.0)
        {
            return;
        }

        cachedMetaItemId = armedBaseItemId;
        cachedMetaUtc = now;

        cachedItemRow = dataManager.GetExcelSheet<Item>()?.GetRowOrDefault(armedBaseItemId);
        cachedCanTag = MagicSortService.CanLockOrTagBaseId(armedBaseItemId);
        cachedIsManual = magicSort.IsItemOverwritten(armedBaseItemId);
        cachedCurrentEntry = triageEngine.ClassifySingleItemOnDemand(armedBaseItemId, queuePriceIfMissing: false);
        cachedIsLocked = magicSort.IsItemLocked(armedBaseItemId);
        cachedIsItemTracked = materialTracker.IsItemTracked(armedBaseItemId);
        cachedHasRecipe = materialTracker.HasRecipeForItem(armedBaseItemId);
        cachedIsCabinet = triageEngine.IsCabinetItem(armedBaseItemId);
        cachedMarketPriority = safety.GetMarketPriority(armedBaseItemId);
        cachedRetainers = holdings.GetKnownRetainersForCurrentCharacter();

        var autoEntry = cachedIsManual
            ? triageEngine.ClassifySingleItemOnDemand(armedBaseItemId, queuePriceIfMissing: false, ignoreManualOverride: true)
            : cachedCurrentEntry;

        if (autoEntry == null || string.Equals(autoEntry.Route, "Keep", StringComparison.OrdinalIgnoreCase))
        {
            cachedAutoLabel = armedFromArmoury ? "Armoury" : "Bag";
            cachedAutoRouteFilter = "Keep";
        }
        else if (string.Equals(autoEntry.Route, "Retainer", StringComparison.OrdinalIgnoreCase))
        {
            cachedAutoLabel = string.IsNullOrWhiteSpace(autoEntry.TargetRetainerName)
                ? "Retainer"
                : autoEntry.TargetRetainerName;
            cachedAutoRouteFilter = autoEntry.TargetRetainerId != 0
                ? $"Retainer:{autoEntry.TargetRetainerId}"
                : "Retainer";
        }
        else if (string.Equals(autoEntry.Route, "GrandCompany", StringComparison.OrdinalIgnoreCase))
        {
            cachedAutoLabel = "GC Seals";
            cachedAutoRouteFilter = "GrandCompany";
        }
        else if (string.Equals(autoEntry.Route, "Glamour", StringComparison.OrdinalIgnoreCase))
        {
            cachedAutoLabel = "Glamour Dresser";
            cachedAutoRouteFilter = "Glamour";
        }
        else
        {
            cachedAutoLabel = autoEntry.Route;
            cachedAutoRouteFilter = autoEntry.Route;
        }
    }

    private bool TryLocateMagicContextRow(
        out float menuLeft,
        out float menuRight,
        out float rowTop,
        out float rowHeight,
        out bool isNativeRowHovered,
        out bool isOtherNativeRowHovered)
    {
        menuLeft = 0f;
        menuRight = 0f;
        rowTop = 0f;
        rowHeight = 24f;
        isNativeRowHovered = false;
        isOtherNativeRowHovered = false;

        var menu = NativeUiHelpers.GetReadyAddon(gameGui, "ContextMenu");
        if (menu == null || !menu->IsVisible)
        {
            return false;
        }

        AtkComponentList* list = null;
        var node2 = menu->GetNodeById(2);
        if (node2 != null)
        {
            list = node2->GetAsAtkComponentList();
        }

        if (list == null)
        {
            for (var n = 0; n < menu->UldManager.NodeListCount; n++)
            {
                var node = menu->UldManager.NodeList[n];
                if (node == null)
                {
                    continue;
                }

                var candidate = node->GetAsAtkComponentList();
                if (candidate != null && candidate->ListLength > 0)
                {
                    list = candidate;
                    break;
                }
            }
        }

        if (list == null || list->ItemRendererList == null || list->ListLength <= 0)
        {
            return false;
        }

        var magicIndex = -1;
        AtkComponentListItemRenderer* magicRenderer = null;
        for (var i = 0; i < list->ListLength; i++)
        {
            var renderer = list->ItemRendererList[i].AtkComponentListItemRenderer;
            if (renderer == null)
            {
                continue;
            }

            var textNode = renderer->AtkComponentButton.ButtonTextNode;
            if (textNode == null)
            {
                continue;
            }

            var text = textNode->NodeText.ToString();
            if (!string.IsNullOrEmpty(text)
                && text.Contains("Magic Inventory", StringComparison.OrdinalIgnoreCase))
            {
                magicIndex = i;
                magicRenderer = renderer;
                break;
            }
        }

        if (magicIndex < 0 || magicRenderer == null || magicRenderer->OwnerNode == null)
        {
            return false;
        }

        var vpPos = ImGui.GetMainViewport().Pos;
        var addonScale = menu->Scale > 0.01f ? menu->Scale : 1f;
        var owner = (AtkResNode*)magicRenderer->OwnerNode;

        var rx = 0f;
        var ry = 0f;
        var sx = 1f;
        var sy = 1f;
        var cur = owner;
        while (cur != null && cur->ParentNode != null)
        {
            rx = rx * cur->ScaleX + cur->X;
            ry = ry * cur->ScaleY + cur->Y;
            sx *= cur->ScaleX;
            sy *= cur->ScaleY;
            cur = cur->ParentNode;
        }

        var rowLeft = vpPos.X + menu->X + rx * addonScale;
        rowTop = vpPos.Y + menu->Y + ry * addonScale;
        var rowW = owner->Width * sx * addonScale;
        rowHeight = MathF.Max(16f, owner->Height * sy * addonScale);

        var rootWidth = menu->RootNode != null ? menu->RootNode->Width * addonScale : 0f;
        menuLeft = vpPos.X + menu->X;
        menuRight = rootWidth > rowW ? menuLeft + rootWidth : rowLeft + rowW + 8f * addonScale;

        var mouse = ImGui.GetMousePos();
        var hoveredIdx = list->HoveredItemIndex;
        var inRowRect = mouse.X >= menuLeft - 2f
            && mouse.X <= menuRight + 4f
            && mouse.Y >= rowTop - 2f
            && mouse.Y <= rowTop + rowHeight + 2f;

        isNativeRowHovered = hoveredIdx == magicIndex || inRowRect;
        isOtherNativeRowHovered = hoveredIdx >= 0 && hoveredIdx != magicIndex && !inRowRect;
        return true;
    }

    private bool IsMouseInFlyoutOrBridge(Vector2 mouse, float menuLeft, float menuRight, float rowTop, float rowHeight)
    {
        if (IsMouseInDrawnPanels(mouse, padding: 12f))
        {
            return true;
        }

        // Generous horizontal bridge between the native "Magic Inventory" row and Panel 1 (supports both right-side & left-flipped placement)
        var p1Right = panel1Pos.X + panel1Size.X;
        var gap1MinX = panel1Pos.X >= menuLeft ? MathF.Min(menuRight, panel1Pos.X) - 10f : MathF.Min(p1Right, menuLeft) - 10f;
        var gap1MaxX = panel1Pos.X >= menuLeft ? MathF.Max(menuRight, panel1Pos.X) + 10f : MathF.Max(p1Right, menuLeft) + 10f;
        var gap1MinY = MathF.Min(rowTop, panel1Pos.Y) - 6f;
        var gap1MaxY = MathF.Max(rowTop + rowHeight, panel1Pos.Y + panel1Size.Y) + 6f;
        if (mouse.X >= gap1MinX && mouse.X <= gap1MaxX && mouse.Y >= gap1MinY && mouse.Y <= gap1MaxY)
        {
            return true;
        }

        // Bridge between Panel 1 and Panel 2 (supports both right-side & left-flipped placement)
        if (panel2DrawnLastFrame && panel2Size.X > 0f)
        {
            var p2Right = panel2Pos.X + panel2Size.X;
            var gap2MinX = panel2Pos.X >= panel1Pos.X ? MathF.Min(p1Right, panel2Pos.X) - 12f : MathF.Min(p2Right, panel1Pos.X) - 12f;
            var gap2MaxX = panel2Pos.X >= panel1Pos.X ? MathF.Max(p1Right, panel2Pos.X) + 12f : MathF.Max(p2Right, panel1Pos.X) + 12f;
            var gap2MinY = MathF.Min(panel1Pos.Y, panel2Pos.Y) - 6f;
            var gap2MaxY = MathF.Max(panel1Pos.Y + panel1Size.Y, panel2Pos.Y + panel2Size.Y) + 6f;
            if (mouse.X >= gap2MinX && mouse.X <= gap2MaxX && mouse.Y >= gap2MinY && mouse.Y <= gap2MaxY)
            {
                return true;
            }
        }

        return false;
    }

    private bool IsMouseInDrawnPanels(Vector2 mouse, float padding)
    {
        if (panel1Size.X > 0f && panel1Size.Y > 0f
            && mouse.X >= panel1Pos.X - padding
            && mouse.X <= panel1Pos.X + panel1Size.X + padding
            && mouse.Y >= panel1Pos.Y - padding
            && mouse.Y <= panel1Pos.Y + panel1Size.Y + padding)
        {
            return true;
        }

        if (panel2DrawnLastFrame && panel2Size.X > 0f && panel2Size.Y > 0f
            && mouse.X >= panel2Pos.X - padding
            && mouse.X <= panel2Pos.X + panel2Size.X + padding
            && mouse.Y >= panel2Pos.Y - padding
            && mouse.Y <= panel2Pos.Y + panel2Size.Y + padding)
        {
            return true;
        }

        return false;
    }

    private void CloseNativeContextMenu()
    {
        var menu = NativeUiHelpers.GetReadyAddon(gameGui, "ContextMenu");
        if (menu == null || !menu->IsVisible)
        {
            return;
        }

        menu->Close(true);
    }

    private void CloseFlyoutOnly()
    {
        isFlyoutOpen = false;
        isPinnedFromClick = false;
        activeSubmenu = SubmenuKind.None;
        panel2DrawnLastFrame = false;
        highlightController.SetHoveredRouteFilter(null);
    }

    private void ResetAll()
    {
        armedBaseItemId = 0;
        armedItemName = string.Empty;
        armedFromArmoury = false;
        anchorPos = Vector2.Zero;
        CloseFlyoutOnly();
    }

    private static void PushFlyoutStyle()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(6f, 6f));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(4f, 2f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 4f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, ColorWindowBg);
        ImGui.PushStyleColor(ImGuiCol.Border, ColorBorder);
        ImGui.PushStyleColor(ImGuiCol.Separator, ColorSeparator);
    }

    private static void PopFlyoutStyle()
    {
        ImGui.PopStyleColor(3);
        ImGui.PopStyleVar(4);
    }

    public void Dispose()
    {
        ResetAll();
    }
}
