"""Ground and rock materials: soil, grass, stone family, sand family, snow, ice, water."""
from functools import lru_cache

import numpy as np

from texlib import (TILE, Canvas, band, blur, col, cov_from_sdf, fbm, finish, grey, grid,
                    hash01, local_uv, mix3, poisson, polyline_capsules, ramp, sd_ellipse,
                    smoothstep, spectral, warp, worley)

T = TILE


def _speckle(n, seed, frac, sigma=0.0):
    """Sparse random specks: 1 where a (slightly blurred) white noise is in the top `frac`."""
    w = np.random.default_rng(seed).random((n, n))
    if sigma:
        w = blur(w, sigma)
    thr = np.quantile(w, 1.0 - frac)
    return smoothstep(thr, thr + (w.max() - thr) * 0.35 + 1e-9, w)


# ------------------------------------------------------------------ soil ---
SOIL = [(0.0, '#33251b'), (0.3, '#4a3526'), (0.55, '#624632'), (0.8, '#7b5b41'), (1.0, '#916f53')]


def _lumpy_ellipse(X, Y, cx, cy, a, b, ang, ph, amt=0.16):
    """Ellipse SDF with an irregular, lumpy outline."""
    u, v = local_uv(X, Y, cx, cy, ang)
    th = np.arctan2(v / b, u / a)
    k = np.sqrt((u / a) ** 2 + (v / b) ** 2)
    rm = 1 + amt * np.sin(3 * th + ph[0]) + amt * 0.6 * np.sin(5 * th + ph[1]) + amt * 0.4 * np.sin(2 * th + ph[2])
    k = k / rm
    return (k - 1.0) * min(a, b), k


@lru_cache(None)
def dirt_base(seed=11):
    n = T
    ss = 2
    N = n * ss
    rng = np.random.default_rng(seed)
    low = fbm(N, seed + 1, beta=2.8)
    f2 = fbm(N, seed + 2, beta=1.4)
    base_t = 0.40 + 0.13 * low
    cv = Canvas(n, ss=ss, wrap=True, c=(0, 0, 0), h=0.0, s=0.0)
    cv.fill(c=ramp(base_t - 0.16 + 0.05 * f2, SOIL), h=-1.2 + 0.3 * f2, s=0.1)
    count = 1500
    zs = np.sort(rng.random(count))
    for z in zs:
        cx, cy = rng.random(2) * N
        a = (1.3 + 3.2 * rng.random() ** 1.7) * ss
        b = a * rng.uniform(0.6, 1.0)
        ang = rng.uniform(0, np.pi)
        X, Y, idx = cv.region(cx, cy, a * 1.3 + 2)
        d, k = _lumpy_ellipse(X, Y, cx, cy, a, b, ang, rng.uniform(0, 6.3, 3))
        dome = np.sqrt(np.clip(1 - k * k, 0, 1))
        bt = base_t[int(cy) % N, int(cx) % N]
        t = bt + 0.12 * z + rng.normal(0, 0.085)
        c0 = ramp(np.array(t), SOIL)
        cc = c0 * (0.86 + 0.16 * dome)[..., None]
        cv.paint(idx, cov_from_sdf(d), c=cc, h=1.1 * z + dome * (b / ss) * 0.55, s=0.12 + 0.05 * dome)
    # a few distinct stones
    fine = fbm(N, seed + 3, beta=0.9)
    pal = ['#857a6b', '#9a8f7e', '#6f655a', '#a49884', '#7d6b58', '#8d8478']
    for i in range(11):
        cx, cy = rng.random(2) * N
        a = rng.uniform(2.2, 4.4) * ss
        b = a * rng.uniform(0.6, 0.9)
        ang = rng.uniform(0, np.pi)
        X, Y, idx = cv.region(cx, cy, a * 1.3 + 2)
        d, k = _lumpy_ellipse(X, Y, cx, cy, a, b, ang, rng.uniform(0, 6.3, 3), amt=0.1)
        dome = np.sqrt(np.clip(1 - k * k, 0, 1))
        sp = fine[idx[0][:, None], idx[1][None, :]]
        c0 = col(pal[i % len(pal)]) * rng.uniform(0.88, 1.05)
        cc = c0 * (0.82 + 0.16 * dome + 0.06 * sp)[..., None]
        cc = cc * (1 - 0.18 * smoothstep(0.7, 1.0, k))[..., None]
        cv.paint(idx, cov_from_sdf(d), c=cc, h=1.6 + 1.6 * dome * (b / ss) / 4, s=0.32)
    # root fibres
    for i in range(7):
        x, y = rng.random(2) * N
        ang = rng.uniform(0, 2 * np.pi)
        pts_ = [(x, y)]
        L = rng.uniform(26, 50)
        for _ in range(8):
            ang += rng.normal(0, 0.45)
            x += np.cos(ang) * L / 8 * ss
            y += np.sin(ang) * L / 8 * ss
            pts_.append((x, y))
        dark = i % 3 != 0
        c0 = col('#3b2a1e') if dark else col('#8c7558')
        polyline_capsules(cv, pts_, rng.uniform(0.9, 1.3), 0.5,
                          lambda tg, dn, X2, Y2, c0=c0: dict(c=c0 * (1 - 0.15 * dn)[..., None], h=1.6 + 0.4 * (1 - dn * dn), s=0.2))
    A, o = cv.resolve()
    alb, h, sm = o['c'], o['h'], o['s']
    g_l = _speckle(n, seed + 6, 0.015)
    g_d = _speckle(n, seed + 7, 0.04)
    alb = mix3(alb, col('#9a8770'), g_l * 0.3)
    alb = mix3(alb, col('#2c2018'), g_d * 0.3)
    return alb, h + 0.2 * g_l, sm


def dirt():
    alb, h, sm = dirt_base()
    return finish(alb, h, sm, nstrength=1.0)


# ----------------------------------------------------------------- grass ---
def _blade_lum(z, rng, dry):
    base = 0.50 + 0.30 * z + rng.normal(0, 0.03)
    return base + (0.05 if dry else 0.0)


@lru_cache(None)
def grass_top_fields(seed=21):
    n = T
    ss = 2
    N = n * ss
    low = fbm(N, seed + 1, beta=3.0)
    under = 0.40 + 0.05 * fbm(N, seed + 2, beta=1.8)
    cv = Canvas(n, ss=ss, wrap=True, l=0.0, h=0.0, w=0.0)
    cv.fill(l=under, h=np.zeros((N, N)), w=np.zeros((N, N)))
    rng = np.random.default_rng(seed)
    nb = 2900
    zs = np.sort(rng.random(nb))
    lean = rng.uniform(0, 2 * np.pi)
    for z in zs:
        cx, cy = rng.random(2) * N
        if rng.random() < 0.55:
            ang = lean + rng.normal(0, 0.7)
        else:
            ang = rng.uniform(0, 2 * np.pi)
        L = rng.uniform(5.5, 12.5) * ss
        w0 = rng.uniform(0.85, 1.35) * ss
        dry = rng.random() < 0.05
        lum = _blade_lum(z, rng, dry)
        # slight curve
        bend = rng.normal(0, 0.25)
        p0 = np.array([cx, cy])
        dirv = np.array([np.cos(ang), np.sin(ang)])
        perp = np.array([-dirv[1], dirv[0]])
        p2 = p0 + dirv * L
        p1 = p0 + dirv * L * 0.5 + perp * bend * L * 0.4
        ts = np.linspace(0, 1, 5)
        pts = [(1 - t) ** 2 * p0 + 2 * (1 - t) * t * p1 + t * t * p2 for t in ts]
        warm = 0.10 if dry else 0.015 * z

        def vf(tg, dn, X2, Y2, lum=lum, z=z, warm=warm):
            l = lum * (0.86 + 0.18 * tg) * (1 - 0.13 * dn * dn)
            return dict(l=l, h=2.4 * z + 0.6 * np.sqrt(np.clip(1 - dn * dn, 0, 1)) + 0.3 * tg, w=warm)
        polyline_capsules(cv, pts, w0, 0.18 * ss, vf)
    A, o = cv.resolve()
    l = o['l'] * (1 + 0.07 * blur(low, 0.1)[::ss, ::ss])
    return l, o['h'], o['w']


def grass_top():
    l, h, w = grass_top_fields()
    alb = grey(l, 0) * np.stack([1 + w, np.ones_like(w), 1 - w], -1)
    sm = 0.24 + 0.06 * np.clip(h / 3, 0, 1)
    return finish(alb, h, sm, tint=np.ones_like(l), nstrength=1.0, cavity=0.07)


def _profile1d(n, seed, base, amp, beta=2.2):
    """Periodic 1D profile along x (sampled from a periodic 2D noise row)."""
    f = fbm(n, seed, beta=beta)[n // 3]
    return base + amp * f / (np.abs(f).max() + 1e-9)


def _fringe_canvas(seed, n_blades, band_base, band_amp, len_rng, lum_off=0.0):
    n = T
    ss = 2
    N = n * ss
    X, Y = np.meshgrid(np.arange(N) + 0.5, np.arange(N) + 0.5)
    prof = _profile1d(n, seed + 1, band_base, band_amp)
    prof2 = np.repeat(prof, ss) * ss
    cv = Canvas(n, ss=ss, wrap='x', l=0.0, h=0.0, w=0.0)
    under = 0.47 + 0.05 * fbm(N, seed + 2, beta=1.8)
    bandcov = np.clip(prof2[None, :] - Y + 0.5, 0, 1)
    cv.paint_full(bandcov, l=under, h=1.0, w=0.0)
    rng = np.random.default_rng(seed)
    zs = np.sort(rng.random(n_blades))
    for z in zs:
        x0 = rng.random() * N
        top = np.interp(x0, np.arange(N) + 0.5, prof2, period=N)
        y0 = rng.uniform(0.0, max(top - 1.0 * ss, 0.5))
        L = (len_rng[0] + (len_rng[1] - len_rng[0]) * rng.random() ** 1.8) * ss
        ang = np.pi / 2 + rng.normal(0, 0.28)
        bend = rng.normal(0, 0.35)
        p0 = np.array([x0, y0])
        dirv = np.array([np.cos(ang), np.sin(ang)])
        perp = np.array([-dirv[1], dirv[0]])
        p2 = p0 + dirv * L
        p1 = p0 + dirv * L * 0.55 + perp * bend * L * 0.35
        pts = [(1 - t) ** 2 * p0 + 2 * (1 - t) * t * p1 + t * t * p2 for t in np.linspace(0, 1, 6)]
        lum = 0.50 + 0.30 * z + rng.normal(0, 0.035) + lum_off
        dry = rng.random() < 0.05
        warm = 0.10 if dry else 0.01

        def vf(tg, dn, X2, Y2, lum=lum, z=z, warm=warm):
            l = lum * (1.02 - 0.16 * tg) * (1 - 0.12 * dn * dn)
            return dict(l=l, h=1.2 + 1.0 * z + 0.5 * np.sqrt(np.clip(1 - dn * dn, 0, 1)), w=warm)
        polyline_capsules(cv, pts, rng.uniform(1.1, 1.6) * ss, 0.2 * ss, vf)
    return cv.resolve()


def _contact_shadow(A, drop=2.0, sigma=2.4, strength=0.45):
    sh = np.roll(A, int(round(drop)), axis=0)
    sh[: int(round(drop))] = 1.0
    sh = blur(sh, sigma * 0.6, sigma)
    return np.clip(sh - A, 0, 1) * strength


def grass_side():
    dalb, dh, dsm = dirt_base()
    A, o = _fringe_canvas(31, 260, 6.5, 2.5, (7, 30))
    l = o['l']
    gal = grey(l) * np.stack([1 + o['w'], np.ones_like(l), 1 - o['w']], -1)
    shadow = _contact_shadow(A)
    alb = mix3(dalb * (1 - 0.5 * shadow)[..., None], gal, A)
    h = dh * (1 - A) + (o['h'] + 1.0) * A
    sm = dsm * (1 - A) + 0.26 * A
    return finish(alb, h, sm, tint=A, ao=1 - shadow * 0.8)


def snowy_grass_side():
    n = T
    dalb, dh, dsm = dirt_base()
    ss = 2
    N = n * ss
    X, Y = np.meshgrid(np.arange(N) + 0.5, np.arange(N) + 0.5)
    rng = np.random.default_rng(41)
    # a few frosted dry blades peek out under the snow line
    prof = _profile1d(n, 42, 22.0, 4.0, beta=2.6)
    # rounded lumps along the lower edge
    lumps = np.zeros(n)
    xs = np.arange(n) + 0.5
    for _ in range(9):
        c = rng.random() * n
        r = rng.uniform(4, 9)
        d = np.abs((xs - c + n / 2) % n - n / 2)
        lumps = np.maximum(lumps, np.sqrt(np.clip(1 - (d / r) ** 2, 0, 1)) * rng.uniform(1.5, 4.0))
    prof = prof + lumps
    prof2 = np.repeat(prof, ss) * ss
    cv = Canvas(n, ss=ss, wrap='x', c=(0, 0, 0), h=0.0, s=0.0)
    for i in range(36):
        x0 = rng.random() * N
        top = np.interp(x0, np.arange(N) + 0.5, prof2, period=N)
        y0 = top - rng.uniform(1, 4) * ss
        L = rng.uniform(3, 10) * ss
        ang = np.pi / 2 + rng.normal(0, 0.45)
        p0 = np.array([x0, y0])
        p2 = p0 + np.array([np.cos(ang), np.sin(ang)]) * L
        c0 = col('#8b8466') * rng.uniform(0.85, 1.1)
        polyline_capsules(cv, [p0, (p0 + p2) / 2, p2], 1.1 * ss, 0.2 * ss,
                          lambda tg, dn, X2, Y2, c0=c0: dict(c=c0 * (1 - 0.15 * tg)[..., None], h=1.2, s=0.25))
    # the snow body
    edge = prof2[None, :] - Y
    cov = np.clip(edge + 0.5, 0, 1)
    fine = fbm(N, 43, beta=2.0)
    t = 0.55 + 0.18 * fine + 0.25 * smoothstep(0, 6 * ss, edge)
    snow_c = ramp(t, [(0.0, '#aebccc'), (0.45, '#cdd7e1'), (0.8, '#dfe6ec'), (1.0, '#e7ecf0')])
    lip = np.sqrt(np.clip(edge / (5.0 * ss), 0, 1))
    snow_h = 1.6 + 1.8 * lip + 0.35 * fine
    cv.paint_full(cov, c=snow_c, h=snow_h, s=0.42)
    A, o = cv.resolve()
    spark = _speckle(n, 44, 0.012) * A
    sc = mix3(o['c'], col('#eef2f5'), spark * 0.8)
    shadow = _contact_shadow(A, drop=2.5, sigma=2.8, strength=0.5)
    alb = mix3(dalb * (1 - 0.5 * shadow)[..., None], sc, A)
    h = dh * (1 - A) + (o['h'] + 0.6) * A
    sm = dsm * (1 - A) + (o['s'] + 0.35 * spark) * A
    return finish(alb, h, sm, ao=1 - shadow * 0.8)


# ---------------------------------------------------------------- podzol ---
def _litter(cv, rng, count_needles, count_leaves, count_twigs, count_moss, ss, region=None):
    N = cv.N

    def rpos():
        if region is None:
            return rng.random(2) * N
        x = rng.random() * N
        y = rng.uniform(region[0], region[1]) * ss
        return np.array([x, y])
    needle_cols = ['#7a5232', '#8a5d36', '#6a4529', '#6e6052', '#4f3626', '#946640', '#5e4a3a']
    items = []
    for _ in range(count_needles):
        items.append(('n', rng.random()))
    for _ in range(count_leaves):
        items.append(('l', rng.random()))
    for _ in range(count_twigs):
        items.append(('t', rng.random() * 0.6))
    for _ in range(count_moss):
        items.append(('m', rng.random() * 0.4))
    items.sort(key=lambda it: it[1])
    for kind, z in items:
        p = rpos()
        if kind == 'n':
            ang = rng.uniform(0, np.pi)
            L = rng.uniform(5, 11) * ss
            d = np.array([np.cos(ang), np.sin(ang)]) * L / 2
            c0 = col(needle_cols[rng.integers(len(needle_cols))]) * rng.uniform(0.88, 1.1)
            polyline_capsules(cv, [p - d, p + d], 0.6 * ss, 0.45 * ss,
                              lambda tg, dn, X, Y, c0=c0, z=z: dict(c=c0 * (1 - 0.18 * dn)[..., None], h=0.5 + 1.2 * z + 0.3 * (1 - dn), s=0.3))
        elif kind == 'l':
            a = rng.uniform(2.5, 5.0) * ss
            b = a * rng.uniform(0.45, 0.65)
            ang = rng.uniform(0, np.pi)
            X, Y, idx = cv.region(p[0], p[1], a + 2)
            dd, u, v, k = sd_ellipse(X, Y, p[0], p[1], a, b, ang)
            c0 = col(['#6e4a2c', '#7f5834', '#5e4029', '#875f3a'][rng.integers(4)])
            vein = np.exp(-(v / (0.35 * ss)) ** 2) * 0.12
            cv.paint(idx, cov_from_sdf(dd), c=c0 * (0.92 + vein + 0.06 * u / a)[..., None],
                     h=0.6 + 1.2 * z + 0.4 * np.sqrt(np.clip(1 - k * k, 0, 1)), s=0.28)
        elif kind == 't':
            ang = rng.uniform(0, 2 * np.pi)
            L = rng.uniform(12, 28) * ss
            pts = [p]
            for _ in range(4):
                ang += rng.normal(0, 0.3)
                pts.append(pts[-1] + np.array([np.cos(ang), np.sin(ang)]) * L / 4)
            c0 = col('#6b5a48') * rng.uniform(0.85, 1.1)
            polyline_capsules(cv, pts, 1.15 * ss, 0.7 * ss,
                              lambda tg, dn, X, Y, c0=c0, z=z: dict(c=c0 * (1 - 0.25 * dn * dn + 0.08 * (1 - dn))[..., None], h=1.0 + 1.5 * z + 0.8 * np.sqrt(np.clip(1 - dn * dn, 0, 1)), s=0.25))
        else:
            r = rng.uniform(1.5, 3.5) * ss
            X, Y, idx = cv.region(p[0], p[1], r + 2)
            dd = np.hypot(X - p[0], Y - p[1]) - r
            fz = np.random.default_rng(int(z * 1e6)).random(dd.shape)
            c0 = mix3(col('#4f5e2c'), col('#7b8a42'), fz)
            cv.paint(idx, cov_from_sdf(dd) * (0.6 + 0.4 * fz), c=c0, h=0.6 + 0.6 * fz, s=0.2)


def podzol_top():
    n = T
    ss = 2
    N = n * ss
    f = fbm(N, 51, beta=2.4)
    g = fbm(N, 52, beta=1.4)
    base = ramp(0.5 + 0.22 * f + 0.08 * g, [(0, '#2f2117'), (0.4, '#3f2c1d'), (0.7, '#553b25'), (1, '#6a4a2c')])
    cv = Canvas(n, ss=ss, wrap=True, c=(0, 0, 0), h=0.0, s=0.0)
    cv.fill(c=base, h=0.4 * f, s=0.15)
    rng = np.random.default_rng(53)
    _litter(cv, rng, 780, 55, 12, 22, ss)
    A, o = cv.resolve()
    return finish(o['c'], o['h'], o['s'], cavity=0.11)


def podzol_side():
    n = T
    dalb, dh, dsm = dirt_base()
    ss = 2
    N = n * ss
    X, Y = np.meshgrid(np.arange(N) + 0.5, np.arange(N) + 0.5)
    prof = _profile1d(n, 61, 13.0, 4.0)
    prof2 = np.repeat(prof, ss) * ss
    cv = Canvas(n, ss=ss, wrap='x', c=(0, 0, 0), h=0.0, s=0.0)
    f = fbm(N, 62, beta=2.0)
    hum = ramp(0.5 + 0.25 * f, [(0, '#261a12'), (0.5, '#33241a'), (1, '#433022')])
    cov = np.clip(prof2[None, :] - Y + 0.5, 0, 1)
    cv.paint_full(cov, c=hum, h=1.0 + 0.3 * f, s=0.15)
    rng = np.random.default_rng(63)
    _litter(cv, rng, 420, 14, 4, 8, ss, region=(0.0, 12.0))
    # needles dangling over the edge
    for _ in range(115):
        x0 = rng.random() * N
        top = np.interp(x0, np.arange(N) + 0.5, prof2, period=N)
        y0 = top - rng.uniform(0, 3) * ss
        ang = np.pi / 2 + rng.normal(0, 0.6)
        L = rng.uniform(4, 10) * ss
        p0 = np.array([x0, y0])
        p1 = p0 + np.array([np.cos(ang), np.sin(ang)]) * L
        c0 = col(['#8e5a2f', '#a26a38', '#6a4528'][rng.integers(3)])
        polyline_capsules(cv, [p0, p1], 0.6 * ss, 0.4 * ss,
                          lambda tg, dn, X2, Y2, c0=c0: dict(c=c0 * (1 - 0.15 * dn)[..., None], h=1.5, s=0.3))
    A, o = cv.resolve()
    shadow = _contact_shadow(A, drop=2.0, sigma=2.4, strength=0.5)
    alb = mix3(dalb * (1 - 0.5 * shadow)[..., None], o['c'], A)
    h = dh * (1 - A) + (o['h'] + 0.8) * A
    sm = dsm * (1 - A) + o['s'] * A
    return finish(alb, h, sm, ao=1 - shadow * 0.8)


# ----------------------------------------------------------------- stone ---
@lru_cache(None)
def stone_fields(seed=71):
    n = T
    X, Y = grid(n)
    wx = fbm(n, seed + 1, beta=3.2) * 3.5
    wy = fbm(n, seed + 2, beta=3.2) * 3.5
    broad = warp(fbm(n, seed + 3, beta=2.6), wx, wy)
    fine = fbm(n, seed + 4, beta=1.3)
    pts = poisson(n, 25, seed + 5)
    w = worley(n, pts, qdx=wx * 1.4, qdy=wy * 1.4)
    rng = np.random.default_rng(seed + 6)
    k = len(pts)
    gx = rng.normal(0, 0.09, k)
    gy = rng.normal(0, 0.09, k)
    hk = rng.normal(0, 0.6, k)
    tone = rng.normal(0, 0.035, k)
    facet = hk[w['id']] + gx[w['id']] * (X + wx * 1.4 - w['cx']) + gy[w['id']] * (Y + wy * 1.4 - w['cy'])
    facet = blur(facet, 0.7)
    pair = np.vectorize(lambda a, b: hash01(min(a, b), max(a, b), seed))(w['id'], w['id2'])
    crack_w = 0.55 + 0.45 * smoothstep(-0.5, 1.0, fbm(n, seed + 7, beta=2.5))
    crack = (1 - smoothstep(0.1, crack_w + 0.15, w['E'])) * (pair < 0.2)
    crack = np.clip(crack, 0, 1)
    # a second, finer set of chisel facets
    w2 = worley(n, poisson(n, 11, seed + 11), qdx=wx * 0.7, qdy=wy * 0.7)
    k2 = w2['id'].max() + 1
    g2x = rng.normal(0, 0.1, k2)[w2['id']]
    g2y = rng.normal(0, 0.1, k2)[w2['id']]
    facet2 = blur(g2x * (X + wx * 0.7 - w2['cx']) + g2y * (Y + wy * 0.7 - w2['cy']), 0.6)
    mid = band(n, seed + 12, 9, 4)
    strata = np.sin(2 * np.pi * (2 * Y / n + 1 * X / n) + 2.2 * fbm(n, seed + 13, beta=3.0))
    h = 1.5 * facet + 0.45 * facet2 + 0.9 * broad + 0.18 * blur(mid, 0.6) + 0.1 * fine - 1.1 * crack
    t = (0.52 + 0.15 * broad + 0.09 * fine + 0.05 * mid + tone[w['id']] + 0.04 * facet
         + 0.03 * facet2 + 0.025 * strata)
    alb = ramp(t, [(0.0, '#4e4d4b'), (0.3, '#646361'), (0.55, '#787773'), (0.8, '#8d8b86'), (1.0, '#a3a09a')])
    warm = fbm(n, seed + 8, beta=3.0)
    alb = alb * (1 + 0.03 * warm[..., None] * np.array([1.0, 0.2, -1.0]))
    dk = _speckle(n, seed + 9, 0.022)
    lt = _speckle(n, seed + 10, 0.015)
    alb = mix3(alb, col('#424140'), dk * 0.6)
    alb = mix3(alb, col('#b3afa6'), lt * 0.55)
    alb = alb * (1 - 0.3 * crack)[..., None]
    sm = 0.24 + 0.1 * lt - 0.08 * crack
    return alb, h, sm, crack


def stone():
    alb, h, sm, _ = stone_fields()
    return finish(alb, h, sm, nstrength=0.9, cavity=0.10)


# ------------------------------------------------------------ cobblestone ---
@lru_cache(None)
def cobble_fields(seed=81):
    n = T
    X, Y = grid(n)
    pts, rads = poisson(n, 0.86, seed, radii=lambda r: r.uniform(13.5, 20.0))
    wx = fbm(n, seed + 1, beta=3.0) * 2.6
    wy = fbm(n, seed + 2, beta=3.0) * 2.6
    w = worley(n, pts, qdx=wx, qdy=wy)
    g = 1.55 + 0.55 * smoothstep(-1, 1.5, fbm(n, seed + 3, beta=2.2))
    s = w['E'] - g
    m = smoothstep(-0.6, 0.6, s)
    prof = np.sqrt(np.clip(1 - (1 - np.clip(s / 6.5, 0, 1)) ** 2, 0, 1))
    rk = rads[w['id']]
    dx = X + wx - w['cx']
    dy = Y + wy - w['cy']
    r2 = (dx * dx + dy * dy) / (rk * 1.25) ** 2
    dome = np.clip(1 - r2, 0, 1)
    rng = np.random.default_rng(seed + 4)
    k = len(pts)
    tilt_x = rng.normal(0, 0.05, k)[w['id']]
    tilt_y = rng.normal(0, 0.05, k)[w['id']]
    det = fbm(n, seed + 5, beta=2.2)
    fine = fbm(n, seed + 6, beta=1.3)
    stone_h = 3.0 * prof + 1.3 * dome + 0.45 * det + 0.15 * fine + tilt_x * dx + tilt_y * dy
    grit = fbm(n, seed + 7, beta=1.2)
    mortar_h = -0.4 + 0.3 * grit
    h = mortar_h * (1 - m) + stone_h * m
    pal = [col(c) for c in ['#6d6c68', '#7e7c77', '#8f8c85', '#75726c', '#62615e', '#7c7166', '#6d7277', '#86837d', '#9a968e']]
    pick = np.array([pal[int(hash01(i, seed) * len(pal))] for i in range(k)])
    jit = rng.normal(0, 0.03, k)
    base = pick[w['id']] * (1 + jit[w['id']])[..., None]
    sv = 0.93 + 0.09 * det + 0.05 * fine + 0.05 * dome
    stone_c = base * sv[..., None]
    rim = 1 - smoothstep(0.0, 2.5, s)
    stone_c = stone_c * (1 - 0.12 * rim)[..., None]
    lt = _speckle(n, seed + 8, 0.02)
    dk = _speckle(n, seed + 9, 0.025)
    stone_c = mix3(stone_c, col('#b1ada4'), lt * 0.45)
    stone_c = mix3(stone_c, col('#45433f'), dk * 0.45)
    mortar_c = ramp(0.5 + 0.3 * grit, [(0, '#3f3b35'), (0.5, '#4d4840'), (1, '#5f594f')])
    deep = smoothstep(0.5, -1.5, s)
    mortar_c = mortar_c * (1 - 0.18 * deep)[..., None]
    alb = mix3(mortar_c, stone_c, m)
    sm = 0.13 * (1 - m) + (0.28 + 0.06 * dome + 0.08 * lt) * m
    return dict(alb=alb, h=h, sm=sm, m=m, s=s, id=w['id'], dy=dy, rk=rk, X=X, Y=Y)


def cobblestone():
    f = cobble_fields()
    return finish(f['alb'], f['h'], f['sm'], cavity=0.10, bevel_d=1.3)


def mossy_cobblestone():
    f = cobble_fields()
    n = T
    m = f['m']
    mn1 = fbm(n, 91, beta=2.4)
    mn2 = fbm(n, 92, beta=2.6)
    hf = fbm(n, 93, beta=0.9)
    upper = np.clip(-f['dy'] / f['rk'], -1, 1)
    in_mortar = (1 - m) * smoothstep(-1.0, 0.0, mn1)
    on_stone = m * smoothstep(0.15, 0.75, 0.55 * mn2 + 0.55 * upper - 0.25 + 0.45 * smoothstep(3, 0, f['s']))
    moss = np.maximum(in_mortar, on_stone)
    mA = smoothstep(0.3, 0.56, moss + 0.12 * hf)
    fz = fbm(n, 94, beta=1.5)
    mc = ramp(0.5 + 0.2 * fz + 0.1 * mn2 + 0.1 * (mA - 0.5), [(0, '#29391b'), (0.4, '#3b5324'), (0.7, '#516b2d'), (1, '#68823a')])
    tips = _speckle(n, 95, 0.02) * mA
    mc = mix3(mc, col('#869446'), tips * 0.35)
    stain = np.clip(blur(mA, 2.5) * 1.3 - mA, 0, 1)
    base = f['alb'] * (1 - stain[..., None] * np.array([0.18, 0.08, 0.25]))
    alb = mix3(base, mc, mA)
    h = f['h'] + mA * (0.9 + 0.45 * fz + 0.3 * tips)
    sm = f['sm'] * (1 - mA) + 0.17 * mA
    return finish(alb, h, sm, cavity=0.10, bevel_d=1.3)


# --------------------------------------------------------------- bedrock ---
def bedrock():
    n = T
    seed = 101
    X, Y = grid(n)
    wx = fbm(n, seed + 1, beta=3.0) * 4
    wy = fbm(n, seed + 2, beta=3.0) * 4
    c1 = worley(n, poisson(n, 26, seed + 3), qdx=wx, qdy=wy)
    c2 = worley(n, poisson(n, 11, seed + 4), qdx=wx * 0.6, qdy=wy * 0.6)
    rng = np.random.default_rng(seed + 5)
    k1 = c1['id'].max() + 1
    k2 = c2['id'].max() + 1
    lev1 = rng.normal(0, 1, k1)[c1['id']]
    lev2 = rng.normal(0, 1, k2)[c2['id']]
    ridge = 1 - np.abs(fbm(n, seed + 6, beta=2.2))
    blocky = 1.4 * lev1 * smoothstep(0, 2.0, c1['E']) + 1.0 * lev2 * smoothstep(0, 1.5, c2['E'])
    pits = (1 - smoothstep(0.0, 2.0, c2['E'])) * 1.6 + (1 - smoothstep(0, 1.2, c1['E'])) * 1.0
    h = blocky + 0.9 * ridge + 0.3 * fbm(n, seed + 7, beta=1.3) - pits
    t = 0.45 + 0.17 * blocky / 1.5 + 0.12 * ridge + 0.05 * fbm(n, seed + 8, beta=1.2) - 0.2 * pits / 1.6
    alb = ramp(t, [(0, '#2a2928'), (0.25, '#353331'), (0.5, '#484543'), (0.72, '#5e5a56'), (0.88, '#77726b'), (1, '#8c867e')])
    streak = _speckle(n, seed + 9, 0.02, sigma=0.6)
    alb = mix3(alb, col('#8f897f'), streak * 0.5)
    sm = 0.2 + 0.08 * ridge
    return finish(alb, h, sm, nstrength=1.1, cavity=0.12, bevel_d=1.4)


# ------------------------------------------------------------------ sand ---
@lru_cache(None)
def sand_fields(seed=111):
    n = T
    X, Y = grid(n)
    mid = fbm(n, seed + 1, beta=2.4)
    fine = blur(np.random.default_rng(seed + 2).standard_normal((n, n)), 0.55)
    fine = fine / fine.std()
    wv = fbm(n, seed + 3, beta=3.2)
    ph = 2 * np.pi * (5 * Y / n + 1 * X / n) + 1.4 * wv
    rip = (np.sin(ph) + 1) / 2
    rip = rip ** 1.6
    h = 0.75 * rip + 0.35 * mid + 0.18 * fine
    t = 0.56 + 0.12 * mid + 0.07 * fine + 0.08 * (rip - 0.4)
    alb = ramp(t, [(0, '#b19a70'), (0.4, '#c6b088'), (0.7, '#d4c19a'), (1, '#e0cfab')])
    dk = _speckle(n, seed + 4, 0.035)
    mn = _speckle(n, seed + 5, 0.008)
    qz = _speckle(n, seed + 6, 0.02)
    alb = mix3(alb, col('#8f7a5c'), dk * 0.5)
    alb = mix3(alb, col('#6e6352'), mn * 0.45)
    alb = mix3(alb, col('#ece2c8'), qz * 0.6)
    sm = 0.14 + 0.35 * qz
    return alb, h, sm


def sand():
    alb, h, sm = sand_fields()
    return finish(alb, h, sm, nstrength=1.0, cavity=0.06, bevel_d=1.2)


def sandstone_side():
    n = T
    seed = 121
    X, Y = grid(n)
    rng = np.random.default_rng(seed)
    und = fbm(n, seed + 1, beta=3.0, ax=0.25) * 2.2
    yy = (Y + und) % n
    th = np.array([26, 18, 30, 14, 22, 18], float)
    th = th / th.sum() * n
    bounds = np.concatenate([[0], np.cumsum(th)])
    idx = np.clip(np.searchsorted(bounds, yy, side='right') - 1, 0, len(th) - 1)
    d_top = yy - bounds[idx]
    d_bot = bounds[idx + 1] - yy
    dist = np.minimum(d_top, d_bot)
    hard = np.array([1.0, 0.35, 0.9, 0.2, 0.75, 0.45])[idx]
    tones = np.array([col(c) for c in ['#d2b682', '#c8a874', '#dcc392', '#bf9f6c', '#d6bc8a', '#cbad7b']])
    fine = fbm(n, seed + 2, beta=1.3)
    grain = fbm(n, seed + 3, beta=1.0, ax=0.35)
    lip = np.sqrt(np.clip(1 - (1 - np.clip(dist / 3.2, 0, 1)) ** 2, 0, 1))
    h = 1.6 * hard * lip + (1 - hard) * (0.35 * lip) + 0.25 * fine + 0.12 * grain
    xb = 0.5 + 0.5 * np.sin(2 * np.pi * (3 * X / n - 2 * Y / n) + 2.0 * fbm(n, seed + 4, beta=3))
    xbed = smoothstep(0.92, 1.0, xb) * (1 - hard) * 0.5
    alb = tones[idx] * (1 + 0.05 * fine + 0.03 * grain - 0.05 * xbed)[..., None]
    line = 1 - smoothstep(0.0, 1.3, dist)
    alb = mix3(alb, col('#a9875a'), line * 0.45)
    alb = alb * (1 - 0.08 * (1 - lip) * (1 - hard))[..., None]
    pits = _speckle(n, seed + 5, 0.015, sigma=0.7)
    alb = mix3(alb, col('#a68660'), pits * 0.5)
    h = h - pits * 0.8
    sp = _speckle(n, seed + 6, 0.03)
    alb = mix3(alb, col('#e6d6b2'), sp * 0.4)
    sm = 0.17 + 0.03 * hard
    return finish(np.clip(alb, 0, 0.9), h, sm, cavity=0.09, bevel_d=1.4)


def sandstone_top():
    n = T
    seed = 131
    X, Y = grid(n)
    mott = fbm(n, seed, beta=2.8)
    fine = fbm(n, seed + 1, beta=1.3)
    alb = ramp(0.55 + 0.14 * mott + 0.06 * fine, [(0, '#bf9f6d'), (0.5, '#d2b784'), (1, '#e0caa0')])
    pts = poisson(n, 24, seed + 2)
    wx = fbm(n, seed + 3, beta=3) * 2
    w = worley(n, pts, qdx=wx, qdy=-wx)
    pair = np.vectorize(lambda a, b: hash01(min(a, b), max(a, b), seed))(w['id'], w['id2'])
    crack = (1 - smoothstep(0.1, 0.8, w['E'])) * (pair < 0.3)
    pits = _speckle(n, seed + 4, 0.012, sigma=0.8)
    sp = _speckle(n, seed + 5, 0.03)
    alb = mix3(alb, col('#a8885e'), crack * 0.5 + pits * 0.5)
    alb = mix3(alb, col('#ead9b6'), sp * 0.35)
    # a softly incised border, like a dressed block face
    D = np.minimum(np.minimum(X, n - X), np.minimum(Y, n - Y))
    groove = np.exp(-((D - 10.5) / 1.1) ** 2)
    alb = alb * (1 - 0.12 * groove)[..., None]
    h = 0.5 * mott + 0.15 * fine - 1.0 * crack - 0.9 * pits - 1.1 * groove + 0.3 * smoothstep(9, 13, D)
    sm = 0.2 + 0.05 * mott
    return finish(alb, h, sm, cavity=0.09, bevel_d=1.3)


# ---------------------------------------------------------------- gravel ---
def gravel():
    n = T
    seed = 141
    ss = 2
    N = n * ss
    grit = fbm(N, seed, beta=1.2)
    cv = Canvas(n, ss=ss, wrap=True, c=(0, 0, 0), h=0.0, s=0.0)
    cv.fill(c=ramp(0.5 + 0.3 * grit, [(0, '#3a3631'), (0.5, '#4a453e'), (1, '#5a544b')]), h=-1.5 + 0.3 * grit, s=0.1)
    rng = np.random.default_rng(seed + 1)
    pal = ['#8b867e', '#77726b', '#9d978e', '#615c56', '#8c7b68', '#7b6b5a', '#a9a196', '#575350',
           '#b4ad9f', '#6e6a64', '#85796c', '#938d82']
    speck = fbm(N, seed + 2, beta=0.8)
    count = 430
    zs = np.sort(rng.random(count))
    for z in zs:
        cx, cy = rng.random(2) * N
        a = (2.4 + 5.0 * rng.random() ** 1.5) * ss
        b = a * rng.uniform(0.55, 0.92)
        ang = rng.uniform(0, np.pi)
        X, Y, idx = cv.region(cx, cy, a * 1.3 + 2)
        d, k = _lumpy_ellipse(X, Y, cx, cy, a, b, ang, rng.uniform(0, 6.3, 3), amt=0.12)
        dome = np.sqrt(np.clip(1 - k * k, 0, 1))
        c0 = col(pal[rng.integers(len(pal))]) * rng.uniform(0.9, 1.08)
        sp = speck[idx[0][:, None], idx[1][None, :]]
        cc = c0 * (0.9 + 0.07 * sp + 0.08 * dome)[..., None]
        cc = cc * (1 - 0.15 * smoothstep(0.75, 1.0, k))[..., None]
        cv.paint(idx, cov_from_sdf(d), c=cc, h=1.2 * z + 2.2 * dome * (b / ss) / 6.0 + 0.2 * sp, s=0.24 + 0.12 * dome)
    A, o = cv.resolve()
    return finish(o['c'], o['h'], o['s'], nstrength=1.0, cavity=0.12, bevel_d=1.3)


# ------------------------------------------------------------------ clay ---
def clay():
    n = T
    seed = 151
    f = fbm(n, seed, beta=2.6)
    sme = fbm(n, seed + 1, beta=2.0, ax=0.12)
    fine = fbm(n, seed + 2, beta=1.2)
    X, Y = grid(n)
    layers_ = np.sin(2 * np.pi * (6 * Y / n) + 1.6 * fbm(n, seed + 6, beta=3.0, ax=0.3))
    alb = ramp(0.5 + 0.15 * f + 0.06 * sme + 0.04 * fine + 0.05 * layers_,
               [(0, '#6d7680'), (0.5, '#848d96'), (1, '#9aa2a9')])
    mott = smoothstep(0.4, 1.4, fbm(n, seed + 7, beta=2.4))
    alb = mix3(alb, col('#8e8a80'), mott * 0.25)
    lt = _speckle(n, seed + 3, 0.02)
    dk = _speckle(n, seed + 4, 0.02)
    alb = mix3(alb, col('#b8bcbf'), lt * 0.4)
    alb = mix3(alb, col('#666d75'), dk * 0.4)
    hs = fbm(n, seed + 5, beta=3.0, fmax=9)
    h = 1.1 * hs + 0.3 * blur(sme, 1.5) + 0.25 * blur(layers_, 1.0) + 0.03 * fine - 0.25 * dk
    sm = 0.38 + 0.06 * f
    return finish(alb, h, sm, cavity=0.06, bevel_d=1.2, nstrength=0.8)


# ------------------------------------------------------------------ snow ---
def snow():
    n = T
    seed = 161
    f = fbm(n, seed, beta=3.2)
    fine = fbm(n, seed + 1, beta=1.8)
    drift = fbm(n, seed + 3, beta=3.5, fmax=7, ax=0.6)
    h = 2.2 * drift + 0.25 * fbm(n, seed + 4, beta=2.5, fmax=22) + 0.03 * fine
    alb = ramp(0.55 + 0.2 * f + 0.06 * fine, [(0, '#bccada'), (0.45, '#d4dde6'), (0.8, '#e1e7ed'), (1, '#e8edf1')])
    spark = _speckle(n, seed + 2, 0.012)
    alb = mix3(alb, col('#f1f4f6'), spark * 0.7)
    sm = 0.38 + 0.45 * spark
    return finish(alb, h, sm, cavity=0.05, bevel_d=1.3, nstrength=0.8)


# ------------------------------------------------------------------- ice ---
def ice():
    n = T
    seed = 171
    X, Y = grid(n)
    f = fbm(n, seed, beta=2.8)
    deep = fbm(n, seed + 1, beta=3.2)
    alb = ramp(0.5 + 0.16 * f - 0.12 * smoothstep(0.3, 1.5, deep), [(0, '#7ea2c2'), (0.5, '#9dbdd7'), (1, '#b9d3e6')])
    wx = fbm(n, seed + 2, beta=3) * 2.5
    pts = poisson(n, 26, seed + 3)
    w = worley(n, pts, qdx=wx, qdy=fbm(n, seed + 4, beta=3) * 2.5)
    pair = np.vectorize(lambda a, b: hash01(min(a, b), max(a, b), seed))(w['id'], w['id2'])
    crack = (1 - smoothstep(0.0, 0.9, w['E'])) * (pair < 0.55)
    glow = (1 - smoothstep(0.0, 4.0, w['E'])) * (pair < 0.55)
    # long straight fracture planes
    fr = np.zeros((n, n))
    rng = np.random.default_rng(seed + 5)
    for _ in range(3):
        a = rng.uniform(0, np.pi)
        kx, ky = int(round(np.cos(a) * 2)), int(round(np.sin(a) * 2))
        if kx == 0 and ky == 0:
            kx = 1
        ph = (kx * X + ky * Y) / n + rng.random()
        d = np.abs((ph % 1.0) - 0.5) * n / np.hypot(kx, ky)
        seg = smoothstep(0.2, 0.7, fbm(n, int(rng.integers(1e6)), beta=3.0))
        fr = np.maximum(fr, (1 - smoothstep(0.0, 0.8, d)) * seg)
    alb = mix3(alb, col('#cfe2ef'), glow * 0.25)
    alb = mix3(alb, col('#e3eef5'), np.clip(crack + fr * 0.8, 0, 1) * 0.75)
    # trapped air bubbles
    bub = np.zeros((n, n))
    for _ in range(26):
        cx, cy = rng.random(2) * n
        r = rng.uniform(0.7, 1.9)
        dx = (X - cx + n / 2) % n - n / 2
        dy = (Y - cy + n / 2) % n - n / 2
        d = np.hypot(dx, dy)
        bub = np.maximum(bub, np.exp(-((d - r) / 0.45) ** 2))
    alb = mix3(alb, col('#e9f2f7'), bub * 0.7)
    scratch = np.zeros((n, n))
    for _ in range(14):
        a = rng.uniform(0, np.pi)
        cx, cy = rng.random(2) * n
        L = rng.uniform(8, 26)
        dx = (X - cx + n / 2) % n - n / 2
        dy = (Y - cy + n / 2) % n - n / 2
        u = dx * np.cos(a) + dy * np.sin(a)
        v = -dx * np.sin(a) + dy * np.cos(a)
        scratch = np.maximum(scratch, np.exp(-(v / 0.35) ** 2) * (np.abs(u) < L / 2))
    h = 0.25 * f - 0.6 * crack - 0.3 * scratch - 0.3 * fr
    sm = 0.88 - 0.18 * scratch - 0.1 * crack
    return finish(alb, h, sm, nstrength=0.9, cavity=0.04, bevel_d=1.1, edge_ao=0.08)


# ----------------------------------------------------------------- water ---
def water_still():
    n = T
    seed = 181
    X, Y = grid(n)
    wx = fbm(n, seed, beta=3.2) * 4
    wy = fbm(n, seed + 1, beta=3.2) * 4
    w = worley(n, poisson(n, 22, seed + 2), qdx=wx, qdy=wy)
    lines = 1 - smoothstep(0.0, 3.0, w['F2'] - w['F1'])
    f = fbm(n, seed + 3, beta=2.6)
    alb = ramp(0.5 + 0.2 * f, [(0, '#22507a'), (0.5, '#2c6290'), (1, '#3a76a3')])
    alb = mix3(alb, col('#6fa6cf'), lines * 0.55)
    h = 0.8 * f + 0.5 * lines
    return finish(alb, h, 0.95, bevel=False, nstrength=0.8, cavity=0.0)
