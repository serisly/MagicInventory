using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using MagicInventory.Core;

namespace MagicInventory.Services;

public sealed record RecipeIngredientRequirement(uint ItemId, string Name, int Amount);

public sealed record ActiveRecipeInfo(
    uint RecipeId,
    uint ResultItemId,
    string Name,
    IReadOnlyList<RecipeIngredientRequirement> Ingredients);

public sealed record TrackedMaterialLine(
    uint ItemId,
    string Name,
    int OwnedCount,
    int NeededCount,
    int ExternalOwnedCount,
    string RetainerNote,
    int OpenRetainerCount)
{
    public int MissingCount => Math.Max(0, NeededCount - OwnedCount);
    public bool IsComplete => OwnedCount >= NeededCount;
    public bool IsCoveredByHoldings => (OwnedCount + ExternalOwnedCount) >= NeededCount;
}

public sealed record ContextButtonState(
    bool IsVisible,
    bool IsEnabled,
    string Label,
    string Tooltip);

public sealed record TrackerSnapshot(
    string Title,
    IReadOnlyList<TrackedMaterialLine> Lines,
    string CraftCountLabel = "x1",
    bool WithdrawAllStacks = false,
    uint RecipeId = 0,
    int CraftCount = 1,
    string RequiredJobAbbr = "",
    int RequiredLevel = 0,
    int PlayerJobLevel = 0,
    bool IsOnRequiredJob = true,
    bool HasAllMaterialsInBag = false,
    int MatchingGearsetId = -1,
    bool CanQuickSynth = false);

/// <summary>
/// Pure read-only material & recipe tracking state service (per-character isolated).
/// All actual retainer withdrawals and Market Board purchases are executed through
/// RetainerSortPlanner and MarketBoardBuyPlanner with strict Server-ACK pacing.
/// </summary>
public sealed unsafe class MaterialTrackerService
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly Configuration config;
    private readonly HoldingsService holdings;
    private readonly IDataManager dataManager;
    private readonly IGameGui gameGui;

    // CraftType 0..7 -> ClassJob RowId (CRP=8, BSM=9, ARM=10, GSM=11, LTW=12, WVR=13, ALC=14, CUL=15)
    private static readonly uint[] CraftTypeToClassJobId = [8, 9, 10, 11, 12, 13, 14, 15];

    private DateTime lastSnapshotUtc = DateTime.MinValue;
    private TrackerSnapshot cachedSnapshot = new(string.Empty, Array.Empty<TrackedMaterialLine>());
    private bool dirty = true;

    // Self-terminating native Quick Synthesis starter (zero idle polling; 6s hard timeout)
    private enum PendingCraftStage { None, WaitingForRecipeNote, WaitingForQuickSynthDialog }
    private PendingCraftStage pendingCraftStage = PendingCraftStage.None;
    private uint pendingCraftRecipeId;
    private int pendingCraftCount;
    private DateTime pendingCraftDeadlineUtc = DateTime.MinValue;
    private DateTime pendingCraftNextTickUtc = DateTime.MinValue;

    public MaterialTrackerService(
        IDalamudPluginInterface pluginInterface,
        Configuration config,
        HoldingsService holdings,
        IDataManager dataManager,
        IGameGui gameGui)
    {
        this.pluginInterface = pluginInterface;
        this.config = config;
        this.holdings = holdings;
        this.dataManager = dataManager;
        this.gameGui = gameGui;
    }

    private CharacterProfile ActiveProfile
        => config.GetOrCreateCharacter(InventorySafetyEvaluator.GetCurrentContentId());

    public bool HasTrackedItems => ActiveProfile.TrackedItems.Count > 0;

    public int TrackedItemCount => ActiveProfile.TrackedItems.Count;

    public bool IsItemInActiveTracker(uint baseItemId)
    {
        var normalized = HoldingsService.NormalizeBaseItemId(baseItemId);
        return normalized != 0
            && ActiveProfile.TrackedItems.TryGetValue(normalized, out var needed)
            && needed > 0;
    }

    public bool IsRecipeTracked(uint recipeId)
        => recipeId != 0 && ActiveProfile.TrackedRecipeId == recipeId && ActiveProfile.TrackedItems.Count > 0;

    public bool IsItemTracked(uint baseItemId)
    {
        var normalized = HoldingsService.NormalizeBaseItemId(baseItemId);
        return normalized != 0
            && ActiveProfile.TrackedRecipeId == 0
            && ActiveProfile.TrackedItems.Count == 1
            && ActiveProfile.TrackedItems.ContainsKey(normalized);
    }

    public string CraftCountModeLabel
        => ActiveProfile.TrackWithdrawAllStacks ? "All" : $"x{Math.Max(1, ActiveProfile.TrackedCraftCount)}";

    public void CycleCraftCount()
    {
        var profile = ActiveProfile;
        if (profile.TrackWithdrawAllStacks)
        {
            profile.TrackWithdrawAllStacks = false;
            profile.TrackedCraftCount = 1;
        }
        else
        {
            switch (profile.TrackedCraftCount)
            {
                case <= 1:
                    profile.TrackedCraftCount = 5;
                    break;
                case < 10:
                    profile.TrackedCraftCount = 10;
                    break;
                case < 20:
                    profile.TrackedCraftCount = 20;
                    break;
                default:
                    profile.TrackWithdrawAllStacks = true;
                    break;
            }
        }

        config.Save(pluginInterface);
        dirty = true;
    }

    public Dictionary<uint, int> GetMissingTrackedForRetainerWithdraw()
    {
        var missing = new Dictionary<uint, int>();
        var profile = ActiveProfile;
        if (profile.TrackedItems.Count == 0)
        {
            return missing;
        }

        var withdrawAll = profile.TrackWithdrawAllStacks;
        var mult = Math.Max(1, profile.TrackedCraftCount);

        foreach (var (rawId, baseNeeded) in profile.TrackedItems)
        {
            var baseId = HoldingsService.NormalizeBaseItemId(rawId);
            if (baseId == 0 || baseNeeded <= 0)
            {
                continue;
            }

            if (withdrawAll)
            {
                missing[baseId] = int.MaxValue;
                continue;
            }

            var needed = baseNeeded * mult;
            var bagCount = HoldingsService.GetLiveBagCount(baseId);
            var delta = needed - bagCount;
            if (delta > 0)
            {
                missing[baseId] = delta;
            }
        }

        return missing;
    }

    public void MarkDirty() => dirty = true;

    public string GetIngredientOwnershipLabel(uint baseItemId)
    {
        holdings.CaptureActiveRetainerIfOpen(GetOpenRetainerAddonName() != null);
        return holdings.FormatExternalOwnership(baseItemId, maxChars: 26);
    }

    public ActiveRecipeInfo? GetActiveRecipe(AddonRecipeNote* addon)
    {
        if (addon == null || !addon->IsVisible || !addon->IsReady)
        {
            return null;
        }

        var detailHeader = addon->GetNodeById(59);
        if (detailHeader == null || !detailHeader->IsVisible())
        {
            return null;
        }

        var recipeSheet = dataManager.GetExcelSheet<Recipe>();
        var itemSheet = dataManager.GetExcelSheet<Item>();

        uint recipeId = 0;
        var nativeRecipeNote = FFXIVClientStructs.FFXIV.Client.Game.UI.RecipeNote.Instance();
        if (nativeRecipeNote != null
            && nativeRecipeNote->RecipeList != null
            && nativeRecipeNote->RecipeList->Recipes != null
            && nativeRecipeNote->RecipeList->RecipeCount > 0
            && nativeRecipeNote->RecipeList->SelectedIndex < nativeRecipeNote->RecipeList->RecipeCount)
        {
            var entry = nativeRecipeNote->RecipeList->SelectedRecipe;
            if (entry != null && entry->RecipeId != 0)
            {
                recipeId = entry->RecipeId;

                var entryIngredients = new List<RecipeIngredientRequirement>(6);
                foreach (var ing in entry->Ingredients)
                {
                    if (ing.ItemId == 0 || ing.Amount == 0)
                    {
                        continue;
                    }

                    var baseId = HoldingsService.NormalizeBaseItemId(ing.ItemId);
                    var ingName = itemSheet.TryGetRow(baseId, out var itemRow)
                        ? itemRow.Name.ExtractText()
                        : ing.Name.ToString();

                    entryIngredients.Add(new RecipeIngredientRequirement(baseId, ingName, ing.Amount));
                }

                if (entryIngredients.Count > 0)
                {
                    var resultId = HoldingsService.NormalizeBaseItemId(entry->ItemId);
                    var recipeName = itemSheet.TryGetRow(resultId, out var resRow)
                        ? resRow.Name.ExtractText()
                        : entry->ItemName.ToString();

                    return new ActiveRecipeInfo(recipeId, resultId, recipeName, entryIngredients);
                }
            }
        }

        if (recipeId == 0)
        {
            var agent = AgentRecipeNote.Instance();
            if (agent != null && agent->ActiveCraftRecipeId != 0)
            {
                recipeId = agent->ActiveCraftRecipeId;
            }
            else if (nativeRecipeNote != null && nativeRecipeNote->ActiveCraftRecipeId != 0)
            {
                recipeId = nativeRecipeNote->ActiveCraftRecipeId;
            }
        }

        if (recipeId == 0 || !recipeSheet.TryGetRow(recipeId, out var row))
        {
            return null;
        }

        var ingredients = new List<RecipeIngredientRequirement>(6);
        var count = Math.Min(6, Math.Min(row.Ingredient.Count, row.AmountIngredient.Count));
        for (var i = 0; i < count; i++)
        {
            var itemId = row.Ingredient[i].RowId;
            var amount = row.AmountIngredient[i];
            if (itemId == 0 || amount == 0)
            {
                continue;
            }

            var itemName = itemSheet.TryGetRow(itemId, out var itemRow)
                ? itemRow.Name.ExtractText()
                : $"Item #{itemId}";
            ingredients.Add(new RecipeIngredientRequirement(itemId, itemName, amount));
        }

        if (ingredients.Count == 0)
        {
            return null;
        }

        var resultItemId = row.ItemResult.RowId;
        var resultName = itemSheet.TryGetRow(resultItemId, out var resultItem)
            ? resultItem.Name.ExtractText()
            : $"Recipe #{recipeId}";

        return new ActiveRecipeInfo(recipeId, resultItemId, resultName, ingredients);
    }

    public void ToggleRecipe(ActiveRecipeInfo recipe)
    {
        if (IsRecipeTracked(recipe.RecipeId))
        {
            ClearAll();
            return;
        }

        var profile = ActiveProfile;
        profile.TrackedItems.Clear();
        foreach (var ing in recipe.Ingredients)
        {
            profile.TrackedItems[ing.ItemId] = profile.TrackedItems.GetValueOrDefault(ing.ItemId) + ing.Amount;
        }

        profile.TrackedRecipeId = recipe.RecipeId;
        profile.TrackedTitle = recipe.Name;
        config.Save(pluginInterface);
        dirty = true;
    }

    public void ToggleSingleItem(uint rawItemId)
    {
        var baseItemId = HoldingsService.NormalizeBaseItemId(rawItemId);
        if (baseItemId == 0)
        {
            return;
        }

        if (IsItemTracked(baseItemId))
        {
            ClearAll();
            return;
        }

        var itemSheet = dataManager.GetExcelSheet<Item>();
        var itemName = itemSheet.TryGetRow(baseItemId, out var row)
            ? row.Name.ExtractText()
            : $"Item #{baseItemId}";

        var summary = holdings.GetHoldingSummary(baseItemId);
        var profile = ActiveProfile;
        profile.TrackedItems.Clear();
        profile.TrackedRecipeId = 0;
        profile.TrackedTitle = itemName;
        profile.TrackedCraftCount = 1;
        profile.TrackWithdrawAllStacks = false;
        profile.TrackedItems[baseItemId] = Math.Max(summary.BagCount + 1, 1);
        config.Save(pluginInterface);
        dirty = true;
    }

    public void ClearAll()
    {
        pendingCraftStage = PendingCraftStage.None;
        var profile = ActiveProfile;
        if (profile.TrackedItems.Count == 0 && profile.TrackedRecipeId == 0 && string.IsNullOrEmpty(profile.TrackedTitle))
        {
            return;
        }

        profile.TrackedItems.Clear();
        profile.TrackedRecipeId = 0;
        profile.TrackedTitle = string.Empty;
        config.Save(pluginInterface);
        dirty = true;
    }

    public bool TrackRecipeByQuery(string rawArgs, out string statusMessage)
    {
        var trimmed = (rawArgs ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            if (HasTrackedItems)
            {
                var snap = GetSnapshot();
                statusMessage = $"Currently tracking: {snap.Title} ({snap.CraftCountLabel}). Use '/m track clear' to clear.";
                return true;
            }

            statusMessage = "Usage: /m track <recipe name> [amount]  (example: /m track fish soup 5)";
            return false;
        }

        if (string.Equals(trimmed, "clear", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "stop", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "none", StringComparison.OrdinalIgnoreCase))
        {
            ClearAll();
            statusMessage = "Cleared tracked recipe.";
            return true;
        }

        var craftCount = 1;
        var query = trimmed;
        var lastSpace = trimmed.LastIndexOf(' ');
        if (lastSpace > 0)
        {
            var tail = trimmed[(lastSpace + 1)..].Trim().TrimStart('x', 'X');
            if (int.TryParse(tail, out var parsedCount) && parsedCount >= 1 && parsedCount <= 999)
            {
                craftCount = parsedCount;
                query = trimmed[..lastSpace].Trim();
            }
        }

        var recipeSheet = dataManager.GetExcelSheet<Recipe>();
        var itemSheet = dataManager.GetExcelSheet<Item>();
        var classJobSheet = dataManager.GetExcelSheet<ClassJob>();
        if (recipeSheet == null || itemSheet == null)
        {
            statusMessage = "Recipe data is not available yet.";
            return false;
        }

        var ps = PlayerState.Instance();
        var currentJobId = ps != null ? (uint)ps->CurrentClassJobId : 0u;

        Recipe? bestExact = null;
        Recipe? bestPrefix = null;
        Recipe? bestContains = null;

        foreach (var row in recipeSheet)
        {
            var resultId = row.ItemResult.RowId;
            if (resultId == 0 || row.CraftType.RowId >= CraftTypeToClassJobId.Length)
            {
                continue;
            }

            if (!itemSheet.TryGetRow(resultId, out var resItem))
            {
                continue;
            }

            var resName = resItem.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(resName))
            {
                continue;
            }

            var jobMatches = CraftTypeToClassJobId[row.CraftType.RowId] == currentJobId;

            if (string.Equals(resName, query, StringComparison.OrdinalIgnoreCase))
            {
                if (bestExact == null || jobMatches)
                {
                    bestExact = row;
                    if (jobMatches)
                    {
                        break;
                    }
                }
            }
            else if (resName.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            {
                if (bestPrefix == null || jobMatches || resName.Length < itemSheet.GetRow(bestPrefix.Value.ItemResult.RowId).Name.ExtractText().Length)
                {
                    bestPrefix = row;
                }
            }
            else if (resName.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                if (bestContains == null || jobMatches)
                {
                    bestContains = row;
                }
            }
        }

        var chosen = bestExact ?? bestPrefix ?? bestContains;
        if (chosen is null)
        {
            statusMessage = $"No crafting recipe found matching \"{query}\".";
            return false;
        }

        var recipe = chosen.Value;
        var profile = ActiveProfile;
        profile.TrackedItems.Clear();

        var ingCount = Math.Min(6, Math.Min(recipe.Ingredient.Count, recipe.AmountIngredient.Count));
        for (var i = 0; i < ingCount; i++)
        {
            var ingId = recipe.Ingredient[i].RowId;
            var amt = recipe.AmountIngredient[i];
            if (ingId == 0 || amt == 0)
            {
                continue;
            }

            profile.TrackedItems[ingId] = profile.TrackedItems.GetValueOrDefault(ingId) + amt;
        }

        if (profile.TrackedItems.Count == 0)
        {
            statusMessage = $"Recipe #{recipe.RowId} has no trackable ingredients.";
            return false;
        }

        var recipeName = itemSheet.TryGetRow(recipe.ItemResult.RowId, out var chosenItem)
            ? chosenItem.Name.ExtractText()
            : query;
        profile.TrackedRecipeId = recipe.RowId;
        profile.TrackedTitle = recipeName;
        profile.TrackedCraftCount = craftCount;
        profile.TrackWithdrawAllStacks = false;
        config.Save(pluginInterface);
        dirty = true;

        var reqJobId = CraftTypeToClassJobId[recipe.CraftType.RowId];
        var jobAbbr = classJobSheet != null && classJobSheet.TryGetRow(reqJobId, out var jobRow)
            ? jobRow.Abbreviation.ExtractText()
            : "DoH";
        var reqLv = recipe.RecipeLevelTable.ValueNullable?.ClassJobLevel ?? 1;
        statusMessage = $"Tracking {recipeName} x{craftCount} ({jobAbbr} Lv. {reqLv}).";
        return true;
    }

    public bool SwitchToTrackedJob(out string message)
    {
        var snap = GetSnapshot();
        if (snap.MatchingGearsetId < 0)
        {
            message = $"No gearset found for {snap.RequiredJobAbbr}.";
            return false;
        }

        var gm = RaptureGearsetModule.Instance();
        if (gm == null)
        {
            message = "Gearset manager is unavailable.";
            return false;
        }

        gm->EquipGearset(snap.MatchingGearsetId);
        dirty = true;
        message = $"Switched to {snap.RequiredJobAbbr}.";
        return true;
    }

    public bool StartCraftTracked(out string message)
    {
        var snap = GetSnapshot();
        if (snap.RecipeId == 0)
        {
            message = "No crafting recipe is currently tracked.";
            return false;
        }

        if (!snap.IsOnRequiredJob)
        {
            message = $"Switch to {snap.RequiredJobAbbr} first (requires Lv. {snap.RequiredLevel}).";
            return false;
        }

        if (snap.PlayerJobLevel < snap.RequiredLevel)
        {
            message = $"{snap.RequiredJobAbbr} Lv. {snap.RequiredLevel} required (currently Lv. {snap.PlayerJobLevel}).";
            return false;
        }

        if (!snap.HasAllMaterialsInBag)
        {
            message = "Still missing tracked materials in your bags.";
            return false;
        }

        var agent = AgentRecipeNote.Instance();
        if (agent == null)
        {
            message = "Crafting Log is unavailable right now.";
            return false;
        }

        agent->OpenRecipeByRecipeId(snap.RecipeId);
        if (snap.CanQuickSynth)
        {
            pendingCraftRecipeId = snap.RecipeId;
            pendingCraftCount = Math.Clamp(snap.CraftCount, 1, 99);
            pendingCraftStage = PendingCraftStage.WaitingForRecipeNote;
            pendingCraftNextTickUtc = DateTime.UtcNow.AddMilliseconds(550);
            pendingCraftDeadlineUtc = DateTime.UtcNow.AddSeconds(6);
            message = $"Opening {snap.Title} (x{pendingCraftCount}) for Quick Synthesis...";
        }
        else
        {
            pendingCraftStage = PendingCraftStage.None;
            message = $"Opened {snap.Title} in Crafting Log.";
        }

        return true;
    }

    public void StopPendingCraft() => pendingCraftStage = PendingCraftStage.None;

    public void TickPendingCraft()
    {
        if (pendingCraftStage == PendingCraftStage.None)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (now > pendingCraftDeadlineUtc)
        {
            pendingCraftStage = PendingCraftStage.None;
            return;
        }

        if (now < pendingCraftNextTickUtc)
        {
            return;
        }

        if (pendingCraftStage == PendingCraftStage.WaitingForRecipeNote)
        {
            var rn = NativeUiHelpers.GetReadyAddon(gameGui, "RecipeNote");
            if (rn == null)
            {
                pendingCraftNextTickUtc = now.AddMilliseconds(250);
                return;
            }

            // Native Quick Synthesis button on RecipeNote is NodeId 103
            var qsNode = rn->GetNodeById(103);
            if (qsNode == null || !qsNode->IsVisible())
            {
                pendingCraftStage = PendingCraftStage.None;
                return;
            }

            var btn = qsNode->GetAsAtkComponentButton();
            if (btn == null || !btn->IsEnabled)
            {
                // Quick Synthesis locked/unavailable for this recipe; leave Crafting Log cleanly open on the recipe
                pendingCraftStage = PendingCraftStage.None;
                return;
            }

            var args = stackalloc AtkValue[1];
            args[0] = NativeUiHelpers.IntValue(13);
            if (NativeUiHelpers.FireCallback(rn, true, new ReadOnlySpan<AtkValue>(args, 1)))
            {
                pendingCraftStage = PendingCraftStage.WaitingForQuickSynthDialog;
                pendingCraftNextTickUtc = now.AddMilliseconds(550);
                return;
            }

            pendingCraftStage = PendingCraftStage.None;
            return;
        }

        if (pendingCraftStage == PendingCraftStage.WaitingForQuickSynthDialog)
        {
            var dialog = NativeUiHelpers.GetReadyAddon(gameGui, "SynthesisSimpleDialog");
            if (dialog == null)
            {
                pendingCraftNextTickUtc = now.AddMilliseconds(250);
                return;
            }

            var args = stackalloc AtkValue[2];
            args[0] = NativeUiHelpers.IntValue(pendingCraftCount);
            args[1] = NativeUiHelpers.IntValue(0);
            NativeUiHelpers.FireCallback(dialog, true, new ReadOnlySpan<AtkValue>(args, 2));
            pendingCraftStage = PendingCraftStage.None;
        }
    }

    public TrackerSnapshot GetSnapshot()
    {
        var now = DateTime.UtcNow;
        if (!dirty && (now - lastSnapshotUtc).TotalMilliseconds < 180)
        {
            return cachedSnapshot;
        }

        dirty = false;
        lastSnapshotUtc = now;

        var profile = ActiveProfile;
        if (profile.TrackedItems.Count == 0)
        {
            cachedSnapshot = new TrackerSnapshot(string.Empty, Array.Empty<TrackedMaterialLine>());
            return cachedSnapshot;
        }

        var openRetainerAddon = GetOpenRetainerAddonName();
        var isRetainerOpen = openRetainerAddon != null;
        holdings.CaptureActiveRetainerIfOpen(isRetainerOpen);

        var itemSheet = dataManager.GetExcelSheet<Item>();
        var lines = new List<TrackedMaterialLine>(profile.TrackedItems.Count);
        var withdrawAll = profile.TrackWithdrawAllStacks;
        var craftMult = Math.Max(1, profile.TrackedCraftCount);

        foreach (var (itemId, baseNeeded) in profile.TrackedItems)
        {
            if (itemId == 0 || baseNeeded <= 0)
            {
                continue;
            }

            var itemName = itemSheet.TryGetRow(itemId, out var row)
                ? row.Name.ExtractText()
                : $"Item #{itemId}";

            var summary = holdings.GetHoldingSummary(itemId);
            var openRetainerQty = isRetainerOpen ? HoldingsService.GetLiveActiveRetainerCount(itemId) : 0;
            var effectiveNeeded = withdrawAll
                ? Math.Max(baseNeeded, summary.BagCount + summary.TotalRetainerCount)
                : baseNeeded * craftMult;

            var retainerNote = summary.BagCount < effectiveNeeded && summary.Retainers.Count > 0
                ? string.Join(", ", summary.Retainers.Select(r => $"{r.RetainerName}: {r.Quantity}"))
                : string.Empty;

            lines.Add(new TrackedMaterialLine(
                itemId,
                itemName,
                summary.BagCount,
                effectiveNeeded,
                summary.TotalRetainerCount,
                retainerNote,
                openRetainerQty));
        }

        var title = ResolveTrackedTitle(lines);
        ResolveRecipeMetadata(
            profile.TrackedRecipeId,
            lines,
            out var reqJobAbbr,
            out var reqLevel,
            out var playerJobLevel,
            out var isOnReqJob,
            out var hasAllInBag,
            out var matchingGearsetId,
            out var canQuickSynth);

        cachedSnapshot = new TrackerSnapshot(
            title,
            lines,
            CraftCountModeLabel,
            withdrawAll,
            profile.TrackedRecipeId,
            craftMult,
            reqJobAbbr,
            reqLevel,
            playerJobLevel,
            isOnReqJob,
            hasAllInBag,
            matchingGearsetId,
            canQuickSynth);
        return cachedSnapshot;
    }

    private void ResolveRecipeMetadata(
        uint recipeId,
        List<TrackedMaterialLine> lines,
        out string requiredJobAbbr,
        out int requiredLevel,
        out int playerJobLevel,
        out bool isOnRequiredJob,
        out bool hasAllMaterialsInBag,
        out int matchingGearsetId,
        out bool canQuickSynth)
    {
        requiredJobAbbr = string.Empty;
        requiredLevel = 0;
        playerJobLevel = 0;
        isOnRequiredJob = true;
        hasAllMaterialsInBag = lines.Count > 0 && lines.All(l => l.IsComplete);
        matchingGearsetId = -1;
        canQuickSynth = false;

        if (recipeId == 0)
        {
            return;
        }

        var recipeSheet = dataManager.GetExcelSheet<Recipe>();
        var classJobSheet = dataManager.GetExcelSheet<ClassJob>();
        if (recipeSheet == null || !recipeSheet.TryGetRow(recipeId, out var recipe) || recipe.CraftType.RowId >= CraftTypeToClassJobId.Length)
        {
            return;
        }

        var reqJobId = CraftTypeToClassJobId[recipe.CraftType.RowId];
        requiredLevel = recipe.RecipeLevelTable.ValueNullable?.ClassJobLevel ?? 1;
        canQuickSynth = recipe.CanQuickSynth;

        if (classJobSheet != null && classJobSheet.TryGetRow(reqJobId, out var jobRow))
        {
            requiredJobAbbr = jobRow.Abbreviation.ExtractText();
            var ps = PlayerState.Instance();
            if (ps != null)
            {
                isOnRequiredJob = ps->CurrentClassJobId == reqJobId;
                if (jobRow.ExpArrayIndex >= 0 && jobRow.ExpArrayIndex < ps->ClassJobLevels.Length)
                {
                    playerJobLevel = ps->ClassJobLevels[jobRow.ExpArrayIndex];
                }
            }
        }

        if (!isOnRequiredJob)
        {
            var gm = RaptureGearsetModule.Instance();
            if (gm != null)
            {
                for (var i = 0; i < 100; i++)
                {
                    var entry = gm->GetGearset(i);
                    if (entry != null && entry->Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists) && entry->ClassJob == reqJobId)
                    {
                        matchingGearsetId = i;
                        break;
                    }
                }
            }
        }
    }

    private string ResolveTrackedTitle(List<TrackedMaterialLine> lines)
    {
        var profile = ActiveProfile;
        if (!string.IsNullOrWhiteSpace(profile.TrackedTitle))
        {
            return profile.TrackedTitle;
        }

        var rnPtr = gameGui.GetAddonByName("RecipeNote");
        if (!rnPtr.IsNull)
        {
            var active = GetActiveRecipe((AddonRecipeNote*)rnPtr.Address);
            if (active != null
                && active.Ingredients.Count == profile.TrackedItems.Count
                && active.Ingredients.All(i => profile.TrackedItems.TryGetValue(i.ItemId, out var q) && q == i.Amount))
            {
                profile.TrackedRecipeId = active.RecipeId;
                profile.TrackedTitle = active.Name;
                config.Save(pluginInterface);
                return active.Name;
            }
        }

        return lines.Count == 1 ? lines[0].Name : "Tracked Materials";
    }

    public string? GetOpenRetainerAddonName()
    {
        if (IsAddonOpenAndReady("InventoryRetainerLarge"))
        {
            return "InventoryRetainerLarge";
        }

        if (IsAddonOpenAndReady("InventoryRetainer"))
        {
            return "InventoryRetainer";
        }

        return null;
    }

    public bool IsAddonOpenAndReady(string addonName)
        => NativeUiHelpers.IsAddonOpenAndReady(gameGui, addonName);
}
