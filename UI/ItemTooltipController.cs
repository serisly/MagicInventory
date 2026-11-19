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
        if (itemId == 0 || itemId >= 2_000_000)
        {
            hoveredRawItemId = 0;
            hoveredContainer = null;
            hoveredSlot = null;
            return;
        }

        if (hoveredRawItemId != 0
            && HoldingsService.NormalizeBaseItemId((uint)itemId) != HoldingsService.NormalizeBaseItemId(hoveredRawItemId))
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

    private static bool IsNonOwnedTooltipKind(DetailKind kind)
    {
        if (kind >= DetailKind.Action)
        {
            return true;
        }

        return kind is DetailKind.ItemSearchResult
            or DetailKind.ShopBuyback
            or DetailKind.ShopItem
            or DetailKind.KeyItem
            or DetailKind.ItemId
            or DetailKind.MiragePrismPlateItem
            or DetailKind.MiragePrismBoxItemSetPreview
            or DetailKind.MerchantSetting
            or DetailKind.MJIKeyItem
            or DetailKind.Cabinet
            or DetailKind.TradeRemote
            or DetailKind.LetterView;
    }

    /// <summary>
    /// AgentItemDetail.TypeOrId reports visual UI bag tab codes 48..51 for Inventory1..4
    /// (ordered by ItemOrderModule.InventorySorter). Map those back to physical InventoryType + Slot.
    /// </summary>
    private static void NormalizeVisualBagSlot(ref InventoryType? container, ref short? slot)
    {
        if (!container.HasValue || !slot.HasValue || slot.Value < 0)
        {
            return;
        }

        var rawType = (int)container.Value;
        if (rawType is < 48 or > 51)
        {
            return;
        }

        var visualTab = rawType - 48;
        var visualSlot = (int)slot.Value;
        var orderModule = ItemOrderModule.Instance();
        var sorter = orderModule != null ? orderModule->InventorySorter : null;
        if (sorter != null && sorter->Items.Count > 0)
        {
            var perPage = sorter->ItemsPerPage > 0 ? sorter->ItemsPerPage : 35;
            var visualIndex = visualTab * perPage + visualSlot;
            if (visualIndex >= 0 && visualIndex < sorter->Items.Count)
            {
                var entryPtr = sorter->Items[visualIndex].Value;
                if (entryPtr != null)
                {
                    container = entryPtr->Page switch
                    {
                        0 => InventoryType.Inventory1,
                        1 => InventoryType.Inventory2,
                        2 => InventoryType.Inventory3,
                        3 => InventoryType.Inventory4,
                        _ => container,
                    };
                    slot = (short)entryPtr->Slot;
                    return;
                }
            }
        }

        container = visualTab switch
        {
            0 => InventoryType.Inventory1,
            1 => InventoryType.Inventory2,
            2 => InventoryType.Inventory3,
            3 => InventoryType.Inventory4,
            _ => container,
        };
    }

    private void GenerateItemTooltipDetour(
        AddonItemDetail* addon,
        NumberArrayData* numberArrayData,
        StringArrayData* stringArrayData)
    {
        try
        {
            var agent = AgentItemDetail.Instance();
            var skipTooltip = agent != null && IsNonOwnedTooltipKind(agent->DetailKind);

            if (!skipTooltip
                && stringArrayData != null
                && stringArrayData->StringArray != null
                && stringArrayData->Size > DescriptionStringIndex)
            {
                uint rawId = 0;
                if (agent != null && agent->ItemId is > 0 and < 2_000_000)
                {
                    rawId = agent->ItemId;
                }
                else if (gameGui.HoveredItem is > 0 and < 2_000_000)
                {
                    rawId = (uint)gameGui.HoveredItem;
                }
                else if (hoveredRawItemId != 0
                    && (agent == null || agent->DetailKind is DetailKind.InventoryItem or DetailKind.InventoryContext))
                {
                    rawId = hoveredRawItemId;
                }

                InventoryType? effectiveContainer = null;
                short? effectiveSlot = null;
                InventoryItem* liveItem = null;

                var canAttachHoveredSlot = hoveredContainer.HasValue
                    && hoveredSlot.HasValue
                    && hoveredSlot.Value >= 0
                    && hoveredRawItemId != 0
                    && (rawId == 0 || HoldingsService.NormalizeBaseItemId(hoveredRawItemId) == HoldingsService.NormalizeBaseItemId(rawId));

                if (canAttachHoveredSlot)
                {
                    effectiveContainer = hoveredContainer;
                    effectiveSlot = hoveredSlot;
                }
                else if (agent != null
                    && (agent->DetailKind is DetailKind.InventoryItem or DetailKind.InventoryContext)
                    && agent->Index < 250)
                {
                    effectiveContainer = (InventoryType)agent->TypeOrId;
                    effectiveSlot = (short)agent->Index;
                }

                NormalizeVisualBagSlot(ref effectiveContainer, ref effectiveSlot);

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
                            }

                            if (HoldingsService.NormalizeBaseItemId(candidate->ItemId)
                                == HoldingsService.NormalizeBaseItemId(rawId))
                            {
                                liveItem = candidate;
                            }
                        }
                    }
                }

                // Fallback for ArmouryBoard (where AgentItemDetail may report a visual container ID or clear hoveredContainer):
                // if we still didn't bind a physical InventoryItem* for this hovered item, resolve it from Armoury Chest.
                if (liveItem == null && rawId != 0)
                {
                    var targetBase = HoldingsService.NormalizeBaseItemId(rawId);
                    var manager = InventoryManager.Instance();
                    if (manager != null)
                    {
                        var armouryOpen = NativeUiHelpers.IsAddonOpenAndReady(gameGui, "ArmouryBoard");
                        var inBags = false;
                        if (!armouryOpen)
                        {
                            foreach (var bagType in Core.InventorySafetyEvaluator.PlayerBagContainers)
                            {
                                var bag = manager->GetInventoryContainer(bagType);
                                if (!Core.InventorySafetyEvaluator.IsContainerReadable(bag))
                                {
                                    continue;
                                }

                                for (var i = 0; i < bag->Size; i++)
                                {
                                    var s = bag->GetInventorySlot(i);
                                    if (s != null && s->ItemId != 0 && HoldingsService.NormalizeBaseItemId(s->ItemId) == targetBase)
                                    {
                                        inBags = true;
                                        break;
                                    }
                                }

                                if (inBags)
                                {
                                    break;
                                }
                            }
                        }

                        if (armouryOpen || !inBags)
                        {
                            // Prefer active Armoury tab first when ArmouryBoard is open
                            InventoryType? activeTabContainer = null;
                            if (armouryOpen)
                            {
                                activeTabContainer = InventoryHighlightController.GetActiveArmouryTabContainer(gameGui);
                            }

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
                                    if (s != null && s->ItemId != 0 && HoldingsService.NormalizeBaseItemId(s->ItemId) == targetBase)
                                    {
                                        foundSlot = (short)i;
                                        return s;
                                    }
                                }

                                return null;
                            }

                            if (activeTabContainer.HasValue)
                            {
                                var hit = TryFindInContainer(activeTabContainer.Value, out var hitSlot);
                                if (hit != null)
                                {
                                    effectiveContainer = activeTabContainer.Value;
                                    effectiveSlot = hitSlot;
                                    liveItem = hit;
                                }
                            }

                            if (liveItem == null)
                            {
                                foreach (var armType in Core.InventorySafetyEvaluator.ArmouryGearContainers)
                                {
                                    var hit = TryFindInContainer(armType, out var hitSlot);
                                    if (hit != null)
                                    {
                                        effectiveContainer = armType;
                                        effectiveSlot = hitSlot;
                                        liveItem = hit;
                                        break;
                                    }
                                }
                            }

                            if (liveItem == null)
                            {
                                var hit = TryFindInContainer(InventoryType.ArmorySoulCrystal, out var hitSlot);
                                if (hit != null)
                                {
                                    effectiveContainer = InventoryType.ArmorySoulCrystal;
                                    effectiveSlot = hitSlot;
                                    liveItem = hit;
                                }
                            }
                        }

                        if (liveItem == null && HoldingsService.GetActiveRetainerId() != 0)
                        {
                            foreach (var retType in Core.InventorySafetyEvaluator.RetainerBagContainers)
                            {
                                var c = manager->GetInventoryContainer(retType);
                                if (!Core.InventorySafetyEvaluator.IsContainerReadable(c))
                                {
                                    continue;
                                }

                                for (var i = 0; i < c->Size; i++)
                                {
                                    var s = c->GetInventorySlot(i);
                                    if (s != null && s->ItemId != 0 && HoldingsService.NormalizeBaseItemId(s->ItemId) == targetBase)
                                    {
                                        effectiveContainer = retType;
                                        effectiveSlot = (short)i;
                                        liveItem = s;
                                        break;
                                    }
                                }

                                if (liveItem != null)
                                {
                                    break;
                                }
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
                                suppressUpdates: false);
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
