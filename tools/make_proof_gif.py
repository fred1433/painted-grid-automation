"""Builds proof/proof.gif from the captioned real captures of a run (proof/frames/*.png). No other image is used.
Usage: python3 tools/make_proof_gif.py proof/frames proof/proof.gif [seconds-per-frame]   (needs Pillow)"""
import sys, glob
from PIL import Image

frames_dir, out = sys.argv[1], sys.argv[2]
secs = float(sys.argv[3]) if len(sys.argv) > 3 else 9
frames = []
for path in sorted(glob.glob(f"{frames_dir}/*.png")):
    im = Image.open(path).convert("RGB")
    w = 880
    im = im.resize((w, round(im.height * w / im.width)), Image.LANCZOS)
    frames.append(im.quantize(colors=128, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE))
frames[0].save(out, save_all=True, append_images=frames[1:], duration=int(secs * 1000), loop=0, optimize=True)
print(f"{out}: {len(frames)} frames, {len(frames) * secs:.0f} s")
