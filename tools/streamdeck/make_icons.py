"""Draws the Stream Deck plugin's pictures: white glyphs for the action list, violet keys, and the plugin icon."""
import math
import pathlib
from PIL import Image, ImageDraw

here = pathlib.Path(__file__).resolve().parent
imgs = here / 'com.petal.app.sdPlugin' / 'imgs'
imgs.mkdir(parents=True, exist_ok=True)
logo = here.parent.parent / 'store' / 'logos' / 'tile-icon-300x300.png'
VIOLET = (124, 92, 255)
PINK = (217, 108, 244)


def flower(d, cx, cy, r, width, colour):
    """Five petals around a centre, as Petal's wheel."""
    for k in range(5):
        a = -math.pi / 2 + k * 2 * math.pi / 5
        px, py = cx + math.cos(a) * r * 0.52, cy + math.sin(a) * r * 0.52
        pr = r * 0.42
        d.ellipse([px - pr, py - pr, px + pr, py + pr], outline=colour, width=width)
    cr = r * 0.2
    d.ellipse([cx - cr, cy - cr, cx + cr, cy + cr], fill=colour)


def wheel(d, cx, cy, r, width, colour):
    """A ring cut into petals, with a hub."""
    d.ellipse([cx - r, cy - r, cx + r, cy + r], outline=colour, width=width)
    d.ellipse([cx - r * 0.38, cy - r * 0.38, cx + r * 0.38, cy + r * 0.38], outline=colour, width=width)
    for k in range(6):
        a = k * math.pi / 3
        d.line([cx + math.cos(a) * r * 0.38, cy + math.sin(a) * r * 0.38, cx + math.cos(a) * r, cy + math.sin(a) * r], fill=colour, width=width)


def search(d, cx, cy, r, width, colour):
    """A magnifying glass: the command palette."""
    gr = r * 0.55
    gx, gy = cx - r * 0.18, cy - r * 0.18
    d.ellipse([gx - gr, gy - gr, gx + gr, gy + gr], outline=colour, width=width)
    d.line([gx + gr * 0.72, gy + gr * 0.72, cx + r * 0.85, cy + r * 0.85], fill=colour, width=int(width * 1.4))


def glyph_png(draw_fn, size, path, scale=4):
    big = size * scale
    im = Image.new('RGBA', (big, big), (0, 0, 0, 0))
    draw_fn(ImageDraw.Draw(im), big / 2, big / 2, big * 0.42, max(1, int(big * 0.08)), (255, 255, 255, 255))
    im.resize((size, size), Image.LANCZOS).save(path)


def key_png(draw_fn, size, path, scale=4):
    big = size * scale
    im = Image.new('RGBA', (big, big))
    # A diagonal violet-to-pink gradient, like Petal's own.
    for y in range(big):
        for x in range(0, big, 4):
            t = (x + y) / (2 * big)
            c = tuple(int(VIOLET[i] + (PINK[i] - VIOLET[i]) * t) for i in range(3)) + (255,)
            for dx in range(4):
                if x + dx < big:
                    im.putpixel((x + dx, y), c)
    draw_fn(ImageDraw.Draw(im), big / 2, big * 0.44, big * 0.26, max(1, int(big * 0.045)), (255, 255, 255, 255))
    im.resize((size, size), Image.LANCZOS).save(path)


for name, fn in (('do', flower), ('wheel', wheel), ('commands', search)):
    glyph_png(fn, 20, imgs / f'action-{name}.png')
    glyph_png(fn, 40, imgs / f'action-{name}@2x.png')
    key_png(fn, 72, imgs / f'key-{name}.png')
    key_png(fn, 144, imgs / f'key-{name}@2x.png')
glyph_png(flower, 28, imgs / 'category.png')
glyph_png(flower, 56, imgs / 'category@2x.png')
tile = Image.open(logo).convert('RGBA')
tile.resize((256, 256), Image.LANCZOS).save(imgs / 'plugin.png')
tile.resize((512, 512), Image.LANCZOS).save(imgs / 'plugin@2x.png')
print('icons in', imgs)
