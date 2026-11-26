// <copyright file="EquipmentAffixTiers.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace StashUtility.Data
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    /// <summary>Offline, base-specific lookup. Unknown IDs never receive an inferred tier.</summary>
    internal sealed class EquipmentAffixTiers
    {
        internal const string AlgorithmName = "toolkit-context-level-desc-v1";
        private Document document;
        private IReadOnlyList<string> targetTypeIds = Array.Empty<string>();

        internal string Error { get; private set; } = string.Empty;
        internal bool Ready => this.document != null;
        internal bool TypesReady { get; private set; }
        internal IReadOnlyList<string> TargetTypeIds => this.targetTypeIds;
        internal string SourceVersion => this.document?.SourceVersion ?? string.Empty;
        internal int BaseCount => this.document?.Bases.Count ?? 0;

        internal void Load(string path)
        {
            this.document = null;
            this.TypesReady = false;
            this.targetTypeIds = Array.Empty<string>();
            this.Error = string.Empty;
            if (!File.Exists(path)) return;
            try
            {
                // Optional type data must never prevent the existing tier lookup from loading.
                var root = JObject.Parse(File.ReadAllText(path));
                var typesToken = root["ModTypes"];
                root.Remove("ModTypes");
                var candidate = root.ToObject<Document>();
                if (candidate == null || candidate.SchemaVersion != 1 || candidate.Algorithm != AlgorithmName ||
                    candidate.TierConvention != "T1Best" || string.IsNullOrWhiteSpace(candidate.SourceVersion) ||
                    candidate.Bases == null || candidate.Bases.Count == 0 ||
                    candidate.Profiles == null || candidate.Levels == null || candidate.Levels.Count == 0)
                {
                    throw new InvalidDataException("Unsupported or incomplete affix tier data.");
                }

                foreach (var entry in candidate.Bases)
                {
                    if (string.IsNullOrEmpty(entry.Key) || string.IsNullOrEmpty(entry.Value) ||
                        !candidate.Profiles.ContainsKey(entry.Value))
                        throw new InvalidDataException("A base refers to an unknown affix pool.");
                }

                int total = 0;
                foreach (var pool in candidate.Profiles.Values)
                {
                    if (pool == null) throw new InvalidDataException("Null affix pool.");
                    foreach (var entry in pool)
                    {
                        if (entry.Value < 1 || entry.Value > 100 ||
                            !candidate.Levels.TryGetValue(entry.Key, out var level) || level < 0)
                            throw new InvalidDataException("Invalid tier or missing mod level.");
                        total++;
                    }
                }
                if (total == 0) throw new InvalidDataException("Empty affix pools.");
                this.document = candidate;
                this.LoadModTypes(candidate, typesToken);
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is JsonException)
            {
                this.Error = ex.Message;
            }
        }

        internal bool TryGet(string internalName, string metadataPath, string modId, out int level, out int tier)
        {
            level = tier = 0;
            if (this.document == null || string.IsNullOrEmpty(modId)) return false;
            // Only exact base IDs/metadata paths; no name, suffix or category guessing.
            if (!this.TryGetPool(internalName, out var pool) && !this.TryGetPool(metadataPath, out pool)) return false;
            return pool.TryGetValue(modId, out tier) && this.document.Levels.TryGetValue(modId, out level);
        }

        internal bool TryGetModType(string modId, out string modType)
        {
            modType = string.Empty;
            if (!this.TypesReady || string.IsNullOrEmpty(modId) ||
                !this.document.ModTypes.TryGetValue(modId, out var value)) return false;
            modType = value;
            return true;
        }

        // These pairs share their complete Mods.Stat1..Stat8 vector in the bundled patch.
        // Alias targets only; the source types and per-base tier ladders stay separate.
        internal static string NormalizeTargetType(string modType) => modType switch
        {
            "GlobalIncreaseMeleeSkillGemLevelWeapon" => "GlobalIncreaseMeleeSkillGemLevel",
            "GlobalIncreaseMinionSpellSkillGemLevelWeapon" => "GlobalIncreaseMinionSpellSkillGemLevel",
            "GlobalIncreaseProjectileSkillGemLevelWeapon" => "GlobalIncreaseProjectileSkillGemLevel",
            "GlobalIncreaseSpellSkillGemLevelWeapon" => "GlobalIncreaseSpellSkillGemLevel",
            _ => modType
        };

        /// <summary>Counts each qualifying explicit mod, including repeated IDs, and finds the first target type.</summary>
        internal (int MatchingCount, string TargetType) Evaluate(string internalName, string metadataPath,
            IEnumerable<string> modIds, int maxTier, ICollection<string> targetTypes)
        {
            if (this.document == null || modIds == null ||
                (!this.TryGetPool(internalName, out var pool) && !this.TryGetPool(metadataPath, out pool)))
                return (0, string.Empty);

            int matchingCount = 0;
            string targetType = string.Empty;
            maxTier = Math.Clamp(maxTier, 1, 100);
            foreach (var modId in modIds)
            {
                if (string.IsNullOrEmpty(modId) || !pool.TryGetValue(modId, out var tier) || tier > maxTier) continue;
                matchingCount++;
                if (targetType.Length != 0 || targetTypes == null || !this.TryGetModType(modId, out var modType)) continue;
                string candidateType = NormalizeTargetType(modType);
                foreach (var target in targetTypes)
                {
                    if (!string.Equals(NormalizeTargetType(target), candidateType, StringComparison.Ordinal)) continue;
                    targetType = candidateType;
                    break;
                }
            }
            return (matchingCount, targetType);
        }

        private void LoadModTypes(Document candidate, JToken token)
        {
            if (token is not JObject types || types.Count == 0) return;
            var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in types.Properties())
            {
                if (string.IsNullOrWhiteSpace(property.Name) || property.Value.Type != JTokenType.String) return;
                var modType = property.Value.Value<string>();
                if (string.IsNullOrWhiteSpace(modType)) return;
                mapping.Add(property.Name, modType);
            }

            var referencedTypes = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var pool in candidate.Profiles.Values)
            {
                foreach (var modId in pool.Keys)
                {
                    if (!mapping.TryGetValue(modId, out var modType)) return;
                    referencedTypes.Add(NormalizeTargetType(modType));
                }
            }
            candidate.ModTypes = mapping;
            var ids = new string[referencedTypes.Count];
            referencedTypes.CopyTo(ids);
            this.targetTypeIds = Array.AsReadOnly(ids);
            this.TypesReady = true;
        }

        private bool TryGetPool(string key, out Dictionary<string, int> pool)
        {
            pool = null;
            return !string.IsNullOrEmpty(key) && this.document.Bases.TryGetValue(key, out var profile) &&
                this.document.Profiles.TryGetValue(profile, out pool);
        }

        private sealed class Document
        {
            public int SchemaVersion { get; set; }
            public string Algorithm { get; set; }
            public string TierConvention { get; set; }
            public string SourceVersion { get; set; }
            public Dictionary<string, int> Levels { get; set; }
            public Dictionary<string, string> Bases { get; set; }
            public Dictionary<string, Dictionary<string, int>> Profiles { get; set; }
            public Dictionary<string, string> ModTypes { get; set; }
        }
    }
}
