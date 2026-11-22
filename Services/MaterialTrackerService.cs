using Dalamud.Game.ClientState.Conditions;
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
    IReadOnlyList<RecipeIngredientRequirement> Ingredients,
    IReadOnlyList<RecipeIngredientRequirement> Crystals)
{
    public IEnumerable<RecipeIngredientRequirement> AllRequirements => Ingredients.Concat(Crystals);
}

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
    public int UnownedMissingCount => Math.Max(0, MissingCount - ExternalOwnedCount);
    public bool IsComplete => OwnedCount >= NeededCount;
    public bool IsCoveredByHoldings => (OwnedCount + ExternalOwnedCount) >= NeededCount;
}

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

public sealed unsafe class MaterialTrackerService : IDisposable
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IFramework framework;
    private readonly Configuration config;
    private readonly HoldingsService holdings;
    private readonly IDataManager dataManager;
    private readonly IGameGui gameGui;
    private readonly ICondition condition;
    private readonly IChatGui chatGui;

    private static readonly uint[] CraftTypeToClassJobId = [8, 9, 10, 11, 12, 13, 14, 15];

    private DateTime lastSnapshotUtc = DateTime.MinValue;
    private ulong lastSnapshotCharId;
    private TrackerSnapshot cachedSnapshot = new(string.Empty, Array.Empty<TrackedMaterialLine>());
    private bool dirty = true;
    private Dictionary<uint, List<uint>>? recipesByResultItemId;

    private enum PendingCraftStage { None, WaitingForRecipeNote, WaitingForQuickSynthDialog }
    private PendingCraftStage pendingCraftStage = PendingCraftStage.None;
    private uint pendingCraftRecipeId;
    private int pendingCraftCount;
    private bool pendingCraftUseHq;
    private DateTime pendingCraftDeadlineUtc = DateTime.MinValue;
    private DateTime pendingCraftNextTickUtc = DateTime.MinValue;

    public MaterialTrackerService(
        IDalamudPluginInterface pluginInterface,
        IFramework framework,
        Configuration config,
        HoldingsService holdings,
        IDataManager dataManager,
        IGameGui gameGui,
        ICondition condition,
        IChatGui chatGui)
    {
        this.pluginInterface = pluginInterface;
        this.framework = framework;
        this.config = config;
        this.holdings = holdings;
        this.dataManager = dataManager;
        this.gameGui = gameGui;
        this.condition = condition;
        this.chatGui = chatGui;

        this.framework.Update += OnFrameworkUpdate;
    }

    public event System.Action? TrackerChanged;

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        pendingCraftStage = PendingCraftStage.None;
    }

    private void OnFrameworkUpdate(IFramework _) => TickPendingCraft();

    private CharacterProfile ActiveProfile
        => config.GetOrCreateCharacter(InventorySafetyEvaluator.GetCurrentContentId());

    private void NotifyTrackerChanged()
    {
        pendingCraftStage = PendingCraftStage.None;
        dirty = true;
        TrackerChanged?.Invoke();
    }

    private bool IsNonCrystalCurrency(uint baseItemId)
    {
        if (baseItemId is >= 2 and <= 19)
        {
            return false;
        }

        var row = dataManager.GetExcelSheet<Item>()?.GetRowOrDefault(baseItemId);
        return row is not null && ItemIdentity.IsCurrencyOrCrystal(baseItemId, row.Value);
    }

    public bool HasTrackedItems => ActiveProfile.TrackedItems.Count > 0;

    public int TrackedItemCount => ActiveProfile.TrackedItems.Count;

    public bool IsRecipeTracked(uint recipeId)
        => recipeId != 0 && ActiveProfile.TrackedRecipeId == recipeId && ActiveProfile.TrackedItems.Count > 0;

    public bool IsTrackedRecipeResultItem(uint rawItemId)
    {
        var baseId = ItemIdentity.NormalizeBaseItemId(rawItemId);
        var recipeId = ActiveProfile.TrackedRecipeId;
        if (baseId == 0 || recipeId == 0 || ActiveProfile.TrackedItems.Count == 0)
        {
            return false;
        }

        var recipeSheet = dataManager.GetExcelSheet<Recipe>();
        return recipeSheet != null
            && recipeSheet.TryGetRow(recipeId, out var row)
            && row.ItemResult.RowId == baseId;
    }

    public bool IsItemTracked(uint baseItemId)
    {
        var normalized = ItemIdentity.NormalizeBaseItemId(baseItemId);
        return normalized != 0
            && ActiveProfile.TrackedItems.TryGetValue(normalized, out var needed)
            && needed > 0;
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
                    profile.TrackedCraftCount = 1;
                    break;
            }
        }

        config.Save(pluginInterface);
        NotifyTrackerChanged();
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
            var baseId = ItemIdentity.NormalizeBaseItemId(rawId);
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

    public void RefreshActiveRetainerSnapshot()
        => holdings.CaptureActiveRetainerIfOpen(GetOpenRetainerAddonName() != null);

    public string GetIngredientOwnershipLabel(uint baseItemId, bool captureRetainer = true)
    {
        if (captureRetainer)
        {
            RefreshActiveRetainerSnapshot();
        }

        return holdings.FormatExternalOwnership(baseItemId, maxChars: 26);
    }

    public ActiveRecipeInfo? GetActiveRecipe(AddonRecipeNote* addon)
    {
        if (addon == null || !addon->IsVisible || !addon->IsReady)
        {
            return null;
        }

        if (addon->SelectedRecipeName == null || !addon->SelectedRecipeName->IsVisible())
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

                    var baseId = ItemIdentity.NormalizeBaseItemId(ing.ItemId);
                    if (baseId == 0)
                    {
                        continue;
                    }

                    var ingName = itemSheet.TryGetRow(baseId, out var itemRow)
                        ? itemRow.Name.ExtractText()
                        : ing.Name.ToString();

                    entryIngredients.Add(new RecipeIngredientRequirement(baseId, ingName, ing.Amount));
                }

                var entryCrystals = new List<RecipeIngredientRequirement>(2);
                if (recipeId != 0 && recipeSheet.TryGetRow(recipeId, out var recipeRowForCrystals))
                {
                    var maxIng = Math.Min(recipeRowForCrystals.Ingredient.Count, recipeRowForCrystals.AmountIngredient.Count);
                    for (var c = 6; c < maxIng; c++)
                    {
                        var crysId = recipeRowForCrystals.Ingredient[c].RowId;
                        var crysAmt = recipeRowForCrystals.AmountIngredient[c];
                        if (crysId == 0 || crysAmt == 0)
                        {
                            continue;
                        }

                        var baseId = ItemIdentity.NormalizeBaseItemId(crysId);
                        if (baseId == 0)
                        {
                            continue;
                        }

                        var crysName = itemSheet.TryGetRow(baseId, out var itemRow)
                            ? itemRow.Name.ExtractText()
                            : $"Crystal #{baseId}";
                        entryCrystals.Add(new RecipeIngredientRequirement(baseId, crysName, crysAmt));
                    }
                }

                if (entryIngredients.Count > 0 || entryCrystals.Count > 0)
                {
                    var resultId = ItemIdentity.NormalizeBaseItemId(entry->ItemId);
                    var recipeName = itemSheet.TryGetRow(resultId, out var resRow)
                        ? resRow.Name.ExtractText()
                        : entry->ItemName.ToString();

                    return new ActiveRecipeInfo(recipeId, resultId, recipeName, entryIngredients, entryCrystals);
                }
            }
        }

        if (recipeId == 0 || !recipeSheet.TryGetRow(recipeId, out var row))
        {
            return null;
        }

        var (ingredients, crystals) = ExtractRecipeRequirements(row, itemSheet);
        if (ingredients.Count == 0 && crystals.Count == 0)
        {
            return null;
        }

        var resultItemId = row.ItemResult.RowId;
        var resultName = itemSheet.TryGetRow(resultItemId, out var resultItem)
            ? resultItem.Name.ExtractText()
            : $"Recipe #{recipeId}";

        return new ActiveRecipeInfo(recipeId, resultItemId, resultName, ingredients, crystals);
    }

    private static (List<RecipeIngredientRequirement> Ingredients, List<RecipeIngredientRequirement> Crystals) ExtractRecipeRequirements(
        Recipe row,
        Lumina.Excel.ExcelSheet<Item> itemSheet)
    {
        var ingredients = new List<RecipeIngredientRequirement>(6);
        var crystals = new List<RecipeIngredientRequirement>(2);
        var count = Math.Min(row.Ingredient.Count, row.AmountIngredient.Count);
        for (var i = 0; i < count; i++)
        {
            var itemId = row.Ingredient[i].RowId;
            var amount = row.AmountIngredient[i];
            if (!ItemIdentity.IsValidItemId(itemId) || amount == 0)
            {
                continue;
            }

            var itemName = itemSheet.TryGetRow(itemId, out var itemRow)
                ? itemRow.Name.ExtractText()
                : $"Item #{itemId}";
            var req = new RecipeIngredientRequirement(itemId, itemName, amount);
            if (ItemIdentity.IsCrystalItemId(itemId))
            {
                crystals.Add(req);
            }
            else
            {
                ingredients.Add(req);
            }
        }

        return (ingredients, crystals);
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
        foreach (var req in recipe.AllRequirements)
        {
            profile.TrackedItems[req.ItemId] = profile.TrackedItems.GetValueOrDefault(req.ItemId) + req.Amount;
        }

        profile.TrackedRecipeId = recipe.RecipeId;
        profile.TrackedTitle = recipe.Name;
        profile.TrackedCraftCount = 1;
        profile.TrackWithdrawAllStacks = false;
        config.Save(pluginInterface);
        NotifyTrackerChanged();
    }

    public void AddRecipeToList(ActiveRecipeInfo recipe)
    {
        var profile = ActiveProfile;
        if (profile.TrackedItems.Count == 0)
        {
            ToggleRecipe(recipe);
            return;
        }

        FlattenCraftMultiplierIntoItems(profile);
        foreach (var req in recipe.AllRequirements)
        {
            profile.TrackedItems[req.ItemId] = profile.TrackedItems.GetValueOrDefault(req.ItemId) + req.Amount;
        }

        profile.TrackedRecipeId = 0;
        profile.TrackedTitle = $"Tracked List ({profile.TrackedItems.Count} items)";
        profile.TrackWithdrawAllStacks = false;
        config.Save(pluginInterface);
        NotifyTrackerChanged();
    }

    private Dictionary<uint, List<uint>> GetRecipeLookup()
    {
        if (recipesByResultItemId != null)
        {
            return recipesByResultItemId;
        }

        var map = new Dictionary<uint, List<uint>>(8192);
        var recipeSheet = dataManager.GetExcelSheet<Recipe>();
        if (recipeSheet != null)
        {
            foreach (var row in recipeSheet)
            {
                var resId = row.ItemResult.RowId;
                if (resId == 0 || row.CraftType.RowId >= CraftTypeToClassJobId.Length)
                {
                    continue;
                }

                if (!map.TryGetValue(resId, out var list))
                {
                    list = new List<uint>(1);
                    map[resId] = list;
                }

                list.Add(row.RowId);
            }
        }

        recipesByResultItemId = map;
        return map;
    }

    public bool HasRecipeForItem(uint rawItemId)
    {
        var baseId = ItemIdentity.NormalizeBaseItemId(rawItemId);
        return baseId != 0 && GetRecipeLookup().ContainsKey(baseId);
    }

    private int GetRecipeJobPriorityRank(Recipe row, PlayerState* ps, Lumina.Excel.ExcelSheet<ClassJob>? classJobSheet, uint currentJobId, out int jobLevel)
    {
        jobLevel = 0;
        if (row.CraftType.RowId >= CraftTypeToClassJobId.Length)
        {
            return 4;
        }

        var reqJobId = CraftTypeToClassJobId[row.CraftType.RowId];
        var reqLv = row.RecipeLevelTable.ValueNullable?.ClassJobLevel ?? 1;
        if (ps != null && classJobSheet != null && classJobSheet.TryGetRow(reqJobId, out var jobRow)
            && jobRow.ExpArrayIndex >= 0 && jobRow.ExpArrayIndex < ps->ClassJobLevels.Length)
        {
            jobLevel = ps->ClassJobLevels[jobRow.ExpArrayIndex];
        }

        if (reqJobId == currentJobId && jobLevel >= reqLv)
        {
            return 0;
        }

        if (jobLevel >= reqLv && jobLevel > 0)
        {
            return 1;
        }

        if (reqJobId == currentJobId)
        {
            return 2;
        }

        return jobLevel > 0 ? 3 : 4;
    }

    private bool TryResolveBestRecipeForItem(uint baseId, out Recipe recipe)
    {
        recipe = default;
        var recipeSheet = dataManager.GetExcelSheet<Recipe>();
        var classJobSheet = dataManager.GetExcelSheet<ClassJob>();
        if (baseId == 0 || recipeSheet == null || !GetRecipeLookup().TryGetValue(baseId, out var recipeIds))
        {
            return false;
        }

        var ps = PlayerState.Instance();
        var currentJobId = ps != null ? (uint)ps->CurrentClassJobId : 0u;
        Recipe? chosen = null;
        var bestKey = (Rank: int.MaxValue, NegLevel: int.MaxValue, RowId: uint.MaxValue);

        foreach (var rid in recipeIds)
        {
            if (!recipeSheet.TryGetRow(rid, out var row) || row.CraftType.RowId >= CraftTypeToClassJobId.Length)
            {
                continue;
            }

            var rank = GetRecipeJobPriorityRank(row, ps, classJobSheet, currentJobId, out var jobLevel);
            var key = (rank, -jobLevel, row.RowId);
            if (chosen == null || key.CompareTo(bestKey) < 0)
            {
                chosen = row;
                bestKey = key;
            }
        }

        if (chosen is null)
        {
            return false;
        }

        recipe = chosen.Value;
        return true;
    }

    private static void FlattenCraftMultiplierIntoItems(CharacterProfile profile)
    {
        var mult = profile.TrackWithdrawAllStacks ? 1 : Math.Max(1, profile.TrackedCraftCount);
        if (mult > 1 && profile.TrackedItems.Count > 0)
        {
            foreach (var key in profile.TrackedItems.Keys.ToArray())
            {
                profile.TrackedItems[key] = profile.TrackedItems[key] * mult;
            }
        }

        profile.TrackedCraftCount = 1;
        profile.TrackWithdrawAllStacks = false;
    }

    public bool TrackRecipeForResultItem(uint rawItemId, out string statusMessage)
        => TrackRecipeForResultItem(rawItemId, addToExistingList: false, out statusMessage);

    public bool AddRecipeToList(uint rawItemId, out string statusMessage)
        => TrackRecipeForResultItem(rawItemId, addToExistingList: true, out statusMessage);

    private bool TrackRecipeForResultItem(uint rawItemId, bool addToExistingList, out string statusMessage)
    {
        var baseId = ItemIdentity.NormalizeBaseItemId(rawItemId);
        var itemSheet = dataManager.GetExcelSheet<Item>();
        var classJobSheet = dataManager.GetExcelSheet<ClassJob>();
        if (baseId == 0 || itemSheet == null)
        {
            statusMessage = "Recipe data is not available.";
            return false;
        }

        if (!TryResolveBestRecipeForItem(baseId, out var recipe))
        {
            statusMessage = "No crafting recipe found for this item.";
            return false;
        }

        var (reqIngs, reqCrystals) = ExtractRecipeRequirements(recipe, itemSheet);
        var ingredients = new Dictionary<uint, int>(reqIngs.Count + reqCrystals.Count);
        foreach (var req in reqIngs.Concat(reqCrystals))
        {
            ingredients[req.ItemId] = ingredients.GetValueOrDefault(req.ItemId) + req.Amount;
        }

        if (ingredients.Count == 0)
        {
            statusMessage = "This recipe has no trackable ingredients.";
            return false;
        }

        var recipeName = itemSheet.TryGetRow(baseId, out var resItem) ? resItem.Name.ExtractText() : $"Recipe #{recipe.RowId}";
        var profile = ActiveProfile;
        var mergingIntoExisting = addToExistingList && profile.TrackedItems.Count > 0;

        if (mergingIntoExisting)
        {
            FlattenCraftMultiplierIntoItems(profile);
            foreach (var (ingId, amt) in ingredients)
            {
                profile.TrackedItems[ingId] = profile.TrackedItems.GetValueOrDefault(ingId) + amt;
            }

            profile.TrackedRecipeId = 0;
            profile.TrackedTitle = $"Tracked List ({profile.TrackedItems.Count} items)";
            profile.TrackWithdrawAllStacks = false;
            config.Save(pluginInterface);
            NotifyTrackerChanged();
            statusMessage = $"Added {recipeName} ingredients to tracked list ({profile.TrackedItems.Count} items).";
            return true;
        }

        profile.TrackedItems.Clear();
        foreach (var (ingId, amt) in ingredients)
        {
            profile.TrackedItems[ingId] = amt;
        }

        profile.TrackedRecipeId = recipe.RowId;
        profile.TrackedTitle = recipeName;
        profile.TrackedCraftCount = 1;
        profile.TrackWithdrawAllStacks = false;
        config.Save(pluginInterface);
        NotifyTrackerChanged();

        var reqJobId = CraftTypeToClassJobId[recipe.CraftType.RowId];
        var jobAbbr = classJobSheet != null && classJobSheet.TryGetRow(reqJobId, out var jobRow)
            ? jobRow.Abbreviation.ExtractText()
            : "DoH";
        var reqLv = recipe.RecipeLevelTable.ValueNullable?.ClassJobLevel ?? 1;
        statusMessage = $"Tracking recipe: {recipeName} x1 ({jobAbbr} Lv. {reqLv}).";
        return true;
    }

    public void AddItemToList(uint rawItemId, out string statusMessage)
    {
        var baseItemId = ItemIdentity.NormalizeBaseItemId(rawItemId);
        if (baseItemId == 0 || IsNonCrystalCurrency(baseItemId))
        {
            statusMessage = "Currencies cannot be tracked in the Material Tracker.";
            return;
        }

        var itemSheet = dataManager.GetExcelSheet<Item>();
        var itemName = itemSheet != null && itemSheet.TryGetRow(baseItemId, out var row)
            ? row.Name.ExtractText()
            : $"Item #{baseItemId}";

        var profile = ActiveProfile;
        if (profile.TrackedItems.Count == 0)
        {
            TrackItemOnly(rawItemId, out statusMessage);
            return;
        }

        FlattenCraftMultiplierIntoItems(profile);
        if (profile.TrackedItems.TryGetValue(baseItemId, out var existing))
        {
            profile.TrackedItems[baseItemId] = Math.Max(1, existing) + 1;
            statusMessage = $"Updated {itemName} in tracked list (target: {profile.TrackedItems[baseItemId]}).";
        }
        else
        {
            var summary = holdings.GetHoldingSummary(baseItemId);
            profile.TrackedItems[baseItemId] = ComputeSensibleInitialTarget(summary);
            statusMessage = $"Added {itemName} to tracked list ({profile.TrackedItems.Count} items).";
        }

        profile.TrackedRecipeId = 0;
        profile.TrackedTitle = profile.TrackedItems.Count == 1
            ? itemName
            : $"Tracked List ({profile.TrackedItems.Count} items)";
        profile.TrackWithdrawAllStacks = false;
        config.Save(pluginInterface);
        NotifyTrackerChanged();
    }

    public void TrackItemOnly(uint rawItemId, out string statusMessage)
    {
        var baseItemId = ItemIdentity.NormalizeBaseItemId(rawItemId);
        if (baseItemId == 0 || IsNonCrystalCurrency(baseItemId))
        {
            statusMessage = "Currencies cannot be tracked in the Material Tracker.";
            return;
        }

        var itemSheet = dataManager.GetExcelSheet<Item>();
        var itemName = itemSheet != null && itemSheet.TryGetRow(baseItemId, out var row)
            ? row.Name.ExtractText()
            : $"Item #{baseItemId}";

        var summary = holdings.GetHoldingSummary(baseItemId);
        var profile = ActiveProfile;
        profile.TrackedItems.Clear();
        profile.TrackedRecipeId = 0;
        profile.TrackedTitle = itemName;
        profile.TrackedCraftCount = 1;
        profile.TrackWithdrawAllStacks = false;
        profile.TrackedItems[baseItemId] = ComputeSensibleInitialTarget(summary);
        config.Save(pluginInterface);
        NotifyTrackerChanged();
        statusMessage = $"Tracking only: {itemName}.";
    }

    public static int ComputeSensibleInitialTarget(ItemHoldingSummary summary)
    {
        if (summary.TotalExternalCount > 0)
        {
            return Math.Max(1, summary.BagCount + summary.TotalExternalCount);
        }

        return Math.Max(1, summary.BagCount);
    }

    public void RemoveItemFromList(uint rawItemId, out string statusMessage)
    {
        var baseItemId = ItemIdentity.NormalizeBaseItemId(rawItemId);
        var profile = ActiveProfile;
        if (baseItemId == 0 || !profile.TrackedItems.Remove(baseItemId))
        {
            statusMessage = string.Empty;
            return;
        }

        var itemSheet = dataManager.GetExcelSheet<Item>();
        var itemName = itemSheet != null && itemSheet.TryGetRow(baseItemId, out var row)
            ? row.Name.ExtractText()
            : $"Item #{baseItemId}";

        if (profile.TrackedItems.Count == 0)
        {
            profile.TrackedRecipeId = 0;
            profile.TrackedTitle = string.Empty;
            profile.TrackedCraftCount = 1;
            profile.TrackWithdrawAllStacks = false;
            statusMessage = $"Removed {itemName}; tracked list is now empty.";
        }
        else
        {
            profile.TrackedRecipeId = 0;
            if (profile.TrackedItems.Count == 1)
            {
                var remainingId = profile.TrackedItems.Keys.First();
                profile.TrackedTitle = itemSheet != null && itemSheet.TryGetRow(remainingId, out var remRow)
                    ? remRow.Name.ExtractText()
                    : $"Item #{remainingId}";
            }
            else
            {
                profile.TrackedTitle = $"Tracked List ({profile.TrackedItems.Count} items)";
            }

            statusMessage = $"Removed {itemName} from tracked list ({profile.TrackedItems.Count} left).";
        }

        config.Save(pluginInterface);
        NotifyTrackerChanged();
    }

    public void ClearAll()
    {
        var profile = ActiveProfile;
        if (profile.TrackedItems.Count == 0
            && profile.TrackedRecipeId == 0
            && string.IsNullOrEmpty(profile.TrackedTitle)
            && profile.TrackedCraftCount == 1
            && !profile.TrackWithdrawAllStacks)
        {
            pendingCraftStage = PendingCraftStage.None;
            return;
        }

        profile.TrackedItems.Clear();
        profile.TrackedRecipeId = 0;
        profile.TrackedTitle = string.Empty;
        profile.TrackedCraftCount = 1;
        profile.TrackWithdrawAllStacks = false;
        config.Save(pluginInterface);
        NotifyTrackerChanged();
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

        Recipe? bestRecipe = null;
        var bestKey = (Tier: int.MaxValue, JobRank: int.MaxValue, NameLen: int.MaxValue, NegLevel: int.MaxValue, RowId: uint.MaxValue);

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

            int tier;
            if (string.Equals(resName, query, StringComparison.OrdinalIgnoreCase))
            {
                tier = 0;
            }
            else if (resName.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            {
                tier = 1;
            }
            else if (resName.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                tier = 2;
            }
            else
            {
                continue;
            }

            var jobRank = GetRecipeJobPriorityRank(row, ps, classJobSheet, currentJobId, out var jobLevel);
            var key = (tier, jobRank, resName.Length, -jobLevel, row.RowId);
            if (bestRecipe == null || key.CompareTo(bestKey) < 0)
            {
                bestRecipe = row;
                bestKey = key;
            }
        }

        if (bestRecipe is null)
        {
            statusMessage = $"No crafting recipe found matching \"{query}\".";
            return false;
        }

        var recipe = bestRecipe.Value;
        var (reqIngs, reqCrystals) = ExtractRecipeRequirements(recipe, itemSheet);
        var ingredients = new Dictionary<uint, int>(reqIngs.Count + reqCrystals.Count);
        foreach (var req in reqIngs.Concat(reqCrystals))
        {
            ingredients[req.ItemId] = ingredients.GetValueOrDefault(req.ItemId) + req.Amount;
        }

        if (ingredients.Count == 0)
        {
            statusMessage = $"Recipe #{recipe.RowId} has no trackable ingredients.";
            return false;
        }

        var profile = ActiveProfile;
        profile.TrackedItems.Clear();
        foreach (var (ingId, amt) in ingredients)
        {
            profile.TrackedItems[ingId] = amt;
        }

        var recipeName = itemSheet.TryGetRow(recipe.ItemResult.RowId, out var chosenItem)
            ? chosenItem.Name.ExtractText()
            : query;
        profile.TrackedRecipeId = recipe.RowId;
        profile.TrackedTitle = recipeName;
        profile.TrackedCraftCount = craftCount;
        profile.TrackWithdrawAllStacks = false;
        config.Save(pluginInterface);
        NotifyTrackerChanged();

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
        if (!IsCraftConditionSafe(out var condReason))
        {
            message = condReason;
            return false;
        }

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

        var res = gm->EquipGearset(snap.MatchingGearsetId);
        if (res != 0)
        {
            message = $"Cannot switch to {snap.RequiredJobAbbr} right now.";
            return false;
        }

        dirty = true;
        message = $"Switched to {snap.RequiredJobAbbr}.";
        return true;
    }

    private bool IsCraftConditionSafe(out string reason)
    {
        if (condition[ConditionFlag.Crafting]
            || condition[ConditionFlag.ExecutingCraftingAction]
            || condition[ConditionFlag.PreparingToCraft])
        {
            reason = "Already crafting.";
            return false;
        }

        if (condition[ConditionFlag.InCombat])
        {
            reason = "Cannot craft while in combat.";
            return false;
        }

        if (condition[ConditionFlag.Mounted] || condition[ConditionFlag.RidingPillion])
        {
            reason = "Dismount before crafting.";
            return false;
        }

        if (condition[ConditionFlag.BetweenAreas]
            || condition[ConditionFlag.BetweenAreas51]
            || condition[ConditionFlag.OccupiedInEvent]
            || condition[ConditionFlag.OccupiedInQuestEvent]
            || condition[ConditionFlag.OccupiedInCutSceneEvent])
        {
            reason = "Cannot craft while busy or zoning.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public bool StartCraftTracked(out string message)
    {
        if (!IsCraftConditionSafe(out var condReason))
        {
            message = condReason;
            return false;
        }

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
            pendingCraftUseHq = HasAnyHqTrackedIngredientsInBags(snap.Lines);
            pendingCraftStage = PendingCraftStage.WaitingForRecipeNote;
            pendingCraftNextTickUtc = DateTime.UtcNow.AddMilliseconds(550);
            pendingCraftDeadlineUtc = DateTime.UtcNow.AddSeconds(6);
            var capNote = snap.CraftCount > 99 ? " (Quick Synthesis max is 99 per batch)" : string.Empty;
            message = $"Opening {snap.Title} (x{pendingCraftCount}) for Quick Synthesis...{capNote}";
        }
        else
        {
            pendingCraftStage = PendingCraftStage.None;
            message = $"Opened {snap.Title} in Crafting Log.";
        }

        return true;
    }

    private static bool HasAnyHqTrackedIngredientsInBags(IReadOnlyList<TrackedMaterialLine> lines)
    {
        var manager = InventoryManager.Instance();
        if (manager == null)
        {
            return false;
        }

        foreach (var line in lines)
        {
            if (ItemIdentity.IsCrystalItemId(line.ItemId))
            {
                continue;
            }

            if (manager->GetInventoryItemCount(line.ItemId, isHq: true, checkEquipped: false, checkArmory: false) > 0)
            {
                return true;
            }
        }

        return false;
    }

    public bool IsPendingCraftActive => pendingCraftStage != PendingCraftStage.None;

    public bool StopPendingCraft()
    {
        var wasActive = pendingCraftStage != PendingCraftStage.None;
        pendingCraftStage = PendingCraftStage.None;
        return wasActive;
    }

    public void TickPendingCraft()
    {
        if (pendingCraftStage == PendingCraftStage.None)
        {
            return;
        }

        if (!IsCraftConditionSafe(out _))
        {
            pendingCraftStage = PendingCraftStage.None;
            return;
        }

        var now = DateTime.UtcNow;
        if (now > pendingCraftDeadlineUtc)
        {
            pendingCraftStage = PendingCraftStage.None;
            chatGui.PrintError("Quick Synthesis timed out waiting for the Crafting Log.");
            return;
        }

        if (now < pendingCraftNextTickUtc)
        {
            return;
        }

        if (pendingCraftStage == PendingCraftStage.WaitingForRecipeNote)
        {
            var rn = (AddonRecipeNote*)NativeUiHelpers.GetReadyAddon(gameGui, "RecipeNote");
            if (rn == null)
            {
                pendingCraftNextTickUtc = now.AddMilliseconds(250);
                return;
            }

            var activeRecipe = GetActiveRecipe(rn);
            if (activeRecipe == null || activeRecipe.RecipeId != pendingCraftRecipeId)
            {
                pendingCraftNextTickUtc = now.AddMilliseconds(250);
                return;
            }

            var btn = rn->QuickSynthesisButton;
            if (btn == null || btn->AtkComponentBase.OwnerNode == null || !btn->AtkComponentBase.OwnerNode->AtkResNode.IsVisible())
            {
                pendingCraftStage = PendingCraftStage.None;
                chatGui.PrintError("Quick Synthesis is not available for this recipe. Craft it from the Crafting Log.");
                return;
            }

            if (!btn->IsEnabled)
            {
                pendingCraftStage = PendingCraftStage.None;
                chatGui.PrintError("Quick Synthesis is unavailable (unlock Quick Synthesis or craft manually from the Crafting Log).");
                return;
            }

            if (NativeUiHelpers.ClickAddonButton((AtkUnitBase*)rn, btn))
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

            var args = stackalloc AtkValue[3];
            args[0] = NativeUiHelpers.IntValue(pendingCraftCount);
            args[1] = NativeUiHelpers.BoolValue(pendingCraftUseHq);
            args[2] = NativeUiHelpers.BoolValue(true);
            NativeUiHelpers.FireCallback(dialog, true, new ReadOnlySpan<AtkValue>(args, 3));
            pendingCraftStage = PendingCraftStage.None;
        }
    }

    public TrackerSnapshot GetSnapshot()
    {
        var now = DateTime.UtcNow;
        var cid = InventorySafetyEvaluator.GetCurrentContentId();
        if (!dirty && cid == lastSnapshotCharId && (now - lastSnapshotUtc).TotalMilliseconds < 180)
        {
            return cachedSnapshot;
        }

        dirty = false;
        lastSnapshotUtc = now;
        lastSnapshotCharId = cid;

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
        var craftMult = withdrawAll ? 1 : Math.Max(1, profile.TrackedCraftCount);
        var maxCraftableFromBags = int.MaxValue;

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

            var bagCraftable = summary.BagCount / baseNeeded;
            if (bagCraftable < maxCraftableFromBags)
            {
                maxCraftableFromBags = bagCraftable;
            }

            var retainerNote = string.Empty;
            if (summary.BagCount < effectiveNeeded && (summary.Retainers.Count > 0 || summary.SaddlebagCount > 0))
            {
                var parts = summary.Retainers.Select(r => $"{r.RetainerName}: {r.Quantity}").ToList();
                if (summary.SaddlebagCount > 0)
                {
                    parts.Add($"Saddlebag: {summary.SaddlebagCount}");
                }

                var rawNote = string.Join(", ", parts);
                retainerNote = rawNote.Length > 38 ? rawNote[..35] + "..." : rawNote;
            }

            lines.Add(new TrackedMaterialLine(
                itemId,
                itemName,
                summary.BagCount,
                effectiveNeeded,
                summary.TotalExternalCount,
                retainerNote,
                openRetainerQty));
        }

        var effectiveCraftCount = withdrawAll
            ? (maxCraftableFromBags == int.MaxValue ? 1 : Math.Clamp(maxCraftableFromBags, 1, 99))
            : craftMult;
        var hasCraftableMats = withdrawAll
            ? (lines.Count > 0 && maxCraftableFromBags >= 1 && maxCraftableFromBags != int.MaxValue)
            : (lines.Count > 0 && lines.All(l => l.IsComplete));

        var title = ResolveTrackedTitle(lines);
        ResolveRecipeMetadata(
            profile.TrackedRecipeId,
            hasCraftableMats,
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
            effectiveCraftCount,
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
        bool hasCraftableMats,
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
        hasAllMaterialsInBag = hasCraftableMats;
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
                var bestIlvl = -1;
                for (var i = 0; i < gm->Entries.Length; i++)
                {
                    var entry = gm->GetGearset(i);
                    if (entry != null
                        && entry->Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists)
                        && entry->ClassJob == reqJobId
                        && entry->ItemLevel > bestIlvl)
                    {
                        bestIlvl = entry->ItemLevel;
                        matchingGearsetId = i;
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
                && active.AllRequirements.Count() == profile.TrackedItems.Count
                && active.AllRequirements.All(i => profile.TrackedItems.TryGetValue(i.ItemId, out var q) && q == i.Amount))
            {
                return active.Name;
            }
        }

        return lines.Count == 1 ? lines[0].Name : $"Tracked List ({lines.Count} items)";
    }

    public string? GetOpenRetainerAddonName()
        => NativeUiHelpers.GetOpenRetainerAddonName(gameGui);
}
