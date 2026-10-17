#!/usr/bin/env python3
"""Wildmere block-texture generator (entry point).

    python3 art-source/textures/generate.py            # everything
    python3 art-source/textures/generate.py --only stone,dirt   # quick partial check (no files written)

Writes (relative to the repo root):
  Assets/Resources/Blocks/blocks_albedo.png   sRGB RGBA, A = cutout coverage
  Assets/Resources/Blocks/blocks_normal.png   tangent space, OpenGL (+Y up), linear RGB
  Assets/Resources/Blocks/blocks_mask.png     linear RGBA: R=AO G=smoothness B=height A=tint mask
                                              (for layers flagged "emissive" A = emission strength)
  Assets/Resources/Blocks/blocks.json
  Assets/Resources/Blocks/crack.png           8 break stages, 8x1 grid
  Assets/Resources/Textures/water_normal_a.png, water_normal_b.png, water_foam.png, cloud_noise.png
  Assets/Resources/Textures/Items/<item>.png

All art is procedural and deterministic (fixed seeds); no external images are used.
"""
import argparse
import json
import math
import os
import sys
import time

import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import gen_extras as X  # noqa: E402
import mat_crafted as C  # noqa: E402
import mat_plants as P  # noqa: E402
import mat_terrain as G  # noqa: E402
import mat_wood as W  # noqa: E402
from texlib import TILE, finalize_cutout  # noqa: E402

ROOT = os.path.abspath(os.path.join(HERE, '..', '..'))
BLOCKS = os.path.join(ROOT, 'Assets', 'Resources', 'Blocks')
TEXTURES = os.path.join(ROOT, 'Assets', 'Resources', 'Textures')
COLUMNS = 8

# (layer name, generator, flags) -- order defines the Texture2DArray slice index.
LAYERS = [
    ('grass_top', G.grass_top, ['tint']),
    ('grass_side', G.grass_side, ['tint']),
    ('snowy_grass_side', G.snowy_grass_side, []),
    ('dirt', G.dirt, []),
    ('podzol_top', G.podzol_top, []),
    ('podzol_side', G.podzol_side, []),
    ('stone', G.stone, []),
    ('cobblestone', G.cobblestone, []),
    ('mossy_cobblestone', G.mossy_cobblestone, []),
    ('bedrock', G.bedrock, []),
    ('sand', G.sand, []),
    ('sandstone_side', G.sandstone_side, []),
    ('sandstone_top', G.sandstone_top, []),
    ('gravel', G.gravel, []),
    ('clay', G.clay, []),
    ('snow', G.snow, []),
    ('ice', G.ice, []),
    ('water_still', G.water_still, ['translucent']),
    ('log_oak_side', W.log_oak_side, []),
    ('log_oak_top', W.log_oak_top, []),
    ('log_birch_side', W.log_birch_side, []),
    ('log_birch_top', W.log_birch_top, []),
    ('log_spruce_side', W.log_spruce_side, []),
    ('log_spruce_top', W.log_spruce_top, []),
    ('log_dark_side', W.log_dark_side, []),
    ('log_dark_top', W.log_dark_top, []),
    ('planks', W.planks, []),
    ('glass', C.glass, ['cutout']),
    ('leaves_oak', P.leaves_oak, ['cutout', 'tint']),
    ('leaves_birch', P.leaves_birch, ['cutout', 'tint']),
    ('leaves_spruce', P.leaves_spruce, ['cutout', 'tint']),
    ('leaves_dark', P.leaves_dark, ['cutout', 'tint']),
    ('coal_ore', C.coal_ore, []),
    ('iron_ore', C.iron_ore, []),
    ('gold_ore', C.gold_ore, []),
    ('cactus_side', C.cactus_side, []),
    ('cactus_top', C.cactus_top, []),
    ('workbench_top', C.workbench_top, []),
    ('workbench_side', C.workbench_side, []),
    ('furnace_front', C.furnace_front, []),
    ('furnace_side', C.furnace_side, []),
    ('furnace_top', C.furnace_top, []),
    ('torch', P.torch, ['cutout', 'emissive', 'cross']),
    ('tall_grass', P.tall_grass, ['cutout', 'tint', 'cross']),
    ('fern', P.fern, ['cutout', 'tint', 'cross']),
    ('flower_red', P.flower_red, ['cutout', 'cross']),
    ('flower_yellow', P.flower_yellow, ['cutout', 'cross']),
    ('flower_blue', P.flower_blue, ['cutout', 'cross']),
    ('flower_white', P.flower_white, ['cutout', 'cross']),
    ('dead_bush', P.dead_bush, ['cutout', 'cross']),
    ('mushroom_red', P.mushroom_red, ['cutout', 'cross']),
    ('mushroom_brown', P.mushroom_brown, ['cutout', 'cross']),
    ('glowcap', P.glowcap, ['cutout', 'emissive', 'cross']),
    # extra (appended so the requested order is untouched): lit furnace mouth
    ('furnace_front_lit', C.furnace_front_lit, ['emissive']),
]


def u8(x):
    return np.clip(np.round(np.asarray(x) * 255.0), 0, 255).astype(np.uint8)


def build_layer(name, fn, flags):
    L = fn()
    emissive = 'emissive' in flags
    if not emissive:
        # keep albedo physically plausible: no pure white on non-emissive surfaces
        L.albedo = np.minimum(L.albedo, 0.94)
    if 'tint' not in flags and not emissive:
        L.maska = np.zeros_like(L.maska)
    if 'cutout' in flags or L.alpha.min() < 0.999:
        L = finalize_cutout(L)
    else:
        L.alpha = np.ones_like(L.alpha)
    return L


def pack(layers):
    n = TILE
    rows = int(math.ceil(len(layers) / COLUMNS))
    H, Wd = rows * n, COLUMNS * n
    alb = np.zeros((H, Wd, 4), np.uint8)
    nrm = np.zeros((H, Wd, 3), np.uint8)
    nrm[..., 0] = 128
    nrm[..., 1] = 128
    nrm[..., 2] = 255
    msk = np.zeros((H, Wd, 4), np.uint8)
    msk[..., 0] = 255  # padding: AO 1, smooth 0, height 0, mask 0
    for i, L in enumerate(layers):
        r, c = divmod(i, COLUMNS)
        sl = (slice(r * n, (r + 1) * n), slice(c * n, (c + 1) * n))
        alb[sl] = np.concatenate([u8(L.albedo), u8(L.alpha)[..., None]], -1)
        nrm[sl] = u8(L.normal * 0.5 + 0.5)
        msk[sl] = np.stack([u8(L.ao), u8(L.smooth), u8(L.height), u8(L.maska)], -1)
    return alb, nrm, msk, rows


def save(path, arr, mode):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    Image.fromarray(arr).save(path, optimize=True)
    print(f'  wrote {os.path.relpath(path, ROOT)}  ({os.path.getsize(path) / 1024:.0f} KB)')


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--only', help='comma-separated layer names to build (no files written)')
    ap.add_argument('--skip-extras', action='store_true')
    args = ap.parse_args()
    t0 = time.time()
    sel = LAYERS
    if args.only:
        want = set(args.only.split(','))
        sel = [l for l in LAYERS if l[0] in want]
    built = []
    for name, fn, flags in sel:
        t = time.time()
        built.append(build_layer(name, fn, flags))
        print(f'  {name:20s} {time.time() - t:5.2f}s')
    if args.only:
        return
    alb, nrm, msk, rows = pack(built)
    save(os.path.join(BLOCKS, 'blocks_albedo.png'), alb, 'RGBA')
    save(os.path.join(BLOCKS, 'blocks_normal.png'), nrm, 'RGB')
    save(os.path.join(BLOCKS, 'blocks_mask.png'), msk, 'RGBA')
    meta = {
        'tile': TILE,
        'columns': COLUMNS,
        'rows': rows,
        'layers': [l[0] for l in LAYERS],
        'flags': {l[0]: l[2] for l in LAYERS if l[2]},
    }
    with open(os.path.join(BLOCKS, 'blocks.json'), 'w') as f:
        json.dump(meta, f, indent=1)
        f.write('\n')
    print(f'  wrote Assets/Resources/Blocks/blocks.json ({len(LAYERS)} layers, {rows} rows)')
    if not args.skip_extras:
        save(os.path.join(BLOCKS, 'crack.png'), X.crack_sheet(), 'RGBA')
        save(os.path.join(TEXTURES, 'water_normal_a.png'), X.water_normal(256, 1111, math.radians(20), 4.0, 1.6, 6, 0.32), 'RGB')
        save(os.path.join(TEXTURES, 'water_normal_b.png'), X.water_normal(256, 1121, math.radians(115), 11.0, 3.5, 3, 0.28), 'RGB')
        save(os.path.join(TEXTURES, 'water_foam.png'), X.water_foam(), 'L')
        save(os.path.join(TEXTURES, 'cloud_noise.png'), X.cloud_noise(), 'RGBA')
        for name, fn in X.ITEMS.items():
            save(os.path.join(TEXTURES, 'Items', f'{name}.png'), fn(), 'RGBA')
    print(f'done in {time.time() - t0:.1f}s')


if __name__ == '__main__':
    main()
