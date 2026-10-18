#!/usr/bin/env python3
"""Loudness / brightness / event timeline of a long recording (research helper).

    python3 art-source/audio/timeline.py <source id> <file glob> [window s]

Per window: short-term loudness, peak, low-band (<200 Hz) share, centroid and the
number of distinct events -- used to pick steady, lively or quiet stretches.
"""
import sys
import numpy as np
import dsp, build

sid, pat = sys.argv[1], sys.argv[2]
win = float(sys.argv[3]) if len(sys.argv) > 3 else 10.0
x, p = build.load(sid, pat)
m = dsp.to_mono(x)
w = int(win * dsp.SR)
print(p, f"{len(m) / dsp.SR:.1f}s")
for i in range(0, len(m) - w + 1, w):
    seg = m[i:i + w]
    X = np.abs(np.fft.rfft(seg)) ** 2
    f = np.fft.rfftfreq(len(seg), 1 / dsp.SR)
    low = X[f < 200].sum() / (X.sum() + 1e-20)
    cen = (f * X).sum() / (X.sum() + 1e-20)
    ev = len(dsp.find_events(seg, over_floor_db=10))
    print(f"  {i / dsp.SR:6.0f}s  L {dsp.loudness(seg):6.1f}  pk {dsp.db(dsp.peak(seg)):6.1f}  "
          f"low {low:4.2f}  cen {cen:6.0f}  ev {ev:3d}")
