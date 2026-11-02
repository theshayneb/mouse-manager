"""Draws the KeyShare icon: two screens with a cursor crossing between them.

Usage: python assets/make_icon.py assets/KeyShare.ico preview.png
Needs Pillow. Sizes up to 32 px use a simpler drawing so they stay crisp.
"""
import sys
from PIL import Image, ImageDraw

BLUE = (0, 103, 192)
BLUE_LIGHT = (76, 194, 255)
TEAL = (0, 153, 136)
TEAL_LIGHT = (96, 220, 200)
STAND = (96, 108, 120)
WHITE = (255, 255, 255)
OUTLINE = (24, 32, 40)

def gradient_rect(img, box, top, bottom, radius):
    x0, y0, x1, y1 = box
    w, h = x1 - x0, y1 - y0
    grad = Image.new("RGBA", (w, h))
    gd = ImageDraw.Draw(grad)
    for y in range(h):
        t = y / max(1, h - 1)
        c = tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3)) + (255,)
        gd.line([(0, y), (w, y)], fill=c)
    mask = Image.new("L", (w, h), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, w - 1, h - 1], radius=radius, fill=255)
    img.paste(grad, (x0, y0), mask)

def screen(img, d, x, y, w, h, top, bottom, s):
    # stand
    d.rounded_rectangle([x + w * 0.40, y + h, x + w * 0.60, y + h + 22 * s], radius=3 * s, fill=STAND)
    d.rounded_rectangle([x + w * 0.26, y + h + 18 * s, x + w * 0.74, y + h + 30 * s], radius=6 * s, fill=STAND)
    # bezel and display
    d.rounded_rectangle([x, y, x + w, y + h], radius=18 * s, fill=OUTLINE)
    b = int(9 * s)
    gradient_rect(img, (int(x + b), int(y + b), int(x + w - b), int(y + h - b)), top, bottom, int(10 * s))

def cursor(d, x, y, size):
    # classic arrow pointer, tip at (x, y)
    u = size / 16.0
    pts = [(0, 0), (0, 16), (4, 12.5), (7, 19), (9.6, 17.8), (6.7, 11.5), (12, 11.5)]
    pts = [(x + px * u, y + py * u) for px, py in pts]
    d.polygon(pts, fill=OUTLINE)
    # white fill inset by the outline thickness
    t = 1.3
    inner = [(0+t*0.6, 0+t*1.6), (0+t*0.6, 16-t*1.9), (4, 12.5-t*1.1), (7+t*0.3, 19-t*1.6), (9.6-t*0.9, 17.8-t*0.9), (6.7-t*0.5, 11.5-t*0.75), (12-t*1.9, 11.5-t*0.75)]
    d.polygon([(x + px * u, y + py * u) for px, py in inner], fill=WHITE)

def draw(size):
    S = 1024
    s = S / 256
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    w, h = 112 * s, 92 * s
    screen(img, d, 10 * s, 58 * s, w, h, BLUE_LIGHT, BLUE, s)
    screen(img, d, 134 * s, 58 * s, w, h, TEAL_LIGHT, TEAL, s)
    # cursor straddling the gap between the screens
    cursor(d, 114 * s, 84 * s, 78 * s)
    return img.resize((size, size), Image.LANCZOS)

def draw_small(size):
    # A simpler version that stays crisp at 16-32 px: two screens and a cursor.
    S = 256
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([4, 56, 122, 168], radius=20, fill=BLUE)
    d.rounded_rectangle([134, 56, 252, 168], radius=20, fill=TEAL)
    for cx in (63, 193):
        d.rectangle([cx - 12, 168, cx + 12, 196], fill=STAND)
        d.rectangle([cx - 34, 192, cx + 34, 212], fill=STAND)
    cursor(d, 106, 74, 104)
    return img.resize((size, size), Image.LANCZOS)

if __name__ == "__main__":
    out_ico, out_png = sys.argv[1], sys.argv[2]
    sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
    frames = [draw_small(n) if n <= 32 else draw(n) for n in sizes]
    big = frames[-1]
    big.save(out_ico, format="ICO", sizes=[(n, n) for n in sizes], append_images=frames[:-1])
    # preview: each size at real pixels, plus 16/24/32 zoomed 4x
    sheet = Image.new("RGBA", (960, 640), (243, 243, 243, 255))
    ImageDraw.Draw(sheet).rectangle([0, 320, 960, 640], fill=(32, 32, 32, 255))
    for row, y in ((0, 20), (1, 340)):
        x = 10
        for f in frames:
            sheet.paste(f, (x, y), f); x += f.width + 8
        x = 10
        for f in frames[:4]:
            z = f.resize((f.width * 4, f.height * 4), Image.NEAREST)
            sheet.paste(z, (x, y + 170), z); x += z.width + 10
    sheet.save(out_png)
