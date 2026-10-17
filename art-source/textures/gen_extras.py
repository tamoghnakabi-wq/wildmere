"""Non-block textures: crack overlay, water normals/foam, sky cloud noise, item icons."""
import numpy as np
from PIL import Image, ImageDraw

from texlib import (Canvas, band, blur, blur_clamped, col, cov_from_sdf, fbm, grid, local_uv, mix3,
                    normals_from_height, norm01, poisson, polyline_capsules, ramp, smoothstep, worley)


# ------------------------------------------------------------------ crack ---
def crack_sheet(n=128, stages=8, seed=1001):
    """8 break stages, white cracks with alpha, damage increasing left to right."""
    rng = np.random.default_rng(seed)
    paths = []  # (points, birth_stage, base_width)

    def walk(p, ang, length, birth, w, depth):
        pts = [np.array(p, float)]
        step = 2.2
        L = 0.0
        while L < length:
            ang += rng.normal(0, 0.32)
            q = pts[-1] + np.array([np.cos(ang), np.sin(ang)]) * step
            if not (2 < q[0] < n - 2 and 2 < q[1] < n - 2):
                break
            pts.append(q)
            L += step
        paths.append((np.array(pts), birth, w))
        if depth < 3:
            nb = int(rng.integers(1, 4 - min(depth, 2)))
            for _ in range(nb):
                if len(pts) < 6:
                    continue
                i = int(rng.integers(2, len(pts) - 2))
                a2 = ang + rng.choice([-1, 1]) * rng.uniform(0.5, 1.1)
                walk(pts[i], a2, length * rng.uniform(0.35, 0.6), min(stages - 1, birth + int(rng.integers(1, 3))),
                     w * 0.7, depth + 1)
    origin = np.array([n / 2 + rng.normal(0, 5), n / 2 + rng.normal(0, 5)])
    base = rng.uniform(0, 2 * np.pi)
    births = [0, 0, 1, 2, 3, 5]
    for k in range(6):
        walk(origin, base + k * np.pi / 3 + rng.normal(0, 0.3), rng.uniform(44, 66), births[k], 1.7, 0)
    for k in range(3):  # late corner fractures
        corner = np.array([rng.choice([14, n - 14]), rng.choice([14, n - 14])], float)
        walk(corner, np.arctan2(*(origin - corner)[::-1]) + rng.normal(0, 0.4), rng.uniform(20, 34), 4 + k, 0.9, 1)
    sheet = np.zeros((n, n * stages, 4), np.uint8)
    ss = 4
    for s in range(stages):
        cv = Canvas(n, ss=ss, wrap=False, v=0.0)
        grow = (s + 1) / stages
        for pts, birth, w in paths:
            if s < birth:
                continue
            frac = np.clip((s - birth + 1) / 3.0, 0, 1)
            m = max(2, int(len(pts) * frac))
            pp = pts[:m] * ss
            wd = (w * (0.8 + 0.7 * grow)) * ss
            polyline_capsules(cv, pp, wd * 0.5, wd * 0.12, lambda tg, dn, X, Y: dict(v=1.0))
        A, _ = cv.resolve()
        a = np.clip(A * 1.15, 0, 1)
        sheet[:, s * n:(s + 1) * n, :3] = 255
        sheet[:, s * n:(s + 1) * n, 3] = np.round(a * 255).astype(np.uint8)
    return sheet


# ------------------------------------------------------------------ water ---
def _wave_height(n, seed, direction, kc, ks, power):
    f = np.fft.fftfreq(n) * n
    FX, FY = np.meshgrid(f, f)
    k = np.hypot(FX, FY)
    th = np.arctan2(-FY, FX)  # image rows grow downward
    amp = np.exp(-((k - kc) / ks) ** 2) * np.abs(np.cos(th - direction)) ** power
    amp[0, 0] = 0
    w = np.random.default_rng(seed).standard_normal((n, n))
    h = np.fft.ifft2(np.fft.fft2(w) * amp).real
    return h / (np.abs(h).max() + 1e-9)


def water_normal(n, seed, direction, kc, ks, power, slope):
    h = _wave_height(n, seed, direction, kc, ks, power)
    h = h + 0.25 * _wave_height(n, seed + 1, direction + 0.6, kc * 2.1, ks * 2, 2)
    # scale so the steepest slope is about `slope` (gentle waves)
    gx = (np.roll(h, -1, 1) - np.roll(h, 1, 1)) * 0.5
    gy = (np.roll(h, -1, 0) - np.roll(h, 1, 0)) * 0.5
    m = np.percentile(np.hypot(gx, gy), 99.5)
    nrm = normals_from_height(h, slope / max(m, 1e-9))
    return np.round((nrm * 0.5 + 0.5) * 255).astype(np.uint8)


def water_foam(n=256, seed=1101):
    wx = fbm(n, seed, beta=3.0) * 6
    wy = fbm(n, seed + 1, beta=3.0) * 6
    w = worley(n, poisson(n, 18, seed + 2), qdx=wx, qdy=wy)
    lines = 1 - smoothstep(0.0, 3.2, w['F2'] - w['F1'])
    w2 = worley(n, poisson(n, 8, seed + 3), qdx=wx * 0.5, qdy=wy * 0.5)
    cells = 1 - smoothstep(0.0, 1.8, w2['F2'] - w2['F1'])
    patch = smoothstep(-0.6, 1.2, fbm(n, seed + 4, beta=2.6))
    bub = np.random.default_rng(seed + 5).random((n, n))
    bub = smoothstep(0.985, 0.998, bub)
    f = np.clip(0.75 * lines * (0.35 + 0.65 * patch) + 0.45 * cells * patch + 0.5 * bub * patch, 0, 1)
    f = blur(f, 0.6)
    return np.round(np.clip(f / f.max(), 0, 1) * 255).astype(np.uint8)


# ------------------------------------------------------------------- sky ---
def _turbulence(n, seed, f0, octaves=4):
    out = np.zeros((n, n))
    amp = 1.0
    tot = 0.0
    for o in range(octaves):
        b = band(n, seed + o, f0 * 2 ** o, max(1.0, f0 * 2 ** o * 0.5))
        out += amp * np.abs(b)
        tot += amp
        amp *= 0.5
    return norm01(out / tot)


def cloud_noise(n=256, seed=1201):
    r = _turbulence(n, seed, 2.0, 4)
    g = _turbulence(n, seed + 10, 5.0, 4)
    b = _turbulence(n, seed + 20, 14.0, 3)
    wa = worley(n, poisson(n, 46, seed + 30), edge=False)
    wb = worley(n, poisson(n, 22, seed + 31), edge=False)
    pa = 1 - np.clip(wa['F1'] / np.percentile(wa['F1'], 99), 0, 1)
    pb = 1 - np.clip(wb['F1'] / np.percentile(wb['F1'], 99), 0, 1)
    a = norm01(blur(0.65 * pa ** 1.5 + 0.35 * pb ** 1.5, 1.0))
    img = np.stack([r, g, b, a], -1)
    return np.round(img * 255).astype(np.uint8)


# ------------------------------------------------------------------ items ---
IS = 4  # icon supersampling


def _poly(pts, N):
    im = Image.new('L', (N, N), 0)
    ImageDraw.Draw(im).polygon([tuple(p) for p in pts], fill=255)
    return np.asarray(im, dtype=np.float64) / 255.0


def _finish_icon(cv, outline_col, n=128):
    A, o = cv.resolve()
    c = o['c']
    ring = np.clip(blur_clamped(A, 1.3) * 2.6, 0, 1)
    ring = np.maximum(ring, A)
    sh = np.zeros_like(A)
    sh[4:, 3:] = ring[:-4, :-3]
    sh = blur_clamped(sh, 2.6) * 0.5
    oc = col(outline_col)
    # composite: shadow -> outline -> icon (straight alpha "over")
    def over(c_top, a_top, c_bot, a_bot):
        a = a_top + a_bot * (1 - a_top)
        cc = (c_top * a_top[..., None] + c_bot * (a_bot * (1 - a_top))[..., None]) / np.maximum(a, 1e-6)[..., None]
        return cc, a
    cs, a_s = np.broadcast_to(col('#1b1610'), c.shape), sh
    cc, aa = over(np.broadcast_to(oc, c.shape), ring, cs, a_s)
    cc, aa = over(c, A, cc, aa)
    # bleed colour into fully transparent texels (no dark/odd fringes when filtered)
    from texlib import bleed
    cc = bleed(cc, (aa > 0.01) * aa)
    img = np.concatenate([np.clip(cc, 0, 1), aa[..., None]], -1)
    return np.round(img * 255).astype(np.uint8)


def _icon_canvas():
    return Canvas(128, ss=IS, wrap=False, c=(0, 0, 0))


def _light(nx, ny):
    """Shade factor for a facet normal direction (light from the top-left)."""
    l = np.array([-0.55, -0.65, 0.52])
    nz = np.sqrt(np.clip(1 - nx * nx - ny * ny, 0, 1))
    return np.clip(nx * l[0] + ny * l[1] + nz * l[2], 0, 1)


def icon_stick():
    cv = _icon_canvas()
    N = 128 * IS
    p0 = np.array([30, 102]) * IS
    p2 = np.array([100, 28]) * IS
    p1 = np.array([62, 62]) * IS + np.array([4, 6]) * IS
    pts = [(1 - t) ** 2 * p0 + 2 * (1 - t) * t * p1 + t * t * p2 for t in np.linspace(0, 1, 16)]
    streak = fbm(N, 1301, beta=1.4)

    def vf(tg, dn, X, Y):
        across = dn
        wood = ramp(0.5 + 0.12 * streak[np.clip(Y.astype(int), 0, N - 1), np.clip(X.astype(int), 0, N - 1)],
                    [(0, '#5c3f24'), (0.5, '#8a6138'), (1, '#a77a4b')])
        sh = 1.08 - 0.45 * across ** 2
        return dict(c=wood * sh[..., None])
    # side twig nub
    q = pts[9]
    polyline_capsules(cv, [q, q + np.array([10, 4]) * IS, q + np.array([16, 3]) * IS], 3.2 * IS, 1.8 * IS, vf)
    polyline_capsules(cv, pts, 6.0 * IS, 5.0 * IS, vf)
    # lighter cut end-grain at the top end
    X, Y, idx = cv.region(p2[0], p2[1], 7 * IS)
    d = np.hypot(X - p2[0], Y - p2[1])
    ring = 0.5 + 0.5 * np.sin(d / IS * 2.2)
    cv.paint(idx, cov_from_sdf(d - 4.6 * IS), c=mix3(col('#b88f5e'), col('#d6b382'), ring))
    return _finish_icon(cv, '#2a1a0e')


def _faceted_lump(cv, centre, radius, seed, palette, glint, verts=9, squash=0.85):
    rng = np.random.default_rng(seed)
    N = 128 * IS
    cx, cy = np.array(centre) * IS
    angs = np.sort(rng.uniform(0, 2 * np.pi, verts))
    rs = radius * rng.uniform(0.78, 1.08, verts) * IS
    ring = np.stack([cx + np.cos(angs) * rs, cy + np.sin(angs) * rs * squash], -1)
    peak = np.array([cx - radius * 0.18 * IS, cy - radius * 0.22 * IS])
    mid = (ring + peak) * 0.5 + rng.normal(0, 2.5 * IS, ring.shape)
    for i in range(verts):
        j = (i + 1) % verts
        for tri in ([peak, mid[i], mid[j]], [mid[i], ring[i], ring[j], mid[j]]):
            tri = np.array(tri)
            c0 = tri.mean(axis=0)
            nx = (c0[0] - peak[0]) / (radius * IS * 1.2)
            ny = (c0[1] - peak[1]) / (radius * IS * 1.2)
            if len(tri) == 3:
                nx, ny = nx * 0.45, ny * 0.45
            shade = _light(np.clip(nx + rng.normal(0, 0.12), -0.95, 0.95), np.clip(ny + rng.normal(0, 0.12), -0.95, 0.95))
            cov = _poly(tri, N)
            c = ramp(np.full((N, N), shade), palette)
            cv.paint_full(cov, c=c)
    # glints on the brightest top facets
    for k in range(3):
        gx = peak[0] + rng.normal(-4, 4) * IS
        gy = peak[1] + rng.normal(-3, 3) * IS
        X, Y, idx = cv.region(gx, gy, 3 * IS)
        d = np.hypot(X - gx, Y - gy)
        cv.paint(idx, cov_from_sdf(d - rng.uniform(0.8, 1.6) * IS) * 0.9, c=col(glint))


def icon_coal():
    cv = _icon_canvas()
    _faceted_lump(cv, (64, 66), 42, 1311, [(0, '#222126'), (0.4, '#34343a'), (0.75, '#52525c'), (1, '#80828f')],
                  '#c9ccdb', verts=11)
    return _finish_icon(cv, '#0f0e10')


def _nugget(cv, seed, base_pal, spot_col, rock_col):
    rng = np.random.default_rng(seed)
    N = 128 * IS
    X, Y = grid(N)
    blobs = [(64, 66, 37, 30), (44, 56, 22, 19), (84, 78, 23, 18), (72, 46, 20, 17)]
    nz = fbm(N, seed + 1, beta=2.4)
    fine = fbm(N, seed + 2, beta=1.3)
    field = np.full((N, N), -10.0)
    for (bx, by, a, b) in blobs:
        k = np.sqrt(((X - bx * IS) / (a * IS)) ** 2 + ((Y - by * IS) / (b * IS)) ** 2)
        field = np.maximum(field, 1 - k)
    field = field + 0.06 * nz
    cov = np.clip(field * (34 * IS) + 0.5, 0, 1)
    gy, gx = np.gradient(blur(np.maximum(field, -0.3), 6.0 * IS) + 0.01 * blur(fine, 1.5 * IS))
    g = np.hypot(gx, gy) + 1e-9
    s = 1.0 / (np.percentile(g, 95) + 1e-9)
    shade = _light(np.clip(-gx * s * 0.8, -0.95, 0.95), np.clip(-gy * s * 0.8, -0.95, 0.95))
    t = 0.25 + 0.7 * shade + 0.06 * fine
    c = ramp(t, base_pal)
    spots = smoothstep(0.55, 1.0, fbm(N, seed + 3, beta=2.0)) * 0.8
    c = mix3(c, col(spot_col) * (0.7 + 0.5 * shade)[..., None], spots)
    rock = smoothstep(0.75, 1.1, fbm(N, seed + 4, beta=1.8))
    c = mix3(c, col(rock_col) * (0.6 + 0.6 * shade)[..., None], rock)
    cv.paint_full(cov, c=c)


def icon_raw_iron():
    cv = _icon_canvas()
    _nugget(cv, 1321, [(0, '#5e3b28'), (0.4, '#a2704f'), (0.75, '#cf9f7c'), (1, '#f0cfb0')], '#b4562c', '#6e6a66')
    return _finish_icon(cv, '#2b1a10')


def icon_raw_gold():
    cv = _icon_canvas()
    _nugget(cv, 1331, [(0, '#6a4510'), (0.4, '#bb8a22'), (0.75, '#e8bf45'), (1, '#fff0b0')], '#d18a1c', '#7a705f')
    return _finish_icon(cv, '#2e1e06')


def _ingot(cv, top, front, side, shine, mark):
    N = 128 * IS
    s = IS
    # 3/4 view trapezoid prism
    ft = [(22 * s, 74 * s), (94 * s, 74 * s), (104 * s, 98 * s), (14 * s, 98 * s)]  # front face (bottom)
    tp = [(36 * s, 44 * s), (104 * s, 44 * s), (94 * s, 74 * s), (22 * s, 74 * s)]  # top face
    sd = [(104 * s, 44 * s), (114 * s, 70 * s), (104 * s, 98 * s), (94 * s, 74 * s)]  # right side
    X, Y = grid(N)
    for poly, cc in ((sd, side), (ft, front), (tp, top)):
        cov = _poly(poly, N)
        cv.paint_full(cov, c=np.broadcast_to(col(cc), (N, N, 3)))
    # gradients + specular band on the top face
    cov_t = _poly(tp, N)
    band_ = np.exp(-(((X - 0.55 * Y) - 30 * s) / (6 * s)) ** 2)
    g = mix3(col(top), col(shine), np.clip(band_ * 0.9, 0, 1))
    g = g * (1.02 - 0.12 * ((Y - 44 * s) / (30 * s)))[..., None]
    cv.paint_full(cov_t, c=g)
    cov_f = _poly(ft, N)
    gf = col(front) * (1.05 - 0.18 * np.clip((Y - 74 * s) / (24 * s), 0, 1))[..., None]
    cv.paint_full(cov_f, c=gf)
    # bevel highlight along the top-front edge
    edge = np.exp(-((Y - 74 * s) / (1.2 * s)) ** 2) * cov_f.clip(0, 1) * ((X > 22 * s) & (X < 94 * s))
    cv.paint_full(edge * 0.7, c=np.broadcast_to(col(shine), (N, N, 3)))
    # stamped recess on the top
    rec = [(48 * s, 52 * s), (92 * s, 52 * s), (87 * s, 66 * s), (42 * s, 66 * s)]
    cv.paint_full(_poly(rec, N) * 0.55, c=np.broadcast_to(col(mark), (N, N, 3)))
    rec2 = [(49 * s, 53 * s), (91 * s, 53 * s), (90 * s, 56 * s), (48 * s, 56 * s)]
    cv.paint_full(_poly(rec2, N) * 0.5, c=np.broadcast_to(col(side), (N, N, 3)))


def icon_iron_ingot():
    cv = _icon_canvas()
    _ingot(cv, '#c9d0d4', '#a3abb0', '#767e84', '#f2f6f8', '#9aa2a7')
    return _finish_icon(cv, '#1e2226')


def icon_gold_ingot():
    cv = _icon_canvas()
    _ingot(cv, '#f0c64a', '#d9a62e', '#a7761a', '#fff3be', '#d4a535')
    return _finish_icon(cv, '#2e1e06')


def icon_flint():
    cv = _icon_canvas()
    N = 128 * IS
    s = IS
    X, Y = grid(N)
    outline = [(60, 14), (84, 40), (98, 74), (84, 108), (54, 114), (34, 92), (30, 58), (42, 30)]
    pts = np.array(outline, float) * s
    cov = _poly(pts, N)
    # cortex (chalky rind) along the lower-left edge
    cortex = _poly(np.array([(34, 92), (54, 114), (62, 112), (44, 90), (36, 64), (30, 58)], float) * s, N)
    centre = np.array([66, 62]) * s
    # facets: fan from an off-centre ridge with conchoidal ripples
    rng = np.random.default_rng(1341)
    ridge = [np.array([60, 14]) * s, np.array([66, 60]) * s, np.array([62, 104]) * s]
    base = np.zeros((N, N, 3))
    cv.paint_full(cov, c=np.broadcast_to(col('#3f4247'), (N, N, 3)))
    for i in range(len(pts)):
        j = (i + 1) % len(pts)
        r = ridge[min(2, int(3 * i / len(pts)))]
        tri = np.array([r, pts[i], pts[j]])
        c0 = tri.mean(axis=0)
        nx = (c0[0] - centre[0]) / (40 * s)
        ny = (c0[1] - centre[1]) / (40 * s)
        sh = _light(np.clip(nx, -0.9, 0.9), np.clip(ny, -0.9, 0.9))
        d = np.hypot(X - pts[i][0], Y - pts[i][1]) / s
        ripple = 0.5 + 0.5 * np.sin(d * 0.9 + rng.uniform(0, 6))
        cc = ramp(0.2 + 0.7 * sh + 0.06 * ripple, [(0, '#25272b'), (0.45, '#3e4248'), (0.8, '#5f646c'), (1, '#8a909a')])
        cv.paint_full(_poly(tri, N) * cov, c=cc)
    rind = ramp(0.5 + 0.2 * fbm(N, 1342, beta=1.6), [(0, '#b4aa96'), (1, '#ddd4c2')])
    cv.paint_full(cortex * cov, c=rind)
    # crisp highlight on the ridge
    for k in range(2):
        polyline_capsules(cv, [ridge[k], ridge[k + 1]], 0.9 * s, 0.6 * s,
                          lambda tg, dn, X2, Y2: dict(c=col('#a4abb5') * (1 - 0.2 * dn)[..., None]))
    return _finish_icon(cv, '#111214')


ITEMS = {
    'stick': icon_stick,
    'coal': icon_coal,
    'raw_iron': icon_raw_iron,
    'iron_ingot': icon_iron_ingot,
    'raw_gold': icon_raw_gold,
    'gold_ingot': icon_gold_ingot,
    'flint': icon_flint,
}
