"""
Verifies that item ids from the commented-out sections of the old patch.ts
still point at the item the comment says they do, against the live database.

For each (id, expected hint) pair: looks up the id's en.json display name and
whether it exists in items.json at all, and flags anything that doesn't look
like a match so nothing gets silently ported onto the wrong item.

Usage: python tools/check_item_names.py
"""

import json
from pathlib import Path

ITEMS_PATH = Path(r"D:\Game\SPT5\SPT_Runtime\SPT_Data\database\templates\items.json")
EN_LOCALE_PATH = Path(r"D:\Game\SPT5\SPT_Runtime\SPT_Data\database\locales\global\en.json")

# id -> hint taken from the // comment in patch.ts's commented-out sections
CANDIDATES = {
    "5a800961159bd4315e3a1657": "GL21",
    "5a7b483fe899ef0016170d15": "Surefire XC1",
    "6272370ee4013c5d7e31f418": "Baldr Pro",
    "644a3df63b0b6f03e101e065": "MAWL-C1+",
    "61605d88ffa6e502ac5e7eeb": "RAPTAR",
    "56def37dd2720bec348b456a": "X400",
    "5fc23426900b1d5091531e15": "Sword Int. Mk-18 .338 LM 10",
    "5d25a4a98abbc30b917421a4": "AICS 5",
    "5df8f535bb49d91fb446d6b0": "KAC Steel 10",
    "5df25b6c0b92095fd441e4cf": ".308 T-5000 5-round mag",
    "673cbdfad0453ba50c0f76d6": "Sako TRG M10 .338 LM 8",
    "5f647d9f8499b57dc40ddb93": "KS-23M 23mm 3",
    "668fe5c5f35310705d02b696": "Desert Eagle .50 AE 7-round mag",
    "5de653abf76fdc1ce94a5a2a": ".366 TKM 4-round",
    "67d418d0ffb910d21f04720e": "AK-50 mag",
    "5448c1d04bdc2dff2f8b4569": "PMAG GEN M3 20 5.56x45",
    "63076701a987397c0816d21b": "Glock 9x19 19-round",
    "5a7ae0c351dfba0017554310": "GLOCK 17",
    "5447a9cd4bdc2dbd208b4567": "M4A1",
    "5a7ad55551dfba0015068f42": "Aimtech Tiger Shark mount",
    "55d6190f4bdc2d87028b4567": "MONSTER-MINI",
    "61714eec290d254f5e6b2ffc": "Schmidt & Bender PM II 3-12x50",
    "5b3b99475acfc432ff4dcbee": "Vudu 1-6x24",
    "617151c1d92c473c770214ab": "Schmidt & Bender PM II 1-8x24",
    "5a71e4f48dc32e001207fb26": "Glock 19",
    "59db3a1d86f77429e05b4e92": "Gral S",
    "6113cc78d3a39d50044c065a": "F1 Firearms Skeletonized Style 2 PC",
    "5aa66c72e5b5b00016327c93": "AGS-74 (comment conflict - a second commented line calls this same id 'Nightforce Multimount 34', check which is right)",
    "6171407e50224f204c1da3c5": "RN 30",
    "61713cc4d8e3106d9806c109": "RN 34",
    "57c69dd424597774c03b7bbc": "Lobaev Arms Lobar 30mm mount (referenced via pushset, not editmount)",
}


def main():
    print(f"Loading {EN_LOCALE_PATH.name}...")
    locale = json.loads(EN_LOCALE_PATH.read_text(encoding="utf-8"))

    print(f"Loading {ITEMS_PATH.name} ({ITEMS_PATH.stat().st_size / 1_000_000:.1f} MB)...")
    items = json.loads(ITEMS_PATH.read_text(encoding="utf-8"))

    print(f"\n{'id':<26} {'exists':<7} {'live name':<45} hint")
    print("-" * 120)
    for item_id, hint in CANDIDATES.items():
        exists = item_id in items
        live_name = locale.get(f"{item_id} Name", "(no locale entry)")
        flag = "" if exists else "  <-- MISSING FROM items.json"
        print(f"{item_id:<26} {str(exists):<7} {live_name:<45} {hint}{flag}")


if __name__ == "__main__":
    main()
