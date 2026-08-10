# Generates the ShadowWhispr app icon: a luminous whisper wave inside a soft
# "shadow" tile, in the app's Midnight Aurora palette (#7C6BF2 -> #46D9F5 on #12111C).
#
# Run with:
#   python scripts/generate-icon.py
# Produces src/ShadowWhispr/icon.ico plus icon-preview.png for a quick look.

from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parent.parent
OUT_DIR = ROOT / "src" / "ShadowWhispr"

S = 1024  # supersampled canvas, downscaled per icon size
TILE = (18, 17, 28, 255)      # #12111C
EDGE = (65, 63, 99, 255)      # #413F63 rim so the tile reads on dark taskbars
IRIS = (124, 107, 242)        # #7C6BF2
CYAN = (70, 217, 245)         # #46D9F5


def gradient(size, top, bottom):
    """Vertical iris -> aurora-cyan ramp used to fill the wave."""
    ramp = Image.new("RGB", (1, size), top)
    px = ramp.load()
    for y in range(size):
        t = y / max(size - 1, 1)
        px[0, y] = tuple(round(top[i] + (bottom[i] - top[i]) * t) for i in range(3))
    return ramp.resize((size, size), Image.BILINEAR)


img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(img)

# Rounded-square tile with a thin rim.
pad = 32
d.rounded_rectangle([pad, pad, S - pad, S - pad], radius=230, fill=TILE, outline=EDGE, width=8)

cx = S // 2
cy = S // 2

# Whisper wave: seven rounded bars that rise and fall around the centre line.
mask = Image.new("L", (S, S), 0)
md = ImageDraw.Draw(mask)
heights = [120, 250, 400, 560, 400, 250, 120]
bar_w = 58
gap = 42
total = len(heights) * bar_w + (len(heights) - 1) * gap
x = cx - total // 2
for h in heights:
    md.rounded_rectangle([x, cy - h // 2, x + bar_w, cy + h // 2], radius=bar_w // 2, fill=255)
    x += bar_w + gap

wave = Image.new("RGBA", (S, S), (0, 0, 0, 0))
wave.paste(gradient(S, IRIS, CYAN).convert("RGBA"), (0, 0), mask)

# Soft glow behind the wave so it reads as light rather than paint.
glow = wave.filter(ImageFilter.GaussianBlur(38))
glow.putalpha(glow.getchannel("A").point(lambda a: int(a * 0.55)))
img.alpha_composite(glow)
img.alpha_composite(wave)

sizes = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]
OUT_DIR.mkdir(parents=True, exist_ok=True)
ico_path = OUT_DIR / "icon.ico"
img.save(ico_path, format="ICO", sizes=sizes)
img.resize((256, 256), Image.LANCZOS).save(OUT_DIR / "icon-preview.png")
print(f"wrote {ico_path}")
