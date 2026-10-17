#!/usr/bin/env python3
"""Build Wildmere's shipped sound set from the downloaded CC0 recordings.

    python3 art-source/audio/build.py              # rebuild everything
    python3 art-source/audio/build.py step/ dig/   # rebuild only names with these prefixes

For every output name in recipes.py this cuts the chosen takes out of the source
recordings, then: high-passes rumble, repairs isolated clicks (field recordings),
trims, fades, normalises loudness per group, resamples to 48 kHz 16-bit, folds
positional one-shots to mono, keeps beds/music stereo, and makes loops seamless
with an equal-power crossfade at the seam.

Outputs:
  Assets/Resources/Audio/<group>/<name>_<nn>.wav (or .ogg for long beds/music)
  Assets/Resources/Audio/audio.json          -- the runtime contract
  art-source/audio/provenance.json           -- per-file source, time range, licence
  AUDIO_CREDITS.md                           -- generated from provenance.json
"""
import glob, json, os, sys
import numpy as np
import dsp
import recipes

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
DL = os.path.join(HERE, "downloads")
OUT = os.path.join(ROOT, "Assets", "Resources", "Audio")
PROV = os.path.join(HERE, "provenance.json")
SR = dsp.SR

_cache = {}


def src_path(sid, pat):
    hits = sorted(glob.glob(os.path.join(DL, sid, "**", pat), recursive=True))
    hits = [h for h in hits if os.path.isfile(h) and not h.endswith(".part")]
    if not hits:
        raise FileNotFoundError(f"{sid}: no file matches {pat!r}")
    if len(hits) > 1:
        exact = [h for h in hits if os.path.basename(h) == pat]
        if len(exact) == 1:
            return exact[0]
        raise ValueError(f"{sid}: {pat!r} is ambiguous: {[os.path.basename(h) for h in hits][:6]}")
    return hits[0]


def load(sid, pat):
    p = src_path(sid, pat)
    if p not in _cache:
        npy = os.path.join(HERE, "cache", "decoded", sid + "__" + os.path.basename(p) + ".npy")
        if os.path.exists(npy) and os.path.getmtime(npy) > os.path.getmtime(p):
            _cache[p] = np.load(npy)
        else:
            x = dsp.decode(p)
            os.makedirs(os.path.dirname(npy), exist_ok=True)
            np.save(npy, x.astype(np.float32))
            _cache[p] = x.astype(np.float32)
    return _cache[p].astype(np.float64), p


def archive_member(sid, path):
    """'archive.zip -> inner/file.wav' for credits, or the plain file name."""
    rel = os.path.relpath(path, os.path.join(DL, sid))
    parts = rel.split(os.sep)
    if parts[0].endswith(".d") and len(parts) > 1:
        return parts[0][:-2] + " -> " + "/".join(parts[1:])
    return rel


def take(t):
    """Cut one take. t: dict(s=source id, f=file glob, t0/t1 seconds, ev=event index,
    evp=find_events kwargs, g=gain dB, rate=varispeed, ch='L'/'R'/None)."""
    x, p = load(t["s"], t["f"])
    t0, t1 = t.get("t0"), t.get("t1")
    a = int(t0 * SR) if t0 is not None else 0
    b = int(t1 * SR) if t1 is not None else x.shape[0]
    seg = x[a:b]
    if "ev" in t:
        evs = dsp.find_events(dsp.to_mono(seg), **t.get("evp", {}))
        if t["ev"] >= len(evs):
            raise IndexError(f"{t['s']}/{t['f']}: event {t['ev']} of {len(evs)}")
        e0, e1 = evs[t["ev"]]
        pad_post = int(t.get("tail", 0.0) * SR)
        seg = seg[e0:min(seg.shape[0], e1 + pad_post)]
        a, b = a + e0, a + e0 + seg.shape[0]
    else:
        b = a + seg.shape[0]
    if t.get("ch") == "L":
        seg = np.stack([seg[:, 0], seg[:, 0]], 1)
    elif t.get("ch") == "R":
        seg = np.stack([seg[:, 1], seg[:, 1]], 1)
    if t.get("rate", 1.0) != 1.0:
        seg = dsp.resample_ratio(seg, t["rate"])
    seg = seg * dsp.undb(t.get("g", 0.0))
    prov = {"source": t["s"], "file": archive_member(t["s"], p),
            "t0": round(a / SR, 3), "t1": round(b / SR, 3)}
    if t.get("rate", 1.0) != 1.0:
        prov["varispeed"] = t["rate"]
    return seg, prov


# ----------------------------------------------------------------- processing

def finish_oneshot(x, P):
    """Shared one-shot chain. P is the recipe's processing dict."""
    m = dsp.to_mono(x) if P.get("mono", True) else x
    m = dsp.remove_dc(m)
    if P.get("declick"):
        m, _ = dsp.declick(m)
    if P.get("hp"):
        m = dsp.highpass(m, P["hp"], order=4)
    if P.get("lp"):
        m = dsp.lowpass(m, P["lp"], order=P.get("lp_order", 2))
    for fc, gdb, q in P.get("eq", []):
        m = dsp.eq_peak(m, fc, gdb, q)
    if P.get("shelf"):
        fc, gdb, hi = P["shelf"]
        m = dsp.shelf(m, fc, gdb, high=hi)
    if P.get("trim", True):
        m = dsp.trim(m, thresh_db=P.get("trim_db", -48.0), pre_ms=P.get("pre_ms", 4.0),
                     post_ms=P.get("post_ms", 40.0), lead_db=P.get("lead_db"))
    if P.get("max_s"):
        n = int(P["max_s"] * SR)
        if m.shape[0] > n:
            m = m[:n]
    m = dsp.fade(m, P.get("fade_in_ms", 1.5), P.get("fade_out_ms", 40.0))
    return dsp.zero_mean_faded(m)


def finish_bed(x, P):
    y = dsp.remove_dc(x)
    if y.ndim == 1:
        y = np.stack([y, y], 1)
    if P.get("declick", True):
        y, n = dsp.declick(y)
        P["_declicked"] = n
    return y


def loopify(y, P):
    L = P.get("loop_s", 50.0)
    X = P.get("xfade_s", 3.0)
    L = min(L, (y.shape[0] / SR) - X - 0.5)
    out, s = dsp.make_loop(y, L, X, search=P.get("search"))
    hp = P.get("hp", 30.0)
    if hp:
        out = dsp.highpass(out, hp, order=4, circular=True)
    if P.get("lp"):
        out = dsp.lowpass(out, P["lp"], order=P.get("lp_order", 2), circular=True)
    for fc, gdb, q in P.get("eq", []):
        out = dsp.eq_peak(out, fc, gdb, q, circular=True)
    if P.get("shelf"):
        fc, gdb, hi = P["shelf"]
        out = dsp.shelf(out, fc, gdb, high=hi, circular=True)
    return out, s


def level(clips, P, kind):
    """Normalise a group: one-shots to a momentary-loudness target, beds to an
    integrated target; peaks never above the ceiling (default -1 dBFS)."""
    ceil = P.get("ceiling_db", -1.0)
    tgt = P.get("lufs", -20.0)
    out = []
    if P.get("match", "each") == "group":
        # keep natural relative levels inside the group; scale the group as one
        meas = [dsp.momentary_max(c) for c in clips]
        g = dsp.undb(tgt - max(meas))
        pk = max(dsp.peak(c) for c in clips) * g
        if pk > dsp.undb(ceil):
            g *= dsp.undb(ceil) / pk
        return [c * g for c in clips]
    for c in clips:
        if kind == "bed":
            y, _ = dsp.gain_to(c, tgt, ceil, measure=dsp.loudness)
        elif P.get("limit_db", 6.0) > 0:
            y = dsp.level_limited(c, tgt, ceil, max_gr_db=P.get("limit_db", 6.0))
            y = dsp.zero_mean_faded(y)           # limiting can be asymmetric
            tp = dsp.true_peak(y)
            if tp > dsp.undb(ceil - 0.5):
                y = y * (dsp.undb(ceil - 0.55) / tp)
        else:
            y, _ = dsp.gain_to(c, tgt, ceil, measure=dsp.momentary_max)
        out.append(y)
    return out


# ------------------------------------------------------------------- outputs

def write_clip(name, i, y, fmt):
    group, base = name.split("/", 1)
    rel = f"Audio/{group}/{base}_{i:02d}"
    path = os.path.join(OUT, group, f"{base}_{i:02d}.{fmt}")
    if fmt == "ogg":
        dsp.write_ogg(path, y, quality=6)
    else:
        dsp.write_wav(path, y)
    return rel, path


def clear_old(name):
    group, base = name.split("/", 1)
    for ext in ("wav", "ogg"):
        for p in glob.glob(os.path.join(OUT, group, f"{base}_[0-9][0-9].{ext}")):
            os.remove(p)
            meta = p + ".meta"
            if os.path.exists(meta):
                os.remove(meta)


def build_one(name, R):
    kind = R.get("kind", "oneshot")
    P = dict(R.get("p", {}))
    clips, provs = [], []
    for spec in R["takes"]:
        parts = spec if isinstance(spec, list) else [spec]
        layers, lprov = [], []
        for t in parts:
            x, pv = take(t)
            off = t.get("at", 0.0)
            layers.append((x, 1.0, off))
            pv["role"] = t.get("role", "take")
            lprov.append(pv)
        x = dsp.layer(*layers) if len(layers) > 1 else layers[0][0]
        if kind == "oneshot":
            y = finish_oneshot(x, P)
        else:
            y = finish_bed(x, P)
            if R.get("loop", False):
                y, _ = loopify(y, P)
            else:
                if P.get("hp", 30.0):
                    y = dsp.highpass(y, P.get("hp", 30.0), order=4)
                if P.get("max_s") and y.shape[0] > P["max_s"] * SR:
                    y = y[:int(P["max_s"] * SR)]
                y = dsp.fade(y, P.get("fade_in_ms", 200.0), P.get("fade_out_ms", 1500.0))
        clips.append(y)
        provs.append(lprov)
    clips = level(clips, P, "bed" if kind != "oneshot" else "oneshot")
    clear_old(name)
    files, entries = [], []
    for i, (y, pv) in enumerate(zip(clips, provs), 1):
        rel, path = write_clip(name, i, y, R.get("fmt", "wav"))
        files.append(rel)
        entries.append({"file": os.path.relpath(path, ROOT), "layers": pv,
                        "seconds": round(y.shape[0] / SR, 3),
                        "channels": 1 if y.ndim == 1 else y.shape[1]})
    manifest = {"files": files, "volume": R.get("volume", 0.8), "loop": bool(R.get("loop", False))}
    return manifest, entries


def credits(prov, sources):
    lines = ["# Audio credits", "",
             "Every sound in Wildmere is a real recording made by the people credited",
             "below; nothing is a Minecraft sound or ripped from a game. All sources are",
             "**CC0 1.0** (public-domain dedication) unless the row says otherwise --",
             "no attribution is legally required, but every file stays traceable.",
             "Files marked *PD-self* were released by their recordist with a plain",
             "public-domain dedication (\"I release this work into the public domain ...",
             "if not legally possible, I grant anyone the right to use this work for any",
             "purpose, without any conditions\"), which is equivalent in effect to CC0.",
             "",
             "Processing (cutting, filtering, loudness, looping, layering) is done by",
             "`art-source/audio/build.py`; `art-source/audio/fetch.py` re-downloads every",
             "source and `art-source/audio/sources.json` records file hashes.",
             "", "## Sources", "",
             "| Id | Title | Author | Licence | Page |", "|---|---|---|---|---|"]
    used = sorted({l["source"] for v in prov.values() for e in v for l in e["layers"]})
    for sid in used:
        s = sources[sid]
        lic = "CC0 1.0" if s["licence"].startswith("CC0") else "PD-self"
        lines.append(f"| `{sid}` | {s['title']} | {s['author']} | {lic} | <{s['page']}> |")
    lines += ["", "## Per-file provenance", "",
              "Time ranges are in seconds within the original file. Layered sounds list",
              "every recording that went into them.", "",
              "| Shipped file | Source page | Author | Licence | Original file [range] |",
              "|---|---|---|---|---|"]
    for name in sorted(prov):
        for e in prov[name]:
            for k, l in enumerate(e["layers"]):
                s = sources[l["source"]]
                lic = "CC0 1.0" if s["licence"].startswith("CC0") else "PD-self"
                f = e["file"].replace("Assets/Resources/", "") if k == 0 else "&nbsp;&nbsp;+ layer"
                rng = f"{l['t0']:.2f}-{l['t1']:.2f}s"
                extra = f", varispeed {l['varispeed']}" if "varispeed" in l else ""
                lines.append(f"| {f} | <{s['page']}> | {s['author']} | {lic} | "
                             f"`{l['file']}` [{rng}{extra}] |")
    return "\n".join(lines) + "\n"


def main(prefixes):
    sources = json.load(open(os.path.join(HERE, "sources.json")))
    prov = json.load(open(PROV)) if os.path.exists(PROV) else {}
    mpath = os.path.join(OUT, "audio.json")
    manifest = json.load(open(mpath)) if os.path.exists(mpath) else {}
    names = [n for n in recipes.RECIPES if not prefixes or any(n.startswith(p) for p in prefixes)]
    for n in names:
        try:
            m, e = build_one(n, recipes.RECIPES[n])
        except Exception as ex:  # noqa: BLE001
            print(f"!! {n}: {type(ex).__name__}: {ex}")
            continue
        manifest[n] = m
        prov[n] = e
        print(f"{n:24s} {len(m['files'])} file(s)  vol {m['volume']:.2f}  loop {m['loop']}")
    # drop names no longer in recipes
    for n in list(manifest):
        if n not in recipes.RECIPES:
            manifest.pop(n)
            prov.pop(n, None)
    manifest = {k: manifest[k] for k in sorted(manifest)}
    os.makedirs(OUT, exist_ok=True)
    json.dump(manifest, open(mpath, "w"), indent=2)
    json.dump({k: prov[k] for k in sorted(prov)}, open(PROV, "w"), indent=1)
    open(os.path.join(ROOT, "AUDIO_CREDITS.md"), "w").write(credits(prov, sources))


if __name__ == "__main__":
    main(sys.argv[1:])
