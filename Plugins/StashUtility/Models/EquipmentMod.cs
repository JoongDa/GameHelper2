// <copyright file="EquipmentMod.cs" company="GameHelper">
// Copyright (c) GameHelper. All rights reserved.
// </copyright>

namespace StashUtility.Models
{
    using System;
    using System.Collections.Generic;
    using Newtonsoft.Json;

    public sealed class EquipmentRollRange
    {
        public string StatId { get; set; } = string.Empty;
        public int Min { get; set; }
        public int Max { get; set; }
    }

    public sealed class EquipmentMod
    {
        // Id identifies a category-specific database row, never a game Mod record.
        public string Id { get; set; } = string.Empty;
        public string GameModId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string DisplayText { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public List<EquipmentRollRange> RawRollRanges { get; set; } = new();
        public bool RawValuesAreGameUnits { get; set; }
        public string Source { get; set; } = string.Empty;
        public string SourceVersion { get; set; } = string.Empty;
        public string MatchStatus { get; set; } = "Unverified";
        public string MatchIssue { get; set; } = string.Empty;
        public bool LiveVerified { get; set; }
        public List<string> ItemClasses { get; set; } = new();

        // Exact describes the source ID mapping, not verification of the live client.
        [JsonIgnore]
        public bool IsExactMatch =>
            string.Equals(MatchStatus, "Exact", StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(GameModId) && RawValuesAreGameUnits;
    }
}
