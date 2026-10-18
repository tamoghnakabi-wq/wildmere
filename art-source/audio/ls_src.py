#!/usr/bin/env python3
"""List the audio files inside downloaded sources (research helper).

    python3 art-source/audio/ls_src.py <source id> [...]
"""
import os, sys

HERE = os.path.dirname(os.path.abspath(__file__))
EXT = (".wav", ".ogg", ".flac", ".mp3", ".aif", ".aiff", ".m4a", ".opus")
for sid in sys.argv[1:]:
    names = []
    for dp, _, fns in os.walk(os.path.join(HERE, "downloads", sid)):
        names += [f for f in fns if f.lower().endswith(EXT)]
    print(f"== {sid} ({len(names)}): " + "  ".join(sorted(names)))
