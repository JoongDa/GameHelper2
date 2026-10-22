"""Offline ranking/gating regressions; fixtures are NOT real game tier data."""

import unittest

from generate_affix_tiers import generate, normalize_mods, rank_pool


def mod(level, group="Life", generation="Prefix", gates=None, domain="Item"):
    return {"domain": domain, "generationType": generation, "group": group,
            "level": level, "tier": 999,
            "spawnWeights": gates if gates is not None else [{"tag": "default", "weight": 1}]}


class AffixTiersTests(unittest.TestCase):
    def test_levels_define_tier_not_id_suffix_or_toolkit_tier(self):
        data = {"Example999": mod(20), "Example1": mod(60), "SameLevel": mod(60)}
        pool = rank_pool(normalize_mods({}, data), {"boots"})
        self.assertEqual(pool, {"Example1": 1, "SameLevel": 1, "Example999": 2})

    def test_first_matching_zero_weight_blocks_later_positive_default(self):
        data = {"Blocked": mod(80, gates=[{"tag": "boots", "weight": 0},
                                          {"tag": "default", "weight": 1}]),
                "Allowed": mod(20)}
        mods = normalize_mods({}, data)
        self.assertEqual(rank_pool(mods, {"boots"}), {"Allowed": 1})
        self.assertEqual(rank_pool(mods, {"ring"}), {"Allowed": 2, "Blocked": 1})

    def test_suffix_unique_and_monster_do_not_inflate_prefix_ladder(self):
        data = {"Prefix": mod(20), "Suffix": mod(80, generation="Suffix"),
                "Unique": mod(99, generation="Unique"), "Monster": mod(100, domain="Monster")}
        self.assertEqual(rank_pool(normalize_mods({}, data), set()), {"Prefix": 1, "Suffix": 1})

    def test_base_context_changes_pool_but_drop_level_does_not_rebase_tiers(self):
        tables = {"Tags": [{"Id": "str_armour"}], "ItemClasses": [{"Id": "Boots"}, {"Id": "Ring"}],
                  "BaseItemTypes": [{"Id": "Metadata/Items/Armours/Boots/Low", "ModDomain": 1,
                                     "ItemClass": 0, "Tags": [0], "DropLevel": 1},
                                    {"Id": "Metadata/Items/Armours/Boots/High", "ModDomain": 1,
                                     "ItemClass": 0, "Tags": [0], "DropLevel": 80},
                                    {"Id": "Metadata/Items/Rings/Example", "ModDomain": 1,
                                     "ItemClass": 1, "Tags": []}]}
        data = {"Low": mod(20), "High": mod(80, gates=[{"tag": "boots", "weight": 1},
                                                       {"tag": "default", "weight": 0}])}
        result = generate(tables, "fixture-only", data)
        low, high, ring = [result["Bases"][base["Id"]] for base in tables["BaseItemTypes"]]
        self.assertEqual(low, high)
        self.assertEqual(result["Profiles"][low], {"High": 1, "Low": 2})
        self.assertEqual(result["Profiles"][ring], {"Low": 1})

    def test_decoded_rows_and_toolkit_export_agree(self):
        tables = {"Mods": [{"Id": "Example", "Domain": 1, "GenerationType": 1,
                            "ModType": 0, "Level": 54, "SpawnWeight_Tags": [0], "SpawnWeight_Values": [1]}],
                  "ModType": [{"Name": "Life"}], "Tags": [{"Id": "default"}]}
        self.assertEqual(normalize_mods(tables), normalize_mods({}, {"Example": mod(54)}))

    def test_irrelevant_base_tags_share_the_same_ranked_pool(self):
        tables = {"Tags": [{"Id": "cosmetic_a"}, {"Id": "cosmetic_b"}],
                  "ItemClasses": [{"Id": "Boots"}],
                  "BaseItemTypes": [{"Id": "BaseA", "ModDomain": 1, "ItemClass": 0, "Tags": [0]},
                                    {"Id": "BaseB", "ModDomain": 1, "ItemClass": 0, "Tags": [1]}]}
        result = generate(tables, "fixture-only", {"Example": mod(54)})
        self.assertEqual(result["Bases"]["BaseA"], result["Bases"]["BaseB"])
        self.assertEqual(len(result["Profiles"]), 1)

    def test_missing_level_and_misaligned_tag_arrays_fail(self):
        with self.assertRaisesRegex(ValueError, "level"):
            normalize_mods({}, {"Bad": mod(None)})
        tables = {"Mods": [{"Id": "Bad", "Domain": 1, "GenerationType": 1,
                            "ModType": 0, "Level": 1, "SpawnWeight_Tags": [0], "SpawnWeight_Values": []}],
                  "ModType": [{"Name": "Life"}], "Tags": []}
        with self.assertRaisesRegex(ValueError, "spawn weights"):
            normalize_mods(tables)


if __name__ == "__main__":
    unittest.main()
