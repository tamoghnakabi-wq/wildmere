"""Wildmere props: rocks, logs, stump, mushrooms, sandstone pillar, dead tree, ice spike, bushes,
reeds, lily pad.

Run headless from the repo root:
  /Applications/Blender.app/Contents/MacOS/Blender -b --factory-startup \
      --python art-source/blender/build_props.py -- [--only Boulder_A,Reeds] [--no-preview]

Every prop is ONE mesh object named after the asset (the FBX root) with origin at the centre of
its ground contact (lowest point on y=0 in Unity) plus a "<Name>_LOD1" child (~40 % triangles).
"""
import os
import sys
import math
import numpy as np
from mathutils import Vector, Matrix

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import wm_lib as W
from wm_lib import MB, Part, ring, loft, lerp_path, interp, hexc, mix, sstep

PREVIEW_VIEWS = [(0.9, -1.0, 0.5), (1.0, 0.0, 0.15), (-0.8, 1.0, 0.45), (0.2, -0.3, 1.4)]


def center_xy(mb):
    xs = [v.x for v in mb.v]
    ys = [v.y for v in mb.v]
    c = Vector(((min(xs) + max(xs)) / 2, (min(ys) + max(ys)) / 2, 0))
    mb.transform(Matrix.Translation(-c))
    return mb


def rot_z(a):
    return Matrix.Rotation(a, 3, 'Z')


# =========================================================================== shared paint bits
GRANITE = dict(cool=hexc("767a7d"), warm=hexc("8a8378"), dark=hexc("302f2c"), light=hexc("d2cec4"),
               moss=hexc("3b5220"), moss2=hexc("5f782b"), lichen=hexc("b3ad62"),
               dirt=hexc("43372a"))


def granite(ctx, seed, moss_amt=1.0, lichen=True):
    P, N = ctx.P, ctx.N
    big = ctx.vnoise((0.9, 0.9, 0.9), 3, seed)
    mid = ctx.vnoise((4, 4, 4), 3, seed + 1)
    spk = ctx.vnoise((42, 42, 42), 2, seed + 2)
    col = mix(GRANITE["cool"], GRANITE["warm"], np.clip(0.5 + 1.2 * big, 0, 1))
    col = col * (1 + 0.16 * mid[:, None])
    col = mix(col, GRANITE["light"], sstep(0.20, 0.40, spk) * 0.6)
    col = mix(col, GRANITE["dark"], sstep(-0.22, -0.42, spk) * 0.65)
    col = col * (1 + 0.10 * ctx.fnoise(5.0, seed + 3)[:, None])
    col = col * (1 - 0.18 * sstep(0.0, -0.8, ctx.FN[:, 2]))[:, None]  # down-facing facets
    if moss_amt > 0:
        mn = ctx.vnoise((1.8, 1.8, 1.8), 3, seed + 5)
        m = sstep(0.50, 0.85, ctx.N[:, 2] + 0.55 * mn) * moss_amt
        mc = mix(GRANITE["moss"], GRANITE["moss2"],
                 np.clip(0.5 + 1.2 * ctx.vnoise((6, 6, 6), 2, seed + 6), 0, 1))
        mc = mc * (1 + 0.22 * ctx.fnoise(11.0, seed + 7)[:, None])
        col = mix(col, mc, m)
    if lichen:
        ln = ctx.vnoise((8, 8, 8), 2, seed + 9)
        col = mix(col, GRANITE["lichen"], sstep(0.40, 0.50, ln) * sstep(-0.3, 0.2, N[:, 2]) * 0.6)
    col = mix(col, GRANITE["dirt"], sstep(0.12, 0.0, P[:, 2]) * 0.6)
    return col


# =========================================================================== rocks
def rock_mb(seed, size, cuts=14, subdiv=4, noise_amp=0.035, flat_bottom=-0.55, mat="M_Stone"):
    """Granite chunk: icosphere -> big noise -> planar fracture facets -> fine noise."""
    rng = np.random.default_rng(seed)
    mb = MB()
    W.ico(mb, (0, 0, 0), 1.0, mat, subdiv=subdiv)
    planes = []
    for i in range(cuts):
        d = rng.normal(size=3)
        d[2] = abs(d[2]) * 0.9 if i % 3 else d[2] * 0.3
        d /= np.linalg.norm(d)
        planes.append((Vector(d), rng.uniform(0.58, 0.88)))
    off = Vector(rng.uniform(-50, 50, 3))
    for i, v in enumerate(mb.v):
        p = v.copy()
        p *= 1 + 0.12 * W.noise1(p + off, 1.1, seed)
        for d, h in planes:
            k = p.dot(d)
            if k > h:
                p -= d * (k - h) * 0.95
        p += Vector((W.noise1(p + off, 3.5, seed + 1), W.noise1(p + off, 3.5, seed + 2),
                     W.noise1(p + off, 3.5, seed + 3))) * noise_amp
        if p.z < flat_bottom:
            p.z = flat_bottom + (p.z - flat_bottom) * 0.15
        mb.v[i] = Vector((p.x * size[0], p.y * size[1], p.z * size[2]))
    return mb


def boulder(name, seed, size, moss):
    mb = center_xy(rock_mb(seed, size))

    def paint(ctx):
        return granite(ctx, seed, moss_amt=moss)
    return W.assemble(name, [Part(name, mb, (0, 0, 0), paint=paint, smooth=28)], root_mode="mesh",
                      ao={"dist": max(size) * 0.6, "rays": 40}, cav=(1.2, 0.6), face_jitter=0.05,
                      seed=seed)


def small_rocks(name, seed, spec):
    mb = MB()
    for i, (x, y, sz, rz, sub) in enumerate(spec):
        r = rock_mb(seed + i * 7, sz, cuts=10, subdiv=sub, noise_amp=0.05, flat_bottom=-0.5)
        zmin = min(v.z for v in r.v)
        r.transform(Matrix.Translation((x, y, -zmin - 0.01)) @ rot_z(rz).to_4x4())
        mb.extend(r)
    center_xy(mb)

    def paint(ctx):
        return granite(ctx, seed, moss_amt=0.5)
    return W.assemble(name, [Part(name, mb, (0, 0, 0), paint=paint, smooth=28)], root_mode="mesh",
                      ao={"dist": 0.25, "rays": 40}, cav=(1.2, 0.6), face_jitter=0.05, seed=seed)


# =========================================================================== logs / stump
def ridge(theta, along, seed, freq=4.0, stretch=0.35):
    """Bark ridge field in ~[-1, 1], elongated along the trunk axis."""
    v = Vector((math.cos(theta) * freq, math.sin(theta) * freq, along * stretch))
    return W.noise1(v, 1.0, seed) + 0.5 * W.noise1(v * 2.3, 1.0, seed + 1)


def end_grain(mb, ring_idx, centre, mat="M_Wood#end", out=Vector((0, 0, 0)), fracs=None):
    """Concentric growth-ring loops inside a closed ring; loops alternate w = 0 / 1."""
    fracs = fracs or (0.82, 0.68, 0.54, 0.40, 0.26, 0.13)
    n = len(ring_idx)
    prev = ring_idx
    for li, f in enumerate(fracs):
        ri = [mb.add(centre + (mb.v[vi] - centre) * f + out * (1 - f)) for vi in ring_idx]
        for vi in ri:
            mb.w[vi] = float(li % 2)
        for k in range(n):
            k1 = (k + 1) % n
            mb.face([prev[k], prev[k1], ri[k1], ri[k]], mat)
        prev = ri
    c = mb.add(centre + out)
    mb.w[c] = 0.5
    for k in range(n):
        mb.face([prev[k], prev[(k + 1) % n], c], mat)


def log_mb(seed, length, radius, sides=20, rings=15, birch=False):
    rng = np.random.default_rng(seed)
    mb = MB()
    amp = 0.035 if birch else 0.11
    xs = np.linspace(-length / 2, length / 2, rings)
    path, profs, wv = [], [], []
    for i, x in enumerate(xs):
        t = i / (rings - 1)
        r = radius * (1.05 - 0.12 * t)
        bend = 0.05 * math.sin(t * math.pi) * radius
        path.append(Vector((x, bend * 0.6, r + bend)))
        pr, ww = [], []
        for k in range(sides):
            th = 2 * math.pi * k / sides
            m = ridge(th, x, seed, freq=2.0 if birch else 4.0)
            pr.append((r * (1 + amp * m) * math.cos(th), r * (1 + amp * m) * math.sin(th)))
            ww.append(m)
        profs.append(pr)
        wv.append(ww)
    rings_idx = loft(mb, path, profs, "M_Bark", up_hint=(0, 0, 1), cap0=None, cap1=None)
    for ri, ww in zip(rings_idx, wv):
        for vi, m in zip(ri, ww):
            mb.w[vi] = m
    n = sides
    # -X end: broken - a crown of ragged splinters around a recessed centre
    first, p0 = rings_idx[0], path[0]
    teeth, inner = [], []
    for k, vi in enumerate(first):
        d = mb.v[vi] - p0
        h = radius * (0.08 + 0.9 * rng.random() ** 2) * (1.0 if k % 2 else 0.45)
        teeth.append(mb.add(p0 + d * 0.86 + Vector((-h, 0, 0))))
        inner.append(mb.add(p0 + d * 0.42 + Vector((-radius * 0.15 * rng.random(), 0, 0))))
    c0 = mb.add(p0 + Vector((-0.02, 0, 0)))
    for k in range(n):
        k1 = (k + 1) % n
        mb.face([first[k1], first[k], teeth[k], teeth[k1]], "M_Wood#splinter")
        mb.face([teeth[k1], teeth[k], inner[k], inner[k1]], "M_Wood#splinter")
        mb.face([inner[k1], inner[k], c0], "M_Wood#splinter")
    # +X end: old clean cut - bark lip then growth rings
    last, pe = rings_idx[-1], path[-1]
    lip = [mb.add(pe + (mb.v[vi] - pe) * 0.93 + Vector((0.006, 0, 0))) for vi in last]
    for k in range(n):
        k1 = (k + 1) % n
        mb.face([last[k], last[k1], lip[k1], lip[k]], "M_Bark")
    end_grain(mb, lip, pe + Vector((0.006, 0, 0)), out=Vector((0.004, 0, 0)))
    return mb, path


def flat_bottom(mb, h):
    zmin = min(v.z for v in mb.v)
    for v in mb.v:
        if v.z < zmin + h:
            v.z = zmin + h * 0.5 + (v.z - zmin - h * 0.5) * 0.25


def fallen_log(name, birch):
    seed = 31 if birch else 30
    length, radius = (2.4, 0.21) if birch else (2.8, 0.31)
    mb, path = log_mb(seed, length, radius, birch=birch)
    # branch stub
    base = Vector((length * 0.12, radius * 0.3, radius * 1.65))
    d = Vector((0.25, 0.45, 0.85)).normalized()
    bp = lerp_path([base - d * radius * 0.5, base + d * radius * 0.6, base + d * radius * 1.4], 4)
    rings_b = W.tube(mb, bp, [radius * 0.32, radius * 0.27, radius * 0.22, radius * 0.17],
                     "M_Bark", sides=7, cap0="flat", cap1=None)
    end_grain(mb, rings_b[-1], bp[-1], fracs=(0.6, 0.3))
    if not birch:  # two bracket fungi on the shady side
        for (x, s_) in ((-0.55, 1.0), (-0.30, 0.7)):
            cap = MB()
            W.ico(cap, (0, 0, 0), 0.10 * s_, "M_Cap#bracket", subdiv=2, scale=(1.0, 0.65, 0.22))
            cap.transform(Matrix.Translation(Vector((x, -radius * 1.0 - 0.02 * s_, radius * 1.15))))
            mb.extend(cap)
    flat_bottom(mb, 0.035)
    center_xy(mb)
    if birch:
        C = dict(bark=hexc("e8e4da"), bark2=hexc("c9c3b6"), groove=hexc("8d877c"),
                 dash=hexc("1f1c1a"), inner=hexc("b86c3c"), wood=hexc("dcc59c"),
                 ring=hexc("ad8e62"), splinter=hexc("e2d1ad"), moss=hexc("617e30"))
    else:
        C = dict(bark=hexc("665443"), bark2=hexc("4a3c30"), groove=hexc("1f1914"),
                 dash=hexc("2a221c"), inner=hexc("7a5a3a"), wood=hexc("b98f5b"),
                 ring=hexc("6f4f2f"), splinter=hexc("cfb284"), moss=hexc("435d22"))

    def paint(ctx):
        P, N = ctx.P, ctx.N
        x, y, z = P[:, 0], P[:, 1], P[:, 2]
        streak = ctx.vnoise((0.5, 10, 10), 3, seed)
        col = mix(C["bark"], C["bark2"], np.clip(0.5 + 1.3 * streak, 0, 1) * 0.7)
        col = mix(col, C["groove"], sstep(-0.1, -0.8, ctx.w) * (0.4 if birch else 0.85))
        col = col * (1 + 0.10 * sstep(0.2, 0.9, ctx.w))[:, None]
        if birch:
            th = np.arctan2(z - radius, y)
            dash = np.array([W.noise1((xx * 14.0, math.cos(t) * 1.6, math.sin(t) * 1.6), 1.0, seed + 4)
                             for xx, t in zip(x, th)])
            col = mix(col, C["dash"], sstep(0.28, 0.40, dash) * 0.95)
            col = mix(col, C["inner"], sstep(0.45, 0.52, ctx.vnoise((2.5, 2.5, 2.5), 2, seed + 8)) * 0.8)
        mn = ctx.vnoise((2.5, 2.5, 2.5), 3, seed + 5)
        moss = sstep(0.55, 0.85, N[:, 2] + 0.5 * mn) * (0.35 if birch else 1.0)
        mc = mix(C["moss"], C["moss"] * 1.5, np.clip(0.5 + ctx.vnoise((8, 8, 8), 2, 3), 0, 1))
        col = mix(col, mc * (1 + 0.25 * ctx.fnoise(10, seed + 6)[:, None]), moss)
        end = ctx.tag == "end"
        r = np.sqrt((y - y.mean()) ** 2 + (z - radius) ** 2)
        wc = mix(C["wood"], C["ring"], ctx.w * 0.75)
        wc = wc * (1 + 0.08 * ctx.vnoise((20, 20, 20), 2, seed + 7)[:, None])
        col = np.where(end[:, None], wc, col)
        spl = ctx.tag == "splinter"
        sc = mix(C["splinter"], C["ring"], np.clip(0.5 + 1.5 * ctx.vnoise((2, 30, 30), 2, seed + 9), 0, 1) * 0.6)
        col = np.where(spl[:, None], sc, col)
        cap = ctx.tag == "bracket"
        fc = mix(hexc("e0c896"), hexc("6e4a2c"), sstep(-0.3, 0.7, N[:, 2]))
        col = np.where(cap[:, None], fc, col)
        col = mix(col, GRANITE["dirt"], sstep(0.06, 0.0, z) * 0.5)
        return col
    return W.assemble(name, [Part(name, mb, (0, 0, 0), paint=paint, smooth=45)], root_mode="mesh",
                      ao={"dist": 0.5, "rays": 40}, cav=(1.2, 0.5), face_jitter=0.04, seed=seed)


def stump(name="Stump"):
    seed = 40
    mb = MB()
    sides, R = 18, 0.36
    zs = [0.0, 0.04, 0.10, 0.18, 0.28, 0.38, 0.46, 0.50]
    path, profs, wv = [], [], []
    for z in zs:
        rootamp = interp([(0, 0.95), (0.12, 0.45), (0.25, 0.12), (0.5, 0.0)], z)
        pr, ww = [], []
        for k in range(sides):
            th = 2 * math.pi * k / sides
            roots = max(0.0, math.cos(5 * th + 0.4 + 0.3 * math.sin(2 * th))) ** 3
            m = ridge(th, z * 4, seed)
            r = R * (1 + rootamp * roots * (0.7 + 0.3 * math.sin(th * 3))) * (1 + 0.07 * m)
            pr.append((r * math.cos(th), r * math.sin(th)))
            ww.append(m)
        path.append(Vector((0.03 * z, 0, z)))
        profs.append(pr)
        wv.append(ww)
    rings_idx = loft(mb, path, profs, "M_Bark", up_hint=(0, -1, 0), cap0="flat", cap1=None)
    for ri, ww in zip(rings_idx, wv):
        for vi, m in zip(ri, ww):
            mb.w[vi] = m
    top, pc = rings_idx[-1], path[-1]
    lip = [mb.add(pc + (mb.v[vi] - pc) * 0.92 + Vector((0, 0, 0.006))) for vi in top]
    for k in range(sides):
        k1 = (k + 1) % sides
        mb.face([top[k], top[k1], lip[k1], lip[k]], "M_Bark")
    end_grain(mb, lip, pc + Vector((0, 0, 0.006)), out=Vector((0, 0, 0.008)),
              fracs=(0.84, 0.70, 0.56, 0.42, 0.28, 0.14))
    center_xy(mb)
    C = dict(bark=hexc("5e4c3b"), bark2=hexc("3b3026"), groove=hexc("1d1712"), wood=hexc("c09a64"),
             ring=hexc("7a5a34"), moss=hexc("435d22"), moss2=hexc("6c8a32"))

    def paint(ctx):
        P, N = ctx.P, ctx.N
        x, y, z = P[:, 0], P[:, 1], P[:, 2]
        streak = ctx.vnoise((9, 9, 0.8), 3, seed)
        col = mix(C["bark"], C["bark2"], np.clip(0.5 + 1.2 * streak, 0, 1) * 0.75)
        col = mix(col, C["groove"], sstep(-0.1, -0.8, ctx.w) * 0.8)
        mn = ctx.vnoise((4, 4, 4), 3, seed + 5)
        m = sstep(0.45, 0.8, N[:, 2] + 0.5 * mn) * sstep(0.0, 0.06, z)
        m = np.maximum(m, sstep(0.22, 0.0, z) * sstep(0.1, 0.5, mn) * 0.8)
        mc = mix(C["moss"], C["moss2"], np.clip(0.5 + ctx.vnoise((9, 9, 9), 2, seed + 6), 0, 1))
        col = mix(col, mc * (1 + 0.2 * ctx.fnoise(12, seed)[:, None]), m)
        wc = mix(C["wood"], C["ring"], ctx.w * 0.7)
        wc = wc * (1 + 0.08 * ctx.vnoise((14, 14, 14), 2, seed + 7)[:, None])
        col = np.where((ctx.tag == "end")[:, None], wc, col)
        col = mix(col, GRANITE["dirt"], sstep(0.05, 0.0, z) * 0.6)
        return col
    return W.assemble(name, [Part(name, mb, (0, 0, 0), paint=paint, smooth=50)], root_mode="mesh",
                      ao={"dist": 0.4, "rays": 40}, cav=(1.2, 0.5), face_jitter=0.04, seed=seed)


# =========================================================================== mushrooms
def mushroom(mb, base, h, cap_r, seed, cap_mat="M_Cap", stem_mat="M_Cap#stem", lean=(0, 0),
             bell=0.5, sides=10, stem_r=None):
    rng = np.random.default_rng(seed)
    stem_r = stem_r or cap_r * 0.28
    lean = Vector((lean[0], lean[1], 0))
    sp = [Vector(base) + lean * (t * t) + Vector((0, 0, h * t)) for t in (0, 0.35, 0.7, 1.0)]
    W.tube(mb, sp, [stem_r * 1.25, stem_r * 1.0, stem_r * 0.92, stem_r * 0.85], stem_mat,
           sides=max(5, sides // 2 + 1), cap0="flat", cap1="flat", up_hint=(0, -1, 0))
    top = sp[-1]
    # cap: dome from rim up to the crown, underside (gills) from rim to stem
    ch = cap_r * (0.55 + bell * 0.5)
    rim_z = top.z - cap_r * 0.12
    prof_pts = [(1.0, 0.0), (0.92, 0.32), (0.70, 0.68), (0.38, 0.92)]
    path = [top.copy() + Vector((0, 0, rim_z - top.z + ch * pz)) for (pr, pz) in prof_pts]
    profs = [ring(sides, cap_r * pr, cap_r * pr,
                  mod=lambda th, s=seed: 1 + 0.06 * math.sin(3 * th + s)) for (pr, pz) in prof_pts]
    rings_idx = loft(mb, path, profs, cap_mat, up_hint=(0, -1, 0), cap0=None, cap1="point",
                     tip1=top + Vector((0, 0, rim_z - top.z + ch * 1.02)))
    rim = rings_idx[0]
    under = [mb.add(top + Vector((math.cos(2 * math.pi * k / sides) * stem_r * 1.1,
                                  math.sin(2 * math.pi * k / sides) * stem_r * 1.1,
                                  rim_z - top.z + cap_r * 0.10))) for k in range(sides)]
    gmat = cap_mat.split("#")[0] + "#gill"
    for k in range(sides):
        k1 = (k + 1) % sides
        mb.face([rim[k1], rim[k], under[k], under[k1]], gmat)
    c = mb.add(top + Vector((0, 0, rim_z - top.z + cap_r * 0.12)))
    for k in range(sides):
        mb.face([under[(k + 1) % sides], under[k], c], gmat)


def mushroom_cluster(name="MushroomCluster"):
    seed = 50
    mb = MB()
    spec = [(0.0, 0.0, 0.26, 0.13, (0.02, -0.01)), (0.13, 0.06, 0.18, 0.10, (0.04, 0.02)),
            (-0.10, 0.09, 0.14, 0.08, (-0.03, 0.03)), (0.05, -0.13, 0.10, 0.06, (0.01, -0.03)),
            (-0.12, -0.08, 0.07, 0.045, (-0.02, -0.02)), (0.18, -0.06, 0.05, 0.035, (0.02, 0.0))]
    for i, (x, y, h, r, ln) in enumerate(spec):
        mushroom(mb, (x, y, 0), h, r, seed + i, lean=ln, bell=0.35 + 0.1 * (i % 3))
    center_xy(mb)
    C = dict(cap=hexc("8a5634"), cap2=hexc("b98455"), rim=hexc("d7b48a"), gill=hexc("e6d6bd"),
             stem=hexc("eee3cf"), stem_dk=hexc("b9a688"))

    def paint(ctx):
        P, N = ctx.P, ctx.N
        z = P[:, 2]
        n = ctx.vnoise((30, 30, 30), 2, seed)
        top = sstep(0.2, 0.95, N[:, 2])
        col = mix(C["rim"], mix(C["cap2"], C["cap"], top), sstep(-0.1, 0.5, N[:, 2]))
        col = col * (1 + 0.1 * n[:, None])
        col = np.where((ctx.tag == "gill")[:, None], C["gill"] * (1 + 0.1 * n[:, None]), col)
        st = mix(C["stem_dk"], C["stem"], sstep(0.0, 0.06, z))
        col = np.where((ctx.tag == "stem")[:, None], st, col)
        return col
    return W.assemble(name, [Part(name, mb, (0, 0, 0), paint=paint, smooth=50)], root_mode="mesh",
                      ao={"dist": 0.12, "rays": 40}, cav=(0.8, 0.3), face_jitter=0.04, seed=seed)


def giant_mushroom(name="GiantMushroom"):
    seed = 60
    mb = MB()
    H = 1.95
    sp = lerp_path([(0, 0, 0), (0.05, 0.02, 0.6), (0.0, -0.04, 1.3), (-0.08, 0.0, H)], 10)
    rads = [interp([(0, 0.42), (0.08, 0.30), (0.25, 0.23), (0.7, 0.2), (1, 0.19)], i / 9)
            for i in range(10)]
    profs = [ring(14, r, r, mod=lambda th, i=i: 1 + 0.05 * math.sin(4 * th + i)) for i, r in
             enumerate(rads)]
    loft(mb, sp, profs, "M_Cap#stem", up_hint=(0, -1, 0), cap0="flat", cap1="flat")
    sk = Vector((0.0, -0.035, 1.38))   # skirt (annulus)
    W.tube(mb, [sk + Vector((0, 0, 0.02)), sk + Vector((0, 0, -0.03)), sk + Vector((0, 0, -0.09))],
           [0.21, 0.27, 0.30], "M_Cap#stem", sides=14, cap0="flat", cap1="flat", up_hint=(0, -1, 0))
    top = Vector((-0.08, 0.0, H))
    R, sides = 1.05, 22
    prof_pts = [(1.0, -0.10), (0.985, 0.05), (0.94, 0.20), (0.86, 0.38), (0.74, 0.55),
                (0.58, 0.71), (0.40, 0.84), (0.20, 0.93)]
    lift = lambda pz: Vector((0.04 * pz, 0, 0.62 * pz))
    path = [top + lift(pz) for (pr, pz) in prof_pts]
    profs = [ring(sides, R * pr, R * pr, mod=lambda th, pz=pz: 1 + 0.05 * math.sin(3 * th + 1)
                  - 0.06 * (pz < 0)) for (pr, pz) in prof_pts]
    rings_idx = loft(mb, path, profs, "M_Cap", up_hint=(0, -1, 0), cap0=None, cap1="point",
                     tip1=top + lift(1.0))
    rim = rings_idx[0]
    under1 = [mb.add(top + Vector((math.cos(2 * math.pi * k / sides) * R * 0.62 * (1.0 if k % 2 else 0.93),
                                   math.sin(2 * math.pi * k / sides) * R * 0.62 * (1.0 if k % 2 else 0.93),
                                   -0.02))) for k in range(sides)]
    under2 = [mb.add(top + Vector((math.cos(2 * math.pi * k / sides) * 0.24,
                                   math.sin(2 * math.pi * k / sides) * 0.24, 0.04)))
              for k in range(sides)]
    for k in range(sides):
        k1 = (k + 1) % sides
        mb.face([rim[k1], rim[k], under1[k], under1[k1]], "M_Cap#gill")
        mb.face([under1[k1], under1[k], under2[k], under2[k1]], "M_Cap#gill")
    c = mb.add(top + Vector((0, 0, 0.06)))
    for k in range(sides):
        mb.face([under2[(k + 1) % sides], under2[k], c], "M_Cap#gill")
    # raised pale warts sitting on the cap surface
    rng = np.random.default_rng(seed)
    prs = [p for p, _ in prof_pts][::-1]
    pzs = [z for _, z in prof_pts][::-1]
    for i in range(15):
        a = rng.uniform(0, 2 * math.pi)
        rho = math.sqrt(rng.uniform(0.02, 0.8))
        pz = float(np.interp(rho, prs, pzs))
        pos = top + lift(pz) + Vector((math.cos(a) * R * rho, math.sin(a) * R * rho, 0.0))
        nrm = Vector((math.cos(a) * rho * 0.9, math.sin(a) * rho * 0.9, 1.0)).normalized()
        rot = nrm.to_track_quat('Z', 'Y').to_matrix()
        W.ico(mb, pos, rng.uniform(0.05, 0.10), "M_Cap#spot", subdiv=1, scale=(1, 1, 0.38),
              jitter=0.2, seed=seed + i, rot=rot)
    center_xy(mb)
    C = dict(cap=hexc("3c2230"), cap2=hexc("5c2f44"), rim=hexc("8a5560"), spot=hexc("e3d4b8"),
             gill=hexc("6e5450"), stem=hexc("d2c9be"), stem_dk=hexc("7d7064"), moss=hexc("435d22"))

    def paint(ctx):
        P, N = ctx.P, ctx.N
        z = P[:, 2]
        n = ctx.vnoise((5, 5, 5), 3, seed)
        col = mix(C["rim"], mix(C["cap2"], C["cap"], sstep(0.3, 0.95, N[:, 2])),
                  sstep(-0.2, 0.45, N[:, 2]))
        col = col * (1 + 0.14 * n[:, None] + 0.06 * ctx.fnoise(8, seed + 1)[:, None])
        col = np.where((ctx.tag == "spot")[:, None], C["spot"] * (1 + 0.1 * n[:, None]), col)
        gc = C["gill"] * (1 + 0.2 * ctx.fnoise(25, seed)[:, None])
        col = np.where((ctx.tag == "gill")[:, None], gc, col)
        st = mix(C["stem_dk"], C["stem"], sstep(0.0, 0.5, z))
        st = st * (1 + 0.08 * ctx.vnoise((8, 8, 1), 2, seed + 2)[:, None])
        st = mix(st, C["moss"], sstep(0.25, 0.0, z) * sstep(0.0, 0.5, n) * 0.8)
        col = np.where((ctx.tag == "stem")[:, None], st, col)
        return col
    return W.assemble(name, [Part(name, mb, (0, 0, 0), paint=paint, smooth=50)], root_mode="mesh",
                      ao={"dist": 0.9, "rays": 40}, cav=(0.9, 0.3), face_jitter=0.03, seed=seed)


def glowcap_cluster(name="GlowcapCluster"):
    seed = 70
    mb = MB()
    spec = [(0.0, 0.0, 0.32, 0.10), (0.11, 0.07, 0.22, 0.075), (-0.09, 0.08, 0.17, 0.06),
            (0.06, -0.11, 0.13, 0.05), (-0.12, -0.06, 0.09, 0.04), (0.17, -0.04, 0.06, 0.03),
            (-0.03, 0.15, 0.07, 0.032)]
    for i, (x, y, h, r) in enumerate(spec):
        mushroom(mb, (x, y, 0), h, r, seed + i, cap_mat="M_Glow", stem_mat="M_Cap#stem",
                 lean=(0.03 * math.sin(i * 2.1), 0.03 * math.cos(i * 1.7)), bell=1.2, sides=9,
                 stem_r=r * 0.22)
    center_xy(mb)
    C = dict(cap=hexc("3fe8d0"), core=hexc("c8fff4"), rim=hexc("1a8fae"), gill=hexc("2a6d80"),
             stem=hexc("cfd9cc"), stem_dk=hexc("6f8a80"))

    def paint(ctx):
        P, N = ctx.P, ctx.N
        z = P[:, 2]
        col = mix(C["rim"], C["cap"], sstep(-0.2, 0.5, N[:, 2]))
        col = mix(col, C["core"], sstep(0.75, 1.0, N[:, 2]) * 0.8)
        col = np.where((ctx.tag == "gill")[:, None], C["gill"], col)
        st = mix(C["stem_dk"], C["stem"], sstep(0.0, 0.08, z))
        col = np.where((ctx.tag == "stem")[:, None], st, col)
        return col
    paint.no_ao = ("M_Glow",)
    return W.assemble(name, [Part(name, mb, (0, 0, 0), paint=paint, smooth=50)], root_mode="mesh",
                      ao={"dist": 0.1, "rays": 40}, cav=(0.6, 0.2), face_jitter=0.03, seed=seed)


# =========================================================================== desert / snow
def eroded_column(mb, base, st, sides, seed, lean=(0.10, 0.05), rough=0.18):
    """Hoodoo column: rings (z, r); equal-z pairs make crisp strata ledges."""
    path, profs = [], []
    for z, r in st:
        off = Vector((lean[0] * (z / 3.5) ** 2 + 0.10 * W.noise1((0, 0, z * 0.7), 1.0, seed + 5),
                      lean[1] * math.sin(z * 1.3) + 0.10 * W.noise1((3, 0, z * 0.7), 1.0, seed + 6),
                      z))
        path.append(Vector(base) + off)
        pr = []
        for k in range(sides):
            th = 2 * math.pi * k / sides
            rr = r * (1 + rough * W.noise1((math.cos(th) * 1.4, math.sin(th) * 1.4, z * 0.8), 1.0, seed)
                      + 0.07 * W.noise1((math.cos(th) * 4, math.sin(th) * 4, z * 3), 1.0, seed + 1))
            pr.append((rr * math.cos(th), rr * math.sin(th)))
        profs.append(pr)
    loft(mb, path, profs, "M_Stone", up_hint=(0, -1, 0), cap0="flat", cap1="point",
         tip1=path[-1] + Vector((0.02, 0, 0.03)))


def sandstone_pillar(name="SandstonePillar"):
    seed = 80
    mb = MB()
    main = [(0.0, 1.05), (0.22, 1.0), (0.45, 0.90), (0.46, 0.80), (0.80, 0.72), (1.10, 0.66),
            (1.11, 0.58), (1.45, 0.52), (1.80, 0.46), (2.10, 0.47), (2.11, 0.55), (2.35, 0.50),
            (2.62, 0.56), (2.63, 0.74), (2.78, 0.90), (2.98, 0.96), (2.99, 0.86), (3.22, 0.88),
            (3.40, 0.78), (3.52, 0.58)]
    eroded_column(mb, (0, 0, 0), main, 16, seed)
    side = [(0.0, 0.62), (0.3, 0.55), (0.31, 0.48), (0.7, 0.42), (1.05, 0.36), (1.06, 0.44),
            (1.3, 0.47), (1.5, 0.40), (1.62, 0.28)]
    eroded_column(mb, (0.78, 0.42, 0), side, 11, seed + 3, lean=(0.25, 0.08), rough=0.2)
    for i, (x, y, s_) in enumerate(((1.25, -0.25, 0.30), (-0.85, -0.85, 0.22), (-0.3, 1.0, 0.18))):
        r = rock_mb(seed + 10 + i, (s_, s_ * 0.9, s_ * 0.7), cuts=8, subdiv=2, noise_amp=0.05)
        zmin = min(v.z for v in r.v)
        r.transform(Matrix.Translation((x, y, -zmin)))
        mb.extend(r)
    center_xy(mb)
    bands = [hexc("d9a66a"), hexc("c0743c"), hexc("e3c08c"), hexc("9c4f2a"), hexc("cf9456"),
             hexc("b4673a"), hexc("e6cc9c")]

    def paint(ctx):
        P, N = ctx.P, ctx.N
        z = P[:, 2]
        wob = ctx.vnoise((1.2, 1.2, 0.3), 2, seed)
        bz = (z * 2.8 + 0.35 * wob) % len(bands)
        i0 = np.floor(bz).astype(int) % len(bands)
        i1 = (i0 + 1) % len(bands)
        f = sstep(0.72, 1.0, bz - np.floor(bz))
        B = np.array(bands)
        col = B[i0] * (1 - f[:, None]) + B[i1] * f[:, None]
        col = col * (1 + 0.12 * ctx.vnoise((6, 6, 6), 3, seed + 2)[:, None]
                     + 0.06 * ctx.fnoise(7, seed + 4)[:, None])
        varnish = ctx.vnoise((4, 4, 0.45), 3, seed + 3)          # vertical desert-varnish streaks
        col = mix(col, hexc("5e3520"), sstep(0.10, 0.45, varnish) * sstep(0.6, -0.2, N[:, 2]) * 0.55)
        col = mix(col, hexc("f0dcb4"), sstep(0.65, 0.95, N[:, 2]) * 0.45)
        col = mix(col, hexc("7a4a2c"), sstep(0.25, 0.0, z) * 0.45)
        return col
    return W.assemble(name, [Part(name, mb, (0, 0, 0), paint=paint, smooth=35)], root_mode="mesh",
                      ao={"dist": 1.0, "rays": 40}, cav=(1.2, 0.6), face_jitter=0.05, seed=seed)


def dead_tree(name="DeadTree"):
    seed = 90
    mb = MB()
    rng = np.random.default_rng(seed)

    def branch(p0, d, length, r0, depth, sides):
        d = Vector(d).normalized()
        n = 5 if depth == 0 else 4
        pts = [Vector(p0)]
        dirv = d.copy()
        for i in range(1, n):
            dirv = (dirv + Vector(rng.normal(0, 0.18, 3))).normalized()
            dirv.z = max(dirv.z, 0.15 if depth else 0.6)
            dirv.normalize()
            pts.append(pts[-1] + dirv * (length / (n - 1)))
        radii = [r0 * (1 - 0.8 * i / (n - 1)) for i in range(n)]
        W.tube(mb, pts, radii, "M_Bark", sides=sides, cap0="flat", cap1="point",
               tip1=pts[-1] + dirv * r0 * 1.5)
        return pts, radii
    tp = lerp_path([(0, 0, 0), (0.03, 0.02, 0.6), (-0.05, 0.04, 1.25), (0.02, 0.0, 1.75)], 7)
    trad = [interp([(0, 0.30), (0.12, 0.2), (0.4, 0.16), (1, 0.12)], i / 6) for i in range(7)]
    tprofs = [ring(10, r, r, mod=lambda th, i=i: 1 + (0.45 if i == 0 else 0.12 if i == 1 else 0.05) *
                   max(0, math.cos(5 * th + 0.5)) ** 2) for i, r in enumerate(trad)]
    loft(mb, tp, tprofs, "M_Bark", up_hint=(0, -1, 0), cap0="flat", cap1="point",
         tip1=tp[-1] + Vector((0.0, 0.0, 0.2)))
    top = tp[-1]
    mains = [((0.8, 0.2, 1.0), 1.15), ((-0.7, 0.5, 1.0), 1.0), ((-0.1, -0.9, 1.0), 0.9)]
    for i, (d, L) in enumerate(mains):
        start = tp[5] if i == 2 else top - Vector((0, 0, 0.05))
        pts, radii = branch(start, d, L, 0.085, 0, 7)
        sub_d = Vector(d) + Vector((0.6 * (-1) ** i, 0.5, 0.6))
        branch(pts[2], sub_d, L * 0.45, 0.035, 1, 5)
    branch(tp[3], (0.9, -0.5, 0.5), 0.55, 0.05, 1, 5)
    for k in range(5):  # short surface roots that dive into the ground
        a = k * 2 * math.pi / 5 + 0.5
        rp = [Vector((math.cos(a) * 0.14, math.sin(a) * 0.14, 0.16)),
              Vector((math.cos(a) * 0.32, math.sin(a) * 0.32, 0.05)),
              Vector((math.cos(a) * 0.46, math.sin(a) * 0.46, 0.0))]
        W.tube(mb, rp, [0.10, 0.065, 0.035], "M_Bark", sides=5, cap0="flat", cap1="point",
               tip1=rp[-1] + Vector((math.cos(a) * 0.05, math.sin(a) * 0.05, -0.02)))
    for v in mb.v:
        if v.z < 0:
            v.z = 0.0
    center_xy(mb)
    C = dict(bark=hexc("8b8276"), bark2=hexc("5c544a"), tip=hexc("cbc3b3"), crack=hexc("2f2924"))

    def paint(ctx):
        P, N = ctx.P, ctx.N
        z = P[:, 2]
        streak = ctx.vnoise((10, 10, 1.0), 3, seed)
        col = mix(C["bark"], C["bark2"], np.clip(0.5 + 1.4 * streak, 0, 1) * 0.75)
        col = mix(col, C["crack"], sstep(0.28, 0.45, ctx.vnoise((24, 24, 2), 2, seed + 1)) * 0.7)
        col = mix(col, C["tip"], sstep(1.8, 3.0, z) * 0.6)
        col = mix(col, C["tip"], sstep(0.5, 0.95, N[:, 2]) * 0.35)
        col = mix(col, hexc("6b5a46"), sstep(0.15, 0.0, z) * 0.5)
        return col
    return W.assemble(name, [Part(name, mb, (0, 0, 0), paint=paint, smooth=50)], root_mode="mesh",
                      ao={"dist": 0.6, "rays": 40}, cav=(1.0, 0.4), face_jitter=0.04, seed=seed)


def ice_spike(name="IceSpike"):
    seed = 100
    mb = MB()
    rng = np.random.default_rng(seed)
    spikes = [((0, 0), 3.6, 0.42, (0.03, 0.02)), ((0.55, 0.25), 2.3, 0.30, (0.25, 0.12)),
              ((-0.45, 0.35), 1.7, 0.26, (-0.22, 0.18)), ((0.1, -0.55), 1.3, 0.22, (0.05, -0.28)),
              ((-0.55, -0.35), 0.8, 0.17, (-0.3, -0.2)), ((0.62, -0.38), 0.6, 0.13, (0.3, -0.25))]
    for i, ((x, y), h, r, (lx, ly)) in enumerate(spikes):
        base = Vector((x, y, -0.15))
        axis = Vector((lx * 0.35, ly * 0.35, 1)).normalized()
        pts = [base + axis * h * t for t in (0, 0.3, 0.62, 0.84)]
        rr = [r, r * 0.9, r * 0.72, r * 0.45]
        ph = rng.uniform(0, 1)
        profs = [ring(6, q, q, phase=ph, mod=lambda th, j=j: 1 + 0.08 * math.sin(2 * th + j))
                 for j, q in enumerate(rr)]
        loft(mb, pts, profs, "M_Ice", up_hint=(0, -1, 0), cap0="flat", cap1="point",
             tip1=base + axis * h)
    for i in range(4):
        a = i * 1.6 + 0.3
        c = (math.cos(a) * 0.45, math.sin(a) * 0.45, 0.0)
        r = rock_mb(seed + 20 + i, (0.32, 0.28, 0.2), cuts=6, subdiv=2, noise_amp=0.04, mat="M_Ice")
        r.transform(Matrix.Translation(c))
        mb.extend(r)
    zmin = 0.0
    for v in mb.v:
        if v.z < zmin:
            v.z = zmin
    center_xy(mb)
    C = dict(deep=hexc("4d86bf"), mid=hexc("8fc6ea"), pale=hexc("d6f0fb"), white=hexc("f4fbff"))

    def paint(ctx):
        P, N = ctx.P, ctx.N
        z = P[:, 2]
        col = mix(C["deep"], C["mid"], sstep(0.0, 1.2, z))
        col = mix(col, C["pale"], sstep(1.0, 2.6, z))
        col = mix(col, C["white"], sstep(2.6, 3.4, z))
        streak = ctx.vnoise((4, 4, 0.6), 2, seed)
        col = col * (1 + 0.12 * streak[:, None])
        col = mix(col, C["white"], sstep(0.55, 0.9, N[:, 2]) * 0.6)  # frost on up-facing facets
        return col
    return W.assemble(name, [Part(name, mb, (0, 0, 0), paint=paint, smooth=None)], root_mode="mesh",
                      ao={"dist": 0.8, "rays": 32, "strength": 0.6}, cav=(0.4, 0.6), face_jitter=0.06,
                      seed=seed)


# =========================================================================== plants
def bush(name, seed, width, height, n_clumps, berries=0):
    rng = np.random.default_rng(seed)
    mb = MB()
    clumps = []
    for i in range(n_clumps):
        a = rng.uniform(0, 2 * math.pi)
        f = math.sqrt(rng.uniform(0.0, 1.0))
        rr = f * width * 0.33
        s_ = (rng.uniform(0.40, 0.52) - 0.12 * f) * width
        sz = rng.uniform(0.72, 0.9)
        cz = max(s_ * sz * 0.42, height * (0.62 - 0.30 * f) + rng.uniform(-0.04, 0.05) - s_ * sz * 0.25)
        c = Vector((math.cos(a) * rr, math.sin(a) * rr, cz))
        clumps.append((c, s_ / 2, sz))
        W.ico(mb, c, s_ / 2, "M_Leaves", subdiv=2, scale=(1.0, 1.0, sz), jitter=0.22,
              seed=seed + i, rot=Matrix.Rotation(rng.uniform(0, 6.28), 3, 'Z'))
    for k in range(3):  # a few woody stems peeking out at the base
        a = k * 2.1 + 0.4
        p = [Vector((math.cos(a) * 0.04, math.sin(a) * 0.04, 0.0)),
             Vector((math.cos(a) * 0.14, math.sin(a) * 0.14, height * 0.22))]
        W.tube(mb, p, [0.028, 0.016], "M_Bark", sides=4, cap0="flat", cap1="flat")
    for b in range(berries):
        c, r, sz = clumps[rng.integers(len(clumps))]
        d = Vector((rng.normal(), rng.normal(), abs(rng.normal()) + 0.3)).normalized()
        p = c + Vector((d.x * r, d.y * r, d.z * r * sz)) * 0.98
        W.ico(mb, p, 0.03, "M_Leaves#berry", subdiv=1)
    for v in mb.v:
        if v.z < 0:
            v.z *= 0.2
    center_xy(mb)
    C = dict(leaf=hexc("406a28"), leaf2=hexc("5e8a33"), top=hexc("8fb447"), dark=hexc("1f3517"),
             berry=hexc("9a1426"), berry2=hexc("e04a4a"), twig=hexc("4a3a2a"))

    def paint(ctx):
        P, N = ctx.P, ctx.N
        z = P[:, 2]
        fn = ctx.fnoise(7.0, seed)
        col = mix(C["leaf"], C["leaf2"], np.clip(0.5 + 1.2 * ctx.vnoise((2.5, 2.5, 2.5), 2, seed), 0, 1))
        col = mix(col, C["top"], sstep(0.35, 1.0, N[:, 2]) * sstep(height * 0.45, height, z) * 0.75)
        col = mix(col, C["dark"], sstep(height * 0.4, 0.0, z) * 0.55)
        col = col * (1 + 0.24 * fn[:, None])
        bc = mix(C["berry"], C["berry2"], sstep(0.3, 0.9, N[:, 2]))
        col = np.where((ctx.tag == "berry")[:, None], bc, col)
        col = np.where(ctx.is_mat("M_Bark")[:, None], C["twig"], col)
        return col
    paint.no_ao = ()
    return W.assemble(name, [Part(name, mb, (0, 0, 0), paint=paint, smooth=None)], root_mode="mesh",
                      ao={"dist": width * 0.45, "rays": 40}, cav=(0.5, 0.2), face_jitter=0.10,
                      seed=seed)


def reeds(name="Reeds"):
    seed = 110
    rng = np.random.default_rng(seed)
    mb = MB()
    for i in range(18):
        a = rng.uniform(0, 2 * math.pi)
        rr = rng.uniform(0.0, 0.3)
        base = Vector((math.cos(a) * rr, math.sin(a) * rr, 0.0))
        h = rng.uniform(0.8, 1.55)
        lean = Vector((math.cos(a), math.sin(a), 0)) * rng.uniform(0.08, 0.3)
        pts = [base + lean * (t ** 2) + Vector((0, 0, h * t)) for t in (0, 0.25, 0.5, 0.72, 0.9)]
        w = rng.uniform(0.018, 0.026)
        profs = [ring(3, w * (1 - 0.75 * t), w * 0.35 * (1 - 0.6 * t), phase=a)
                 for t in (0, 0.25, 0.5, 0.72, 0.9)]
        loft(mb, pts, profs, "M_Leaves#blade", up_hint=(0, -1, 0), cap0="flat", cap1="point",
             tip1=base + lean * 1.1 + Vector((0, 0, h)))
    for i in range(4):
        a = i * 1.57 + 0.6
        base = Vector((math.cos(a) * 0.12, math.sin(a) * 0.12, 0.0))
        h = 1.25 + 0.12 * i
        lean = Vector((math.cos(a), math.sin(a), 0)) * 0.06
        sp = [base + lean * t + Vector((0, 0, h * t)) for t in (0, 0.5, 0.78)]
        W.tube(mb, sp, [0.012, 0.010, 0.008], "M_Leaves#stalk", sides=4, cap0="flat", cap1="flat")
        hp = [base + lean * t + Vector((0, 0, h * t)) for t in (0.78, 0.80, 0.92, 0.94)]
        W.tube(mb, hp, [0.012, 0.032, 0.032, 0.012], "M_Leaves#cattail", sides=6, cap0="flat",
               cap1="flat", up_hint=(0, -1, 0))
        W.tube(mb, [hp[-1], base + lean + Vector((0, 0, h * 1.02))], [0.006, 0.003], "M_Leaves#stalk",
               sides=3, cap0="flat", cap1="point", tip1=base + lean + Vector((0, 0, h * 1.06)))
    center_xy(mb)
    C = dict(base=hexc("3c5a26"), mid=hexc("5f8236"), tip=hexc("bdb56c"), cat=hexc("5a3820"),
             stalk=hexc("6f7f3a"))

    def paint(ctx):
        P, N = ctx.P, ctx.N
        z = P[:, 2]
        col = mix(C["base"], C["mid"], sstep(0.0, 0.5, z))
        col = mix(col, C["tip"], sstep(0.9, 1.5, z) * 0.85)
        col = col * (1 + 0.12 * ctx.vnoise((20, 20, 2), 2, seed)[:, None])
        col = np.where((ctx.tag == "cattail")[:, None], C["cat"] * (1 + 0.1 * ctx.vnoise((60, 60, 60), 2, 3)[:, None]), col)
        col = np.where((ctx.tag == "stalk")[:, None], mix(C["base"], C["stalk"], sstep(0, 0.8, z)), col)
        return col
    return W.assemble(name, [Part(name, mb, (0, 0, 0), paint=paint, smooth=45)], root_mode="mesh",
                      ao={"dist": 0.3, "rays": 32, "strength": 0.7}, cav=(0.3, 0.1), face_jitter=0.05,
                      seed=seed)


def lily_pad(name="LilyPad"):
    seed = 120
    mb = MB()

    def pad(c, R, rot, thick=0.012):
        segs = 18
        notch = math.radians(34)
        angs = [notch / 2 + (2 * math.pi - notch) * k / segs for k in range(segs + 1)]
        top, bot = [], []
        for f in (0.0, 0.45, 0.8, 1.0):
            rt, rb = [], []
            for a in angs:
                aa = a + rot
                curl = 0.012 * (f ** 3)
                p = Vector((c[0] + math.cos(aa) * R * f, c[1] + math.sin(aa) * R * f, thick + curl))
                rt.append(mb.add(p))
                rb.append(mb.add(p - Vector((0, 0, thick))))
            top.append(rt)
            bot.append(rb)
        n = len(angs)
        for i in range(3):
            for k in range(n - 1):
                mb.face([top[i][k], top[i][k + 1], top[i + 1][k + 1], top[i + 1][k]], "M_Leaves#pad")
                mb.face([bot[i][k], bot[i + 1][k], bot[i + 1][k + 1], bot[i][k + 1]], "M_Leaves#under")
        for k in range(n - 1):  # rim
            mb.face([top[3][k], top[3][k + 1], bot[3][k + 1], bot[3][k]], "M_Leaves#under")
        for i in range(3):      # notch walls
            mb.face([top[i][0], top[i + 1][0], bot[i + 1][0], bot[i][0]], "M_Leaves#under")
            mb.face([top[i][-1], bot[i][-1], bot[i + 1][-1], top[i + 1][-1]], "M_Leaves#under")
    pad((0, 0), 0.44, 0.3)
    pad((0.50, 0.32), 0.26, 2.4)
    pad((-0.36, 0.42), 0.18, 4.1)
    # flower: two rings of pointed petals + a stamen dome
    fc = Vector((0.18, -0.14, 0.012))
    for layer, (n, L, up, off) in enumerate(((8, 0.11, 0.35, 0.0), (6, 0.08, 0.8, 0.4))):
        for k in range(n):
            a = 2 * math.pi * k / n + off
            d = Vector((math.cos(a), math.sin(a), up)).normalized()
            side = Vector((-math.sin(a), math.cos(a), 0)) * 0.028
            b = fc + Vector((0, 0, 0.01 * layer))
            m = b + d * L * 0.5
            t = b + d * L
            i0, i1, i2, i3 = mb.add(b), mb.add(m + side), mb.add(t), mb.add(m - side)
            i4 = mb.add(m + Vector((0, 0, -0.008)))
            mb.face([i0, i1, i2, i3], "M_Leaves#petal")
            mb.face([i0, i3, i4], "M_Leaves#petal")
            mb.face([i3, i2, i4], "M_Leaves#petal")
            mb.face([i2, i1, i4], "M_Leaves#petal")
            mb.face([i1, i0, i4], "M_Leaves#petal")
    W.ico(mb, fc + Vector((0, 0, 0.02)), 0.022, "M_Leaves#stamen", subdiv=1, scale=(1, 1, 0.7))
    center_xy(mb)
    C = dict(pad=hexc("3f6b2a"), pad2=hexc("5d8a34"), vein=hexc("8fb35a"), under=hexc("6b3f3a"),
             edge=hexc("8a4a3a"), petal=hexc("f4e6ef"), petal2=hexc("e58fb4"), stamen=hexc("f0c53a"))

    def paint(ctx):
        P, N = ctx.P, ctx.N
        x, y = P[:, 0], P[:, 1]
        col = mix(C["pad"], C["pad2"], np.clip(0.5 + ctx.vnoise((5, 5, 5), 2, seed), 0, 1))
        col = col * (1 + 0.12 * ctx.fnoise(14, seed)[:, None])
        col = np.where((ctx.tag == "under")[:, None], C["under"], col)
        pet = ctx.tag == "petal"
        dc = np.linalg.norm(P[:, :2] - np.array([0.18 + 0.0, -0.14]), axis=1)
        pc = mix(C["petal2"], C["petal"], sstep(0.02, 0.09, dc))
        col = np.where(pet[:, None], pc, col)
        col = np.where((ctx.tag == "stamen")[:, None], C["stamen"], col)
        return col
    return W.assemble(name, [Part(name, mb, (0, 0, 0), paint=paint, smooth=None)], root_mode="mesh",
                      ao={"dist": 0.1, "rays": 32, "ground": False, "strength": 0.6}, cav=(0.4, 0.2),
                      face_jitter=0.05, seed=seed)


# =========================================================================== registry
ASSETS = {
    "Boulder_A": (lambda: boulder("Boulder_A", 201, (0.85, 0.75, 0.72), 1.0),
                  ["Forest", "Plains", "Mountains", "Taiga"]),
    "Boulder_B": (lambda: boulder("Boulder_B", 202, (1.25, 0.95, 0.80), 0.8),
                  ["Mountains", "Plains", "Taiga"]),
    "Boulder_C": (lambda: boulder("Boulder_C", 203, (0.55, 0.50, 0.85), 1.0),
                  ["Forest", "DarkForest", "Riverbank"]),
    "Rocks_Small_A": (lambda: small_rocks("Rocks_Small_A", 211, [
        (0.0, 0.0, (0.24, 0.20, 0.17), 0.3, 3), (0.30, 0.12, (0.16, 0.13, 0.11), 1.2, 3),
        (-0.22, 0.20, (0.11, 0.10, 0.08), 2.0, 3)]), ["Plains", "Forest", "Mountains", "Riverbank"]),
    "Rocks_Small_B": (lambda: small_rocks("Rocks_Small_B", 212, [
        (0.0, 0.0, (0.20, 0.16, 0.10), 0.5, 3), (0.24, -0.10, (0.15, 0.12, 0.08), 1.4, 3),
        (-0.16, 0.18, (0.10, 0.09, 0.06), 2.2, 2), (0.10, 0.24, (0.08, 0.07, 0.05), 0.9, 2),
        (-0.25, -0.12, (0.07, 0.06, 0.05), 2.9, 2)]), ["Riverbank", "Beach", "Plains", "Desert"]),
    "FallenLog_Oak": (lambda: fallen_log("FallenLog_Oak", False), ["Forest", "DarkForest"]),
    "FallenLog_Birch": (lambda: fallen_log("FallenLog_Birch", True), ["BirchForest", "Forest"]),
    "Stump": (stump, ["Forest", "BirchForest", "Taiga", "DarkForest"]),
    "MushroomCluster": (mushroom_cluster, ["Forest", "DarkForest", "Taiga"]),
    "GiantMushroom": (giant_mushroom, ["DarkForest"]),
    "GlowcapCluster": (glowcap_cluster, ["DarkForest", "Caves"]),
    "SandstonePillar": (sandstone_pillar, ["Desert"]),
    "DeadTree": (dead_tree, ["Desert", "Snowy"]),
    "IceSpike": (ice_spike, ["Snowy"]),
    "Bush_A": (lambda: bush("Bush_A", 130, 1.2, 0.95, 12), ["Forest", "Plains", "BirchForest"]),
    "Bush_B": (lambda: bush("Bush_B", 131, 1.5, 0.75, 11, berries=14), ["Plains", "Forest", "Taiga"]),
    "Reeds": (reeds, ["Riverbank", "Swamp"]),
    "LilyPad": (lily_pad, ["Riverbank", "Swamp"]),
}
NOTES = {
    "LilyPad": "Origin at the pad underside centre; place at the water surface (y = sea level + ~0.0). "
               "Pads are 12 mm thick, double-sided by geometry.",
    "Reeds": "Blades are thin 3-sided prisms (no double-sided shader needed).",
    "GlowcapCluster": "Caps use M_Glow (emissive, vertex colour = emission colour).",
    "IceSpike": "Flat-shaded crystal facets; M_Ice may be rendered glossy/translucent.",
}


def main():
    args = W.parse_args()
    entries = {}
    for nm, (fn, biomes) in ASSETS.items():
        if args["only"] and nm not in args["only"]:
            continue
        root = fn()
        meta = dict(biomes=biomes, pivot=NOTES.get(nm, "Origin at ground-contact centre (min y = 0). "
                                                     "Child %s_LOD1 for the LODGroup." % nm))
        e = W.finish_asset(root, "Props", meta, args, icon_views=None,
                           preview_views=PREVIEW_VIEWS, cube_offset="auto")
        e["budget"] = W.BUDGET["Props"]
        entries[nm] = e
    if args["export"]:
        W.update_manifest(entries)


if __name__ == "__main__":
    main()
