"""Thunder one-shots (imported by recipes.py). Mono, long natural tails.

Near = close strikes with a crack and a long roll; far = distant rumbles with the
rain hiss filtered away so they sit under any rain bed.
"""
from recipes import T, P, RECIPES

THUNDER = {"hp": 25, "trim_db": -45, "lead_db": -30, "fade_in_ms": 25, "fade_out_ms": 1800,
           "limit_db": 4.0, "declick": True}
EZ = "pd_ezwa_storm"

RECIPES["thunder/near"] = {
    "takes": [T("oga_rain_thunder", "rain-thunder.ogg", t0=20.3, t1=36.0),
              T("oga_rd_100v2", "sfx100v2_thunder_01.ogg"),
              T(EZ, "Thunder_and_rain_on_a_v.ogg", t0=2.6, t1=19.0),
              T("pd_caesar_thunder", "Rain_and_thunder.ogg", t0=1.4, t1=16.5),
              T(EZ, "Rain_thunder_and_birds.ogg", t0=3.0, t1=17.5)],
    "p": P(THUNDER, lp=9000, max_s=15.0, lufs=-13.0), "volume": 0.9}

RECIPES["thunder/far"] = {
    "takes": [T(EZ, "Rain_thunder_and_other_noises.ogg", t0=6.5, t1=21.0),
              T(EZ, "Rain_thunder_and_other_noises.ogg", t0=64.5, t1=80.0),
              T(EZ, "Rain_thunder_and_other_noises.ogg", t0=141.0, t1=158.0),
              T(EZ, "Rain_and_thunder_*1*.ogg", t0=0.0, t1=15.0),
              T("pd_tonitrus", "Tonitrus.ogg", t0=20.5, t1=32.2)],
    "p": P(THUNDER, lp=1500, max_s=14.0, lufs=-20.0, lead_db=-36, fade_in_ms=300), "volume": 0.75}
