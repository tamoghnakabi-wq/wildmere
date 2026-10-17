"""Wildmere held tools: Pickaxe/Axe/Shovel x Wood/Stone/Iron + Torch.

Run headless from the repo root:
  /Applications/Blender.app/Contents/MacOS/Blender -b --factory-startup \
      --python art-source/blender/build_tools.py -- [--only Axe_Iron,Torch] [--no-icons] [--no-preview]

Authoring frame (Blender): grip (hand centre) at the origin, long axis +Z, working edge / pick
point / blade face toward -Y.  After export that is Unity: long axis +Y, working edge +Z.
Hierarchy: root empty "<Tool>_<Tier>" -> "Handle" (mesh) + "Head" (mesh); Torch -> "Handle" +
"Flame" (pivot at the flame base).  Tools have no LOD1 (always close to the camera).
"""
import os
import sys
import math
import numpy as np
from mathutils import Vector, Matrix

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import wm_lib as W
from wm_lib import MB, Part, ring, loft, lerp_path, interp, hexc, mix, sstep

ICON_VIEW = [(1.0, -0.35, 0.28)]
ICON_ROT = (math.radians(-45), 0, 0)
PREVIEW_VIEWS = [(1.0, -0.35, 0.28), (0.0, -1.0, 0.15), (1.0, 0.0, 0.0), (0.4, -0.5, 1.0)]

PAL = {
    "handle": hexc("6a4a2f"), "handle_dk": hexc("3f2a1a"), "wood": hexc("94714a"),
    "wood_dk": hexc("5e4429"), "stone": hexc("8a8780"), "stone_dk": hexc("4e4b47"),
    "stone_lt": hexc("b8b4aa"), "iron": hexc("8f979f"), "iron_dk": hexc("4f555c"),
    "iron_lt": hexc("e2e7ec"), "cord": hexc("a3864f"), "cord_dk": hexc("6b5530"),
    "leather": hexc("553422"), "leather_dk": hexc("2e1c12"), "cloth": hexc("6a5a44"),
    "char": hexc("1e1915"), "ember": hexc("ff7a1a"),
}


# --------------------------------------------------------------------------- helpers
def handle(mb, z0, z1, r0, r1, mat="M_Wood", sides=8, rough=0.0, seed=0, knob=1.25):
    n = 7
    pts, profs = [], []
    for i in range(n):
        t = i / (n - 1)
        z = z0 + (z1 - z0) * t
        r = r0 + (r1 - r0) * t
        if i == 0:
            r *= knob
        wob = 1 + rough * W.noise1((0, 0, z), 9.0, seed)
        pts.append(Vector((rough * 0.004 * W.noise1((z, 0, 0), 5, seed + 3), 0, z)))
        profs.append(ring(sides, r * wob, r * wob, phase=math.pi / sides))
    pts.insert(1, Vector((0, 0, z0 + 0.018)))
    profs.insert(1, ring(sides, r0 * knob * 0.92, r0 * knob * 0.92, phase=math.pi / sides))
    pts.insert(2, Vector((0, 0, z0 + 0.03)))
    profs.insert(2, ring(sides, r0 * 0.95, r0 * 0.95, phase=math.pi / sides))
    loft(mb, pts, profs, mat, up_hint=(0, -1, 0), cap0="flat", cap1="flat")


def torus(mb, center, normal, R, r, mat, seg=10, sides=4, phase=0.0):
    nrm = Vector(normal).normalized()
    a = Vector((1, 0, 0)) if abs(nrm.x) < 0.9 else Vector((0, 1, 0))
    u = nrm.cross(a).normalized()
    v = nrm.cross(u)
    path = [Vector(center) + (u * math.cos(phase + 2 * math.pi * i / seg) +
                              v * math.sin(phase + 2 * math.pi * i / seg)) * R for i in range(seg)]
    rings = []
    for i in range(seg):
        p = path[i]
        t = (path[(i + 1) % seg] - path[i - 1]).normalized()
        radial = (p - Vector(center)).normalized()
        rings.append([mb.add(p + (radial * math.cos(2 * math.pi * k / sides) +
                                  nrm * math.sin(2 * math.pi * k / sides)) * r)
                      for k in range(sides)])
    for i in range(seg):
        a_, b_ = rings[i], rings[(i + 1) % seg]
        for k in range(sides):
            k1 = (k + 1) % sides
            mb.face([a_[k], a_[k1], b_[k1], b_[k]], mat)


def knapped(mb, center, size, seed, mat="M_Stone", cuts=7, taper_y=0.0, subdiv=2, edge=0.0):
    """Faceted chipped stone: icosphere -> random planar cuts -> optional pointy taper on Y."""
    rng = np.random.default_rng(seed)
    tmp = MB()
    W.ico(tmp, (0, 0, 0), 1.0, mat, subdiv=subdiv)
    planes = []
    for _ in range(cuts):
        d = rng.normal(size=3)
        d /= np.linalg.norm(d)
        planes.append((Vector(d), rng.uniform(0.62, 0.86)))
    for i, v in enumerate(tmp.v):
        p = v.copy()
        for d, h in planes:
            k = p.dot(d)
            if k > h:
                p -= d * (k - h)
        if taper_y:
            f = max(0.0, 1.0 - abs(p.y)) ** taper_y
            p.x *= 0.25 + 0.75 * f
            p.z *= 0.25 + 0.75 * f
        if edge:  # thin cutting edge toward -Y, thick poll toward +Y
            p.x *= 1.0 - edge * (1.0 - (p.y + 1.0) / 2.0)
        tmp.v[i] = Vector(center) + Vector((p.x * size[0], p.y * size[1], p.z * size[2]))
    mb.extend(tmp)


# --------------------------------------------------------------------------- paint
def make_paint(tier):
    def paint(ctx):
        pn = ctx.obj.name.replace("_LOD1", "")
        P, N = ctx.P, ctx.N
        x, y, z = P[:, 0], P[:, 1], P[:, 2]
        col = np.zeros((ctx.n, 3))
        m = ctx.mat
        # handle wood: grain streaks along Z
        g = ctx.vnoise((70, 70, 3.5), 3, 2)
        hw = mix(PAL["handle"], PAL["handle_dk"], np.clip(0.5 + 0.9 * g, 0, 1) * 0.55)
        hw = mix(hw, PAL["handle_dk"], sstep(-0.06, -0.11, z) * 0.5)
        sel = (m == "M_Wood") & (pn in ("Handle",))
        col[sel] = hw[sel]
        # head wood (lighter, grain along Y)
        g2 = ctx.vnoise((90, 5, 90), 3, 4)
        g3 = np.sin(ctx.P[:, 0] * 260 + ctx.P[:, 2] * 260 + 6 * g2)
        ww = mix(PAL["wood"], PAL["wood_dk"], np.clip(0.5 + 1.4 * g2, 0, 1) * 0.75)
        ww = mix(ww, PAL["wood_dk"], sstep(0.55, 0.95, g3) * 0.45)
        sel = (m == "M_Wood") & (pn != "Handle")
        col[sel] = ww[sel]
        # stone: speckled granite-ish
        sp = ctx.vnoise((90, 90, 90), 2, 7)
        bl = ctx.vnoise((14, 14, 14), 3, 8)
        st = mix(PAL["stone"], PAL["stone_dk"], np.clip(0.5 + bl, 0, 1) * 0.5)
        st = mix(st, PAL["stone_lt"], sstep(0.25, 0.45, sp) * 0.6)
        st = mix(st, PAL["stone_dk"] * 0.7, sstep(-0.25, -0.45, sp) * 0.6)
        sel = m == "M_Stone"
        col[sel] = st[sel]
        # metal: dark forged near the eye, bright worked edges toward the tips
        d = np.abs(y) + np.maximum(z - 0.62, 0) * 0.5
        me = mix(PAL["iron_dk"], PAL["iron"], sstep(0.03, 0.10, d))
        me = mix(me, PAL["iron_lt"], sstep(0.13, 0.25, d) * 0.7)
        me = me * (1 + 0.10 * bl[:, None])
        sel = m == "M_Metal"
        col[sel] = me[sel]
        # cord / leather / cloth
        tw = np.sin((z * 260 + x * 140 + y * 140))
        cd = mix(PAL["cord"], PAL["cord_dk"], sstep(0.2, 0.9, tw) * 0.6)
        col[ctx.tag == "cord"] = cd[ctx.tag == "cord"]
        wr = np.sin(z * 150 + np.arctan2(y, x) * 1.0)
        le = mix(PAL["leather"], PAL["leather_dk"], sstep(0.6, 0.95, wr) * 0.8)
        col[ctx.tag == "leather"] = le[ctx.tag == "leather"]
        cl = mix(PAL["cloth"], PAL["char"], sstep(0.25, 0.34, z))
        cl = cl * (1 + 0.15 * sp[:, None])
        col[ctx.tag == "cloth"] = cl[ctx.tag == "cloth"]
        em = mix(PAL["ember"], PAL["char"], np.clip(0.5 + bl, 0, 1) * 0.6)
        col[ctx.tag == "ember"] = em[ctx.tag == "ember"]
        # flame: white-yellow core at the base -> orange -> red tips
        if (m == "M_Flame").any():
            h = np.clip((z - 0.30) / 0.24, 0, 1)
            r = np.sqrt(x * x + y * y)
            fl = mix(hexc("fff4c2"), hexc("ffb12e"), sstep(0.05, 0.45, h))
            fl = mix(fl, hexc("e8481c"), sstep(0.5, 0.95, h))
            fl = mix(fl, hexc("ff8a20"), sstep(0.02, 0.05, r) * 0.4)
            col[m == "M_Flame"] = fl[m == "M_Flame"]
        return col
    paint.no_ao = ("M_Flame", "M_Glow")
    return paint


# --------------------------------------------------------------------------- tools
def pickaxe(tier):
    hd, hl = MB(), MB()
    handle(hl, -0.10, 0.62, 0.025, 0.023, rough=0.15 if tier == "Wood" else 0.05, seed=1)
    if tier == "Iron":
        ctrl = [(0.235, 0.505), (0.13, 0.575), (0.0, 0.600), (-0.14, 0.575), (-0.275, 0.485)]
        path = lerp_path([Vector((0, y, z)) for y, z in ctrl], 11)
        profs = []
        for i, p in enumerate(path):
            t = i / 10                        # 0 back tip -> 1 front point
            c = abs(t - 0.47) / 0.53
            hw = interp([(0, 0.014), (0.25, 0.030), (0.47, 0.040), (0.7, 0.027), (1, 0.005)], t)
            hv = interp([(0, 0.008), (0.25, 0.033), (0.47, 0.045), (0.7, 0.030), (1, 0.006)], t)
            if t < 0.15:
                hw = 0.030 * (0.6 + t * 2.5)  # flat chisel back end
            profs.append(ring(6, hw, hv, power=1.7, phase=0))
        loft(hd, path, profs, "M_Metal", cap0="flat", cap1="point",
             tip1=Vector((0, -0.292, 0.468)))
        # socket collar
        loft(hd, [Vector((0, 0, 0.545)), Vector((0, 0, 0.565)), Vector((0, 0, 0.635)),
                  Vector((0, 0, 0.652))],
             [ring(8, r, r, phase=math.pi / 8) for r in (0.028, 0.037, 0.037, 0.029)], "M_Metal",
             cap0="flat", cap1="flat", up_hint=(0, -1, 0))
        # leather grip wrap
        loft(hl, [Vector((0, 0, z)) for z in (-0.075, -0.07, 0.10, 0.105)],
             [ring(8, r, r, phase=math.pi / 8) for r in (0.0255, 0.0278, 0.0278, 0.0255)],
             "M_Cloth#leather", cap0="flat", cap1="flat", up_hint=(0, -1, 0))
    elif tier == "Stone":
        knapped(hd, (0, -0.025, 0.59), (0.050, 0.26, 0.062), seed=11, taper_y=0.7, subdiv=2,
                cuts=9)
        for zc, tilt in ((0.59, (0.0, 0.55, 1)), (0.59, (0.0, -0.55, 1))):
            torus(hd, (0, 0, zc), tilt, 0.046, 0.0065, "M_Cloth#cord", seg=10, sides=4)
        torus(hd, (0, 0, 0.53), (0, 0, 1), 0.029, 0.006, "M_Cloth#cord", seg=9, sides=4)
        torus(hd, (0, 0, 0.645), (0, 0, 1), 0.028, 0.006, "M_Cloth#cord", seg=9, sides=4)
    else:  # Wood: chunky carved head, blunt
        ctrl = [(0.18, 0.535), (0.09, 0.588), (0.0, 0.600), (-0.12, 0.584), (-0.245, 0.52)]
        path = lerp_path([Vector((0, y, z)) for y, z in ctrl], 9)
        profs = []
        for i in range(9):
            t = i / 8
            hw = interp([(0, 0.024), (0.5, 0.044), (1, 0.013)], t)
            hv = interp([(0, 0.026), (0.5, 0.050), (1, 0.013)], t)
            profs.append(ring(8, hw, hv, power=3.0, phase=math.pi / 8))
        loft(hd, path, profs, "M_Wood", cap0="flat", cap1="point", tip1=Vector((0, -0.262, 0.505)))
        # wooden wedge peg on top
        loft(hd, [Vector((0, 0, 0.62)), Vector((0, 0, 0.655))],
             [ring(4, 0.015, 0.025, phase=math.pi / 4), ring(4, 0.013, 0.021, phase=math.pi / 4)],
             "M_Wood", up_hint=(0, -1, 0))
        torus(hd, (0, 0, 0.535), (0, 0, 1), 0.028, 0.006, "M_Cloth#cord", seg=9, sides=4)
    return hl, hd


def axe(tier):
    hd, hl = MB(), MB()
    handle(hl, -0.10, 0.56, 0.024, 0.022, rough=0.15 if tier == "Wood" else 0.05, seed=2)
    zc = 0.49
    if tier in ("Iron", "Wood"):
        thick = 1.0 if tier == "Iron" else 1.45
        st = [  # y, half-thickness(x), half-height(z), z-offset (beard)
            (0.060, 0.026, 0.030, 0.0), (0.040, 0.031, 0.036, 0.0), (0.0, 0.032, 0.040, 0.0),
            (-0.035, 0.024, 0.038, -0.004), (-0.075, 0.014, 0.050, -0.010),
            (-0.115, 0.008, 0.070, -0.016), (-0.150, 0.004, 0.088, -0.020),
            (-0.168, 0.0015, 0.094, -0.020)]
        mat = "M_Metal" if tier == "Iron" else "M_Wood"
        path = [Vector((0, y, zc + dz)) for y, a, b, dz in st]
        profs = [ring(8, min(a * thick * 1.25, 0.042), b * 1.25, power=3.0, phase=math.pi / 8)
                 for y, a, b, dz in st]
        path = [Vector((0, p.y * 1.15, zc + (p.z - zc) * 1.25)) for p in path]
        loft(hd, path, profs, mat, cap0="flat", cap1="flat")
        if tier == "Iron":
            loft(hl, [Vector((0, 0, z)) for z in (-0.075, -0.07, 0.10, 0.105)],
                 [ring(8, r, r, phase=math.pi / 8) for r in (0.0245, 0.0268, 0.0268, 0.0245)],
                 "M_Cloth#leather", cap0="flat", cap1="flat", up_hint=(0, -1, 0))
        else:
            torus(hd, (0, 0, zc - 0.06), (0, 0, 1), 0.027, 0.006, "M_Cloth#cord", seg=9, sides=4)
            torus(hd, (0, 0, zc + 0.06), (0, 0, 1), 0.027, 0.006, "M_Cloth#cord", seg=9, sides=4)
    else:  # Stone: knapped wedge lashed into a split haft
        knapped(hd, (0, -0.085, zc), (0.036, 0.13, 0.10), seed=21, cuts=8, subdiv=2, edge=0.85)
        for tilt in ((0.0, 0.6, 1), (0.0, -0.6, 1)):
            torus(hd, (0, -0.008, zc), tilt, 0.044, 0.0065, "M_Cloth#cord", seg=10, sides=4)
        torus(hd, (0, 0, zc - 0.075), (0, 0, 1), 0.027, 0.006, "M_Cloth#cord", seg=9, sides=4)
    return hl, hd


def shovel(tier):
    hd, hl = MB(), MB()
    handle(hl, -0.10, 0.60, 0.023, 0.022, rough=0.15 if tier == "Wood" else 0.05, seed=3)
    thick = {"Iron": 0.008, "Stone": 0.020, "Wood": 0.018}[tier]
    mat = {"Iron": "M_Metal", "Stone": "M_Stone", "Wood": "M_Wood"}[tier]
    z0, z1 = 0.58, 0.88
    rows, cols = 7, 7
    front, back = [], []
    for r in range(rows):
        t = r / (rows - 1)
        z = z0 + (z1 - z0) * t
        w = interp([(0, 0.085), (0.3, 0.112), (0.65, 0.106), (0.9, 0.058), (1, 0.014)], t)
        if tier == "Stone":
            w *= 1 + 0.08 * W.noise1((0, 0, z), 30, 5)
        fr, bk = [], []
        for c in range(cols):
            u = (c / (cols - 1)) * 2 - 1
            x = u * w
            yoff = -0.022 - 0.022 * (u * u) - 0.01 * t   # concave front, lifted forward
            fr.append(hd.add((x, yoff, z)))
            bk.append(hd.add((x, yoff + thick, z)))
        front.append(fr)
        back.append(bk)
    for r in range(rows - 1):
        for c in range(cols - 1):
            hd.face([front[r][c], front[r + 1][c], front[r + 1][c + 1], front[r][c + 1]], mat)
            hd.face([back[r][c], back[r][c + 1], back[r + 1][c + 1], back[r + 1][c]], mat)
    for r in range(rows - 1):  # side rims
        hd.face([front[r][0], back[r][0], back[r + 1][0], front[r + 1][0]], mat)
        hd.face([front[r][-1], front[r + 1][-1], back[r + 1][-1], back[r][-1]], mat)
    for c in range(cols - 1):  # bottom and top rims
        hd.face([front[0][c], front[0][c + 1], back[0][c + 1], back[0][c]], mat)
        hd.face([front[-1][c], back[-1][c], back[-1][c + 1], front[-1][c + 1]], mat)
    if tier == "Iron":
        # socket (tapered tube from handle into the blade) + rolled step edge
        loft(hd, [Vector((0, -0.004, z)) for z in (0.50, 0.53, 0.60, 0.66)],
             [ring(8, a, b, phase=math.pi / 8) for a, b in
              ((0.025, 0.025), (0.029, 0.030), (0.034, 0.028), (0.018, 0.014))],
             "M_Metal", cap0="flat", cap1="point", up_hint=(0, -1, 0),
             tip1=Vector((0, -0.01, 0.70)))
        loft(hl, [Vector((0, 0, z)) for z in (-0.075, -0.07, 0.10, 0.105)],
             [ring(8, r, r, phase=math.pi / 8) for r in (0.0235, 0.0258, 0.0258, 0.0235)],
             "M_Cloth#leather", cap0="flat", cap1="flat", up_hint=(0, -1, 0))
    else:
        for zc in (0.555, 0.60):
            torus(hd, (0, -0.006, zc), (0, 0, 1), 0.029, 0.006, "M_Cloth#cord", seg=9, sides=4)
    return hl, hd


def torch():
    hl, fl = MB(), MB()
    handle(hl, -0.12, 0.27, 0.019, 0.024, rough=0.2, seed=4, knob=1.0)
    # wrapped, charred head
    hp = [Vector((0, 0, z)) for z in (0.215, 0.23, 0.26, 0.30, 0.325, 0.335)]
    hr = [0.025, 0.036, 0.039, 0.038, 0.032, 0.021]
    profs = [ring(8, r * (1 + 0.08 * W.noise1((0, 0, z.z), 40, 2)), r, phase=math.pi / 8)
             for r, z in zip(hr, hp)]
    loft(hl, hp, profs, ["M_Cloth#cloth"] * 4 + ["M_Glow#ember"], up_hint=(0, -1, 0), cap0="flat",
         cap1="flat")
    torus(hl, (0, 0, 0.245), (0, 0, 1), 0.037, 0.005, "M_Cloth#cord", seg=9, sides=4)
    torus(hl, (0, 0, 0.29), (0, 0, 1), 0.039, 0.005, "M_Cloth#cord", seg=9, sides=4)
    # flame: two twisted, pinched tongues
    for k, (off, h, rs, ph) in enumerate(((0.0, 0.25, 1.0, 0.0), (0.012, 0.17, 0.62, 1.7))):
        pts, prs = [], []
        for i in range(7):
            t = i / 6
            z = 0.30 + h * t
            sway = 0.010 * math.sin(t * 4.0 + ph) + off * (1 - t)
            pts.append(Vector((sway, -0.6 * sway, z)))
            r = interp([(0, 0.022), (0.25, 0.040), (0.55, 0.030), (0.85, 0.012), (1, 0.003)], t) * rs
            tw = t * 2.2 + ph
            prs.append(ring(6, r, r * 0.85, phase=tw,
                            mod=lambda th, t=t: 1 + 0.18 * math.sin(3 * th) * (0.3 + t)))
        loft(fl, pts, prs, "M_Flame", up_hint=(0, -1, 0), cap0="flat", cap1="point",
             tip1=pts[-1] + Vector((0, 0, 0.03 * rs)))
    return hl, fl


def build_tool(name):
    kind, _, tier = name.partition("_")
    paint = make_paint(tier)
    if kind == "Torch":
        hl, fl = torch()
        parts = [Part("Handle", hl, (0, 0, 0), paint=paint, smooth=50, lod=False),
                 Part("Flame", fl, (0, 0, 0.30), paint=paint, smooth=70, lod=False)]
    else:
        hl, hd = {"Pickaxe": pickaxe, "Axe": axe, "Shovel": shovel}[kind](tier)
        sm = 32 if tier == "Stone" else 45
        parts = [Part("Handle", hl, (0, 0, 0), paint=paint, smooth=50, lod=False),
                 Part("Head", hd, (0, 0, 0), paint=paint, smooth=sm, lod=False)]
    return W.assemble(name, parts, ground=False, ao={"dist": 0.06, "rays": 32, "ground": False},
                      cav=(0.9, 0.45), face_jitter=0.04, seed=len(name), lod=False)


BIOME_ALL = ["Any"]
ASSETS = {}
for k in ("Pickaxe", "Axe", "Shovel"):
    for t in ("Wood", "Stone", "Iron"):
        ASSETS["%s_%s" % (k, t)] = dict(
            biomes=BIOME_ALL, tier=t.lower(), tool=k.lower(),
            pivot="Origin = grip (hand centre), 0.10 m above the butt. Long axis +Y; %s toward +Z. "
                  "Children Handle + Head share the grip pivot." % (
                      {"Pickaxe": "main pick point", "Axe": "blade edge",
                       "Shovel": "blade face (concave side)"}[k]))
ASSETS["Torch"] = dict(biomes=BIOME_ALL, tool="torch",
                       pivot="Origin = grip, 0.12 m above the stick end. Long axis +Y. Child Flame "
                             "pivots at the flame base (y=0.30) for flicker scaling; M_Flame and "
                             "the M_Glow ember top are emissive.")


def main():
    args = W.parse_args()
    entries = {}
    for nm, meta in ASSETS.items():
        if args["only"] and nm not in args["only"]:
            continue
        root = build_tool(nm)
        root.rotation_euler = (0, 0, 0)
        shov = nm.startswith("Shovel")
        e = W.finish_asset(root, "Tools", meta, args,
                           icon_views=[(-0.30, -1.0, 0.25)] if shov else ICON_VIEW,
                           preview_views=PREVIEW_VIEWS,
                           icon_rot=(0, math.radians(45), 0) if shov else ICON_ROT)
        e["budget"] = W.BUDGET["Tools"]
        entries[nm] = e
    if args["export"]:
        W.update_manifest(entries)


if __name__ == "__main__":
    main()
