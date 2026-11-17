#!/usr/bin/env bash
# Builds proof/proof.gif from the captioned real captures of a run (proof/frames/*.png). No other image is used.
# Usage: tools/make-proof-gif.sh proof/frames proof/proof.gif [seconds-per-frame]
set -euo pipefail
frames="$1"; out="$2"; secs="${3:-9}"
list="$(mktemp)"
for f in "$frames"/*.png; do printf "file '%s'\nduration %s\n" "$(cd "$(dirname "$f")" && pwd)/$(basename "$f")" "$secs" >> "$list"; done
last="$(ls "$frames"/*.png | tail -1)"; printf "file '%s'\n" "$(cd "$(dirname "$last")" && pwd)/$(basename "$last")" >> "$list"
ffmpeg -y -loglevel error -f concat -safe 0 -i "$list" -vf "fps=1,scale=880:-1:flags=lanczos,split[a][b];[a]palettegen=max_colors=96[p];[b][p]paletteuse=dither=none" "$out"
rm -f "$list"
ls -la "$out"
