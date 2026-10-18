#!/usr/bin/env python3
"""Browse Wikimedia Commons categories for CC0 / public-domain recordings (research helper).

robots.txt for commons.wikimedia.org disallows /w/ (incl. the API) and /wiki/Special:,
but allows /wiki/Category: and /wiki/File: pages; upload.wikimedia.org is allowed.
So this reads the normal category and file pages, slowly.

    python3 -I art-source/audio/survey_commons.py cat  "Sounds_of_thunder" [...]
    python3 -I art-source/audio/survey_commons.py file "File:Thunder.ogg" [...]
"""
import html, json, os, re, sys, time, urllib.parse, urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
CACHE = os.path.join(HERE, "cache")
UA = {"User-Agent": "WildmereAudioSurvey/1.0 (tamoghnakabi-wq; game asset provenance check) "
                    "Mozilla/5.0 (Macintosh)"}
BASE = "https://commons.wikimedia.org/wiki/"
AUD = (".ogg", ".oga", ".wav", ".flac", ".mp3", ".opus", ".webm")


def get(u):
    for _ in range(3):
        try:
            return urllib.request.urlopen(urllib.request.Request(u, headers=UA),
                                          timeout=60).read().decode("utf-8", "ignore")
        except Exception as e:  # noqa: BLE001
            print("  !!", e, u, file=sys.stderr)
            time.sleep(5)
    return ""


def cat(name):
    s = get(BASE + "Category:" + urllib.parse.quote(name))
    subs = sorted(set(urllib.parse.unquote(m) for m in
                      re.findall(r'href="/wiki/Category:([^"#?]+)"', s)))
    files = sorted(set(urllib.parse.unquote(m) for m in
                       re.findall(r'href="/wiki/(File:[^"#?]+)"', s)
                       if urllib.parse.unquote(m).lower().endswith(AUD)))
    return subs, files


def fileinfo(title):
    s = get(BASE + urllib.parse.quote(title))
    s2 = re.sub(r"<script.*?</script>|<style.*?</style>", "", s, flags=re.S)
    t = html.unescape(re.sub(r"\s+", " ", re.sub(r"<[^>]+>", " ", s2)))
    i = t.find("Summary [ edit ]")
    j = t.find("Licensing [ edit ]", i)
    k = t.find("File history", j)
    summ = t[i + 16:j].strip() if i >= 0 and j > i else ""
    licsec = t[j + 18:k].strip() if j >= 0 and k > j else ""
    au = re.search(r"Author (.*?)(?: Permission| Other versions| Camera location|$)", summ)
    ln = re.search(r"length ([^,)]+)", t)
    orig = re.search(r'href="(https://upload\.wikimedia\.org/wikipedia/commons/[0-9a-f]/[0-9a-f]{2}/[^"]+)"', s)
    lic = []
    low = licsec.lower()
    if "creative commons cc0" in low or "cc0 1.0" in low or "cc zero" in low or "cc0" in low:
        lic.append("CC0")
    if "public domain" in low:
        lic.append("PD")
    for m in re.findall(r"(Attribution(?:-Share ?Alike)? \d\.\d[^ ]*)", licsec):
        lic.append(m)
    if "gnu free documentation" in low:
        lic.append("GFDL")
    return {
        "title": title, "page": BASE + urllib.parse.quote(title),
        "url": html.unescape(orig.group(1)) if orig else None,
        "licence_hits": sorted(set(lic)), "licence_text": licsec[:400],
        "author": au.group(1).strip()[:120] if au else "?",
        "desc": summ[:300], "length": ln.group(1).strip() if ln else "?",
    }


if __name__ == "__main__":
    os.makedirs(CACHE, exist_ok=True)
    mode, args = sys.argv[1], sys.argv[2:]
    store = os.path.join(CACHE, "commons.json")
    db = json.load(open(store)) if os.path.exists(store) else {"cats": {}, "files": {}}
    for a in args:
        if mode == "cat":
            subs, files = cat(a)
            db["cats"][a] = {"subs": subs, "files": files}
            print(f"== {a}: {len(subs)} subcats, {len(files)} audio files")
            for x in subs:
                print("   [cat]", x)
            for x in files:
                print("   ", x)
        else:
            if a in db["files"]:
                info = db["files"][a]
            else:
                info = fileinfo(a)
                db["files"][a] = info
            print(f"{info['licence_hits']}  {info['length']}  {info['author'][:40]} | {a} | {info['desc'][:120]}")
        json.dump(db, open(store, "w"), indent=1)
        time.sleep(2.0)
