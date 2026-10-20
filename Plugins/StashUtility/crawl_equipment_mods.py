"""Export per-ID PoE2 equipment affixes for the current List<EquipmentMod> loader.

Run from any directory: python path/to/crawl_equipment_mods.py
Like crawl_jewel_mods.py, this uses urllib and JSON without third-party packages.
RePoE supplies real game Mod IDs directly, rather than generated website IDs.
MinRoll/MaxRoll describe the FIRST raw stat only; no multi-stat reduction is made.
"""

import argparse
from collections import Counter
from concurrent.futures import ThreadPoolExecutor
import hashlib
import html
import json
import math
import os
from pathlib import Path
import re
import sys
import tempfile
import time
import urllib.request


SOURCE_URL = "https://repoe-fork.github.io/poe2/"
SOURCE_FILES = ("mods.json", "base_items.json", "mods_by_base.json")
CATEGORIES = (
    "Weapons", "Helmets", "Body Armour", "Gloves", "Boots", "Rings",
    "Amulets", "Belts", "Shields", "Quivers",
)
CLASS_CATEGORY = {
    **dict.fromkeys((
        "One Hand Axe", "Two Hand Axe", "One Hand Mace", "Two Hand Mace",
        "One Hand Sword", "Two Hand Sword", "Bow", "Crossbow", "Claw",
        "Dagger", "Flail", "Spear", "Staff", "Warstaff", "Sceptre", "Wand",
        "Talisman", "TrapTool", "FishingRod",
    ), "Weapons"),
    "Helmet": "Helmets", "Body Armour": "Body Armour", "Gloves": "Gloves",
    "Boots": "Boots", "Ring": "Rings", "Amulet": "Amulets", "Belt": "Belts",
    "Shield": "Shields", "Buckler": "Shields", "Quiver": "Quivers",
}
FIELDS = {"Id", "Name", "Type", "Category", "MinRoll", "MaxRoll", "GameModId",
          "PropertyId", "DisplayText", "StatIds", "RawRollRanges"}
DAMAGE_RANGE_PAIRS = {
    (scope + "minimum_added_" + element + "_damage", scope + "maximum_added_" + element + "_damage")
    for scope in ("local_", "attack_", "allies_in_presence_attack_")
    for element in ("physical", "fire", "cold", "lightning")
}


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"Duplicate JSON key: {key}")
        result[key] = value
    return result


def fetch(name, args):
    if args.source_dir:
        return (args.source_dir / (name or "index.html")).read_bytes()
    request = urllib.request.Request(args.source_url + name, headers={
        "User-Agent": "Mozilla/5.0 GameHelper2-EquipmentModExporter/1.0",
    })
    for attempt in range(3):
        try:
            with urllib.request.urlopen(request, timeout=60) as response:
                return response.read()
        except OSError:
            if attempt == 2:
                raise
            time.sleep(attempt + 1)


def version_of(index):
    match = re.search(r"PoE2\s+version\s+([0-9][0-9A-Za-z._-]*)", index.decode("utf-8"))
    if not match:
        raise ValueError("Missing PoE2 version; source format may have changed")
    return match.group(1)


def clean_text(text):
    text = re.sub(r"\[([^\]|]+)\|([^\]]+)\]", r"\2", text)
    text = re.sub(r"\[([^\]]+)\]", r"\1", text)
    return " ".join(html.unescape(re.sub(r"<[^>]+>", " ", text)).split())


def property_description(text, stats):
    # Group by all ordered stats; grouping does not imply that one scalar roll is supported.
    if not stats or not text:
        return text
    text = re.sub(r"\([+-]?\d+(?:\.\d+)?-[+-]?\d+(?:\.\d+)?\)", "#", text)
    values = {str(stat["min"]) for stat in stats if stat["min"] == stat["max"]}
    if " reduced " in text or " less " in text:
        values.update(str(-stat["min"]) for stat in stats if stat["min"] == stat["max"] and stat["min"] < 0)
    text = re.sub(r"(?<![\w.])-?\d+(?:\.\d+)?(?![\w.])",
                  lambda match: "#" if match.group() in values else match.group(), text)
    # Singular descriptions for these exact counters have the same stat semantics.
    aliases = {
        ("base_number_of_crossbow_bolts", "Loads an additional bolt"): "Loads # additional bolts",
        ("grenade_skill_number_of_additional_projectiles", "Grenade Skills Fire an additional Projectile"): "Grenade Skills Fire # additional Projectiles",
        ("grenade_skill_cooldown_count_+", "Grenade Skills have +# Cooldown Use"): "Grenade Skills have +# Cooldown Uses",
    }
    if len(stats) == 1:
        text = aliases.get((stats[0]["id"], text), text)
    if tuple(stat["id"] for stat in stats) in DAMAGE_RANGE_PAIRS:
        text = re.sub(r"\bAdds # to # (Physical|Fire|Cold|Lightning) Damage\b", r"Adds \1 Damage", text)
    return text


def referenced_ids(group):
    mods = group.get("mods")
    if not isinstance(mods, dict):
        raise ValueError("Applicability group is missing its mods object")
    for kind in ("prefix", "suffix"):
        families = mods.get(kind, {})
        if not isinstance(families, dict):
            raise ValueError("Affix family structure changed")
        for records in families.values():
            if not isinstance(records, dict):
                raise ValueError("Affix ID structure changed")
            yield from records
    conditional = group.get("conditional_mods")
    if conditional is None:
        return
    if not isinstance(conditional, list) or any(not isinstance(value, str) for value in conditional):
        raise ValueError("Conditional Mod ID structure changed")
    yield from conditional


def build_records(sources):
    mods, bases, applicability = (sources[name] for name in SOURCE_FILES)
    if any(not isinstance(value, dict) or not value for value in sources.values()):
        raise ValueError("Source tables must be nonempty objects")
    if len(mods) < 1000 or len(bases) < 100:
        raise ValueError("Source tables unexpectedly small; refusing partial export")
    pairs = set()
    statistics = Counter()
    for groups in applicability.values():
        if not isinstance(groups, dict):
            raise ValueError("Applicability class structure changed")
        for group in groups.values():
            if not isinstance(group, dict) or not isinstance(group.get("bases"), list):
                raise ValueError("Applicability base structure changed")
            categories = set()
            for base_id in group["bases"]:
                base = bases.get(base_id)
                if not isinstance(base, dict):
                    raise ValueError(f"Unknown base in applicability table: {base_id}")
                category = CLASS_CATEGORY.get(base.get("item_class"))
                if (category and base.get("domain") == "item"
                        and base.get("release_state") in ("released", "unique_only")):
                    categories.add(category)
            if not categories:
                continue
            for mod_id in referenced_ids(group):
                for category in categories:
                    pair = (category, mod_id)
                    statistics["DuplicateReferencesMerged"] += int(pair in pairs)
                    pairs.add(pair)

    records, rejected = [], []
    for category, mod_id in sorted(pairs):
        mod = mods.get(mod_id)
        if not isinstance(mod, dict):
            rejected.append({"Category": category, "GameModId": mod_id,
                             "Reason": "No source Mod record; exact ID unconfirmed"})
            continue
        if mod.get("domain") != "item" or mod.get("generation_type") not in ("prefix", "suffix"):
            statistics["NonExplicitOrNonItemSkipped"] += 1
            continue
        stats = mod.get("stats")
        if not isinstance(stats, list) or any(
            not isinstance(stat, dict) or not isinstance(stat.get("id"), str) or not stat["id"]
            or type(stat.get("min")) is not int or type(stat.get("max")) is not int
            or stat["min"] > stat["max"] for stat in stats
        ):
            rejected.append({"Category": category, "GameModId": mod_id,
                             "Reason": "Missing or invalid ordered raw stat ranges"})
            continue
        kind = mod["generation_type"].capitalize()
        title = clean_text(mod.get("name") or mod_id)
        description = clean_text(mod.get("text") or "")
        display_text = property_description(description, stats)
        semantics = "\0".join((category, kind, "\0".join(stat["id"] for stat in stats), display_text))
        property_id = ("EquipmentProperty:" + hashlib.sha256(semantics.encode("utf-8")).hexdigest()[:24]
                       if stats and display_text else f"Equipment_{category.replace(' ', '_')}_{mod_id}")
        if not description:
            description = f"Description unavailable; internal ID: {mod_id}"
            statistics["MissingDisplayDescriptions"] += 1
        # Keep every exact ID separately. Id is an application key, GameModId is the source key.
        records.append({
            "Id": f"Equipment_{category.replace(' ', '_')}_{mod_id}",
            "Name": f"{title} ({kind}): {description}",
            "Type": kind,
            "Category": category,
            "MinRoll": stats[0]["min"] if stats else 0,
            "MaxRoll": stats[0]["max"] if stats else 0,
            "GameModId": mod_id,
            "PropertyId": property_id,
            "DisplayText": display_text or description,
            "StatIds": [stat["id"] for stat in stats],
            "RawRollRanges": [{"StatId": stat["id"], "Min": stat["min"], "Max": stat["max"]} for stat in stats],
        })
        statistics["MultipleRawStatsFirstRangeOnly"] += int(len(stats) > 1)
        statistics["NoNumericStatsZeroRange"] += int(not stats)
    return records, rejected, statistics


def validate(records, sources):
    if not isinstance(records, list) or not records:
        raise ValueError("Output must be a nonempty root array")
    ids, pairs = set(), set()
    for record in records:
        if set(record) != FIELDS or any(not isinstance(record[field], str) or not record[field]
                                       for field in FIELDS - {"MinRoll", "MaxRoll", "StatIds", "RawRollRanges"}):
            raise ValueError("Missing model fields or invalid text fields")
        if record["Category"] not in CATEGORIES or record["Type"] not in ("Prefix", "Suffix"):
            raise ValueError("Category or affix type is incompatible with the current UI")
        if any(type(record[field]) not in (int, float) or not math.isfinite(record[field])
               for field in ("MinRoll", "MaxRoll")) or record["MinRoll"] > record["MaxRoll"]:
            raise ValueError("Invalid scalar raw range")
        pair = (record["Category"], record["GameModId"])
        if record["Id"] in ids or pair in pairs:
            raise ValueError("Duplicate application ID or category/Mod ID pair")
        ids.add(record["Id"])
        pairs.add(pair)
        source_mod = sources["mods.json"].get(record["GameModId"])
        if (not isinstance(source_mod, dict) or source_mod.get("domain") != "item"
                or source_mod.get("generation_type", "").capitalize() != record["Type"]):
            raise ValueError("GameModId is not a confirmed source item affix ID")
        stats = source_mod["stats"]
        if record["StatIds"] != [stat["id"] for stat in stats]:
            raise ValueError("Stat semantics do not match the source Mod record")
        if record["RawRollRanges"] != [{"StatId": stat["id"], "Min": stat["min"], "Max": stat["max"]} for stat in stats]:
            raise ValueError("Full raw ranges do not match the source Mod record")
        expected = (stats[0]["min"], stats[0]["max"]) if stats else (0, 0)
        if (record["MinRoll"], record["MaxRoll"]) != expected:
            raise ValueError("Scalar range does not match the first source raw stat")
    counts = Counter(record["Category"] for record in records)
    if set(counts) != set(CATEGORIES):
        raise ValueError("One or more supported equipment categories are empty")
    families = {}
    for record in records:
        semantic_key = (record["Category"], record["Type"], tuple(record["StatIds"]), record["DisplayText"])
        if record["PropertyId"] in families and families[record["PropertyId"]] != semantic_key:
            raise ValueError("Conflicting property grouping semantics")
        families[record["PropertyId"]] = semantic_key
    return counts


def backup_existing(output, directory):
    if not output.exists():
        return
    original = output.read_bytes()
    digest = hashlib.sha256(original).hexdigest()
    directory.mkdir(parents=True, exist_ok=True)
    backup = directory / f"{output.stem}.{digest}.backup.json"
    if backup.exists():
        if backup.read_bytes() != original:
            raise ValueError("Existing backup differs; refusing replacement")
    else:
        with backup.open("xb") as stream:
            stream.write(original)
    if backup.read_bytes() != original:
        raise ValueError("Backup verification failed")
    print(f"Backup: {backup} (SHA256 {digest})", flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-url", default=SOURCE_URL)
    parser.add_argument("--source-dir", type=Path, help="Offline source directory with index.html and the three JSON tables")
    parser.add_argument("--output", type=Path, default=Path(__file__).resolve().parent / "Data" / "equipment_mod_ranges.json")
    parser.add_argument("--backup-dir", type=Path, default=Path(tempfile.gettempdir()) / "GameHelper2-equipment-mod-backups")
    args = parser.parse_args()
    args.source_url = args.source_url.rstrip("/") + "/"
    try:
        backup_existing(args.output, args.backup_dir)
        print(f"Fetching PoE2 sources: {args.source_dir or args.source_url}", flush=True)
        version = version_of(fetch("", args))
        with ThreadPoolExecutor(max_workers=3) as executor:
            raw = dict(zip(SOURCE_FILES, executor.map(lambda name: fetch(name, args), SOURCE_FILES)))
        if version_of(fetch("", args)) != version:
            raise ValueError("Source version changed during download; rerun the export")
        sources = {name: json.loads(value.decode("utf-8-sig"), object_pairs_hook=unique_object)
                   for name, value in raw.items()}
        records, rejected, statistics = build_records(sources)
        for record in rejected:
            print("Rejected: " + json.dumps(record, ensure_ascii=False), flush=True)
        if len(rejected) > max(10, len(records) // 20):
            raise ValueError("Too many unconfirmed records; refusing partial replacement")
        content = json.dumps(records, ensure_ascii=False, indent=2, allow_nan=False) + "\n"
        counts = validate(json.loads(content), sources)
        args.output.parent.mkdir(parents=True, exist_ok=True)
        if args.output.exists() and args.output.read_text(encoding="utf-8-sig") == content:
            print(f"Unchanged: {args.output}")
        else:
            temporary = None
            try:
                with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", newline="\n",
                                                 dir=args.output.parent, delete=False) as stream:
                    temporary = Path(stream.name)
                    stream.write(content)
                    stream.flush()
                    os.fsync(stream.fileno())
                os.replace(temporary, args.output)
            finally:
                if temporary and temporary.exists():
                    temporary.unlink()
            print(f"Saved: {args.output}")
        print(f"Source: {args.source_url} | PoE2 version: {version}")
        print("Source SHA256: " + json.dumps({name: hashlib.sha256(value).hexdigest() for name, value in raw.items()}))
        print("Categories: " + json.dumps(dict(sorted(counts.items()))))
        print(f"Records: {len(records)}; distinct real Mod IDs: {len({row['GameModId'] for row in records})}; rejected: {len(rejected)}")
        print(f"UI properties: {len({row['PropertyId'] for row in records})}; full ordered stat semantics retained.")
        print("Statistics: " + json.dumps(dict(sorted(statistics.items()))))
        print("Validation: root array, exact model fields, unique IDs, all categories, and source Mod IDs/ranges passed.")
        print("MinRoll/MaxRoll are FIRST raw stat bounds only, not display units or a multi-stat score.")
        print("Internal IDs were verified against source data, not a live game client.")
        return 0
    except (OSError, ValueError, TypeError, KeyError) as error:
        print(f"Equipment output not replaced: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
