// <copyright file="StashUtilityCore.EquipmentUI.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace StashUtility
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Numerics;
    using ImGuiNET;
    using StashUtility.Models;

    public sealed partial class StashUtilityCore
    {
        private readonly Dictionary<string, string> equipmentSearchTerms = new();
        private readonly Dictionary<string, string> equipmentSelectedModIds = new();
        private readonly Dictionary<string, int> equipmentAffixFilters = new();
        private readonly Dictionary<string, string> equipmentDebugTargetGroupIds = new();
        private readonly Dictionary<string, Dictionary<string, int[]>> equipmentObservedMods = new();
        private Dictionary<string, List<EquipmentMod>> equipmentDatabaseByCategory;

        private void DrawEquipmentManagerSettings()
        {
            Settings.Equipment.Normalize();
            if (ImGui.Checkbox(PluginText.Label("stashutility.equipment.enabled", "Enable Equipment Manager", "equipment_enabled"), ref Settings.Equipment.Enabled)) SaveSettings();
            if (!ImGui.CollapsingHeader(PluginText.Title("stashutility.equipment.manager", "Equipment Mod Filter Manager", "equipment_manager"))) return;

            ImGui.Indent();
            ImGui.TextWrapped(PluginText.T("stashutility.equipment.rules_hint", "Groups use OR. Each group counts distinct exact Explicit Mod IDs; enabled Raw minimums use AND. Empty groups never match."));
            ImGui.TextWrapped(PluginText.T("stashutility.equipment.raw_hint", "Thresholds use game Raw units, not displayed percentages. For example, a displayed 7.13% may have a Raw value of 713. No Tier conversion is used."));
            ImGui.TextDisabled(PluginText.F("stashutility.equipment.data_version", "Equipment database version: {0}", Data.ModDatabase.EquipmentSourceVersion));
            if (!string.IsNullOrWhiteSpace(Data.ModDatabase.EquipmentLoadError)) ImGui.TextWrapped(Data.ModDatabase.EquipmentLoadError);
            RememberEquipmentDebugMods();

            if (ImGui.BeginTabBar("EquipmentCategoryTabs"))
            {
                foreach (var category in EquipmentCategories.All)
                {
                    if (!ImGui.BeginTabItem(EquipmentCategoryLabel(category) + "###equipment_category_" + category)) continue;
                    ImGui.PushID(category);
                    var groups = GetEquipmentGroups(category);
                    if (ImGui.Button(PluginText.Label("stashutility.equipment.add_group", "+ Add Profile Group", "add_equipment_group")))
                    {
                        groups.Add(CreateEquipmentGroup(groups.Count + 1));
                        SaveSettings();
                    }

                    if (groups.Count == 0) ImGui.TextDisabled(PluginText.T("stashutility.equipment.no_groups", "No profile groups. Add a group or create one from the Debug inspector."));
                    for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
                    {
                        var group = groups[groupIndex];
                        ImGui.PushID(group.Id);
                        var name = string.IsNullOrWhiteSpace(group.Name) ? PluginText.F("stashutility.equipment.default_group", "Profile Group {0}", groupIndex + 1) : group.Name;
                        var header = PluginText.F("stashutility.equipment.group_header", "{0} (Min Match: {1})", name, group.MinMatchCount);
                        if (ImGui.CollapsingHeader(header + "###equipment_group", ImGuiTreeNodeFlags.DefaultOpen))
                        {
                            ImGui.Indent(10f);
                            var nameBuffer = group.Name ?? string.Empty;
                            ImGui.SetNextItemWidth(220f);
                            if (ImGui.InputText(PluginText.Label("stashutility.equipment.group_name", "Group Name", "equipment_group_name"), ref nameBuffer, 100))
                            {
                                group.Name = nameBuffer;
                                SaveSettings();
                            }

                            ImGui.SameLine();
                            if (ImGui.Button(PluginText.Label("stashutility.equipment.delete_group", "Delete Group", "equipment_delete_group")))
                            {
                                groups.RemoveAt(groupIndex);
                                SaveSettings();
                                ImGui.Unindent(10f);
                                ImGui.PopID();
                                break;
                            }

                            ImGui.SameLine();
                            if (ImGui.Button(PluginText.Label("stashutility.equipment.save", "Save", "equipment_save_group"))) SaveSettings();
                            var selectedCount = group.ModPatternIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).Count();
                            var minimum = group.MinMatchCount;
                            ImGui.SetNextItemWidth(180f);
                            if (ImGui.SliderInt(PluginText.Label("stashutility.equipment.min_match", "Min Match Count", "equipment_min_match"), ref minimum, 1, Math.Max(1, selectedCount)))
                            {
                                group.MinMatchCount = minimum;
                                SaveSettings();
                            }

                            if (selectedCount == 0 || minimum < 1 || minimum > selectedCount)
                                ImGui.TextColored(new Vector4(1f, 0.6f, 0.25f, 1f), PluginText.T("stashutility.equipment.invalid_group", "This group cannot match: select modifiers and set Min Match Count within the selected count."));
                            DrawEquipmentModPicker(category, group);
                            DrawEquipmentSelectedMods(category, group);
                            ImGui.Unindent(10f);
                        }

                        ImGui.PopID();
                        ImGui.Spacing();
                    }

                    ImGui.PopID();
                    ImGui.EndTabItem();
                }

                ImGui.EndTabBar();
            }

            if (ImGui.TreeNode(PluginText.Label("stashutility.equipment.visuals", "Equipment Highlight Settings", "equipment_visuals")))
            {
                if (ImGui.ColorEdit4(PluginText.Label("stashutility.equipment.color", "Highlight Color", "equipment_color"), ref Settings.Equipment.HighlightColor)) SaveSettings();
                if (ImGui.SliderFloat(PluginText.Label("stashutility.equipment.thickness", "Border Thickness", "equipment_thickness"), ref Settings.Equipment.BorderThickness, 1f, 10f)) SaveSettings();
                if (ImGui.SliderFloat(PluginText.Label("stashutility.equipment.margin", "Border Margin", "equipment_margin"), ref Settings.Equipment.BorderMargin, 0f, 10f)) SaveSettings();
                var styles = new[] { PluginText.T("stashutility.style_solid", "Solid"), PluginText.T("stashutility.style_dashed", "Dashed"), PluginText.T("stashutility.style_dotted", "Dotted") };
                if (ImGui.Combo(PluginText.Label("stashutility.equipment.style", "Border Style", "equipment_style"), ref Settings.Equipment.FrameStyle, styles, styles.Length)) SaveSettings();
                ImGui.TreePop();
            }

            ImGui.Unindent();
        }

        private void DrawEquipmentModPicker(string category, EquipmentGroup group)
        {
            var key = category + ":" + group.Id;
            equipmentSearchTerms.TryGetValue(key, out var search);
            search ??= string.Empty;
            ImGui.SetNextItemWidth(300f);
            if (ImGui.InputText(PluginText.Label("stashutility.equipment.search", "Search Name / Full Mod ID", "equipment_search"), ref search, 160))
            {
                equipmentSearchTerms[key] = search;
                equipmentSelectedModIds.Remove(key);
            }

            equipmentAffixFilters.TryGetValue(key, out var affixFilter);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(130f);
            var types = new[] { PluginText.T("stashutility.equipment.all_affixes", "All Affixes"), PluginText.T("stashutility.equipment.prefix", "Prefix"), PluginText.T("stashutility.equipment.suffix", "Suffix") };
            if (ImGui.Combo("##equipment_affix_filter", ref affixFilter, types, types.Length))
            {
                equipmentAffixFilters[key] = affixFilter;
                equipmentSelectedModIds.Remove(key);
            }

            var database = GetEquipmentDatabase(category);
            var candidates = database.Where(mod =>
                (affixFilter == 0 || string.Equals(mod.Type, affixFilter == 1 ? "Prefix" : "Suffix", StringComparison.Ordinal)) &&
                (string.IsNullOrWhiteSpace(search) || EquipmentModContains(mod, search))).ToList();
            equipmentSelectedModIds.TryGetValue(key, out var selectedId);
            var selected = candidates.FirstOrDefault(mod => string.Equals(mod.Id, selectedId, StringComparison.Ordinal)) ?? candidates.FirstOrDefault();

            if (candidates.Count > 0)
            {
                ImGui.SetNextItemWidth(500f);
                if (ImGui.BeginCombo(PluginText.Label("stashutility.equipment.select_mod", "Select Modifier", "equipment_select_mod"), EquipmentModCaption(selected)))
                {
                    foreach (var mod in candidates.Take(400))
                    {
                        var isSelected = ReferenceEquals(mod, selected);
                        if (ImGui.Selectable(EquipmentModCaption(mod) + "##" + mod.Id, isSelected))
                        {
                            equipmentSelectedModIds[key] = mod.Id;
                            selected = mod;
                        }
                        if (isSelected) ImGui.SetItemDefaultFocus();
                    }
                    if (candidates.Count > 400) ImGui.TextDisabled(PluginText.T("stashutility.equipment.refine_search", "Showing the first 400 results. Refine the search to find other modifiers."));
                    ImGui.EndCombo();
                }

                DrawEquipmentModDetails(selected);
                var canAdd = selected.IsExactMatch && !string.IsNullOrWhiteSpace(selected.GameModId) && selected.RawValuesAreGameUnits;
                ImGui.BeginDisabled(!canAdd || group.ModPatternIds.Contains(selected.GameModId, StringComparer.Ordinal));
                if (ImGui.Button(PluginText.Label("stashutility.equipment.add_mod", "Add Exact Mod ID", "equipment_add_mod"))) AddEquipmentModToGroup(group, selected.GameModId, null);
                ImGui.EndDisabled();
                if (!canAdd) ImGui.TextDisabled(PluginText.T("stashutility.equipment.unverified", "This source record has no reliable internal ID / Raw mapping and cannot be added for exact matching."));
            }
            else
            {
                ImGui.TextDisabled(PluginText.T("stashutility.equipment.no_results", "No database modifiers match this search. Use Debug to add an observed full internal Mod ID."));
            }

            var fallbackIds = GetEquipmentGroups(category).SelectMany(g => g.ModPatternIds);
            if (equipmentObservedMods.TryGetValue(category, out var observed)) fallbackIds = fallbackIds.Concat(observed.Keys);
            var observedIds = fallbackIds.Where(id => !string.IsNullOrWhiteSpace(id) && (string.IsNullOrWhiteSpace(search) || id.Contains(search, StringComparison.OrdinalIgnoreCase)))
                .Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToList();
            if (observedIds.Count > 0 && ImGui.TreeNode(PluginText.Label("stashutility.equipment.observed_ids", "Debug / Saved Exact IDs", "equipment_observed_ids")))
            {
                foreach (var id in observedIds)
                {
                    ImGui.PushID("observed:" + id);
                    ImGui.BeginDisabled(group.ModPatternIds.Contains(id, StringComparer.Ordinal));
                    if (ImGui.SmallButton(PluginText.Label("stashutility.equipment.add_id", "Add ID", "equipment_add_observed"))) AddEquipmentModToGroup(group, id, null);
                    ImGui.EndDisabled();
                    ImGui.SameLine();
                    ImGui.TextUnformatted(id);
                    ImGui.PopID();
                }
                ImGui.TreePop();
            }
        }

        private void DrawEquipmentSelectedMods(string category, EquipmentGroup group)
        {
            ImGui.Separator();
            ImGui.TextColored(new Vector4(0.4f, 0.8f, 1f, 1f), PluginText.F("stashutility.equipment.active_count", "Selected Explicit Modifiers ({0})", group.ModPatternIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).Count()));
            if (group.ModPatternIds.Count == 0)
            {
                ImGui.TextDisabled(PluginText.T("stashutility.equipment.no_mods", "No modifiers selected. This group does not match any equipment."));
                return;
            }

            ImGui.BeginChild("equipment_selected_mods", new Vector2(0, 300), ImGuiChildFlags.Borders);
            foreach (var id in group.ModPatternIds.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToList())
            {
                ImGui.PushID(id);
                if (ImGui.SmallButton(PluginText.Label("stashutility.equipment.remove", "Remove", "equipment_remove_mod")))
                {
                    group.ModPatternIds.RemoveAll(value => string.Equals(value, id, StringComparison.Ordinal));
                    group.ModRequiredMinRolls.Remove(id);
                    group.ModRequiredMinRollsValue1.Remove(id);
                    SaveSettings();
                    ImGui.PopID();
                    continue;
                }

                var mod = GetEquipmentDatabase(category).FirstOrDefault(m => string.Equals(m.GameModId, id, StringComparison.Ordinal) && m.IsExactMatch);
                ImGui.SameLine();
                ImGui.TextUnformatted(mod?.Name ?? id);
                if (mod != null) DrawEquipmentModDetails(mod);
                else ImGui.TextDisabled(PluginText.F("stashutility.equipment.observed_only", "Internal ID: {0} (Debug / saved ID; no database range)", id));
                if (equipmentObservedMods.TryGetValue(category, out var observed) && observed.TryGetValue(id, out var raw))
                    ImGui.TextDisabled(PluginText.F("stashutility.equipment.observed_raw", "Last observed Raw: [{0}]", string.Join(", ", raw)));
                DrawEquipmentMinimum(group.ModRequiredMinRolls, id, 0, mod);
                DrawEquipmentMinimum(group.ModRequiredMinRollsValue1, id, 1, mod);
                ImGui.Separator();
                ImGui.PopID();
            }
            ImGui.EndChild();
        }

        private void DrawEquipmentMinimum(Dictionary<string, float> requirements, string id, int index, EquipmentMod mod)
        {
            var enabled = requirements.TryGetValue(id, out var required);
            var defaultValue = mod?.RawRollRanges != null && mod.RawValuesAreGameUnits && mod.RawRollRanges.Count > index ? mod.RawRollRanges[index].Min : 0;
            if (ImGui.Checkbox(PluginText.F("stashutility.equipment.require_min", "Require Raw[{0}] minimum", index) + "##equipment_require_" + index, ref enabled))
            {
                if (enabled) requirements[id] = required = defaultValue;
                else requirements.Remove(id);
                SaveSettings();
            }
            if (!enabled) return;
            ImGui.SameLine();
            ImGui.SetNextItemWidth(150f);
            var value = required;
            if (ImGui.InputFloat("##equipment_raw_min_" + index, ref value, 1f, 10f, "%.0f") && float.IsFinite(value))
            {
                requirements[id] = value;
                SaveSettings();
            }
        }

        private void DrawEquipmentDebugInspector()
        {
            if (!ImGui.CollapsingHeader(PluginText.Title("stashutility.equipment.debug", "Hovered Equipment Inspector (Debug)", "equipment_debug"))) return;
            ImGui.Checkbox(PluginText.Label("stashutility.equipment.freeze", "Freeze Equipment Sample", "equipment_freeze"), ref freezeHoveredEquipment);
            ImGui.SameLine();
            if (ImGui.Button(PluginText.Label("stashutility.equipment.clear_sample", "Clear Sample", "equipment_clear_sample"))) ClearEquipmentDebugSnapshot();
            ImGui.TextWrapped(PluginText.T("stashutility.equipment.freeze_hint", "Freeze only retains this authoring sample. Live equipment matching always reads fresh data."));
            var snapshot = lastHoveredEquipmentSnapshot;
            if (snapshot == null)
            {
                ImGui.TextWrapped(PluginText.T("stashutility.equipment.hover_hint", "Hover an equipment item in an open inventory or stash to inspect its complete Explicit IDs and Raw values."));
                return;
            }

            RememberEquipmentDebugMods();
            ImGui.TextUnformatted(EquipmentCategoryLabel(snapshot.Category) + " / " + snapshot.BaseName);
            ImGui.TextUnformatted(snapshot.InternalName ?? string.Empty);
            ImGui.TextWrapped(snapshot.Path ?? string.Empty);
            ImGui.TextUnformatted(PluginText.F("stashutility.equipment.address", "Item Address: 0x{0:X}", snapshot.ItemAddress.ToInt64()));
            var groups = GetEquipmentGroups(snapshot.Category);
            if (ImGui.Button(PluginText.Label("stashutility.equipment.create_debug_group", "Create Profile for This Category", "equipment_debug_create_group")))
            {
                var newGroup = CreateEquipmentGroup(groups.Count + 1);
                groups.Add(newGroup);
                equipmentDebugTargetGroupIds[snapshot.Category] = newGroup.Id;
                SaveSettings();
            }

            if (groups.Count == 0)
            {
                ImGui.TextDisabled(PluginText.T("stashutility.equipment.debug_no_group", "Create a profile group before adding these modifiers."));
                return;
            }

            equipmentDebugTargetGroupIds.TryGetValue(snapshot.Category, out var targetId);
            var target = groups.FirstOrDefault(g => g.Id == targetId) ?? groups[0];
            ImGui.SetNextItemWidth(300f);
            if (ImGui.BeginCombo(PluginText.Label("stashutility.equipment.target_group", "Target Profile Group", "equipment_debug_target"), target.Name))
            {
                foreach (var group in groups)
                {
                    if (ImGui.Selectable((group.Name ?? string.Empty) + "##" + group.Id, ReferenceEquals(group, target)))
                    {
                        equipmentDebugTargetGroupIds[snapshot.Category] = group.Id;
                        target = group;
                    }
                }
                ImGui.EndCombo();
            }

            ImGui.TextDisabled(PluginText.T("stashutility.equipment.debug_add_hint", "Add ID keeps existing thresholds. Add ID + Raw uses both available current values only for a newly added ID."));
            foreach (var mod in snapshot.Mods)
            {
                ImGui.PushID("equipment_debug_" + mod.ModId);
                ImGui.TextUnformatted(mod.ModId + ": [" + string.Join(", ", mod.RawValues) + "]");
                ImGui.BeginDisabled(target.ModPatternIds.Contains(mod.ModId, StringComparer.Ordinal));
                if (ImGui.SmallButton(PluginText.Label("stashutility.equipment.add_id", "Add ID", "equipment_debug_add_id"))) AddEquipmentModToGroup(target, mod.ModId, null);
                ImGui.SameLine();
                if (ImGui.SmallButton(PluginText.Label("stashutility.equipment.add_raw", "Add ID + Raw Minimums", "equipment_debug_add_raw"))) AddEquipmentModToGroup(target, mod.ModId, mod.RawValues);
                ImGui.EndDisabled();
                ImGui.PopID();
            }
        }

        private void RememberEquipmentDebugMods()
        {
            var snapshot = lastHoveredEquipmentSnapshot;
            if (snapshot == null) return;
            if (!equipmentObservedMods.TryGetValue(snapshot.Category, out var observed))
                equipmentObservedMods[snapshot.Category] = observed = new Dictionary<string, int[]>(StringComparer.Ordinal);
            foreach (var mod in snapshot.Mods) observed[mod.ModId] = (int[])mod.RawValues.Clone();
        }

        private void ClearEquipmentUiState()
        {
            equipmentSearchTerms.Clear();
            equipmentSelectedModIds.Clear();
            equipmentAffixFilters.Clear();
            equipmentDebugTargetGroupIds.Clear();
            equipmentObservedMods.Clear();
        }

        private void AddEquipmentModToGroup(EquipmentGroup group, string id, int[] raw)
        {
            if (string.IsNullOrWhiteSpace(id) || group.ModPatternIds.Contains(id, StringComparer.Ordinal)) return;
            group.ModPatternIds.Add(id);
            if (raw?.Length > 0) group.ModRequiredMinRolls[id] = raw[0];
            if (raw?.Length > 1) group.ModRequiredMinRollsValue1[id] = raw[1];
            SaveSettings();
        }

        private List<EquipmentGroup> GetEquipmentGroups(string category)
        {
            if (!Settings.Equipment.CategoryGroups.TryGetValue(category, out var groups) || groups == null)
                Settings.Equipment.CategoryGroups[category] = groups = new List<EquipmentGroup>();
            return groups;
        }

        private EquipmentGroup CreateEquipmentGroup(int number) => new()
        {
            Name = PluginText.F("stashutility.equipment.default_group", "Profile Group {0}", number),
            MinMatchCount = 1
        };

        private List<EquipmentMod> GetEquipmentDatabase(string category)
        {
            equipmentDatabaseByCategory ??= Data.ModDatabase.AllEquipmentMods.GroupBy(m => m.Category, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.OrderBy(m => m.Name, StringComparer.Ordinal).ThenBy(m => m.GameModId, StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
            return equipmentDatabaseByCategory.TryGetValue(category, out var mods) ? mods : new List<EquipmentMod>();
        }

        private static bool EquipmentModContains(EquipmentMod mod, string search) =>
            (mod.Name?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (mod.GameModId?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (mod.Id?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (mod.DisplayText?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false);

        private static string EquipmentModCaption(EquipmentMod mod) => mod == null ? string.Empty : mod.Name + " — " + mod.DisplayText + " [" + mod.GameModId + "]";

        private void DrawEquipmentModDetails(EquipmentMod mod)
        {
            ImGui.TextWrapped(PluginText.F("stashutility.equipment.internal_id", "Internal Mod ID: {0}", mod.GameModId));
            ImGui.TextDisabled(PluginText.F("stashutility.equipment.database_id", "Database ID: {0}", mod.Id));
            ImGui.TextWrapped(mod.DisplayText ?? mod.Name ?? string.Empty);
            var type = mod.Type == "Prefix" ? PluginText.T("stashutility.equipment.prefix", "Prefix") : mod.Type == "Suffix" ? PluginText.T("stashutility.equipment.suffix", "Suffix") : mod.Type;
            ImGui.TextDisabled(PluginText.F("stashutility.equipment.source", "{0} | Source: {1} {2} | Mapping: {3}", type, mod.Source, mod.SourceVersion, mod.MatchStatus));
            ImGui.TextDisabled(PluginText.T("stashutility.equipment.mapping_hint", "Exact mapping means the source supplies a full internal ID; it does not certify the running client's version."));
            if (!string.IsNullOrWhiteSpace(mod.MatchIssue)) ImGui.TextWrapped(mod.MatchIssue);
            if (mod.ItemClasses?.Count > 0) ImGui.TextWrapped(PluginText.F("stashutility.equipment.item_classes", "Item classes: {0}", string.Join(", ", mod.ItemClasses)));
            if (!mod.RawValuesAreGameUnits)
            {
                ImGui.TextDisabled(PluginText.T("stashutility.equipment.display_units", "Source values are display units; Raw thresholds cannot be inferred from them."));
                return;
            }
            if (mod.RawRollRanges == null || mod.RawRollRanges.Count == 0)
                ImGui.TextDisabled(PluginText.T("stashutility.equipment.no_ranges", "No Raw numerical ranges in this source record."));
            else
            {
                for (var index = 0; index < mod.RawRollRanges.Count; index++)
                {
                    var range = mod.RawRollRanges[index];
                    ImGui.TextDisabled(PluginText.F("stashutility.equipment.raw_range", "Raw[{0}]: {1} to {2} ({3})", index, range.Min, range.Max, range.StatId));
                }
            }
        }

        private string EquipmentCategoryLabel(string category) => category switch
        {
            "Weapons" => PluginText.T("stashutility.equipment.category.weapons", "Weapons"),
            "Helmets" => PluginText.T("stashutility.equipment.category.helmets", "Helmets"),
            "BodyArmours" => PluginText.T("stashutility.equipment.category.body_armours", "Body Armour"),
            "Gloves" => PluginText.T("stashutility.equipment.category.gloves", "Gloves"),
            "Boots" => PluginText.T("stashutility.equipment.category.boots", "Boots"),
            "Rings" => PluginText.T("stashutility.equipment.category.rings", "Rings"),
            "Amulets" => PluginText.T("stashutility.equipment.category.amulets", "Amulets"),
            "Belts" => PluginText.T("stashutility.equipment.category.belts", "Belts"),
            "Shields" => PluginText.T("stashutility.equipment.category.shields", "Shields"),
            "Quivers" => PluginText.T("stashutility.equipment.category.quivers", "Quivers"),
            "Focus" => PluginText.T("stashutility.equipment.category.focus", "Focus"),
            _ => category
        };
    }
}
