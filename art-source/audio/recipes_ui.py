"""UI sounds (imported by recipes.py): short, soft, mono. Kenney's "UI Audio" set is
recorded mechanical clicks and switches; open/close use real leather/book foley so
menus feel like a satchel and a journal rather than a computer."""
from recipes import T, P, RECIPES, KR, KF, KU

UI = {"hp": 120, "trim_db": -45, "lead_db": -30, "fade_in_ms": 0.5, "fade_out_ms": 25,
      "lufs": -24.0, "limit_db": 6.0}

RECIPES["ui/click"] = {
    "takes": [T(KU, "click1.ogg"), T(KU, "click3.ogg"), T(KF, "select_001.ogg"), T(KF, "select_002.ogg"),
              T(KU, "mouseclick1.ogg")],
    "p": P(UI, max_s=0.12, limit_db=9.0), "volume": 0.35}
RECIPES["ui/hover"] = {
    "takes": [T(KU, "rollover2.ogg"), T(KU, "rollover5.ogg")],
    "p": P(UI, max_s=0.12, lufs=-28.0), "volume": 0.25}
RECIPES["ui/open"] = {
    "takes": [T(KR, "handleSmallLeather.ogg"), T(KR, "bookOpen.ogg")],
    "p": P(UI, max_s=0.40, fade_out_ms=60), "volume": 0.40}
RECIPES["ui/close"] = {
    "takes": [T(KR, "bookClose.ogg"), T(KR, "handleSmallLeather2.ogg")],
    "p": P(UI, max_s=0.40, fade_out_ms=60), "volume": 0.40}
RECIPES["ui/error"] = {
    "takes": [T(KF, "error_008.ogg"), T(KF, "error_004.ogg")],
    "p": P(UI, max_s=0.30), "volume": 0.35}
RECIPES["ui/confirm"] = {
    "takes": [T(KF, "confirmation_001.ogg"), T(KF, "confirmation_003.ogg")],
    "p": P(UI, max_s=0.45, fade_out_ms=80), "volume": 0.35}
