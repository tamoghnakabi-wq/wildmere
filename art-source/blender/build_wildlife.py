"""Wildmere wildlife: Deer, Doe, Rabbit, Songbird_Blue, Songbird_Red.

Run headless from the repo root:
  /Applications/Blender.app/Contents/MacOS/Blender -b --factory-startup \
      --python art-source/blender/build_wildlife.py -- [--only Deer,Rabbit] [--no-icons] [--no-preview]

Every animal is authored facing -Y in Blender (=> +Z in Unity) with its LEFT side on +X
(=> -X in Unity).  Parts are separate objects whose origins sit on the joints:
  root empty (ground, between the feet)
    Body -> Neck -> Head -> EarL, EarR      (deer/doe; rabbit has no Neck)
         -> LegFL, LegFR, LegBL, LegBR (pivot at shoulder / hip)
         -> Tail (pivot at tail root), WingL, WingR (birds, pivot at shoulder)
  every part has a "<Part>_LOD1" child (decimated, same pivot) for the LODGroup.
"""
import os
import sys
import math
import numpy as np
from mathutils import Vector, Matrix

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import wm_lib as W
from wm_lib import MB, Part, ring, loft, lerp_path, interp, hexc, mix, sstep

ICON_VIEW = [(0.85, -1.0, 0.42)]
PREVIEW_VIEWS = [(0.85, -1.0, 0.42), (1.0, 0.0, 0.12), (-0.8, 1.0, 0.5), (0.25, -0.55, 1.3)]


def part_name(ctx):
    return ctx.obj.name.replace("_LOD1", "")


def sym(f):
    """Run builder f(side) for side=+1 (animal's LEFT, +X) and -1 (RIGHT)."""
    return f(1), f(-1)


# =========================================================================== DEER / DOE
HEAD_ANCHOR = Vector((0, -0.585, 1.205))   # atlas joint (Head pivot) before scaling


def build_cervid(name, doe=False):
    s = 0.92 if doe else 1.0            # overall scale
    nk = 0.95 if doe else 1.0           # neck girth
    S = lambda x, y, z: Vector((x * s, y * s, z * s))
    H = HEAD_ANCHOR
    SH = lambda dx, dy, dz: S(H.x + dx, H.y + dy, H.z + dz)
    parts = []

    # ---- Body: loft from rump (+Y) to chest (-Y); u = X (width), v = Z
    st = [  # y,    zc,   hw,   top,  bot
        (0.575, 0.880, 0.03, 0.03, 0.03),
        (0.55, 0.880, 0.090, 0.090, 0.105),
        (0.50, 0.876, 0.145, 0.130, 0.160),
        (0.42, 0.872, 0.178, 0.155, 0.185),
        (0.32, 0.864, 0.192, 0.160, 0.195),
        (0.20, 0.850, 0.186, 0.155, 0.195),
        (0.06, 0.840, 0.200, 0.160, 0.225),
        (-0.08, 0.846, 0.206, 0.170, 0.245),
        (-0.22, 0.860, 0.200, 0.185, 0.255),
        (-0.34, 0.868, 0.185, 0.178, 0.250),
        (-0.43, 0.872, 0.160, 0.158, 0.225),
        (-0.50, 0.876, 0.118, 0.120, 0.172),
        (-0.545, 0.880, 0.060, 0.062, 0.092),
    ]
    body = MB()
    path = [S(0, y, zc) for (y, zc, hw, t, b) in st]
    profs = [ring(12, hw * s, t * s, b * s, power=2.25) for (y, zc, hw, t, b) in st]
    loft(body, path, profs, "M_Fur", cap0="point", cap1="point",
         tip0=S(0, 0.59, 0.88), tip1=S(0, -0.56, 0.885))
    parts.append(Part("Body", body, S(0, 0.0, 0.86), smooth=60))

    # ---- Neck: from inside the chest up-forward to the skull (short and strong)
    neck = MB()
    npath = lerp_path([S(0, -0.33, 0.87), S(0, -0.44, 0.99), S(0, -0.53, 1.11),
                       SH(0, -0.03, 0.012)], 6)
    nprof = []
    for i in range(6):
        t = i / 5
        hw = interp([(0, 0.118), (0.45, 0.090), (0.8, 0.068), (1, 0.05)], t) * s * nk
        tp = interp([(0, 0.100), (0.5, 0.072), (0.8, 0.060), (1, 0.045)], t) * s * nk
        bt = interp([(0, 0.150), (0.45, 0.118), (0.8, 0.080), (1, 0.055)], t) * s * nk
        nprof.append(ring(10, hw, tp, bt, power=2.1))
    loft(neck, npath, nprof, "M_Fur", cap0="flat", cap1="flat")
    parts.append(Part("Neck", neck, S(0, -0.36, 0.91), parent="Body", smooth=60))

    # ---- Head (+ antlers, eyes): offsets from the atlas joint
    head = MB()
    hk = 1.08
    hpts = [(0.020, 0.025), (-0.020, 0.050), (-0.070, 0.056), (-0.120, 0.036),
            (-0.170, 0.002), (-0.212, -0.028), (-0.246, -0.050), (-0.264, -0.060)]
    hprof = [(0.046, 0.046, 0.052), (0.068, 0.060, 0.070), (0.075, 0.064, 0.078),
             (0.070, 0.054, 0.072), (0.054, 0.045, 0.060), (0.043, 0.040, 0.049),
             (0.036, 0.034, 0.040), (0.023, 0.023, 0.026)]
    loft(head, [SH(0, dy * hk, dz * hk) for dy, dz in hpts],
         [ring(10, a * s * hk, b * s * hk, c * s * hk, power=2.2) for a, b, c in hprof],
         "M_Fur", cap0="point", cap1="point", tip0=SH(0, 0.036, 0.024),
         tip1=SH(0, -0.29, -0.068))
    for side in (1, -1):
        W.ico(head, SH(side * 0.072, -0.090, 0.060), 0.019 * s, "M_Horn#eye", subdiv=1,
              scale=(0.55, 1.0, 0.8))
    if not doe:
        for side in (1, -1):
            B = SH(side * 0.034, -0.040, 0.092)
            ctrl = [(0, 0, 0), (0.06, 0.020, 0.075), (0.14, 0.040, 0.155), (0.215, 0.020, 0.245),
                    (0.245, -0.050, 0.330), (0.215, -0.135, 0.390)]
            bp = lerp_path([B + Vector((side * x, y, z)) for x, y, z in ctrl], 8)
            rad = [0.030, 0.027, 0.023, 0.021, 0.019, 0.016, 0.013, 0.010]
            W.tube(head, bp, rad, "M_Horn", sides=6, cap0="flat", cap1="point",
                   tip1=bp[-1] + (bp[-1] - bp[-2]).normalized() * 0.035)
            tines = [(1, (0.25 * side, -1.0, 0.45), 0.13), (3, (0.05 * side, -0.35, 1.0), 0.16),
                     (5, (-0.15 * side, -0.15, 1.0), 0.13)]
            for idx, d, ln in tines:
                d = Vector(d).normalized()
                p0 = bp[idx] - d * 0.010
                ctrl2 = [p0, p0 + d * ln * 0.45 + Vector((0, 0, 0.015)), p0 + d * ln]
                tp = lerp_path(ctrl2, 4)
                W.tube(head, tp, [0.015, 0.012, 0.009, 0.005], "M_Horn", sides=5, cap0="flat",
                       cap1="point", tip1=tp[-1] + d * 0.028)
    parts.append(Part("Head", head, SH(0, 0, 0), parent="Neck", smooth=55))

    # ---- Ears
    ek = 1.25 if doe else 1.05
    for side, nm in ((1, "EarL"), (-1, "EarR")):
        ear = MB()
        base = SH(side * 0.052, -0.022, 0.082)
        tipd = Vector((side * 0.85, 0.30, 0.42)).normalized()
        L = 0.135 * ek * s
        epath = [base + tipd * (L * t) + Vector((0, 0, 0.012 * math.sin(t * math.pi)))
                 for t in (0, 0.25, 0.55, 0.85)]
        eprof = [ring(6, w * s * ek, th * s, power=2.0) for w, th in
                 ((0.020, 0.013), (0.040, 0.014), (0.037, 0.011), (0.016, 0.007))]
        loft(ear, epath, eprof, "M_Fur", up_hint=(0, -1, 0), cap0="flat", cap1="point",
             tip1=base + tipd * L * 1.04)
        parts.append(Part(nm, ear, base, parent="Head", smooth=50))

    # ---- Legs (pivot at the shoulder / hip, inside the body)
    def leg(side, front):
        m = MB()
        x0 = side * (0.110 if front else 0.108)
        if front:
            ctrl = [(0.84, -0.30, 0.048, 0.080), (0.71, -0.314, 0.066, 0.094),
                    (0.56, -0.290, 0.052, 0.070), (0.45, -0.312, 0.040, 0.052),
                    (0.35, -0.322, 0.038, 0.047), (0.27, -0.328, 0.027, 0.035),
                    (0.13, -0.334, 0.025, 0.032), (0.078, -0.344, 0.031, 0.037),
                    (0.054, -0.352, 0.032, 0.040), (0.053, -0.352, 0.033, 0.041),
                    (0.0, -0.368, 0.035, 0.052)]
            piv = (x0, -0.31, 0.80)
        else:
            ctrl = [(0.87, 0.33, 0.058, 0.095), (0.74, 0.30, 0.078, 0.120),
                    (0.58, 0.290, 0.058, 0.088), (0.46, 0.355, 0.042, 0.058),
                    (0.37, 0.395, 0.035, 0.045), (0.31, 0.380, 0.027, 0.035),
                    (0.14, 0.368, 0.025, 0.032), (0.080, 0.362, 0.031, 0.037),
                    (0.054, 0.356, 0.032, 0.040), (0.053, 0.356, 0.033, 0.041),
                    (0.0, 0.344, 0.035, 0.052)]
            piv = (x0, 0.33, 0.83)
        path = [S(x0, y, z) for (z, y, a, b) in ctrl]
        profs = [ring(8, a * s, b * s, power=2.0, phase=math.pi / 8) for (z, y, a, b) in ctrl]
        mats = ["M_Fur"] * 9 + ["M_Horn"]
        loft(m, path, profs, mats, up_hint=(0, -1, 0), cap0="point", cap1="flat",
             tip0=path[0] + Vector((0, 0, 0.03 * s)))
        return m, S(*piv)

    for nm, side, front in (("LegFL", 1, True), ("LegFR", -1, True), ("LegBL", 1, False),
                            ("LegBR", -1, False)):
        m, pv = leg(side, front)
        parts.append(Part(nm, m, pv, parent="Body", smooth=55))

    # ---- Tail
    tail = MB()
    tp = lerp_path([S(0, 0.535, 0.945), S(0, 0.595, 0.925), S(0, 0.635, 0.865), S(0, 0.648, 0.80)], 5)
    tprof = [ring(6, w * s, t * s, power=2.0) for w, t in
             ((0.038, 0.027), (0.050, 0.026), (0.046, 0.022), (0.035, 0.017), (0.018, 0.011))]
    loft(tail, tp, tprof, "M_Fur", cap0="flat", cap1="point", tip1=S(0, 0.652, 0.775))
    parts.append(Part("Tail", tail, S(0, 0.54, 0.94), parent="Body", smooth=50))

    paint = cervid_paint(doe, s)
    for p in parts:
        p.paint = paint
    root = W.assemble(name, parts, ground=True, ao={"dist": 0.45 * s, "rays": 48,
                                                    "strength": 1.0},
                      cav=(1.0, 0.35), face_jitter=0.035, seed=3 if doe else 2)
    return root


def cervid_paint(doe, s):
    if doe:
        C = dict(back=hexc("6e4b30"), flank=hexc("9c7450"), belly=hexc("e4d8c0"),
                 cream=hexc("efe7d6"), shin=hexc("5a4030"), muzzle=hexc("4e4038"))
    else:
        C = dict(back=hexc("553823"), flank=hexc("85603f"), belly=hexc("dccdb1"),
                 cream=hexc("ece3d0"), shin=hexc("45311f"), muzzle=hexc("3d3029"))
    C.update(nose=hexc("1a1614"), hoof=hexc("26201c"), ant0=hexc("4a3727"), ant1=hexc("a08867"),
             ant2=hexc("efe6d2"), eye=hexc("100c0a"), ear_in=hexc("cfb09a"),
             tdark=hexc("33241a"), dorsal=hexc("3e2a1b"))
    H = np.array(HEAD_ANCHOR)

    def paint(ctx):
        pn = part_name(ctx)
        P, N = ctx.P / s, ctx.N
        x, y, z = P[:, 0], P[:, 1], P[:, 2]
        nz = N[:, 2]
        streak = ctx.vnoise((12, 12, 40), 3, 5)
        blot = ctx.vnoise((2.5, 2.5, 2.5), 3, 9)
        top = sstep(-0.1, 0.85, nz)
        col = mix(C["flank"], C["back"], top * 0.95)
        col = col * (1 + 0.10 * streak[:, None] + 0.08 * blot[:, None])
        if pn == "Body":
            col = mix(col, C["belly"], sstep(-0.05, -0.55, nz) * sstep(0.88, 0.78, z))
            col = mix(col, C["cream"], sstep(0.42, 0.53, y) * sstep(0.15, 0.6, N[:, 1]) *
                      sstep(0.13, 0.08, np.abs(x)))
            col = mix(col, C["dorsal"], sstep(0.85, 0.98, nz) * 0.6)
        elif pn == "Neck":
            front = sstep(-0.2, -0.75, N[:, 1] - 0.3 * nz)
            col = mix(col, C["cream"], front * sstep(1.02, 1.10, z) * 0.95)
            col = mix(col, C["belly"], front * sstep(1.04, 0.94, z) * 0.55)
            col = mix(col, C["dorsal"], sstep(0.4, 0.9, N[:, 1] + nz) * 0.4)
        elif pn == "Head":
            R = P - H
            ry, rz = R[:, 1] / 1.08, R[:, 2] / 1.08
            col = mix(col, C["muzzle"], sstep(-0.165, -0.19, ry))
            col = mix(col, C["nose"], sstep(-0.235, -0.250, ry) * sstep(-0.075, -0.05, rz))
            col = mix(col, C["cream"], sstep(-0.30, -0.70, nz) * sstep(-0.12, -0.17, ry) * 0.9)
            col = mix(col, C["cream"], sstep(-0.2, -0.6, nz) * sstep(-0.05, -0.09, ry) *
                      sstep(-0.21, -0.17, ry) * 0.6)
            for sd in (1, -1):
                d = np.linalg.norm(R - np.array([sd * 0.072, -0.090, 0.060]), axis=1)
                col = mix(col, C["cream"], sstep(0.036, 0.024, d) * 0.85)
            col = mix(col, C["dorsal"], sstep(0.6, 0.95, nz) * sstep(-0.10, -0.03, ry) * 0.6)
            horn = ctx.is_mat("M_Horn") & (ctx.tag != "eye")
            t = np.clip((rz - 0.09) / 0.42, 0, 1)
            hc = mix(mix(C["ant0"], C["ant1"], sstep(0.0, 0.30, t)), C["ant2"], sstep(0.55, 1.0, t))
            hc = hc * (1 + 0.14 * streak[:, None])
            col = np.where(horn[:, None], hc, col)
            col = np.where((ctx.tag == "eye")[:, None], C["eye"], col)
        elif pn in ("EarL", "EarR"):
            inner = sstep(-0.1, -0.6, N[:, 1])
            col = mix(col, C["ear_in"], inner * 0.85)
            d = np.linalg.norm(ctx.L / s, axis=1)
            col = mix(col, C["tdark"], sstep(0.11, 0.15, d) * 0.85)
        elif pn.startswith("Leg"):
            col = mix(col, C["shin"], sstep(0.48, 0.26, z) * 0.85)
            inner = sstep(-0.2, -0.7, N[:, 0] * np.sign(x))
            col = mix(col, C["belly"], inner * sstep(0.30, 0.55, z) * 0.7)
            col = mix(col, C["cream"] * 0.9, sstep(0.10, 0.078, z) * sstep(0.054, 0.07, z) * 0.6)
            hoof = mix(C["hoof"], C["hoof"] * 1.7, sstep(0.0, 0.054, z))
            col = np.where(ctx.is_mat("M_Horn")[:, None], hoof, col)
        elif pn == "Tail":
            back = sstep(-0.1, 0.5, N[:, 1] + 0.4 * nz)
            col = mix(C["cream"], mix(C["back"], C["tdark"], sstep(0.88, 0.82, z)), back)
        return col
    return paint


# =========================================================================== RABBIT
def build_rabbit(name="Rabbit"):
    parts = []
    body = MB()
    st = [  # y, zc, hw, top, bot
        (0.145, 0.105, 0.02, 0.02, 0.02),
        (0.135, 0.108, 0.055, 0.055, 0.055),
        (0.110, 0.112, 0.078, 0.078, 0.072),
        (0.075, 0.112, 0.085, 0.082, 0.078),
        (0.035, 0.108, 0.078, 0.074, 0.070),
        (-0.010, 0.110, 0.066, 0.064, 0.060),
        (-0.050, 0.118, 0.058, 0.056, 0.056),
        (-0.080, 0.125, 0.048, 0.048, 0.050),
        (-0.100, 0.130, 0.030, 0.030, 0.034),
    ]
    loft(body, [Vector((0, y, z)) for y, z, *_ in st],
         [ring(12, a, b, c, power=2.0) for _, _, a, b, c in st], "M_Fur",
         cap0="point", cap1="point", tip0=Vector((0, 0.152, 0.105)), tip1=Vector((0, -0.108, 0.132)))
    parts.append(Part("Body", body, (0, 0.03, 0.11), smooth=70))

    head = MB()
    hp = [(-0.080, 0.170), (-0.100, 0.183), (-0.125, 0.190), (-0.150, 0.186), (-0.172, 0.176),
          (-0.186, 0.166)]
    hr = [(0.028, 0.03, 0.03), (0.040, 0.040, 0.038), (0.043, 0.042, 0.040), (0.036, 0.035, 0.034),
          (0.025, 0.026, 0.025), (0.013, 0.014, 0.014)]
    loft(head, [Vector((0, y, z)) for y, z in hp], [ring(10, a, b, c) for a, b, c in hr], "M_Fur",
         cap0="point", cap1="point", tip0=Vector((0, -0.072, 0.168)), tip1=Vector((0, -0.192, 0.163)))
    for side in (1, -1):
        W.ico(head, (side * 0.033, -0.138, 0.198), 0.0105, "M_Horn#eye", subdiv=1, scale=(0.6, 1, 1))
    parts.append(Part("Head", head, (0, -0.085, 0.165), parent="Body", smooth=70))

    for side, nm in ((1, "EarL"), (-1, "EarR")):
        ear = MB()
        base = Vector((side * 0.017, -0.118, 0.218))
        d = Vector((side * 0.22, 0.42, 1.0)).normalized()
        ep = [base + d * (0.105 * t) + Vector((0, 0.012 * math.sin(t * math.pi), 0))
              for t in (0, 0.2, 0.5, 0.8, 0.95)]
        eprof = [ring(6, w, th) for w, th in
                 ((0.010, 0.008), (0.018, 0.008), (0.021, 0.0075), (0.017, 0.006), (0.009, 0.004))]
        loft(ear, ep, eprof, "M_Fur", up_hint=(0, -1, 0), cap0="flat", cap1="point",
             tip1=base + d * 0.112)
        parts.append(Part(nm, ear, base, parent="Head", smooth=55))

    for side in (1, -1):
        nm = "LegFL" if side > 0 else "LegFR"
        m = MB()
        ctrl = [(0.085, -0.060, 0.016, 0.020), (0.055, -0.072, 0.014, 0.016),
                (0.025, -0.080, 0.011, 0.013), (0.008, -0.088, 0.012, 0.016),
                (0.0, -0.100, 0.012, 0.012)]
        loft(m, [Vector((side * 0.034, y, z)) for z, y, a, b in ctrl],
             [ring(6, a, b) for z, y, a, b in ctrl], "M_Fur", up_hint=(0, -1, 0),
             cap0="flat", cap1="point", tip1=Vector((side * 0.034, -0.108, 0.004)))
        parts.append(Part(nm, m, (side * 0.034, -0.062, 0.08), parent="Body", smooth=55))
    for side in (1, -1):
        nm = "LegBL" if side > 0 else "LegBR"
        m = MB()
        # haunch (thigh) blob
        W.ico(m, (side * 0.058, 0.072, 0.082), 0.048, "M_Fur", subdiv=2, scale=(0.62, 1.05, 0.95))
        # long hind foot along the ground, toes forward
        fp = [Vector((side * 0.06, y, z)) for y, z in
              ((0.108, 0.030), (0.088, 0.017), (0.050, 0.012), (0.012, 0.010), (-0.004, 0.008))]
        loft(m, fp, [ring(6, a, b, power=2.2) for a, b in
                     ((0.017, 0.022), (0.020, 0.018), (0.019, 0.013), (0.016, 0.011), (0.011, 0.008))],
             "M_Fur", up_hint=(0, 0, 1), cap0="flat", cap1="point",
             tip1=Vector((side * 0.06, -0.016, 0.006)))
        parts.append(Part(nm, m, (side * 0.055, 0.07, 0.085), parent="Body", smooth=60))

    tail = MB()
    W.ico(tail, (0, 0.155, 0.120), 0.024, "M_Fur", subdiv=1, scale=(1.0, 0.85, 0.95), jitter=0.12,
          seed=4)
    parts.append(Part("Tail", tail, (0, 0.145, 0.118), parent="Body", smooth=60))

    C = dict(back=hexc("6f6152"), flank=hexc("98856d"), belly=hexc("ece4d6"),
             cream=hexc("f4efe6"), dark=hexc("3a3028"), ear_in=hexc("cfa494"), nose=hexc("8a5b55"),
             eye=hexc("140f0d"), rufous=hexc("a5734d"))

    def paint(ctx):
        pn = part_name(ctx)
        P, N = ctx.P, ctx.N
        x, y, z = P[:, 0], P[:, 1], P[:, 2]
        nz = N[:, 2]
        tick = ctx.vnoise((60, 60, 60), 2, 3)   # agouti ticking
        blot = ctx.vnoise((12, 12, 12), 3, 7)
        col = mix(C["flank"], C["back"], sstep(0.0, 0.9, nz))
        col = col * (1 + 0.12 * tick[:, None] + 0.08 * blot[:, None])
        col = mix(col, C["belly"], sstep(-0.1, -0.6, nz) * 0.95)
        if pn == "Head":
            col = mix(col, C["rufous"], sstep(0.4, 0.9, nz) * sstep(-0.10, -0.13, y) * 0.5)
            col = mix(col, C["belly"], sstep(-0.165, -0.18, y) * sstep(0.17, 0.165, z) * 0.6)
            col = mix(col, C["nose"], sstep(-0.183, -0.19, y) * sstep(0.162, 0.170, z))
            for sd in (1, -1):
                d = np.linalg.norm(P - np.array([sd * 0.033, -0.138, 0.198]), axis=1)
                col = mix(col, C["cream"], sstep(0.019, 0.012, d) * 0.7)
            col = np.where((ctx.tag == "eye")[:, None], C["eye"], col)
        elif pn in ("EarL", "EarR"):
            col = mix(col, C["ear_in"], sstep(-0.1, -0.6, N[:, 1]) * 0.85)
            d = np.linalg.norm(ctx.L, axis=1)
            col = mix(col, C["dark"], sstep(0.085, 0.105, d) * 0.9)
        elif pn == "Tail":
            col = mix(C["cream"], C["back"], sstep(0.3, 0.9, nz) * 0.5)
        elif pn.startswith("LegB"):
            col = mix(col, C["belly"], sstep(0.03, 0.012, z) * 0.8)
        elif pn.startswith("LegF"):
            col = mix(col, C["belly"], sstep(0.04, 0.01, z) * 0.7)
        return col

    for p in parts:
        p.paint = paint
    return W.assemble(name, parts, ground=True, ao={"dist": 0.08, "rays": 40}, cav=(0.8, 0.25),
                      face_jitter=0.03, seed=4)


# =========================================================================== SONGBIRD
def build_songbird(name, variant):
    parts = []
    body = MB()
    # body egg, tilted (chest forward/up), y forward = -Y
    st = [  # y, zc, hw, top, bot
        (0.050, 0.060, 0.008, 0.006, 0.008),
        (0.040, 0.058, 0.020, 0.016, 0.020),
        (0.020, 0.054, 0.029, 0.025, 0.031),
        (-0.005, 0.054, 0.031, 0.029, 0.033),
        (-0.028, 0.060, 0.027, 0.027, 0.030),
        (-0.040, 0.068, 0.018, 0.020, 0.020),
    ]
    loft(body, [Vector((0, y, z)) for y, z, *_ in st],
         [ring(10, a, b, c) for _, _, a, b, c in st], "M_Fur", cap0="point", cap1="point",
         tip0=Vector((0, 0.056, 0.062)), tip1=Vector((0, -0.044, 0.074)))
    for side in (1, -1):  # little legs + toes (part of Body)
        lp = [Vector((side * 0.010, 0.004, 0.032)), Vector((side * 0.011, 0.002, 0.012)),
              Vector((side * 0.011, 0.000, 0.002))]
        W.tube(body, lp, [0.0032, 0.0024, 0.0024], "M_Horn#beak", sides=4, cap0="flat", cap1="flat")
        for dx, dy in ((0.0, -0.016), (0.008, -0.010), (-0.008, -0.010), (0.0, 0.010)):
            tp = [Vector((side * 0.011, 0.0, 0.002)), Vector((side * 0.011 + dx, dy, 0.0015))]
            W.tube(body, tp, [0.0018, 0.0012], "M_Horn#beak", sides=3, cap0="flat", cap1="point",
                   up_hint=(0, 0, 1))
    parts.append(Part("Body", body, (0, 0.0, 0.055), smooth=70))

    head = MB()
    hp = [(-0.030, 0.082), (-0.036, 0.092), (-0.048, 0.098), (-0.060, 0.096), (-0.068, 0.090)]
    hr = [(0.014, 0.014, 0.014), (0.021, 0.021, 0.020), (0.022, 0.021, 0.020), (0.017, 0.016, 0.016),
          (0.009, 0.009, 0.009)]
    loft(head, [Vector((0, y, z)) for y, z in hp], [ring(10, a, b, c) for a, b, c in hr],
         "M_Fur", cap0="point", cap1="flat", tip0=Vector((0, -0.024, 0.080)))
    bp = [Vector((0, -0.066, 0.089)), Vector((0, -0.074, 0.088)), Vector((0, -0.082, 0.086))]
    W.tube(head, bp, [0.0065, 0.0045, 0.0025], "M_Horn#beak", sides=5, cap0="flat", cap1="point",
           tip1=Vector((0, -0.088, 0.0845)), flat=0.8)
    for side in (1, -1):
        W.ico(head, (side * 0.0165, -0.054, 0.099), 0.0045, "M_Horn#eye", subdiv=1, scale=(0.6, 1, 1))
    parts.append(Part("Head", head, (0, -0.032, 0.080), parent="Body", smooth=60))

    for side, nm in ((1, "WingL"), (-1, "WingR")):
        w = MB()
        sh = Vector((side * 0.024, -0.020, 0.071))
        ctrl = [sh, Vector((side * 0.031, 0.010, 0.067)), Vector((side * 0.025, 0.040, 0.062)),
                Vector((side * 0.012, 0.068, 0.060))]
        wp = lerp_path(ctrl, 5)
        prof = [ring(6, a, b, power=1.6, phase=0) for a, b in
                ((0.012, 0.006), (0.020, 0.005), (0.017, 0.004), (0.010, 0.003), (0.005, 0.002))]
        # u = across the wing (vertical-ish), v = thickness (sideways)
        loft(w, wp, prof, "M_Fur", up_hint=(side, 0, 0.75), cap0="flat", cap1="point",
             tip1=wp[-1] + Vector((-side * 0.003, 0.012, -0.001)))
        parts.append(Part(nm, w, sh, parent="Body", smooth=50))

    tail = MB()
    t0 = Vector((0, 0.040, 0.058))
    tpth = lerp_path([t0, t0 + Vector((0, 0.025, 0.004)), t0 + Vector((0, 0.048, 0.010))], 4)
    loft(tail, tpth, [ring(6, a, b, power=1.5) for a, b in
                      ((0.008, 0.004), (0.012, 0.003), (0.015, 0.0028), (0.017, 0.0025))],
         "M_Fur", up_hint=(0, 0, 1), cap0="flat", cap1="flat")
    parts.append(Part("Tail", tail, t0, parent="Body", smooth=40))

    if variant == "Blue":
        C = dict(top=hexc("234c92"), hi=hexc("4479c4"), belly=hexc("ece4d4"),
                 breast=hexc("d0874f"), wing=hexc("244a8c"), bar=hexc("e9eef5"),
                 mask=hexc("1d2433"), beak=hexc("2b2622"), leg=hexc("3a3029"), tail=hexc("1f3f78"))
    else:
        C = dict(top=hexc("9e1f1b"), hi=hexc("cf3a2e"), belly=hexc("e3c4ae"),
                 breast=hexc("cf3a2c"), wing=hexc("6e1f1d"), bar=hexc("d9cfc4"),
                 mask=hexc("1b1414"), beak=hexc("e39a3c"), leg=hexc("5a4036"), tail=hexc("5a1a19"))
    eye = hexc("0b0909")

    def paint(ctx):
        pn = part_name(ctx)
        P, N = ctx.P, ctx.N
        x, y, z = P[:, 0], P[:, 1], P[:, 2]
        nz = N[:, 2]
        fl = ctx.vnoise((140, 140, 140), 2, 11)
        col = mix(C["top"], C["hi"], sstep(0.4, 1.0, nz) * 0.6)
        col = mix(col, C["belly"], sstep(-0.1, -0.7, nz))
        if pn == "Body":
            col = mix(col, C["breast"], sstep(-0.2, -0.75, N[:, 1]) * sstep(0.035, 0.06, z) * 0.95)
            col = np.where((ctx.tag == "beak")[:, None], C["leg"], col)
        elif pn == "Head":
            col = mix(col, C["breast"], sstep(-0.3, -0.8, nz) * 0.6)
            d = np.minimum(np.linalg.norm(P - np.array([0.0165, -0.054, 0.099]), axis=1),
                           np.linalg.norm(P - np.array([-0.0165, -0.054, 0.099]), axis=1))
            col = mix(col, C["mask"], sstep(-0.050, -0.062, y) * sstep(0.2, 0.6, np.abs(N[:, 0])) * 0.8)
            col = mix(col, C["mask"], sstep(0.010, 0.006, d) * 0.9)
            col = np.where((ctx.tag == "beak")[:, None], C["beak"], col)
            col = np.where((ctx.tag == "eye")[:, None], eye, col)
        elif pn in ("WingL", "WingR"):
            d = np.linalg.norm(ctx.L, axis=1)
            col = mix(C["wing"], C["top"], sstep(0.04, 0.0, d) * 0.6)
            bar = sstep(0.026, 0.030, d) * sstep(0.040, 0.036, d)
            col = mix(col, C["bar"], bar * 0.7)
            col = mix(col, C["wing"] * 0.55, sstep(0.06, 0.085, d))
        elif pn == "Tail":
            d = np.linalg.norm(ctx.L, axis=1)
            col = mix(C["tail"], C["tail"] * 0.6, sstep(0.03, 0.06, d))
        return col * (1 + 0.06 * fl[:, None])

    for p in parts:
        p.paint = paint
    return W.assemble(name, parts, ground=True, ao={"dist": 0.03, "rays": 32}, cav=(0.6, 0.2),
                      face_jitter=0.03, seed=6 if variant == "Blue" else 7, lod_ratio=0.45)


# =========================================================================== main
ASSETS = {
    "Deer": (lambda: build_cervid("Deer"), dict(
        biomes=["Forest", "BirchForest", "Plains", "Taiga"],
        pivot="Root at ground between the hooves, facing +Z. Parts pivot at joints: Body (torso centre), "
              "Neck (base, inside chest), Head (atlas joint; antlers are part of Head), EarL/EarR "
              "(ear base), LegFL/FR (shoulder), LegBL/BR (hip), Tail (tail root). Rotate parts about "
              "local X to swing legs/nod head.", species="stag, antlered")),
    "Doe": (lambda: build_cervid("Doe", doe=True), dict(
        biomes=["Forest", "BirchForest", "Plains", "Taiga"],
        pivot="Same rig as Deer at 92% scale, no antlers, larger ears.", species="doe")),
    "Rabbit": (build_rabbit, dict(
        biomes=["Plains", "Forest", "Meadow", "Snowy"],
        pivot="Root at ground between the feet, facing +Z. Body (hips), Head (neck joint), EarL/EarR "
              "(ear base, rotate about local X/Z to flop), LegF* (shoulder), LegB* (hip, includes "
              "haunch + long foot), Tail (tail root). No Neck part.", species="rabbit")),
    "Songbird_Blue": (lambda: build_songbird("Songbird_Blue", "Blue"), dict(
        biomes=["Forest", "BirchForest", "Plains", "Riverbank"],
        pivot="Root at ground between the feet (legs are part of Body), facing +Z. Head (neck joint), "
              "WingL/WingR (shoulder; rotate about local Z to flap), Tail (rump).", species="songbird")),
    "Songbird_Red": (lambda: build_songbird("Songbird_Red", "Red"), dict(
        biomes=["Forest", "DarkForest", "Taiga", "Snowy"],
        pivot="Same rig as Songbird_Blue; crimson colour variant (vertex colours only).",
        species="songbird")),
}


def main():
    args = W.parse_args()
    entries = {}
    for nm, (fn, meta) in ASSETS.items():
        if args["only"] and nm not in args["only"]:
            continue
        root = fn()
        e = W.finish_asset(root, "Wildlife", meta, args, icon_views=ICON_VIEW,
                           preview_views=PREVIEW_VIEWS, cube_offset=(0.6, 0.6))
        e["budget"] = W.BUDGET["Bird" if nm.startswith("Songbird") else "Wildlife"]
        entries[nm] = e
    if args["export"]:
        W.update_manifest(entries)


if __name__ == "__main__":
    main()
