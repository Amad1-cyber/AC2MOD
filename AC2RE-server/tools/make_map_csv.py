"""Normalize positioned world objects for the AC2RE map loader."""
import argparse
import csv
import math
from collections import Counter
from pathlib import Path

SERVER_ROOT = Path(__file__).resolve().parents[1]
ID_LIMITS = {"id": 2**64, "entityDid": 2**32, "landblockId": 2**16, "cellId": 2**16}
POSITION_FIELDS = ("posX", "posY", "posZ", "rotX", "rotY", "rotZ", "rotW", "scale")
NAME_FIELDS = ("nameStringId", "nameTableDid")
FIELDS = ("packageType", *ID_LIMITS, *POSITION_FIELDS, *NAME_FIELDS)


def optional_name_id(value):
    text = value.strip()
    try:
        number = int(text, 16 if text.lower().startswith("0x") else 10)
    except ValueError:
        return "NULL"
    return str(number) if 0 < number < 2**32 else "NULL"


def convert(source, destination):
    kept, dropped, seen = [], Counter(), set()
    with source.open(newline="", encoding="utf-8-sig") as input_csv:
        reader = csv.DictReader(input_csv)
        if reader.fieldnames != list(FIELDS):
            raise ValueError("Expected a MapDbGen CSV with the standard map columns.")
        for row in reader:
            if row["packageType"] in {"Corpse", "PlayerAvatar"}:
                dropped[row["packageType"]] += 1
                continue
            try:
                ids = {field: int(row[field], 10) for field in ID_LIMITS}
                position = [float(row[field]) for field in POSITION_FIELDS]
                if not all(0 <= ids[field] < ceiling for field, ceiling in ID_LIMITS.items()):
                    raise ValueError
                if not ids["id"] or not ids["entityDid"] or not all(math.isfinite(v) for v in position):
                    raise ValueError
                names = [optional_name_id(row[field]) for field in NAME_FIELDS]
            except (TypeError, ValueError, AttributeError):
                dropped["invalid map row"] += 1
                continue
            if ids["id"] in seen:
                dropped["duplicate id"] += 1
                continue
            row.update(ids)
            row.update(zip(NAME_FIELDS, names if "NULL" not in names else ("NULL", "NULL")))
            seen.add(ids["id"])
            kept.append(row)

    if not kept:
        raise ValueError("The input contains no valid world placements.")
    with destination.open("w", newline="", encoding="utf-8") as output_csv:
        writer = csv.DictWriter(output_csv, fieldnames=FIELDS, extrasaction="ignore")
        writer.writeheader()
        writer.writerows(kept)

    print(f"Validated {len(kept)} placements across {len({row['landblockId'] for row in kept})} landblocks.")
    print("Discarded rows:", dict(dropped))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", nargs="?", type=Path, default=SERVER_ROOT / "obj_all.csv")
    parser.add_argument("destination", nargs="?", type=Path, default=SERVER_ROOT / "map_obj.csv")
    args = parser.parse_args()
    convert(args.source, args.destination)

