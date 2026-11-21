using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using MagicInventory.Core;
using MagicInventory.Services;

namespace MagicInventory.UI;

/// <summary>
/// Renders a crisp, non-intrusive X-ray destination preview (tiny badge + soft highlight)
/// over non-staying slots across open Player Bags, Armoury Chest, AND Retainer Grids while
/// hovering Magic Sort (in Inventory, Retainer UI, or Summoning Bell), plus Glamour Creation row highlights.
/// </summary>
public sealed unsafe class InventoryRouteOverlay : IDisposable
{
    private readonly record struct SlotBadge(string Label, Vector4 Color);

    private readonly IGameGui gameGui;
    private readonly IDataManager dataManager;
    private readonly Configuration configuration;
    private readonly InventorySafetyEvaluator safety;
    private readonly MagicSortService magicSort;

    private string? hoveredInventoryAddon;
    private DateTime lastCacheUtc = DateTime.MinValue;
    private readonly Dictionary<(string AddonName, uint NodeId), SlotBadge> cachedBadges = new();
    private readonly Dictionary<(string Label, int BucketWidth, int FontSizeBucket), string> fittedLabelCache = new();
    private readonly List<InventoryGridLayout.VisualGridSlot> visualSlotScratch = new(320);

    private bool glamourListHovered;
    private DateTime lastGlamourNamesUtc = DateTime.MinValue;
    private IReadOnlyList<string> cachedGlamourNames = Array.Empty<string>();

    public InventoryRouteOverlay(
        IGameGui gameGui,
        IDataManager dataManager,
        Configuration configuration,
        InventorySafetyEvaluator safety,
        MagicSortService magicSort)
    {
        this.gameGui = gameGui;
        this.dataManager = dataManager;
        this.configuration = configuration;
        this.safety = safety;
        this.magicSort = magicSort;
    }

    public void SetGlamourListHover(bool isHovered)
    {
        glamourListHovered = isHovered;
        lastGlamourNamesUtc = DateTime.MinValue;
    }

    public void SetHoveredInventorySortAddon(string addonName, bool isHovered)
    {
        if (isHovered)
        {
            if (!string.Equals(hoveredInventoryAddon, addonName, StringComparison.Ordinal))
            {
                hoveredInventoryAddon = addonName;
                lastCacheUtc = DateTime.MinValue;
            }
        }
        else if (string.Equals(hoveredInventoryAddon, addonName, StringComparison.Ordinal))
        {
            hoveredInventoryAddon = null;
            cachedBadges.Clear();
        }
    }

    public void Draw()
    {
        DrawGlamourListHighlights();

        if (hoveredInventoryAddon == null || !configuration.EnableHoverHighlights)
        {
            return;
        }

        if (!NativeUiHelpers.IsAddonOpenAndReady(gameGui, hoveredInventoryAddon))
        {
            hoveredInventoryAddon = null;
            cachedBadges.Clear();
            return;
        }

        var now = DateTime.UtcNow;
        if ((now - lastCacheUtc).TotalMilliseconds >= 200.0)
        {
            RebuildBadges();
            lastCacheUtc = now;
        }

        if (cachedBadges.Count == 0)
        {
            return;
        }

        // Draw on BackgroundDrawList (below ImGui overlays/settings) and skip slots occluded by native Tooltip/ItemDetail
        var dl = ImGui.GetBackgroundDrawList();
        var vpPos = ImGui.GetMainViewport().Pos;
        var font = ImGui.GetFont();
        var fontSize = MathF.Max(10f, ImGui.GetFontSize() * 0.72f);

        var hasTooltipRect = TryGetAddonScreenRect("Tooltip", vpPos, out var tipMin, out var tipMax);
        var hasItemDetailRect = TryGetAddonScreenRect("ItemDetail", vpPos, out var detailMin, out var detailMax);

        // Cache Addon* lookups per frame (at most ~10 grid addons instead of ~190 lookups/frame)
        var addonCache = new Dictionary<string, nint>(12, StringComparer.Ordinal);

        foreach (var ((addonName, nodeId), badge) in cachedBadges)
        {
            if (!addonCache.TryGetValue(addonName, out var addonAddr))
            {
                var ready = NativeUiHelpers.GetReadyAddon(gameGui, addonName);
                addonAddr = (nint)ready;
                addonCache[addonName] = addonAddr;
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

            if ((hasTooltipRect && RectsOverlap(min, max, tipMin, tipMax))
                || (hasItemDetailRect && RectsOverlap(min, max, detailMin, detailMax)))
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
            var borderCol = ImGui.ColorConvertFloat4ToU32(new Vector4(badge.Color.X, badge.Color.Y, badge.Color.Z, 0.88f));
            dl.AddRectFilled(insetMin, insetMax, washCol, 3f);
            dl.AddRect(insetMin, insetMax, borderCol, 3f, ImDrawFlags.None, 1.3f);

            var pillHeight = MathF.Min(fontSize + 3f, height * 0.42f);
            var pillMin = new Vector2(insetMin.X, insetMax.Y - pillHeight);
            var pillMax = insetMax;
            var pillBg = ImGui.ColorConvertFloat4ToU32(new Vector4(0.05f, 0.06f, 0.08f, 0.88f));
            dl.AddRectFilled(pillMin, pillMax, pillBg, 2f);

            var fitted = GetFittedLabel(badge.Label, width - 5f, fontSize);
            var textSize = ImGui.CalcTextSize(fitted) * (fontSize / MathF.Max(1f, ImGui.GetFontSize()));
            var textX = pillMin.X + MathF.Max(1.5f, ((pillMax.X - pillMin.X) - textSize.X) * 0.5f);
            var textY = pillMin.Y + MathF.Max(0f, (pillHeight - textSize.Y) * 0.5f);

            var textCol = ImGui.ColorConvertFloat4ToU32(badge.Color);
            dl.AddText(font, fontSize, new Vector2(textX, textY), textCol, fitted);
        }
    }

    private void RebuildBadges()
    {
        cachedBadges.Clear();
        var manager = InventoryManager.Instance();
        var entries = magicSort.GetLatestManifest();
        if (manager == null || entries.Count == 0)
        {
            return;
        }

        var gearsetIds = safety.BuildGearsetItemIds();
        var unlockedTop = safety.BuildUnlockedJobTopIlvl();
        var activeRetainerId = HoldingsService.GetActiveRetainerId();
        var isRetainerSortHover = string.Equals(hoveredInventoryAddon, "InventoryRetainerLarge", StringComparison.Ordinal)
            || string.Equals(hoveredInventoryAddon, "InventoryRetainer", StringComparison.Ordinal);

        HashSet<(InventoryType Container, int Slot)>? retainerActiveSlots = null;
        if (isRetainerSortHover && activeRetainerId != 0)
        {
            magicSort.GetActiveRetainerSortHighlightSlots(hoveredInventoryAddon, out var depSlots, out var wdSlots);
            retainerActiveSlots = new HashSet<(InventoryType, int)>(depSlots.Count + wdSlots.Count);
            retainerActiveSlots.UnionWith(depSlots);
            retainerActiveSlots.UnionWith(wdSlots);
        }

        visualSlotScratch.Clear();
        InventoryGridLayout.CollectVisiblePlayerBagSlots(gameGui, visualSlotScratch);
        InventoryGridLayout.CollectVisibleArmourySlots(gameGui, visualSlotScratch);
        if (activeRetainerId != 0)
        {
            InventoryGridLayout.CollectVisibleRetainerSlots(gameGui, visualSlotScratch);
        }

        var resolver = magicSort.SlotResolver;
        foreach (var vs in visualSlotScratch)
        {
            // When hovering a Retainer's Magic Sort button, only badge items actually participating in THIS retainer's sort!
            if (retainerActiveSlots != null && !retainerActiveSlots.Contains((vs.Container, vs.PhysicalSlot)))
            {
                continue;
            }
            var slot = InventorySafetyEvaluator.GetSlot(manager, vs.Container, vs.PhysicalSlot);
            if (slot == null || slot->ItemId == 0 || slot->Quantity <= 0)
            {
                continue;
            }

            var inRetainer = InventorySafetyEvaluator.IsRetainerContainer(vs.Container);
            var inArmoury = InventorySafetyEvaluator.IsArmouryContainer(vs.Container);

            var resolved = resolver.ResolveSlot(
                slot->ItemId,
                vs.Container,
                slot,
                entries,
                gearsetIds,
                unlockedTop,
                includeBagToArmouryRoute: !inRetainer && !inArmoury);
            if (resolved is null)
            {
                continue;
            }

            var r = resolved.Value;
            // Inside a retainer, badge items that will actually LEAVE this retainer
            // (tracked pulls, explicit manual Keep->Bag pulls, sell/use/glamour/armoire routes, or moves to a DIFFERENT retainer)
            if (inRetainer)
            {
                if (r.IsPendingPrice || r.NeedsRetainerVisit)
                {
                    continue;
                }

                if (string.Equals(r.Route, "Retainer", StringComparison.OrdinalIgnoreCase)
                    && (r.TargetRetainerId == 0 || r.TargetRetainerId == activeRetainerId))
                {
                    continue;
                }
            }
            else if (string.Equals(r.Route, "Keep", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var shortLabel = inRetainer && string.Equals(r.Route, "Keep", StringComparison.OrdinalIgnoreCase)
                ? "Keep"
                : RoutePresentation.GetShortBadgeLabel(r.Route, r.TargetRetainerName);
            if (string.IsNullOrEmpty(shortLabel))
            {
                continue;
            }

            var badgeColor = inRetainer && string.Equals(r.Route, "Keep", StringComparison.OrdinalIgnoreCase)
                ? RoutePresentation.ColorTracked
                : RoutePresentation.GetRouteColor(r.Route);
            cachedBadges[(vs.AddonName, vs.NodeId)] = new SlotBadge(shortLabel, badgeColor);
        }
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
        if ((now - lastGlamourNamesUtc).TotalMilliseconds >= 300.0)
        {
            lastGlamourNamesUtc = now;
            cachedGlamourNames = magicSort.GetGlamourStoreItemNames();
        }

        if (cachedGlamourNames.Count == 0)
        {
            return;
        }

        var listNode = addon->GetNodeById(11);
        var compNode = listNode != null ? listNode->GetAsAtkComponentNode() : null;
        var list = compNode != null ? (AtkComponentList*)compNode->Component : null;
        if (list == null || list->ItemRendererList == null || list->ListLength <= 0)
        {
            return;
        }

        var dl = ImGui.GetBackgroundDrawList();
        var vpPos = ImGui.GetMainViewport().Pos;
        if (!InventoryGridLayout.TryGetNodeScreenRect(addon, listNode, vpPos, out var listMin, out var listMax))
        {
            return;
        }

        var col = RoutePresentation.ColorGlamour;
        var wash = ImGui.ColorConvertFloat4ToU32(new Vector4(col.X, col.Y, col.Z, 0.22f));
        var border = ImGui.ColorConvertFloat4ToU32(new Vector4(col.X, col.Y, col.Z, 0.75f));

        for (var i = 0; i < list->ListLength && i < list->AllocatedItemRendererListLength; i++)
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

            var rowText = ReadFirstText(&renderer->AtkComponentButton.AtkComponentBase.UldManager);
            if (string.IsNullOrEmpty(rowText))
            {
                continue;
            }

            var cleanRow = NativeUiHelpers.NormalizeItemLabel(rowText);
            var matched = false;
            foreach (var name in cachedGlamourNames)
            {
                if (string.Equals(cleanRow, name, StringComparison.OrdinalIgnoreCase))
                {
                    matched = true;
                    break;
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
            dl.AddRectFilled(min, max, wash, 3f);
            dl.AddRect(min, max, border, 3f, ImDrawFlags.None, 1.2f);
        }
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

    private bool TryGetAddonScreenRect(string addonName, Vector2 vpPos, out Vector2 min, out Vector2 max)
    {
        min = Vector2.Zero;
        max = Vector2.Zero;
        var addon = NativeUiHelpers.GetReadyAddon(gameGui, addonName);
        if (addon == null || addon->RootNode == null || !addon->IsVisible)
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

    private static bool RectsOverlap(Vector2 aMin, Vector2 aMax, Vector2 bMin, Vector2 bMax)
        => aMin.X < bMax.X && aMax.X > bMin.X && aMin.Y < bMax.Y && aMax.Y > bMin.Y;

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
        hoveredInventoryAddon = null;
        glamourListHovered = false;
        cachedBadges.Clear();
        fittedLabelCache.Clear();
    }
}
