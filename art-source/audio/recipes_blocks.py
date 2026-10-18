"""dig / break / place recipes (imported by recipes.py).

dig   = the short hit repeated while mining (<= 0.32 s, bright attack)
break = the block giving way: louder, longer, with debris
place = setting a block down: a darker, tighter, weightier thunk
"""
from recipes import T, P, KI, DIG, BREAK, PLACE, RECIPES

NU, NW, RDB, RDW, SV = "oga_nu_crunch", "oga_nu_wood", "oga_rd_breaking", "oga_rd_woodmetal", "oga_spring_various"

# ---- stone
RECIPES["dig/stone"] = {
    "takes": [T(KI, f"impactMining_00{i}.ogg") for i in (0, 1, 2, 4)] +
             [T(SV, "tap_stone.wav"), T(SV, "small_rock_impact.wav")],
    "p": P(DIG, lp=9000), "volume": 0.55}
RECIPES["break/stone"] = {
    "takes": [[T(KI, "impactMining_003.ogg"), T(RDB, "bfh1_rock_falling_01.ogg", at=0.03, g=-6, role="debris")],
              T(RDB, "bfh1_rock_breaking_01.ogg"), T(RDB, "bfh1_rock_breaking_02.ogg"),
              T(SV, "break_stone.wav", t1=1.2),
              [T("oga_rock_break", "rock_break_0.ogg"),
               T(RDB, "bfh1_rock_falling_03.ogg", at=0.05, g=-8, role="debris")]],
    "p": P(BREAK, max_s=0.9), "volume": 0.70}
RECIPES["place/stone"] = {
    "takes": [[T(RDB, "bfh1_rock_hit_01.ogg", g=-3), T(KI, "impactPlate_heavy_000.ogg", g=-4, role="weight")],
              [T("oga_rd_100v2", "sfx100v2_stones_01.ogg", t1=0.25, g=-6),
               T(KI, "impactPlate_heavy_002.ogg", g=-3, role="weight")],
              [T("oga_rd_100v2", "sfx100v2_stones_03.ogg", t1=0.25, g=-6),
               T(KI, "impactPlate_heavy_004.ogg", g=-3, role="weight")],
              T("oga_boulder", "move_boulder_0.ogg", t1=0.35), T("oga_qubodup_impact", "qubodupImpactStone.flac")],
    "p": P(PLACE, lp=4500), "volume": 0.55}

# ---- dirt
RECIPES["dig/dirt"] = {
    "takes": [T("oga_shovel", "shovel_0.ogg", ev=0), T("oga_rd_mud", "mud_03.ogg"), T("oga_rd_mud", "mud_19.ogg"),
              T(KI, "impactSoft_medium_000.ogg"), T(KI, "impactSoft_medium_002.ogg"),
              T(NU, "impactcrunch04.mp3.flac")],
    "p": P(DIG, lp=6000, eq=[(250, 2.0, 1.0)]), "volume": 0.55}
RECIPES["break/dirt"] = {
    "takes": [[T(KI, "impactSoft_heavy_000.ogg"), T("oga_cave_in", "cave_in*", ev=1, g=-4, role="crumble")],
              [T(KI, "impactSoft_heavy_003.ogg"), T(NU, "impactcrunch05.mp3.flac", g=-3, role="crumble")],
              [T(KI, "impactSoft_heavy_001.ogg"), T("oga_shovel", "shovel_0.ogg", ev=1, g=-2, role="crumble")],
              T(SV, "crush.wav")],
    "p": P(BREAK, lp=7000, max_s=0.8), "volume": 0.68}
RECIPES["place/dirt"] = {
    "takes": [T("oga_rd_mud", "mud_18.ogg"), T("oga_rd_mud", "mud_11.ogg", rate=0.9),
              T("oga_wet_towel", "wet_towel_on_floor-03.flac", rate=0.85),
              T("oga_wet_towel", "wet_towel_on_floor-07.flac", rate=0.85)],
    "p": P(PLACE, lp=2500, hp=50, lufs=-21.0, limit_db=9.0), "volume": 0.62}

# ---- sand (Peludo's beach steps 0 and 5 are not used by step/sand)
RECIPES["dig/sand"] = {
    "takes": [T("oga_peludo", "sand_footsteps*", ev=0), T("oga_peludo", "sand_footsteps*", ev=5),
              T("oga_cave_in", "cave_in*", ev=2), T("oga_rd_mud", "mud_07.ogg"),
              T("oga_rd_mud", "mud_21.ogg")],
    "p": P(DIG, hp=120, eq=[(5000, -2.0, 0.7)]), "volume": 0.50}
RECIPES["break/sand"] = {
    "takes": [T("oga_cave_in", "cave_in*", ev=0), T("oga_cave_in", "cave_in*", ev=1, rate=1.12),
              [T(KI, "impactSoft_medium_000.ogg", g=-6, role="body"),
               T("oga_peludo", "sand_footsteps*", ev=0, role="spray")]],
    "p": P(BREAK, hp=90, max_s=0.85), "volume": 0.62}
RECIPES["place/sand"] = {
    "takes": [[T(KI, "impactSoft_medium_000.ogg", rate=0.95), T("oga_rd_mud", "mud_22.ogg", g=-8, role="grain")],
              [T(KI, "impactSoft_heavy_003.ogg", rate=1.1), T("oga_rd_mud", "mud_23.ogg", g=-8, role="grain")],
              [T(KI, "impactSoft_heavy_000.ogg", rate=1.15), T("oga_rd_mud", "mud_16.ogg", g=-10, role="grain")]],
    "p": P(PLACE, lp=5000), "volume": 0.52}

# ---- gravel
RECIPES["dig/gravel"] = {
    "takes": [T("oga_rd_100v2", "sfx100v2_stones_02.ogg", t1=0.3), T(NU, "impactcrunch01.mp3.flac"),
              T(NU, "impactcrunch03.mp3.flac"), T("oga_corsica_snow", "*snow_covered_gravel_05.flac"),
              T("oga_corsica_snow", "*snow_covered_gravel_07.flac"),
              T("oga_corsica_snow", "*snow_covered_gravel_09.flac")],
    "p": P(DIG), "volume": 0.52}
RECIPES["break/gravel"] = {
    "takes": [T(RDB, f"bfh1_rock_falling_0{i}.ogg") for i in (2, 4, 5, 7)] +
             [[T(NU, "impactcrunch02.mp3.flac"), T(RDB, "bfh1_rock_falling_08.ogg", at=0.04, g=-6, role="debris")]],
    "p": P(BREAK, max_s=0.8), "volume": 0.66}
RECIPES["place/gravel"] = {
    "takes": [T("oga_corsica_snow", f"*snow_covered_gravel_{n}.flac", rate=0.9) for n in ("13", "14", "16", "18")],
    "p": P(PLACE, lp=5000), "volume": 0.52}

# ---- wood (impactwood16-21 duplicate 01-06 in this pack; only distinct files are used)
RECIPES["dig/wood"] = {
    "takes": [T(NW, f"impactwood{n}.mp3.flac", ev=0) for n in ("02", "04", "07", "10", "14", "24")],
    "p": P(DIG), "volume": 0.55}
RECIPES["break/wood"] = {
    "takes": [T(NW, "crack02.mp3.flac", ev=0), T(NW, "crack04.mp3.flac", ev=0), T(NW, "crack09.mp3.flac", ev=0),
              T(RDW, "wood_breaking_01.ogg"), T(RDB, "bfh1_wood_breaking_03.ogg")],
    "p": P(BREAK, max_s=0.9), "volume": 0.68}
RECIPES["place/wood"] = {
    "takes": [T("oga_ogrebane_wm2", f"wood{n}.aif") for n in (2, 8, 12, 16)] +
             [T(RDW, "wood_hammer_01.ogg"), T(RDW, "wood_close_02.ogg")],
    "p": P(PLACE, lp=6000), "volume": 0.55}

# ---- leaves (rustles 09/10/12 belong to step/leaves)
RECIPES["dig/leaves"] = {
    "takes": [T("oga_qubodup_rustle", f"rustle{n}.flac", t1=0.30) for n in ("01", "02", "05", "11", "17")],
    "p": P(DIG, lp=10000, fade_out_ms=90, max_s=0.30), "volume": 0.45}
RECIPES["break/leaves"] = {
    "takes": [T("oga_qubodup_rustle", f"rustle{n}.flac", t1=0.75) for n in ("03", "07", "08", "13", "14")],
    "p": P(BREAK, lp=10000, max_s=0.75, fade_out_ms=250, lufs=-16.0), "volume": 0.55}
RECIPES["place/leaves"] = {
    "takes": [[T("oga_qubodup_rustle", f"rustle{n}.flac", t1=0.25, g=-2),
               T(KI, f"impactSoft_{k}.ogg", g=-9, role="body")]
              for n, k in (("15", "medium_000"), ("16", "medium_002"), ("18", "heavy_000"), ("19", "heavy_003"))],
    "p": P(PLACE, lp=8000, fade_out_ms=90), "volume": 0.45}

# ---- glass
RECIPES["dig/glass"] = {
    "takes": [T(KI, f"impactGlass_light_00{i}.ogg") for i in range(5)] + [T(RDB, "bfh1_glass_hit_01.ogg")],
    "p": P(DIG, hp=150), "volume": 0.50}
RECIPES["break/glass"] = {
    "takes": [T("oga_glass_break", "glass_breaking.wav"), T(RDB, "bfh1_glass_breaking_01.ogg"),
              T(RDB, "bfh1_glass_breaking_03.ogg"), T(RDB, "bfh1_glass_breaking_06.ogg"),
              T("oga_ice_shatter", "LedasLuzta33.ogg"), T(KI, "impactGlass_heavy_001.ogg")],
    "p": P(BREAK, hp=120, max_s=1.1, fade_out_ms=250), "volume": 0.66}
RECIPES["place/glass"] = {
    "takes": [T(KI, f"impactGlass_medium_00{i}.ogg") for i in (0, 1, 2, 3)],
    "p": P(PLACE, hp=120, max_s=0.35), "volume": 0.50}

# ---- snow (wet steps; step/snow uses the dry ones)
RECIPES["dig/snow"] = {
    "takes": [T("oga_qubodup_wetsnow", f"snow_step_wet-0{i}.flac", t1=0.30) for i in (1, 3, 4, 6, 8)],
    "p": P(DIG, lp=9000), "volume": 0.50}
RECIPES["break/snow"] = {
    "takes": [T("oga_ignasd_snow", "SnowWalk.ogg"), T("oga_ignasd_snow", "SnowWalk2.ogg"),
              T("oga_corsica_snow", "*gravel_and_ice_04.flac"), T("oga_corsica_snow", "*gravel_and_ice_17.flac"),
              T("oga_qubodup_wetsnow", "snow_step_wet-09.flac")],
    "p": P(BREAK, lp=9000, max_s=0.7), "volume": 0.60}
RECIPES["place/snow"] = {
    "takes": [T(KI, f"footstep_snow_00{i}.ogg", rate=0.92) for i in (1, 4)] +
             [T("oga_qubodup_wetsnow", "snow_step_wet-02.flac", rate=0.9),
              T("oga_qubodup_wetsnow", "snow_step_wet-05.flac", rate=0.9)],
    "p": P(PLACE, lp=4500), "volume": 0.50}
