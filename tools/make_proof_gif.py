"""Builds proof/proof.gif from the captioned real captures of a run (proof/frames/*.png). No other image is used.
The frames are taken in the order of the story below, at the capture's own width, about one minute in all.
Usage: python3 tools/make_proof_gif.py proof/frames proof/proof.gif   (needs Pillow)"""
import sys, glob
from PIL import Image

frames_dir, out = sys.argv[1], sys.argv[2]
# (frame label suffix, seconds on screen)
STORY = [
    ("14-freeze-after-post-payment-then-freeze", 9),
    ("14-freeze-after-post-restart-refused", 9),
    ("23-guard-only-defense-guard-only-defense", 9),
    ("03-correct-write-seed-21-correct-write", 8),
    ("06-duplicate-rows-ambiguity-blocks", 8),
    ("18-ocr-lookalike-glyphs-lookalike-refused", 8),
    ("28-real-wrong-click-wrong-click-caught", 9),
]
frames, durations, missing = [], [], []
for label, secs in STORY:
    hits = sorted(glob.glob(f"{frames_dir}/*-{label}.png"))
    if not hits:
        missing.append(label)
        continue
    im = Image.open(hits[0]).convert("RGB")
    frames.append(im.quantize(colors=160, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE))
    durations.append(secs * 1000)
if missing:
    sys.exit(f"missing frames: {', '.join(missing)}")
frames[0].save(out, save_all=True, append_images=frames[1:], duration=durations, loop=0, optimize=True)
print(f"{out}: {len(frames)} frames, {sum(durations) / 1000:.0f} s, {frames[0].width}x{frames[0].height}")
