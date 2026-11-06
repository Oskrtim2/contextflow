#!/usr/bin/env python3
"""Generate ContextFlow's multi-resolution Windows application icon.

Usage:
    python tools/generate_icon.py
Requires: Pillow
"""
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / "src" / "ContextFlow" / "Assets" / "AppIcon.ico"

CANVAS = 1024
img = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))
draw = ImageDraw.Draw(img)

# Soft rounded app tile with an indigo -> violet diagonal gradient.
gradient = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))
grad_draw = ImageDraw.Draw(gradient)
start = (99, 102, 241)
end = (139, 92, 246)
for y in range(CANVAS):
    for_x_shift = int(y * 0.45)
    t = y / (CANVAS - 1)
    color = tuple(int(start[i] * (1 - t) + end[i] * t) for i in range(3)) + (255,)
    grad_draw.line([(0, y), (CANVAS, y)], fill=color)

mask = Image.new("L", (CANVAS, CANVAS), 0)
mask_draw = ImageDraw.Draw(mask)
margin = 40
radius = 232
mask_draw.rounded_rectangle(
    (margin, margin, CANVAS - margin, CANVAS - margin), radius=radius, fill=255
)
img.paste(gradient, (0, 0), mask)

# Capsule lanes.
left, right = 232, 792
top = 350
gap = 162
main_width = 58
secondary_width = 38
draw.rounded_rectangle(
    (left, top - main_width // 2, right, top + main_width // 2),
    radius=main_width // 2,
    fill=(255, 255, 255, 250),
)
draw.rounded_rectangle(
    (left + 36, top + gap - secondary_width // 2, right - 42, top + gap + secondary_width // 2),
    radius=secondary_width // 2,
    fill=(255, 255, 255, 150),
)
draw.rounded_rectangle(
    (left, top + gap * 2 - main_width // 2, right, top + gap * 2 + main_width // 2),
    radius=main_width // 2,
    fill=(255, 255, 255, 250),
)

# Amber flow node.
draw.ellipse((right - 92, top - 46, right + 46, top + 92), fill=(242, 196, 90, 255))
draw.ellipse((right - 52, top - 6, right + 6, top + 52), fill=(255, 255, 255, 235))

# Supersample down for crisp small sizes.
master = img.resize((512, 512), Image.Resampling.LANCZOS)
sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
OUTPUT.parent.mkdir(parents=True, exist_ok=True)
master.save(OUTPUT, format="ICO", sizes=[(s, s) for s in sizes])
print(f"Wrote {OUTPUT} ({OUTPUT.stat().st_size} bytes)")
