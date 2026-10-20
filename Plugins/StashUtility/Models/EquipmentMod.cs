namespace StashUtility.Models
{
    public class EquipmentMod
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Type { get; set; } // "Prefix" or "Suffix"
        public string Category { get; set; } // "Weapons", "Helmets", "Body Armour", "Gloves", "Boots", "Rings", "Amulets", "Belts", "Shields", "Quivers", etc.

        public string GameModId { get; set; }
        public float MinRoll { get; set; } = 0f;
        public float MaxRoll { get; set; } = 0f;

        public EquipmentMod(string id, string name)
        {
            Id = id;
            Name = name;
        }
    }
}
