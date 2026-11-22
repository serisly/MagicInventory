using System;
using System.Collections.Generic;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Controllers;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using MagicInventory.Services;

namespace MagicInventory.UI;

public sealed unsafe class NativeAddonButtonGroup<TAddon> : IDisposable
    where TAddon : unmanaged
{
    public delegate AtkUnitBase* UnitBaseResolver(TAddon* addon);
    public delegate AtkResNode* ParentNodeResolver(TAddon* addon);
    public delegate (Vector2 Position, Vector2 Size) PlacementResolver(TAddon* addon);

    public sealed record ButtonSpec(
        string InitialLabel,
        Func<MagicSortButtonState> GetState,
        Action OnClick,
        ParentNodeResolver ResolveParent,
        PlacementResolver ResolvePlacement,
        Action<bool>? OnHoverChanged = null);

    private sealed class ButtonSlot
    {
        public required ButtonSpec Spec { get; init; }
        public TextButtonNode? Node { get; set; }
        public string LastLabel { get; set; } = string.Empty;
        public string LastTooltip { get; set; } = string.Empty;
        public bool? LastEnabled { get; set; }
        public bool? LastVisible { get; set; }
        public bool IsHovered { get; set; }
    }

    private readonly AddonController<TAddon> controller;
    private readonly ButtonSlot[] slots;
    private readonly UnitBaseResolver toUnitBase;

    private nint attachedAddonAddress;
    private bool setupAttemptedForInstance;
    private float lastRootW;
    private float lastRootH;

    public NativeAddonButtonGroup(
        string addonName,
        UnitBaseResolver toUnitBase,
        params ButtonSpec[] buttonSpecs)
    {
        this.toUnitBase = toUnitBase;
        slots = new ButtonSlot[buttonSpecs.Length];
        for (var i = 0; i < buttonSpecs.Length; i++)
        {
            slots[i] = new ButtonSlot { Spec = buttonSpecs[i] };
        }

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
        DetachAll();
        attachedAddonAddress = (nint)addon;
        setupAttemptedForInstance = true;
        lastRootW = 0f;
        lastRootH = 0f;

        if (addon == null)
        {
            return;
        }

        var unit = toUnitBase(addon);
        if (unit != null && unit->RootNode != null)
        {
            lastRootW = unit->RootNode->Width;
            lastRootH = unit->RootNode->Height;
        }

        foreach (var slot in slots)
        {
            var parentNode = slot.Spec.ResolveParent(addon);
            if (parentNode == null)
            {
                continue;
            }

            var (pos, size) = slot.Spec.ResolvePlacement(addon);
            var capturedSlot = slot;
            var btn = new TextButtonNode
            {
                Position = pos,
                Size = size,
                String = slot.Spec.InitialLabel,
                IsVisible = false,
                OnClick = () =>
                {
                    ClearHover(capturedSlot);
                    capturedSlot.Spec.OnClick();
                },
            };

            if (slot.Spec.OnHoverChanged is { } onHover)
            {
                btn.AddEvent(AtkEventType.MouseOver, () =>
                {
                    if (!capturedSlot.IsHovered && capturedSlot.LastEnabled != false && btn.IsEnabled)
                    {
                        capturedSlot.IsHovered = true;
                        onHover(true);
                    }
                });
                btn.AddEvent(AtkEventType.MouseOut, () => ClearHover(capturedSlot));
            }

            btn.AttachNode(parentNode, NodePosition.AsLastChild);
            slot.Node = btn;
            ResetSlotCache(slot);
        }

        SyncState(addon);
    }

    private void SyncState(TAddon* addon)
    {
        if (addon == null)
        {
            return;
        }

        if (!setupAttemptedForInstance || attachedAddonAddress != (nint)addon)
        {
            OnSetup(addon);
            return;
        }

        var unit = toUnitBase(addon);
        if (unit != null && unit->RootNode != null)
        {
            var w = unit->RootNode->Width;
            var h = unit->RootNode->Height;
            if (MathF.Abs(w - lastRootW) > 1f || MathF.Abs(h - lastRootH) > 1f)
            {
                lastRootW = w;
                lastRootH = h;
                foreach (var slot in slots)
                {
                    if (slot.Node == null)
                    {
                        continue;
                    }

                    var (pos, size) = slot.Spec.ResolvePlacement(addon);
                    slot.Node.Position = pos;
                    slot.Node.Size = size;
                }
            }
        }

        foreach (var slot in slots)
        {
            if (slot.Node == null)
            {
                continue;
            }

            var state = slot.Spec.GetState();

            if (slot.LastVisible != state.IsVisible)
            {
                slot.LastVisible = state.IsVisible;
                slot.Node.IsVisible = state.IsVisible;
                if (!state.IsVisible)
                {
                    ClearHover(slot);
                }
            }

            if (!state.IsVisible)
            {
                continue;
            }

            if (slot.LastEnabled != state.IsEnabled)
            {
                slot.LastEnabled = state.IsEnabled;
                slot.Node.IsEnabled = state.IsEnabled;
                if (!state.IsEnabled)
                {
                    ClearHover(slot);
                }
            }

            if (slot.LastLabel != state.Label)
            {
                slot.LastLabel = state.Label;
                slot.Node.String = state.Label;
            }

            var tip = state.Tooltip ?? string.Empty;
            if (slot.LastTooltip != tip)
            {
                slot.LastTooltip = tip;
                slot.Node.TextTooltip = tip;
            }
        }
    }

    private void OnFinalize(TAddon* _)
    {
        DetachAll();
        attachedAddonAddress = nint.Zero;
        setupAttemptedForInstance = false;
    }

    private void DetachAll()
    {
        foreach (var slot in slots)
        {
            ClearHover(slot);
            if (slot.Node != null)
            {
                slot.Node.DetachNode();
                slot.Node.Dispose();
                slot.Node = null;
            }

            ResetSlotCache(slot);
        }
    }

    private static void ClearHover(ButtonSlot slot)
    {
        if (slot.IsHovered)
        {
            slot.IsHovered = false;
            slot.Spec.OnHoverChanged?.Invoke(false);
        }
    }

    private static void ResetSlotCache(ButtonSlot slot)
    {
        slot.LastLabel = string.Empty;
        slot.LastTooltip = string.Empty;
        slot.LastEnabled = null;
        slot.LastVisible = null;
    }

    public void Dispose()
    {
        controller.Disable();
        DetachAll();
        controller.Dispose();
    }
}
