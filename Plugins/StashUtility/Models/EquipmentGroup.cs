// <copyright file="EquipmentGroup.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace StashUtility.Models
{
    using System.Collections.Generic;

    /// <summary>Reuses jewel groups while storing complete equipment mod IDs and raw thresholds.</summary>
    public sealed class EquipmentGroup : JewelGroup
    {
        /// <summary>Gets or sets the optional minimum for the second raw value of each mod.</summary>
        public Dictionary<string, float> ModRequiredMinRollsValue1 { get; set; } = new();
    }

    /// <summary>Stable category keys shared by the equipment database, UI, and scanner.</summary>
    public static class EquipmentCategories
    {
        public static readonly string[] All =
        {
            "Weapons", "Helmets", "BodyArmours", "Gloves", "Boots", "Rings",
            "Amulets", "Belts", "Shields", "Quivers", "Focus"
        };
    }
}
