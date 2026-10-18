#!/usr/bin/env python3
"""Turn WM_QA_SOAK lines from a player log into a CSV and a growth summary.

usage: qa/soak_report.py qa/results/soak.log [qa/results/soak.csv]
Growth is measured from the first sample after warm-up (t >= 60 s) to the last one; a metric
that grows > 20 % and keeps rising across the run is flagged.
"""
import csv, re, sys

KEYS = ["t", "phase", "monoMB", "unityMB", "reservedMB", "gfxMB", "meshes", "gameObjects", "materials", "chunks", "meshed",
        "verts", "drops", "animals", "props", "frames", "avgMs", "p99Ms", "maxMs", "gc0", "errors"]


def main():
    log = sys.argv[1]
    out = sys.argv[2] if len(sys.argv) > 2 else log.rsplit(".", 1)[0] + "-samples.csv"
    rows = []
    for line in open(log, errors="replace"):
        if not line.startswith("WM_QA_SOAK "):
            continue
        d = dict(re.findall(r"(\w+)=([^\s]+)", line))
        rows.append(d)
    with open(out, "w", newline="") as f:
        w = csv.writer(f)
        w.writerow(KEYS)
        for d in rows:
            w.writerow([d.get(k, "") for k in KEYS])
    print(f"{len(rows)} samples -> {out}")
    warm = [d for d in rows if float(d["t"]) >= 60] or rows
    if len(warm) < 2:
        return
    a, b = warm[0], warm[-1]
    for k in ["monoMB", "unityMB", "reservedMB", "gfxMB", "meshes", "gameObjects", "materials", "chunks", "verts", "drops"]:
        try:
            x, y = float(a[k]), float(b[k])
        except (KeyError, ValueError):
            continue
        series = [float(d[k]) for d in warm if d.get(k) not in (None, "")]
        mx = max(series)
        g = (y - x) / x * 100 if x else 0
        rising = all(series[i] <= series[i + 1] + 1e-9 for i in range(len(series) - 1))
        flag = "FLAG" if g > 20 and (rising or y >= mx * 0.98) else "ok"
        print(f"{k:12s} t={a['t']}s:{x:10.1f}  t={b['t']}s:{y:10.1f}  max={mx:10.1f}  growth={g:+6.1f}%  {flag}")
    fr = [(float(d["avgMs"]), float(d["p99Ms"]), float(d["maxMs"])) for d in warm]
    print("frame avgMs: min %.2f max %.2f | p99 max %.2f | worst frame %.1f ms" %
          (min(f[0] for f in fr), max(f[0] for f in fr), max(f[1] for f in fr), max(f[2] for f in fr)))


if __name__ == "__main__":
    main()
