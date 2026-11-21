using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
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
        private const float PanelWidth = 284f;
        private const float DragHandleWidth = PanelWidth - 104f;
        private const float HeaderHeight = 24f;
        private const float RowHeight = 20f;

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
        private string lastActionLabel = string.Empty;
        private string lastActionTooltip = string.Empty;
        private bool actionSwitchesJob;
        private string lastOverflowText = string.Empty;

        public override OverlayLayer OverlayLayer => OverlayLayer.BehindUserInterface;
        public override bool HideWithNativeUi => true;
        public override bool HideWithUiToggled => true;

        private sealed record RowWidgets(TextNode NameNode, TextNode RetainerNode, TextNode CountNode)
        {
            public string LastName { get; set; } = string.Empty;
            public string LastRetainer { get; set; } = string.Empty;
            public string LastCount { get; set; } = string.Empty;
            public bool? LastComplete { get; set; }
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

            var vpSize = Dalamud.Bindings.ImGui.ImGui.GetMainViewport().Size;
            var maxStartX = vpSize.X > 400f ? Math.Max(320f, vpSize.X - PanelWidth - 8f) : 3600f;
            var maxStartY = vpSize.Y > 300f ? Math.Max(200f, vpSize.Y - 60f) : 2000f;
            var startX = float.IsFinite(config.TrackerX) ? Math.Clamp(config.TrackerX, 4f, maxStartX) : 24f;
            var startY = float.IsFinite(config.TrackerY) ? Math.Clamp(config.TrackerY, 4f, maxStartY) : 120f;

            Position = new Vector2(startX, startY);
            Size = new Vector2(PanelWidth, HeaderHeight);
            IsVisible = false;

            headerNode = new UnderlinedTextNode
            {
                Position = new Vector2(0f, 0f),
                Size = new Vector2(PanelWidth, HeaderHeight),
                String = "Tracked Materials",
                IsVisible = true,
            };
            headerNode.AttachNode(this, NodePosition.AsLastChild);

            jobMetaNode = CreateLineTextNode(AlignmentType.Left, RetainerSubtleColor, fontSize: 11);
            jobMetaNode.AttachNode(this, NodePosition.AsLastChild);

            dragHandleNode = new SimpleComponentNode
            {
                Position = new Vector2(0f, 0f),
                Size = new Vector2(DragHandleWidth, HeaderHeight),
                ShowClickableCursor = true,
                IsVisible = true,
            };
            dragHandleNode.CollisionNode.AddEvent(AtkEventType.MouseDown, OnDragHandleMouseDown);
            dragHandleNode.AttachNode(this, NodePosition.AsLastChild);

            countModeButton = new TextButtonNode
            {
                Position = new Vector2(PanelWidth - 100f, -2f),
                Size = new Vector2(42f, 20f),
                String = "x1",
                TextTooltip = "Click to cycle craft count (x1 / x5 / x10 / x20 / All).",
                IsVisible = true,
                OnClick = tracker.CycleCraftCount,
            };
            countModeButton.AttachNode(this, NodePosition.AsLastChild);

            clearButton = new TextButtonNode
            {
                Position = new Vector2(PanelWidth - 54f, -2f),
                Size = new Vector2(54f, 20f),
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
                Position = new Vector2(0f, 0f),
                Size = new Vector2(130f, 22f),
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
            // Release stuck drag if left mouse button was released outside game window (e.g., alt-tab)
            if (activeDragListener != null
                && !Dalamud.Bindings.ImGui.ImGui.IsMouseDown(Dalamud.Bindings.ImGui.ImGuiMouseButton.Left))
            {
                StopDragging(savePosition: true);
            }

            if (!clientState.IsLoggedIn
                || condition[ConditionFlag.BetweenAreas]
                || condition[ConditionFlag.BetweenAreas51]
                || condition[ConditionFlag.OccupiedInCutSceneEvent]
                || condition[ConditionFlag.BoundByDuty]
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

            // Keep tracker clamped inside current display viewport (e.g., after monitor/resolution switch)
            var liveVp = Dalamud.Bindings.ImGui.ImGui.GetMainViewport().Size;
            if (liveVp.X > 400f && liveVp.Y > 300f)
            {
                var clampedX = Math.Clamp(Position.X, 4f, Math.Max(4f, liveVp.X - PanelWidth - 8f));
                var clampedY = Math.Clamp(Position.Y, 4f, Math.Max(4f, liveVp.Y - 60f));
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

            tracker.TickPendingCraft();

            var y = HeaderHeight + 3f;

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

                jobMetaNode.IsVisible = true;
                jobMetaNode.Position = new Vector2(2f, y);
                jobMetaNode.Size = new Vector2(PanelWidth - 4f, 16f);
                jobMetaNode.TextColor = metaColor;
                if (lastJobMeta != metaText)
                {
                    lastJobMeta = metaText;
                    jobMetaNode.String = metaText;
                }

                y += 17f;
            }
            else if (jobMetaNode.IsVisible)
            {
                jobMetaNode.IsVisible = false;
                lastJobMeta = string.Empty;
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
                    }

                    continue;
                }

                var line = snapshot.Lines[i];
                if (!widget.NameNode.IsVisible)
                {
                    widget.NameNode.IsVisible = true;
                    widget.CountNode.IsVisible = true;
                }

                widget.NameNode.Position = new Vector2(2f, y);
                widget.NameNode.Size = new Vector2(PanelWidth - 76f, RowHeight);

                widget.CountNode.Position = new Vector2(PanelWidth - 72f, y);
                widget.CountNode.Size = new Vector2(72f, RowHeight);

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
                    var retText = $"  {line.RetainerNote}";
                    widget.RetainerNode.IsVisible = true;
                    widget.RetainerNode.Position = new Vector2(8f, y - 3f);
                    widget.RetainerNode.Size = new Vector2(PanelWidth - 12f, 16f);
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
                }
            }

            if (snapshot.Lines.Count > MaxDisplayedRows)
            {
                overflowNode.IsVisible = true;
                overflowNode.Position = new Vector2(0f, y);
                overflowNode.Size = new Vector2(PanelWidth, 16f);
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
            }

            // Bottom action button: Switch Job (if wrong job & gearset exists) or Craft (when ready)
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
                craftActionButton.IsVisible = true;
                craftActionButton.Position = new Vector2(0f, y + 3f);
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
            }

            Size = new Vector2(PanelWidth, y);
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

                var vpSize = Dalamud.Bindings.ImGui.ImGui.GetMainViewport().Size;
                var maxX = Math.Max(320f, vpSize.X - PanelWidth - 8f);
                var maxY = Math.Max(200f, vpSize.Y - 60f);
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
            if (!isNativeDestructor)
            {
                StopDragging(savePosition: false);

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
            }

            base.Dispose(isNativeDestructor);
        }
    }
}
