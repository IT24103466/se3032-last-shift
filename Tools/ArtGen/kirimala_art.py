"""
Kirimala Estate - generated environment art (World Builder).

Makes the textures that could not be downloaded:
  * TeaLeaves      tiling PBR set for the tea bushes (colour, normal, roughness, AO)
  * TeaChest       plywood tea chest with a stencilled "CEYLON TEA" mark and metal straps
  * Sign_*         trilingual (English / Sinhala / Tamil) estate and factory signs

Requirements: Python 3, Pillow built with libraqm (needed to shape Sinhala and Tamil
correctly), numpy, and these OFL fonts (from npm, @fontsource/...):
  noto-sans-sinhala, noto-sans-tamil, oswald, allerta-stencil
Usage:  python kirimala_art.py <fonts_dir> <unity_project_dir>
"""
import math, os, random, sys
import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

FONTS, PROJECT = sys.argv[1], sys.argv[2]
ENV = os.path.join(PROJECT, "Assets/_Project/Art/Environment")

def font(name, size):
    files = {
        "si": "fontsource-noto-sans-sinhala-5.3.0/files/noto-sans-sinhala-sinhala-700-normal.woff",
        "ta": "fontsource-noto-sans-tamil-5.3.0/files/noto-sans-tamil-tamil-700-normal.woff",
        "en": "fontsource-oswald-5.3.0/files/oswald-latin-700-normal.woff",
        "st": "allerta/files/allerta-stencil-latin-400-normal.woff",
    }
    return ImageFont.truetype(os.path.join(FONTS, files[name]), size, layout_engine=ImageFont.Layout.RAQM)

def noise(w, h, scale, seed, octaves=4):
    """Tileable value noise in 0..1."""
    rng = np.random.default_rng(seed)
    out = np.zeros((h, w), np.float32); amp = 1.0; total = 0.0
    for o in range(octaves):
        g = max(2, int(scale * 2 ** o))
        grid = rng.random((g, g)).astype(np.float32)
        img = Image.fromarray((grid * 255).astype(np.uint8)).resize((w, h), Image.BICUBIC)
        # make it wrap: blend with a shifted copy
        a = np.asarray(img, np.float32) / 255
        out += a * amp; total += amp; amp *= 0.5
    return out / total

def save(arr, path, mode="RGB"):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    arr = np.clip(arr, 0, 1)
    Image.fromarray((arr * 255 + 0.5).astype(np.uint8), mode).save(path)

# ------------------------------------------------------------------ tea leaves
def tea_leaves(size=1024, seed=7):
    rng = random.Random(seed)
    color = Image.new("RGB", (size, size), (22, 40, 16))
    height = Image.new("L", (size, size), 0)
    dc, dh = ImageDraw.Draw(color), ImageDraw.Draw(height)
    n = 9000
    for i in range(n):
        x, y = rng.uniform(0, size), rng.uniform(0, size)
        L = rng.uniform(16, 36); W = L * rng.uniform(0.38, 0.5); ang = rng.uniform(0, math.pi)
        fresh = rng.random() < 0.16                       # light-green new flush (two leaves and a bud)
        if fresh: c = (rng.randint(95, 125), rng.randint(130, 160), rng.randint(40, 60))
        else:
            k = rng.uniform(0.75, 1.25)
            c = (int(30 * k), int(70 * k), int(26 * k))
        z = int(60 + 195 * i / n)                         # later leaves sit on top
        pts = []
        for t in range(16):                               # pointed leaf outline
            a = 2 * math.pi * t / 16
            r = (abs(math.cos(a)) ** 0.8) * L / 2
            px, py = math.cos(a) * L / 2, math.sin(a) * W / 2 * (1 - 0.3 * abs(math.cos(a)))
            pts.append((px * math.cos(ang) - py * math.sin(ang), px * math.sin(ang) + py * math.cos(ang)))
        for ox in (-size, 0, size):                       # draw wrapped copies so the tile is seamless
            for oy in (-size, 0, size):
                if -L < x + ox < size + L and -L < y + oy < size + L:
                    poly = [(x + ox + px, y + oy + py) for px, py in pts]
                    dc.polygon(poly, fill=c); dh.polygon(poly, fill=z)
    col = np.asarray(color, np.float32) / 255
    h = np.asarray(height.filter(ImageFilter.GaussianBlur(1.5)), np.float32) / 255
    # leaf shading: centre vein and soft edges
    col *= (0.75 + 0.35 * h)[..., None]
    # normal map (OpenGL, +Y up) from height
    gx = (np.roll(h, -1, 1) - np.roll(h, 1, 1)) * 3.0
    gy = (np.roll(h, -1, 0) - np.roll(h, 1, 0)) * 3.0
    nrm = np.dstack([-gx, gy, np.ones_like(h)]); nrm /= np.linalg.norm(nrm, axis=2, keepdims=True)
    ao = np.asarray(Image.fromarray((h * 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(6)), np.float32) / 255
    ao = np.clip(0.45 + 0.75 * ao, 0, 1)
    rough = np.where(h > 0.2, 0.5, 0.85).astype(np.float32)   # waxy leaves, matte gaps
    d = os.path.join(ENV, "Textures/TeaLeaves")
    save(col, f"{d}/TeaLeaves_Color.png")
    save(nrm * 0.5 + 0.5, f"{d}/TeaLeaves_NormalGL.png")
    save(rough, f"{d}/TeaLeaves_Roughness.png", "L")
    save(ao, f"{d}/TeaLeaves_AmbientOcclusion.png", "L")

# ------------------------------------------------------------------ tea chest
def tea_chest(size=512, seed=3):
    g = noise(size, size, 3, seed)
    grain = (np.sin(np.linspace(0, 60, size))[None, :] * 0.5 + 0.5) * 0.08
    base = np.array([0.70, 0.55, 0.37]) * (0.85 + 0.25 * g + grain)[..., None]
    img = Image.fromarray((np.clip(base, 0, 1) * 255).astype(np.uint8))
    d = ImageDraw.Draw(img)
    strap = (58, 58, 56)
    b = 26
    for r in [(0, 0, size, b), (0, size - b, size, size), (0, 0, b, size), (size - b, 0, size, size)]:
        d.rectangle(r, fill=strap)
    for x in range(b // 2, size, 64):                     # rivets
        for y in (b // 2, size - b // 2):
            d.ellipse((x - 4, y - 4, x + 4, y + 4), fill=(110, 108, 100))
            d.ellipse((y - 4, x - 4, y + 4, x + 4), fill=(110, 108, 100))
    ink = (25, 22, 20)
    def centre(text, f, y):
        w = d.textlength(text, font=f); d.text(((size - w) / 2, y), text, font=f, fill=ink)
    centre("CEYLON", font("st", 92), 110)
    centre("TEA", font("st", 120), 205)
    centre("KIRIMALA ESTATE", font("st", 34), 350)
    centre("BOPF   NET 50 KG", font("st", 30), 395)
    arr = np.asarray(img, np.float32) / 255
    fade = 0.85 + 0.15 * noise(size, size, 6, seed + 1)   # worn paint and dirt
    arr = arr * fade[..., None]
    save(arr, os.path.join(ENV, "Props/TeaChest_Color.png"))

# ------------------------------------------------------------------ signs
SIGNS = {
    # name: (width m, height m, background, text colour, accent, [English, Sinhala, Tamil])
    "Sign_Estate":   (3.0, 1.5, (28, 72, 46), (238, 232, 205), (200, 170, 90),
                      ["KIRIMALA ESTATE", "කිරිමල වත්ත", "கிரிமலா தோட்டம்"]),
    "Sign_Factory":  (6.0, 1.5, (236, 230, 205), (24, 70, 44), (24, 70, 44),
                      ["KIRIMALA TEA FACTORY", "කිරිමල තේ කර්මාන්තශාලාව", "கிரிமலா தேயிலை தொழிற்சாலை"]),
    "Sign_Store":    (2.4, 1.0, (236, 230, 205), (24, 70, 44), (24, 70, 44),
                      ["STORE ROOM", "ගබඩා කාමරය", "களஞ்சிய அறை"]),
    "Sign_Boiler":   (2.0, 1.0, (238, 196, 40), (20, 20, 20), (170, 30, 25),
                      ["DANGER  BOILER ROOM", "අනතුරුයි   බොයිලේරු කාමරය", "அபாயம்   கொதிகலன் அறை"]),
    "Sign_LeafShed": (2.8, 0.9, (28, 72, 46), (238, 232, 205), (200, 170, 90),
                      ["LEAF COLLECTION SHED", "දළු එකතු කරන මඩුව", "கொழுந்து சேகரிப்பு கொட்டகை"]),
    "Sign_MainGate": (2.4, 1.0, (28, 72, 46), (238, 232, 205), (200, 170, 90),
                      ["MAIN GATE", "ප්‍රධාන ගේට්ටුව", "பிரதான வாயில்"]),
}

def fit(d, text, kind, max_w, max_h, lang):
    s = int(max_h * 1.4)
    while s > 8:
        f = font(kind, s)
        l, t, r, b = d.textbbox((0, 0), text, font=f, language=lang)
        if r - l <= max_w and b - t <= max_h: return f, (l, t, r, b)
        s -= 2
    return font(kind, 8), d.textbbox((0, 0), text, font=font(kind, 8), language=lang)

def sign(name, w_m, h_m, bg, fg, accent, lines, ppm=320, seed=11):
    W, H = int(w_m * ppm), int(h_m * ppm)
    img = Image.new("RGB", (W, H), bg); d = ImageDraw.Draw(img)
    border = max(6, H // 28)
    d.rectangle((border, border, W - border, H - border), outline=accent, width=border)
    if name == "Sign_Boiler":                              # red warning band behind the English line
        d.rectangle((2 * border, 2 * border, W - 2 * border, int(H * 0.40)), fill=accent)
    pad = 3 * border
    rows = [("en", None, 0.36), ("si", "si", 0.30), ("ta", "ta", 0.24)]
    inner_h = H - 2 * pad; y = pad
    for (kind, lang, frac), text in zip(rows, lines):
        row_h = inner_h * frac / sum(r[2] for r in rows) * 0.98
        f, (l, t, r, b) = fit(d, text, kind, W - 2 * pad, row_h * 0.82, lang)
        colour = (245, 240, 220) if (name == "Sign_Boiler" and kind == "en") else fg
        d.text(((W - (r - l)) / 2 - l, y + (row_h - (b - t)) / 2 - t), text, font=f, fill=colour, language=lang)
        y += row_h
    arr = np.asarray(img, np.float32) / 255
    grime = 0.82 + 0.18 * noise(W, H, 4, seed + len(name))    # weathered paint
    streak = 1 - 0.08 * noise(W, H, 12, seed) * np.linspace(0.2, 1, H)[:, None]   # rain streaks downwards
    save(arr * (grime * streak)[..., None], os.path.join(ENV, f"Signs/{name}.png"))

if __name__ == "__main__":
    tea_leaves(); tea_chest()
    for k, v in SIGNS.items(): sign(k, *v)
    print("done")
