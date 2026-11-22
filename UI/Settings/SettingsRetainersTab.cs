using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using MagicInventory.Core;
using MagicInventory.Services;

namespace MagicInventory.UI.Settings;

internal sealed class SettingsRetainersTab
{
    private static readonly (uint RowId, string FallbackName)[] SpecialtyCategoryDefs =
    [
        (ItemIdentity.UiCatIngredient, "Ingredient"),
        (ItemIdentity.UiCatSeafood, "Seafood"),
        (ItemIdentity.UiCatMeal, "Meal"),
        (ItemIdentity.UiCatMedicine, "Medicine"),
        (ItemIdentity.UiCatReagent, "Reagent"),
        (ItemIdentity.UiCatCatalyst, "Catalyst"),
        (ItemIdentity.UiCatGardening, "Gardening"),
        (ItemIdentity.UiCatFishingTackle, "Fishing Tackle"),
        (ItemIdentity.UiCatStone, "Stone"),
        (ItemIdentity.UiCatMetal, "Metal"),
        (ItemIdentity.UiCatLumber, "Lumber"),
        (ItemIdentity.UiCatCloth, "Cloth"),
        (ItemIdentity.UiCatLeather, "Leather"),
        (ItemIdentity.UiCatBone, "Bone"),
        (ItemIdentity.UiCatPart, "Part"),
        (ItemIdentity.UiCatMateria, "Materia"),
        (ItemIdentity.UiCatDye, "Dye"),
        (ItemIdentity.UiCatFurnishing, "Furnishing"),
        (ItemIdentity.UiCatDemimateria, "Demimateria"),
        (ItemIdentity.UiCatMiscellany, "Miscellany"),
    ];

    private readonly SettingsUiContext ctx;
    private readonly (uint RowId, string Name)[] specialtyCategories;
    private readonly Dictionary<uint, string> uiCategoryNamesById;

    public SettingsRetainersTab(SettingsUiContext ctx)
    {
        this.ctx = ctx;
        specialtyCategories = BuildLocalizedSpecialtyCategories(ctx.DataManager);
        uiCategoryNamesById = BuildUiCategoryNameMap(ctx.DataManager);
    }

    public void Draw(ulong cid)
    {
        var retainers = ctx.Holdings.GetKnownRetainersForCurrentCharacter();
        var profile = ctx.Config.GetOrCreateCharacter(cid);

        if (retainers.Count == 0)
        {
            ImGui.TextColored(SettingsUiContext.ColorGold, "No retainers found yet for this character.");
            ImGui.TextColored(SettingsUiContext.ColorMuted, "Open a Summoning Bell once and visit each retainer to see their bags.");
            return;
        }

        ImGui.TextWrapped("Pick which categories each retainer should hold. Anything unchecked follows where you already keep most of that type, and overflows to another retainer with free space when full.");
        ImGui.Spacing();

        var ownerByCatId = new Dictionary<uint, (ulong RetainerId, string Name)>();
        foreach (var r in retainers)
        {
            if (profile.RetainerSpecialtyCategoryIds.TryGetValue(r.RetainerId, out var catIds))
            {
                foreach (var catId in catIds)
                {
                    ownerByCatId[catId] = (r.RetainerId, r.Name);
                }
            }
        }

        foreach (var ret in retainers)
        {
            ImGui.PushID(ret.RetainerId.ToString());
            var status = !ret.IsVisited
                ? "Not visited yet"
                : (ret.UsedBagSlots >= HoldingsService.MaxRetainerBagSlots ? $"{ret.UsedBagSlots}/175 slots (FULL)" : $"{ret.UsedBagSlots}/175 slots");
            var header = $"{ret.Name}  ({status})  |  Market: {ret.MarketItemCount}/20###ret_{ret.RetainerId}";
            if (ImGui.CollapsingHeader(header, ImGuiTreeNodeFlags.DefaultOpen))
            {
                var drewTopLine = false;
                if (ret.CategoryCounts.Count > 0)
                {
                    var topCats = ret.CategoryCounts
                        .OrderByDescending(kv => kv.Value)
                        .Take(6)
                        .Select(kv =>
                        {
                            var catName = uiCategoryNamesById.GetValueOrDefault(kv.Key, $"Category #{kv.Key}");
                            return $"{catName} ({kv.Value})";
                        });
                    ImGui.TextColored(SettingsUiContext.ColorMuted, $"Mostly holds: {string.Join(", ", topCats)}");
                    drewTopLine = true;
                }

                profile.RetainerSpecialtyCategoryIds.TryGetValue(ret.RetainerId, out var assigned);
                var hasCustom = assigned is { Count: > 0 };
                if (hasCustom)
                {
                    if (drewTopLine)
                    {
                        ImGui.SameLine();
                    }

                    if (ImGui.SmallButton("Reset to Auto"))
                    {
                        profile.RetainerSpecialtyCategoryIds.Remove(ret.RetainerId);
                        profile.RetainerSpecialties.Remove(ret.RetainerId);
                        SaveAndRefresh();
                    }
                }

                ImGui.Spacing();
                const int cols = 4;
                if (ImGui.BeginTable($"SpecTable_{ret.RetainerId}", cols, ImGuiTableFlags.SizingStretchSame))
                {
                    for (var i = 0; i < specialtyCategories.Length; i++)
                    {
                        if (i % cols == 0)
                        {
                            ImGui.TableNextRow();
                        }

                        ImGui.TableSetColumnIndex(i % cols);
                        var (catId, cat) = specialtyCategories[i];
                        var isChecked = assigned != null && assigned.Contains(catId);
                        var ownedByOther = !isChecked
                            && ownerByCatId.TryGetValue(catId, out var otherOwner)
                            && otherOwner.RetainerId != ret.RetainerId;
                        var label = ownedByOther ? $"{cat}*" : cat;

                        if (ImGui.Checkbox($"{label}##spec_{catId}", ref isChecked))
                        {
                            var set = Configuration.GetOrCreateRetainerSpecialtyCategoryIds(profile, ret.RetainerId);
                            if (isChecked)
                            {
                                foreach (var otherRetId in profile.RetainerSpecialtyCategoryIds.Keys.ToList())
                                {
                                    if (otherRetId != ret.RetainerId)
                                    {
                                        profile.RetainerSpecialtyCategoryIds[otherRetId].Remove(catId);
                                    }
                                }

                                set.Add(catId);
                            }
                            else
                            {
                                set.Remove(catId);
                            }

                            SaveAndRefresh();
                        }

                        if (ownedByOther && ImGui.IsItemHovered())
                        {
                            ImGui.SetTooltip($"Currently assigned to {ownerByCatId[catId].Name}. Checking this moves {cat} to {ret.Name}.");
                        }
                    }

                    ImGui.EndTable();
                }

                ImGui.Spacing();
            }

            ImGui.PopID();
        }
    }

    private void SaveAndRefresh()
    {
        ctx.Config.Save(ctx.PluginInterface);
        ctx.TriageEngine.Invalidate();
        ctx.MagicSort.InvalidateButtonCache();
        ctx.InvalidateLocalCaches();
    }

    private static (uint RowId, string Name)[] BuildLocalizedSpecialtyCategories(IDataManager dataManager)
    {
        var sheet = dataManager.GetExcelSheet<ItemUICategory>();
        var list = new (uint RowId, string Name)[SpecialtyCategoryDefs.Length];
        for (var i = 0; i < SpecialtyCategoryDefs.Length; i++)
        {
            var (rowId, fallback) = SpecialtyCategoryDefs[i];
            var localized = sheet?.GetRowOrDefault(rowId)?.Name.ExtractText();
            list[i] = (rowId, string.IsNullOrWhiteSpace(localized) ? fallback : localized);
        }

        return list;
    }

    private static Dictionary<uint, string> BuildUiCategoryNameMap(IDataManager dataManager)
    {
        var map = new Dictionary<uint, string>(128);
        var sheet = dataManager.GetExcelSheet<ItemUICategory>();
        if (sheet == null)
        {
            return map;
        }

        foreach (var row in sheet)
        {
            if (row.RowId == 0)
            {
                continue;
            }

            var name = row.Name.ExtractText();
            if (!string.IsNullOrWhiteSpace(name))
            {
                map[row.RowId] = name;
            }
        }

        return map;
    }
}
