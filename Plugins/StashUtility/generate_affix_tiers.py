"""Generate an equipment affix lookup from pathofexile-dat's decoded tables.

Reference: rajtik76/poe2-toolkit packages/poe2-mod-extractor/src/buildMods.ts.
Keep its domain/generation/ModType separation and ordered spawn-tag gate, but
rank DISTINCT levels descending (T1 best), separately for each base's tag pool.
The upstream `tier` field and numeric Mod ID suffixes are deliberately unused.
No network, game-process access, or third-party Python dependencies.
"""

import argparse
import hashlib
import json
from pathlib import Path


ALGORITHM = "toolkit-context-level-desc-v1"

# BaseItemTypes.Tags only includes base-specific tags. Add item-class tags,
# following toolkit's buildItems/effectiveTags contract. Unknown classes fail
# closed rather than silently receiving an incomplete equipment affix pool.
CLASS_TAGS = {
    "Amulet": ["amulet"], "Belt": ["belt"], "Ring": ["ring"],
    "Quiver": ["quiver"], "Body Armour": ["armour", "body_armour"],
    "Boots": ["armour", "boots"], "Gloves": ["armour", "gloves"],
    "Helmet": ["armour", "helmet"], "Shield": ["armour", "shield"],
    "Buckler": ["armour", "shield", "buckler"], "Focus": ["armour", "focus"],
    "Sceptre": ["sceptre", "onehand"], "Wand": ["wand", "onehand"],
    "Staff": ["staff", "twohand"], "TrapTool": ["trap", "twohand"],
    "FishingRod": ["fishing_rod", "twohand"],
}
for _class, _tag in {
    "Bow": "bow", "Crossbow": "crossbow", "Two Hand Axe": "axe",
    "Two Hand Mace": "mace", "Two Hand Sword": "sword", "Warstaff": "warstaff",
    "Talisman": "talisman",
}.items():
    CLASS_TAGS[_class] = ["weapon", _tag, "two_hand_weapon", "twohand"]
    if _class in ("Bow", "Crossbow"):
        CLASS_TAGS[_class].append("ranged")
for _class, _tag in {
    "Claw": "claw", "Dagger": "dagger", "Flail": "flail", "Spear": "spear",
    "One Hand Axe": "axe", "One Hand Mace": "mace", "One Hand Sword": "sword",
}.items():
    CLASS_TAGS[_class] = ["weapon", _tag, "one_hand_weapon", "onehand"]


def integer(value, field):
    if type(value) is not int or value < 0:
        raise ValueError(f"{field}: expected non-negative integer, got {value!r}")
    return value


def reference(rows, index, field):
    integer(index, field)
    if index >= len(rows):
        raise ValueError(f"{field}: foreign row {index} is out of range")
    return rows[index]


def load_table(directory, name):
    path = directory / f"{name}.json"
    rows = json.loads(path.read_text(encoding="utf-8-sig"))
    if not isinstance(rows, list) or any(not isinstance(row, dict) for row in rows):
        raise ValueError(f"{path}: expected an array of decoded table rows")
    return rows


def normalize_mods(tables, toolkit_mods=None):
    """Use toolkit's keyed export when supplied, or its equivalent decoded fields."""
    if toolkit_mods is not None:
        if not isinstance(toolkit_mods, dict):
            raise ValueError("--mods must be toolkit's keyed mods.json object")
        source = toolkit_mods.items()
    else:
        source = []
        for row in tables["Mods"]:
            # Only normal equipment prefixes/suffixes belong to this feature.
            if row.get("Domain") != 1 or row.get("GenerationType") not in (1, 2):
                continue
            if row.get("ModType") is None:
                continue
            group = reference(tables["ModType"], row["ModType"], "ModType").get("Name")
            tag_refs, weights = row.get("SpawnWeight_Tags"), row.get("SpawnWeight_Values")
            if not isinstance(tag_refs, list) or not isinstance(weights, list) or len(tag_refs) != len(weights):
                raise ValueError(f"{row.get('Id')}: invalid ordered spawn weights")
            gates = [{"tag": reference(tables["Tags"], tag, "SpawnWeight_Tags")["Id"],
                      "weight": integer(weight, "SpawnWeight_Values")}
                     for tag, weight in zip(tag_refs, weights)]
            source.append((row.get("Id"), {
                "domain": "Item", "generationType": "Prefix" if row["GenerationType"] == 1 else "Suffix",
                "group": group, "level": row.get("Level"), "spawnWeights": gates,
            }))

    mods = {}
    for mod_id, row in source:
        if row.get("domain") != "Item" or row.get("generationType") not in ("Prefix", "Suffix"):
            continue
        if row.get("group") is None:
            continue
        if not isinstance(mod_id, str) or not mod_id or mod_id in mods:
            raise ValueError(f"Invalid or duplicate Mod ID: {mod_id!r}")
        if not isinstance(row["group"], str) or not row["group"]:
            raise ValueError(f"{mod_id}: missing ModType name")
        gates = row.get("spawnWeights")
        if not isinstance(gates, list):
            raise ValueError(f"{mod_id}: missing ordered spawnWeights")
        for gate in gates:
            if not isinstance(gate.get("tag"), str) or not gate["tag"]:
                raise ValueError(f"{mod_id}: invalid spawn tag")
            integer(gate.get("weight"), f"{mod_id}.weight")
        mods[mod_id] = {"level": integer(row.get("level"), f"{mod_id}.level"),
                        "group": row["group"], "generation": row["generationType"], "gates": gates}
    if not mods:
        raise ValueError("No equipment prefixes/suffixes found; check input format and schema")
    return mods


def eligible(mod, tags):
    # FIRST matching tag decides. A zero blocks; don't skip to a later positive.
    return next((gate["weight"] > 0 for gate in mod["gates"]
                 if gate["tag"] == "default" or gate["tag"] in tags), False)


def rank_pool(mods, tags):
    ladders = {}
    pool = {mod_id: mod for mod_id, mod in mods.items() if eligible(mod, tags)}
    for mod in pool.values():
        ladders.setdefault((mod["group"], mod["generation"]), set()).add(mod["level"])
    ranks = {key: {level: i + 1 for i, level in enumerate(sorted(levels, reverse=True))}
             for key, levels in ladders.items()}
    return {mod_id: ranks[(mod["group"], mod["generation"])][mod["level"]]
            for mod_id, mod in sorted(pool.items())}


def generate(tables, source_version, toolkit_mods=None):
    if not source_version.strip():
        raise ValueError("A game/data version is required")
    mods = normalize_mods(tables, toolkit_mods)
    bases, profiles, tag_profiles = {}, {}, {}
    for base in tables["BaseItemTypes"]:
        if base.get("ModDomain") != 1 or base.get("ItemClass") is None:
            continue
        class_id = reference(tables["ItemClasses"], base["ItemClass"], "ItemClass").get("Id")
        if class_id not in CLASS_TAGS:
            continue
        base_id = base.get("Id")
        if not isinstance(base_id, str) or not base_id or base_id in bases:
            raise ValueError(f"Invalid or duplicate base ID: {base_id!r}")
        if not isinstance(base.get("Tags"), list):
            raise ValueError(f"{base_id}: missing base Tags")
        tags = set(CLASS_TAGS[class_id]) | {"default"}
        tags.update(reference(tables["Tags"], index, "BaseItemTypes.Tags")["Id"] for index in base["Tags"])
        tag_key = tuple(sorted(tags))
        if tag_key not in tag_profiles:
            pool = rank_pool(mods, tags)
            # Many cosmetic/base tags do not change any affix. Share the actual
            # ranked pool, not just identical tag sets, to keep runtime data small.
            pool_bytes = json.dumps(pool, sort_keys=True, separators=(",", ":")).encode()
            profile_id = hashlib.sha256(pool_bytes).hexdigest()[:16]
            if profile_id in profiles and profiles[profile_id] != pool:
                raise ValueError("Affix pool hash collision")
            profiles[profile_id] = pool
            tag_profiles[tag_key] = profile_id
        bases[base_id] = tag_profiles[tag_key]
    if not bases or not any(profiles.values()):
        raise ValueError("No supported equipment bases/affix pools; check table schema")
    return {"SchemaVersion": 1, "Algorithm": ALGORITHM, "SourceVersion": source_version,
            "TierConvention": "T1Best", "Levels": {key: mod["level"] for key, mod in sorted(mods.items())},
            "Bases": dict(sorted(bases.items())), "Profiles": dict(sorted(profiles.items()))}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tables", type=Path, required=True, help="Directory of decoded JSON tables")
    parser.add_argument("--mods", type=Path, help="Optional toolkit mods.json (its tier values are ignored)")
    parser.add_argument("--source-version", required=True, help="Version of the extracted game data")
    parser.add_argument("--source-url", help="Optional provenance URL for the extracted game data")
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    try:
        names = ["Tags", "BaseItemTypes", "ItemClasses"]
        if not args.mods:
            names += ["Mods", "ModType"]
        tables = {name: load_table(args.tables, name) for name in names}
        exported = json.loads(args.mods.read_text(encoding="utf-8-sig")) if args.mods else None
        result = generate(tables, args.source_version, exported)
        result["InputSha256"] = {
            name: hashlib.sha256((args.tables / f"{name}.json").read_bytes()).hexdigest()
            for name in names
        }
        if args.mods:
            result["InputSha256"]["ToolkitMods"] = hashlib.sha256(args.mods.read_bytes()).hexdigest()
        if args.source_url:
            result["SourceUrl"] = args.source_url
        args.out.parent.mkdir(parents=True, exist_ok=True)
        temporary = args.out.with_suffix(args.out.suffix + ".tmp")
        temporary.write_text(json.dumps(result, ensure_ascii=False, separators=(",", ":")) + "\n", encoding="utf-8")
        temporary.replace(args.out)
    except (OSError, ValueError, KeyError, TypeError, AttributeError) as error:
        parser.exit(1, f"Cannot generate affix tiers: {error}\n")
    print(f"Generated {len(result['Bases'])} bases, {len(result['Profiles'])} pools, "
          f"{len(result['Levels'])} mod levels; data version {args.source_version}")


if __name__ == "__main__":
    main()
