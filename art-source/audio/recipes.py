"""What every shipped sound is cut from. Read by build.py.

Each recipe: {"takes": [take, ...], "p": processing, "volume": 0..1, "loop": bool,
"kind": "oneshot" | "bed", "fmt": "wav" | "ogg"}. A take is a dict made by T(); a
list of takes is layered into one variant (each layer may carry "at" seconds).
Take keys: s = source id (fetch.py), f = file name/glob inside that download,
t0/t1 = seconds, ev = event index from dsp.find_events (evp = its kwargs),
g = gain dB, rate = varispeed ratio, ch = "L"/"R" to pick one channel.
"""


def T(s, f, **k):
    d = {"s": s, "f": f}
    d.update(k)
    return d


KI, KR, KF, KU = "kenney_impact", "kenney_rpg", "kenney_interface", "kenney_ui"

# ---------------------------------------------------------------- processing
STEP = {"hp": 70, "trim_db": -42, "lead_db": -26, "max_s": 0.42, "fade_out_ms": 70, "lufs": -18.0}
DIG = {"hp": 60, "trim_db": -40, "lead_db": -24, "max_s": 0.32, "fade_out_ms": 60, "lufs": -17.0}
BREAK = {"hp": 45, "trim_db": -46, "lead_db": -26, "max_s": 0.95, "fade_out_ms": 160, "lufs": -14.0}
PLACE = {"hp": 60, "trim_db": -40, "lead_db": -24, "max_s": 0.30, "fade_out_ms": 60, "lufs": -18.0}


def P(base, **over):
    d = dict(base)
    d.update(over)
    return d


RECIPES = {}

# ================================================================= footsteps
# Fantozzi's "sand" takes are soft and grassy (his own note); darkened a little and
# joined by the two least-alike Kenney grass takes (the other three are near-copies).
RECIPES["step/grass"] = {
    "takes": [T("oga_fantozzi", "Fantozzi-SandL1.ogg"), T("oga_fantozzi", "Fantozzi-SandL3.ogg"),
              T("oga_fantozzi", "Fantozzi-SandR1.ogg"), T("oga_fantozzi", "Fantozzi-SandR3.ogg"),
              T(KI, "footstep_grass_001.ogg")],
    "p": P(STEP, hp=110, lp=7000, eq=[(300, 2.0, 1.0)], max_s=0.40, limit_db=9.0), "volume": 0.30}

RECIPES["step/dirt"] = {
    "takes": [T("oga_fantozzi", "Fantozzi-SandL2.ogg"), T("oga_fantozzi", "Fantozzi-SandR2.ogg"),
              T("oga_kdd_steps", "mud02.ogg"), T("oga_ignasd_steps", "Ejimas1.ogg"),
              T("oga_rd_mud", "mud_11.ogg"), T("oga_rd_mud", "mud_18.ogg")],
    # darker and thumpier than sand: dirt packs down rather than sprays
    "p": P(STEP, lp=5200, eq=[(220, 3.0, 1.0)], max_s=0.36), "volume": 0.30}

RECIPES["step/stone"] = {
    "takes": [T("oga_fantozzi", f"Fantozzi-Stone{s}{i}.ogg") for s in "LR" for i in (1, 2, 3)],
    "p": P(STEP, max_s=0.34), "volume": 0.30}

RECIPES["step/sand"] = {
    "takes": [T("oga_peludo", "sand_footsteps*", ev=i) for i in (1, 2, 3, 4, 6, 7)],
    "p": P(STEP, max_s=0.40, eq=[(3500, -2.0, 0.8)]), "volume": 0.30}

RECIPES["step/gravel"] = {
    "takes": [T("oga_kdd_steps", "gravel.ogg"),
              T("oga_rd_100v2", "sfx100v2_footstep_01.ogg"), T("oga_rd_100v2", "sfx100v2_footstep_02.ogg"),
              T("oga_corsica_snow", "*snow_covered_gravel_04.flac"),
              T("oga_corsica_snow", "*snow_covered_gravel_10.flac"),
              T("oga_corsica_snow", "*snow_covered_gravel_12.flac")],
    "p": P(STEP, max_s=0.40), "volume": 0.30}

RECIPES["step/wood"] = {
    "takes": [T("oga_rd_100v2", f"sfx100v2_footstep_wood_0{i}.ogg") for i in (1, 2, 4)] +
             [T(KI, "footstep_wood_000.ogg"), T("oga_kdd_steps", "wood01.ogg")],
    "p": P(STEP, hp=90, max_s=0.32, limit_db=9.0), "volume": 0.30}

RECIPES["step/snow"] = {
    "takes": [T("oga_qubodup_drysnow", f"snow_step_dry-0{i}.flac") for i in (1, 2, 3, 4)] +
             [T(KI, "footstep_snow_000.ogg"), T(KI, "footstep_snow_003.ogg")],
    "p": P(STEP, max_s=0.45, fade_out_ms=90), "volume": 0.30}

# leaf litter: a crisp rustle burst over a soft footfall
RECIPES["step/leaves"] = {
    "takes": [T("oga_kdd_steps", "leaves01.ogg"), T("oga_kdd_steps", "leaves02.ogg"),
              [T("oga_qubodup_rustle", "rustle10.flac", t1=0.40, g=-4),
               T(KI, "footstep_grass_001.ogg", g=-3, role="footfall")],
              [T("oga_qubodup_rustle", "rustle09.flac", t1=0.40, g=-4),
               T(KI, "footstep_grass_003.ogg", g=-3, role="footfall")],
              [T("oga_qubodup_rustle", "rustle12.flac", t1=0.40, g=-2),
               T(KI, "footstep_grass_004.ogg", g=-3, role="footfall")],
              T("oga_haeldb_random", "moving leaves stereo.ogg", t1=0.5)],
    "p": P(STEP, lp=9000, max_s=0.42, fade_out_ms=110), "volume": 0.30}

# wading: wet footfalls plus small hand-made splashes
RECIPES["step/water"] = {
    "takes": [T("oga_rd_100v2", f"sfx100v2_footstep_wet_0{i}.ogg") for i in (1, 2, 3)] +
             [T("oga_rd_water", "splash_06.ogg"), T("oga_rd_water", "splash_14.ogg"),
              T("oga_ezwa_splash", "water_splash-02.flac")],
    "p": P(STEP, max_s=0.50, fade_out_ms=120, lp=11000), "volume": 0.30}


import recipes_blocks  # noqa: E402,F401  (dig / break / place)
import recipes_amb  # noqa: E402,F401  (ambience beds)
import recipes_weather  # noqa: E402,F401  (thunder)
import recipes_ui  # noqa: E402,F401  (UI)
import recipes_misc  # noqa: E402,F401  (items, player, water, torch, wildlife)
