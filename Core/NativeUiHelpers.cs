using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MagicInventory.Core;

public static unsafe class NativeUiHelpers
{
    public static bool IsAddonOpenAndReady(IGameGui gameGui, string addonName)
    {
        return GetReadyAddon(gameGui, addonName) != null;
    }

    public static string? GetOpenRetainerAddonName(IGameGui gameGui)
    {
        if (IsAddonOpenAndReady(gameGui, "InventoryRetainerLarge"))
        {
            return "InventoryRetainerLarge";
        }

        return IsAddonOpenAndReady(gameGui, "InventoryRetainer")
            ? "InventoryRetainer"
            : null;
    }

    public static AtkUnitBase* GetReadyAddon(IGameGui gameGui, string addonName)
    {
        if (string.IsNullOrWhiteSpace(addonName))
        {
            return null;
        }

        var ptr = gameGui.GetAddonByName(addonName);
        if (ptr.IsNull)
        {
            return null;
        }

        var addon = (AtkUnitBase*)ptr.Address;
        if (addon == null || !addon->IsVisible || !addon->IsReady)
        {
            return null;
        }

        return addon;
    }

    public static string ReadTextNodeValue(AtkTextNode* textNode)
    {
        if (textNode == null || textNode->NodeText.StringPtr.Value == null)
        {
            return string.Empty;
        }

        return MemoryHelper.ReadSeStringNullTerminated((nint)textNode->NodeText.StringPtr.Value)
            .TextValue
            .Trim();
    }

    public static string NormalizeItemLabel(string raw)
        => raw.Trim().TrimEnd('\uE03C', ' ');

    public static bool ClickAddonButton(AtkUnitBase* addon, AtkComponentButton* button)
    {
        if (addon == null || !addon->IsVisible || !addon->IsReady || button == null || !button->IsEnabled)
        {
            return false;
        }

        var owner = button->AtkComponentBase.OwnerNode;
        if (owner == null || !owner->AtkResNode.IsVisible())
        {
            return false;
        }

        var evt = owner->AtkResNode.AtkEventManager.Event;
        AtkEvent* matched = null;
        AtkEvent* fallbackClick = null;
        var guard = 0;
        while (evt != null && guard++ < 64)
        {
            if (evt->State.EventType == AtkEventType.ButtonClick)
            {
                if (evt->Listener == (AtkEventListener*)addon)
                {
                    matched = evt;
                    break;
                }

                if (fallbackClick == null && evt->Listener != null)
                {
                    fallbackClick = evt;
                }
            }

            evt = evt->NextEvent;
        }

        var target = matched != null ? matched : fallbackClick;
        if (target == null || target->Listener == null)
        {
            return false;
        }

        var eventData = default(AtkEventData);
        target->Listener->ReceiveEvent(target->State.EventType, (int)target->Param, target, &eventData);
        return true;
    }

    public static bool FireCallback(AtkUnitBase* addon, bool updateState, ReadOnlySpan<AtkValue> values)
    {
        if (addon == null || !addon->IsVisible || !addon->IsReady || values.Length == 0)
        {
            return false;
        }

        AtkValue* buffer = stackalloc AtkValue[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            buffer[i] = values[i];
        }

        addon->FireCallback((uint)values.Length, buffer, updateState);
        return true;
    }

    public static AtkValue IntValue(int value)
    {
        var atkValue = new AtkValue();
        atkValue.SetInt(value);
        return atkValue;
    }

    public static AtkValue UIntValue(uint value)
    {
        return new AtkValue
        {
            Type = AtkValueType.UInt,
            UInt = value,
        };
    }

    public static AtkValue BoolValue(bool value)
    {
        var atkValue = new AtkValue();
        atkValue.SetBool(value);
        return atkValue;
    }

    public static (Vector2 Position, Vector2 Size) ResolveBottomMarginPosition(
        AtkUnitBase* addon,
        float width,
        float height,
        bool preferRightSide,
        float? explicitTargetY = null)
    {
        if (addon == null || addon->RootNode == null)
        {
            return (new Vector2(18f, 360f), new Vector2(width, height));
        }

        var rootW = Math.Max(320f, addon->RootNode->Width);
        var rootH = Math.Max(220f, addon->RootNode->Height);
        var targetY = explicitTargetY ?? (rootH - 46f);

        var obstacles = new List<(float Left, float Right)>();
        var uld = addon->UldManager;
        if (uld.NodeList != null)
        {
            for (var i = 0; i < uld.NodeListCount; i++)
            {
                var node = uld.NodeList[i];
                if (node == null || !node->IsVisible() || node->NodeId >= 100_000)
                {
                    continue;
                }

                float absX;
                float absY;
                if (node->ParentNode == addon->RootNode)
                {
                    absX = node->X;
                    absY = node->Y;
                }
                else if (node->ParentNode != null
                    && node->ParentNode->ParentNode == addon->RootNode
                    && node->ParentNode->IsVisible()
                    && node->ParentNode->Height <= 80f)
                {
                    absX = node->ParentNode->X + node->X;
                    absY = node->ParentNode->Y + node->Y;
                }
                else
                {
                    continue;
                }

                if (node->Type == NodeType.Res)
                {
                    continue;
                }

                if (node->Width <= 6f || node->Height <= 6f || node->Width > rootW * 0.7f || node->Height > 64f)
                {
                    continue;
                }

                var obstacleLeft = absX;
                var obstacleRight = absX + node->Width;
                if (node->Type == NodeType.Text)
                {
                    var textNode = node->GetAsAtkTextNode();
                    var text = ReadTextNodeValue(textNode);
                    if (string.IsNullOrEmpty(text))
                    {
                        continue;
                    }

                    ushort drawW = 0;
                    ushort drawH = 0;
                    textNode->GetTextDrawSize(&drawW, &drawH);
                    if (drawW > 0 && drawW < node->Width)
                    {
                        var align = textNode->AlignmentType;
                        if (align is AlignmentType.TopRight or AlignmentType.Right or AlignmentType.BottomRight)
                        {
                            obstacleLeft = obstacleRight - drawW;
                        }
                        else if (align is AlignmentType.Top or AlignmentType.Center or AlignmentType.Bottom)
                        {
                            var pad = (node->Width - drawW) * 0.5f;
                            obstacleLeft = absX + pad;
                            obstacleRight = obstacleLeft + drawW;
                        }
                        else
                        {
                            obstacleRight = absX + drawW;
                        }
                    }
                }

                var verticalOverlap = absY < targetY + height && (absY + node->Height) > targetY;
                if (verticalOverlap)
                {
                    obstacles.Add((obstacleLeft - 6f, obstacleRight + 6f));
                }
            }
        }

        obstacles.Sort((a, b) => a.Left.CompareTo(b.Left));

        var clearSpans = new List<(float Start, float End)>();
        var cursor = 16f;
        var rightBound = rootW - 16f;

        foreach (var (left, right) in obstacles)
        {
            if (right <= cursor)
            {
                continue;
            }

            if (left > cursor)
            {
                clearSpans.Add((cursor, Math.Min(left, rightBound)));
            }

            cursor = Math.Max(cursor, right);
            if (cursor >= rightBound)
            {
                break;
            }
        }

        if (cursor < rightBound)
        {
            clearSpans.Add((cursor, rightBound));
        }

        if (preferRightSide)
        {
            for (var i = clearSpans.Count - 1; i >= 0; i--)
            {
                var span = clearSpans[i];
                if (span.End - span.Start >= width)
                {
                    return (new Vector2(span.End - width, targetY), new Vector2(width, height));
                }
            }
        }
        else
        {
            for (var i = 0; i < clearSpans.Count; i++)
            {
                var span = clearSpans[i];
                if (span.End - span.Start >= width)
                {
                    return (new Vector2(span.Start, targetY), new Vector2(width, height));
                }
            }
        }

        var fallbackX = preferRightSide
            ? Math.Max(16f, rootW - width - 18f)
            : 18f;
        return (new Vector2(fallbackX, targetY), new Vector2(width, height));
    }

    public static string ExtractLongestAddonLiteral(IDataManager dataManager, uint addonRowId)
    {
        var row = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Addon>()?.GetRowOrDefault(addonRowId);
        if (row is null)
        {
            return string.Empty;
        }

        var macro = row.Value.Text.ToMacroString();
        if (string.IsNullOrWhiteSpace(macro))
        {
            return string.Empty;
        }

        var best = string.Empty;
        var span = macro.AsSpan();
        var segStart = 0;
        var depth = 0;
        for (var i = 0; i < span.Length; i++)
        {
            var c = span[i];
            if (c == '<')
            {
                if (depth == 0 && i > segStart)
                {
                    var candidate = CollapseWhitespace(span[segStart..i]);
                    if (candidate.Length > best.Length)
                    {
                        best = candidate;
                    }
                }

                depth++;
            }
            else if (c == '>' && depth > 0)
            {
                depth--;
                if (depth == 0)
                {
                    segStart = i + 1;
                }
            }
        }

        if (depth == 0 && segStart < span.Length)
        {
            var candidate = CollapseWhitespace(span[segStart..]);
            if (candidate.Length > best.Length)
            {
                best = candidate;
            }
        }

        return best;
    }

    public static string CollapseWhitespace(ReadOnlySpan<char> input)
    {
        if (input.IsEmpty)
        {
            return string.Empty;
        }

        Span<char> buffer = input.Length <= 512 ? stackalloc char[input.Length] : new char[input.Length];
        var outLen = 0;
        var prevSpace = true;
        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (char.IsWhiteSpace(c))
            {
                if (!prevSpace)
                {
                    buffer[outLen++] = ' ';
                    prevSpace = true;
                }
            }
            else
            {
                buffer[outLen++] = c;
                prevSpace = false;
            }
        }

        if (outLen > 0 && buffer[outLen - 1] == ' ')
        {
            outLen--;
        }

        return outLen == 0 ? string.Empty : new string(buffer[..outLen]);
    }
}
