#!/usr/bin/env python3
"""QA for the shipped sound set. Writes art-source/audio/qa_report.txt; exit 1 on failure.

Checks every file listed in Assets/Resources/Audio/audio.json:
  format   48 kHz, 16-bit PCM WAV (or Ogg Vorbis at 48 kHz for long beds/music)
  channels mono for positional sounds, stereo for ambience beds and music
  level    sample peak <= -1 dBFS, 4x-oversampled true peak <= -0.5 dBFS,
           no clipped runs, |DC| <= 0.001, ends faded (no click at start/end)
  duration per-group sanity ranges; one-shots start within 40 ms (no dead air)
  loops    wrap-point continuity vs the clip's own interior, level and spectrum
           match across the seam
  variants pairwise peak cross-correlation (any lag) -- near-duplicates fail --
           and no two variants cut from overlapping ranges of the same file
  mix      effective loudness (file loudness + volume) per group: footsteps must
           sit well below ambience, UI subtle; total size budget
"""
import json, os, subprocess, sys
import numpy as np
import dsp

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
RES = os.path.join(ROOT, "Assets", "Resources")
AUD = os.path.join(RES, "Audio")
STEREO = ("amb/", "music/")
# (min s, max s) per group prefix
DUR = {"step/": (0.08, 0.6), "dig/": (0.05, 0.5), "break/": (0.15, 1.6), "place/": (0.05, 0.5),
       "amb/": (30.0, 90.0), "music/": (60.0, 420.0), "thunder/": (1.5, 16.0), "ui/": (0.02, 1.2),
       "bird/": (0.2, 4.0), "deer/": (0.4, 5.0), "rabbit/": (0.05, 0.6), "water/": (0.2, 3.0),
       "player/": (0.08, 1.5), "item/": (0.05, 2.0), "torch/place": (0.1, 2.0),
       "torch/crackle": (8.0, 90.0)}
XCORR_FAIL, XCORR_WARN = 0.90, 0.80


def read_any(path):
    if path.endswith(".wav"):
        x, sr = dsp.read_wav(path)
        return x, sr, "pcm16"
    p = dsp.probe(path)
    x = dsp.decode(path, sr=p["sr"], channels=p["ch"])
    return x, p["sr"], p["codec"]


def true_peak(x):
    y = x if x.ndim == 2 else x[:, None]
    n = y.shape[0]
    if n > 48000 * 20:      # long beds: check in chunks
        return max(true_peak(y[i:i + 48000 * 10]) for i in range(0, n, 48000 * 10))
    X = np.fft.rfft(y, axis=0)
    Z = np.zeros((4 * n // 2 + 1, y.shape[1]), complex)
    Z[:X.shape[0]] = X
    up = np.fft.irfft(Z, 4 * n, axis=0) * 4
    return float(np.max(np.abs(up)))


def clipped_runs(x, thr=0.999, run=3):
    y = np.abs(x if x.ndim == 1 else np.max(np.abs(x), axis=1)) >= thr
    if not y.any():
        return 0
    d = np.diff(np.concatenate([[0], y.astype(int), [0]]))
    s, e = np.nonzero(d == 1)[0], np.nonzero(d == -1)[0]
    return int(np.sum((e - s) >= run))


def spec_db(x, sr):
    m = x if x.ndim == 1 else x.mean(axis=1)
    X = np.abs(np.fft.rfft(m * np.hanning(len(m)))) + 1e-9
    f = np.fft.rfftfreq(len(m), 1 / sr)
    edges = np.geomspace(60, 16000, 25)
    return np.array([20 * np.log10(np.mean(X[(f >= a) & (f < b)])) for a, b in zip(edges[:-1], edges[1:])])


def group_range(name):
    for k, v in DUR.items():
        if name.startswith(k):
            return v
    return (0.02, 120.0)


def main():
    manifest = json.load(open(os.path.join(AUD, "audio.json")))
    prov = json.load(open(os.path.join(HERE, "provenance.json")))
    fails, warns, rows = [], [], []
    eff = {}
    total = 0
    for name, ent in manifest.items():
        lo, hi = group_range(name)
        mono_sigs, mm = [], []
        if not (0.0 <= ent["volume"] <= 1.0):
            fails.append(f"{name}: volume {ent['volume']} outside 0..1")
        for rel in ent["files"]:
            cands = [os.path.join(RES, rel + e) for e in (".wav", ".ogg")]
            path = next((c for c in cands if os.path.exists(c)), None)
            if not path:
                fails.append(f"{name}: missing {rel}")
                continue
            total += os.path.getsize(path)
            x, sr, codec = read_any(path)
            tag = f"{name} {os.path.basename(path)}"
            dur = x.shape[0] / sr
            if sr != 48000:
                fails.append(f"{tag}: sample rate {sr}")
            if path.endswith(".ogg") and not name.startswith(STEREO):
                warns.append(f"{tag}: ogg used for a non-bed sound")
            want_st = name.startswith(STEREO)
            if want_st and x.shape[1] != 2:
                fails.append(f"{tag}: expected stereo, has {x.shape[1]} ch")
            if not want_st and x.shape[1] != 1:
                fails.append(f"{tag}: expected mono, has {x.shape[1]} ch")
            pk = dsp.peak(x)
            tp = true_peak(x)
            dc = float(np.max(np.abs(np.mean(x, axis=0))))
            clips = clipped_runs(x)
            if dsp.db(pk) > -1.0 + 1e-3:
                fails.append(f"{tag}: sample peak {dsp.db(pk):.2f} dBFS > -1")
            if dsp.db(tp) > -0.5:
                (fails if path.endswith(".wav") else warns).append(f"{tag}: true peak {dsp.db(tp):.2f} dBFS")
            if clips:
                fails.append(f"{tag}: {clips} clipped runs")
            if dc > 0.001:
                fails.append(f"{tag}: DC offset {dc:.4f}")
            if not (lo <= dur <= hi):
                fails.append(f"{tag}: duration {dur:.2f}s outside {lo}-{hi}s")
            m = dsp.to_mono(x) if x.shape[1] > 1 else x[:, 0]
            if not ent["loop"]:
                edge = max(abs(float(m[0])), abs(float(m[-1])))
                if edge > 0.01:
                    fails.append(f"{tag}: unfaded edge ({edge:.3f})")
                e, w = dsp.envelope(m, sr, 1.0)
                onset = np.argmax(e > 0.1 * np.max(e)) * w / sr
                if dur < 4 and onset > 0.04 and not name.startswith(("amb/", "music/", "thunder/far", "torch/")):
                    warns.append(f"{tag}: onset at {onset * 1000:.0f} ms")
            seam = ""
            if ent["loop"]:
                s = dsp.seam_report(x, sr)
                n = min(x.shape[0] // 4, sr)
                sd = float(np.sqrt(np.mean((spec_db(x[-n:], sr) - spec_db(x[:n], sr)) ** 2)))
                seam = f" seam jump {s['wrap_jump_vs_p99']:.2f} lvl {s['edge_level_diff_db']:.2f}dB spec {sd:.1f}dB"
                if s["wrap_jump_vs_p99"] > 1.0:
                    fails.append(f"{tag}: loop wrap discontinuity {s['wrap_jump_vs_p99']:.2f}x interior p99")
                if s["edge_level_diff_db"] > 3.0:
                    fails.append(f"{tag}: loop level jump {s['edge_level_diff_db']:.2f} dB")
                elif s["edge_level_diff_db"] > 1.5:
                    warns.append(f"{tag}: loop level step {s['edge_level_diff_db']:.2f} dB")
                if sd > 6.0:
                    warns.append(f"{tag}: loop spectral step {sd:.1f} dB")
            lm = dsp.loudness(x, sr) if dur > 3 else dsp.momentary_max(x, sr)
            mm.append(dsp.momentary_max(x, sr) if dur <= 3 else lm)
            mono_sigs.append(m[:int(min(len(m), 3 * sr))])
            rows.append(f"{tag:44s} {x.shape[1]}ch {dur:6.2f}s pk {dsp.db(pk):6.2f} tp {dsp.db(tp):6.2f} "
                        f"{'LUFS' if dur > 3 else 'Mmax'} {lm:6.1f} dc {dc:.4f}{seam}")
        # variants
        k = len(mono_sigs)
        worst = 0.0
        for i in range(k):
            for j in range(i + 1, k):
                c = dsp.max_xcorr(mono_sigs[i], mono_sigs[j])
                worst = max(worst, c)
                if c >= XCORR_FAIL:
                    fails.append(f"{name}: variants {i + 1} and {j + 1} near-identical (xcorr {c:.2f})")
                elif c >= XCORR_WARN:
                    warns.append(f"{name}: variants {i + 1} and {j + 1} similar (xcorr {c:.2f})")
        pv = prov.get(name, [])
        spans = [(l["source"], l["file"], l["t0"], l["t1"]) for e in pv for l in e["layers"][:1]]
        for i in range(len(spans)):
            for j in range(i + 1, len(spans)):
                a, b = spans[i], spans[j]
                if a[:2] == b[:2] and min(a[3], b[3]) - max(a[2], b[2]) > 0.02:
                    fails.append(f"{name}: variants {i + 1} and {j + 1} cut from overlapping ranges of {a[1]}")
        if mm:
            eff[name] = (float(np.max(mm)) + 20 * np.log10(max(ent["volume"], 1e-4)), k, worst)
    # mix relationships
    amb = [v[0] for n, v in eff.items() if n.startswith("amb/")]
    steps = [v[0] for n, v in eff.items() if n.startswith("step/")]
    ui = [v[0] for n, v in eff.items() if n.startswith("ui/")]
    if amb and steps:
        if max(steps) > np.median(amb) - 3.0:
            fails.append(f"mix: loudest footsteps {max(steps):.1f} not 3 dB below median ambience {np.median(amb):.1f}")
    if ui and amb and max(ui) > np.median(amb):
        warns.append(f"mix: UI {max(ui):.1f} louder than median ambience {np.median(amb):.1f}")
    mb = total / 1e6
    if mb > 120:
        fails.append(f"size: {mb:.1f} MB > 120 MB")
    out = ["Wildmere audio QA", "=" * 17, f"{len(manifest)} names, {sum(len(e['files']) for e in manifest.values())} files, {mb:.1f} MB", ""]
    out += ["Effective level per name (loudest variant + 20log10(volume); beds = integrated LUFS):"]
    for n in sorted(eff):
        v = eff[n]
        out.append(f"  {n:24s} {v[0]:6.1f}  variants {v[1]}  max xcorr {v[2]:.2f}")
    out += ["", f"FAIL ({len(fails)}):"] + ["  " + f for f in fails]
    out += ["", f"WARN ({len(warns)}):"] + ["  " + w for w in warns]
    out += ["", "Per file:"] + ["  " + r for r in rows]
    open(os.path.join(HERE, "qa_report.txt"), "w").write("\n".join(out) + "\n")
    print("\n".join(out[:4]))
    print(f"FAIL {len(fails)}  WARN {len(warns)}")
    for f in fails[:40]:
        print("  FAIL", f)
    for w in warns[:25]:
        print("  warn", w)
    return 1 if fails else 0


if __name__ == "__main__":
    sys.exit(main())
