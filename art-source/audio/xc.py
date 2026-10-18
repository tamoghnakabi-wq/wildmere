#!/usr/bin/env python3
"""Pairwise similarity of candidate takes (research helper).

    python3 art-source/audio/xc.py <source id> <glob> [<glob> ...]

Prints the peak normalised cross-correlation matrix so near-copies (the same
recording pitched or re-edited) can be avoided before they reach recipes.py.
"""
import glob, os, sys
import dsp, build

sid, pats = sys.argv[1], sys.argv[2:]
files = []
for p in pats:
    files += sorted(glob.glob(os.path.join(build.DL, sid, "**", p), recursive=True))
sig = []
for f in files:
    x = dsp.to_mono(dsp.decode(f))
    sig.append(dsp.highpass(x[:dsp.SR * 2], 60))
names = [os.path.basename(f)[:22] for f in files]
print(" " * 24 + " ".join(f"{i:4d}" for i in range(len(files))))
for i, a in enumerate(sig):
    row = [f"{dsp.max_xcorr(a, b):4.2f}" if j != i else "  - " for j, b in enumerate(sig)]
    print(f"{i:2d} {names[i]:22s} " + " ".join(row))
