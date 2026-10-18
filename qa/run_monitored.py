#!/usr/bin/env python3
"""Launch the Wildmere player and sample its CPU use and resident memory from outside.

CPU% is computed from deltas of cumulative CPU time (ps -o time=), so it is exact per
interval (100% = one full core), not ps's decaying average.  Each sample is tagged with the
latest WM_QA_IDLE/WM_QA_SOAK phase line found in the player log.

usage: qa/run_monitored.py --out qa/results/idle --interval 2 -- -qatest idle -nosave
       (default app: Build/mac/Wildmere.app relative to the repo root)
"""
import argparse, csv, os, re, statistics, subprocess, sys, time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def cputime(pid):
    out = subprocess.run(["ps", "-o", "time=,rss=", "-p", str(pid)], capture_output=True, text=True).stdout.strip()
    if not out:
        return None, None
    t, rss = out.split()
    secs = 0.0
    parts = t.replace("-", ":").split(":")
    for p in parts:
        secs = secs * 60 + float(p)
    return secs, int(rss) / 1024.0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--app", default=os.path.join(ROOT, "Build/mac/Wildmere.app/Contents/MacOS/Wildmere"))
    ap.add_argument("--out", required=True, help="output prefix (writes <out>.csv, <out>.log, <out>-summary.txt)")
    ap.add_argument("--interval", type=float, default=2.0)
    ap.add_argument("--res", default="1600x900")
    ap.add_argument("args", nargs=argparse.REMAINDER)
    a = ap.parse_args()
    args = [x for x in a.args if x != "--"]
    os.makedirs(os.path.dirname(os.path.abspath(a.out)), exist_ok=True)
    log = os.path.abspath(a.out + ".log")
    if os.path.exists(log):
        os.remove(log)
    w, h = a.res.split("x")
    cmd = [a.app, "-logFile", log, "-screen-fullscreen", "0", "-screen-width", w, "-screen-height", h] + args
    print("launch:", " ".join(cmd), flush=True)
    proc = subprocess.Popen(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    rows = []
    phase = "boot"
    t0 = time.time()
    last_cpu, last_t = None, None
    pos = 0
    phase_re = re.compile(r"^WM_QA_(?:IDLE|SOAK) .*?phase=(\w+)")
    while proc.poll() is None:
        time.sleep(a.interval)
        now = time.time()
        if os.path.exists(log):
            with open(log, "r", errors="replace") as f:
                f.seek(pos)
                for line in f:
                    m = phase_re.match(line)
                    if m:
                        phase = m.group(1)
                pos = f.tell()
        c, rss = cputime(proc.pid)
        if c is None:
            break
        if last_cpu is not None:
            pct = 100.0 * (c - last_cpu) / (now - last_t)
            rows.append((round(now - t0, 1), phase, round(pct, 1), round(rss, 1)))
        last_cpu, last_t = c, now
    rc = proc.wait()
    with open(a.out + ".csv", "w", newline="") as f:
        wr = csv.writer(f)
        wr.writerow(["t_s", "phase", "cpu_pct_of_one_core", "rss_mb"])
        wr.writerows(rows)
    lines = [f"exit={rc} samples={len(rows)} interval={a.interval}s args={' '.join(args)}"]
    phases = []
    for r in rows:
        if r[1] not in phases:
            phases.append(r[1])
    for p in phases:
        v = [r[2] for r in rows if r[1] == p]
        m = [r[3] for r in rows if r[1] == p]
        # skip the first 3 samples of each phase (transition/loading)
        vs = v[3:] if len(v) > 6 else v
        lines.append(f"phase={p:8s} n={len(v):3d} cpu% median={statistics.median(vs):6.1f} mean={statistics.mean(vs):6.1f} "
                     f"max={max(vs):6.1f}  rssMB first={m[0]:.0f} last={m[-1]:.0f} max={max(m):.0f}")
    txt = "\n".join(lines)
    print(txt)
    with open(a.out + "-summary.txt", "w") as f:
        f.write(txt + "\n")
    return rc


if __name__ == "__main__":
    sys.exit(main())
