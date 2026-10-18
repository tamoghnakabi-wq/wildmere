"""Offline audio helpers for Wildmere's sound pipeline (numpy only, no scipy).

Every sound that leaves build.py started as a sample in a downloaded recording;
these helpers only cut, filter, balance and loop that material. Filtering is done
in the frequency domain (zero phase), which is exact, fast for whole files and --
for loop beds -- naturally circular, so a filtered loop stays seamless.
"""
import os, struct, subprocess
import numpy as np

SR = 48000


# ----------------------------------------------------------------------------- io

def decode(path, sr=SR, channels=2):
    """Decode anything ffmpeg reads to float32 (n, channels) at `sr`."""
    cmd = ["ffmpeg", "-v", "error", "-nostdin", "-i", path, "-vn", "-f", "f32le",
           "-af", f"aresample={sr}:filter_size=64:cutoff=0.97",
           "-ac", str(channels), "-"]
    raw = subprocess.run(cmd, capture_output=True, check=True).stdout
    x = np.frombuffer(raw, dtype="<f4").astype(np.float64).reshape(-1, channels)
    return x


def probe(path):
    r = subprocess.run(["ffprobe", "-v", "error", "-show_entries",
                        "stream=codec_name,sample_rate,channels:format=duration",
                        "-of", "json", path], capture_output=True, text=True)
    import json
    try:
        d = json.loads(r.stdout)
        s = d["streams"][0]
        return {"codec": s.get("codec_name"), "sr": int(s.get("sample_rate", 0)),
                "ch": int(s.get("channels", 0)), "dur": float(d["format"].get("duration", 0))}
    except Exception:  # noqa: BLE001
        return None


def to_mono(x):
    """Fold to mono without comb-filtering spaced-pair stereo.

    If the channels are well correlated the mid signal is used; otherwise the
    louder channel alone (a spaced pair summed to mono can hollow out a sound).
    """
    if x.ndim == 1:
        return x
    if x.shape[1] == 1:
        return x[:, 0]
    l, r = x[:, 0], x[:, 1]
    el, er = float(np.sum(l * l)), float(np.sum(r * r))
    if el + er < 1e-20:
        return l
    corr = float(np.sum(l * r)) / np.sqrt(max(el * er, 1e-30))
    if corr > 0.6:
        return 0.5 * (l + r)
    return l if el >= er else r


def tpdf(n, ch):
    rng = np.random.default_rng(1234 + n)
    return (rng.random((n, ch)) - rng.random((n, ch)))


def write_wav(path, x, sr=SR):
    """float [-1,1] -> 16-bit PCM WAV with TPDF dither. Returns seconds."""
    os.makedirs(os.path.dirname(path), exist_ok=True)
    x = np.nan_to_num(np.asarray(x, dtype=np.float64))
    if x.ndim == 1:
        x = x[:, None]
    ch = x.shape[1]
    q = np.round(x * 32767.0 + tpdf(x.shape[0], ch))
    q = np.clip(q, -32768, 32767).astype("<i2")
    pcm = q.reshape(-1).tobytes()
    hdr = (b"RIFF" + struct.pack("<I", 36 + len(pcm)) + b"WAVEfmt " +
           struct.pack("<IHHIIHH", 16, 1, ch, sr, sr * ch * 2, ch * 2, 16) +
           b"data" + struct.pack("<I", len(pcm)))
    with open(path, "wb") as f:
        f.write(hdr + pcm)
    return x.shape[0] / sr


def read_wav(path):
    """Read a 16-bit PCM WAV written by write_wav -> (float (n,ch), sr)."""
    b = open(path, "rb").read()
    assert b[:4] == b"RIFF" and b[8:12] == b"WAVE", path
    i, fmt, data = 12, None, None
    while i + 8 <= len(b):
        cid, sz = b[i:i + 4], struct.unpack("<I", b[i + 4:i + 8])[0]
        if cid == b"fmt ":
            fmt = struct.unpack("<HHIIHH", b[i + 8:i + 24])
        elif cid == b"data":
            data = b[i + 8:i + 8 + sz]
        i += 8 + sz + (sz & 1)
    _, ch, sr, _, _, bits = fmt
    assert bits == 16, path
    x = np.frombuffer(data, dtype="<i2").astype(np.float64).reshape(-1, ch) / 32768.0
    return x, sr


def write_ogg(path, x, sr=SR, quality=6):
    """Encode float audio to Ogg Vorbis via ffmpeg's built-in encoder."""
    os.makedirs(os.path.dirname(path), exist_ok=True)
    x = np.asarray(x, dtype=np.float32)
    if x.ndim == 1:
        x = x[:, None]
    cmd = ["ffmpeg", "-v", "error", "-nostdin", "-y", "-f", "f32le", "-ar", str(sr),
           "-ac", str(x.shape[1]), "-i", "-", "-c:a", "vorbis", "-strict", "-2",
           "-q:a", str(quality), path]
    subprocess.run(cmd, input=x.tobytes(), check=True)
    return x.shape[0] / sr


# -------------------------------------------------------------------------- level

def peak(x):
    return float(np.max(np.abs(x))) if np.size(x) else 0.0


def rms(x):
    return float(np.sqrt(np.mean(np.square(x)))) if np.size(x) else 0.0


def db(v):
    return 20.0 * np.log10(max(float(v), 1e-12))


def undb(d):
    return 10.0 ** (d / 20.0)


def _kweight_gain(n, sr):
    """BS.1770 K-weighting magnitude (high-shelf ~+4 dB above 1.5 kHz, HPF 38 Hz)."""
    f = np.fft.rfftfreq(n, 1.0 / sr)
    shelf = np.sqrt((1 + (f / 1500.0) ** 2 * 10 ** (4.0 / 10)) / (1 + (f / 1500.0) ** 2))
    hp = (f / 38.0) ** 2 / np.sqrt(1 + (f / 38.0) ** 4)
    return shelf * hp


def kweight(x, sr=SR):
    return spectral(x, _kweight_gain, sr)


def loudness(x, sr=SR):
    """Gated integrated loudness (LUFS, BS.1770-style) of a clip."""
    y = kweight(x, sr)
    if y.ndim == 1:
        y = y[:, None]
    blk, hop = int(0.4 * sr), int(0.1 * sr)
    if y.shape[0] < blk:
        e = np.sum(np.mean(y ** 2, axis=0))
        return -0.691 + 10 * np.log10(max(e, 1e-12))
    pw = np.array([np.sum(np.mean(y[i:i + blk] ** 2, axis=0))
                   for i in range(0, y.shape[0] - blk + 1, hop)])
    lk = -0.691 + 10 * np.log10(np.maximum(pw, 1e-12))
    g = pw[lk > -70]
    if not g.size:
        return -70.0
    rel = -0.691 + 10 * np.log10(np.mean(g)) - 10
    g2 = pw[(lk > -70) & (lk > rel)]
    return float(-0.691 + 10 * np.log10(np.mean(g2 if g2.size else g)))


def momentary_max(x, sr=SR, win=0.4):
    """Max loudness over a sliding `win` window (0.4 s = BS.1770 momentary; 0.1 s
    for levelling short impacts against longer sounds, roughly the ear's own
    integration time for transients)."""
    y = kweight(x, sr)
    if y.ndim == 1:
        y = y[:, None]
    blk, hop = int(win * sr), int(min(0.02, win / 4) * sr)
    if y.shape[0] <= blk:
        e = np.sum(np.sum(y ** 2, axis=0)) / blk   # pad the window with silence
        return float(-0.691 + 10 * np.log10(max(e, 1e-12)))
    c = np.cumsum(np.concatenate([np.zeros((1, y.shape[1])), y ** 2]), axis=0)
    e = (c[blk:] - c[:-blk])[::hop] / blk
    return float(-0.691 + 10 * np.log10(max(np.max(np.sum(e, axis=1)), 1e-12)))


def true_peak(x):
    """4x-oversampled peak (FFT interpolation), chunked for long files."""
    y = x if x.ndim == 2 else x[:, None]
    n = y.shape[0]
    if n > SR * 20:
        return max(true_peak(y[i:i + SR * 10]) for i in range(0, n, SR * 10))
    X = np.fft.rfft(y, axis=0)
    Z = np.zeros((4 * n // 2 + 1, y.shape[1]), complex)
    Z[:X.shape[0]] = X
    return float(np.max(np.abs(np.fft.irfft(Z, 4 * n, axis=0) * 4)))


def _slide_min(v, w):
    pad = np.concatenate([np.full(w // 2, v[0]), v, np.full(w - w // 2 - 1, v[-1])])
    return np.min(np.lib.stride_tricks.sliding_window_view(pad, w), axis=1)


def _slide_mean(v, w):
    pad = np.concatenate([np.full(w // 2, v[0]), v, np.full(w - w // 2 - 1, v[-1])])
    c = np.concatenate([[0.0], np.cumsum(pad)])
    return (c[w:] - c[:-w]) / w


def limit(x, ceiling, sr=SR, look_ms=1.5, release_ms=40.0):
    """Look-ahead peak limiter: smooth gain riding so |x| stays under `ceiling`.

    Used only to tame the first few dB of a recording's sharpest transients so
    quiet-but-peaky takes can sit at the same loudness as their siblings.
    """
    a = np.abs(x) if x.ndim == 1 else np.max(np.abs(x), axis=1)
    req = np.minimum(1.0, ceiling / np.maximum(a, 1e-9))
    w = max(3, int(sr * look_ms / 1000))
    g = _slide_min(req, 2 * w + 1)
    # release: let the gain recover slowly (one-pole on the reduction, causal)
    r = max(1, int(sr * release_ms / 1000))
    red = 1.0 - g
    k = 1.0 / r
    out = np.empty_like(red)
    acc = 0.0
    for i in range(len(red)):           # short clips only; cost is fine
        acc = red[i] if red[i] > acc else acc + (red[i] - acc) * k
        out[i] = acc
    g = _slide_mean(1.0 - out, w)
    g = np.minimum(g, req)
    return x * (g[:, None] if x.ndim == 2 else g)


def level_limited(x, target, ceiling_db=-1.0, max_gr_db=6.0, sr=SR):
    """Bring a one-shot to `target` momentary loudness, limiting at most
    `max_gr_db` of peak; beyond that, the clip stays quieter instead."""
    c = undb(ceiling_db) * undb(-0.6)          # leave room for inter-sample peaks
    m = momentary_max(x, sr, win=0.1)
    g = undb(target - m)
    tp = true_peak(x) * g
    over = db(tp / c) if tp > c else 0.0
    if over > max_gr_db:
        g *= undb(-(over - max_gr_db))
    y = x * g
    if true_peak(y) > c:
        y = limit(y, c, sr)
        for _ in range(3):
            tp = true_peak(y)
            if tp <= undb(ceiling_db) * undb(-0.5):
                break
            y = y * (undb(ceiling_db) * undb(-0.55) / tp)
    return y


def gain_to(x, target_lufs, ceiling_db=-1.0, measure=None, sr=SR):
    """Scale to a loudness target, never letting the (true) peak exceed `ceiling_db`."""
    m = (measure or momentary_max)(x, sr)
    g = undb(target_lufs - m)
    p = max(peak(x), true_peak(x) * undb(-0.5)) * g
    c = undb(ceiling_db)
    if p > c:
        g *= c / p
    return x * g, g


# ------------------------------------------------------------------------ filters

def spectral(x, gain_fn, sr=SR, circular=False):
    """Apply a zero-phase magnitude response gain_fn(n_fft, sr) -> array(n_fft//2+1).

    One-shots are zero-padded so filter ringing can't wrap; loops (circular=True)
    are filtered as the periodic signal they are.
    """
    x = np.asarray(x, dtype=np.float64)
    n0 = x.shape[0]
    pad = 0 if circular else min(max(n0 // 2, 4096), 96000)
    n = n0 + 2 * pad
    nf = n if circular else int(2 ** np.ceil(np.log2(n)))
    y = np.zeros((nf,) + x.shape[1:])
    y[pad:pad + n0] = x
    X = np.fft.rfft(y, axis=0)
    g = gain_fn(nf, sr)
    X *= g[:, None] if x.ndim == 2 else g
    out = np.fft.irfft(X, nf, axis=0)
    return out[pad:pad + n0]


def _butter_mag(f, fc, order, kind):
    r = (np.maximum(f, 1e-9) / fc) ** (2 * order)
    if kind == "lp":
        return 1.0 / np.sqrt(1.0 + r)
    return np.sqrt(r / (1.0 + r))


def highpass(x, fc, order=2, sr=SR, circular=False):
    return spectral(x, lambda n, s: _butter_mag(np.fft.rfftfreq(n, 1 / s), fc, order, "hp"),
                    sr, circular)


def lowpass(x, fc, order=2, sr=SR, circular=False):
    return spectral(x, lambda n, s: _butter_mag(np.fft.rfftfreq(n, 1 / s), fc, order, "lp"),
                    sr, circular)


def bandpass(x, lo, hi, order=2, sr=SR, circular=False):
    def g(n, s):
        f = np.fft.rfftfreq(n, 1 / s)
        return _butter_mag(f, lo, order, "hp") * _butter_mag(f, hi, order, "lp")
    return spectral(x, g, sr, circular)


def shelf(x, fc, gain_db, high=True, sr=SR, circular=False):
    """First-order-ish shelf: +gain_db above (high) or below (low) fc."""
    def g(n, s):
        f = np.fft.rfftfreq(n, 1 / s)
        t = (f / fc) ** 2
        w = t / (1 + t) if high else 1 / (1 + t)
        return undb(gain_db * w)
    return spectral(x, g, sr, circular)


def eq_peak(x, fc, gain_db, q=1.0, sr=SR, circular=False):
    """Bell EQ on a log-frequency axis."""
    def g(n, s):
        f = np.maximum(np.fft.rfftfreq(n, 1 / s), 1.0)
        bw = 1.0 / q
        return undb(gain_db * np.exp(-0.5 * (np.log2(f / fc) / (bw / 2)) ** 2))
    return spectral(x, g, sr, circular)


def resample_ratio(x, ratio):
    """Varispeed by `ratio` (>1 = higher and shorter) with band-limited interpolation."""
    x = np.asarray(x, dtype=np.float64)
    if abs(ratio - 1.0) < 1e-6:
        return x
    if ratio > 1.0:   # band-limit before speeding up
        x = lowpass(x, 0.45 * SR / ratio, order=8)
    n = int(round(x.shape[0] / ratio))
    src = np.arange(n) * ratio
    i0 = np.floor(src).astype(int)
    fr = src - i0
    i0 = np.clip(i0, 0, x.shape[0] - 1)
    i1 = np.clip(i0 + 1, 0, x.shape[0] - 1)
    # cubic Hermite
    im1 = np.clip(i0 - 1, 0, x.shape[0] - 1)
    i2 = np.clip(i0 + 2, 0, x.shape[0] - 1)
    if x.ndim == 2:
        fr = fr[:, None]
    p0, p1, p2, p3 = x[im1], x[i0], x[i1], x[i2]
    a = -0.5 * p0 + 1.5 * p1 - 1.5 * p2 + 0.5 * p3
    b = p0 - 2.5 * p1 + 2 * p2 - 0.5 * p3
    c = -0.5 * p0 + 0.5 * p2
    return ((a * fr + b) * fr + c) * fr + p1


# ------------------------------------------------------------------------ editing

def fade(x, in_ms=2.0, out_ms=30.0, sr=SR):
    x = np.array(x, dtype=np.float64, copy=True)
    n = x.shape[0]
    a = min(int(sr * in_ms / 1000), n // 2)
    b = min(int(sr * out_ms / 1000), n // 2)
    if a > 1:
        w = np.sin(np.linspace(0, np.pi / 2, a)) ** 2
        x[:a] *= w[:, None] if x.ndim == 2 else w
    if b > 1:
        w = np.cos(np.linspace(0, np.pi / 2, b)) ** 2
        x[-b:] *= w[:, None] if x.ndim == 2 else w
    return x


def envelope(x, sr=SR, win_ms=5.0):
    m = np.abs(x if x.ndim == 1 else np.max(np.abs(x), axis=1))
    w = max(1, int(sr * win_ms / 1000))
    pad = (-len(m)) % w
    if pad:
        m = np.concatenate([m, np.zeros(pad)])
    return m.reshape(-1, w).max(axis=1), w


def rms_env(x, sr=SR, win_ms=10.0):
    m = x if x.ndim == 1 else np.mean(x, axis=1)
    w = max(1, int(sr * win_ms / 1000))
    pad = (-len(m)) % w
    if pad:
        m = np.concatenate([m, np.zeros(pad)])
    return np.sqrt(np.mean(m.reshape(-1, w) ** 2, axis=1)), w


def trim(x, sr=SR, thresh_db=-50.0, pre_ms=5.0, post_ms=60.0, lead_db=None):
    """Strip near-silence before/after the sound, relative to its peak.

    `lead_db` (if given) sets a separate, stricter threshold for the start, so a
    quiet scuff before a footstep doesn't delay the step's audible onset.
    """
    e, w = envelope(x, sr, 2.0)
    if not e.size:
        return x
    t = peak(x) * undb(thresh_db)
    hot = np.nonzero(e > t)[0]
    if not hot.size:
        return x
    if lead_db is not None:
        lead = np.nonzero(e > peak(x) * undb(lead_db))[0]
        hot = np.concatenate([lead[:1], hot[hot > lead[0]]]) if lead.size else hot
    a = max(0, hot[0] * w - int(sr * pre_ms / 1000))
    b = min(x.shape[0], (hot[-1] + 1) * w + int(sr * post_ms / 1000))
    return x[a:b]


def noise_floor(x, sr=SR, pct=10.0):
    e, _ = rms_env(x, sr, 20.0)
    return float(np.percentile(e, pct)) if e.size else 0.0


def find_events(x, sr=SR, over_floor_db=14.0, min_gap_ms=120.0, min_len_ms=40.0,
                max_len_ms=1500.0, pre_ms=6.0, tail_db=8.0, hold_ms=40.0, abs_min=0.0):
    """Locate individual sounds (takes) inside a longer recording.

    Returns a list of (start, end) sample indices. A take starts where the 2 ms
    envelope rises `over_floor_db` above the file's noise floor and runs until the
    envelope stays within `tail_db` of the floor for `hold_ms`.
    """
    xm = x if x.ndim == 1 else np.max(np.abs(x), axis=1)
    e, w = envelope(xm, sr, 2.0)
    fl = max(noise_floor(xm, sr) * 1.41, 1e-5)
    on_t = max(fl * undb(over_floor_db), abs_min)
    off_t = fl * undb(tail_db)
    hold = max(1, int(hold_ms / 2.0))
    gap = int(min_gap_ms / 2.0)
    out, i, n = [], 0, len(e)
    while i < n:
        if e[i] > on_t:
            j = i
            while j > 0 and e[j - 1] > off_t and (i - j) < 25:
                j -= 1
            k, quiet = i, 0
            lim = min(n, i + int(max_len_ms / 2.0))
            while k < lim:
                quiet = quiet + 1 if e[k] < off_t else 0
                if quiet >= hold:
                    break
                k += 1
            end = k - quiet + int(hold_ms / 4.0)
            a = max(0, j * w - int(sr * pre_ms / 1000))
            b = min(xm.shape[0], end * w)
            if (b - a) >= sr * min_len_ms / 1000:
                out.append((a, b))
            i = max(k, i + 1) + gap
        else:
            i += 1
    return out


def declick(x, sr=SR, k=10.0, max_ms=1.0):
    """Repair isolated digital clicks/pops; leaves genuine transients alone.

    A click is a burst of <= max_ms where the second difference exceeds k times the
    local median absolute second difference AND the surrounding 20 ms is not itself
    loud (i.e. it is not the onset of a real impact). Returns (y, n_repaired).
    """
    y = np.array(x, dtype=np.float64, copy=True)
    chans = [y] if y.ndim == 1 else [y[:, c] for c in range(y.shape[1])]
    fixed = 0
    span = max(2, int(sr * max_ms / 1000))
    for ch in chans:
        d2 = np.abs(np.diff(ch, 2, prepend=ch[0], append=ch[-1]))
        w = int(sr * 0.02)
        pad = (-len(d2)) % w
        blocks = np.concatenate([d2, np.zeros(pad)]).reshape(-1, w)
        med = np.median(blocks, axis=1)
        loc = np.repeat(np.maximum(med, 1e-6), w)[:len(d2)]
        cand = np.nonzero(d2 > k * loc * 6.0)[0]
        if not cand.size:
            continue
        # group into runs
        runs, s, p = [], cand[0], cand[0]
        for c in cand[1:]:
            if c - p > span:
                runs.append((s, p))
                s = c
            p = c
        runs.append((s, p))
        lev, _ = rms_env(ch, sr, 20.0)
        lev_db = 20 * np.log10(np.maximum(lev, 1e-9))
        med_db = np.median(lev_db)
        for a, b in runs:
            if b - a > span:
                continue
            blk = a // int(sr * 0.02)
            if blk < len(lev_db) and lev_db[blk] > med_db + 12:   # a real transient
                continue
            a0, b0 = max(1, a - 2), min(len(ch) - 2, b + 2)
            ch[a0:b0 + 1] = np.linspace(ch[a0 - 1], ch[b0 + 1], b0 - a0 + 3)[1:-1]
            fixed += 1
    return y, fixed


def remove_dc(x):
    return x - np.mean(x, axis=0)


def zero_mean_faded(x):
    """Zero a faded clip's mean without touching its (silent) edges.

    Subtracts a Hann-shaped bump whose area equals the clip's sum -- a component
    with a period of the whole clip (a few Hz for a one-shot), so it is inaudible.
    """
    n = x.shape[0]
    if n < 8:
        return x
    w = np.hanning(n)
    c = np.sum(x, axis=0) / np.sum(w)
    return x - (w[:, None] * c if x.ndim == 2 else w * c)


# ------------------------------------------------------------------------- loops

def _steadiness(e, i, m, ref):
    seg = e[i:i + m]
    return float(np.max(seg) / ref) + float(np.std(seg) / (np.mean(seg) + 1e-9))


def make_loop(x, length_s, xfade_s=2.0, sr=SR, search=None):
    """Cut a seamless loop of `length_s` from a longer recording.

    loop = x[s : s+L] with x[s+L : s+L+X] (the material that naturally follows
    the loop's end) folded over the loop's head with an equal-power (sin/cos)
    crossfade -- correct for uncorrelated, noise-like beds. The sample after the
    loop's last sample is therefore its true continuation: no discontinuity at the
    wrap. `s` is chosen so both crossfaded stretches are steady (no bird call or
    wave crash gets smeared). `search` limits s to (lo_s, hi_s) seconds.
    """
    L, X = int(length_s * sr), int(xfade_s * sr)
    if x.shape[0] < L + X:
        raise ValueError(f"source too short for loop: {x.shape[0] / sr:.1f}s < {(L + X) / sr:.1f}s")
    e, w = rms_env(x, sr, 50.0)
    ref = float(np.median(e)) + 1e-9
    m = max(1, X // w)
    lo, hi = 0, x.shape[0] - L - X
    if search:
        lo, hi = max(lo, int(search[0] * sr)), min(hi, int(search[1] * sr))
    best, s = 1e18, lo
    for cand in range(lo, hi + 1, max(1, int(0.1 * sr))):
        i, j = cand // w, (cand + L) // w
        if j + m > len(e):
            break
        sc = _steadiness(e, i, m, ref) + _steadiness(e, j, m, ref)
        if sc < best:
            best, s = sc, cand
    head, tail = x[s:s + X], x[s + L:s + L + X]
    t = np.linspace(0, np.pi / 2, X)
    fin, fout = np.sin(t), np.cos(t)
    if x.ndim == 2:
        fin, fout = fin[:, None], fout[:, None]
    out = np.array(x[s:s + L], copy=True)
    out[:X] = head * fin + tail * fout
    return out, s


def seam_report(x, sr=SR):
    """Continuity of a loop's wrap point vs. its own interior."""
    y = x if x.ndim == 2 else x[:, None]
    d_wrap = np.abs(y[0] - y[-1])
    d_int = np.abs(np.diff(y, axis=0))
    jump = float(np.max(d_wrap / (np.percentile(d_int, 99.0, axis=0) + 1e-9)))
    w = int(0.25 * sr)
    a, b = rms(y[-w:]), rms(y[:w])
    lvl = abs(db(a) - db(b))
    return {"wrap_jump_vs_p99": jump, "edge_level_diff_db": lvl}


# ------------------------------------------------------------------- similarity

def max_xcorr(a, b):
    """Peak normalised cross-correlation of two mono signals (any lag)."""
    a = a - np.mean(a)
    b = b - np.mean(b)
    na, nb = np.linalg.norm(a), np.linalg.norm(b)
    if na < 1e-9 or nb < 1e-9:
        return 0.0
    n = int(2 ** np.ceil(np.log2(len(a) + len(b))))
    c = np.fft.irfft(np.fft.rfft(a, n) * np.conj(np.fft.rfft(b, n)), n)
    return float(np.max(np.abs(c)) / (na * nb))


def layer(*parts):
    """Sum (signal, gain, offset_s) parts of differing lengths."""
    n = max(int(o * SR) + p.shape[0] for p, g, o in parts)
    st = any(p.ndim == 2 for p, _, _ in parts)
    out = np.zeros((n, 2) if st else n)
    for p, g, o in parts:
        if st and p.ndim == 1:
            p = np.stack([p, p], 1)
        i = int(o * SR)
        out[i:i + p.shape[0]] += p * g
    return out
