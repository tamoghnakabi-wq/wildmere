"""Wood: bark sides, end-grain log tops, planks and a reusable grain generator."""
from functools import lru_cache

import numpy as np

from texlib import (TILE, Canvas, blur, col, cov_from_sdf, edge_distance, fbm, finish, grid,
                    hash01, mix3, poisson, polyline_capsules, ramp, smoothstep, spectral, warp,
                    worley)

T = TILE


def _speck(n, seed, frac):
    w = np.random.default_rng(seed).random((n, n))
    thr = np.quantile(w, 1.0 - frac)
    return smoothstep(thr, thr + (1 - thr) * 0.4, w)


# ------------------------------------------------------------------ bark ---
def _furrowed_bark(seed, pal, spacing=11.0, ay=0.3, furrow=4.5, depth=3.2, fib_amt=0.5,
                   crack_frac=0.35, tone_sd=0.06, weather=None):
    """Ridged bark with anastomosing vertical furrows (oak / dark oak)."""
    n = T
    X, Y = grid(n)
    wx = fbm(n, seed + 1, beta=3.0, ax=1.0, ay=0.35) * 3.0
    pts = poisson(n, spacing, seed + 2, ay=ay)
    w = worley(n, pts, ax=1.0, ay=ay, qdx=wx)
    E = w['E']
    rp = np.sqrt(np.clip(1 - (1 - np.clip(E / furrow, 0, 1)) ** 2, 0, 1))
    fib = spectral(n, seed + 3, beta=1.7, ax=1.0, ay=0.1)
    fib2 = spectral(n, seed + 4, beta=1.2, ax=1.0, ay=0.25)
    rng = np.random.default_rng(seed + 5)
    k = len(pts)
    tone = rng.normal(0, tone_sd, k)[w['id']]
    lev = rng.normal(0, 0.35, k)[w['id']]
    has = (rng.random(k) < crack_frac * 0.6)[w['id']]
    off = rng.uniform(-12, 12, k)[w['id']]
    crack = np.exp(-((Y - w['cy'] - off) / 0.8) ** 2) * has * smoothstep(1.0, 3.0, E)
    dark = smoothstep(0.4, furrow * 0.85, E)
    h = depth * rp + 0.5 * lev * rp + fib_amt * fib + 0.2 * fib2 - 0.9 * crack
    t = 0.14 + 0.5 * dark + 0.18 * rp * rp + 0.07 * fib + 0.05 * fib2 + tone - 0.18 * crack
    alb = ramp(t, pal)
    if weather is not None:
        wmask = smoothstep(0.6, 1.0, rp) * smoothstep(0.0, 1.2, fbm(n, seed + 6, beta=2.4))
        alb = mix3(alb, col(weather), wmask * 0.45)
    sm = 0.16 + 0.08 * rp
    return alb, h, sm


def log_oak_side():
    pal = [(0.0, '#2e2118'), (0.25, '#3f2e20'), (0.55, '#5b432e'), (0.8, '#6f553b'), (1.0, '#82684b')]
    alb, h, sm = _furrowed_bark(201, pal, spacing=10.0, ay=0.22, furrow=6.5, depth=3.6, weather='#8a8070')
    return finish(alb, h, sm, nstrength=1.0, cavity=0.11, bevel_d=1.4)


def log_dark_side():
    pal = [(0.0, '#1f1712'), (0.3, '#2b2119'), (0.6, '#3b2e23'), (0.85, '#4a3b2e'), (1.0, '#57483a')]
    alb, h, sm = _furrowed_bark(211, pal, spacing=9.0, ay=0.4, furrow=5.0, depth=3.8,
                                crack_frac=0.55, weather='#5e5a48')
    lich = _speck(T, 212, 0.012) * smoothstep(0.3, 1.0, fbm(T, 213, beta=2.5))
    alb = mix3(alb, col('#6d6f55'), lich * 0.6)
    return finish(np.clip(alb, 0.14, 1), h, sm, nstrength=1.0, cavity=0.11, bevel_d=1.4)


def log_spruce_side():
    n = T
    seed = 221
    X, Y = grid(n)
    wx = fbm(n, seed + 1, beta=3.0) * 1.8
    wy = fbm(n, seed + 2, beta=3.0) * 1.8
    pts = poisson(n, 7.4, seed + 3, ay=0.55)
    w = worley(n, pts, ax=1.0, ay=0.55, qdx=wx, qdy=wy)
    E = w['E']
    rng = np.random.default_rng(seed + 4)
    k = len(pts)
    dy = (Y + wy - w['cy'])
    shingle = np.clip(dy / 9.0, -1, 1)
    rp = smoothstep(0.0, 2.6, E)
    lev = rng.normal(0, 0.3, k)[w['id']]
    fine = fbm(n, seed + 5, beta=1.3)
    h = rp * (1.6 + 1.5 * shingle + lev) + 0.2 * fine
    grey_ = (rng.random(k) < 0.22)[w['id']]
    tone = rng.normal(0, 0.05, k)[w['id']]
    t = 0.18 + 0.42 * rp + 0.32 * rp * (shingle * 0.5 + 0.5) + tone + 0.05 * fine
    pal = [(0.0, '#24150f'), (0.25, '#3c2419'), (0.55, '#5a3726'), (0.8, '#6f4632'), (1.0, '#83573f')]
    alb = ramp(t, pal)
    alb = mix3(alb, alb * 0.6 + col('#7a6c62') * 0.4, grey_ * rp * 0.7)
    return finish(alb, h, 0.17 + 0.06 * rp, nstrength=1.0, cavity=0.12, bevel_d=1.3)


def log_birch_side():
    n = T
    seed = 231
    ss = 2
    N = n * ss
    X, Y = grid(N)
    streak = spectral(N, seed, beta=2.0, ax=0.07, ay=1.0)
    mott = fbm(N, seed + 1, beta=2.8)
    base = ramp(0.55 + 0.1 * streak + 0.08 * mott, [(0, '#bdb5a5'), (0.4, '#d3ccbd'), (0.75, '#dfd9cc'), (1, '#e7e2d7')])
    cv = Canvas(n, ss=ss, wrap=True, c=(0, 0, 0), h=0.0, s=0.0)
    cv.fill(c=base, h=0.2 * streak, s=0.34)
    rng = np.random.default_rng(seed + 2)
    rough = fbm(N, seed + 3, beta=2.2)
    # dark bark patches (irregular, horizontally stretched)
    for i in range(7):
        cx, cy = rng.random(2) * N
        a = rng.uniform(5, 13) * ss
        b = a * rng.uniform(0.25, 0.5)
        X2, Y2, idx = cv.region(cx, cy, a * 1.4 + 2, b * 2.2 + 2)
        u = (X2 - cx) / a
        v = (Y2 - cy) / b
        r = np.sqrt(u * u + v * v)
        nz = rough[idx[0][:, None], idx[1][None, :]]
        d = (r - 1.0 + 0.3 * nz) * b
        cc = ramp(0.5 + 0.3 * nz, [(0, '#2c2824'), (0.5, '#3b3631'), (1, '#4f4943')])
        cv.paint(idx, cov_from_sdf(d), c=cc, h=0.3 + 0.5 * nz, s=0.18)
        if rng.random() < 0.6:  # chevron branch scar above the patch
            for sgn in (-1, 1):
                p0 = np.array([cx, cy - b * 0.6])
                p1 = p0 + np.array([sgn * a * rng.uniform(0.8, 1.4), -rng.uniform(2, 5) * ss])
                polyline_capsules(cv, [p0, p1], 1.6 * ss, 0.5 * ss,
                                  lambda tg, dn, X3, Y3: dict(c=col('#3a3530') * (1 + 0.15 * dn)[..., None], h=0.2, s=0.18))
    # lenticels: thin dark horizontal dashes
    for i in range(70):
        cx, cy = rng.random(2) * N
        L = rng.uniform(4, 15) * ss
        th = rng.uniform(0.45, 0.9) * ss
        ang = rng.normal(0, 0.04)
        d = np.array([np.cos(ang), np.sin(ang)]) * L / 2
        c0 = col(['#4a433b', '#5a5249', '#3d3731'][rng.integers(3)])
        polyline_capsules(cv, [np.array([cx, cy]) - d, np.array([cx, cy]) + d], th, th * 0.6,
                          lambda tg, dn, X3, Y3, c0=c0: dict(c=c0 * (1 + 0.25 * dn)[..., None], h=-0.6 + 0.4 * dn, s=0.2))
    # papery peel curls
    for i in range(9):
        cx, cy = rng.random(2) * N
        L = rng.uniform(6, 14) * ss
        d = np.array([L / 2, rng.normal(0, 0.6)])
        c0 = col(['#c9ad86', '#d8c09c', '#b8946c'][rng.integers(3)])
        polyline_capsules(cv, [np.array([cx, cy]) - d, np.array([cx, cy]) + d], 1.1 * ss, 0.7 * ss,
                          lambda tg, dn, X3, Y3, c0=c0: dict(c=c0 * (1.05 - 0.2 * dn)[..., None], h=0.9 * (1 - dn), s=0.3))
    A, o = cv.resolve()
    return finish(o['c'], o['h'], o['s'], nstrength=1.0, cavity=0.08, bevel_d=1.3)


# --------------------------------------------------------------- log tops ---
def _log_top(seed, wood, late, heart, bark_pal, spacing=4.6, rim=9.0, pith='#4d3624',
             rays=0.0, checks=2, superp=3.4, bark_dark='#2a1d14', speck_col=None):
    n = T
    X, Y = grid(n)
    dx, dy = X - n / 2, Y - n / 2
    r = (np.abs(dx) ** superp + np.abs(dy) ** superp) ** (1 / superp)
    rc = np.hypot(dx, dy)
    th = np.arctan2(dy, dx)
    wob = fbm(n, seed, beta=3.4) * 1.6
    rw = r + wob + 0.6 * np.sin(3 * th + 1.3)
    rr = rw / spacing + 0.12 * fbm(n, seed + 1, beta=2.2)
    # ring spacing varies with growth years
    rr = rr + 0.6 * np.sin(rw / 13.0)
    f = rr % 1.0
    lw = smoothstep(0.55, 0.85, f) * (1 - smoothstep(0.9, 1.0, f))
    fine = fbm(n, seed + 2, beta=1.4)
    t = 0.5 + 0.12 * fine
    wood_c = ramp(t, [(0, col(wood) * 0.9), (1, col(wood) * 1.06)])
    wood_c = mix3(wood_c, col(late), lw * 0.75)
    heartk = 1 - smoothstep(14, 34, rw)
    wood_c = mix3(wood_c, col(heart), heartk * 0.35)
    if rays:
        ray = 0.5 + 0.5 * np.sin(th * 70 + 3 * fbm(n, seed + 3, beta=2.5))
        wood_c = mix3(wood_c, col(wood) * 1.1, smoothstep(0.93, 1.0, ray) * rays * smoothstep(3, 10, rc))
    # radial drying checks
    rng = np.random.default_rng(seed + 4)
    crack = np.zeros((n, n))
    wig = fbm(n, seed + 8, beta=2.6) * 0.05
    for i in range(checks):
        a0 = rng.uniform(-np.pi, np.pi)
        r0 = rng.uniform(rim + 6, rim + 16)
        L = rng.uniform(10, 20)
        da = np.angle(np.exp(1j * (th - a0 - wig * 3)))
        dist = np.abs(da) * rc
        rin = (n / 2 - r0)
        u = np.clip((rc - rin) / L, 0, 1)
        wdt = 0.55 * u + 0.1
        crack = np.maximum(crack, (1 - smoothstep(0, wdt + 0.35, dist)) * (rc > rin) * (rc < rin + L + 2))
    pithm = 1 - smoothstep(1.5, 3.2, rc)
    wood_c = mix3(wood_c, col(pith), pithm)
    wood_c = wood_c * (1 - 0.4 * crack)[..., None]
    # bark rim
    D = edge_distance(n)
    rimn = fbm(n, seed + 5, beta=2.6)
    rim_edge = rim + 1.3 * rimn
    barkm = smoothstep(rim_edge + 0.6, rim_edge - 0.6, D)
    along = np.where(np.abs(dx) > np.abs(dy), Y, X)
    bk = spectral(n, seed + 6, beta=1.6, ax=1.0, ay=1.0)
    furrows = 0.5 + 0.5 * np.sin(along * 2 * np.pi / 7.0 + 2.5 * bk)
    bark_c = ramp(0.25 + 0.6 * furrows * smoothstep(0, 3, D) + 0.1 * bk, bark_pal)
    bark_c = mix3(bark_c, col(bark_dark), 1 - smoothstep(rim_edge - 2.5, rim_edge, D) * 0 - smoothstep(0, rim_edge, D) * 0 + 0)  # no-op keep
    if speck_col is not None:
        sp = _speck(n, seed + 7, 0.06)
        bark_c = mix3(bark_c, col(speck_col), sp * 0.8)
    camb = np.exp(-((D - rim_edge - 1.2) / 0.9) ** 2)
    sap = smoothstep(rim_edge + 7, rim_edge + 1.5, D) * (1 - barkm)
    wood_c = mix3(wood_c, col(wood) * 1.08, sap * 0.35)
    wood_c = wood_c * (1 - 0.25 * camb)[..., None]
    alb = mix3(wood_c, bark_c, barkm)
    h = (0.25 * lw + 0.1 * fine - 1.2 * crack - 0.5 * pithm) * (1 - barkm) + barkm * (1.2 + 1.0 * furrows + 0.3 * bk)
    h = h - 0.6 * camb
    sm = (0.24 + 0.04 * lw) * (1 - barkm) + 0.17 * barkm
    return finish(alb, h, sm, nstrength=1.0, cavity=0.09, bevel_d=1.3)


def log_oak_top():
    return _log_top(241, '#b48d5c', '#8a6439', '#9b7046',
                    [(0, '#2e2118'), (0.5, '#4f3a28'), (1, '#6d5339')], rays=0.6)


def log_birch_top():
    return _log_top(251, '#d6c29a', '#b89b6e', '#c8ad80',
                    [(0, '#a8a090'), (0.5, '#cfc8b8'), (1, '#e0dbcf')], spacing=5.2, rim=7.0,
                    pith='#8a7050', speck_col='#3d3832')


def log_spruce_top():
    return _log_top(261, '#bd9065', '#94643f', '#a8754c',
                    [(0, '#24150f'), (0.5, '#4a2d20'), (1, '#6a4330')], spacing=3.4, rim=8.0)


def log_dark_top():
    return _log_top(271, '#7a5838', '#553820', '#62432a',
                    [(0, '#1f1712'), (0.5, '#33271d'), (1, '#4a3b2e')], spacing=4.2, rim=9.5,
                    pith='#3a2818', checks=3)


# ------------------------------------------------------------------ grain ---
def wood_grain(n, seed, spacing=6.0, warp_amt=5.0, knots=(), axis='x'):
    """Periodic flat-sawn grain running along x (axis='x') or y.

    Returns (t, late): t = 0..1 tone field, late = latewood line mask.
    knots: list of (x, y, r) bulging the grain lines around a knot.
    """
    X, Y = grid(n)
    if axis == 'y':
        X, Y = Y, X
    wv = spectral(n, seed, beta=3.2, ax=0.18, ay=1.0) * warp_amt
    if axis == 'y':
        wv = wv.T
    disp = wv.copy()
    for (kx, ky, kr) in knots:
        ddx = (X - kx + n / 2) % n - n / 2
        ddy = (Y - ky + n / 2) % n - n / 2
        g = np.exp(-(ddx ** 2 / (2 * (kr * 2.2) ** 2) + ddy ** 2 / (2 * (kr * 1.1) ** 2)))
        disp = disp + np.sign(ddy + 1e-6) * g * kr * 1.6
    k = max(1, int(round(n / spacing)))
    ph = (Y + disp) * k / n
    f = ph % 1.0
    late = smoothstep(0.62, 0.8, f) * (1 - smoothstep(0.86, 1.0, f))
    fib = spectral(n, seed + 1, beta=1.5, ax=0.05, ay=1.0)
    if axis == 'y':
        fib = fib.T
    t = 0.55 + 0.12 * fib - 0.16 * late + 0.05 * spectral(n, seed + 2, beta=2.5, ax=0.3, ay=1.0)
    knot_m = np.zeros((n, n))
    for (kx, ky, kr) in knots:
        ddx = (X - kx + n / 2) % n - n / 2
        ddy = (Y - ky + n / 2) % n - n / 2
        rr = np.sqrt((ddx / 1.0) ** 2 + (ddy / 0.75) ** 2)
        ring = 0.5 + 0.5 * np.sin(rr * 2.2)
        km = 1 - smoothstep(kr * 0.7, kr, rr)
        knot_m = np.maximum(knot_m, km)
        t = t - km * (0.25 + 0.12 * ring)
    if axis == 'y':
        t, late, knot_m = t, late, knot_m
    return t, late, knot_m, fib


PLANK_PAL = [(0.0, '#5e4127'), (0.3, '#7d5a38'), (0.55, '#9a7349'), (0.8, '#ad8558'), (1.0, '#bd9668')]


@lru_cache(None)
def planks_fields(seed=281, pal=None):
    n = T
    pal = pal or PLANK_PAL
    X, Y = grid(n)
    rng = np.random.default_rng(seed)
    bh = 32
    board = np.clip((Y // bh).astype(int), 0, 3)
    yl = Y - board * bh
    alb = np.zeros((n, n, 3))
    h = np.zeros((n, n))
    sm = np.zeros((n, n))
    # jointed boards get two butt joints inside the tile; the segment that wraps
    # through the tile edge uses one periodic grain field, so the sheet tiles cleanly
    joints = []
    for b in range(4):
        if b in (0, 2):
            j0 = rng.uniform(14, 50)
            joints.append((j0, j0 + rng.uniform(44, 64)))
        else:
            joints.append(None)
    nails = np.zeros((n, n))
    for b in range(4):
        for side in (0, 1):
            seg_seed = seed + 10 + b * 2 + side
            knots = []
            if hash01(b, side, seed) < 0.35:
                knots = [(rng.uniform(10, 118), b * bh + rng.uniform(10, 22), rng.uniform(2.2, 3.4))]
            t, late, km, fib = wood_grain(n, seg_seed, spacing=rng.uniform(8.0, 11.0), knots=knots)
            tone = rng.normal(0, 0.05)
            m = board == b
            if joints[b] is not None:
                j0, j1 = joints[b]
                inner = (X >= j0) & (X < j1)
                m = m & (inner if side == 1 else ~inner)
            elif side == 1:
                continue
            alb[m] = ramp(t[m] + tone, pal)
            h[m] = (0.3 * (1 - late) + 0.12 * fib - 0.6 * km)[m]
            sm[m] = 0.28 + 0.05 * (1 - late[m])
    # board edges: long seams and butt joints, rounded over ~2.5 px
    e = np.minimum(yl, bh - yl)
    for b in range(4):
        if joints[b] is None:
            continue
        m = board == b
        for jx in joints[b]:
            ex = np.abs(X - jx)
            e = np.where(m, np.minimum(e, ex), e)
            for yy in (b * bh + 8, b * bh + 24):
                for xx in (jx - 4.5, jx + 4.5):
                    d = np.hypot(X - xx, Y - yy)
                    nails = np.maximum(nails, 1 - smoothstep(1.0, 1.9, d))
    prof = np.sqrt(np.clip(1 - (1 - np.clip(e / 2.6, 0, 1)) ** 2, 0, 1))
    gap = 1 - smoothstep(0.3, 1.0, e)
    h = h + 1.5 * prof - 0.6 * gap
    alb = alb * (0.8 + 0.2 * prof)[..., None]
    alb = mix3(alb, col('#3b2a1a'), gap * 0.7)
    nail_c = col('#5b5550')
    alb = mix3(alb, nail_c, nails)
    alb = mix3(alb, col('#4a3424'), np.clip(blur(nails, 1.2) * 1.5 - nails, 0, 1) * 0.6)
    h = h + 0.2 * nails
    sm = sm * (1 - nails) + 0.5 * nails
    return alb, h, sm


def planks():
    alb, h, sm = planks_fields()
    return finish(alb, h, sm, nstrength=1.0, cavity=0.1, bevel_d=1.2)
