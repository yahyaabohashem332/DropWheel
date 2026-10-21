"""Draws the Store package's tiles and logos from Petal's 1024 px icon.

    python tools/store/make_assets.py

Every image is the icon on a transparent background, with the padding Windows expects for each kind
(the taskbar's targetsize icons are full bleed; tiles keep the icon at about two thirds of the shorter side).
"""
from pathlib import Path

from PIL import Image

root = Path(__file__).resolve().parents[2]
icon = Image.open(root / 'src' / 'Petal.App' / 'Assets' / 'Petal-1024.png').convert('RGBA')
out = Path(__file__).resolve().parent / 'Assets'
out.mkdir(exist_ok=True)


def draw(name: str, width: int, height: int, share: float) -> None:
    canvas = Image.new('RGBA', (width, height), (0, 0, 0, 0))
    side = max(1, round(min(width, height) * share))
    canvas.alpha_composite(icon.resize((side, side), Image.LANCZOS), ((width - side) // 2, (height - side) // 2))
    canvas.save(out / name, optimize=True)


scales = {100: 1.0, 125: 1.25, 150: 1.5, 200: 2.0, 400: 4.0}
for scale, factor in scales.items():
    draw(f'Square44x44Logo.scale-{scale}.png', round(44 * factor), round(44 * factor), 0.92)
    draw(f'Square150x150Logo.scale-{scale}.png', round(150 * factor), round(150 * factor), 0.62)
    draw(f'Wide310x150Logo.scale-{scale}.png', round(310 * factor), round(150 * factor), 0.62)
    draw(f'SmallTile.scale-{scale}.png', round(71 * factor), round(71 * factor), 0.7)
    draw(f'LargeTile.scale-{scale}.png', round(310 * factor), round(310 * factor), 0.6)
    draw(f'StoreLogo.scale-{scale}.png', round(50 * factor), round(50 * factor), 0.9)
    draw(f'SplashScreen.scale-{scale}.png', round(620 * factor), round(300 * factor), 0.5)
# The taskbar, Start's list and File Explorer use the plated and unplated target sizes.
for size in (16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256):
    draw(f'Square44x44Logo.targetsize-{size}.png', size, size, 1.0)
    draw(f'Square44x44Logo.targetsize-{size}_altform-unplated.png', size, size, 1.0)
    draw(f'Square44x44Logo.targetsize-{size}_altform-lightunplated.png', size, size, 1.0)
print(len(list(out.glob('*.png'))), 'images in', out)
