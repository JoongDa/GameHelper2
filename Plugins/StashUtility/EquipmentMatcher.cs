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
            return CountMatches(Compile(group, options), explicitMods);
        }

        internal static CompiledGroup Compile(EquipmentGroup group, IReadOnlyList<EquipmentModOption> options)
        {
            var byModId = new Dictionary<string, List<CompiledCondition>>(StringComparer.Ordinal);
            var properties = new Dictionary<string, int>(StringComparer.Ordinal);
            if (group?.ModPatternIds != null && group.ModPatternIds.Count > 0 && options != null)
            {
                var selectedIds = new HashSet<string>(group.ModPatternIds, StringComparer.Ordinal);
                foreach (var option in options)
                {
                    if (option?.Mods == null || option.Mods.Count == 0 || string.IsNullOrEmpty(option.Id)) continue;
                    foreach (var row in option.Mods)
                    {
                        if (row == null || string.IsNullOrEmpty(row.Id) || !selectedIds.Contains(row.Id) ||
                            string.IsNullOrEmpty(row.GameModId) || row.StatIds == null || row.StatIds.Count == 0) continue;

                        float rawMinimum = 0;
                        float averageMinimum = 0;
                        bool hasRawMinimum = group.ModRequiredMinRolls?.TryGetValue(row.Id, out rawMinimum) == true;
                        bool hasAverageMinimum = group.ModRequiredAverageDamageRolls?.TryGetValue(row.Id, out averageMinimum) == true;
                        var mode = RollMode.Presence;
                        double minimum = 0;
                        if (hasRawMinimum || hasAverageMinimum)
                        {
                            // Incompatible saved thresholds and unknown conversions cannot pass.
                            if (!option.SupportsRoll) continue;
                            if (option.UsesAverageDamage)
                            {
                                if (hasRawMinimum || !hasAverageMinimum || !float.IsFinite(averageMinimum)) continue;
                                mode = RollMode.AverageDamage;
                                minimum = averageMinimum;
                            }
                            else
                            {
                                if (hasAverageMinimum || !hasRawMinimum || !float.IsFinite(rawMinimum)) continue;
                                mode = RollMode.Raw;
                                minimum = rawMinimum;
                            }
                        }

                        if (!properties.TryGetValue(option.Id, out var propertyIndex))
                        {
                            propertyIndex = properties.Count;
                            properties.Add(option.Id, propertyIndex);
                        }
                        if (!byModId.TryGetValue(row.GameModId, out var conditions))
                        {
                            conditions = new List<CompiledCondition>();
                            byModId.Add(row.GameModId, conditions);
                        }
                        conditions.Add(new CompiledCondition(propertyIndex, row.StatIds.Count > 1,
                            option.UsesAverageDamage, mode, minimum));
                    }
                }
            }

            var rules = new Dictionary<string, CompiledCondition[]>(byModId.Count, StringComparer.Ordinal);
            foreach (var pair in byModId) rules.Add(pair.Key, pair.Value.ToArray());
            return new CompiledGroup(Math.Max(1, group?.MinMatchCount ?? 1), rules, properties.Count);
        }

        internal static int CountMatches(
            CompiledGroup group, IReadOnlyList<(string name, (float v0, float v1) vals)> explicitMods)
        {
            return group?.CountMatches(explicitMods) ?? 0;
        }

        internal enum RollMode
        {
            Presence,
            Raw,
            AverageDamage,
        }

        internal readonly struct CompiledCondition
        {
            internal readonly int PropertyIndex;
            private readonly bool needsSecondValue;
            private readonly bool usesAverageDamage;
            private readonly RollMode mode;
            private readonly double minimum;

            internal CompiledCondition(int propertyIndex, bool needsSecondValue, bool usesAverageDamage, RollMode mode, double minimum)
            {
                this.PropertyIndex = propertyIndex;
                this.needsSecondValue = needsSecondValue;
                this.usesAverageDamage = usesAverageDamage;
                this.mode = mode;
                this.minimum = minimum;
            }

            internal bool Matches(float raw0, float raw1)
            {
                // Complete raw vectors are validated by the snapshot reader before matching.
                if (!float.IsFinite(raw0) || (this.needsSecondValue && !float.IsFinite(raw1)) ||
                    (this.usesAverageDamage && raw0 > raw1)) return false;
                return this.mode == RollMode.Presence ||
                    (this.mode == RollMode.AverageDamage ? ((double)raw0 + raw1) / 2 : raw0) >= this.minimum;
            }
        }

        internal sealed class CompiledGroup
        {
            private readonly Dictionary<string, CompiledCondition[]> rules;
            private readonly bool[] matchedProperties;

            internal int MinMatchCount { get; }

            internal CompiledGroup(int minMatchCount, Dictionary<string, CompiledCondition[]> rules, int propertyCount)
            {
                this.MinMatchCount = minMatchCount;
                this.rules = rules;
                this.matchedProperties = new bool[propertyCount];
            }

            internal int CountMatches(IReadOnlyList<(string name, (float v0, float v1) vals)> explicitMods)
            {
                if (explicitMods == null || explicitMods.Count == 0 || this.rules.Count == 0) return 0;
                // Compiled groups are evaluated sequentially on the overlay thread.
                Array.Clear(this.matchedProperties, 0, this.matchedProperties.Length);
                int count = 0;
                for (int i = 0; i < explicitMods.Count; i++)
                {
                    var mod = explicitMods[i];
                    if (string.IsNullOrEmpty(mod.name) || !this.rules.TryGetValue(mod.name, out var conditions)) continue;
                    foreach (var condition in conditions)
                    {
                        if (this.matchedProperties[condition.PropertyIndex] || !condition.Matches(mod.vals.v0, mod.vals.v1)) continue;
                        this.matchedProperties[condition.PropertyIndex] = true;
                        count++;
                    }
                }
                return count;
            }
        }
    }
}
