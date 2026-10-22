namespace StashUtility.Models
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    public class EquipmentMod
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Type { get; set; } // "Prefix" or "Suffix"
        public string Category { get; set; } // "Weapons", "Helmets", "Body Armour", "Gloves", "Boots", "Rings", "Amulets", "Belts", "Shields", "Quivers", etc.

        public string GameModId { get; set; }
        public float MinRoll { get; set; } = 0f;
        public float MaxRoll { get; set; } = 0f;
        public string PropertyId { get; set; }
        public string DisplayText { get; set; }
        public List<string> StatIds { get; set; } = new();
        public List<EquipmentRollRange> RawRollRanges { get; set; } = new();

        public EquipmentMod(string id, string name)
        {
            Id = id;
            Name = name;
        }
    }

    public sealed class EquipmentRollRange
    {
        public string StatId { get; set; }
        public float Min { get; set; }
        public float Max { get; set; }
    }

    /// <summary>Combines equivalent properties while keeping every real Mod ID and raw range.</summary>
    public sealed class EquipmentModOption
    {
        private readonly HashSet<string> gameModIds;

        public string Id { get; }
        public string Name { get; }
        public List<EquipmentMod> Mods { get; }
        public bool UsesAverageDamage { get; }
        public float MinRoll { get; }
        public float MaxRoll { get; }
        public bool SupportsRoll { get; }

        public EquipmentModOption(string id, IEnumerable<EquipmentMod> mods)
        {
            this.Id = id;
            this.Mods = mods.ToList();
            this.Name = string.IsNullOrWhiteSpace(this.Mods[0].DisplayText) ? this.Mods[0].Name : this.Mods[0].DisplayText;
            // Database options are immutable after loading; derive their metadata once.
            this.UsesAverageDamage = this.Mods.All(IsDamageRange);
            this.SupportsRoll = this.UsesAverageDamage || this.Mods.All(mod => mod.StatIds?.Count == 1);
            this.MinRoll = this.Mods.Min(mod => this.UsesAverageDamage
                ? (mod.RawRollRanges[0].Min / 2f + mod.RawRollRanges[1].Min / 2f) : mod.MinRoll);
            this.MaxRoll = this.Mods.Max(mod => this.UsesAverageDamage
                ? (mod.RawRollRanges[0].Max / 2f + mod.RawRollRanges[1].Max / 2f) : mod.MaxRoll);
            this.gameModIds = new HashSet<string>(this.Mods.Select(mod => mod.GameModId), StringComparer.Ordinal);
        }

        public bool TryGetRoll(string gameModId, float raw0, float raw1, out double roll)
        {
            roll = 0;
            if (!this.gameModIds.Contains(gameModId) ||
                !this.SupportsRoll || !float.IsFinite(raw0)) return false;
            if (this.UsesAverageDamage)
            {
                if (!float.IsFinite(raw1) || raw0 > raw1) return false;
                roll = ((double)raw0 + raw1) / 2;
            }
            else roll = raw0;
            return true;
        }

        private static bool IsDamageRange(EquipmentMod mod)
        {
            if (mod.StatIds?.Count != 2 || mod.RawRollRanges?.Count != 2 ||
                mod.RawRollRanges.Any(range => range == null || !float.IsFinite(range.Min) || !float.IsFinite(range.Max) || range.Min > range.Max) ||
                !mod.StatIds.SequenceEqual(mod.RawRollRanges.Select(range => range.StatId), StringComparer.Ordinal)) return false;
            // Only explicit endpoints for one damage type share an average-damage minimum.
            foreach (var scope in new[] { "local_", "attack_", "allies_in_presence_attack_" })
            foreach (var element in new[] { "physical", "fire", "cold", "lightning" })
                if (mod.StatIds[0] == scope + "minimum_added_" + element + "_damage" &&
                    mod.StatIds[1] == scope + "maximum_added_" + element + "_damage") return true;
            return false;
        }
    }
}
