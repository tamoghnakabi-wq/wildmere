#!/usr/bin/env python3
"""Survey OpenGameArt for CC0 sound effects and music (research helper, not part of the build).

robots.txt: opengameart.org disallows /search/ but not /art-search-advanced or /content/.
It asks for a crawl delay, so requests are spaced out (default 8 s).

    python3 -I art-source/audio/survey_oga.py search  [delay]   # -> cache/oga_search.json
    python3 -I art-source/audio/survey_oga.py detail  [delay]   # -> cache/oga_detail.json (pages in cache/oga_wanted.txt)
"""
import urllib.request, urllib.parse, re, html, json, time, sys, os

HERE = os.path.dirname(os.path.abspath(__file__))
CACHE = os.path.join(HERE, "cache")
UA = {"User-Agent": "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 "
                    "(KHTML, like Gecko) Chrome/124.0 Safari/537.36"}
SFX = ["footstep", "footsteps", "steps", "walking", "snow", "gravel", "sand", "grass", "leaves",
       "wood", "dig", "digging", "shovel", "pickaxe", "mining", "rock", "stone", "break",
       "breaking", "glass", "crunch", "crumble", "debris", "impact", "thud", "dirt", "mud",
       "ambience", "ambient", "forest", "birds", "bird", "nature", "night", "crickets", "insects",
       "wind", "blizzard", "desert", "cave", "drip", "underwater", "bubbles", "river", "stream",
       "creek", "waterfall", "ocean", "waves", "sea", "beach", "rain", "storm", "thunder",
       "deer", "animal", "rabbit", "splash", "swim", "water", "hurt", "pain", "grunt", "voice",
       "jump", "landing", "cloth", "fabric", "pickup", "pop", "craft", "hammer", "saw", "tool",
       "torch", "fire", "campfire", "crackle", "match", "interface", "ui", "click", "menu",
       "button", "error", "field recording", "loop", "foley", "chop", "axe", "leaf", "bush"]
MUSIC = ["ambient", "calm", "peaceful", "piano", "acoustic", "guitar", "relaxing", "exploration",
         "nature", "meditation", "atmospheric", "gentle", "soft", "forest", "harp", "flute", "morning"]
AUDIO_EXT = (".wav", ".ogg", ".mp3", ".flac", ".aif", ".aiff", ".7z", ".zip", ".rar", ".tar.gz")


def get(u):
    for _ in range(3):
        try:
            req = urllib.request.Request(u, headers=UA)
            return urllib.request.urlopen(req, timeout=60).read().decode("utf-8", "ignore")
        except Exception as e:  # noqa: BLE001
            print("  !!", e, u, file=sys.stderr)
            time.sleep(15)
    return ""


def search(delay):
    out = os.path.join(CACHE, "oga_search.json")
    res = json.load(open(out)) if os.path.exists(out) else {}
    done = set(res.get("_done", []))
    for kind, tid, terms in (("sfx", 13, SFX), ("music", 12, MUSIC)):
        for t in terms:
            key = f"{kind}:{t}"
            if key in done:
                continue
            for page in range(3):
                u = ("https://opengameart.org/art-search-advanced?keys=" + urllib.parse.quote(t) +
                     f"&field_art_type_tid%5B%5D={tid}&field_art_licenses_tid%5B%5D=4"
                     "&sort_by=count&sort_order=DESC" + (f"&page={page}" if page else ""))
                s = get(u)
                time.sleep(delay)
                items = re.findall(r'art-preview-title"><a href="/content/([a-z0-9\-]+)">(.*?)</a>', s)
                for slug, title in items:
                    d = res.setdefault(slug, {"title": html.unescape(title), "kind": kind, "terms": []})
                    if t not in d["terms"]:
                        d["terms"].append(t)
                m = re.search(r"Displaying \d+ - (\d+) of (\d+)", s)
                print(f"{key} p{page}: {len(items)} ({m.group(0) if m else 'none'})",
                      file=sys.stderr, flush=True)
                if not m or int(m.group(1)) >= int(m.group(2)):
                    break
            done.add(key)
            res["_done"] = sorted(done)
            json.dump(res, open(out, "w"), indent=1)
    print("slugs:", len(res) - 1, file=sys.stderr)


def parse(slug, s):
    txt = html.unescape(re.sub(r"\s+", " ", re.sub(r"<[^>]+>", " ", s))).replace("\xa0", " ")
    lic = re.search(r"License\(s\):\s*(.{0,120}?)\s*(?:Collections|Favorites|Tags|Copyright/Attribution|File\(s\))", txt)
    note = re.search(r"Copyright/Attribution Notice:\s*(.{0,300}?)\s*(?:File\(s\)|Collections|License)", txt)
    files = []
    for f in re.findall(r'href="(https://opengameart\.org/sites/default/files/[^"]+)"', s):
        f = html.unescape(f)
        name = urllib.parse.unquote(f.split("/")[-1])
        if name.lower().endswith(AUDIO_EXT) and "audio_preview" not in f and f not in [x["url"] for x in files]:
            files.append({"url": f, "name": name})
    sizes = re.findall(r'<span class="file">(.*?)</span>\s*</div>', s, re.S)
    title = re.search(r"<title>(.*?)</title>", s)
    au = re.search(r'field-name-author-submitter.*?<a href="/users/([^"]+)">([^<]+)</a>', s, re.S)
    date = re.search(r'field-name-post-date.*?<div class="field-item even">([^<]+)</div>', s, re.S)
    body = re.search(r'field-name-body.*?<div class="field-item even"[^>]*>(.*?)</div>', s, re.S)
    return {
        "title": html.unescape(title.group(1)).replace(" | OpenGameArt.org", "").strip() if title else slug,
        "page": "https://opengameart.org/content/" + slug,
        "author_slug": au.group(1) if au else "?",
        "author": html.unescape(au.group(2)).strip() if au else "?",
        "date": date.group(1).strip() if date else "?",
        "licence": lic.group(1).strip() if lic else "?",
        "attribution_notice": note.group(1).strip() if note else "",
        "files": files,
        "file_sizes": [re.sub(r"\s+", " ", re.sub(r"<[^>]+>", " ", html.unescape(x))).strip() for x in sizes],
        "body": re.sub(r"\s+", " ", re.sub(r"<[^>]+>", " ", html.unescape(body.group(1))))[:700] if body else "",
    }


def detail(delay):
    want = [l.strip() for l in open(os.path.join(CACHE, "oga_wanted.txt")) if l.strip()
            and not l.startswith("#")]
    pages = os.path.join(CACHE, "oga_pages")
    os.makedirs(pages, exist_ok=True)
    out = os.path.join(CACHE, "oga_detail.json")
    res = json.load(open(out)) if os.path.exists(out) else {}
    for slug in want:
        p = os.path.join(pages, slug + ".html")
        if not os.path.exists(p):
            s = get("https://opengameart.org/content/" + slug)
            time.sleep(delay)
            if not s:
                continue
            open(p, "w").write(s)
        s = open(p).read()
        res[slug] = parse(slug, s)
        r = res[slug]
        print(f"{slug[:48]:50s} {len(r['files']):2d} files  {r['licence'][:24]:26s} {r['author'][:20]}",
              file=sys.stderr, flush=True)
        json.dump(res, open(out, "w"), indent=1)


if __name__ == "__main__":
    os.makedirs(CACHE, exist_ok=True)
    mode = sys.argv[1]
    d = float(sys.argv[2]) if len(sys.argv) > 2 else 8.0
    search(d) if mode == "search" else detail(d)
