"""Ores, cactus, workbench, furnace and glass."""
import numpy as np

from mat_terrain import stone_fields
from mat_wood import wood_grain
from texlib import (TILE, Canvas, blur, col, cov_from_sdf, edge_distance, fbm, finish, grid, hash01,
                    local_uv, mix3, poisson, polyline_capsules, ramp, smoothstep, spectral, worley,
                    wrap_delta)

T = TILE


def _speck(n, seed, frac):
    w = np.random.default_rng(seed).random((n, n))
    thr = np.quantile(w, 1.0 - frac)
    return smoothstep(thr, thr + (1 - thr) * 0.4, w)


def sd_box(X, Y, cx, cy, hw, hh, r=0.0, ang=0.0):
    u, v = local_uv(X, Y, cx, cy, ang)
    qx = np.abs(u) - hw + r
    qy = np.abs(v) - hh + r
    out = np.hypot(np.maximum(qx, 0), np.maximum(qy, 0))
    return out + np.minimum(np.maximum(qx, qy), 0) - r


class Stack:
    """Full-tile layer compositor (albedo, height, smoothness, emission)."""

    def __init__(self, alb, h, sm, e=None):
        n = alb.shape[0]
        self.alb = alb.copy()
        self.h = h.copy()
        self.sm = np.broadcast_to(np.asarray(sm, float), (n, n)).copy()
        self.e = np.zeros((n, n)) if e is None else e.copy()

    def put(self, cov, c=None, h=None, s=None, e=None, hadd=None):
        cov = np.clip(cov, 0, 1)
        if c is not None:
            self.alb = mix3(self.alb, np.broadcast_to(np.asarray(c, float), self.alb.shape), cov)
        if h is not None:
            self.h = self.h * (1 - cov) + np.asarray(h, float) * cov
        if hadd is not None:
            self.h = self.h + np.asarray(hadd, float) * cov
        if s is not None:
            self.sm = self.sm * (1 - cov) + np.asarray(s, float) * cov
        if e is not None:
            self.e = self.e * (1 - cov) + np.asarray(e, float) * cov


# ------------------------------------------------------------------- ores ---
def _ore(seed, pal, rim_col, nug_smooth, clusters=6, size=(2.8, 6.0)):
    alb, h, sm, _ = stone_fields()
    n = T
    X, Y = grid(n)
    rng = np.random.default_rng(seed)
    centres = poisson(n, 30, seed, count=clusters)
    nug = np.zeros((n, n))
    dome = np.zeros((n, n))
    rough = fbm(n, seed + 1, beta=1.8)
    for (cx, cy) in centres:
        for b in range(int(rng.integers(4, 8))):
            r0 = rng.uniform(0, 9.0)
            a0 = rng.uniform(0, 2 * np.pi)
            bx, by = cx + np.cos(a0) * r0, cy + np.sin(a0) * r0
            a = rng.uniform(*size)
            bb = a * rng.uniform(0.6, 0.95)
            ang = rng.uniform(0, np.pi)
            dx = wrap_delta(X - bx, n)
            dy = wrap_delta(Y - by, n)
            u = dx * np.cos(ang) + dy * np.sin(ang)
            v = -dx * np.sin(ang) + dy * np.cos(ang)
            k = np.sqrt((u / a) ** 2 + (v / bb) ** 2) * (1 + 0.12 * rough)
            cov = cov_from_sdf((k - 1) * bb)
            dm = np.sqrt(np.clip(1 - k * k, 0, 1)) * bb * 0.55
            nug = np.maximum(nug, cov)
            dome = np.maximum(dome, dm)
    # crystalline facets inside nuggets
    fw = worley(n, poisson(n, 4.2, seed + 2), qdx=rough * 0.8)
    kf = fw['id'].max() + 1
    gx = rng.normal(0, 0.35, kf)[fw['id']]
    gy = rng.normal(0, 0.35, kf)[fw['id']]
    plane = gx * (X - fw['cx']) + gy * (Y - fw['cy'])
    ftone = rng.normal(0, 0.12, kf)[fw['id']] + 0.25 * (gy - gx)
    t = 0.5 + ftone + 0.18 * (dome / (dome.max() + 1e-6)) + 0.05 * rough
    nug_c = ramp(t, pal)
    glint = smoothstep(0.82, 0.95, t) * nug
    halo = np.clip(blur(nug, 1.3) * 1.8 - nug, 0, 1)
    out = mix3(alb, col(rim_col), halo * 0.55)
    out = mix3(out, nug_c, nug)
    hh = h * (1 - nug * 0.6) + nug * (1.4 + dome + 0.25 * plane) - halo * 0.5
    ss = sm * (1 - nug) + nug * (nug_smooth + 0.15 * glint)
    return finish(out, hh, ss, nstrength=1.0, cavity=0.10)


def coal_ore():
    return _ore(601, [(0, '#232226'), (0.45, '#303034'), (0.75, '#3f3f45'), (1, '#5b5b63')], '#38363a', 0.5)


def iron_ore():
    return _ore(611, [(0, '#8a5638'), (0.35, '#b07a57'), (0.7, '#d29e7a'), (1, '#ebc6a2')], '#7c4c33', 0.5)


def gold_ore():
    return _ore(621, [(0, '#94641a'), (0.35, '#c48f27'), (0.7, '#e6bc44'), (1, '#f7de8c')], '#7e6034', 0.66,
                clusters=5, size=(2.4, 5.2))


# ----------------------------------------------------------------- cactus ---
CACTUS = [(0, '#28441c'), (0.35, '#3a6327'), (0.65, '#4f7f35'), (0.88, '#649545'), (1, '#77a656')]


def _areole(cv, rng, x, y, ss, spines=4, base_ang=-np.pi / 2):
    X, Y, idx = cv.region(x, y, 3.2 * ss)
    d = np.hypot(X - x, Y - y)
    fz = rng.random(d.shape)
    wool = mix3(col('#b9ae8c'), col('#e2d9b8'), fz * (1 - np.clip(d / (2.6 * ss), 0, 1)))
    cv.paint(idx, cov_from_sdf(d - 2.5 * ss) * (0.8 + 0.2 * fz), c=wool, h=3.6 + 0.8 * (1 - d / (2.6 * ss)), s=0.2)
    for i in range(spines):
        a = base_ang + (i - (spines - 1) / 2) * 0.9 + rng.normal(0, 0.2)
        L = rng.uniform(5.5, 9.0) * ss
        p0 = np.array([x, y])
        p1 = p0 + np.array([np.cos(a), np.sin(a)]) * L
        polyline_capsules(cv, [p0, p1], 0.55 * ss, 0.18 * ss, lambda tg, dn, X2, Y2: dict(
            c=mix3(col('#efe6bc'), col('#a8915e'), tg) * (1 - 0.15 * dn)[..., None], h=4.5 - 0.5 * tg, s=0.4))


def cactus_side():
    n = T
    ss = 2
    N = n * ss
    X, Y = grid(N)
    ph = 2 * np.pi * (X / ss - 16) * 4 / n
    c = np.cos(ph)
    prof = (0.5 + 0.5 * c) ** 0.8
    streak = spectral(N, 701, beta=1.6, ax=1.0, ay=0.12)
    fine = fbm(N, 702, beta=1.2)
    t = 0.3 + 0.55 * prof + 0.05 * streak + 0.03 * fine
    base = ramp(t, CACTUS)
    stip = _speck(N, 703, 0.03)
    base = mix3(base, col('#86b166'), stip * 0.35)
    cv = Canvas(n, ss=ss, wrap=True, c=(0, 0, 0), h=0.0, s=0.0)
    cv.fill(c=base, h=3.0 * prof + 0.15 * streak, s=0.36 + 0.08 * prof)
    rng = np.random.default_rng(704)
    sp = n / 5
    for k in range(4):
        x = (16 + 32 * k) * ss
        for j in range(5):
            y = ((j + 0.5 * (k % 2)) * sp + 6) * ss
            _areole(cv, rng, x, y, ss)
    A, o = cv.resolve()
    return finish(o['c'], o['h'], o['s'], nstrength=1.0, cavity=0.1, bevel_d=1.2)


def cactus_top():
    n = T
    ss = 2
    N = n * ss
    X, Y = grid(N)
    dx, dy = X - N / 2, Y - N / 2
    r = np.hypot(dx, dy) / ss
    th = np.arctan2(dy, dx)
    ribs = 0.5 + 0.5 * np.cos(8 * th + 0.4)
    ribk = ribs ** 0.8 * smoothstep(4, 18, r)
    dome = 1 - np.clip(r / 70, 0, 1) ** 2
    t = 0.32 + 0.4 * ribk + 0.22 * (1 - smoothstep(0, 40, r)) + 0.04 * fbm(N, 711, beta=1.4)
    base = ramp(t, CACTUS)
    cv = Canvas(n, ss=ss, wrap=True, c=(0, 0, 0), h=0.0, s=0.0)
    cv.fill(c=base, h=2.2 * ribk + 2.0 * dome, s=0.38)
    rng = np.random.default_rng(712)
    for i in range(8):
        a = (2 * np.pi * i - 0.4) / 8
        for rr in (22, 38, 54):
            x = N / 2 + np.cos(a) * rr * ss
            y = N / 2 + np.sin(a) * rr * ss
            _areole(cv, rng, x, y, ss, spines=3, base_ang=a)
    X2, Y2, idx = cv.region(N / 2, N / 2, 12 * ss)
    d = np.hypot(X2 - N / 2, Y2 - N / 2)
    fz = rng.random(d.shape)
    wool = mix3(col('#bdb291'), col('#e6ddbd'), fz)
    cv.paint(idx, cov_from_sdf(d - 9 * ss) * (0.75 + 0.25 * fz), c=wool, h=5.0 + fz, s=0.2)
    for i in range(10):
        a = rng.uniform(0, 2 * np.pi)
        _areole(cv, rng, N / 2 + np.cos(a) * 4 * ss, N / 2 + np.sin(a) * 4 * ss, ss, spines=2, base_ang=a)
    A, o = cv.resolve()
    return finish(o['c'], o['h'], o['s'], nstrength=1.0, cavity=0.1, bevel_d=1.2)


# -------------------------------------------------------------- workbench ---
FRAME = [(0, '#3e2817'), (0.3, '#54371f'), (0.55, '#6a492a'), (0.8, '#7c5633'), (1, '#8b643e')]
PANEL = [(0, '#7a5a36'), (0.3, '#97734c'), (0.55, '#ab875c'), (0.8, '#bb9668'), (1, '#c8a476')]
WALNUT = '#4a3020'
STEEL = '#8f969b'
BRASS = '#c2a052'


def _rounded_edge(e, w=2.4):
    return np.sqrt(np.clip(1 - (1 - np.clip(e / w, 0, 1)) ** 2, 0, 1))


def workbench_top():
    n = T
    X, Y = grid(n)
    tx, latex, kmx, _ = wood_grain(n, 801, spacing=9.0, axis='x')
    ty, latey, kmy, _ = wood_grain(n, 802, spacing=9.0, axis='y')
    tp, latep, kmp, _ = wood_grain(n, 803, spacing=10.0, knots=[(84, 40, 2.6)], axis='x')
    fw = 11.0
    dxe = np.minimum(X, n - X)
    dye = np.minimum(Y, n - Y)
    in_frame = np.minimum(dxe, dye) < fw
    horiz = dye <= dxe
    frame_c = np.where(horiz[..., None], ramp(tx, FRAME), ramp(ty, FRAME))
    panel_c = ramp(tp + 0.04 * (Y > 64), PANEL)
    alb = np.where(in_frame[..., None], frame_c, panel_c)
    h = np.where(in_frame, 0.8 + 0.2 * np.where(horiz, 1 - latex, 1 - latey), 0.25 * (1 - latep) - 0.5 * kmp)
    # joints: frame/panel seam, mitres, panel board seam
    e_seam = np.abs(np.minimum(dxe, dye) - fw)
    e_mitre = np.abs(dxe - dye) / np.sqrt(2) + (np.minimum(dxe, dye) >= fw) * 99
    e_board = np.abs(Y - 64) + in_frame * 99
    e = np.minimum(np.minimum(e_seam, e_mitre), e_board)
    prof = _rounded_edge(e, 2.0)
    h = h + 1.2 * prof
    alb = alb * (0.78 + 0.22 * prof)[..., None]
    st = Stack(alb, h, 0.3)
    # carved 3x3 grid
    g0, g1 = 34.0, 94.0
    inside = (X > g0 - 2) & (X < g1 + 2) & (Y > g0 - 2) & (Y < g1 + 2)
    gd = np.full((n, n), 99.0)
    for v in np.linspace(g0, g1, 4):
        gd = np.minimum(gd, np.where((Y > g0 - 1) & (Y < g1 + 1), np.abs(X - v), 99))
        gd = np.minimum(gd, np.where((X > g0 - 1) & (X < g1 + 1), np.abs(Y - v), 99))
    groove = (1 - smoothstep(0.6, 1.5, gd)) * inside
    st.put(groove, c=st.alb * 0.55, hadd=-1.6)
    st.put(np.clip(blur(groove, 1.0) - groove, 0, 1) * 0.6, c=st.alb * 0.85)
    # brass set-square inlay (top-left)
    sq = np.minimum(sd_box(X, Y, 29, 18, 13, 2.2), sd_box(X, Y, 18, 29, 2.2, 13))
    outline = cov_from_sdf(np.abs(sq) - 0.6) * 0.8
    cov = cov_from_sdf(sq)
    shine = 0.5 + 0.5 * np.sin((X + Y) * 0.35)
    brass = mix3(col('#9c7c36'), col(BRASS), 0.6 + 0.4 * shine)
    st.put(outline, c=col('#2e1f14'), hadd=-0.5)
    st.put(cov, c=brass, h=st.h + 0.2, s=0.68)
    for i in range(5):
        tick = cov_from_sdf(sd_box(X, Y, 20 + 4.5 * (i + 1), 17.2, 0.35, 1.2))
        st.put(tick * cov, c=col('#6e5524'))
        tick = cov_from_sdf(sd_box(X, Y, 17.2, 20 + 4.5 * (i + 1), 1.2, 0.35))
        st.put(tick * cov, c=col('#6e5524'))
    # mallet inlay (bottom-right): walnut handle + steel head
    ang = -np.pi / 4
    hd = sd_box(X, Y, 101, 101, 11, 1.8, r=1.2, ang=ang)
    st.put(cov_from_sdf(np.abs(hd) - 0.5) * 0.8, c=col('#2e1f14'), hadd=-0.4)
    st.put(cov_from_sdf(hd), c=ramp(ty, [(0, '#3a2518'), (1, '#5a3c28')]), s=0.35)
    head = sd_box(X, Y, 93, 93, 3.4, 7.5, r=1.0, ang=ang)
    steel = mix3(col('#6c7377'), col('#b3babe'), 0.5 + 0.5 * np.sin((X - Y) * 0.4))
    st.put(cov_from_sdf(np.abs(head) - 0.5) * 0.8, c=col('#262220'), hadd=-0.4)
    st.put(cov_from_sdf(head), c=steel, s=0.6, hadd=0.3)
    # corner dowels (end grain)
    for (cx, cy) in [(5.5, 5.5), (n - 5.5, 5.5), (5.5, n - 5.5), (n - 5.5, n - 5.5)]:
        d = np.hypot(X - cx, Y - cy)
        ring = 0.5 + 0.5 * np.sin(d * 3.0)
        st.put(cov_from_sdf(d - 2.8), c=mix3(col('#a27a4e'), col('#c79f70'), ring), hadd=0.2)
        st.put(cov_from_sdf(np.abs(d - 2.8) - 0.4) * 0.7, c=col('#2e1f14'))
    return finish(st.alb, st.h, st.sm, nstrength=1.0, cavity=0.1, bevel_d=1.3)


def workbench_side():
    n = T
    X, Y = grid(n)
    tx, latex, _, _ = wood_grain(n, 811, spacing=9.0, axis='x')
    ty, latey, _, _ = wood_grain(n, 812, spacing=9.0, axis='y')
    tp, latep, _, _ = wood_grain(n, 813, spacing=8.0, axis='y')
    ttop, latet, _, _ = wood_grain(n, 814, spacing=10.0, axis='x')
    # recessed back boards (vertical)
    bw = n / 5
    xb = X % bw
    eb = np.minimum(xb, bw - xb)
    alb = ramp(tp - 0.12, PANEL) * (0.62 + 0.18 * _rounded_edge(eb, 1.8))[..., None]
    h = -1.5 + 0.8 * _rounded_edge(eb, 1.8)
    st = Stack(alb, h, 0.26)
    # pegs
    for px in (36, 86):
        d = np.hypot(X - px, Y - 31)
        st.put(cov_from_sdf(d - 2.2), c=mix3(col('#5d4128'), col('#8a6642'), 1 - d / 2.2), h=1.5, s=0.3)
    # saw: wooden handle with grip hole, steel blade with teeth
    hdl = sd_box(X, Y, 36, 40, 6.5, 6.0, r=2.5)
    hole = sd_box(X, Y, 36, 40.5, 3.2, 2.2, r=1.2)
    hdl = np.maximum(hdl, -hole)
    st.put(cov_from_sdf(hdl), c=ramp(ty, [(0, '#5a3a22'), (1, '#8a5e38')]), h=1.4 + 0.6 * _rounded_edge(-hdl, 1.5), s=0.35)
    yy = np.clip((Y - 46) / 46, 0, 1)
    halfw = 7.0 - 2.5 * yy
    teeth = 1.2 * np.abs(((Y - 46) / 2.4) % 1.0 - 0.5) * 2
    blade = np.maximum(np.maximum(-(X - 36) - halfw, (X - 36) - halfw + teeth * (X > 36)), np.maximum(46 - Y, Y - 92))
    steel = mix3(col('#7d8488'), col('#c3c9cc'), 0.35 + 0.4 * (1 - yy) * (0.5 + 0.5 * np.sin((X - 30) * 0.5 - Y * 0.08)))
    st.put(cov_from_sdf(blade), c=steel, h=0.8, s=0.6)
    for ry in (52, 60):
        d = np.hypot(X - 34, Y - ry)
        st.put(cov_from_sdf(d - 1.0), c=col('#5d5a55'), hadd=0.2)
    # hammer hanging head-up
    hd = sd_box(X, Y, 86, 66, 2.2, 24, r=1.5)
    st.put(cov_from_sdf(hd), c=ramp(ty, [(0, '#3a2518'), (1, '#5e402a')]), h=1.6 + 0.5 * _rounded_edge(-hd, 1.8), s=0.35)
    head = sd_box(X, Y, 86, 40, 10, 4.2, r=1.0)
    st.put(cov_from_sdf(head), c=mix3(col('#5f666a'), col('#aab1b5'), 0.5 + 0.5 * np.cos((Y - 36) * 0.5)),
           h=2.4 + 0.5 * _rounded_edge(-head, 1.5), s=0.6)
    # frame: legs, top board, apron, stretcher
    legs = (X < 13) | (X > n - 13)
    exl = np.where(X < 13, np.minimum(X, 13 - X), np.minimum(n - X, X - (n - 13)))
    top = Y < 13
    apron = (Y >= 13) & (Y < 24) & ~legs
    stretch = (Y >= 104) & (Y < 115) & ~legs
    under = (Y >= 115) & ~legs
    st.put(under.astype(float), c=ramp(tp - 0.3, PANEL) * 0.38, h=-2.5, s=0.2)
    st.put(stretch.astype(float), c=ramp(tx, FRAME) * (0.8 + 0.2 * _rounded_edge(np.minimum(Y - 104, 115 - Y), 2.0))[..., None],
           h=0.6 + 1.0 * _rounded_edge(np.minimum(Y - 104, 115 - Y), 2.0), s=0.3)
    st.put(apron.astype(float), c=ramp(tx + 0.03, FRAME) * (0.8 + 0.2 * _rounded_edge(np.minimum(Y - 13, 24 - Y), 2.0))[..., None],
           h=0.6 + 1.0 * _rounded_edge(np.minimum(Y - 13, 24 - Y), 2.0), s=0.3)
    st.put(legs.astype(float), c=ramp(ty, FRAME) * (0.78 + 0.22 * _rounded_edge(exl, 2.4))[..., None],
           h=1.0 + 1.2 * _rounded_edge(exl, 2.4), s=0.3)
    et = np.minimum(Y, 13 - Y)
    st.put(top.astype(float), c=ramp(ttop, PANEL) * (0.8 + 0.2 * _rounded_edge(et, 2.2))[..., None],
           h=1.6 + 1.2 * _rounded_edge(et, 2.2), s=0.32)
    # contact shadows under the top and apron
    sh = np.clip(1 - (Y - 24) / 5, 0, 1) * (Y >= 24) * ~legs + np.clip(1 - (Y - 13) / 3, 0, 1) * (Y >= 13) * legs
    st.alb = st.alb * (1 - 0.35 * sh)[..., None]
    for (cx, cy) in [(6.5, 18.5), (n - 6.5, 18.5), (6.5, 109.5), (n - 6.5, 109.5)]:
        d = np.hypot(X - cx, Y - cy)
        st.put(cov_from_sdf(d - 2.0), c=mix3(col('#a27a4e'), col('#c79f70'), 0.5 + 0.5 * np.sin(d * 3)), hadd=0.2)
    return finish(st.alb, st.h, st.sm, nstrength=1.0, cavity=0.12, bevel_d=1.3)


# ---------------------------------------------------------------- furnace ---
def _ashlar(seed, courses=4, wmin=26, wmax=46, tone_sd=0.05):
    """Dressed stone masonry with chisel-tooled faces; periodic."""
    n = T
    X, Y = grid(n)
    rng = np.random.default_rng(seed)
    ch = n / courses
    ci = np.clip((Y // ch).astype(int), 0, courses - 1)
    yl = Y - ci * ch
    bid = np.zeros((n, n), int)
    ex = np.full((n, n), 99.0)
    for c in range(courses):
        widths = []
        while sum(widths) < n - wmin:
            widths.append(rng.uniform(wmin, wmax))
        widths = np.array(widths) * n / sum(widths)
        edges = np.concatenate([[0], np.cumsum(widths)]) + rng.uniform(0, n)
        m = ci == c
        xs = (X[m] - edges[0]) % n
        k = np.searchsorted(np.cumsum(widths), xs, side='right')
        start = np.concatenate([[0], np.cumsum(widths)])[k]
        end = np.cumsum(widths)[np.clip(k, 0, len(widths) - 1)]
        ex[m] = np.minimum(xs - start, end - xs)
        bid[m] = c * 100 + k
    e = np.minimum(ex, np.minimum(yl, ch - yl))
    gap = 1.3
    face = smoothstep(gap - 0.5, gap + 0.5, e)
    pillow = _rounded_edge(e - gap, 3.5)
    tones = np.array([rng.normal(0, tone_sd) for _ in range(courses * 100 + 50)])
    fine = fbm(n, seed + 1, beta=1.5)
    mid = fbm(n, seed + 2, beta=2.4)
    chisel = np.sin((X + Y * 0.6) * 2.1 + 2.0 * fine)
    t = 0.55 + tones[bid] + 0.07 * mid + 0.05 * fine + 0.03 * chisel
    stone_c = ramp(t, [(0, '#4f4c48'), (0.35, '#6a6661'), (0.65, '#837e77'), (1, '#9d978e')])
    lt = _speck(n, seed + 3, 0.02)
    stone_c = mix3(stone_c, col('#b3ada2'), lt * 0.4)
    mortar_c = ramp(0.5 + 0.3 * fine, [(0, '#3a3631'), (1, '#55504a')])
    alb = mix3(mortar_c, stone_c * (0.85 + 0.15 * pillow)[..., None], face)
    h = -0.6 * (1 - face) + face * (2.4 * pillow + 0.35 * mid + 0.12 * chisel + 0.1 * fine)
    sm = 0.14 * (1 - face) + 0.27 * face
    return alb, h, sm, face


def furnace_side():
    alb, h, sm, _ = _ashlar(901)
    return finish(alb, h, sm, cavity=0.1, bevel_d=1.3)


def furnace_top():
    n = T
    X, Y = grid(n)
    alb, h, sm, face = _ashlar(911, courses=2, wmin=56, wmax=72)
    st = Stack(alb, h, sm)
    d = np.hypot(X - 64, Y - 64)
    soot = smoothstep(40, 18, d)
    st.alb = st.alb * (1 - 0.45 * soot)[..., None]
    ring = cov_from_sdf(np.abs(d - 22) - 4.0)
    iron = mix3(col('#3b3836'), col('#6d6965'), 0.5 + 0.5 * np.cos((X - Y) * 0.15))
    st.put(ring, c=iron, h=2.5 + _rounded_edge(4 - np.abs(d - 22), 2.0), s=0.45)
    inner = cov_from_sdf(d - 18)
    st.put(inner, c=col('#26211f'), h=-3.0, s=0.2)
    for i in range(-3, 4):
        bar = cov_from_sdf(np.maximum(np.abs(X - 64 - i * 5.2) - 1.3, d - 18.5))
        st.put(bar, c=mix3(col('#3d3a38'), col('#6a6662'), 0.5 + 0.5 * np.cos((X - 64 - i * 5.2) / 1.3 * 1.5)),
               h=0.5, s=0.45)
    for (cx, cy) in [(64, 40), (64, 88), (40, 64), (88, 64)]:
        dd = np.hypot(X - cx, Y - cy)
        st.put(cov_from_sdf(dd - 1.7), c=col('#7f7a74'), hadd=0.6, s=0.5)
    return finish(st.alb, st.h, st.sm, cavity=0.1, bevel_d=1.3)


def _furnace_front(lit=False):
    n = T
    X, Y = grid(n)
    alb, h, sm, face = _ashlar(921)
    st = Stack(alb, h, sm)
    cx, cy, R = 64.0, 68.0, 25.0
    bottom = 112.0
    d = np.hypot(X - cx, Y - cy)
    th = np.arctan2(Y - cy, X - cx)
    open_sd = np.where(Y > cy, np.maximum(np.abs(X - cx) - R, Y - bottom), np.maximum(d - R, Y - bottom))
    # voussoir ring and jambs (dressed stone around the mouth)
    ring_sd = np.where(Y > cy, np.maximum(np.abs(X - cx) - R - 9, Y - bottom - 2), d - R - 9)
    surround = cov_from_sdf(ring_sd) * (1 - cov_from_sdf(open_sd))
    fine = fbm(n, 922, beta=1.4)
    rng = np.random.default_rng(923)
    vang = np.linspace(-np.pi, 0, 8)
    seg = np.digitize(th, vang)
    jamb_y = np.where(Y > cy, ((Y - cy) // 15).astype(int), 0)
    sid = np.where(Y > cy, 50 + jamb_y + (X > cx) * 10, seg)
    tones = rng.normal(0, 0.045, 80)
    vt = 0.6 + tones[np.clip(sid, 0, 79)] + 0.06 * fine
    vc = ramp(vt, [(0, '#5b5751'), (0.5, '#7e7972'), (1, '#9f998f')])
    # joints between voussoirs
    joint = np.zeros((n, n))
    for a in vang[1:-1]:
        joint = np.maximum(joint, (Y <= cy) * (1 - smoothstep(0.4, 1.2, np.abs(np.angle(np.exp(1j * (th - a)))) * d)))
    for k in range(1, 4):
        joint = np.maximum(joint, (Y > cy) * (1 - smoothstep(0.4, 1.2, np.abs(Y - (cy + 15 * k)))))
    outer_e = np.clip(-ring_sd, 0, None)
    inner_e = np.clip(open_sd, 0, None)
    vprof = _rounded_edge(np.minimum(outer_e, inner_e), 2.2)
    st.put(surround, c=vc * (0.82 + 0.18 * vprof)[..., None], h=2.8 + 1.0 * vprof + 0.2 * fine, s=0.3)
    st.put(surround * joint, c=col('#3c3833'), hadd=-1.2)
    # keystone
    ks = np.maximum(np.abs(np.angle(np.exp(1j * (th + np.pi / 2)))) * d - 6.5, np.maximum(R - 1 - d, d - R - 12))
    kcov = cov_from_sdf(ks)
    kprof = _rounded_edge(-ks, 2.0)
    st.put(kcov, c=ramp(0.7 + 0.06 * fine, [(0, '#6f6a63'), (1, '#a8a297')]) * (0.82 + 0.18 * kprof)[..., None],
           h=4.2 + 1.0 * kprof, s=0.3)
    # sill
    sill = sd_box(X, Y, cx, bottom + 6.5, R + 13, 5.0, r=1.0)
    sprof = _rounded_edge(-sill, 2.0)
    st.put(cov_from_sdf(sill), c=ramp(0.6 + 0.06 * fine, [(0, '#5e5a54'), (1, '#9a948a')]) * (0.82 + 0.18 * sprof)[..., None],
           h=3.2 + 1.0 * sprof, s=0.3)
    # the firebox
    oc = cov_from_sdf(open_sd)
    depth = np.clip(np.minimum(np.clip(-open_sd, 0, None) / 10, 1), 0, 1)
    back = ramp(0.5 + 0.2 * fbm(n, 924, beta=2.0), [(0, '#231e1c'), (1, '#36302c')])
    bricks = (np.abs(((Y - cy) / 7) % 1 - 0.5) < 0.07) | (np.abs(((X - cx + 5 * ((Y // 7) % 2)) / 10) % 1 - 0.5) < 0.05)
    back = back * (1 - 0.25 * bricks)[..., None]
    back = back * (0.75 + 0.25 * (1 - depth))[..., None]
    e_box = np.zeros((n, n))
    if lit:
        glow = smoothstep(cy - 20, bottom, Y)
        fire = ramp(glow + 0.15 * fbm(n, 925, beta=2.2), [(0, '#6a2a12'), (0.4, '#c4501c'), (0.7, '#f39235'), (1, '#ffd27a')])
        back = mix3(back, fire, 0.35 + 0.65 * glow)
        e_box = 0.35 + 0.65 * glow
    st.put(oc, c=back, h=-4.0 - 1.0 * depth, s=0.15, e=e_box)
    # iron grate and ash/embers on it
    gy = 102.0
    for i in range(-4, 5):
        bar = sd_box(X, Y, cx + i * 5.5, gy, 1.2, 4.5, r=0.6)
        bc = cov_from_sdf(bar) * oc
        st.put(bc, c=mix3(col('#3a3735'), col('#6a6560'), 0.5 + 0.5 * np.cos((X - cx - i * 5.5) * 1.3)), h=-2.0, s=0.45,
               e=0.25 if lit else 0.0)
    rail = sd_box(X, Y, cx, gy - 4.5, R - 1, 1.2, r=0.6)
    st.put(cov_from_sdf(rail) * oc, c=col('#55504c'), h=-1.8, s=0.45, e=0.2 if lit else 0.0)
    ash = cov_from_sdf(sd_box(X, Y, cx, bottom - 3, R - 2, 3.2, r=2.5)) * oc
    ash_c = ramp(0.5 + 0.3 * fine, [(0, '#3d3835'), (1, '#6a645e')])
    if lit:
        emb = _speck(n, 926, 0.12) * ash
        ash_c = mix3(ash_c, col('#ffb04a'), emb)
        st.put(ash, c=ash_c, h=-2.5, s=0.2, e=0.3 + 0.7 * emb)
    else:
        emb = _speck(n, 926, 0.03) * ash
        ash_c = mix3(ash_c, col('#7a4a2a'), emb)
        st.put(ash, c=ash_c, h=-2.5, s=0.2)
    # soot above the mouth
    soot = smoothstep(cy - R - 22, cy - R, Y) * (1 - smoothstep(R + 4, R + 20, np.abs(X - cx))) * (Y < cy - R + 6)
    st.alb = st.alb * (1 - 0.35 * soot * (1 - oc))[..., None]
    if lit:
        warm = np.clip(1 - np.clip(open_sd, 0, None) / 14, 0, 1) * (1 - oc)
        st.alb = st.alb * (1 + warm[..., None] * np.array([0.25, 0.08, -0.05]))
    return finish(np.clip(st.alb, 0, 1), st.h, st.sm, emis=st.e if lit else None, cavity=0.1, bevel_d=1.3)


def furnace_front():
    return _furnace_front(False)


def furnace_front_lit():
    return _furnace_front(True)


# ------------------------------------------------------------------ glass ---
def glass():
    n = T
    X, Y = grid(n)
    D = edge_distance(n)
    fw = 6.0
    frame = cov_from_sdf(D - fw)
    fp = _rounded_edge(np.minimum(D, fw - D).clip(0, None), 2.0)
    c = ramp(0.5 + 0.5 * fp, [(0, '#7f9da6'), (0.5, '#a9c4cb'), (1, '#c9dde2')])
    inner_line = np.exp(-((D - fw + 0.6) / 0.7) ** 2)
    c = mix3(c, col('#6b8890'), inner_line * 0.6)
    alpha = frame.copy()
    st = Stack(c, 1.4 * fp + 0.4 * frame, 0.9)
    # glints across the pane (opaque, thin)
    glints = np.zeros((n, n))
    for (x0, y0, x1, y1, w) in [(22, 46, 46, 22, 1.1), (28, 54, 54, 28, 0.7), (84, 108, 104, 88, 0.8)]:
        vx, vy = x1 - x0, y1 - y0
        L2 = vx * vx + vy * vy
        t = np.clip(((X - x0) * vx + (Y - y0) * vy) / L2, 0, 1)
        dd = np.hypot(x0 + t * vx - X, y0 + t * vy - Y)
        taper = np.sin(np.pi * t) ** 0.6
        glints = np.maximum(glints, cov_from_sdf(dd - w * taper))
    glints = glints * (D > fw + 1)
    st.put(glints, c=col('#e4f0f2'), h=0.8, s=0.95)
    alpha = np.maximum(alpha, glints)
    return finish(st.alb, st.h, st.sm, alpha=alpha, bevel=False, nstrength=1.0, cavity=0.03)
