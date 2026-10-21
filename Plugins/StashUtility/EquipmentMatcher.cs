// <copyright file="EquipmentMatcher.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace StashUtility
{
    using System;
    using System.Collections.Generic;
    using StashUtility.Models;

    /// <summary>Matches equipment properties without accessing game memory or drawing.</summary>
    internal static class EquipmentMatcher
    {
        private static readonly (string Prefix, string Category)[] Categories =
        {
            ("Metadata/Items/Weapons/", "Weapons"),
            ("Metadata/Items/Armours/Helmets/", "Helmets"),
            ("Metadata/Items/Armours/BodyArmours/", "Body Armour"),
            ("Metadata/Items/Armours/Gloves/", "Gloves"),
            ("Metadata/Items/Armours/Boots/", "Boots"),
            ("Metadata/Items/Rings/", "Rings"),
            ("Metadata/Items/Amulets/", "Amulets"),
            ("Metadata/Items/Belts/", "Belts"),
            ("Metadata/Items/Armours/Shields/", "Shields"),
            ("Metadata/Items/Quivers/", "Quivers"),
        };

        internal static string GetCategory(string metadataPath)
        {
            if (string.IsNullOrEmpty(metadataPath)) return null;
            foreach (var (prefix, category) in Categories)
                if (metadataPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return category;
            return null;
        }

        internal static int CountMatches(
            EquipmentGroup group,
            IReadOnlyList<EquipmentModOption> options,
            IReadOnlyList<(string name, (float v0, float v1) vals)> explicitMods)
        {
            if (group?.ModPatternIds == null || group.ModPatternIds.Count == 0 ||
                options == null || explicitMods == null || explicitMods.Count == 0) return 0;

            var selectedIds = new HashSet<string>(group.ModPatternIds, StringComparer.Ordinal);
            var matchedProperties = new HashSet<string>(StringComparer.Ordinal);
            foreach (var option in options)
            {
                if (option?.Mods == null || option.Mods.Count == 0 ||
                    string.IsNullOrEmpty(option.Id) || matchedProperties.Contains(option.Id)) continue;

                bool matched = false;
                foreach (var row in option.Mods)
                {
                    if (row == null || string.IsNullOrEmpty(row.Id) || !selectedIds.Contains(row.Id) ||
                        string.IsNullOrEmpty(row.GameModId)) continue;

                    foreach (var mod in explicitMods)
                    {
                        if (string.Equals(row.GameModId, mod.name, StringComparison.Ordinal) &&
                            MatchesRoll(group, option, row, mod.vals.v0, mod.vals.v1))
                        {
                            matched = true;
                            break;
                        }
                    }
                    if (matched) break;
                }
                if (matched) matchedProperties.Add(option.Id);
            }
            return matchedProperties.Count;
        }

        private static bool MatchesRoll(
            EquipmentGroup group, EquipmentModOption option, EquipmentMod row, float raw0, float raw1)
        {
            // The caller validates the complete raw vector before exposing its first two values.
            if (row.StatIds == null || row.StatIds.Count == 0 || !float.IsFinite(raw0) ||
                (row.StatIds.Count > 1 && !float.IsFinite(raw1))) return false;
            if (option.UsesAverageDamage && raw0 > raw1) return false;

            float rawMinimum = 0;
            float averageMinimum = 0;
            bool hasRawMinimum = group.ModRequiredMinRolls?.TryGetValue(row.Id, out rawMinimum) == true;
            bool hasAverageMinimum = group.ModRequiredAverageDamageRolls?.TryGetValue(row.Id, out averageMinimum) == true;
            if (!hasRawMinimum && !hasAverageMinimum) return true;

            // Unknown scalar conversions and incompatible saved conditions never pass silently.
            if (option.UsesAverageDamage)
            {
                return !hasRawMinimum && hasAverageMinimum && float.IsFinite(averageMinimum) &&
                    option.TryGetRoll(row.GameModId, raw0, raw1, out var average) && average >= averageMinimum;
            }

            return !hasAverageMinimum && hasRawMinimum && float.IsFinite(rawMinimum) &&
                option.TryGetRoll(row.GameModId, raw0, raw1, out var roll) && roll >= rawMinimum;
        }
    }
}
