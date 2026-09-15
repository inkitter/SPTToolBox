"""
Diagnostic for patches/profileTemplates/unheard-usec-hideout-containers.json.

Checks, against the live templates/profiles.json:
1. What parentId every existing 'slotId: hideout' item actually uses under
   Unheard.usec specifically (not guessed from other profile sections).
2. Whether the placeholder _id values in that patch file collide with any
   _id already present in Unheard.usec's Inventory.items.

Usage: python tools/check_hideout_ids.py [path to profiles.json]
Defaults to D:/Game/SPT5/SPT_Runtime/SPT_Data/database/templates/profiles.json
"""

import json
import sys
from pathlib import Path

DEFAULT_PROFILES_PATH = r"D:\Game\SPT5\SPT_Runtime\SPT_Data\database\templates\profiles.json"

PLACEHOLDER_IDS = [
    "690000000000000000000001", "690000000000000000000002", "690000000000000000000003",
    "690000000000000000000004", "690000000000000000000005", "690000000000000000000006",
    "690000000000000000000007", "690000000000000000000008", "690000000000000000000009",
    "69000000000000000000000a", "69000000000000000000000b", "69000000000000000000000c",
    "69000000000000000000000d", "69000000000000000000000e",
]


def main():
    profiles_path = Path(sys.argv[1] if len(sys.argv) > 1 else DEFAULT_PROFILES_PATH)
    if not profiles_path.exists():
        print(f"ERROR: profiles.json not found at {profiles_path}")
        sys.exit(1)

    data = json.loads(profiles_path.read_text(encoding="utf-8"))

    account_type = "Unheard"
    side = "usec"

    try:
        character = data[account_type][side]["character"]
    except KeyError as e:
        print(f"ERROR: couldn't navigate {account_type}.{side}.character - missing key {e}")
        print(f"Top-level account types available: {list(data.keys())}")
        sys.exit(1)

    items = character.get("Inventory", {}).get("items", [])
    print(f"{account_type}.{side}: {len(items)} items in Inventory.items\n")

    hideout_parent_ids = set()
    for item in items:
        if item.get("slotId") == "hideout":
            hideout_parent_ids.add(item.get("parentId"))

    print("parentId values seen on existing slotId='hideout' items:")
    if not hideout_parent_ids:
        print("  (none found - this profile has no hideout-slotted items yet)")
    for pid in hideout_parent_ids:
        print(f"  {pid}")
    if len(hideout_parent_ids) > 1:
        print("  WARNING: more than one distinct parentId - patch file assumption of a single fixed id is wrong.")

    all_ids = {item.get("_id") for item in items}
    collisions = [pid for pid in PLACEHOLDER_IDS if pid in all_ids]
    print(f"\nPlaceholder _id collisions against existing Inventory.items ({len(PLACEHOLDER_IDS)} checked):")
    if collisions:
        print(f"  COLLISION: {collisions}")
    else:
        print("  none - placeholder ids are safe to use as-is")


if __name__ == "__main__":
    main()
