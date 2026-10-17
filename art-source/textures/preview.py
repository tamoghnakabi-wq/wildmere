#!/usr/bin/env python3
"""Contact sheets for the generated block textures (reads the real output PNGs).

    python3 art-source/textures/preview.py

Writes to art-source/textures/preview/ (never under Assets/):
  contact_albedo.png   every layer with its name (sample biome tint applied, checker behind alpha)
  contact_lit.png      every layer lit with normal + mask (fixed light, tint, emission)
  contact_maps.png     normal / AO / smoothness / height / mask-A per layer (debug)
  tiling_<n>.png       3x3 tiling of every full-cube layer (lit), for seam checks
  tints.png            tinted layers under the four sample biome colours
  blocks_iso.png       isometric lit cubes + plant sprites, the way blocks read in-world
  extras.png           crack stages, water normals/foam, cloud noise channels, item icons
Also prints a numeric seam report (edge vs interior texel differences).
"""
import json
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from texlib import Layer, apply_tint, shade  # noqa: E402

ROOT = os.path.abspath(os.path.join(HERE, '..', '..'))
BLOCKS = os.path.join(ROOT, 'Assets', 'Resources', 'Blocks')
TEX = os.path.join(ROOT, 'Assets', 'Resources', 'Textures')
OUT = os.path.join(HERE, 'preview')
TINTS = {'temperate': '#5f9a3a', 'dark forest': '#3f6b2a', 'plains': '#7aad45', 'snowy': '#6f8f6a'}
LAYER_TINT = {'leaves_birch': '#7aad45', 'leaves_spruce': '#6f8f6a', 'leaves_dark': '#3f6b2a'}
BG = (28, 29, 32)


def load():
    meta = json.load(open(os.path.join(BLOCKS, 'blocks.json')))
    t = meta['tile']
    alb = np.asarray(Image.open(os.path.join(BLOCKS, 'blocks_albedo.png')).convert('RGBA'), np.float64) / 255
    nrm = np.asarray(Image.open(os.path.join(BLOCKS, 'blocks_normal.png')).convert('RGB'), np.float64) / 255
    msk = np.asarray(Image.open(os.path.join(BLOCKS, 'blocks_mask.png')).convert('RGBA'), np.float64) / 255
    layers = []
    for i, name in enumerate(meta['layers']):
        r, c = divmod(i, meta['columns'])
        sl = (slice(r * t, (r + 1) * t), slice(c * t, (c + 1) * t))
        n = nrm[sl] * 2 - 1
        # decode like the runtime: RG only, z reconstructed
        z = np.sqrt(np.clip(1 - n[..., 0] ** 2 - n[..., 1] ** 2, 0, 1))
        n = np.stack([n[..., 0], n[..., 1], z], -1)
        flags = meta['flags'].get(name, [])
        L = Layer(alb[sl][..., :3], alb[sl][..., 3], n, msk[sl][..., 0], msk[sl][..., 1], msk[sl][..., 2],
                  msk[sl][..., 3], cube='cross' not in flags)
        layers.append((name, flags, L))
    return meta, layers


def checker(h, w, s=8):
    Y, X = np.mgrid[0:h, 0:w]
    c = ((X // s + Y // s) % 2) * 0.10 + 0.42
    return np.stack([c] * 3, -1)


def over_checker(rgb, a):
    return rgb * a[..., None] + checker(*a.shape) * (1 - a[..., None])


def tint_for(name, flags):
    return LAYER_TINT.get(name, TINTS['temperate']) if 'tint' in flags else None


def lit(name, flags, L, scale_emis=True):
    img = shade(L, tint_for(name, flags), emis_gain=2.5 if ('emissive' in flags and scale_emis) else 0.0)
    return img


def to_img(a):
    return Image.fromarray(np.round(np.clip(a, 0, 1) * 255).astype(np.uint8))


def grid_sheet(cells, cols, cell, label_h=16, pad=6, title=None):
    rows = (len(cells) + cols - 1) // cols
    top = 24 if title else 0
    W = cols * (cell + pad) + pad
    H = top + rows * (cell + label_h + pad) + pad
    img = Image.new('RGB', (W, H), BG)
    d = ImageDraw.Draw(img)
    if title:
        d.text((pad, 6), title, fill=(235, 235, 235))
    for i, (label, arr) in enumerate(cells):
        r, c = divmod(i, cols)
        x = pad + c * (cell + pad)
        y = top + pad + r * (cell + label_h + pad)
        im = to_img(arr)
        if im.size[0] != cell:
            im = im.resize((cell, int(cell * im.size[1] / im.size[0])), Image.NEAREST)
        img.paste(im, (x, y))
        d.text((x + 2, y + im.size[1] + 2), label, fill=(220, 220, 220))
    return img


def seam_report(layers):
    print('seam report (edge diff / interior diff; ~1.0 = seamless):')
    worst = []
    for name, flags, L in layers:
        if not L.cube:
            continue
        a = L.albedo * L.alpha[..., None]
        ex = np.abs(a[:, 0] - a[:, -1]).mean()
        ix = np.abs(a[:, 1:-1] - a[:, :-2]).mean()
        ey = np.abs(a[0] - a[-1]).mean()
        iy = np.abs(a[1:-1] - a[:-2]).mean()
        rx, ry = ex / max(ix, 1e-6), ey / max(iy, 1e-6)
        note = ' (vertical seam by design: side fringe)' if name.endswith('_side') and name.split('_')[0] in ('grass', 'snowy', 'podzol') else ''
        note = ' (log top: bark rim by design)' if name.startswith('log_') and name.endswith('_top') else note
        worst.append((max(rx, ry), name))
        print(f'  {name:20s} x {rx:4.2f}  y {ry:4.2f}{note}')
    return worst


def iso_cube(top, left, right, s=88):
    """Affine-map three lit RGBA faces into an isometric cube image."""
    c30 = np.cos(np.pi / 6)
    W = int(2 * s * c30) + 4
    H = int(2 * s) + 4
    canvas = Image.new('RGBA', (W, H), (0, 0, 0, 0))
    cx, y0 = W / 2, 2.0
    t = 128.0

    def put(face, O, a, b):
        M = np.array([[a[0], b[0]], [a[1], b[1]]]) / t
        Mi = np.linalg.inv(M)
        off = -Mi @ np.array(O)
        coeffs = (Mi[0, 0], Mi[0, 1], off[0], Mi[1, 0], Mi[1, 1], off[1])
        im = face.transform((W, H), Image.AFFINE, coeffs, resample=Image.BICUBIC, fillcolor=(0, 0, 0, 0))
        canvas.alpha_composite(im)
    put(left, (cx - s * c30, y0 + s / 2), (s * c30, s / 2), (0, s))
    put(right, (cx, y0 + s), (s * c30, -s / 2), (0, s))
    put(top, (cx, y0), (s * c30, s / 2), (-s * c30, s / 2))
    return canvas


def face_rgba(name, flags, L, light, sun):
    rgb = shade(L, tint_for(name, flags), light=light, sun=sun, emis_gain=2.5 if 'emissive' in flags else 0)
    a = L.alpha
    return Image.fromarray(np.round(np.concatenate([np.clip(rgb, 0, 1), a[..., None]], -1) * 255).astype(np.uint8))


BLOCK_FACES = [
    ('grass', 'grass_top', 'grass_side', 'grass_side'), ('snowy grass', 'snow', 'snowy_grass_side', 'snowy_grass_side'),
    ('dirt', 'dirt', 'dirt', 'dirt'), ('podzol', 'podzol_top', 'podzol_side', 'podzol_side'),
    ('stone', 'stone', 'stone', 'stone'), ('cobblestone', 'cobblestone', 'cobblestone', 'cobblestone'),
    ('mossy cobble', 'mossy_cobblestone', 'mossy_cobblestone', 'mossy_cobblestone'), ('bedrock', 'bedrock', 'bedrock', 'bedrock'),
    ('sand', 'sand', 'sand', 'sand'), ('sandstone', 'sandstone_top', 'sandstone_side', 'sandstone_side'),
    ('gravel', 'gravel', 'gravel', 'gravel'), ('clay', 'clay', 'clay', 'clay'), ('snow', 'snow', 'snow', 'snow'),
    ('ice', 'ice', 'ice', 'ice'), ('water (icon)', 'water_still', 'water_still', 'water_still'),
    ('oak log', 'log_oak_top', 'log_oak_side', 'log_oak_side'), ('birch log', 'log_birch_top', 'log_birch_side', 'log_birch_side'),
    ('spruce log', 'log_spruce_top', 'log_spruce_side', 'log_spruce_side'), ('dark log', 'log_dark_top', 'log_dark_side', 'log_dark_side'),
    ('planks', 'planks', 'planks', 'planks'), ('glass', 'glass', 'glass', 'glass'),
    ('oak leaves', 'leaves_oak', 'leaves_oak', 'leaves_oak'), ('birch leaves', 'leaves_birch', 'leaves_birch', 'leaves_birch'),
    ('spruce leaves', 'leaves_spruce', 'leaves_spruce', 'leaves_spruce'), ('dark leaves', 'leaves_dark', 'leaves_dark', 'leaves_dark'),
    ('coal ore', 'coal_ore', 'coal_ore', 'coal_ore'), ('iron ore', 'iron_ore', 'iron_ore', 'iron_ore'),
    ('gold ore', 'gold_ore', 'gold_ore', 'gold_ore'), ('cactus', 'cactus_top', 'cactus_side', 'cactus_side'),
    ('workbench', 'workbench_top', 'workbench_side', 'workbench_side'), ('furnace', 'furnace_top', 'furnace_front', 'furnace_side'),
    ('furnace (lit)', 'furnace_top', 'furnace_front_lit', 'furnace_side'),
]


def main():
    os.makedirs(OUT, exist_ok=True)
    meta, layers = load()
    by = {n: (n, f, L) for n, f, L in layers}

    cells = [(n, over_checker(apply_tint(L.albedo, L.maska, tint_for(n, f)), L.alpha)) for n, f, L in layers]
    grid_sheet(cells, 8, 128, title='Wildmere block albedo (tint layers shown with sample biome tint)').save(os.path.join(OUT, 'contact_albedo.png'))
    cells = [(n, over_checker(lit(n, f, L), L.alpha)) for n, f, L in layers]
    grid_sheet(cells, 8, 128, title='Lit preview: normal + AO + smoothness, light from top-left').save(os.path.join(OUT, 'contact_lit.png'))
    cells = []
    for n, f, L in layers:
        strip = np.concatenate([L.normal * 0.5 + 0.5, np.stack([L.ao] * 3, -1), np.stack([L.smooth] * 3, -1),
                                np.stack([L.height] * 3, -1), np.stack([L.maska] * 3, -1)], 1)
        cells.append((n + '  (N / AO / smooth / height / maskA)', strip))
    grid_sheet(cells, 2, 640, title='Maps').save(os.path.join(OUT, 'contact_maps.png'))

    cube = [(n, f, L) for n, f, L in layers if L.cube]
    for k in range(0, len(cube), 12):
        cells = [(n, np.tile(over_checker(lit(n, f, L), L.alpha), (3, 3, 1))) for n, f, L in cube[k:k + 12]]
        grid_sheet(cells, 4, 384, title='3x3 tiling (lit)').save(os.path.join(OUT, f'tiling_{k // 12 + 1}.png'))

    tinted = [(n, f, L) for n, f, L in layers if 'tint' in f]
    cells = []
    for n, f, L in tinted:
        for tn, tc in TINTS.items():
            rgb = shade(L, tc)
            cells.append((f'{n} / {tn}', over_checker(rgb, L.alpha)))
    grid_sheet(cells, 8, 128, title='Biome tints (runtime formula: albedo * lerp(1, tint*1.7, maskA))').save(os.path.join(OUT, 'tints.png'))

    # isometric blocks + plants
    tiles = []
    lt_top, lt_left, lt_right = (-0.25, 0.35, 0.90), (0.45, 0.55, 0.70), (-0.55, 0.50, 0.45)
    for label, t, l, r in BLOCK_FACES:
        im = iso_cube(face_rgba(*by[t], lt_top, 1.05), face_rgba(*by[l], lt_left, 0.85), face_rgba(*by[r], lt_right, 0.55))
        tiles.append((label, im))
    for n, f, L in layers:
        if not L.cube:
            tiles.append((n, face_rgba(n, f, L, (-0.3, 0.4, 0.85), 1.0).resize((152, 152), Image.LANCZOS)))
    cols = 8
    cw, chh = 160, 196
    sheet = Image.new('RGB', (cols * cw + 8, ((len(tiles) + cols - 1) // cols) * chh + 30), (96, 112, 128))
    d = ImageDraw.Draw(sheet)
    d.text((8, 8), 'Isometric lit blocks (top/left/right faces lit differently) and cross plants', fill=(255, 255, 255))
    for i, (label, im) in enumerate(tiles):
        r, c = divmod(i, cols)
        x = 8 + c * cw + (cw - im.size[0]) // 2
        y = 30 + r * chh + (176 - im.size[1]) // 2
        sheet.paste(im, (x, y), im)
        d.text((8 + c * cw + 4, 30 + r * chh + 178), label, fill=(255, 255, 255))
    sheet.save(os.path.join(OUT, 'blocks_iso.png'))

    # extras
    crack = np.asarray(Image.open(os.path.join(BLOCKS, 'crack.png')).convert('RGBA'), np.float64) / 255
    stone = by['stone'][2]
    base = np.tile(shade(stone), (1, 8, 1))
    crack_on = base * (1 - 0.75 * crack[..., 3:4])
    wa = np.asarray(Image.open(os.path.join(TEX, 'water_normal_a.png')).convert('RGB'), np.float64) / 255
    wb = np.asarray(Image.open(os.path.join(TEX, 'water_normal_b.png')).convert('RGB'), np.float64) / 255
    foam = np.asarray(Image.open(os.path.join(TEX, 'water_foam.png')).convert('L'), np.float64) / 255
    cloud = np.asarray(Image.open(os.path.join(TEX, 'cloud_noise.png')).convert('RGBA'), np.float64) / 255
    ex = Image.new('RGB', (1040, 1010), BG)
    d = ImageDraw.Draw(ex)
    d.text((8, 4), 'crack stages over lit stone', fill=(230, 230, 230))
    ex.paste(to_img(crack_on), (8, 18))
    y = 160
    for i, (lab, arr) in enumerate([('water_normal_a', wa), ('water_normal_b', wb), ('water_foam', np.stack([foam] * 3, -1)),
                                    ('cloud R', np.stack([cloud[..., 0]] * 3, -1))]):
        d.text((8 + i * 258, y - 14), lab, fill=(230, 230, 230))
        ex.paste(to_img(arr), (8 + i * 258, y))
    y = 440
    for i, ch in enumerate(['G', 'B', 'A']):
        d.text((8 + i * 258, y - 14), 'cloud ' + ch, fill=(230, 230, 230))
        ex.paste(to_img(np.stack([cloud[..., i + 1]] * 3, -1)), (8 + i * 258, y))
    d.text((8 + 3 * 258, y - 14), 'water_normal_a 2x2', fill=(230, 230, 230))
    ex.paste(to_img(np.tile(wa, (2, 2, 1))[::2, ::2]), (8 + 3 * 258, y))
    y = 720
    names = ['stick', 'coal', 'raw_iron', 'iron_ingot', 'raw_gold', 'gold_ingot', 'flint']
    d.text((8, y - 14), 'item icons at 128 px (top) and 48 px (bottom), on light and dark', fill=(230, 230, 230))
    for i, nm in enumerate(names):
        ic = Image.open(os.path.join(TEX, 'Items', nm + '.png')).convert('RGBA')
        bg = Image.new('RGBA', (132, 132), (190, 186, 178, 255) if i % 2 else (58, 60, 66, 255))
        bg.alpha_composite(ic, (2, 2))
        ex.paste(bg.convert('RGB'), (8 + i * 146, y))
        sm = ic.resize((48, 48), Image.LANCZOS)
        for j, col_ in enumerate([(190, 186, 178, 255), (58, 60, 66, 255)]):
            b2 = Image.new('RGBA', (52, 52), col_)
            b2.alpha_composite(sm, (2, 2))
            ex.paste(b2.convert('RGB'), (8 + i * 146 + j * 58, y + 140))
        d.text((8 + i * 146, y + 196), nm, fill=(230, 230, 230))
    ex.save(os.path.join(OUT, 'extras.png'))

    # albedo statistics (opaque texels, non-emissive): fraction outside sRGB 30..230
    print('albedo stats (sRGB 0-255 over covered texels): mean, p2, p98, % outside 30..230')
    for n, f, L in layers:
        m = L.alpha > 0.5
        v = L.albedo[m].mean(axis=-1) * 255
        out = ((v < 30) | (v > 230)).mean() * 100
        print(f'  {n:20s} {v.mean():6.1f} {np.percentile(v, 2):6.1f} {np.percentile(v, 98):6.1f}  {out:5.1f}%')
    w = seam_report(layers)
    # by design: side fringes / workbench side do not repeat vertically, the cactus top is a centred
    # radial pattern; log_birch_side was checked by hand (a ragged bark patch edge sits on the wrap
    # row and continues across it, which inflates the ratio)
    known = ('grass_side', 'snowy_grass_side', 'podzol_side', 'workbench_side', 'cactus_top', 'log_birch_side')
    bad = [nm for r, nm in w if r > 1.6 and not (nm.endswith('_top') and nm.startswith('log_')) and nm not in known]
    print('seam outliers:', bad if bad else 'none')
    print('previews written to', os.path.relpath(OUT, ROOT))


if __name__ == '__main__':
    main()
