// <copyright file="EquipmentAffixTiers.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace StashUtility.Data
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using Newtonsoft.Json;

    /// <summary>Offline, base-specific lookup. Unknown IDs never receive an inferred tier.</summary>
    internal sealed class EquipmentAffixTiers
    {
        internal const string AlgorithmName = "toolkit-context-level-desc-v1";
        private Document document;

        internal string Error { get; private set; } = string.Empty;
        internal bool Ready => this.document != null;
        internal string SourceVersion => this.document?.SourceVersion ?? string.Empty;
        internal int BaseCount => this.document?.Bases.Count ?? 0;

        internal void Load(string path)
        {
            this.document = null;
            this.Error = string.Empty;
            if (!File.Exists(path)) return;
            try
            {
                var candidate = JsonConvert.DeserializeObject<Document>(File.ReadAllText(path));
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
        }
    }
}
