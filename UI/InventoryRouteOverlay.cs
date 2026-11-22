using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using MagicInventory.Core;
using MagicInventory.Services;

namespace MagicInventory.UI;

public sealed unsafe class InventoryRouteOverlay : IDisposable
{
    private readonly record struct SlotBadge(string Label, Vector4 Color);

    private static readonly Vector4 ColorSellHighlight = new(0.96f, 0.78f, 0.28f, 1f);
    private static readonly Vector4 ColorWithdrawHighlight = new(0.24f, 0.86f, 0.86f, 1f);

    private readonly IGameGui gameGui;
    private readonly Configuration configuration;
    private readonly InventorySafetyEvaluator safety;
    private readonly MagicSortService magicSort;

    private string? hoveredInventorySortAddon;
    private string? hoveredSellAddon;
    private string? hoveredRetainerSortAddon;
    private bool glamourListHovered;

    private readonly Dictionary<string, IReadOnlyList<string?>> contextSubmenusByTitle = new(StringComparer.OrdinalIgnoreCase);
    private string? activeContextSubmenuTitle;
    private IReadOnlyList<string?>? activeContextRouteFilters;
    private DateTime contextSubmenuArmedUtc = DateTime.MinValue;
    private string? lastResolvedContextFilter;

    private DateTime lastCacheUtc = DateTime.MinValue;
    private readonly Dictionary<(string AddonName, uint NodeId), SlotBadge> cachedBadges = new();
    private readonly Dictionary<(string Label, int BucketWidth, int FontSizeBucket), string> fittedLabelCache = new();
    private readonly List<InventoryGridLayout.VisualGridSlot> visualSlotScratch = new(320);

    private readonly Dictionary<string, nint> frameAddonCache = new(16, StringComparer.Ordinal);
    private readonly Dictionary<string, List<(Vector2 Min, Vector2 Max)>> frameOccluderCache = new(16, StringComparer.Ordinal);
    private readonly Stack<List<(Vector2 Min, Vector2 Max)>> occluderListPool = new(16);

    private DateTime lastGlamourIdsUtc = DateTime.MinValue;
    private HashSet<(InventoryType Container, int Slot)> cachedGlamourSlots = new();
    private IReadOnlyList<string> cachedGlamourNames = Array.Empty<string>();

    public InventoryRouteOverlay(
        IGameGui gameGui,
        Configuration configuration,
        InventorySafetyEvaluator safety,
        MagicSortService magicSort)
    {
        this.gameGui = gameGui;
        this.configuration = configuration;
        this.safety = safety;
        this.magicSort = magicSort;
    }

    public void SetGlamourListHover(bool isHovered)
    {
        glamourListHovered = isHovered;
        lastGlamourIdsUtc = DateTime.MinValue;
    }

    public void SetHoveredInventorySortAddon(string addonName, bool isHovered)
    {
        if (isHovered)
        {
            if (!string.Equals(hoveredInventorySortAddon, addonName, StringComparison.Ordinal))
            {
                hoveredInventorySortAddon = addonName;
                InvalidatePreviewCache();
            }
        }
        else if (string.Equals(hoveredInventorySortAddon, addonName, StringComparison.Ordinal))
        {
            hoveredInventorySortAddon = null;
            InvalidatePreviewCache();
        }
    }

    public void SetHoveredSellAddon(string addonName, bool isHovered)
    {
        if (isHovered)
        {
            if (!string.Equals(hoveredSellAddon, addonName, StringComparison.Ordinal))
            {
                hoveredSellAddon = addonName;
                InvalidatePreviewCache();
            }
        }
        else if (string.Equals(hoveredSellAddon, addonName, StringComparison.Ordinal))
        {
            hoveredSellAddon = null;
            InvalidatePreviewCache();
        }
    }

    public void SetHoveredRetainerSortAddon(string addonName, bool isHovered)
    {
        if (isHovered)
        {
            if (!string.Equals(hoveredRetainerSortAddon, addonName, StringComparison.Ordinal))
            {
                hoveredRetainerSortAddon = addonName;
                InvalidatePreviewCache();
            }
        }
        else if (string.Equals(hoveredRetainerSortAddon, addonName, StringComparison.Ordinal))
        {
            hoveredRetainerSortAddon = null;
            InvalidatePreviewCache();
        }
    }

    public void SetActiveContextSubmenuRoutes(string? submenuTitle, IReadOnlyList<string?>? routeFiltersByIndex)
    {
        if (routeFiltersByIndex == null || routeFiltersByIndex.Count == 0)
        {
            lastResolvedContextFilter = null;
            if (string.IsNullOrWhiteSpace(submenuTitle))
            {
                activeContextSubmenuTitle = null;
                activeContextRouteFilters = null;
                contextSubmenusByTitle.Clear();
            }
            else
            {
                var empty = Array.Empty<string?>();
                activeContextSubmenuTitle = submenuTitle;
                activeContextRouteFilters = empty;
                contextSubmenusByTitle[submenuTitle] = empty;
                contextSubmenuArmedUtc = DateTime.UtcNow;
            }

            InvalidatePreviewCache();
            return;
        }

        activeContextSubmenuTitle = submenuTitle;
        activeContextRouteFilters = routeFiltersByIndex;
        if (!string.IsNullOrWhiteSpace(submenuTitle))
        {
            contextSubmenusByTitle[submenuTitle] = routeFiltersByIndex;
        }

        contextSubmenuArmedUtc = DateTime.UtcNow;
        InvalidatePreviewCache();
    }

    private void InvalidatePreviewCache()
    {
        lastCacheUtc = DateTime.MinValue;
    }

    public void Draw()
    {
        if (!configuration.EnableHoverHighlights)
        {
            if (cachedBadges.Count > 0)
            {
                cachedBadges.Clear();
            }

            return;
        }

        ResetFrameCaches();

        DrawGlamourListHighlights();

        PruneClosedHoveredAddons();

        var hasContextHover = TryResolveHoveredContextMenuRouteFilter(out var contextRouteFilter);
        var hasAnyHover = hasContextHover
            || hoveredRetainerSortAddon != null
            || hoveredSellAddon != null
            || hoveredInventorySortAddon != null;

        if (!hasAnyHover)
        {
            if (cachedBadges.Count > 0)
            {
                cachedBadges.Clear();
            }

            lastResolvedContextFilter = null;
            return;
        }

        var now = DateTime.UtcNow;
        var contextChanged = hasContextHover && !string.Equals(contextRouteFilter, lastResolvedContextFilter, StringComparison.OrdinalIgnoreCase);
        var pollMs = hasContextHover ? 60.0 : 200.0;
        if (contextChanged || (now - lastCacheUtc).TotalMilliseconds >= pollMs)
        {
            lastResolvedContextFilter = hasContextHover ? contextRouteFilter : null;
            RebuildBadges(hasContextHover ? contextRouteFilter : null);
            lastCacheUtc = now;
        }

        if (cachedBadges.Count == 0)
        {
            return;
        }

        var dl = ImGui.GetBackgroundDrawList();
        var viewport = ImGui.GetMainViewport();
        var vpPos = viewport.Pos;
        var vpSize = viewport.Size;
        var font = ImGui.GetFont();
        var baseFontSize = MathF.Max(1f, ImGui.GetFontSize());
        var uiScale = ImGuiHelpers.GlobalScale;

        foreach (var ((addonName, nodeId), badge) in cachedBadges)
        {
            if (!frameAddonCache.TryGetValue(addonName, out var addonAddr))
            {
                var ready = NativeUiHelpers.GetReadyAddon(gameGui, addonName);
                addonAddr = (nint)ready;
                frameAddonCache[addonName] = addonAddr;
            }

            if (addonAddr == 0)
            {
                continue;
            }

            var addon = (AtkUnitBase*)addonAddr;
            var node = addon->GetNodeById(nodeId);
            if (node == null || !node->IsVisible())
            {
                continue;
            }

            if (!InventoryGridLayout.TryGetNodeScreenRect(addon, node, vpPos, out var min, out var max))
            {
                continue;
            }

            var occluders = GetFrameOccluders(addonName, addon, vpPos, vpSize);
            if (IsOccludedByAny(min, max, occluders))
            {
                continue;
            }

            var width = max.X - min.X;
            var height = max.Y - min.Y;
            if (width < 18f || height < 18f)
            {
                continue;
            }

            var insetMin = new Vector2(min.X + 1.5f, min.Y + 1.5f);
            var insetMax = new Vector2(max.X - 1.5f, max.Y - 1.5f);

            var washCol = ImGui.ColorConvertFloat4ToU32(new Vector4(badge.Color.X, badge.Color.Y, badge.Color.Z, 0.24f));
            var borderCol = ImGui.ColorConvertFloat4ToU32(new Vector4(badge.Color.X, badge.Color.Y, badge.Color.Z, 0.90f));
            dl.AddRectFilled(insetMin, insetMax, washCol, 3f);
            dl.AddRect(insetMin, insetMax, borderCol, 3f, ImDrawFlags.None, 1.4f);

            if (!string.IsNullOrEmpty(badge.Label))
            {
                var fontSize = Math.Clamp(height * 0.28f, 9f, 13f * uiScale);
                var pillHeight = MathF.Min(fontSize + 3f, height * 0.42f);
                var pillMin = new Vector2(insetMin.X, insetMax.Y - pillHeight);
                var pillMax = insetMax;
                var pillBg = ImGui.ColorConvertFloat4ToU32(new Vector4(0.05f, 0.06f, 0.08f, 0.88f));
                dl.AddRectFilled(pillMin, pillMax, pillBg, 2f);

                var fitted = GetFittedLabel(badge.Label, width - 5f, fontSize);
                var textSize = ImGui.CalcTextSize(fitted) * (fontSize / baseFontSize);
                var textX = pillMin.X + MathF.Max(1.5f, ((pillMax.X - pillMin.X) - textSize.X) * 0.5f);
                var textY = pillMin.Y + MathF.Max(0f, (pillHeight - textSize.Y) * 0.5f);

                var textCol = ImGui.ColorConvertFloat4ToU32(badge.Color);
                dl.AddText(font, fontSize, new Vector2(textX, textY), textCol, fitted);
            }
        }
    }

    private void PruneClosedHoveredAddons()
    {
        static string StripAddonSuffix(string addonKey)
        {
            var colon = addonKey.IndexOf(':');
            return colon > 0 ? addonKey[..colon] : addonKey;
        }

        if (hoveredInventorySortAddon != null && !NativeUiHelpers.IsAddonOpenAndReady(gameGui, StripAddonSuffix(hoveredInventorySortAddon)))
        {
            hoveredInventorySortAddon = null;
        }

        if (hoveredSellAddon != null && !NativeUiHelpers.IsAddonOpenAndReady(gameGui, StripAddonSuffix(hoveredSellAddon)))
        {
            hoveredSellAddon = null;
        }

        if (hoveredRetainerSortAddon != null && !NativeUiHelpers.IsAddonOpenAndReady(gameGui, StripAddonSuffix(hoveredRetainerSortAddon)))
        {
            hoveredRetainerSortAddon = null;
        }
    }

    private void RebuildBadges(string? contextRouteFilter)
    {
        cachedBadges.Clear();
        var manager = InventoryManager.Instance();
        var entries = magicSort.GetLatestManifest();
        if (manager == null)
        {
            return;
        }

        var activeRetainerId = HoldingsService.GetActiveRetainerId();
        visualSlotScratch.Clear();
        InventoryGridLayout.CollectVisiblePlayerBagSlots(gameGui, visualSlotScratch);
        InventoryGridLayout.CollectVisibleArmourySlots(gameGui, visualSlotScratch);
        if (activeRetainerId != 0)
        {
            InventoryGridLayout.CollectVisibleRetainerSlots(gameGui, visualSlotScratch);
        }

        if (visualSlotScratch.Count == 0)
        {
            return;
        }

        if (!string.IsNullOrEmpty(contextRouteFilter))
        {
            magicSort.GetSlotsMatchingRouteFilter(
                contextRouteFilter,
                out var ctxPlayerSlots,
                out var ctxRetainerSlots,
                out var ctxArmourySlots);
            var routeName = contextRouteFilter.StartsWith("Retainer:", StringComparison.OrdinalIgnoreCase)
                ? "Retainer"
                : contextRouteFilter;
            var col = RoutePresentation.GetRouteColor(routeName);
            var badgeLabel = RoutePresentation.GetShortBadgeLabel(routeName, string.Empty);

            foreach (var vs in visualSlotScratch)
            {
                var key = (vs.Container, vs.PhysicalSlot);
                if (ctxPlayerSlots.Contains(key) || ctxArmourySlots.Contains(key))
                {
                    cachedBadges[(vs.AddonName, vs.NodeId)] = new SlotBadge(badgeLabel, col);
                }
                else if (ctxRetainerSlots.Contains(key))
                {
                    cachedBadges[(vs.AddonName, vs.NodeId)] = new SlotBadge(badgeLabel, ColorWithdrawHighlight);
                }
            }

            return;
        }

        if (hoveredRetainerSortAddon != null && activeRetainerId != 0)
        {
            magicSort.GetActiveRetainerSortHighlightSlots(
                hoveredRetainerSortAddon,
                out var depositSlots,
                out var withdrawSlots);
            if (depositSlots.Count == 0 && withdrawSlots.Count == 0)
            {
                return;
            }

            var gearsetIds = safety.BuildGearsetItemIds();
            var unlockedProfile = safety.BuildUnlockedJobGearProfile();
            var missingTracked = magicSort.SlotResolver.CaptureMissingTrackedSnapshot();

            foreach (var vs in visualSlotScratch)
            {
                var key = (vs.Container, vs.PhysicalSlot);
                var isDeposit = depositSlots.Contains(key);
                var isWithdraw = withdrawSlots.Contains(key);
                if (!isDeposit && !isWithdraw)
                {
                    continue;
                }

                var badge = ResolveSlotBadge(manager, vs, entries, gearsetIds, unlockedProfile, missingTracked, activeRetainerId, isWithdraw ? ColorWithdrawHighlight : ColorSellHighlight, isDeposit ? "Store" : "Pull");
                cachedBadges[(vs.AddonName, vs.NodeId)] = badge;
            }

            return;
        }

        if (hoveredSellAddon != null)
        {
            var plannedSlots = magicSort.GetActiveSellHighlightSlots(hoveredSellAddon);
            if (plannedSlots.Count == 0)
            {
                return;
            }

            var gearsetIds = safety.BuildGearsetItemIds();
            var unlockedProfile = safety.BuildUnlockedJobGearProfile();
            var missingTracked = magicSort.SlotResolver.CaptureMissingTrackedSnapshot();

            foreach (var vs in visualSlotScratch)
            {
                if (!plannedSlots.Contains((vs.Container, vs.PhysicalSlot)))
                {
                    continue;
                }

                var badge = ResolveSlotBadge(manager, vs, entries, gearsetIds, unlockedProfile, missingTracked, activeRetainerId, ColorSellHighlight, string.Empty);
                cachedBadges[(vs.AddonName, vs.NodeId)] = badge;
            }

            return;
        }

        if (hoveredInventorySortAddon != null)
        {
            var moveList = magicSort.GetInventorySortHighlightSlots();
            if (moveList.Count == 0)
            {
                return;
            }

            var moveSet = new HashSet<(InventoryType Container, int Slot)>(moveList);
            var gearsetIds = safety.BuildGearsetItemIds();
            var unlockedProfile = safety.BuildUnlockedJobGearProfile();
            var missingTracked = magicSort.SlotResolver.CaptureMissingTrackedSnapshot();

            foreach (var vs in visualSlotScratch)
            {
                if (!moveSet.Contains((vs.Container, vs.PhysicalSlot)))
                {
                    continue;
                }

                var badge = ResolveSlotBadge(manager, vs, entries, gearsetIds, unlockedProfile, missingTracked, activeRetainerId, RoutePresentation.ColorArmoury, "Sort");
                cachedBadges[(vs.AddonName, vs.NodeId)] = badge;
            }
        }
    }

    private SlotBadge ResolveSlotBadge(
        InventoryManager* manager,
        InventoryGridLayout.VisualGridSlot vs,
        IReadOnlyDictionary<uint, MagicSortEntry> entries,
        HashSet<uint> gearsetIds,
        UnlockedJobGearProfile unlockedProfile,
        IReadOnlyDictionary<uint, int>? missingTracked,
        ulong activeRetainerId,
        Vector4 fallbackColor,
        string fallbackLabel)
    {
        var slot = InventorySafetyEvaluator.GetSlot(manager, vs.Container, vs.PhysicalSlot);
        if (slot == null || slot->ItemId == 0 || slot->Quantity <= 0)
        {
            return new SlotBadge(fallbackLabel, fallbackColor);
        }

        var inRetainer = InventorySafetyEvaluator.IsRetainerContainer(vs.Container);
        var inArmoury = InventorySafetyEvaluator.IsArmouryContainer(vs.Container);
        var resolved = magicSort.SlotResolver.ResolveSlot(
            slot->ItemId,
            vs.Container,
            slot,
            entries,
            gearsetIds,
            unlockedProfile,
            includeBagToArmouryRoute: !inRetainer && !inArmoury,
            missingTracked: missingTracked);

        if (resolved is null)
        {
            return new SlotBadge(fallbackLabel, fallbackColor);
        }

        var r = resolved.Value;
        var shortLabel = inRetainer && string.Equals(r.Route, "Keep", StringComparison.OrdinalIgnoreCase)
            ? "Keep"
            : RoutePresentation.GetShortBadgeLabel(r.Route, r.TargetRetainerName);
        if (string.IsNullOrEmpty(shortLabel))
        {
            shortLabel = fallbackLabel;
        }

        var color = inRetainer && string.Equals(r.Route, "Keep", StringComparison.OrdinalIgnoreCase)
            ? RoutePresentation.ColorTracked
            : RoutePresentation.GetRouteColor(r.Route);
        return new SlotBadge(shortLabel, color);
    }

    private bool TryResolveHoveredContextMenuRouteFilter(out string routeFilter)
    {
        routeFilter = string.Empty;
        var routes = activeContextRouteFilters;
        if (routes == null && contextSubmenusByTitle.Count == 0)
        {
            return false;
        }

        var elapsedMs = (DateTime.UtcNow - contextSubmenuArmedUtc).TotalMilliseconds;
        var menu = NativeUiHelpers.GetReadyAddon(gameGui, "AddonContextSub");
        if (menu == null || !menu->IsVisible)
        {
            if (elapsedMs > 450.0)
            {
                activeContextRouteFilters = null;
                activeContextSubmenuTitle = null;
                contextSubmenusByTitle.Clear();
            }

            return false;
        }

        var titleNode = menu->GetNodeById(2);
        var titleTextNode = titleNode != null ? titleNode->GetAsAtkTextNode() : null;
        if (titleTextNode == null)
        {
            return false;
        }

        var actualTitle = NativeUiHelpers.ReadTextNodeValue(titleTextNode);
        if (!string.IsNullOrWhiteSpace(actualTitle)
            && contextSubmenusByTitle.TryGetValue(actualTitle, out var mappedRoutes))
        {
            routes = mappedRoutes;
            activeContextRouteFilters = mappedRoutes;
            activeContextSubmenuTitle = actualTitle;
        }
        else if (string.IsNullOrEmpty(activeContextSubmenuTitle)
            || !string.Equals(actualTitle, activeContextSubmenuTitle, StringComparison.OrdinalIgnoreCase))
        {
            if (elapsedMs > 450.0)
            {
                activeContextRouteFilters = null;
                activeContextSubmenuTitle = null;
                contextSubmenusByTitle.Clear();
            }

            return false;
        }

        if (routes == null || routes.Count == 0)
        {
            return false;
        }

        AtkComponentList* list = null;
        var node4 = menu->GetNodeById(4);
        if (node4 != null)
        {
            list = node4->GetAsAtkComponentList();
        }

        if (list == null && menu->UldManager.NodeList != null)
        {
            for (var n = 0; n < menu->UldManager.NodeListCount; n++)
            {
                var node = menu->UldManager.NodeList[n];
                if (node == null)
                {
                    continue;
                }

                var candidate = node->GetAsAtkComponentList();
                if (candidate != null && candidate->ItemRendererList != null && candidate->ListLength > 0)
                {
                    list = candidate;
                    break;
                }
            }
        }

        if (list == null || list->ItemRendererList == null || list->ListLength <= 0 || list->AllocatedItemRendererListLength <= 0)
        {
            return false;
        }

        var activeRowCount = Math.Min(Math.Min(routes.Count, list->ListLength), list->AllocatedItemRendererListLength);
        var hoveredIdx = list->HoveredItemIndex;

        if (hoveredIdx < 0 || hoveredIdx >= activeRowCount)
        {
            var mouse = ImGui.GetMousePos();
            var vpPos = ImGui.GetMainViewport().Pos;

            for (var i = 0; i < activeRowCount; i++)
            {
                var renderer = list->ItemRendererList[i].AtkComponentListItemRenderer;
                if (renderer == null || renderer->OwnerNode == null)
                {
                    continue;
                }

                var owner = (AtkResNode*)renderer->OwnerNode;
                if (!owner->IsVisible())
                {
                    continue;
                }

                if (!InventoryGridLayout.TryGetNodeScreenRect(menu, owner, vpPos, out var min, out var max))
                {
                    continue;
                }

                if (mouse.X >= min.X && mouse.X <= max.X && mouse.Y >= min.Y && mouse.Y <= max.Y)
                {
                    hoveredIdx = i;
                    break;
                }
            }
        }

        if (hoveredIdx < 0 || hoveredIdx >= activeRowCount)
        {
            return false;
        }

        var matched = routes[hoveredIdx];
        if (string.IsNullOrWhiteSpace(matched))
        {
            return false;
        }

        routeFilter = matched;
        return true;
    }

    private void DrawGlamourListHighlights()
    {
        if (!glamourListHovered || !configuration.EnableHoverHighlights)
        {
            return;
        }

        var addon = NativeUiHelpers.GetReadyAddon(gameGui, "MiragePrismPrismBoxCrystallize");
        if (addon == null)
        {
            glamourListHovered = false;
            return;
        }

        var now = DateTime.UtcNow;
        if ((now - lastGlamourIdsUtc).TotalMilliseconds >= 300.0)
        {
            lastGlamourIdsUtc = now;
            cachedGlamourSlots = magicSort.GetGlamourStoreSlots();
            cachedGlamourNames = magicSort.GetGlamourStoreItemNames();
        }

        if (cachedGlamourSlots.Count == 0)
        {
            return;
        }

        var listNode = addon->GetNodeById(11);
        var list = listNode != null ? listNode->GetAsAtkComponentList() : null;
        if (list == null || list->ItemRendererList == null || list->ListLength <= 0 || list->AllocatedItemRendererListLength <= 0)
        {
            return;
        }

        var dl = ImGui.GetBackgroundDrawList();
        var viewport = ImGui.GetMainViewport();
        var vpPos = viewport.Pos;
        if (!InventoryGridLayout.TryGetNodeScreenRect(addon, listNode, vpPos, out var listMin, out var listMax))
        {
            return;
        }

        var occluders = GetFrameOccluders("MiragePrismPrismBoxCrystallize", addon, vpPos, viewport.Size);
        var col = RoutePresentation.ColorGlamour;
        var wash = ImGui.ColorConvertFloat4ToU32(new Vector4(col.X, col.Y, col.Z, 0.22f));
        var border = ImGui.ColorConvertFloat4ToU32(new Vector4(col.X, col.Y, col.Z, 0.78f));

        var agent = AgentMiragePrismPrismBox.Instance();
        var data = agent != null ? agent->Data : null;
        var crystallizeCount = data != null ? Math.Min((int)data->CrystallizeItemCount, data->CrystallizeItems.Length) : 0;

        var maxRenderers = Math.Min(list->ListLength, list->AllocatedItemRendererListLength);
        for (var i = 0; i < maxRenderers; i++)
        {
            var renderer = list->ItemRendererList[i].AtkComponentListItemRenderer;
            if (renderer == null || renderer->OwnerNode == null)
            {
                continue;
            }

            var owner = (AtkResNode*)renderer->OwnerNode;
            if (!owner->IsVisible())
            {
                continue;
            }

            var matched = false;
            var itemIdx = renderer->ListItemIndex;
            if (data != null && itemIdx >= 0 && itemIdx < crystallizeCount)
            {
                var ci = data->CrystallizeItems[itemIdx];
                matched = cachedGlamourSlots.Contains((ci.Inventory, ci.Slot));
            }
            else
            {
                var rowText = ReadFirstText(&renderer->AtkComponentButton.AtkComponentBase.UldManager);
                if (!string.IsNullOrEmpty(rowText))
                {
                    var cleanRow = NativeUiHelpers.NormalizeItemLabel(rowText);
                    foreach (var name in cachedGlamourNames)
                    {
                        if (string.Equals(cleanRow, name, StringComparison.OrdinalIgnoreCase))
                        {
                            matched = true;
                            break;
                        }
                    }
                }
            }

            if (!matched)
            {
                continue;
            }

            if (!InventoryGridLayout.TryGetNodeScreenRect(addon, owner, vpPos, out var min, out var max))
            {
                continue;
            }

            if (max.Y < listMin.Y || min.Y > listMax.Y)
            {
                continue;
            }

            min = new Vector2(min.X, MathF.Max(min.Y, listMin.Y));
            max = new Vector2(max.X, MathF.Min(max.Y, listMax.Y));
            if (IsOccludedByAny(min, max, occluders))
            {
                continue;
            }

            dl.AddRectFilled(min, max, wash, 3f);
            dl.AddRect(min, max, border, 3f, ImDrawFlags.None, 1.2f);
        }
    }

    private void ResetFrameCaches()
    {
        frameAddonCache.Clear();
        foreach (var list in frameOccluderCache.Values)
        {
            list.Clear();
            occluderListPool.Push(list);
        }

        frameOccluderCache.Clear();
    }

    private List<(Vector2 Min, Vector2 Max)> GetFrameOccluders(
        string addonName,
        AtkUnitBase* addon,
        Vector2 vpPos,
        Vector2 vpSize)
    {
        if (frameOccluderCache.TryGetValue(addonName, out var cached))
        {
            return cached;
        }

        var list = occluderListPool.Count > 0 ? occluderListPool.Pop() : new List<(Vector2, Vector2)>(8);
        InventoryGridLayout.CollectHigherZAddonRects(addon, vpPos, vpSize, list);
        frameOccluderCache[addonName] = list;
        return list;
    }

    private static bool IsOccludedByAny(Vector2 min, Vector2 max, List<(Vector2 Min, Vector2 Max)> occluders)
    {
        for (var i = 0; i < occluders.Count; i++)
        {
            var (bMin, bMax) = occluders[i];
            if (min.X < bMax.X && max.X > bMin.X && min.Y < bMax.Y && max.Y > bMin.Y)
            {
                return true;
            }
        }

        return false;
    }

    private static string ReadFirstText(AtkUldManager* uld)
    {
        if (uld == null || uld->NodeList == null)
        {
            return string.Empty;
        }

        for (var n = 0; n < uld->NodeListCount; n++)
        {
            var node = uld->NodeList[n];
            if (node == null || node->Type != NodeType.Text || !node->IsVisible())
            {
                continue;
            }

            var text = NativeUiHelpers.ReadTextNodeValue(node->GetAsAtkTextNode());
            if (!string.IsNullOrWhiteSpace(text) && text.Length > 2)
            {
                return text;
            }
        }

        return string.Empty;
    }

    private string GetFittedLabel(string label, float maxWidth, float fontSize)
    {
        var bucket = (int)MathF.Floor(maxWidth / 2f);
        var fontBucket = (int)MathF.Round(fontSize * 2f);
        var key = (label, bucket, fontBucket);
        if (fittedLabelCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var scale = fontSize / MathF.Max(1f, ImGui.GetFontSize());
        var result = label;
        if (ImGui.CalcTextSize(label).X * scale > maxWidth)
        {
            result = label[..1];
            for (var len = label.Length - 1; len >= 2; len--)
            {
                var candidate = label[..len];
                if (ImGui.CalcTextSize(candidate).X * scale <= maxWidth)
                {
                    result = candidate;
                    break;
                }
            }
        }

        if (fittedLabelCache.Count > 512)
        {
            fittedLabelCache.Clear();
        }

        fittedLabelCache[key] = result;
        return result;
    }

    public void Dispose()
    {
        hoveredInventorySortAddon = null;
        hoveredSellAddon = null;
        hoveredRetainerSortAddon = null;
        glamourListHovered = false;
        activeContextRouteFilters = null;
        activeContextSubmenuTitle = null;
        contextSubmenusByTitle.Clear();
        cachedBadges.Clear();
        fittedLabelCache.Clear();
        ResetFrameCaches();
        occluderListPool.Clear();
    }
}
