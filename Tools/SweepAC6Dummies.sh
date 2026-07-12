#!/bin/bash
# Sweep every available AC6 FLVER's dummies into one text corpus for the reference doc
# Docs/AC6挂点与动画全量参考.md. Usage:
#   AC6_GAME_ROOT="<解包素材根目录>" bash Tools/SweepAC6Dummies.sh
# Requires the built AC6PartToGltf and an exported Tools/LocalOut/c0000_skeleton.json.
set -e
cd "$(dirname "$0")/.."
GAME="${AC6_GAME_ROOT:-Resources/Armored Core 6 Fires of the Rubicon/Armored Core 6 Fires of the Rubicon}"
T=Tools/DotNet/AC6PartToGltf/bin/Release/net9.0/AC6PartToGltf.exe
OUT=Tools/LocalOut/dummy_sweep.txt
: > "$OUT"

dump() { # <flver> <label>
  echo "@@FILE $2" >> "$OUT"
  "$T" --skeleton-json Tools/LocalOut/c0000_skeleton.json --flver "$1" --dump-dummies 2>/dev/null | grep "ref=" >> "$OUT" || true
}

for d in "$GAME"/parts/*/; do
  name=$(basename "$d")
  case "$name" in common_body|cd_a_0000) continue;; esac
  up=$(echo "$name" | tr a-z A-Z)
  f="$d/$up.flver"
  [ -f "$f" ] && dump "$f" "$name"
done

for d in "$GAME"/wp/*/; do
  w=$(basename "$d")
  for f in "$d"WP_L_"${w#wp_}".flver "$d"WP_R_"${w#wp_}".flver "$d"WP_A_"${w#wp_}".flver; do
    [ -f "$f" ] && dump "$f" "$w/$(basename "$f" .flver)"
  done
done

dump "$GAME/chr/AC - c0000/c0000.flver" "c0000"
echo DONE $(grep -c "^@@FILE" "$OUT") files, $(grep -c "ref=" "$OUT") dummies
