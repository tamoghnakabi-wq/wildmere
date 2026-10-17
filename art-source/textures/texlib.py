"""Wildmere procedural texture toolkit.

Everything here is periodic on an N x N grid unless stated otherwise, so any
texture built only from these primitives tiles seamlessly.

Conventions
-----------
* Colours are sRGB floats in 0..1 (``hexc``); lighting maths converts to linear.
* Heights are in *pixel units* (1.0 = one texel of displacement). Normals are
  derived with a physical slope, so strength 1.0 means "as tall as authored".
* Image row 0 is the TOP of the tile. Tangent-space normals use the OpenGL
  convention (+X right, +Y up = toward row 0, +Z out of the surface).
"""
import numpy as np

TILE = 128
TINT_GAIN = 1.7  # the runtime multiplies tinted texels by tint * 1.7 (linear)


# ---------------------------------------------------------------- colour ---
def hexc(h):
    h = h.lstrip('#')
    return np.array([int(h[i:i + 2], 16) for i in (0, 2, 4)], dtype=np.float64) / 255.0


def col(c):
    return hexc(c) if isinstance(c, str) else np.asarray(c, dtype=np.float64)


def srgb_to_lin(c):
    c = np.clip(c, 0.0, 1.0)
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


def lin_to_srgb(c):
    c = np.clip(c, 0.0, 1.0)
    return np.where(c <= 0.0031308, c * 12.92, 1.055 * np.power(c, 1 / 2.4) - 0.055)


def ramp(t, stops):
    """Map a scalar field through colour stops [(pos, colour), ...]."""
    pos = np.array([s[0] for s in stops], dtype=np.float64)
    cols = np.array([col(s[1]) for s in stops])
    t = np.clip(np.asarray(t, dtype=np.float64), pos[0], pos[-1])
    out = np.empty(t.shape + (3,))
    for c in range(3):
        out[..., c] = np.interp(t, pos, cols[:, c])
    return out


def mix3(a, b, t):
    """Blend colours a -> b by scalar field t (broadcasts t over RGB)."""
    t = np.asarray(t, dtype=np.float64)
    return np.asarray(a, float) + (np.asarray(b, float) - np.asarray(a, float)) * t[..., None]


def luma(rgb):
    return rgb[..., 0] * 0.2126 + rgb[..., 1] * 0.7152 + rgb[..., 2] * 0.0722


def grey(v, warm=0.0):
    """Near-neutral luminance colour (for biome-tinted pixels). warm>0 adds a faint warm cast."""
    v = np.asarray(v, dtype=np.float64)
    return np.stack([v * (1 + warm), v, v * (1 - warm)], -1)


def smoothstep(e0, e1, x):
    t = np.clip((np.asarray(x, dtype=np.float64) - e0) / (e1 - e0), 0.0, 1.0)
    return t * t * (3 - 2 * t)


def norm01(a):
    lo, hi = float(a.min()), float(a.max())
    return (a - lo) / (hi - lo + 1e-12)


def standardize(a):
    return (a - a.mean()) / (a.std() + 1e-12)


# ------------------------------------------------------- periodic noise ---
def _fgrid(n):
    f = np.fft.fftfreq(n) * n
    return np.meshgrid(f, f)  # FX varies along columns (x), FY along rows (y)


def _shape_freq(n, ax, ay, angle):
    FX, FY = _fgrid(n)
    if angle:
        c, s = np.cos(angle), np.sin(angle)
        FX, FY = c * FX + s * FY, -s * FX + c * FY
    # dividing by ax < 1 pushes energy toward FX ~ 0, i.e. features long along x
    return np.hypot(FX / ax, FY / ay)


def spectral(n, seed, beta=2.0, fmax=None, ax=1.0, ay=1.0, angle=0.0, fmin=0.0):
    """Periodic 1/f^beta noise (zero mean, unit std).

    ax/ay are feature stretch factors: ax < 1 makes features long along x
    (grain running horizontally), ay < 1 makes them long along y (fibres).
    """
    R = _shape_freq(n, ax, ay, angle)
    amp = np.zeros_like(R)
    nz = R > 0
    amp[nz] = R[nz] ** (-beta / 2.0)
    if fmin:
        amp *= smoothstep(fmin * 0.5, fmin, R)
    if fmax is not None:
        amp *= np.exp(-(R / fmax) ** 2)
    w = np.random.default_rng(seed).standard_normal((n, n))
    return standardize(np.fft.ifft2(np.fft.fft2(w) * amp).real)


def band(n, seed, f0, bw, ax=1.0, ay=1.0, angle=0.0):
    """Periodic band-limited noise with features of ~n/f0 pixels."""
    R = _shape_freq(n, ax, ay, angle)
    amp = np.exp(-((R - f0) / bw) ** 2)
    amp[0, 0] = 0.0
    w = np.random.default_rng(seed).standard_normal((n, n))
    return standardize(np.fft.ifft2(np.fft.fft2(w) * amp).real)


def fbm(n, seed, beta=2.0, fmax=None, **kw):
    return spectral(n, seed, beta=beta, fmax=fmax, **kw)


def blur(a, sx, sy=None):
    """Periodic gaussian blur (sigma in pixels) over the first two axes."""
    sy = sx if sy is None else sy
    n, m = a.shape[:2]
    fy = np.fft.fftfreq(n)[:, None]
    fx = np.fft.fftfreq(m)[None, :]
    G = np.exp(-2 * np.pi ** 2 * ((sx * fx) ** 2 + (sy * fy) ** 2))
    if a.ndim == 3:
        G = G[..., None]
        return np.fft.ifft2(np.fft.fft2(a, axes=(0, 1)) * G, axes=(0, 1)).real
    return np.fft.ifft2(np.fft.fft2(a) * G).real


def blur_clamped(a, s):
    """Non-periodic blur (for sprites): pads with edge values, then crops."""
    p = int(np.ceil(s * 3)) + 1
    pad = [(p, p), (p, p)] + ([(0, 0)] if a.ndim == 3 else [])
    b = np.pad(a, pad, mode='edge')
    b = blur(b, s)
    return b[p:-p, p:-p]


def sample_wrap(a, x, y):
    """Bilinear sample with wrap-around; x, y in pixel coordinates (0 = first texel centre)."""
    n, m = a.shape[:2]
    x0 = np.floor(x).astype(np.int64)
    y0 = np.floor(y).astype(np.int64)
    fx = x - x0
    fy = y - y0
    x0 %= m
    y0 %= n
    x1 = (x0 + 1) % m
    y1 = (y0 + 1) % n
    if a.ndim == 3:
        fx = fx[..., None]
        fy = fy[..., None]
    return (a[y0, x0] * (1 - fx) * (1 - fy) + a[y0, x1] * fx * (1 - fy)
            + a[y1, x0] * (1 - fx) * fy + a[y1, x1] * fx * fy)


def warp(a, dx, dy):
    n, m = a.shape[:2]
    Y, X = np.mgrid[0:n, 0:m].astype(np.float64)
    return sample_wrap(a, X + dx, Y + dy)


def grid(n):
    """Pixel-centre coordinates X, Y (floats)."""
    Y, X = np.mgrid[0:n, 0:n].astype(np.float64)
    return X + 0.5, Y + 0.5


def hash01(*ints):
    """Deterministic pseudo-random float in [0,1) from integers."""
    h = 2166136261
    for v in ints:
        h ^= (int(v) & 0xFFFFFFFF)
        h = (h * 16777619) & 0xFFFFFFFF
        h ^= h >> 13
        h = (h * 1274126177) & 0xFFFFFFFF
    return (h & 0xFFFFFF) / float(0x1000000)


# --------------------------------------------------------- point sets ---
def wrap_delta(d, n):
    return (d + n / 2.0) % n - n / 2.0


def poisson(n, r, seed, count=None, tries=20000, radii=None, ay=1.0):
    """Periodic dart-throwing Poisson-disc points in [0,n)^2.

    radii: optional callable(rng) -> radius per point; then points keep
    (ri + rj) * r apart (r acts as a packing factor).
    ay < 1 spaces points further apart along y (pair with worley(ay=...)
    to get cells elongated vertically).
    """
    rng = np.random.default_rng(seed)
    pts, rs = [], []
    for _ in range(tries):
        p = rng.random(2) * n
        ri = radii(rng) if radii else r
        if pts:
            P = np.array(pts)
            d = np.hypot(wrap_delta(P[:, 0] - p[0], n), wrap_delta(P[:, 1] - p[1], n) * ay)
            need = (np.array(rs) + ri) * r if radii else r
            if np.any(d < need):
                continue
        pts.append(p)
        rs.append(ri)
        if count and len(pts) >= count:
            break
    if radii:
        return np.array(pts), np.array(rs)
    return np.array(pts)


def worley(n, pts, ax=1.0, ay=1.0, edge=True, chunk=4096, qdx=None, qdy=None):
    """Periodic Worley / Voronoi on an n x n grid.

    pts: (k, 2) array of (x, y) in pixel units. Distances use a metric scaled
    by (ax, ay) (ax < 1 elongates cells along x). Returns dict with
    F1, F2 (distances), id (nearest source index), id2, cx, cy (nearest
    point copy in unwrapped pixel coords) and E (distance to the nearest
    Voronoi edge, exact bisector distance).
    """
    pts = np.asarray(pts, dtype=np.float64)
    k = len(pts)
    P, S = [], []
    for ox in (-1, 0, 1):
        for oy in (-1, 0, 1):
            Q = pts + np.array([ox * n, oy * n])
            keep = ((Q[:, 0] > -0.6 * n) & (Q[:, 0] < 1.6 * n) & (Q[:, 1] > -0.6 * n) & (Q[:, 1] < 1.6 * n))
            P.append(Q[keep])
            S.append(np.arange(k)[keep])
    P = np.concatenate(P)
    S = np.concatenate(S)
    Ps = P * np.array([ax, ay])
    X, Y = grid(n)
    if qdx is not None:
        X = X + qdx
    if qdy is not None:
        Y = Y + qdy
    qx_all = X.ravel() * ax
    qy_all = Y.ravel() * ay
    N = n * n
    F1 = np.empty(N)
    F2 = np.empty(N)
    J1 = np.empty(N, dtype=np.int64)
    J2 = np.empty(N, dtype=np.int64)
    E = np.empty(N)
    for s in range(0, N, chunk):
        qx = qx_all[s:s + chunk, None]
        qy = qy_all[s:s + chunk, None]
        dx = Ps[None, :, 0] - qx
        dy = Ps[None, :, 1] - qy
        d2 = dx * dx + dy * dy
        part = np.argpartition(d2, 1, axis=1)[:, :2]
        pv = np.take_along_axis(d2, part, 1)
        o = np.argsort(pv, axis=1)
        j = np.take_along_axis(part, o, 1)
        rows = np.arange(len(qx))
        J1[s:s + chunk] = j[:, 0]
        J2[s:s + chunk] = j[:, 1]
        F1[s:s + chunk] = np.sqrt(d2[rows, j[:, 0]])
        F2[s:s + chunk] = np.sqrt(d2[rows, j[:, 1]])
        if edge:
            a = Ps[j[:, 0]]
            bx = Ps[None, :, 0] - a[:, 0:1]
            by = Ps[None, :, 1] - a[:, 1:2]
            L = np.sqrt(bx * bx + by * by)
            mx = (Ps[None, :, 0] + a[:, 0:1]) * 0.5
            my = (Ps[None, :, 1] + a[:, 1:2]) * 0.5
            with np.errstate(divide='ignore', invalid='ignore'):
                e = -((qx - mx) * bx + (qy - my) * by) / L
            e[rows, j[:, 0]] = np.inf
            e[~np.isfinite(e)] = np.inf
            E[s:s + chunk] = e.min(axis=1)
    out = dict(F1=F1.reshape(n, n), F2=F2.reshape(n, n), id=S[J1].reshape(n, n),
               id2=S[J2].reshape(n, n), cx=P[J1, 0].reshape(n, n), cy=P[J1, 1].reshape(n, n))
    if edge:
        out['E'] = E.reshape(n, n)
    return out


# ------------------------------------------------------- painting canvas ---
class Canvas:
    """Premultiplied painter's-algorithm canvas with arbitrary attribute channels.

    Every paint() blends *all* channels with the same coverage, so colour,
    height, tint and so on stay consistent and edges never pick up the
    (undefined) background colour.  ss = supersampling factor; resolve()
    box-filters back down to the target size.
    """

    def __init__(self, n, ss=1, wrap=True, **channels):
        self.n = n
        self.ss = ss
        self.N = n * ss
        self.wrap = wrap
        self.A = np.zeros((self.N, self.N))
        self.ch = {}
        self.defaults = {}
        for k, v in channels.items():
            v = np.asarray(v, dtype=np.float64)
            self.defaults[k] = v
            self.ch[k] = np.zeros((self.N, self.N) + v.shape)

    def fill(self, **vals):
        """Opaque background. Values may be full-resolution arrays (canvas size)."""
        self.A[:] = 1.0
        for k, P in self.ch.items():
            v = np.asarray(vals.get(k, self.defaults[k]), dtype=np.float64)
            P[:] = v

    def region(self, cx, cy, r, ry=None):
        """Grid for a box around (cx, cy) in *canvas* pixels -> X, Y, idx."""
        ry = r if ry is None else ry
        x0, x1 = int(np.floor(cx - r)), int(np.ceil(cx + r)) + 1
        y0, y1 = int(np.floor(cy - ry)), int(np.ceil(cy + ry)) + 1
        xs = np.arange(x0, x1)
        ys = np.arange(y0, y1)
        wx = self.wrap in (True, 'x')
        wy = self.wrap is True
        if wx:
            assert len(xs) <= self.N, 'splat larger than canvas'
            ix = xs % self.N
        else:
            xs = xs[(xs >= 0) & (xs < self.N)]
            ix = xs
        if wy:
            assert len(ys) <= self.N, 'splat larger than canvas'
            iy = ys % self.N
        else:
            ys = ys[(ys >= 0) & (ys < self.N)]
            iy = ys
        X, Y = np.meshgrid(xs + 0.5, ys + 0.5)
        return X, Y, (iy, ix)

    def paint(self, idx, cov, **vals):
        iy, ix = idx
        if len(iy) == 0 or len(ix) == 0:
            return
        cov = np.clip(cov, 0.0, 1.0)
        if not np.any(cov > 0):
            return
        sub = np.ix_(iy, ix)
        self.A[sub] = self.A[sub] * (1 - cov) + cov
        for k, P in self.ch.items():
            v = np.asarray(vals.get(k, self.defaults[k]), dtype=np.float64)
            c = cov if P.ndim == 2 else cov[..., None]
            P[sub] = P[sub] * (1 - c) + v * c

    def paint_full(self, cov, **vals):
        """Paint a full-canvas coverage field."""
        cov = np.clip(cov, 0.0, 1.0)
        self.A = self.A * (1 - cov) + cov
        for k, P in self.ch.items():
            v = np.asarray(vals.get(k, self.defaults[k]), dtype=np.float64)
            c = cov if P.ndim == 2 else cov[..., None]
            self.ch[k] = P * (1 - c) + v * c

    def resolve(self):
        s = self.ss

        def down(a):
            if s == 1:
                return a
            sh = (self.n, s, self.n, s) + a.shape[2:]
            return a.reshape(sh).mean(axis=(1, 3))
        A = down(self.A)
        out = {}
        safe = np.maximum(A, 1e-6)
        for k, P in self.ch.items():
            d = down(P)
            out[k] = d / (safe if d.ndim == 2 else safe[..., None])
        return A, out


def cov_from_sdf(d, soft=1.0):
    """Anti-aliased coverage from a signed distance (pixels, negative inside)."""
    return np.clip(0.5 - d / soft, 0.0, 1.0)


def sd_segment(X, Y, x0, y0, x1, y1):
    """Distance to segment and the 0..1 parameter along it."""
    vx, vy = x1 - x0, y1 - y0
    L2 = vx * vx + vy * vy + 1e-12
    t = np.clip(((X - x0) * vx + (Y - y0) * vy) / L2, 0.0, 1.0)
    px = x0 + t * vx - X
    py = y0 + t * vy - Y
    return np.sqrt(px * px + py * py), t


def local_uv(X, Y, cx, cy, ang):
    """Rotate into a local frame: u along angle ang, v across."""
    c, s = np.cos(ang), np.sin(ang)
    dx, dy = X - cx, Y - cy
    return dx * c + dy * s, -dx * s + dy * c


def sd_ellipse(X, Y, cx, cy, a, b, ang=0.0):
    u, v = local_uv(X, Y, cx, cy, ang)
    k = np.sqrt((u / a) ** 2 + (v / b) ** 2)
    return (k - 1.0) * min(a, b), u, v, k


def bezier(p0, p1, p2, t):
    t = np.asarray(t, dtype=np.float64)[..., None]
    p0, p1, p2 = (np.asarray(p, float) for p in (p0, p1, p2))
    return (1 - t) ** 2 * p0 + 2 * (1 - t) * t * p1 + t * t * p2


def polyline_capsules(canvas, pts, w0, w1, value_fn, soft=1.0, cap=True):
    """Paint a tapered polyline as consecutive capsules.

    pts: (m, 2) canvas-pixel points. w0/w1: half-widths at start/end.
    value_fn(t_global, dist_norm, X, Y) -> dict of channel values over the box.
    """
    pts = np.asarray(pts, dtype=np.float64)
    m = len(pts)
    seglen = np.hypot(*np.diff(pts, axis=0).T)
    cum = np.concatenate([[0], np.cumsum(seglen)])
    total = cum[-1] + 1e-9
    for i in range(m - 1):
        (xa, ya), (xb, yb) = pts[i], pts[i + 1]
        ta, tb = cum[i] / total, cum[i + 1] / total
        ra, rb = w0 + (w1 - w0) * ta, w0 + (w1 - w0) * tb
        cx, cy = (xa + xb) / 2, (ya + yb) / 2
        r = np.hypot(xb - xa, yb - ya) / 2 + max(ra, rb) + 2
        X, Y, idx = canvas.region(cx, cy, r)
        d, t = sd_segment(X, Y, xa, ya, xb, yb)
        rad = ra + (rb - ra) * t
        sd = d - rad
        cov = cov_from_sdf(sd, soft)
        if not cap and i == 0:
            pass
        tg = ta + (tb - ta) * t
        dn = np.clip(d / np.maximum(rad, 1e-6), 0, 1)
        canvas.paint(idx, cov, **value_fn(tg, dn, X, Y))


# ------------------------------------------------------------- finishing ---
def grad(h):
    gx = (np.roll(h, -1, 1) - np.roll(h, 1, 1)) * 0.5
    gy = (np.roll(h, -1, 0) - np.roll(h, 1, 0)) * 0.5  # d/d(row): +down
    return gx, gy


def normals_from_height(h, strength=1.0):
    gx, gy = grad(h)
    nx = -gx * strength
    ny = gy * strength  # +Y is up (toward row 0): -dh/dv, v = -row
    nz = np.ones_like(h)
    L = np.sqrt(nx * nx + ny * ny + nz * nz)
    return np.stack([nx / L, ny / L, nz / L], -1)


def edge_distance(n):
    i = np.arange(n) + 0.5
    e = np.minimum(i, n - i)
    return np.minimum(e[None, :], e[:, None])


def bevel_profile(n, width):
    """0 at the block edge -> 1 inside. Blend of quarter-round and smoothstep."""
    D = edge_distance(n)
    t = np.clip(D / width, 0.0, 1.0)
    rnd = np.sqrt(1.0 - (1.0 - t) ** 2)
    sm = t * t * (3 - 2 * t)
    return 0.5 * rnd + 0.5 * sm, D


class Layer:
    """One finished tile: albedo (sRGB 0..1), alpha, normal (-1..1), AO, smoothness, height01, mask A."""

    def __init__(self, albedo, alpha, normal, ao, smooth, height01, maska, cube=True):
        self.albedo = np.clip(albedo, 0, 1)
        self.alpha = np.clip(alpha, 0, 1)
        self.normal = normal
        self.ao = np.clip(ao, 0, 1)
        self.smooth = np.clip(smooth, 0, 1)
        self.height = np.clip(height01, 0, 1)
        self.maska = np.clip(maska, 0, 1)
        self.cube = cube


def finish(albedo, height, smooth, *, alpha=None, tint=None, emis=None, bevel=True,
           bevel_w=5.0, bevel_d=1.6, nstrength=1.0, cavity=0.09, cav_r=2.5, edge_ao=0.12,
           ao=None, relief_fade=0.35, cube=True, min_range=4.0):
    """Turn authored albedo + height (px) + smoothness into a Layer.

    * bevel: soft chiselled block edge (height falloff, slight edge AO).
    * cavity: AO from local concavity (blurred height minus height).
    * mask A = tint (biome multiply) or, for emissive layers, emission.
    """
    n = albedo.shape[0]
    h = np.asarray(height, dtype=np.float64)
    if np.isscalar(smooth) or np.ndim(smooth) == 0:
        smooth = np.full((n, n), float(smooth))
    D = edge_distance(n)
    if bevel:
        p, D = bevel_profile(n, bevel_w)
        # relief is gently flattened toward the edge so the bevel reads cleanly
        fade = 1.0 - relief_fade * (1.0 - smoothstep(0, bevel_w * 1.6, D))
        hm = h - h.mean()
        hb = hm * fade + bevel_d * (p - 1.0)
    else:
        hb = h - h.mean()
    nrm = normals_from_height(hb, nstrength)
    cav = np.clip(blur(hb, cav_r) - hb, 0, None) + 0.5 * np.clip(blur(hb, cav_r * 3) - hb, 0, None)
    AO = np.clip(1.0 - cavity * cav, 0.4, 1.0)
    if bevel:
        AO = AO * (1.0 - edge_ao * (1.0 - smoothstep(0, bevel_w + 2.5, D)))
    if ao is not None:
        AO = AO * ao
    rng_ = max(float(np.ptp(hb)), min_range)
    h01 = (hb - hb.min()) / rng_
    a = np.ones((n, n)) if alpha is None else alpha
    if emis is not None:
        A = emis
    elif tint is not None:
        A = tint
    else:
        A = np.zeros((n, n))
    return Layer(albedo, a, nrm, AO, smooth, h01, A, cube=cube)


def bleed(img, w, eps=1e-6):
    """Push-pull fill: replace texels with weight 0 by a smooth extrapolation of
    nearby weighted texels (keeps mipmaps/bilinear from pulling in black)."""
    squeeze = img.ndim == 2
    if squeeze:
        img = img[..., None]
    pyr = []
    c = img * w[..., None]
    ww = w.copy()
    pyr.append((c, ww))
    while c.shape[0] > 1:
        h2 = c.shape[0] // 2
        c = c.reshape(h2, 2, h2, 2, -1).sum(axis=(1, 3))
        ww = ww.reshape(h2, 2, h2, 2).sum(axis=(1, 3))
        pyr.append((c, ww))
    c, ww = pyr[-1]
    fill = c / np.maximum(ww, eps)[..., None]
    for c, ww in reversed(pyr[:-1]):
        up = np.repeat(np.repeat(fill, 2, 0), 2, 1)
        up = blur_clamped(up, 0.8) if up.shape[0] >= 4 else up
        est = c / np.maximum(ww, eps)[..., None]
        k = np.clip(ww, 0, 1)[..., None]
        fill = est * k + up * (1 - k)
    out = np.where((w > 0)[..., None], img, fill)
    return out[..., 0] if squeeze else out


def finalize_cutout(L):
    """Bleed colour/normal/mask channels into fully transparent texels."""
    w = (L.alpha > 0.02).astype(np.float64) * L.alpha
    L.albedo = np.clip(bleed(L.albedo, w), 0, 1)
    nb = bleed(L.normal, w)
    nb = nb / np.linalg.norm(nb, axis=-1, keepdims=True).clip(1e-6)
    # fully transparent texels drift toward flat so far-away mips stay neutral
    flat = np.array([0.0, 0.0, 1.0])
    k = (1.0 - np.clip(blur_clamped(w, 2.0) * 3, 0, 1))[..., None]
    nb = nb * (1 - k) + flat * k
    L.normal = nb / np.linalg.norm(nb, axis=-1, keepdims=True).clip(1e-6)
    L.ao = np.clip(bleed(L.ao, w), 0, 1)
    L.smooth = np.clip(bleed(L.smooth, w), 0, 1)
    L.height = np.clip(bleed(L.height, w), 0, 1)
    L.maska = np.clip(bleed(L.maska, w), 0, 1)
    return L


# ------------------------------------------------------------- shading ---
def apply_tint(albedo_srgb, maska, tint):
    """sRGB albedo with the runtime biome tint applied (for flat previews)."""
    a = srgb_to_lin(albedo_srgb)
    if tint is not None:
        a = a * mix3(np.ones(3), srgb_to_lin(col(tint)) * TINT_GAIN, maska)
    return lin_to_srgb(a)


def shade(L, tint=None, light=(-0.45, 0.55, 0.70), sun=1.0, sky=0.42, emis_gain=0.0):
    """Simple preview lighting of a Layer in tangent space -> sRGB 0..1 image."""
    a = srgb_to_lin(L.albedo)
    if tint is not None:
        # matches the voxel shader: albedo * lerp(1, tint * 1.7, mask.a), all linear
        a = a * mix3(np.ones(3), srgb_to_lin(col(tint)) * TINT_GAIN, L.maska)
    l = np.asarray(light, float)
    l = l / np.linalg.norm(l)
    N = L.normal
    ndl = np.clip(N @ l, 0, 1)
    v = np.array([0.0, 0.0, 1.0])
    hv = (l + v) / np.linalg.norm(l + v)
    ndh = np.clip(N @ hv, 0, 1)
    sm = L.smooth
    p = 2.0 ** (1.0 + 11.0 * sm)
    spec = (p + 2) / 8.0 * ndh ** p * (0.04 + 0.25 * sm ** 2) * ndl
    skyw = 0.6 + 0.4 * N[..., 1]
    c = a * (sky * L.ao * skyw)[..., None] + a * (sun * ndl * (0.55 + 0.45 * L.ao))[..., None] + spec[..., None] * sun
    if emis_gain:
        c = c + a * (L.maska * emis_gain)[..., None]
    c = c / (1 + c * 0.25)
    return lin_to_srgb(c * 1.15)
