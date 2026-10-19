using System.Collections.Generic;
using StashUtility.Models;

namespace StashUtility.Data
{
    public static class ModDatabase
    {
        public static readonly List<WaystoneMod> AllWaystoneMods = new()
        {
             new WaystoneMod("MapMonsterDamageAsFire", "Monsters deal % of Damage as Extra Fire") { MonsterEffectiveness = 16, WaystoneDropChance = 15 },
             new WaystoneMod("MapMonsterDamageAsCold", "Monsters deal % of Damage as Extra Cold") { ItemRarity = 14, WaystoneDropChance = 15 },
             new WaystoneMod("MapMonsterDamageAsLightning", "Monsters deal % of Damage as Extra Lightning") { PackSize = 8, WaystoneDropChance = 15 },
             new WaystoneMod("MapMonsterDamageIncrease", "% increased Monster Damage") { MonsterRarity = 25, WaystoneDropChance = 20 },
             new WaystoneMod("MapMonsterSpeedIncrease", "Monsters have % increased Attack, Cast and Movement Speed") { PackSize = 9, WaystoneDropChance = 25 },
             new WaystoneMod("MapMonsterCritIncrease", "Monsters have % increased Critical Hit Chance / Monsters have +% Critical Damage Bonus") { PackSize = 9, WaystoneDropChance = 15 },
             new WaystoneMod("MapMonsterLifeIncrease", "% more Monster Life") { MonsterRarity = 23, WaystoneDropChance = 15 },
             new WaystoneMod("MapMonsterElementalResistances", "+% Monster Elemental Resistances") { ItemRarity = 14, WaystoneDropChance = 10 },
             new WaystoneMod("MapMonsterArmoured", "Monsters are Armoured") { MonsterRarity = 18, WaystoneDropChance = 15 },
             new WaystoneMod("MapMonsterEvasive", "Monsters are Evasive") { PackSize = 6, WaystoneDropChance = 15 },
             new WaystoneMod("MapMonsterEnergyShield", "Monsters gain % of maximum Life as Extra maximum Energy Shield") { ItemRarity = 13, WaystoneDropChance = 15 },
             new WaystoneMod("MapMonsterPoisoning", "Monsters have % chance to Poison on Hit") { PackSize = 7, WaystoneDropChance = 10 },
             new WaystoneMod("MapMonsterBleeding", "Monsters have % chance to inflict Bleeding on Hit") { MonsterEffectiveness = 13, WaystoneDropChance = 10 },
             new WaystoneMod("MapMonsterStunAilmentThreshold", "Monsters have % increased Ailment Threshold / Monsters have % increased Stun Threshold") { MonsterEffectiveness = 13, WaystoneDropChance = 10 },
             new WaystoneMod("MapMonsterArmourBreak", "Monsters Break Armour equal to % of Physical Damage dealt") { MonsterRarity = 19, WaystoneDropChance = 10 },
             new WaystoneMod("MapMonsterAccuracy", "Monsters have % increased Accuracy Rating") { PackSize = 7, WaystoneDropChance = 10 },
             new WaystoneMod("MapMonsterDamageAsChaos", "Monsters deal % of Damage as Extra Chaos") { ItemRarity = 15, WaystoneDropChance = 15 },
             new WaystoneMod("MapMonsterStunBuildup", "Monsters have % increased Stun Buildup") { MonsterEffectiveness = 13, WaystoneDropChance = 15 },
             new WaystoneMod("MapMonsterElementAilmentChance", "Monster have % increased Elemental Ailment Application") { ItemRarity = 11, WaystoneDropChance = 15 },
             new WaystoneMod("MapMonsterAdditionalProjectiles", "Monsters fire # additional Projectiles") { PackSize = 9, WaystoneDropChance = 25 },
             new WaystoneMod("MapMonsterIncreasedAreaOfEffect", "Monsters have % increased Area of Effect") { WaystoneDropChance = 20 },
             new WaystoneMod("MapPlayerEnfeeble", "Players are periodically Cursed with Enfeeble") { MonsterEffectiveness = 16, WaystoneDropChance = 20 },
             new WaystoneMod("MapPlayerTemporalChains", "Players are periodically Cursed with Temporal Chains") { PackSize = 8, WaystoneDropChance = 20 },
             new WaystoneMod("MapPlayerElementalWeakness", "Players are periodically Cursed with Elemental Weakness") { ItemRarity = 13, WaystoneDropChance = 20 },
             new WaystoneMod("MapSpreadBurningGround", "Area has patches of Ignited Ground") { MonsterEffectiveness = 15, WaystoneDropChance = 15 },
             new WaystoneMod("MapSpreadChilledGround", "Area has patches of Chilled Ground") { ItemRarity = 12, WaystoneDropChance = 15 },
             new WaystoneMod("MapSpreadShockedGround", "Area has patches of Shocked Ground") { PackSize = 7, WaystoneDropChance = 15 },
             new WaystoneMod("MapMonstersElementalPenetration", "Monster Damage Penetrates % Elemental Resistances") { ItemRarity = 16, WaystoneDropChance = 20 },
             new WaystoneMod("MapPlayerMaximumResists", "-% maximum Player Resistances") { PackSize = 10, WaystoneDropChance = 25 },
             new WaystoneMod("MapPlayerFlaskChargeGain", "Players gain % reduced Flask Charges") { PackSize = 7, WaystoneDropChance = 15 },
             new WaystoneMod("MapPlayerRecoveryRate", "Players have % less Recovery Rate of Life and Energy Shield") { ItemRarity = 15, WaystoneDropChance = 20 },
             new WaystoneMod("MapPlayerCooldownRecovery", "Players have % less Cooldown Recovery Rate") { ItemRarity = 12, WaystoneDropChance = 15 },
             new WaystoneMod("MapMonstersBaseSelfCriticalMultiplier", "Monsters take % reduced Extra Damage from Critical Hits") { MonsterRarity = 18, WaystoneDropChance = 10 },
             new WaystoneMod("MapMonstersCurseEffectOnSelf", "% less effect of Curses on Monsters") { ItemRarity = 10, WaystoneDropChance = 10 },
        };

        public static readonly List<TabletMod> AllTabletMods = new()
        {
            // --- Universal Base Prefixes (General) ---
            new TabletMod("TowerDroppedItemRarityIncrease", "% increased Rarity of Items found in Map", "prefix", "General") { MinRoll = 8, MaxRoll = 12 },
            new TabletMod("TowerMonsterEffectiveness", "Monsters have % increased Effectiveness", "prefix", "General") { MinRoll = 10, MaxRoll = 15 },
            new TabletMod("TowerPackSizeIncrease", "% increased Pack Size in Map", "prefix", "General") { MinRoll = 5, MaxRoll = 7 },
            new TabletMod("TowerMagicPackIncrease", "Map has % increased Magic Monsters", "prefix", "General") { MinRoll = 30, MaxRoll = 40 },
            new TabletMod("TowerRarePackIncrease", "Map has % increased number of Rare Monsters", "prefix", "General") { MinRoll = 25, MaxRoll = 35 },
            new TabletMod("TowerMonsterRarityIncrease", "Map has % increased Monster Rarity", "prefix", "General") { MinRoll = 15, MaxRoll = 20 },
            new TabletMod("TowerDroppedGoldIncrease", "% increased Gold found in Map", "prefix", "General") { MinRoll = 25, MaxRoll = 35 },
            new TabletMod("TowerExperienceGainIncrease", "% increased Experience gain in Map", "prefix", "General") { MinRoll = 12, MaxRoll = 18 },
            new TabletMod("TowerRareChestCount", "Map contains additional Rare Chests", "prefix", "General") { MinRoll = 2, MaxRoll = 3 },
            new TabletMod("TowerAdditionalStoneCircle", "Map contains an additional Summoning Circle", "prefix", "General") { MinRoll = 1, MaxRoll = 1 },
            new TabletMod("TowerAdditionalExile", "% increased Quantity of Waystones found in Map Map is inhabited by an additional Rogue Exile", "prefix", "General") { MinRoll = 1, MaxRoll = 1 },
            new TabletMod("TowerMapBossAdditionalSpirit", "% increased Quantity of Waystones found in Map Map contains an additional Azmeri Spirit", "prefix", "General") { MinRoll = 1, MaxRoll = 1 },
            new TabletMod("TowerMapBossAdditionalEssence", "Map contains an additional Essence % increased Quantity of Waystones found in Map", "prefix", "General") { MinRoll = 1, MaxRoll = 1 },

            // --- Universal Base Suffixes (General) ---
            new TabletMod("TowerMapDroppedMapsIncrease", "% increased Quantity of Waystones found in Map", "suffix", "General") { MinRoll = 30, MaxRoll = 40 },
            new TabletMod("TowerAdditionalShrineChance", "Map has % increased chance to contain Shrines", "suffix", "General") { MinRoll = 70, MaxRoll = 100 },
            new TabletMod("TowerAdditionalShrine", "% reduced Pack Size in Map % increased Quantity of Waystones found in Map Map contains an additional Shrine", "suffix", "General") { MinRoll = 1, MaxRoll = 1 },
            new TabletMod("TowerAdditionalStrongboxChance", "Map has % increased chance to contain Strongboxes", "suffix", "General") { MinRoll = 70, MaxRoll = 100 },
            new TabletMod("TowerAdditionalStrongbox", "% reduced Pack Size in Map % increased Quantity of Waystones found in Map Map contains an additional Strongbox", "suffix", "General") { MinRoll = 1, MaxRoll = 1 },
            new TabletMod("TowerAdditionalEssenceChance", "Map has % increased chance to contain Essences", "suffix", "General") { MinRoll = 70, MaxRoll = 100 },
            new TabletMod("TowerAdditionalSpiritChance", "Map has % increased chance to contain Azmeri Spirits", "suffix", "General") { MinRoll = 70, MaxRoll = 100 },
            new TabletMod("TowerAdditionalExileChance", "Map has % increased chance to contain Rogue Exiles", "suffix", "General") { MinRoll = 70, MaxRoll = 100 },
            new TabletMod("TowerStoneCircleChance", "Map has % increased chance to contain a Summoning Circle", "suffix", "General") { MinRoll = 70, MaxRoll = 100 },
            new TabletMod("TowerMapAdditionalModifier", "Map has additional random Modifiers", "suffix", "General") { MinRoll = 1, MaxRoll = 2 },
            new TabletMod("TowerMapAdditionalUniqueMonsterModifier", "Unique Monsters have 1 additional Rare Modifiers", "suffix", "General") { MinRoll = 1, MaxRoll = 1 },
            new TabletMod("TowerRareAdditionalModChance", "Rare Monsters in Map have a % Surpassing chance to have an additional Modifier", "suffix", "General") { MinRoll = 50, MaxRoll = 80 },

            // --- Overseer Tablet Specific Suffixes ---
            new TabletMod("TowerMapBossRarity", "% increased Rarity of Items dropped by Map Bosses", "suffix", "Overseer") { MinRoll = 35, MaxRoll = 60 },
            new TabletMod("TowerMapBossQuantity", "% increased Quantity of Items dropped by Map Bosses", "suffix", "Overseer") { MinRoll = 13, MaxRoll = 20 },
            new TabletMod("TowerMapBossExperience", "Map Bosses grant % increased Experience", "suffix", "Overseer") { MinRoll = 40, MaxRoll = 80 },
            new TabletMod("TowerMapBossWaystoneChance", "% increased Quantity of Waystones dropped by Map Bosses", "suffix", "Overseer") { MinRoll = 18, MaxRoll = 30 },
            new TabletMod("TowerMapBossAdditionalStrongbox", "Map contains additional Strongboxes", "suffix", "Overseer") { MinRoll = 1, MaxRoll = 2 },
            new TabletMod("TowerMapBossAdditionalShrine", "Map contains additional Shrines", "suffix", "Overseer") { MinRoll = 1, MaxRoll = 2 },
            new TabletMod("TowerAdditionalEssence", "Map contains additional Essences", "suffix", "Overseer") { MinRoll = 1, MaxRoll = 2 },
            new TabletMod("TowerAdditionalAzmeriWisp", "Map contains additional Azmeri Spirits", "suffix", "Overseer") { MinRoll = 1, MaxRoll = 2 },

            // --- Temple Tablet (Incursion) Specific Suffixes ---
            new TabletMod("TowerIncursionPackSize", "% increased Pack Size for Monsters around Vaal Beacons in Map", "suffix", "Temple") { MinRoll = 10, MaxRoll = 30 },
            new TabletMod("TowerIncursionExtraPacks", "1 extra packs of Monsters around Vaal Beacons in Map", "suffix", "Temple") { MinRoll = 1, MaxRoll = 1 },
            new TabletMod("TowerIncursionExtraPacksChance", "% chance for an extra packs of Monsters around Vaal Beacons in Map", "suffix", "Temple") { MinRoll = 30, MaxRoll = 60 },
            new TabletMod("TowerIncursionSecondaryEncounters", "% increased chance Vaal Beacons summon additional Monsters in Map", "suffix", "Temple") { MinRoll = 25, MaxRoll = 50 },
            new TabletMod("TowerIncursionTokenChance", "% chance to gain an additional Crystal from Vaal Beacons in Map", "suffix", "Temple") { MinRoll = 5, MaxRoll = 10 },
            new TabletMod("TowerIncursionBossChance", "% chance to add a Vaal Beacon Unique Monster to the Map", "suffix", "Temple") { MinRoll = 10, MaxRoll = 25 },
            new TabletMod("TowerIncursionRareChestChance", "% increased chance Vaal Beacon Chests are Rare in Map", "suffix", "Temple") { MinRoll = 30, MaxRoll = 60 },

            // --- Breach Tablet Specific Suffixes ---
            new TabletMod("TowerBreachHivebloodQuantity", "% increased Quantity of Hiveblood found in Map", "suffix", "Breach") { MinRoll = 30, MaxRoll = 60 },
            new TabletMod("TowerBreachWombgiftQuantity", "% increased Quantity of Wombgifts found in Map", "suffix", "Breach") { MinRoll = 30, MaxRoll = 60 },
            new TabletMod("TowerBreachWombgiftLevelChance", "Wombgifts have % chance to drop one Level higher in Map", "suffix", "Breach") { MinRoll = 10, MaxRoll = 30 },
            new TabletMod("TowerBreachBossChance", "Unstable Breaches in Map have % increased chance to contain Vruun, Marshal of Xesht", "suffix", "Breach") { MinRoll = 20, MaxRoll = 50 },
            new TabletMod("TowerBreachAdditionalRares", "Unstable Breaches in Map spawn additional Rare Monsters when Stabilised", "suffix", "Breach") { MinRoll = 1, MaxRoll = 3 },
            new TabletMod("TowerBreachRareMonsterPotency", "% increased Effectiveness of Rare Breach Monsters in Map", "suffix", "Breach") { MinRoll = 5, MaxRoll = 20 },
            new TabletMod("TowerBreachMonsterQuantity", "Breaches in Map have % increased Pack Size", "suffix", "Breach") { MinRoll = 5, MaxRoll = 15 },

            // --- Delirium Tablet Specific Suffixes ---
            new TabletMod("TowerDeliriumMonsterSplinterIncrease", "% increased Stack size of Simulacrum Splinters found in Map", "suffix", "Delirium") { MinRoll = 15, MaxRoll = 30 },
            new TabletMod("TowerDeliriumFogDissipationDelayNew", "Delirium Fog in Map lasts additional seconds before dissipating", "suffix", "Delirium") { MinRoll = 6, MaxRoll = 12 },
            new TabletMod("TowerDeliriumFogPersistence", "Delirium Fog in Map dissipates % slower", "suffix", "Delirium") { MinRoll = 20, MaxRoll = 30 },
            new TabletMod("TowerDeliriumDifficultyIncrease", "Delirium Fog in Map applies % increased Deliriousness to Players", "suffix", "Delirium") { MinRoll = 15, MaxRoll = 30 },
            new TabletMod("TowerDeliriumPackSizeIncrease", "Delirium Monsters in Map have % increased Pack Size", "suffix", "Delirium") { MinRoll = 15, MaxRoll = 30 },
            new TabletMod("TowerDeliriumDoodadsIncrease", "Delirium Fog in Map spawns % increased Fracturing Mirrors", "suffix", "Delirium") { MinRoll = 15, MaxRoll = 30 },
            new TabletMod("TowerDeliriumRareMonsterPause", "Slaying Rare Monsters in Map pauses the Delirium Mirror Timer for seconds", "suffix", "Delirium") { MinRoll = 3, MaxRoll = 5 },
            new TabletMod("TowerDeliriumBossChance", "Delirium Encounters in Map are % more likely to spawn Unique Bosses", "suffix", "Delirium") { MinRoll = 15, MaxRoll = 30 },
            new TabletMod("TowerDeliriumAdditionalShardsChance", "Delirium Fog in Map spawns % increased MirrorShards", "suffix", "Delirium") { MinRoll = 12, MaxRoll = 26 },

            // --- Ritual Tablet Specific Suffixes ---
            new TabletMod("TowerRitualTributeIncrease", "Monsters Sacrificed at Ritual Altars in Map grant % increased Tribute", "suffix", "Ritual") { MinRoll = 18, MaxRoll = 30 },
            new TabletMod("TowerRitualRerollCostIncrease", "Rerolling Favours at Ritual Altars in Map costs % reduced Tribute", "suffix", "Ritual") { MinRoll = 20, MaxRoll = 30 },
            new TabletMod("TowerRitualDeferCostIncrease", "Deferring Favours at Ritual Altars in Map costs % reduced Tribute", "suffix", "Ritual") { MinRoll = 20, MaxRoll = 30 },
            new TabletMod("TowerRitualDeferSpeed", "Favours Deferred at Ritual Altars in Map reappear % sooner", "suffix", "Ritual") { MinRoll = 25, MaxRoll = 40 },
            new TabletMod("TowerRitualAdditionalReroll", "Ritual Altars in Map allow rerolling Favours additional times", "suffix", "Ritual") { MinRoll = 1, MaxRoll = 3 },
            new TabletMod("TowerRitualChanceForNoCost", "Favours Rerolled at Ritual Altars in Map have % chance to cost no Tribute", "suffix", "Ritual") { MinRoll = 3, MaxRoll = 6 },
            new TabletMod("TowerRitualRareMonsters", "Revived Monsters from Ritual Altars in Map have % increased chance to be Magic", "suffix", "Ritual") { MinRoll = 35, MaxRoll = 70 },
            new TabletMod("TowerRitualMagicMonsters", "Revived Monsters from Ritual Altars in Map have % increased chance to be Rare", "suffix", "Ritual") { MinRoll = 25, MaxRoll = 40 },
            new TabletMod("TowerRitualOmenChance", "Ritual Favours in Map have % increased chance to be Omens", "suffix", "Ritual") { MinRoll = 35, MaxRoll = 70 },

            // --- Abyss Tablet Specific Suffixes ---
            new TabletMod("TowerAbyssMonsterIncrease", "Abysses in Map spawn % increased Monsters", "suffix", "Abyss") { MinRoll = 20, MaxRoll = 30 },
            new TabletMod("TowerAbyssRareMonsterIncrease", "additional Rare Monsters are spawned from Abysses in Map", "suffix", "Abyss") { MinRoll = 1, MaxRoll = 2 },
            new TabletMod("TowerAbyssEffectivenessPerChasm", "Abyssal Monsters have % increased Effectiveness for each closed Pit, up to 100%", "suffix", "Abyss") { MinRoll = 8, MaxRoll = 12 },
            new TabletMod("TowerAbyssDepthsChance", "Abysses in Map have % increased chance to lead to an Abyssal Depths", "suffix", "Abyss") { MinRoll = 10, MaxRoll = 20 },
            new TabletMod("TowerAbyssAdditionalChance", "Map contains an additional Abyss", "suffix", "Abyss") { MinRoll = 1, MaxRoll = 1 },
            new TabletMod("TowerAbyssIncreasedRewards", "Abyss Pits in Map are twice as likely to have Rewards", "suffix", "Abyss"),
            new TabletMod("TowerAbyss4AdditionalChance", "Map has % chance to contain four additional Abysses", "suffix", "Abyss") { MinRoll = 20, MaxRoll = 40 },
            new TabletMod("TowerAbyssExtraModifiers", "% increased chance for Abyssal monsters in Map to have Abyssal Modifiers", "suffix", "Abyss") { MinRoll = 20, MaxRoll = 30 },
            new TabletMod("TowerAbyssExtraTickets", "% increased chance for Desecrated Currency from Abysses in Map", "suffix", "Abyss") { MinRoll = 20, MaxRoll = 30 },
            new TabletMod("TowerAbyssEnhancedMonstersPerChasm", "Abyssal Monsters in Map have increased Difficulty and Reward for each closed Pit", "suffix", "Abyss"),

            // --- Expedition Tablet Specific Suffixes ---
            new TabletMod("TowerExpeditionExplosionRadius", "% increased Expedition Explosive Area of Effect in Map", "suffix", "Expedition") { MinRoll = 5, MaxRoll = 10 },
            new TabletMod("TowerExpeditionLogbookIncrease", "% increased Quantity of Expedition Logbooks dropped by Runic Monsters in Map", "suffix", "Expedition") { MinRoll = 15, MaxRoll = 25 },
            new TabletMod("TowerExpeditionRunicMonsters", "Map contains % increased number of Runic Monster Markers", "suffix", "Expedition") { MinRoll = 15, MaxRoll = 25 },
            new TabletMod("TowerExpeditionIncreasedVerisium", "Monsters from Verisium Remnants drop % increased Verisium", "suffix", "Expedition") { MinRoll = 15, MaxRoll = 25 },
            new TabletMod("TowerExpeditionUnearthedRares", "The first # unearthed Runic Monsters will be Rare Monsters in Map", "suffix", "Expedition") { MinRoll = 1, MaxRoll = 2 },
            new TabletMod("TowerExpeditionAdditionalSentinels", "Expeditions contain # Additional Verisium Sentries in Map", "suffix", "Expedition") { MinRoll = 1, MaxRoll = 2 },
            new TabletMod("TowerExpeditionIncreasedMonsterRarity", "% increased Expedition Monster Rarity in Map", "suffix", "Expedition") { MinRoll = 15, MaxRoll = 25 },
            new TabletMod("TowerExpeditionFrozenBosses", "Expeditions contain # Additional Bosses encased in ice in Map", "suffix", "Expedition") { MinRoll = 1, MaxRoll = 2 },
            new TabletMod("TowerExpeditionBuriedStrongboxes", "Expeditions contain # buried Strongboxes in Map", "suffix", "Expedition") { MinRoll = 1, MaxRoll = 2 },
            new TabletMod("TowerExpeditionVaalRemnants", "Expeditions contain # Vaal Relics in Map", "suffix", "Expedition") { MinRoll = 1, MaxRoll = 2 },
            new TabletMod("TowerExpeditionChanceForVerisiumRemnant", "Expeditions have +% Surpassing chance to contain an additional Verisium Remnant", "suffix", "Expedition") { MinRoll = 30, MaxRoll = 40 },
            new TabletMod("TowerExpeditionChanceForTwinnedElites", "Expeditions have +% Surpassing chance to Duplicate Runic Monsters in Map", "suffix", "Expedition") { MinRoll = 30, MaxRoll = 40 },
            new TabletMod("TowerExpedition2RunicModPassoverChance", "Verisium Remnants have +% chance to add an additional Runic Modifier in Map", "suffix", "Expedition") { MinRoll = 25, MaxRoll = 35 },
            new TabletMod("TowerExpeditionRelicModEffect", "% increased Effect of Expedition Remnants in Map", "suffix", "Expedition") { MinRoll = 12, MaxRoll = 18 },
            new TabletMod("TowerExpeditionRareMonsters", "% increased number of Rare Expedition Monsters in Map", "suffix", "Expedition") { MinRoll = 25, MaxRoll = 40 },
            new TabletMod("TowerExpeditionRelicIncrease", "Expeditions in Map have +Remnants", "suffix", "Expedition") { MinRoll = 1, MaxRoll = 2 },
            new TabletMod("TowerExpeditionExplosionPlacement", "% increased Expedition Explosive Placement Range in Map", "suffix", "Expedition") { MinRoll = 15, MaxRoll = 30 },
            new TabletMod("TowerExpeditionArtifactIncrease", "% increased quantity of Expedition Artifacts dropped by Monsters in Map", "suffix", "Expedition") { MinRoll = 15, MaxRoll = 30 },
        };

        public static readonly List<JewelMod> AllJewelMods = LoadJewelMods();

        public static string EquipmentLoadError { get; private set; } = string.Empty;
        public static string EquipmentSourceVersion { get; private set; } = string.Empty;
        public static readonly List<EquipmentMod> AllEquipmentMods = LoadEquipmentMods();

        private static List<EquipmentMod> LoadEquipmentMods()
        {
            try
            {
                var asmDir = System.IO.Path.GetDirectoryName(typeof(ModDatabase).Assembly.Location) ?? "";
                string[] candidatePaths =
                {
                    System.IO.Path.Combine(asmDir, "Data", "equipment_mod_ranges.json"),
                    System.IO.Path.Combine(asmDir, "equipment_mod_ranges.json"),
                    System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "Plugins", "StashUtility", "Data", "equipment_mod_ranges.json")
                };

                foreach (var path in candidatePaths)
                {
                    if (!System.IO.File.Exists(path)) continue;
                    if (new System.IO.FileInfo(path).Length > 32 * 1024 * 1024)
                        throw new System.IO.InvalidDataException("Equipment database exceeds the size limit.");

                    var root = Newtonsoft.Json.Linq.JToken.Parse(System.IO.File.ReadAllText(path));
                    Newtonsoft.Json.Linq.JToken rows;
                    if (root is Newtonsoft.Json.Linq.JObject document)
                    {
                        if ((int?)document["SchemaVersion"] != 1)
                            throw new System.IO.InvalidDataException("Unsupported equipment database schema.");
                        EquipmentSourceVersion = (string)document["SourceVersion"] ?? string.Empty;
                        rows = document["Mods"];
                    }
                    else
                    {
                        rows = root;
                    }

                    if (rows is not Newtonsoft.Json.Linq.JArray array || array.Count > 100000)
                        throw new System.IO.InvalidDataException("Equipment database has no valid Mods array.");
                    var records = array.ToObject<List<EquipmentMod>>() ?? new List<EquipmentMod>();
                    var result = new List<EquipmentMod>();
                    var ids = new HashSet<string>(System.StringComparer.Ordinal);
                    var mappings = new HashSet<string>(System.StringComparer.Ordinal);
                    int rejected = 0;
                    for (int index = 0; index < records.Count; index++)
                    {
                        var mod = records[index];
                        if (mod == null || string.IsNullOrWhiteSpace(mod.Id) ||
                            string.IsNullOrWhiteSpace(mod.Category) || !ids.Add(mod.Id) ||
                            !mappings.Add(mod.Category + "\0" + mod.GameModId))
                        {
                            rejected++;
                            continue;
                        }

                        mod.RawRollRanges ??= new List<EquipmentRollRange>();
                        mod.ItemClasses ??= new List<string>();
                        bool validRaw = array[index]["RawRollRanges"] is Newtonsoft.Json.Linq.JArray;
                        foreach (var range in mod.RawRollRanges)
                        {
                            if (range == null || string.IsNullOrWhiteSpace(range.StatId) || range.Min > range.Max)
                            {
                                validRaw = false;
                                break;
                            }
                        }

                        if (string.IsNullOrWhiteSpace(mod.GameModId) || !validRaw ||
                            (mod.Type != "Prefix" && mod.Type != "Suffix"))
                        {
                            mod.MatchStatus = "Unverified";
                            mod.MatchIssue = "Missing exact Mod ID, affix type, or raw stat ranges.";
                        }

                        result.Add(mod);
                    }

                    if (result.Count == 0)
                        throw new System.IO.InvalidDataException("Equipment database contains no usable records.");
                    if (rejected > 0)
                        EquipmentLoadError = $"Equipment database skipped {rejected} invalid or duplicate records.";
                    return result;
                }

                EquipmentLoadError = "equipment_mod_ranges.json was not found.";
            }
            catch (System.Exception exception)
            {
                EquipmentLoadError = exception.Message;
            }

            return new List<EquipmentMod>();
        }

        private static List<JewelMod> LoadJewelMods()
        {
            try
            {
                var asmDir = System.IO.Path.GetDirectoryName(typeof(ModDatabase).Assembly.Location) ?? "";
                string[] candidatePaths = new[]
                {
                    System.IO.Path.Combine(asmDir, "Data", "jewel_mod_ranges.json"),
                    System.IO.Path.Combine(asmDir, "jewel_mod_ranges.json"),
                    System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "Plugins", "StashUtility", "Data", "jewel_mod_ranges.json"),
                    System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "Plugins", "StashUtility", "jewel_mod_ranges.json"),
                    @"c:\Users\Zhu Xian\source\repos\GameHelper2\Plugins\StashUtility\Data\jewel_mod_ranges.json"
                };

                foreach (var p in candidatePaths)
                {
                    if (System.IO.File.Exists(p))
                    {
                        string json = System.IO.File.ReadAllText(p);
                        var list = Newtonsoft.Json.JsonConvert.DeserializeObject<List<JewelMod>>(json);
                        if (list != null && list.Count > 0) return list;
                    }
                }
            }
            catch
            {
                // Fallback empty list if file reading fails
            }
            return new List<JewelMod>();
        }
    }
}
