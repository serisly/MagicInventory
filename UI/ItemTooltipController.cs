using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using MagicInventory.Core;
using MagicInventory.Services;

namespace MagicInventory.UI;

public sealed unsafe class ItemTooltipController : IDisposable
{
    private const int DescriptionStringIndex = 13;
    private const int EffectsStringIndex = 16;
    private const int LevelsStringIndex = 23;
    private const int FlagIntIndex = 5;

    private const int VisibilityDescriptionBit = 0x2;
    private const int VisibilityEffectsBit = 0x100;
    private const int VisibilityLevelsBit = 0x8000;

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate byte OnItemHoveredDelegate(
        AgentItemDetail* agentItemDetail,
        InventoryItem** outItem,
        InventoryType* container,
        short* slot,
        uint itemId,
        uint rowIdOrIndex,
        InventoryItem* item);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void GenerateItemTooltipDelegate(
        AddonItemDetail* addon,
        NumberArrayData* numberArrayData,
        StringArrayData* stringArrayData);

    private readonly IGameInteropProvider gameInteropProvider;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;
    private readonly MagicSortService magicSort;

    private Hook<OnItemHoveredDelegate>? onItemHoveredHook;
    private Hook<GenerateItemTooltipDelegate>? generateItemTooltipHook;
    private bool isHoverSubscribed;

    private uint hoveredRawItemId;
    private InventoryType? hoveredContainer;
    private short? hoveredSlot;

    public ItemTooltipController(
        IGameInteropProvider gameInteropProvider,
        IGameGui gameGui,
        IPluginLog log,
        MagicSortService magicSort)
    {
        this.gameInteropProvider = gameInteropProvider;
        this.gameGui = gameGui;
        this.log = log;
        this.magicSort = magicSort;
    }

    public void Enable()
    {
        try
        {
            if (!isHoverSubscribed)
            {
                gameGui.HoveredItemChanged += OnHoveredItemChanged;
                isHoverSubscribed = true;
            }

            onItemHoveredHook ??= gameInteropProvider.HookFromAddress<OnItemHoveredDelegate>(
                (nint)AgentItemDetail.MemberFunctionPointers.OnItemHovered,
                OnItemHoveredDetour);
            onItemHoveredHook.Enable();

            generateItemTooltipHook ??= gameInteropProvider.HookFromAddress<GenerateItemTooltipDelegate>(
                (nint)AddonItemDetail.MemberFunctionPointers.GenerateItemTooltip,
                GenerateItemTooltipDetour);
            generateItemTooltipHook.Enable();
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to initialize Magic Sort hover tooltip hooks.");
        }
    }

    private void OnHoveredItemChanged(object? sender, ulong itemId)
    {
        if (!ItemIdentity.IsValidItemId(itemId))
        {
            hoveredRawItemId = 0;
            hoveredContainer = null;
            hoveredSlot = null;
            return;
        }

        if (hoveredRawItemId != 0 && (uint)itemId != hoveredRawItemId)
        {
            hoveredRawItemId = 0;
            hoveredContainer = null;
            hoveredSlot = null;
        }
    }

    private byte OnItemHoveredDetour(
        AgentItemDetail* agentItemDetail,
        InventoryItem** outItem,
        InventoryType* container,
        short* slot,
        uint itemId,
        uint rowIdOrIndex,
        InventoryItem* item)
    {
        try
        {
            var preItem = item != null ? item : (outItem != null ? *outItem : null);
            var hasSlot = container != null && slot != null && *slot >= 0;
            hoveredContainer = hasSlot ? *container : null;
            hoveredSlot = hasSlot ? *slot : null;
            hoveredRawItemId = itemId != 0
                ? itemId
                : (preItem != null ? preItem->ItemId : 0u);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Failed to capture hovered item metadata before tooltip generation.");
        }

        var result = onItemHoveredHook!.Original(agentItemDetail, outItem, container, slot, itemId, rowIdOrIndex, item);

        try
        {
            var postItem = item != null ? item : (outItem != null ? *outItem : null);
            if (postItem != null && postItem->ItemId != 0)
            {
                if (hoveredRawItemId == 0)
                {
                    hoveredRawItemId = postItem->ItemId;
                }

                // Prefer the live InventoryItem's physical Container/Slot over visual UI tab codes (48..51).
                if (postItem->Container != InventoryType.Invalid && postItem->Slot >= 0)
                {
                    hoveredContainer = postItem->Container;
                    hoveredSlot = postItem->Slot;
                }
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Failed to refresh hovered item metadata.");
        }

        return result;
    }

    private static bool IsOwnedInventoryTooltipKind(DetailKind kind)
        => kind is DetailKind.InventoryItem or DetailKind.InventoryContext;

    private bool IsCursorOverAnyAddon(ReadOnlySpan<string> addonNames)
    {
        var mouse = ImGui.GetMousePos();
        var vpPos = ImGui.GetMainViewport().Pos;
        foreach (var name in addonNames)
        {
            var addon = NativeUiHelpers.GetReadyAddon(gameGui, name);
            if (addon == null || addon->RootNode == null || !addon->IsVisible)
            {
                continue;
            }

            var scale = addon->Scale > 0.01f ? addon->Scale : 1f;
            var x = vpPos.X + addon->X;
            var y = vpPos.Y + addon->Y;
            var w = addon->RootNode->Width * scale;
            var h = addon->RootNode->Height * scale;
            if (mouse.X >= x && mouse.X <= x + w && mouse.Y >= y && mouse.Y <= y + h)
            {
                return true;
            }
        }

        return false;
    }

    private void GenerateItemTooltipDetour(
        AddonItemDetail* addon,
        NumberArrayData* numberArrayData,
        StringArrayData* stringArrayData)
    {
        try
        {
            var agent = AgentItemDetail.Instance();
            // Strict allowlist: ONLY annotate owned inventory/armoury/retainer tooltips (never chat links, MB, shops, recipes, quests, glamour plates)
            var skipTooltip = agent == null || !IsOwnedInventoryTooltipKind(agent->DetailKind);

            if (!skipTooltip
                && stringArrayData != null
                && stringArrayData->StringArray != null
                && stringArrayData->Size > DescriptionStringIndex)
            {
                uint rawId = 0;
                if (agent != null && ItemIdentity.IsValidItemId(agent->ItemId))
                {
                    rawId = agent->ItemId;
                }
                else if (ItemIdentity.IsValidItemId(gameGui.HoveredItem))
                {
                    rawId = (uint)gameGui.HoveredItem;
                }
                else if (hoveredRawItemId != 0)
                {
                    rawId = hoveredRawItemId;
                }

                InventoryType? effectiveContainer = null;
                short? effectiveSlot = null;
                InventoryItem* liveItem = null;

                var targetIsHq = rawId >= ItemIdentity.HighQualityIdOffset && rawId < ItemIdentity.EventItemIdThreshold;
                var targetIsCollectable = rawId >= ItemIdentity.CollectableIdOffset && rawId < ItemIdentity.HighQualityIdOffset;

                var canAttachHoveredSlot = hoveredContainer.HasValue
                    && hoveredSlot.HasValue
                    && hoveredSlot.Value >= 0
                    && hoveredRawItemId != 0
                    && (rawId == 0 || hoveredRawItemId == rawId);

                if (canAttachHoveredSlot)
                {
                    effectiveContainer = hoveredContainer;
                    effectiveSlot = hoveredSlot;
                }
                else if (agent != null && agent->Index < 250)
                {
                    effectiveContainer = (InventoryType)agent->TypeOrId;
                    effectiveSlot = (short)agent->Index;
                }

                InventoryGridLayout.NormalizeVisualBagSlot(ref effectiveContainer, ref effectiveSlot);

                if (effectiveContainer.HasValue && effectiveSlot.HasValue && effectiveSlot.Value >= 0)
                {
                    var manager = InventoryManager.Instance();
                    var container = manager != null ? manager->GetInventoryContainer(effectiveContainer.Value) : null;
                    if (container != null && container->Items != null && effectiveSlot.Value < container->Size)
                    {
                        var candidate = container->GetInventorySlot(effectiveSlot.Value);
                        if (candidate != null && candidate->ItemId != 0)
                        {
                            if (rawId == 0)
                            {
                                rawId = candidate->ItemId;
                                targetIsHq = ItemIdentity.IsHighQuality(candidate);
                                targetIsCollectable = ItemIdentity.IsCollectable(candidate);
                            }

                            if (ItemIdentity.NormalizeBaseItemId(candidate->ItemId) == ItemIdentity.NormalizeBaseItemId(rawId)
                                && ItemIdentity.IsHighQuality(candidate) == targetIsHq
                                && ItemIdentity.IsCollectable(candidate) == targetIsCollectable)
                            {
                                liveItem = candidate;
                            }
                        }
                    }
                }

                // Fallback when hoveredContainer didn't bind a live InventoryItem*:
                // Hit-test cursor against Retainer / Armoury / Bags first, and require exact (BaseId + HQ + Collectable) match!
                if (liveItem == null && rawId != 0)
                {
                    var targetBase = ItemIdentity.NormalizeBaseItemId(rawId);
                    var manager = InventoryManager.Instance();
                    if (manager != null && targetBase != 0)
                    {
                        InventoryItem* TryFindInContainer(InventoryType cType, out short foundSlot)
                        {
                            foundSlot = -1;
                            var c = manager->GetInventoryContainer(cType);
                            if (!Core.InventorySafetyEvaluator.IsContainerReadable(c))
                            {
                                return null;
                            }

                            for (var i = 0; i < c->Size; i++)
                            {
                                var s = c->GetInventorySlot(i);
                                if (s == null || s->ItemId == 0 || s->Quantity <= 0)
                                {
                                    continue;
                                }

                                if (ItemIdentity.NormalizeBaseItemId(s->ItemId) == targetBase
                                    && ItemIdentity.IsHighQuality(s) == targetIsHq
                                    && ItemIdentity.IsCollectable(s) == targetIsCollectable)
                                {
                                    foundSlot = (short)i;
                                    return s;
                                }
                            }

                            return null;
                        }

                        bool TryScanRetainer()
                        {
                            foreach (var retType in Core.InventorySafetyEvaluator.RetainerBagContainers)
                            {
                                var hit = TryFindInContainer(retType, out var hitSlot);
                                if (hit != null)
                                {
                                    effectiveContainer = retType;
                                    effectiveSlot = hitSlot;
                                    liveItem = hit;
                                    return true;
                                }
                            }

                            return false;
                        }

                        bool TryScanBags()
                        {
                            foreach (var bagType in Core.InventorySafetyEvaluator.PlayerBagContainers)
                            {
                                var hit = TryFindInContainer(bagType, out var hitSlot);
                                if (hit != null)
                                {
                                    effectiveContainer = bagType;
                                    effectiveSlot = hitSlot;
                                    liveItem = hit;
                                    return true;
                                }
                            }

                            return false;
                        }

                        bool TryScanArmoury()
                        {
                            var activeTabContainer = InventoryGridLayout.GetActiveArmouryTabContainer(gameGui);
                            if (activeTabContainer.HasValue)
                            {
                                var hit = TryFindInContainer(activeTabContainer.Value, out var hitSlot);
                                if (hit != null)
                                {
                                    effectiveContainer = activeTabContainer.Value;
                                    effectiveSlot = hitSlot;
                                    liveItem = hit;
                                    return true;
                                }
                            }

                            foreach (var armType in Core.InventorySafetyEvaluator.ArmouryGearContainers)
                            {
                                var hit = TryFindInContainer(armType, out var hitSlot);
                                if (hit != null)
                                {
                                    effectiveContainer = armType;
                                    effectiveSlot = hitSlot;
                                    liveItem = hit;
                                    return true;
                                }
                            }

                            var scHit = TryFindInContainer(InventoryType.ArmorySoulCrystal, out var scSlot);
                            if (scHit != null)
                            {
                                effectiveContainer = InventoryType.ArmorySoulCrystal;
                                effectiveSlot = scSlot;
                                liveItem = scHit;
                                return true;
                            }

                            return false;
                        }

                        var retainerOpen = HoldingsService.GetActiveRetainerId() != 0
                            && (NativeUiHelpers.IsAddonOpenAndReady(gameGui, "InventoryRetainerLarge")
                                || NativeUiHelpers.IsAddonOpenAndReady(gameGui, "InventoryRetainer"));
                        var overRetainer = retainerOpen && IsCursorOverAnyAddon(["InventoryRetainerLarge", "InventoryRetainer", "RetainerGrid0", "RetainerGrid1", "RetainerGrid2", "RetainerGrid3", "RetainerGrid4", "RetainerGrid"]);
                        var overArmoury = IsCursorOverAnyAddon(["ArmouryBoard"]);

                        if (overRetainer)
                        {
                            if (!TryScanRetainer() && !TryScanBags())
                            {
                                TryScanArmoury();
                            }
                        }
                        else if (overArmoury)
                        {
                            if (!TryScanArmoury() && !TryScanBags() && retainerOpen)
                            {
                                TryScanRetainer();
                            }
                        }
                        else
                        {
                            if (!TryScanBags() && (!retainerOpen || !TryScanRetainer()))
                            {
                                TryScanArmoury();
                            }
                        }
                    }
                }

                var label = magicSort.GetHoverSortLabel(rawId, effectiveContainer, effectiveSlot, liveItem);
                if (!string.IsNullOrEmpty(label)
                    && numberArrayData != null
                    && numberArrayData->IntArray != null
                    && numberArrayData->Size > FlagIntIndex)
                {
                    var visibilityFlags = numberArrayData->IntArray[FlagIntIndex];
                    var hasDescriptionText = stringArrayData->Size > DescriptionStringIndex
                        && stringArrayData->StringArray[DescriptionStringIndex].Value != null
                        && stringArrayData->StringArray[DescriptionStringIndex].Value[0] != 0;
                    int targetIndex;
                    var prepend = true;

                    if ((visibilityFlags & VisibilityDescriptionBit) != 0 || hasDescriptionText)
                    {
                        targetIndex = DescriptionStringIndex;
                        numberArrayData->IntArray[FlagIntIndex] |= VisibilityDescriptionBit;
                    }
                    else if ((visibilityFlags & VisibilityEffectsBit) != 0)
                    {
                        targetIndex = EffectsStringIndex;
                    }
                    else if ((visibilityFlags & VisibilityLevelsBit) != 0 && stringArrayData->Size > LevelsStringIndex)
                    {
                        targetIndex = LevelsStringIndex;
                        prepend = false;
                    }
                    else
                    {
                        targetIndex = DescriptionStringIndex;
                        numberArrayData->IntArray[FlagIntIndex] |= VisibilityDescriptionBit;
                    }

                    if (stringArrayData->Size > targetIndex)
                    {
                        var existingPtr = stringArrayData->StringArray[targetIndex].Value;
                        var existingSe = existingPtr != null
                            ? MemoryHelper.ReadSeStringNullTerminated((nint)existingPtr)
                            : new SeString();
                        var existingText = existingSe.TextValue;

                        if (!existingText.Contains("Magic Sort:", StringComparison.Ordinal))
                        {
                            var builder = new SeStringBuilder();
                            var hasExisting = existingSe.Payloads.Count > 0 && !string.IsNullOrEmpty(existingText);

                            if (prepend)
                            {
                                builder.AddUiForeground(31);
                                builder.AddText(label);
                                builder.AddUiForegroundOff();
                                if (hasExisting)
                                {
                                    builder.AddText("\n");
                                    builder.Append(existingSe);
                                }
                            }
                            else
                            {
                                if (hasExisting)
                                {
                                    builder.Append(existingSe);
                                    builder.AddText("\n");
                                }

                                builder.AddUiForeground(31);
                                builder.AddText(label);
                                builder.AddUiForegroundOff();
                            }

                            var encoded = builder.Build().EncodeWithNullTerminator();
                            stringArrayData->SetValue(
                                targetIndex,
                                encoded,
                                readBeforeWrite: false,
                                managed: true,
                                suppressUpdates: true);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Skipped Magic Sort tooltip annotation.");
        }

        generateItemTooltipHook!.Original(addon, numberArrayData, stringArrayData);
    }

    public void Dispose()
    {
        if (isHoverSubscribed)
        {
            gameGui.HoveredItemChanged -= OnHoveredItemChanged;
            isHoverSubscribed = false;
        }

        onItemHoveredHook?.Dispose();
        onItemHoveredHook = null;

        generateItemTooltipHook?.Dispose();
        generateItemTooltipHook = null;
    }
}
