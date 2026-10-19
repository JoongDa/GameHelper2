"""Export exact PoE2 equipment affixes and raw stat ranges using only stdlib.

Run from any directory: python path/to/crawl_equipment_mods.py
RePoE exports the game Mods records keyed by their real internal IDs. The
translated text is display-only; numeric filtering uses the ordered raw stats.
No affix ranking or display-unit-to-raw conversion is performed.
"""

import argparse
from collections import Counter, defaultdict
from concurrent.futures import ThreadPoolExecutor
import hashlib
import html
import json
import os
from pathlib import Path
import re
import sys
import tempfile
import time
import urllib.error
import urllib.request


DEFAULT_SOURCE = "https://repoe-fork.github.io/poe2/"
CLASS_CATEGORY = {
    "One Hand Axe": "Weapons", "Two Hand Axe": "Weapons",
    "One Hand Mace": "Weapons", "Two Hand Mace": "Weapons",
    "One Hand Sword": "Weapons", "Two Hand Sword": "Weapons",
    "Bow": "Weapons", "Crossbow": "Weapons", "Claw": "Weapons",
    "Dagger": "Weapons", "Flail": "Weapons", "Spear": "Weapons",
    "Staff": "Weapons", "Warstaff": "Weapons", "Sceptre": "Weapons",
    "Wand": "Weapons", "Talisman": "Weapons", "TrapTool": "Weapons",
    "FishingRod": "Weapons", "Helmet": "Helmets",
    "Body Armour": "BodyArmours", "Gloves": "Gloves", "Boots": "Boots",
    "Ring": "Rings", "Amulet": "Amulets", "Belt": "Belts",
    "Shield": "Shields", "Buckler": "Shields", "Quiver": "Quivers",
    "Focus": "Focus",
}
SOURCE_FILES = ("mods.json", "base_items.json", "mods_by_base.json")
# User-provided debug samples are compatibility checks, not client verification.
DEBUG_SAMPLES = {
    "Strength7": [29], "LocalIncreasedAccuracy7": [263],
    "LocalCriticalMultiplier6": [23], "ManaLeechLocal4": [713],
    "LocalAddedPhysicalDamageTwoHand5": [20, 33],
}


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"Duplicate JSON key in source: {key}")
        result[key] = value
    return result


def read_source(name, args):
    if args.source_dir:
        return (args.source_dir / name).read_bytes()
    request = urllib.request.Request(
        args.source_url + name,
        headers={"User-Agent": "GameHelper2-EquipmentModExporter/1.0"},
    )
    for attempt in range(3):
        try:
            with urllib.request.urlopen(request, timeout=90) as response:
                return response.read()
        except (urllib.error.URLError, TimeoutError):
            if attempt == 2:
                raise
            time.sleep(attempt + 1)


def source_version(index_bytes):
    text = index_bytes.decode("utf-8")
    match = re.search(r"PoE2\s+version\s+([0-9][0-9A-Za-z._-]*)", text)
    if not match:
        raise ValueError("Source version header missing; source format may have changed")
    return match.group(1)


def clean_text(text):
    text = re.sub(r"\[([^\]|]+)\|([^\]]+)\]", r"\2", text)
    text = re.sub(r"\[([^\]]+)\]", r"\1", text)
    text = re.sub(r"<[^>]+>", " ", text)
    return " ".join(html.unescape(text).split())


def require_dict(value, description):
    if not isinstance(value, dict) or not value:
        raise ValueError(f"Missing or changed source object: {description}")
    return value


def source_mod_ids(group):
    mods = group.get("mods")
    if not isinstance(mods, dict):
        raise ValueError("mods_by_base group has no mods object")
    for generation, families in mods.items():
        if generation not in ("prefix", "suffix"):
            continue
        if not isinstance(families, dict):
            raise ValueError("mods_by_base affix families changed shape")
        for records in families.values():
            if not isinstance(records, dict):
                raise ValueError("mods_by_base affix ID map changed shape")
            yield from records
    conditional = group.get("conditional_mods")
    if conditional is not None:
        if not isinstance(conditional, list) or any(not isinstance(x, str) for x in conditional):
            raise ValueError("mods_by_base conditional_mods changed shape")
        yield from conditional


def parse_raw_ranges(mod):
    stats = mod.get("stats")
    if not isinstance(stats, list):
        return [], "Missing ordered raw stats"
    ranges = []
    for stat in stats:
        if not isinstance(stat, dict):
            return [], "Invalid stat record"
        stat_id, minimum, maximum = stat.get("id"), stat.get("min"), stat.get("max")
        if (not isinstance(stat_id, str) or not stat_id
                or type(minimum) is not int or type(maximum) is not int
                or minimum < -2147483648 or maximum > 2147483647
                or minimum > maximum):
            return [], "Missing stat ID or invalid raw integer range"
        ranges.append({"StatId": stat_id, "Min": minimum, "Max": maximum})
    return ranges, ""


def build_database(sources, version, args):
    mods = require_dict(sources["mods.json"], "mods")
    bases = require_dict(sources["base_items.json"], "base_items")
    by_base = require_dict(sources["mods_by_base.json"], "mods_by_base")
    if len(mods) < 1000 or len(bases) < 100:
        raise ValueError("Unexpectedly small source export; refusing partial replacement")
    applicability = defaultdict(lambda: {"classes": set(), "bases": set()})
    counts = Counter()
    for groups in by_base.values():
        require_dict(groups, "mods_by_base class groups")
        for group in groups.values():
            if not isinstance(group, dict) or not isinstance(group.get("bases"), list):
                raise ValueError("mods_by_base base list changed shape")
            categories = defaultdict(lambda: {"classes": set(), "bases": set()})
            for base_id in group["bases"]:
                base = bases.get(base_id)
                if not isinstance(base, dict):
                    raise ValueError(f"Applicability index references missing base: {base_id}")
                category = CLASS_CATEGORY.get(base.get("item_class"))
                if (not category or base.get("domain") != "item"
                        or base.get("release_state") not in ("released", "unique_only")):
                    continue
                categories[category]["classes"].add(base["item_class"])
                categories[category]["bases"].add(base_id)
            if not categories:
                continue
            for mod_id in source_mod_ids(group):
                counts["ApplicabilityReferences"] += 1
                for category, context in categories.items():
                    key = (category, mod_id)
                    if key in applicability:
                        counts["DuplicateReferencesMerged"] += 1
                    applicability[key]["classes"].update(context["classes"])
                    applicability[key]["bases"].update(context["bases"])

    rows = []
    for (category, mod_id), context in sorted(applicability.items()):
        mod = mods.get(mod_id)
        problem = ""
        if not isinstance(mod, dict):
            mod = {}
            problem = "Applicability index references missing Mod record"
        if mod and (mod.get("domain") != "item"
                    or mod.get("generation_type") not in ("prefix", "suffix")):
            counts["NonExplicitOrNonItemSkipped"] += 1
            continue
        ranges, range_problem = parse_raw_ranges(mod)
        problem = problem or range_problem
        name = mod.get("name")
        text = mod.get("text")
        if not isinstance(name, str) or not name.strip():
            name = mod_id
            counts["MissingDisplayNames"] += 1
        if not isinstance(text, str):
            text = ""
            counts["MissingDisplayText"] += 1
        # The JSON key is the true Mods.dat ID; our row ID also includes category.
        rows.append({
            "Id": f"Equipment:{category}:{mod_id}",
            "GameModId": mod_id if mod else "",
            "Name": clean_text(name), "DisplayText": clean_text(text),
            "Category": category,
            "Type": mod.get("generation_type", "").capitalize(),
            "RawRollRanges": ranges,
            "RawValuesAreGameUnits": not bool(problem),
            "Source": args.source_url + "mods.json",
            "SourceVersion": version,
            "MatchStatus": "Unverified" if problem else "Exact",
            "MatchIssue": problem,
            "LiveVerified": False,
            "ItemClasses": sorted(context["classes"]),
        })
    category_counts = Counter(row["Category"] for row in rows)
    missing = set(CLASS_CATEGORY.values()) - set(category_counts)
    if missing:
        raise ValueError(f"No affixes found for expected categories: {', '.join(sorted(missing))}")
    counts.update({
        "Rows": len(rows),
        "DistinctGameModIds": len({r["GameModId"] for r in rows if r["GameModId"]}),
        "ExactRows": sum(r["MatchStatus"] == "Exact" for r in rows),
        "UnverifiedRows": sum(r["MatchStatus"] != "Exact" for r in rows),
        "MultipleRawValues": sum(len(r["RawRollRanges"]) > 1 for r in rows),
        "MoreThanTwoRawValues": sum(len(r["RawRollRanges"]) > 2 for r in rows),
    })
    if counts["UnverifiedRows"] > max(10, len(rows) // 20):
        raise ValueError("Too many incomplete Mod records; source format may have changed")
    checks = []
    for mod_id, values in DEBUG_SAMPLES.items():
        ranges, problem = parse_raw_ranges(mods.get(mod_id, {}))
        compatible = (not problem and len(ranges) == len(values)
                      and all(r["Min"] <= value <= r["Max"] for r, value in zip(ranges, values)))
        checks.append({"GameModId": mod_id, "ObservedRawValues": values,
                       "SourceRangeCompatible": compatible})
    return {
        "SchemaVersion": 1,
        "Source": args.source_url,
        "SourceVersion": version,
        "RawValueUnits": "Unscaled integer game stats in source order; DisplayText is display-only",
        "LiveClientVerified": False,
        "Statistics": dict(sorted(counts.items())),
        "Categories": dict(sorted(category_counts.items())),
        "DebugSampleCheck": checks,
        "Mods": rows,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-url", default=DEFAULT_SOURCE,
                        help="RePoE PoE2 directory URL, including its version index")
    parser.add_argument("--source-dir", type=Path,
                        help="Use an existing export directory with index.html and the three JSON files")
    parser.add_argument("--output", type=Path,
                        default=Path(__file__).resolve().parent / "Data" / "equipment_mod_ranges.json")
    parser.add_argument("--allow-large-change", action="store_true",
                        help="Permit a greater than 25%% record reduction after reviewing upstream changes")
    args = parser.parse_args()
    args.source_url = args.source_url.rstrip("/") + "/"
    try:
        before = source_version(read_source("index.html" if args.source_dir else "", args))
        with ThreadPoolExecutor(max_workers=len(SOURCE_FILES)) as executor:
            raw_sources = dict(zip(SOURCE_FILES, executor.map(lambda name: read_source(name, args), SOURCE_FILES)))
        after = source_version(read_source("index.html" if args.source_dir else "", args))
        if before != after:
            raise ValueError("Upstream version changed during download; rerun for a consistent export")
        sources = {name: json.loads(raw.decode("utf-8-sig"), object_pairs_hook=unique_object)
                   for name, raw in raw_sources.items()}
        database = build_database(sources, before, args)
        database["SourceHashes"] = {
            name: hashlib.sha256(raw).hexdigest() for name, raw in sorted(raw_sources.items())
        }
        if args.output.exists() and not args.allow_large_change:
            previous = json.loads(args.output.read_text(encoding="utf-8-sig"))
            old_rows = previous if isinstance(previous, list) else previous.get("Mods", [])
            if len(database["Mods"]) < len(old_rows) * 0.75:
                raise ValueError("Record count dropped over 25%; review source before --allow-large-change")
        args.output.parent.mkdir(parents=True, exist_ok=True)
        content = json.dumps(database, ensure_ascii=False, indent=2) + "\n"
        if args.output.exists() and args.output.read_text(encoding="utf-8") == content:
            print(f"Unchanged: {args.output}")
        else:
            temp_path = None
            try:
                with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", newline="\n",
                                                 dir=args.output.parent, delete=False) as output:
                    temp_path = Path(output.name)
                    output.write(content)
                    output.flush()
                    os.fsync(output.fileno())
                os.replace(temp_path, args.output)
            finally:
                if temp_path and temp_path.exists():
                    temp_path.unlink()
            print(f"Saved: {args.output}")
        print(f"PoE2 source version: {before}")
        print(json.dumps(database["Statistics"], indent=2))
        print("Categories: " + json.dumps(database["Categories"]))
        print("Debug sample source-range compatibility: "
              + str(sum(x["SourceRangeCompatible"] for x in database["DebugSampleCheck"]))
              + f"/{len(DEBUG_SAMPLES)}; no live client verification claimed")
        return 0
    except (OSError, ValueError, TypeError, KeyError) as error:
        print(f"Equipment database not replaced: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
