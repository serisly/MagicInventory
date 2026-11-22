using System;
using System.Runtime.InteropServices;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
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

    private void GenerateItemTooltipDetour(
        AddonItemDetail* addon,
        NumberArrayData* numberArrayData,
        StringArrayData* stringArrayData)
    {
        try
        {
            var agent = AgentItemDetail.Instance();
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
                    && (rawId == 0
                        || ItemIdentity.NormalizeBaseItemId(hoveredRawItemId) == ItemIdentity.NormalizeBaseItemId(rawId));

                if (canAttachHoveredSlot)
                {
                    effectiveContainer = hoveredContainer;
                    effectiveSlot = hoveredSlot;
                }

                if (!effectiveContainer.HasValue && agent != null && agent->DetailKind == DetailKind.InventoryItem && agent->Index < 250)
                {
                    effectiveContainer = (InventoryType)agent->TypeOrId;
                    effectiveSlot = (short)agent->Index;
                }

                liveItem = TryBindExactLiveSlot(ref effectiveContainer, ref effectiveSlot, ref rawId, ref targetIsHq, ref targetIsCollectable);

                if (liveItem == null
                    && canAttachHoveredSlot
                    && agent != null
                    && agent->DetailKind == DetailKind.InventoryItem
                    && agent->Index < 250)
                {
                    effectiveContainer = (InventoryType)agent->TypeOrId;
                    effectiveSlot = (short)agent->Index;
                    liveItem = TryBindExactLiveSlot(ref effectiveContainer, ref effectiveSlot, ref rawId, ref targetIsHq, ref targetIsCollectable);
                }

                if (liveItem == null || !effectiveContainer.HasValue || rawId == 0)
                {
                    generateItemTooltipHook!.Original(addon, numberArrayData, stringArrayData);
                    return;
                }

                var label = magicSort.GetHoverSortLabel(rawId, effectiveContainer, liveItem);
                if (!string.IsNullOrEmpty(label)
                    && numberArrayData != null
                    && numberArrayData->IntArray != null
                    && numberArrayData->Size > FlagIntIndex)
                {
                    var visibilityFlags = numberArrayData->IntArray[FlagIntIndex];
                    int targetIndex;
                    var prepend = true;
                    var overwriteFreshField = false;

                    if ((visibilityFlags & VisibilityDescriptionBit) != 0)
                    {
                        targetIndex = DescriptionStringIndex;
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
                        overwriteFreshField = true;
                    }

                    if (stringArrayData->Size > targetIndex)
                    {
                        if (overwriteFreshField)
                        {
                            var builder = new SeStringBuilder();
                            builder.AddUiForeground(31);
                            builder.AddText(label);
                            builder.AddUiForegroundOff();

                            var encoded = builder.Build().EncodeWithNullTerminator();
                            stringArrayData->SetValue(
                                targetIndex,
                                encoded,
                                readBeforeWrite: false,
                                managed: true,
                                suppressUpdates: true);
                        }
                        else
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
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Skipped Magic Sort tooltip annotation.");
        }

        generateItemTooltipHook!.Original(addon, numberArrayData, stringArrayData);
    }

    private static InventoryItem* TryBindExactLiveSlot(
        ref InventoryType? effectiveContainer,
        ref short? effectiveSlot,
        ref uint rawId,
        ref bool targetIsHq,
        ref bool targetIsCollectable)
    {
        InventoryGridLayout.NormalizeVisualBagSlot(ref effectiveContainer, ref effectiveSlot);
        if (!effectiveContainer.HasValue || !effectiveSlot.HasValue || effectiveSlot.Value < 0)
        {
            return null;
        }

        var manager = InventoryManager.Instance();
        var container = manager != null ? manager->GetInventoryContainer(effectiveContainer.Value) : null;
        if (container == null || container->Items == null || effectiveSlot.Value >= container->Size)
        {
            return null;
        }

        var candidate = container->GetInventorySlot(effectiveSlot.Value);
        if (candidate == null || candidate->ItemId == 0 || candidate->Quantity <= 0)
        {
            return null;
        }

        if (rawId == 0)
        {
            rawId = candidate->ItemId;
            targetIsHq = ItemIdentity.IsHighQuality(candidate);
            targetIsCollectable = ItemIdentity.IsCollectable(candidate);
            return candidate;
        }

        if (ItemIdentity.NormalizeBaseItemId(candidate->ItemId) == ItemIdentity.NormalizeBaseItemId(rawId)
            && ItemIdentity.IsHighQuality(candidate) == targetIsHq
            && ItemIdentity.IsCollectable(candidate) == targetIsCollectable)
        {
            return candidate;
        }

        return null;
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
