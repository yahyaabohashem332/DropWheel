"""Draws Petal's app icon: six glassy petals around a hub, like the wheel itself.

Usage: python make_icon.py <output dir>
Writes Petal.ico (16-256 px) and Petal-256.png / Petal-1024.png.
"""
import math
import sys
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFilter

S = 1024
CENTER = S / 2
OUTER = S * 0.47
INNER = S * 0.205
HUB = S * 0.125
GAP = S * 0.05
PETALS = 6


def petal_mask() -> Image.Image:
    mask = Image.new("L", (S, S), 0)
    d = ImageDraw.Draw(mask)
    d.ellipse([CENTER - OUTER, CENTER - OUTER, CENTER + OUTER, CENTER + OUTER], fill=255)
    d.ellipse([CENTER - INNER, CENTER - INNER, CENTER + INNER, CENTER + INNER], fill=0)
    # Cut the gaps between petals, rotated so one petal sits at the top.
    for i in range(PETALS):
        a = math.pi / 2 - 2 * math.pi * (i + 0.5) / PETALS
        dx, dy = math.cos(a), -math.sin(a)
        nx, ny = -dy * GAP / 2, dx * GAP / 2
        far = S
        poly = [
            (CENTER + nx, CENTER + ny),
            (CENTER + dx * far + nx, CENTER + dy * far + ny),
            (CENTER + dx * far - nx, CENTER + dy * far - ny),
            (CENTER - nx, CENTER - ny),
        ]
        d.polygon(poly, fill=0)
    # Round every corner: blur, then threshold.
    mask = mask.filter(ImageFilter.GaussianBlur(S * 0.022))
    return mask.point(lambda v: 255 if v > 128 else 0).filter(ImageFilter.GaussianBlur(1.2))


def gradient(top, bottom) -> Image.Image:
    img = Image.new("RGBA", (S, S))
    px = img.load()
    for y in range(S):
        for x in range(S):
            # Diagonal blend, a little lighter toward the top-left.
            t = min(1.0, max(0.0, (0.65 * y + 0.35 * x) / S))
            px[x, y] = tuple(int(top[k] + (bottom[k] - top[k]) * t) for k in range(3)) + (255,)
    return img


def main(out_dir: Path) -> None:
    out_dir.mkdir(parents=True, exist_ok=True)
    mask = petal_mask()

    canvas = Image.new("RGBA", (S, S), (0, 0, 0, 0))

    # Soft drop shadow.
    shadow = Image.new("RGBA", (S, S), (40, 20, 90, 0))
    shadow.putalpha(mask.point(lambda v: int(v * 0.38)))
    shadow = shadow.filter(ImageFilter.GaussianBlur(S * 0.02))
    canvas.alpha_composite(shadow, (0, int(S * 0.018)))

    petals = gradient((168, 140, 255), (86, 52, 238))
    petals.putalpha(mask)
    canvas.alpha_composite(petals)

    # Glassy highlight on the upper half of each petal.
    highlight = Image.new("L", (S, S), 0)
    hd = ImageDraw.Draw(highlight)
    hd.ellipse([CENTER - OUTER * 1.05, CENTER - OUTER * 1.25, CENTER + OUTER * 1.05, CENTER + OUTER * 0.35], fill=70)
    highlight = ImageChops.multiply(highlight.filter(ImageFilter.GaussianBlur(S * 0.03)), mask)
    white = Image.new("RGBA", (S, S), (255, 255, 255, 0))
    white.putalpha(highlight)
    canvas.alpha_composite(white)

    # Thin bright rim along the petal edges.
    edge = ImageChops.subtract(mask, mask.filter(ImageFilter.MinFilter(9)))
    rim = Image.new("RGBA", (S, S), (255, 255, 255, 0))
    rim.putalpha(edge.point(lambda v: int(v * 0.35)))
    canvas.alpha_composite(rim)

    # Hub.
    hub = Image.new("L", (S, S), 0)
    ImageDraw.Draw(hub).ellipse([CENTER - HUB, CENTER - HUB, CENTER + HUB, CENTER + HUB], fill=255)
    hub = hub.filter(ImageFilter.GaussianBlur(1.2))
    hub_fill = gradient((255, 255, 255), (232, 226, 255))
    hub_fill.putalpha(hub)
    hub_shadow = Image.new("RGBA", (S, S), (60, 30, 140, 0))
    hub_shadow.putalpha(hub.point(lambda v: int(v * 0.35)).filter(ImageFilter.GaussianBlur(S * 0.012)))
    canvas.alpha_composite(hub_shadow, (0, int(S * 0.008)))
    canvas.alpha_composite(hub_fill)

    canvas.save(out_dir / "Petal-1024.png")
    canvas.resize((256, 256), Image.LANCZOS).save(out_dir / "Petal-256.png")
    sizes = [(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)]
    canvas.save(out_dir / "Petal.ico", sizes=sizes)
    print("icon written to", out_dir)


if __name__ == "__main__":
    main(Path(sys.argv[1]))
