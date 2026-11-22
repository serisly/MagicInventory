using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Controllers;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using MagicInventory.Core;
using MagicInventory.Services;

namespace MagicInventory.UI;

public sealed unsafe class RecipeNoteController : IDisposable
{
    private const uint DetailsContainerNodeId = 57;
    private const uint IngredientsContainerNodeId = 88;
    private const int IngredientSlotCount = 6;

    private const float DefaultButtonX = 252f;
    private const float DefaultButtonY = 152f;
    private const float ButtonWidth = 116f;
    private const float ButtonHeight = 28f;

    private const float LabelX = 205f;
    private const float LabelBaseY = 36f;
    private const float LabelRowStride = 46f;
    private const float LabelWidth = 162f;
    private const float LabelHeight = 16f;

    private static readonly Vector4 RetainerLabelColor = new(0.86f, 0.79f, 0.56f, 0.96f);
    private static readonly Vector4 LabelOutlineColor = new(0.08f, 0.08f, 0.08f, 0.92f);

    private readonly IGameGui gameGui;
    private readonly MaterialTrackerService tracker;
    private readonly AddonController<AddonRecipeNote> controller;
    private readonly TextNode?[] retainerLabels = new TextNode?[IngredientSlotCount];
    private readonly string[] lastLabelTexts = new string[IngredientSlotCount];

    private TextButtonNode? trackButton;
    private nint attachedAddonAddress;
    private bool setupAttemptedForInstance;
    private uint lastRecipeId;
    private bool? lastTrackedState;
    private DateTime lastHoldingsRefreshUtc = DateTime.MinValue;

    public RecipeNoteController(IGameGui gameGui, MaterialTrackerService tracker)
    {
        this.gameGui = gameGui;
        this.tracker = tracker;

        controller = new AddonController<AddonRecipeNote>
        {
            AddonName = "RecipeNote",
            OnSetup = OnSetup,
            OnRefresh = SyncState,
            OnUpdate = SyncState,
            OnFinalize = OnFinalize,
        };
    }

    public void Enable() => controller.Enable();

    private void OnSetup(AddonRecipeNote* addon)
    {
        DetachNodes();
        attachedAddonAddress = (nint)addon;
        setupAttemptedForInstance = true;

        if (addon == null)
        {
            return;
        }

        var detailsContainer = addon->GetNodeById(DetailsContainerNodeId);
        if (detailsContainer == null)
        {
            return;
        }

        var maxX = Math.Max(8f, detailsContainer->Width > ButtonWidth + 8f
            ? detailsContainer->Width - ButtonWidth - 6f
            : DefaultButtonX);
        var maxY = Math.Max(8f, detailsContainer->Height > ButtonHeight + 8f
            ? detailsContainer->Height - ButtonHeight - 6f
            : DefaultButtonY);
        var btnX = Math.Clamp(DefaultButtonX, 8f, maxX);
        var btnY = Math.Clamp(DefaultButtonY, 8f, maxY);

        trackButton = new TextButtonNode
        {
            Position = new Vector2(btnX, btnY),
            Size = new Vector2(ButtonWidth, ButtonHeight),
            String = "Track",
            IsVisible = false,
            OnClick = OnTrackClicked,
        };
        trackButton.AttachNode(detailsContainer, NodePosition.AsLastChild);

        var ingredientsContainer = addon->GetNodeById(IngredientsContainerNodeId);
        if (ingredientsContainer != null)
        {
            for (var i = 0; i < IngredientSlotCount; i++)
            {
                var label = new TextNode
                {
                    Position = new Vector2(LabelX, LabelBaseY + (i * LabelRowStride)),
                    Size = new Vector2(LabelWidth, LabelHeight),
                    FontType = FontType.Axis,
                    FontSize = 11,
                    AlignmentType = AlignmentType.Left,
                    TextColor = RetainerLabelColor,
                    TextOutlineColor = LabelOutlineColor,
                    IsVisible = false,
                };
                label.AddTextFlags(TextFlags.Edge);
                label.AttachNode(ingredientsContainer, NodePosition.AsLastChild);

                retainerLabels[i] = label;
                lastLabelTexts[i] = string.Empty;
            }
        }

        lastRecipeId = 0;
        lastTrackedState = null;
        lastHoldingsRefreshUtc = DateTime.MinValue;
        SyncState(addon);
    }

    private void SyncState(AddonRecipeNote* addon)
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

        if (trackButton == null)
        {
            return;
        }

        var recipe = tracker.GetActiveRecipe(addon);
        if (recipe == null)
        {
            if (trackButton.IsVisible)
            {
                trackButton.IsVisible = false;
            }

            HideAllIngredientLabels();
            lastRecipeId = 0;
            lastTrackedState = null;
            return;
        }

        if (!trackButton.IsVisible)
        {
            trackButton.IsVisible = true;
        }

        var recipeChanged = lastRecipeId != recipe.RecipeId;
        var isTracked = tracker.IsRecipeTracked(recipe.RecipeId);

        if (recipeChanged || lastTrackedState != isTracked)
        {
            lastRecipeId = recipe.RecipeId;
            lastTrackedState = isTracked;
            trackButton.String = isTracked ? "Untrack" : "Track";
            trackButton.TextTooltip = isTracked
                ? $"Stop tracking {recipe.Name}."
                : $"Click to track {recipe.Name}. Shift+Click to add its materials to your current tracked list.";
        }

        var now = DateTime.UtcNow;
        if (recipeChanged || (now - lastHoldingsRefreshUtc).TotalMilliseconds >= 600)
        {
            lastHoldingsRefreshUtc = now;
            SyncIngredientLabels(addon, recipe);
        }
    }

    private void SyncIngredientLabels(AddonRecipeNote* addon, ActiveRecipeInfo recipe)
    {
        var ingredientsContainer = addon->GetNodeById(IngredientsContainerNodeId);
        var containerVisible = ingredientsContainer != null && ingredientsContainer->IsVisible();
        if (containerVisible)
        {
            tracker.RefreshActiveRetainerSnapshot();
        }

        for (var i = 0; i < IngredientSlotCount; i++)
        {
            var labelNode = retainerLabels[i];
            if (labelNode == null)
            {
                continue;
            }

            if (!containerVisible || i >= recipe.Ingredients.Count)
            {
                if (labelNode.IsVisible)
                {
                    labelNode.IsVisible = false;
                    lastLabelTexts[i] = string.Empty;
                }

                continue;
            }

            var text = tracker.GetIngredientOwnershipLabel(recipe.Ingredients[i].ItemId, captureRetainer: false);
            if (string.IsNullOrEmpty(text))
            {
                if (labelNode.IsVisible)
                {
                    labelNode.IsVisible = false;
                    lastLabelTexts[i] = string.Empty;
                }

                continue;
            }

            if (!labelNode.IsVisible)
            {
                labelNode.IsVisible = true;
            }

            if (lastLabelTexts[i] != text)
            {
                lastLabelTexts[i] = text;
                labelNode.String = text;
            }
        }
    }

    private void HideAllIngredientLabels()
    {
        for (var i = 0; i < IngredientSlotCount; i++)
        {
            var labelNode = retainerLabels[i];
            if (labelNode is { IsVisible: true })
            {
                labelNode.IsVisible = false;
                lastLabelTexts[i] = string.Empty;
            }
        }
    }

    private void OnTrackClicked()
    {
        var addon = (AddonRecipeNote*)NativeUiHelpers.GetReadyAddon(gameGui, "RecipeNote");
        if (addon == null)
        {
            return;
        }

        var recipe = tracker.GetActiveRecipe(addon);
        if (recipe == null)
        {
            return;
        }

        if (ImGui.GetIO().KeyShift)
        {
            tracker.AddRecipeToList(recipe);
        }
        else
        {
            tracker.ToggleRecipe(recipe);
        }

        lastTrackedState = null;
        SyncState(addon);
    }

    private void OnFinalize(AddonRecipeNote* _)
    {
        DetachNodes();
        attachedAddonAddress = nint.Zero;
        setupAttemptedForInstance = false;
    }

    private void DetachNodes()
    {
        for (var i = 0; i < IngredientSlotCount; i++)
        {
            var label = retainerLabels[i];
            if (label != null)
            {
                label.DetachNode();
                label.Dispose();
                retainerLabels[i] = null;
            }

            lastLabelTexts[i] = string.Empty;
        }

        if (trackButton != null)
        {
            trackButton.DetachNode();
            trackButton.Dispose();
            trackButton = null;
        }

        lastRecipeId = 0;
        lastTrackedState = null;
        lastHoldingsRefreshUtc = DateTime.MinValue;
    }

    public void Dispose()
    {
        controller.Disable();
        DetachNodes();
        controller.Dispose();
    }
}
