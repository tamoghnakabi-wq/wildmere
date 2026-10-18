#!/usr/bin/env python3
"""List the individual sounds find_events() sees in a source file (research helper).

    python3 art-source/audio/events.py <source id> <file glob> [over_floor_db] [min_gap_ms] [max_len_ms]

Prints index, start, duration, peak dBFS, momentary loudness, spectral centroid and
a crude 'brightness-over-time' to help pick takes for recipes.py.
"""
import sys
import numpy as np
import dsp
import build


def centroid(x):
    x = x - np.mean(x)
    X = np.abs(np.fft.rfft(x)) ** 2
    f = np.fft.rfftfreq(len(x), 1 / dsp.SR)
    return float((f * X).sum() / (X.sum() + 1e-20))


def main():
    sid, pat = sys.argv[1], sys.argv[2]
    kw = {}
    if len(sys.argv) > 3:
        kw["over_floor_db"] = float(sys.argv[3])
    if len(sys.argv) > 4:
        kw["min_gap_ms"] = float(sys.argv[4])
    if len(sys.argv) > 5:
        kw["max_len_ms"] = float(sys.argv[5])
    x, p = build.load(sid, pat)
    m = dsp.to_mono(x)
    print(f"{p}\n  {len(m) / dsp.SR:.2f}s  peak {dsp.db(dsp.peak(m)):.1f}  floor {dsp.db(dsp.noise_floor(m)):.1f}")
    for i, (a, b) in enumerate(dsp.find_events(m, **kw)):
        seg = m[a:b]
        print(f"  {i:3d}  t={a / dsp.SR:7.3f}  dur={(b - a) / dsp.SR:5.3f}  pk={dsp.db(dsp.peak(seg)):6.1f}"
              f"  mm={dsp.momentary_max(seg):6.1f}  cen={centroid(seg):6.0f}")


if __name__ == "__main__":
    main()
