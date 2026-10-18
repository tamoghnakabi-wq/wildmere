#!/usr/bin/env python3
"""Print a quick acoustic summary of source files (research helper).

    python3 art-source/audio/inspect_src.py <file-or-dir> [...]

Per file: duration, channels, sample rate, peak dBFS, noise floor, spectral
centroid and 85 % rolloff, and how many distinct events find_events() sees.
"""
import os, sys
import numpy as np
import dsp

EXT = (".wav", ".ogg", ".mp3", ".flac", ".aif", ".aiff", ".opus", ".webm", ".m4a")


def summary(path):
    p = dsp.probe(path) or {}
    x = dsp.decode(path)
    m = dsp.to_mono(x)
    n = min(len(m), 1 << 20)
    seg = dsp.highpass(m[:n] - np.mean(m[:n]), 30.0)
    X = np.abs(np.fft.rfft(seg)) ** 2
    f = np.fft.rfftfreq(len(seg), 1 / dsp.SR)
    tot = X.sum() + 1e-20
    cen = float((f * X).sum() / tot)
    roll = float(f[np.searchsorted(np.cumsum(X), 0.85 * tot)]) if len(X) else 0
    ev = dsp.find_events(m)
    fl = dsp.noise_floor(m)
    corr = 0.0
    if x.shape[1] == 2:
        l, r = x[:, 0], x[:, 1]
        corr = float(np.sum(l * r) / np.sqrt(np.sum(l * l) * np.sum(r * r) + 1e-20))
    return (f"{len(m) / dsp.SR:7.2f}s ch{p.get('ch', '?')} {p.get('sr', '?')}Hz {p.get('codec', '?'):6s} "
            f"pk{dsp.db(dsp.peak(x)):6.1f} fl{dsp.db(fl):6.1f} cen{cen:6.0f} r85 {roll:6.0f} "
            f"lc{corr:+.2f} ev{len(ev):3d}")


def main(args):
    files = []
    for a in args:
        if os.path.isdir(a):
            for dp, _, fns in os.walk(a):
                files += [os.path.join(dp, f) for f in sorted(fns) if f.lower().endswith(EXT)]
        else:
            files.append(a)
    for f in files:
        try:
            print(f"{summary(f)}  {os.path.relpath(f)}")
        except Exception as e:  # noqa: BLE001
            print(f"!! {f}: {e}")


if __name__ == "__main__":
    main(sys.argv[1:])
