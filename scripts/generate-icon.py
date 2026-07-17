# Generates the ShadowWhispr app icon (gold microphone on a dark rounded tile,
# matching the app's #DDB45F-on-#0C0F13 palette).
#
# Run with:
#   python scripts/generate-icon.py
# Produces src/ShadowWhispr/icon.ico plus icon-preview.png for a quick look.

from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
OUT_DIR = ROOT / "src" / "ShadowWhispr"

S = 1024  # supersampled canvas, downscaled per icon size
GOLD = "#DDB45F"
DARK = "#0C0F13"
EDGE = "#1C212B"  # subtle rim so the tile reads on dark taskbars

img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(img)

# Rounded-square tile with a thin rim.
pad = 32
d.rounded_rectangle([pad, pad, S - pad, S - pad], radius=230, fill=DARK, outline=EDGE, width=10)

cx = S // 2

# Microphone capsule.
cap_w, cap_top, cap_bottom = 230, 225, 560
d.rounded_rectangle(
    [cx - cap_w // 2, cap_top, cx + cap_w // 2, cap_bottom],
    radius=cap_w // 2,
    fill=GOLD,
)

# U-shaped cradle under the capsule (bottom half of an ellipse).
d.arc([cx - 195, 320, cx + 195, 785], start=0, end=180, fill=GOLD, width=42)

# Stem and base.
d.line([cx, 785, cx, 825], fill=GOLD, width=42)
d.rounded_rectangle([cx - 115, 804, cx + 115, 846], radius=21, fill=GOLD)

sizes = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]
OUT_DIR.mkdir(parents=True, exist_ok=True)
ico_path = OUT_DIR / "icon.ico"
img.save(ico_path, format="ICO", sizes=sizes)
img.resize((256, 256), Image.LANCZOS).save(OUT_DIR / "icon-preview.png")
print(f"wrote {ico_path}")
