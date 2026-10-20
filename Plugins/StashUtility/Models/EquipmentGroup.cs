using System;
using System.Collections.Generic;

namespace StashUtility.Models
{
    public class EquipmentGroup
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "Default Profile";
        public int MinMatchCount { get; set; } = 2;
        public List<string> ModPatternIds { get; set; } = new();
        public Dictionary<string, float> ModRequiredMinRolls { get; set; } = new();
    }
}
