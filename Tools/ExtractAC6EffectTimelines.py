#!/usr/bin/env python3
"""Extract flat per-action effect timelines from a --dump-tae JSON.

Usage:
    python Tools/ExtractAC6EffectTimelines.py [taeDumpJson] [outDir] [animId ...]

Defaults: Tools/LocalOut/c0000_tae_dump.json -> Tools/LocalOut/effect_timelines/,
anim 10900 (a000_010900). Output schema is deliberately flat so Unity's JsonUtility
can read it:

    { "actionId": "a000_010900", "events": [ { "time": 1.07, "ffxId": 600156,
      "dummyPolyId": 2975 } ] }

The dump and the timelines contain game-derived data and stay out of git
(Tools/LocalOut is ignored); only this script is committed.
"""
import json
import os
import sys

dump_path = sys.argv[1] if len(sys.argv) > 1 else "Tools/LocalOut/c0000_tae_dump.json"
out_dir = sys.argv[2] if len(sys.argv) > 2 else "Tools/LocalOut/effect_timelines"
anim_ids = [int(a) for a in sys.argv[3:]] or [10900]

data = json.load(open(dump_path, encoding="utf8"))
os.makedirs(out_dir, exist_ok=True)

for anim_id in anim_ids:
    events = []
    for tae_file in data["taeFiles"]:
        for anim in tae_file["animations"]:
            if anim["id"] != anim_id:
                continue
            for ev in anim["events"]:
                if ev.get("typeName") != "SpawnOneShotFFX":
                    continue
                p = ev.get("parameters") or {}
                ffx = p.get("FFXID")
                dummy = p.get("DummyPolyID")
                if isinstance(ffx, int) and isinstance(dummy, int):
                    events.append({
                        "time": round(ev["startTime"], 4),
                        "ffxId": ffx,
                        "dummyPolyId": dummy,
                    })
            break
        if events:
            break  # TAE groups repeat the same events; first file with the anim wins

    events.sort(key=lambda e: e["time"])
    action_id = f"a000_{anim_id:06d}"
    out_path = os.path.join(out_dir, f"{action_id}.json")
    json.dump({"actionId": action_id, "events": events}, open(out_path, "w"), indent=1)
    print(f"{action_id}: {len(events)} effect events -> {out_path}")
