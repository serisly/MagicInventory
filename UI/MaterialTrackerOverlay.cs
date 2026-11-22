using System;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Classes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using KamiToolKit.Nodes.Simplified;
using KamiToolKit.UiOverlay;
using MagicInventory.Services;

namespace MagicInventory.UI;

public sealed class MaterialTrackerOverlay : IDisposable
{
    private readonly OverlayController overlayController;
    private readonly MaterialTrackerNode trackerNode;

    public MaterialTrackerOverlay(
        MaterialTrackerService tracker,
        Configuration config,
        IDalamudPluginInterface pluginInterface,
        IClientState clientState,
        ICondition condition,
        IChatGui chatGui)
    {
        overlayController = new OverlayController();
        trackerNode = new MaterialTrackerNode(tracker, config, pluginInterface, clientState, condition, chatGui);
        overlayController.AddNode(trackerNode);
    }

    public void ResetPosition() => trackerNode.ResetPosition();

    public void Dispose()
    {
        overlayController.RemoveNode(trackerNode);
        overlayController.Dispose();
    }

    private sealed unsafe class MaterialTrackerNode : OverlayNode
    {
        private const int MaxDisplayedRows = 8;
        private const float PanelWidth = 296f;
        private const float PanelPadX = 8f;
        private const float PanelPadY = 6f;
        private const float ContentWidth = PanelWidth - (PanelPadX * 2f);
        private const float CountButtonWidth = 50f;
        private const float ClearButtonWidth = 54f;
        private const float DragHandleWidth = ContentWidth - CountButtonWidth - ClearButtonWidth - 8f;
        private const float HeaderHeight = 24f;
        private const float RowHeight = 20f;

        private static readonly Vector4 BackdropColor = new(0.05f, 0.06f, 0.09f, 0.78f);
        private static readonly Vector4 PendingTextColor = new(0.95f, 0.95f, 0.92f, 1.0f);
        private static readonly Vector4 CompleteTextColor = new(0.62f, 0.89f, 0.62f, 0.92f);
        private static readonly Vector4 RetainerSubtleColor = new(0.86f, 0.79f, 0.56f, 0.95f);
        private static readonly Vector4 WarnTextColor = new(0.96f, 0.58f, 0.45f, 0.98f);
        private static readonly Vector4 TextOutline = new(0.08f, 0.08f, 0.08f, 0.92f);

        private readonly MaterialTrackerService tracker;
        private readonly Configuration config;
        private readonly IDalamudPluginInterface pluginInterface;
        private readonly IClientState clientState;
        private readonly ICondition condition;
        private readonly IChatGui chatGui;

        private readonly ColorImageNode backdropNode;
        private readonly UnderlinedTextNode headerNode;
        private readonly TextNode jobMetaNode;
        private readonly SimpleComponentNode dragHandleNode;
        private readonly TextButtonNode countModeButton;
        private readonly TextButtonNode clearButton;
        private readonly TextButtonNode craftActionButton;
        private readonly RowWidgets[] rows = new RowWidgets[MaxDisplayedRows];
        private readonly TextNode overflowNode;

        private ViewportEventListener? activeDragListener;
        private Vector2 lastMousePos;
        private string lastTitle = string.Empty;
        private string lastCountMode = string.Empty;
        private string lastCountModeTooltip = string.Empty;
        private string lastJobMeta = string.Empty;
        private float lastJobMetaY = -1f;
        private string lastActionLabel = string.Empty;
        private string lastActionTooltip = string.Empty;
        private float lastActionY = -1f;
        private bool actionSwitchesJob;
        private string lastOverflowText = string.Empty;
        private float lastOverflowY = -1f;
        private float lastPanelHeight = -1f;

        public override OverlayLayer OverlayLayer => OverlayLayer.BehindUserInterface;
        public override bool HideWithNativeUi => true;
        public override bool HideWithUiToggled => true;

        private sealed record RowWidgets(TextNode NameNode, TextNode RetainerNode, TextNode CountNode)
        {
            public string LastName { get; set; } = string.Empty;
            public string LastRetainer { get; set; } = string.Empty;
            public string LastCount { get; set; } = string.Empty;
            public bool? LastComplete { get; set; }
            public float LastY { get; set; } = -1f;
            public float LastRetainerY { get; set; } = -1f;
        }

        public MaterialTrackerNode(
            MaterialTrackerService tracker,
            Configuration config,
            IDalamudPluginInterface pluginInterface,
            IClientState clientState,
            ICondition condition,
            IChatGui chatGui)
        {
            this.tracker = tracker;
            this.config = config;
            this.pluginInterface = pluginInterface;
            this.clientState = clientState;
            this.condition = condition;
            this.chatGui = chatGui;

            var bounds = GetNativeOverlayViewportSize();
            var maxStartX = Math.Max(4f, bounds.X - PanelWidth - 8f);
            var maxStartY = Math.Max(4f, bounds.Y - 60f);
            var startX = float.IsFinite(config.TrackerX) ? Math.Clamp(config.TrackerX, 4f, maxStartX) : 24f;
            var startY = float.IsFinite(config.TrackerY) ? Math.Clamp(config.TrackerY, 4f, maxStartY) : 120f;

            Position = new Vector2(startX, startY);
            Size = new Vector2(PanelWidth, HeaderHeight + PanelPadY * 2f);
            IsVisible = false;

            backdropNode = new ColorImageNode
            {
                Position = Vector2.Zero,
                Size = new Vector2(PanelWidth, HeaderHeight + PanelPadY * 2f),
                Color = BackdropColor,
                IsVisible = true,
            };
            backdropNode.AttachNode(this, NodePosition.AsLastChild);

            headerNode = new UnderlinedTextNode
            {
                Position = new Vector2(PanelPadX, PanelPadY),
                Size = new Vector2(ContentWidth, HeaderHeight),
                String = "Tracked Materials",
                IsVisible = true,
            };
            headerNode.AttachNode(this, NodePosition.AsLastChild);

            jobMetaNode = CreateLineTextNode(AlignmentType.Left, RetainerSubtleColor, fontSize: 11);
            jobMetaNode.AttachNode(this, NodePosition.AsLastChild);

            dragHandleNode = new SimpleComponentNode
            {
                Position = new Vector2(PanelPadX, PanelPadY),
                Size = new Vector2(DragHandleWidth, HeaderHeight),
                ShowClickableCursor = true,
                IsVisible = true,
            };
            dragHandleNode.CollisionNode.AddEvent(AtkEventType.MouseDown, OnDragHandleMouseDown);
            dragHandleNode.AttachNode(this, NodePosition.AsLastChild);

            countModeButton = new TextButtonNode
            {
                Position = new Vector2(PanelWidth - PanelPadX - ClearButtonWidth - 4f - CountButtonWidth, PanelPadY - 2f),
                Size = new Vector2(CountButtonWidth, 20f),
                String = "x1",
                TextTooltip = "Click to cycle craft count (x1 / x5 / x10 / x20 / All).",
                IsVisible = true,
                OnClick = tracker.CycleCraftCount,
            };
            countModeButton.AttachNode(this, NodePosition.AsLastChild);

            clearButton = new TextButtonNode
            {
                Position = new Vector2(PanelWidth - PanelPadX - ClearButtonWidth, PanelPadY - 2f),
                Size = new Vector2(ClearButtonWidth, 20f),
                String = "Clear",
                TextTooltip = "Clear tracked item.",
                IsVisible = true,
                OnClick = tracker.ClearAll,
            };
            clearButton.AttachNode(this, NodePosition.AsLastChild);

            for (var i = 0; i < MaxDisplayedRows; i++)
            {
                var nameNode = CreateLineTextNode(AlignmentType.Left, PendingTextColor);
                var retainerNode = CreateLineTextNode(AlignmentType.Left, RetainerSubtleColor, fontSize: 11);
                var countNode = CreateLineTextNode(AlignmentType.Right, PendingTextColor);

                nameNode.AttachNode(this, NodePosition.AsLastChild);
                retainerNode.AttachNode(this, NodePosition.AsLastChild);
                countNode.AttachNode(this, NodePosition.AsLastChild);

                rows[i] = new RowWidgets(nameNode, retainerNode, countNode);
            }

            overflowNode = CreateLineTextNode(AlignmentType.Right, RetainerSubtleColor, fontSize: 11);
            overflowNode.AttachNode(this, NodePosition.AsLastChild);

            craftActionButton = new TextButtonNode
            {
                Position = new Vector2(PanelPadX, PanelPadY),
                Size = new Vector2(138f, 22f),
                String = "Craft",
                IsVisible = false,
                OnClick = OnCraftActionClick,
            };
            craftActionButton.AttachNode(this, NodePosition.AsLastChild);
        }

        public void ResetPosition()
        {
            StopDragging(savePosition: false);
            Position = new Vector2(24f, 120f);
            config.TrackerX = 24f;
            config.TrackerY = 120f;
            config.Save(pluginInterface);
        }

        private void OnCraftActionClick()
        {
            if (actionSwitchesJob)
            {
                var ok = tracker.SwitchToTrackedJob(out var msg);
                if (!string.IsNullOrEmpty(msg))
                {
                    if (ok) chatGui.Print(msg);
                    else chatGui.PrintError(msg);
                }
            }
            else
            {
                var ok = tracker.StartCraftTracked(out var msg);
                if (!string.IsNullOrEmpty(msg))
                {
                    if (ok) chatGui.Print(msg);
                    else chatGui.PrintError(msg);
                }
            }
        }

        protected override void OnUpdate()
        {
            if (activeDragListener != null && !IsLeftMouseButtonHeldNative())
            {
                StopDragging(savePosition: true);
            }

            var hideForDuty = condition[ConditionFlag.BoundByDuty]
                && !condition[ConditionFlag.Gathering]
                && !condition[ConditionFlag.Crafting];

            if (!clientState.IsLoggedIn
                || condition[ConditionFlag.BetweenAreas]
                || condition[ConditionFlag.BetweenAreas51]
                || condition[ConditionFlag.OccupiedInCutSceneEvent]
                || hideForDuty
                || condition[ConditionFlag.InCombat]
                || !tracker.HasTrackedItems)
            {
                SetOverlayVisible(false);
                return;
            }

            var snapshot = tracker.GetSnapshot();
            if (snapshot.Lines.Count == 0)
            {
                SetOverlayVisible(false);
                return;
            }

            SetOverlayVisible(true);

            var bounds = GetNativeOverlayViewportSize();
            if (bounds.X > 400f && bounds.Y > 300f)
            {
                var clampedX = Math.Clamp(Position.X, 4f, Math.Max(4f, bounds.X - PanelWidth - 8f));
                var clampedY = Math.Clamp(Position.Y, 4f, Math.Max(4f, bounds.Y - 60f));
                if (MathF.Abs(clampedX - Position.X) > 1f || MathF.Abs(clampedY - Position.Y) > 1f)
                {
                    Position = new Vector2(clampedX, clampedY);
                }
            }

            var displayTitle = snapshot.Title.Length > 20
                ? $"{snapshot.Title[..18].TrimEnd()}..."
                : snapshot.Title;
            if (lastTitle != displayTitle)
            {
                lastTitle = displayTitle;
                headerNode.String = displayTitle;
            }

            if (lastCountMode != snapshot.CraftCountLabel)
            {
                lastCountMode = snapshot.CraftCountLabel;
                countModeButton.String = snapshot.CraftCountLabel;
            }

            var countTip = snapshot.RecipeId > 0
                ? "Click to cycle craft count (x1 / x5 / x10 / x20 / All stacks)."
                : "Click to cycle list multiplier (x1 / x5 / x10 / x20) or pull All stacks from retainers.";
            if (lastCountModeTooltip != countTip)
            {
                lastCountModeTooltip = countTip;
                countModeButton.TextTooltip = countTip;
            }

            var y = PanelPadY + HeaderHeight + 3f;

            if (snapshot.RecipeId > 0 && !string.IsNullOrEmpty(snapshot.RequiredJobAbbr))
            {
                string metaText;
                Vector4 metaColor;
                if (snapshot.PlayerJobLevel < snapshot.RequiredLevel)
                {
                    metaText = $"Requires {snapshot.RequiredJobAbbr} Lv. {snapshot.RequiredLevel} (you: Lv. {snapshot.PlayerJobLevel})";
                    metaColor = WarnTextColor;
                }
                else if (!snapshot.IsOnRequiredJob)
                {
                    metaText = $"Job required: {snapshot.RequiredJobAbbr} (Lv. {snapshot.RequiredLevel})";
                    metaColor = RetainerSubtleColor;
                }
                else
                {
                    metaText = $"{snapshot.RequiredJobAbbr} Lv. {snapshot.RequiredLevel}";
                    metaColor = CompleteTextColor;
                }

                if (!jobMetaNode.IsVisible)
                {
                    jobMetaNode.IsVisible = true;
                }

                if (MathF.Abs(lastJobMetaY - y) > 0.5f)
                {
                    lastJobMetaY = y;
                    jobMetaNode.Position = new Vector2(PanelPadX + 2f, y);
                    jobMetaNode.Size = new Vector2(ContentWidth - 4f, 16f);
                }

                if (lastJobMeta != metaText)
                {
                    lastJobMeta = metaText;
                    jobMetaNode.TextColor = metaColor;
                    jobMetaNode.String = metaText;
                }

                y += 17f;
            }
            else if (jobMetaNode.IsVisible)
            {
                jobMetaNode.IsVisible = false;
                lastJobMeta = string.Empty;
                lastJobMetaY = -1f;
            }

            var visibleCount = Math.Min(MaxDisplayedRows, snapshot.Lines.Count);

            for (var i = 0; i < MaxDisplayedRows; i++)
            {
                var widget = rows[i];
                if (i >= visibleCount)
                {
                    if (widget.NameNode.IsVisible)
                    {
                        widget.NameNode.IsVisible = false;
                        widget.RetainerNode.IsVisible = false;
                        widget.CountNode.IsVisible = false;
                        widget.LastY = -1f;
                        widget.LastRetainerY = -1f;
                    }

                    continue;
                }

                var line = snapshot.Lines[i];
                if (!widget.NameNode.IsVisible)
                {
                    widget.NameNode.IsVisible = true;
                    widget.CountNode.IsVisible = true;
                }

                if (MathF.Abs(widget.LastY - y) > 0.5f)
                {
                    widget.LastY = y;
                    widget.NameNode.Position = new Vector2(PanelPadX + 2f, y);
                    widget.NameNode.Size = new Vector2(ContentWidth - 76f, RowHeight);
                    widget.CountNode.Position = new Vector2(PanelWidth - PanelPadX - 72f, y);
                    widget.CountNode.Size = new Vector2(72f, RowHeight);
                }

                var displayName = line.Name.Length > 26
                    ? $"{line.Name[..24].TrimEnd()}..."
                    : line.Name;
                if (widget.LastName != displayName)
                {
                    widget.LastName = displayName;
                    widget.NameNode.String = displayName;
                }

                var countStr = $"{line.OwnedCount}/{line.NeededCount}";
                if (widget.LastCount != countStr)
                {
                    widget.LastCount = countStr;
                    widget.CountNode.String = countStr;
                }

                if (widget.LastComplete != line.IsComplete)
                {
                    widget.LastComplete = line.IsComplete;
                    var rowColor = line.IsComplete ? CompleteTextColor : PendingTextColor;
                    widget.NameNode.TextColor = rowColor;
                    widget.CountNode.TextColor = rowColor;
                }

                y += RowHeight;

                if (!string.IsNullOrEmpty(line.RetainerNote))
                {
                    var rawNote = line.RetainerNote.Length > 36
                        ? $"{line.RetainerNote[..33].TrimEnd()}..."
                        : line.RetainerNote;
                    var retText = $"  {rawNote}";
                    if (!widget.RetainerNode.IsVisible)
                    {
                        widget.RetainerNode.IsVisible = true;
                    }

                    var retY = y - 3f;
                    if (MathF.Abs(widget.LastRetainerY - retY) > 0.5f)
                    {
                        widget.LastRetainerY = retY;
                        widget.RetainerNode.Position = new Vector2(PanelPadX + 8f, retY);
                        widget.RetainerNode.Size = new Vector2(ContentWidth - 10f, 16f);
                    }

                    if (widget.LastRetainer != retText)
                    {
                        widget.LastRetainer = retText;
                        widget.RetainerNode.String = retText;
                    }

                    y += 15f;
                }
                else if (widget.RetainerNode.IsVisible)
                {
                    widget.RetainerNode.IsVisible = false;
                    widget.LastRetainer = string.Empty;
                    widget.LastRetainerY = -1f;
                }
            }

            if (snapshot.Lines.Count > MaxDisplayedRows)
            {
                if (!overflowNode.IsVisible)
                {
                    overflowNode.IsVisible = true;
                }

                if (MathF.Abs(lastOverflowY - y) > 0.5f)
                {
                    lastOverflowY = y;
                    overflowNode.Position = new Vector2(PanelPadX, y);
                    overflowNode.Size = new Vector2(ContentWidth, 16f);
                }

                var overflowText = $"+{snapshot.Lines.Count - MaxDisplayedRows} more";
                if (lastOverflowText != overflowText)
                {
                    lastOverflowText = overflowText;
                    overflowNode.String = overflowText;
                }

                y += 18f;
            }
            else if (overflowNode.IsVisible)
            {
                overflowNode.IsVisible = false;
                lastOverflowText = string.Empty;
                lastOverflowY = -1f;
            }

            var showAction = false;
            var actionLabel = string.Empty;
            var actionTip = string.Empty;
            if (snapshot.RecipeId > 0)
            {
                if (!snapshot.IsOnRequiredJob && snapshot.MatchingGearsetId >= 0)
                {
                    showAction = true;
                    actionSwitchesJob = true;
                    actionLabel = $"Switch to {snapshot.RequiredJobAbbr}";
                    actionTip = $"Equip your {snapshot.RequiredJobAbbr} gearset.";
                }
                else if (snapshot.IsOnRequiredJob && snapshot.PlayerJobLevel >= snapshot.RequiredLevel && snapshot.HasAllMaterialsInBag)
                {
                    showAction = true;
                    actionSwitchesJob = false;
                    actionLabel = snapshot.CanQuickSynth ? $"Craft ({snapshot.CraftCount})" : "Open Recipe";
                    actionTip = snapshot.CanQuickSynth
                        ? $"Open {snapshot.Title} and start Quick Synthesis ({snapshot.CraftCount})."
                        : $"Open {snapshot.Title} in your Crafting Log.";
                }
            }

            if (showAction)
            {
                if (!craftActionButton.IsVisible)
                {
                    craftActionButton.IsVisible = true;
                }

                var actY = y + 3f;
                if (MathF.Abs(lastActionY - actY) > 0.5f)
                {
                    lastActionY = actY;
                    craftActionButton.Position = new Vector2(PanelPadX, actY);
                }

                if (lastActionLabel != actionLabel)
                {
                    lastActionLabel = actionLabel;
                    craftActionButton.String = actionLabel;
                }

                if (lastActionTooltip != actionTip)
                {
                    lastActionTooltip = actionTip;
                    craftActionButton.TextTooltip = actionTip;
                }

                y += 28f;
            }
            else if (craftActionButton.IsVisible)
            {
                craftActionButton.IsVisible = false;
                lastActionLabel = string.Empty;
                lastActionTooltip = string.Empty;
                lastActionY = -1f;
            }

            var totalHeight = y + PanelPadY;
            if (MathF.Abs(lastPanelHeight - totalHeight) > 0.5f)
            {
                lastPanelHeight = totalHeight;
                Size = new Vector2(PanelWidth, totalHeight);
                backdropNode.Size = new Vector2(PanelWidth, totalHeight);
            }
        }

        private static Vector2 GetNativeOverlayViewportSize()
        {
            var dev = Device.Instance();
            if (dev != null && dev->Width > 320 && dev->Height > 240)
            {
                return new Vector2(dev->Width, dev->Height);
            }

            return new Vector2(1920f, 1080f);
        }

        private static bool IsLeftMouseButtonHeldNative()
        {
            var input = UIInputData.Instance();
            return input != null && (input->CursorInputs.MouseButtonHeldFlags & MouseButtonFlags.LBUTTON) != 0;
        }

        private void OnDragHandleMouseDown(
            AtkEventListener* listener,
            AtkEventType eventType,
            int eventParam,
            AtkEvent* atkEvent,
            AtkEventData* atkEventData)
        {
            if (atkEventData == null || atkEventData->MouseData.ButtonId != 0)
            {
                return;
            }

            StopDragging(savePosition: false);

            lastMousePos = new Vector2(atkEventData->MouseData.PosX, atkEventData->MouseData.PosY);

            activeDragListener = new ViewportEventListener(OnDragViewportEvent);
            activeDragListener.AddEvent(AtkEventType.MouseMove, dragHandleNode);
            activeDragListener.AddEvent(AtkEventType.MouseUp, dragHandleNode);

            if (atkEvent != null)
            {
                atkEvent->SetEventIsHandled(true);
            }
        }

        private void OnDragViewportEvent(
            AtkEventListener* listener,
            AtkEventType eventType,
            int eventParam,
            AtkEvent* atkEvent,
            AtkEventData* atkEventData)
        {
            if (eventType == AtkEventType.MouseMove && atkEventData != null)
            {
                var currentMouse = new Vector2(atkEventData->MouseData.PosX, atkEventData->MouseData.PosY);
                var delta = currentMouse - lastMousePos;
                lastMousePos = currentMouse;

                var bounds = GetNativeOverlayViewportSize();
                var maxX = Math.Max(4f, bounds.X - PanelWidth - 8f);
                var maxY = Math.Max(4f, bounds.Y - 60f);
                Position = new Vector2(
                    Math.Clamp(Position.X + delta.X, 4f, maxX),
                    Math.Clamp(Position.Y + delta.Y, 4f, maxY));

                if (atkEvent != null)
                {
                    atkEvent->SetEventIsHandled(true);
                }
            }
            else if (eventType == AtkEventType.MouseUp)
            {
                StopDragging(savePosition: true);
            }
        }

        private void StopDragging(bool savePosition)
        {
            if (activeDragListener != null)
            {
                activeDragListener.Dispose();
                activeDragListener = null;
            }

            if (savePosition)
            {
                config.TrackerX = Position.X;
                config.TrackerY = Position.Y;
                config.Save(pluginInterface);
            }
        }

        private void SetOverlayVisible(bool visible)
        {
            if (IsVisible == visible)
            {
                return;
            }

            if (!visible)
            {
                StopDragging(savePosition: false);
            }

            IsVisible = visible;
        }

        private static TextNode CreateLineTextNode(AlignmentType alignment, Vector4 textColor, uint fontSize = 12)
        {
            var node = new TextNode
            {
                FontType = FontType.Axis,
                FontSize = fontSize,
                AlignmentType = alignment,
                TextColor = textColor,
                TextOutlineColor = TextOutline,
                IsVisible = false,
            };
            node.AddTextFlags(TextFlags.Edge);
            return node;
        }

        protected override void Dispose(bool isNativeDestructor)
        {
            StopDragging(savePosition: false);

            if (!isNativeDestructor)
            {
                overflowNode.DetachNode();
                overflowNode.Dispose();

                craftActionButton.DetachNode();
                craftActionButton.Dispose();

                jobMetaNode.DetachNode();
                jobMetaNode.Dispose();

                foreach (var row in rows)
                {
                    row.NameNode.DetachNode();
                    row.NameNode.Dispose();
                    row.RetainerNode.DetachNode();
                    row.RetainerNode.Dispose();
                    row.CountNode.DetachNode();
                    row.CountNode.Dispose();
                }

                countModeButton.DetachNode();
                countModeButton.Dispose();

                clearButton.DetachNode();
                clearButton.Dispose();

                dragHandleNode.DetachNode();
                dragHandleNode.Dispose();

                headerNode.DetachNode();
                headerNode.Dispose();

                backdropNode.DetachNode();
                backdropNode.Dispose();
            }

            base.Dispose(isNativeDestructor);
        }
    }
}
