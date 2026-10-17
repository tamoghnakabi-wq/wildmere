"""Foliage and cross-quad plants: leaves (tileable cutout), grass, fern, flowers,
dead bush, mushrooms, glowcap and the torch."""
import numpy as np

from texlib import (TILE, Canvas, blur_clamped, col, cov_from_sdf, fbm, finish, grey, local_uv,
                    mix3, poisson, polyline_capsules, ramp, smoothstep)

T = TILE


# ---------------------------------------------------------------- shapes ---
def leaf_sdf(X, Y, bx, by, ang, L, W, shape='oval', serr=0.0, ph=0.0):
    """Leaf outline. (bx,by) = petiole base, ang = direction to tip.
    Returns signed distance (px), t (0 base .. 1 tip), vn (-1..1 across)."""
    u, v = local_uv(X, Y, bx, by, ang)
    t = u / L
    tc = np.clip(t, 0, 1)
    if shape == 'oval':
        w = W * np.sin(np.pi * tc) ** 0.7
    elif shape == 'lobed':
        w = W * np.sin(np.pi * tc) ** 0.65 * (0.82 + 0.18 * np.cos(tc * 5 * 2 * np.pi + ph))
    elif shape == 'ovate':  # broad base, long point
        w = W * 1.25 * np.sin(np.pi * tc ** 0.75) ** 0.8 * (1 - 0.15 * tc)
    elif shape == 'lance':
        w = W * np.sin(np.pi * tc) ** 1.1
    elif shape == 'petal':
        w = W * np.sin(np.pi * tc ** 1.4) ** 0.6
    else:
        w = W * np.sin(np.pi * tc)
    if serr:
        w = w * (1 + serr * np.sin(tc * 38 + ph))
    d = np.abs(v) - w
    d = np.maximum(d, np.maximum(-u, u - L))
    vn = v / np.maximum(w, 1e-3)
    return d, t, np.clip(vn, -1, 1)


def _leaf_paint(cv, cx, cy, ang, L, W, shape, lum, z, serr=0.0, ph=0.0, tint=1.0, colour=None,
                smooth=0.32, emis=0.0):
    r = L + 2
    X, Y, idx = cv.region(cx + np.cos(ang) * L / 2, cy + np.sin(ang) * L / 2, r / 2 + W + 2)
    d, t, vn = leaf_sdf(X, Y, cx, cy, ang, L, W, shape, serr, ph)
    cov = cov_from_sdf(d)
    if not np.any(cov > 0):
        return
    midrib = np.exp(-(vn / 0.12) ** 2) * (t > 0.05) * (t < 0.9)
    side = np.where(vn > 0, 0.93, 1.03)
    shade = side * (0.9 + 0.12 * np.clip(t, 0, 1)) * (1 - 0.1 * vn * vn) + 0.07 * midrib
    dome = np.sqrt(np.clip(1 - vn * vn, 0, 1)) * np.sin(np.pi * np.clip(t, 0, 1)) ** 0.5
    if colour is None:
        c = grey(lum * shade)
    else:
        c = np.asarray(colour)[None, None, :] * shade[..., None]
    kw = dict(c=c, h=3.0 * z + 0.8 * dome - 0.25 * midrib, t=tint, s=smooth)
    if 'e' in cv.ch:
        kw['e'] = emis
    cv.paint(idx, cov, **kw)


def _twig(cv, pts, w0, w1, c0, z):
    polyline_capsules(cv, pts, w0, w1, lambda tg, dn, X, Y: dict(
        c=np.asarray(c0)[None, None, :] * (1.0 - 0.25 * dn * dn)[..., None],
        h=3.0 * z + 0.5 * np.sqrt(np.clip(1 - dn * dn, 0, 1)), t=0.0, s=0.25))


def normalize_tint(L, target=0.58, cap=0.93):
    """Scale tint-masked texels (in linear space) so their mean equals `target`.

    The runtime multiplies tinted texels by biome_tint * 1.7, so a linear mean of
    ~0.58 reproduces the biome colour on average (contract from the lead)."""
    from texlib import lin_to_srgb, srgb_to_lin
    w = L.maska * L.alpha
    if w.sum() < 1:
        return L
    lin = srgb_to_lin(L.albedo)
    m = (lin.mean(axis=-1) * w).sum() / w.sum()
    k = target / max(m, 1e-4)
    scaled = lin * (1 + (k - 1) * L.maska)[..., None]
    L.albedo = np.minimum(lin_to_srgb(scaled), cap)
    return L


# ---------------------------------------------------------------- leaves ---
def _leaves(seed, shape, cluster_r, per, L_rng, W_ratio, z_lum=(0.5, 1.0), serr=0.0,
            twig='#5a4632', target=0.55, smooth=0.32, needles=False):
    n = T
    ss = 2
    cv = Canvas(n, ss=ss, wrap=True, c=(0, 0, 0), h=0.0, t=1.0, s=0.3)
    rng = np.random.default_rng(seed)
    centres = poisson(n * ss, cluster_r * ss, seed + 1)
    items = []
    for (cx, cy) in centres:
        zc = rng.random()
        a0 = rng.uniform(0, 2 * np.pi)
        Lt = rng.uniform(7, 13) * ss
        dv = np.array([np.cos(a0), np.sin(a0)])
        p0 = np.array([cx, cy]) - dv * Lt * 0.5
        p1 = np.array([cx, cy]) + dv * Lt * 0.5
        items.append(('twig', zc - 0.03, p0, p1))
        k = int(rng.integers(per[0], per[1] + 1))
        for j in range(k):
            f = 0.15 + 0.85 * j / max(k - 1, 1)
            side = 1 if j % 2 else -1
            pos = p0 + (p1 - p0) * f
            if j == k - 1:
                a = a0 + rng.normal(0, 0.15)
                pos = p1
            else:
                a = a0 + side * rng.uniform(0.55, 1.15)
            z = np.clip(zc + rng.normal(0, 0.07) + 0.04 * f, 0, 1)
            items.append(('leaf', z, pos[0], pos[1], a))
    items.sort(key=lambda it: it[1])
    for it in items:
        if it[0] == 'twig':
            _, z, p0, p1 = it
            mid = (p0 + p1) / 2 + rng.normal(0, 1.0, 2) * ss
            _twig(cv, [p0, mid, p1], 0.85 * ss, 0.5 * ss, col(twig) * rng.uniform(0.85, 1.1), z)
            continue
        _, z, cx, cy, a = it
        lum = z_lum[0] + (z_lum[1] - z_lum[0]) * z + rng.normal(0, 0.04)
        if needles:
            # a needle sprig: twig with long thin needles on both sides
            L = rng.uniform(13, 20) * ss
            b = np.array([cx, cy])
            e = b + np.array([np.cos(a), np.sin(a)]) * L
            _twig(cv, [b, e], 0.65 * ss, 0.4 * ss, col(twig), z)
            m = int(L / (1.0 * ss))
            for q in range(1, m):
                f = q / m
                p = b + (e - b) * f
                for sgn in (-1, 1):
                    na = a + sgn * rng.uniform(0.6, 0.95)
                    nl = (7.0 - 2.5 * f) * ss * rng.uniform(0.85, 1.1)
                    pe = p + np.array([np.cos(na), np.sin(na)]) * nl
                    nlum = lum * (0.88 + 0.22 * f) * rng.uniform(0.9, 1.08)
                    polyline_capsules(cv, [p, pe], 0.42 * ss, 0.22 * ss, lambda tg, dn, X, Y, nlum=nlum, z=z: dict(
                        c=grey(nlum * (1.0 - 0.15 * dn * dn) * (0.9 + 0.15 * tg)),
                        h=3.0 * z + 0.35 * np.sqrt(np.clip(1 - dn * dn, 0, 1)), t=1.0, s=smooth))
            continue
        L = rng.uniform(*L_rng) * ss
        W = L * W_ratio * rng.uniform(0.85, 1.15)
        _leaf_paint(cv, cx, cy, a, L, W, shape, lum, z, serr=serr, ph=rng.uniform(0, 6.3), smooth=smooth)
    A, o = cv.resolve()
    L_ = finish(o['c'], o['h'], o['s'], alpha=A, tint=o['t'], bevel=False, nstrength=0.9,
                cavity=0.12, cav_r=2.0, cube=True)
    return normalize_tint(L_, target)


def leaves_oak():
    return _leaves(301, 'lobed', 10.0, (5, 7), (8.5, 12.5), 0.36, z_lum=(0.42, 1.0))


def leaves_birch():
    return _leaves(311, 'ovate', 10.0, (4, 6), (6.5, 9.0), 0.33, z_lum=(0.5, 1.0), serr=0.06,
                   twig='#6b5f55', target=0.57)


def leaves_spruce():
    return _leaves(321, 'lance', 6.6, (1, 1), (6, 8), 0.2, z_lum=(0.38, 0.95), twig='#3a2e22',
                   needles=True, target=0.52, smooth=0.36)


def leaves_dark():
    return _leaves(331, 'oval', 11.0, (4, 6), (11.0, 15.5), 0.38, z_lum=(0.36, 0.95), twig='#3e2f24',
                   target=0.52)


# ---------------------------------------------------------- cross plants ---
SS = 4


def _plant_canvas(emissive=False):
    ch = dict(c=(0, 0, 0), h=0.0, t=0.0, s=0.3)
    if emissive:
        ch['e'] = 0.0
    return Canvas(T, ss=SS, wrap=False, **ch)


def _finish_plant(cv, nstrength=0.8, target=None):
    A, o = cv.resolve()
    L = finish(o['c'], o['h'], o['s'], alpha=A, tint=o['t'], emis=o.get('e'), bevel=False,
               nstrength=nstrength, cavity=0.06, cav_r=1.5, cube=False)
    if target is not None:
        normalize_tint(L, target)
    return L


def _curve(p0, p1, p2, m=10):
    p0, p1, p2 = (np.asarray(p, float) for p in (p0, p1, p2))
    return [(1 - t) ** 2 * p0 + 2 * (1 - t) * t * p1 + t * t * p2 for t in np.linspace(0, 1, m)]


def _stem(cv, pts, w0, w1, c0, tint=0.0, z=0.5, e=None):
    def vf(tg, dn, X, Y):
        d = dict(c=np.asarray(c0)[None, None, :] * (1.04 - 0.22 * dn * dn - 0.06 * tg)[..., None],
                 h=2.0 * z + 0.6 * np.sqrt(np.clip(1 - dn * dn, 0, 1)), t=tint, s=0.3)
        if e is not None:
            d['e'] = e
        return d
    polyline_capsules(cv, pts, w0, w1, vf)


def tall_grass():
    cv = _plant_canvas()
    rng = np.random.default_rng(401)
    N = T * SS
    blades = []
    for i in range(26):
        x0 = rng.normal(64, 15) * SS
        x0 = np.clip(x0, 26 * SS, 102 * SS)
        hgt = rng.uniform(52, 112) * SS
        lean = rng.normal(0, 0.28) + (x0 / SS - 64) / 120
        z = rng.random()
        blades.append((z, x0, hgt, lean))
    blades.sort()
    for z, x0, hgt, lean in blades:
        p0 = (x0, N + 2 * SS)
        tip = (x0 + np.sin(lean) * hgt * 1.0 + rng.normal(0, 4) * SS, N - hgt)
        mid = (x0 + np.sin(lean) * hgt * 0.25, N - hgt * 0.55)
        lum = 0.5 + 0.35 * z
        w0 = rng.uniform(1.9, 2.8) * SS

        def vf(tg, dn, X, Y, lum=lum, z=z):
            l = lum * (0.72 + 0.4 * tg) * (1 - 0.15 * dn * dn)
            return dict(c=grey(l), h=2.0 * z + 0.5 * np.sqrt(np.clip(1 - dn * dn, 0, 1)), t=1.0, s=0.3)
        polyline_capsules(cv, _curve(p0, mid, tip, 12), w0, 0.25 * SS, vf)
    return _finish_plant(cv, target=0.58)


def fern():
    cv = _plant_canvas()
    rng = np.random.default_rng(411)
    N = T * SS
    base = np.array([64.0 * SS, N - 3 * SS])
    fronds = []
    for i in range(7):
        a = -np.pi / 2 + (i - 3) * 0.36 + rng.normal(0, 0.08)
        L = rng.uniform(62, 98) * SS * (1 - 0.18 * abs(i - 3) / 3)
        fronds.append((rng.random(), a, L))
    fronds.sort()
    for z, a, L in fronds:
        droop = np.sign(np.cos(a)) * 0.55 if abs(np.cos(a)) > 0.15 else rng.normal(0, 0.2)
        p2 = base + np.array([np.cos(a + droop * 0.9), np.sin(a + droop * 0.9)]) * L
        p1 = base + np.array([np.cos(a), np.sin(a)]) * L * 0.6
        pts = _curve(base, p1, p2, 24)
        lum = 0.5 + 0.35 * z
        _stem(cv, pts, 1.3 * SS, 0.4 * SS, grey(lum * 0.85), tint=1.0, z=z)
        for q in range(2, len(pts) - 1):
            f = q / (len(pts) - 1)
            p = pts[q]
            dvec = pts[q + 1] - pts[q - 1]
            ang = np.arctan2(dvec[1], dvec[0])
            for sgn in (-1, 1):
                pl = (17 * (1 - f) ** 0.8 * min(1.0, f * 4) + 3) * SS
                pa = ang + sgn * 1.05
                _leaf_paint(cv, p[0], p[1], pa, pl, pl * 0.24, 'lance', lum * (0.85 + 0.2 * f), z + 0.01,
                            serr=0.12, ph=q, smooth=0.3)
    return _finish_plant(cv, target=0.58)


def _flower_stem_and_leaves(cv, rng, top, base_x=64, leaf_pairs=2, green='#4e7a2e'):
    N = T * SS
    p0 = np.array([base_x * SS, N + SS])
    p2 = np.asarray(top, float)
    p1 = (p0 + p2) / 2 + np.array([rng.normal(0, 4) * SS, 0])
    pts = _curve(p0, p1, p2, 14)
    _stem(cv, pts, 1.6 * SS, 1.1 * SS, col(green), z=0.4)
    for k in range(leaf_pairs):
        q = pts[3 + k * 3]
        for sgn in (-1, 1):
            if k > 0 and rng.random() < 0.4:
                continue
            a = -np.pi / 2 + sgn * rng.uniform(0.7, 1.15)
            L = rng.uniform(16, 26) * SS
            _leaf_paint(cv, q[0], q[1], a, L, L * 0.22, 'lance', 0, 0.45,
                        colour=col(green) * rng.uniform(0.9, 1.15), tint=0.0)
    return pts


def _petal_ring(cv, centre, count, L, W, colour_fn, z, shape='petal', tilt=1.0, rot=0.0, rng=None):
    cx, cy = centre
    order = sorted(range(count), key=lambda i: np.sin(rot + i * 2 * np.pi / count))
    for i in order:
        a = rot + i * 2 * np.pi / count + (rng.normal(0, 0.06) if rng is not None else 0)
        # foreshorten vertically for a tilted flower head
        ex, ey = np.cos(a), np.sin(a) * tilt
        ang = np.arctan2(ey, ex)
        Li = L * np.hypot(ex, ey)
        _leaf_paint(cv, cx, cy, ang, Li, W, shape, 0, z + 0.02 * np.sin(a), colour=colour_fn(i, a), tint=0.0,
                    smooth=0.35)


def _disc(cv, cx, cy, r, c0, c1, z, ry=None, e=None, smooth=0.3, h=1.0):
    ry = r if ry is None else ry
    X, Y, idx = cv.region(cx, cy, r + 2, ry + 2)
    k = np.sqrt(((X - cx) / r) ** 2 + ((Y - cy) / ry) ** 2)
    d = (k - 1) * min(r, ry)
    dome = np.sqrt(np.clip(1 - k * k, 0, 1))
    c = mix3(col(c1), col(c0), dome)
    kw = dict(c=c, h=2.0 * z + h * dome, t=0.0, s=smooth)
    if e is not None:
        kw['e'] = e
    cv.paint(idx, cov_from_sdf(d), **kw)


def flower_red():
    """Cup-shaped six-petal bloom, deep red with a dark eye."""
    cv = _plant_canvas()
    rng = np.random.default_rng(421)
    top = np.array([66 * SS, 34 * SS])
    _flower_stem_and_leaves(cv, rng, top)
    reds = [col('#a3221d'), col('#c5332a'), col('#d8473a')]
    _petal_ring(cv, top, 6, 22 * SS, 12 * SS, lambda i, a: reds[i % 3] * (0.85 + 0.2 * (np.sin(a) * 0.5 + 0.5)),
                0.6, tilt=0.62, rot=0.3, rng=rng)
    _disc(cv, top[0], top[1] - 1 * SS, 6.5 * SS, '#3a2a28', '#231a19', 0.75, ry=4.6 * SS)
    for i in range(10):
        a = i * 2 * np.pi / 10
        _disc(cv, top[0] + np.cos(a) * 5 * SS, top[1] - 1 * SS + np.sin(a) * 3.4 * SS, 1.1 * SS, '#e2c25a', '#a9862e', 0.8)
    return _finish_plant(cv)


def flower_yellow():
    """Ray flower (many narrow petals) with an amber disc, plus a bud."""
    cv = _plant_canvas()
    rng = np.random.default_rng(431)
    top = np.array([60 * SS, 32 * SS])
    pts = _flower_stem_and_leaves(cv, rng, top, base_x=62)
    # a bud on a side stalk
    q = pts[7]
    bud = q + np.array([22 * SS, -28 * SS])
    _stem(cv, _curve(q, q + np.array([14 * SS, -6 * SS]), bud, 8), 1.0 * SS, 0.8 * SS, col('#4e7a2e'), z=0.35)
    _disc(cv, bud[0], bud[1], 4.2 * SS, '#e8b93a', '#b98420', 0.4, ry=5.4 * SS)
    yel = lambda i, a: col('#f0c23c') * (0.86 + 0.18 * (i % 2)) * (0.92 + 0.1 * np.sin(a))
    _petal_ring(cv, top, 16, 24 * SS, 3.4 * SS, yel, 0.6, shape='lance', tilt=0.7, rot=0.1, rng=rng)
    _disc(cv, top[0], top[1], 8 * SS, '#c47a22', '#8a4f16', 0.8, ry=6.2 * SS, h=2.0)
    return _finish_plant(cv)


def flower_blue():
    """Arching spike of nodding bells."""
    cv = _plant_canvas()
    rng = np.random.default_rng(441)
    N = T * SS
    base = np.array([58 * SS, N + SS])
    tip = np.array([84 * SS, 16 * SS])
    pts = _curve(base, np.array([56 * SS, 40 * SS]), tip, 20)
    for k in range(3):
        for sgn in (-1, 1):
            a = -np.pi / 2 + sgn * rng.uniform(0.6, 1.0)
            L = rng.uniform(18, 28) * SS
            _leaf_paint(cv, base[0], N - 2 * SS, a, L, L * 0.2, 'lance', 0, 0.3,
                        colour=col('#4a7430') * rng.uniform(0.9, 1.15), tint=0.0)
    _stem(cv, pts, 1.4 * SS, 0.8 * SS, col('#4e7a2e'), z=0.4)
    blues = [col('#3d5fc4'), col('#4f72d6'), col('#5c80e0')]
    for j, q in enumerate([6, 9, 12, 15, 18]):
        p = pts[q]
        side = -1 if j % 2 else 1
        hang = p + np.array([side * 7 * SS, 4 * SS])
        _stem(cv, [p, (p + hang) / 2 + np.array([0, -2 * SS]), hang], 0.7 * SS, 0.6 * SS, col('#4e7a2e'), z=0.45)
        s = (1.0 - 0.12 * j)
        bx, by = hang[0], hang[1] + 1 * SS
        X, Y, idx = cv.region(bx, by + 6 * SS * s, 9 * SS * s + 2, 10 * SS * s + 2)
        yy = (Y - by) / (13 * SS * s)
        half = 4.0 * SS * s * (0.55 + 0.75 * np.clip(yy, 0, 1) ** 1.6)
        flare = 1 + 0.35 * np.exp(-((yy - 1.0) / 0.12) ** 2)
        d = np.maximum(np.abs(X - bx) - half * flare, np.maximum(-(Y - by), Y - by - 13 * SS * s))
        rim = 0.5 + 0.5 * np.cos(np.clip((X - bx) / (half * flare + 1e-3), -1, 1) * np.pi)
        c = blues[j % 3][None, None, :] * (0.72 + 0.35 * rim[..., None]) * (0.9 + 0.15 * np.clip(yy, 0, 1)[..., None])
        mouth = np.exp(-((yy - 0.97) / 0.06) ** 2)
        c = mix3(c, col('#28367a'), mouth * 0.5)
        cv.paint(idx, cov_from_sdf(d), c=c, h=1.0 + 1.0 * rim, t=0.0, s=0.35)
    return _finish_plant(cv)


def flower_white():
    """Umbel of tiny florets on branched stalks above feathery leaves."""
    cv = _plant_canvas()
    rng = np.random.default_rng(451)
    N = T * SS
    base = np.array([64 * SS, N + SS])
    for k in range(6):
        a = -np.pi / 2 + (k - 2.5) * 0.42
        L = rng.uniform(22, 32) * SS
        p0 = base + np.array([0, -2 * SS])
        p2 = p0 + np.array([np.cos(a), np.sin(a)]) * L
        pts = _curve(p0, (p0 + p2) / 2 + np.array([0, -3 * SS]), p2, 10)
        _stem(cv, pts, 0.8 * SS, 0.4 * SS, col('#557c34'), z=0.3)
        for q in range(2, 10):
            pp = pts[q]
            dv = pts[min(q + 1, 9)] - pts[q - 1]
            ang = np.arctan2(dv[1], dv[0])
            for sgn in (-1, 1):
                _leaf_paint(cv, pp[0], pp[1], ang + sgn * 0.9, 5 * SS * (1.1 - q / 12), 1.0 * SS, 'lance', 0, 0.32,
                            colour=col('#5a8436') * rng.uniform(0.85, 1.1), tint=0.0)
    hub = np.array([64 * SS, 46 * SS])
    _stem(cv, _curve(base, np.array([62 * SS, 90 * SS]), hub, 12), 1.5 * SS, 1.1 * SS, col('#557c34'), z=0.4)
    heads = []
    for k in range(9):
        a = -np.pi / 2 + (k - 4) * 0.3
        L = rng.uniform(16, 24) * SS
        tip = hub + np.array([np.cos(a) * L * 1.15, np.sin(a) * L * 0.75])
        _stem(cv, [hub, (hub + tip) / 2, tip], 0.6 * SS, 0.5 * SS, col('#5f8a3a'), z=0.5)
        heads.append(tip)
    for hp in heads:
        for f in range(9):
            r = rng.uniform(0, 6.5) * SS
            a = rng.uniform(0, 2 * np.pi)
            fx, fy = hp[0] + np.cos(a) * r, hp[1] + np.sin(a) * r * 0.6
            shade = rng.uniform(0.86, 1.0)
            _disc(cv, fx, fy, 2.3 * SS, col('#ecebe3') * shade, col('#c9c8bd') * shade, 0.7 + 0.1 * rng.random())
            _disc(cv, fx, fy, 0.7 * SS, '#d9c86a', '#b3a24a', 0.85)
    return _finish_plant(cv)


def dead_bush():
    cv = _plant_canvas()
    rng = np.random.default_rng(461)
    N = T * SS
    cols = [col('#7a6248'), col('#8f7556'), col('#6a5440')]

    def branch(p, a, L, w, depth):
        p2 = p + np.array([np.cos(a), np.sin(a)]) * L
        mid = (p + p2) / 2 + np.array([-np.sin(a), np.cos(a)]) * rng.normal(0, L * 0.08)
        _stem(cv, _curve(p, mid, p2, 6), w, w * 0.62, cols[depth % 3] * rng.uniform(0.9, 1.1), z=0.2 + 0.1 * depth)
        if depth >= 4 or L < 6 * SS:
            return
        k = 2 if rng.random() < 0.7 else 3
        for j in range(k):
            na = a + rng.uniform(0.25, 0.6) * (1 if j % 2 else -1) * (1 + 0.3 * j)
            branch(p2 - (p2 - p) * rng.uniform(0, 0.3), na, L * rng.uniform(0.55, 0.75), w * 0.62, depth + 1)
    base = np.array([64 * SS, N + SS])
    for a in (-np.pi / 2 - 0.55, -np.pi / 2 - 0.1, -np.pi / 2 + 0.3, -np.pi / 2 + 0.7):
        branch(base, a + rng.normal(0, 0.08), rng.uniform(30, 40) * SS, 2.4 * SS, 0)
    return _finish_plant(cv)


def _mushroom(cv, rng, x, y_base, height, cap_r, cap_h, cap_c, cap_dark, stem_c, spots=None, gill='#b8a587',
              z=0.5, e_cap=None, e_spot=None, e_stem=None, ring=True, bell=False):
    sx = x
    top = y_base - height
    _stem(cv, [(sx, y_base), (sx + rng.normal(0, 1) * SS, (y_base + top) / 2), (sx, top + cap_h * 0.3)],
          cap_r * 0.22, cap_r * 0.18, col(stem_c), z=z, e=e_stem)
    if ring:
        _disc(cv, sx, top + cap_h * 0.75 + 4 * SS, cap_r * 0.32, stem_c, '#a99c86', z + 0.05, ry=1.6 * SS, e=e_stem)
    # gills band
    _disc(cv, sx, top + cap_h * 0.1, cap_r * 0.92, gill, gill, z + 0.08, ry=cap_h * 0.32, e=e_cap)
    X, Y, idx = cv.region(sx, top, cap_r + 2, cap_h + 2)
    ux = (X - sx) / cap_r
    uy = (top - Y) / cap_h
    if bell:
        prof = np.clip(1 - ux * ux, 0, 1) ** 0.7
    else:
        prof = np.sqrt(np.clip(1 - ux * ux, 0, 1))
    d = np.maximum((uy - prof) * cap_h, -uy * cap_h)
    k = np.clip(np.hypot(ux, uy / max(prof.max(), 1e-3)), 0, 1)
    shade = 0.78 + 0.32 * (1 - np.clip(np.hypot(ux + 0.3, uy - 0.75), 0, 1))
    c = mix3(col(cap_dark), col(cap_c), np.clip(shade - 0.1, 0, 1))
    kw = dict(c=c, h=2.0 * z + 2.2 * np.sqrt(np.clip(prof - np.clip(uy, 0, 1), 0, 1)) + 0.5, t=0.0, s=0.45)
    if e_cap is not None:
        kw['e'] = e_cap
    cv.paint(idx, cov_from_sdf(d), **kw)
    if spots:
        for (fx, fy, fr) in spots:
            ppx = sx + fx * cap_r
            ppy = top - fy * cap_h
            _disc(cv, ppx, ppy, fr * SS, spots_col(spots), '#d6ccb8', z + 0.15, ry=fr * SS * 0.8, e=e_spot, smooth=0.3, h=0.6)


def spots_col(_):
    return '#efe6d6'


def mushroom_red():
    cv = _plant_canvas()
    rng = np.random.default_rng(471)
    N = T * SS
    _mushroom(cv, rng, 44 * SS, N + SS, 34 * SS, 16 * SS, 14 * SS, '#d0432f', '#8f2219', '#e3d8c2', z=0.3,
              spots=[(-0.5, 0.45, 2.0), (0.1, 0.75, 2.4), (0.55, 0.4, 1.8)], ring=False)
    _mushroom(cv, rng, 72 * SS, N + SS, 66 * SS, 30 * SS, 25 * SS, '#d4442f', '#8a2117', '#e8ddc8', z=0.6,
              spots=[(-0.62, 0.35, 3.2), (-0.25, 0.72, 3.8), (0.2, 0.86, 3.0), (0.5, 0.55, 3.6), (0.05, 0.42, 2.6),
                     (0.78, 0.25, 2.4), (-0.85, 0.12, 2.0)])
    return _finish_plant(cv)


def mushroom_brown():
    cv = _plant_canvas()
    rng = np.random.default_rng(481)
    N = T * SS
    _mushroom(cv, rng, 84 * SS, N + SS, 30 * SS, 16 * SS, 9 * SS, '#a47a52', '#5e3f26', '#ded2bc', z=0.3, ring=False,
              gill='#8d7a62')
    _mushroom(cv, rng, 52 * SS, N + SS, 58 * SS, 32 * SS, 15 * SS, '#9c714a', '#573a22', '#e2d6c0', z=0.6, ring=False,
              gill='#8d7a62')
    _mushroom(cv, rng, 30 * SS, N + SS, 20 * SS, 10 * SS, 6 * SS, '#b08560', '#664629', '#ded2bc', z=0.8, ring=False,
              gill='#8d7a62')
    return _finish_plant(cv)


def glowcap():
    """Bioluminescent bell caps; mask A carries emission strength."""
    cv = _plant_canvas(emissive=True)
    rng = np.random.default_rng(491)
    N = T * SS
    specs = [(40, 46, 13, 15, 0.3), (70, 80, 19, 22, 0.6), (95, 34, 10, 12, 0.8)]
    for x, hgt, r, ch, z in specs:
        spots = [(rng.uniform(-0.6, 0.6), rng.uniform(0.25, 0.85), rng.uniform(1.3, 2.2)) for _ in range(5)]
        _mushroom(cv, rng, x * SS, N + SS, hgt * SS, r * SS, ch * SS, '#4fe0c2', '#1b8d84', '#b9c9cc', z=z,
                  spots=None, gill='#9ff5e2', e_cap=0.75, e_stem=0.08, ring=False, bell=True)
        top = N + SS - hgt * SS
        for (fx, fy, fr) in spots:
            _disc(cv, x * SS + fx * r * SS, top - fy * ch * SS, fr * SS, '#d8fff4', '#8cf0d9', z + 0.2, e=1.0,
                  smooth=0.5, h=0.5)
    return _finish_plant(cv)


def torch():
    """Wrapped torch: wooden haft, resin-soaked binding, flame. Mask A = emission."""
    cv = _plant_canvas(emissive=True)
    N = T * SS
    rng = np.random.default_rng(501)
    x = 64 * SS
    y_bot = N + SS
    y_top = 44 * SS
    grain = fbm(N, 502, beta=1.6, ax=1.0, ay=0.1)
    X, Y, idx = cv.region(x, (y_bot + y_top) / 2, 6 * SS, (y_bot - y_top) / 2 + 2)
    half = (4.2 - 0.8 * np.clip((Y - y_top) / (y_bot - y_top), 0, 1)) * SS
    d = np.maximum(np.abs(X - x) - half, y_top - Y)
    u = np.clip((X - x) / half, -1, 1)
    gs = grain[idx[0][:, None] % N, idx[1][None, :] % N]
    wood = ramp(0.55 + 0.12 * gs, [(0, '#5e4127'), (0.5, '#8a6440'), (1, '#a77d50')])
    wood = wood * (0.78 + 0.3 * np.sqrt(np.clip(1 - u * u, 0, 1)) - 0.1 * u)[..., None]
    cv.paint(idx, cov_from_sdf(d), c=wood, h=1.0 + 1.2 * np.sqrt(np.clip(1 - u * u, 0, 1)), t=0.0, s=0.25, e=0.0)
    # binding
    X, Y, idx = cv.region(x, 50 * SS, 7 * SS, 9 * SS)
    half = 5.6 * SS
    d = np.maximum(np.abs(X - x) - half, np.abs(Y - 50 * SS) - 7.5 * SS)
    u = np.clip((X - x) / half, -1, 1)
    band = 0.5 + 0.5 * np.sin((Y - 0.35 * (X - x)) / SS * 1.6)
    wrap_c = mix3(col('#3d2a1d'), col('#6b4a2e'), band)
    ember = smoothstep(52 * SS, 42 * SS, Y)
    wrap_c = mix3(wrap_c, col('#d0652a'), ember * 0.6)
    wrap_c = wrap_c * (0.75 + 0.3 * np.sqrt(np.clip(1 - u * u, 0, 1)))[..., None]
    cv.paint(idx, cov_from_sdf(d), c=wrap_c, h=2.0 + 1.0 * band, t=0.0, s=0.3, e=0.35 * ember)
    # flame (outer + core), teardrop pointing up
    for (L, W, c_in, c_out, e, z) in [(40, 12.5, '#ffc04a', '#f07a26', 0.95, 3.0), (27, 7.5, '#fff3c2', '#ffd060', 1.0, 3.5)]:
        bx, by = x, 47 * SS
        X, Y, idx = cv.region(bx, by - L * SS / 2, W * SS + 3, L * SS / 2 + 4 * SS)
        u, v = local_uv(X, Y, bx, by + 3 * SS, -np.pi / 2)
        t = u / (L * SS)
        tc = np.clip(t, 0, 1)
        w = W * SS * np.sin(np.pi * np.clip(tc * 0.85 + 0.15, 0, 1) ** 0.55) ** 0.9 * (1 - 0.55 * tc)
        wob = 1.5 * SS * np.sin(t * 7 + 0.5) * tc
        dd = np.maximum(np.abs(v - wob) - w, np.maximum(-u, u - L * SS))
        k = np.clip(np.abs(v - wob) / np.maximum(w, 1e-3), 0, 1)
        c = mix3(col(c_in), col(c_out), np.clip(k ** 1.5 + 0.4 * tc, 0, 1))
        cv.paint(idx, cov_from_sdf(dd), c=c, h=z, t=0.0, s=0.1, e=e)
    return _finish_plant(cv, nstrength=0.6)
