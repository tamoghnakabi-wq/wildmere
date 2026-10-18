"""Music (imported by recipes.py): calm CC0 tracks from OpenGameArt composers,
level-matched and re-encoded as stereo Ogg Vorbis q6 (WAV would be ~30 MB a track).
Played as whole tracks (loop false); the game decides when to start one."""
from recipes import T, RECIPES

MUSIC = {"hp": 20.0, "lufs": -21.0, "declick": False, "fade_in_ms": 30.0, "fade_out_ms": 2500.0,
         "codec_margin_db": 1.0}


def M(sid, f):
    return {"kind": "music", "takes": [T(sid, f)], "p": dict(MUSIC), "volume": 0.5, "loop": False, "fmt": "ogg"}


RECIPES["music/meadow_thoughts"] = M("oga_m_meadow", "Meadow Thoughts.ogg")          # solo harp
RECIPES["music/bluebonnet"] = M("oga_m_bluebonnet", "bluebonnet_in_b_major_2.ogg")   # gentle piano
RECIPES["music/another_august"] = M("oga_m_another_august", "013_Another_August*")   # serene ambient
RECIPES["music/forget_me_not"] = M("oga_m_forget_me_not", "forget_me_not_in_f_major_0.ogg")
