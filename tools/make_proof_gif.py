"""Builds proof/proof.gif from the captioned real captures of a run (proof/frames/*.png). No other image is used.
The frames are taken in the order of the story below, about one minute in all. Captions differ in length, so every frame
is padded at the bottom, in the caption band's colour, to the tallest one: all frames share one size and each replaces
the previous one entirely (disposal 1 over an identical canvas), so no caption is cut and none shows through another.
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
BAND = (28, 27, 25)  # caption band colour of tests/Scenarios/Harness.cs
images, durations, missing = [], [], []
for label, secs in STORY:
    hits = sorted(glob.glob(f"{frames_dir}/*-{label}.png"))
    if not hits:
        missing.append(label)
        continue
    images.append(Image.open(hits[0]).convert("RGB"))
    durations.append(secs * 1000)
if missing:
    sys.exit(f"missing frames: {', '.join(missing)}")
W, H = max(im.width for im in images), max(im.height for im in images)
frames = []
for im in images:
    canvas = Image.new("RGB", (W, H), BAND)
    canvas.paste(im, (0, 0))
    frames.append(canvas.quantize(colors=160, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE))
frames[0].save(out, save_all=True, append_images=frames[1:], duration=durations, loop=0, optimize=False, disposal=1)
print(f"{out}: {len(frames)} frames, {sum(durations) / 1000:.0f} s, {W}x{H}, source heights {[im.height for im in images]}")
