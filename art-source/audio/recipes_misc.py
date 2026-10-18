"""Item, player, water, torch and wildlife sounds (imported by recipes.py)."""
from recipes import T, P, RECIPES, KI, KR, KF

ONE = {"hp": 60, "trim_db": -45, "lead_db": -28, "fade_in_ms": 2, "fade_out_ms": 80,
       "lufs": -18.0, "limit_db": 6.0}
WS = "oga_workshop"
RQ = "oga_qubodup_rustle"

# ------------------------------------------------------------------ items
RECIPES["item/pickup"] = {      # small pops: mouth/liquid plops, a pluck, a gem tick
    "takes": [T("oga_rd_100", "plop_01.ogg"), T("oga_rd_100", "plop_02.ogg"),
              T(KF, "pluck_001.ogg"), T("oga_rd_rpg", "item_gem_01.ogg")],
    "p": P(ONE, hp=150, max_s=0.30, fade_out_ms=60, lufs=-21.0), "volume": 0.45}
RECIPES["item/craft"] = {       # three quick hammer taps on wood (garage recording)
    "takes": [T(WS, "workshop - wood hammering.wav", t0=0.15, t1=1.48),
              T(WS, "workshop - wood hammering.wav", t0=3.98, t1=5.30),
              T(WS, "workshop - wood hammering.wav", t0=6.09, t1=7.40)],
    "p": P(ONE, max_s=1.4, fade_out_ms=120, lufs=-19.0), "volume": 0.55}
RECIPES["item/tool_break"] = {  # the handle snaps, metal head clinks and drops
    "takes": [[T("oga_nu_wood", "crack05.mp3.flac", ev=0), T(KI, "impactMetal_light_002.ogg", at=0.03, g=-3, role="head"),
               T(KI, "impactMetal_light_004.ogg", at=0.32, g=-8, role="drop")],
              [T("oga_rd_woodmetal", "wood_cracking_02.ogg"), T(KI, "impactMetal_light_001.ogg", at=0.02, g=-4, role="head"),
               T(KI, "impactMetal_light_000.ogg", at=0.28, g=-9, role="drop")]],
    "p": P(ONE, max_s=0.9, fade_out_ms=150, lufs=-17.0), "volume": 0.6}

# ------------------------------------------------------------------ player
EP = "oga_emopreben_player"
RECIPES["player/hurt"] = {      # one voice (EmoPreben) throughout
    "takes": [T(EP, "*Hurt1.wav"), T(EP, "*Hurt2.wav"), T(EP, "*Hurt3.wav"), T(EP, "*Hurt4.wav"),
              T("oga_emopreben_pain", "*Huuurh.ogg"), T("oga_emopreben_pain", "*Arrggh .ogg")],
    "p": P(ONE, hp=90, max_s=0.9, fade_out_ms=120, lufs=-18.0), "volume": 0.7}
RECIPES["player/jump"] = {      # clothing/body movement, no voice
    "takes": [T(KR, f"cloth{i}.ogg") for i in (1, 2, 3, 4)] + [T("oga_haeldb_steps", "step_cloth2.ogg", g=6)],
    "p": P(ONE, hp=80, max_s=0.45, lufs=-24.0), "volume": 0.4}
RECIPES["player/land_hard"] = {
    "takes": [[T("oga_jump_landing", "Jump 1.wav"), T(KR, "cloth3.ogg", g=-4, role="cloth")],
              T("oga_jumpland", "jumpland.wav"),
              [T("oga_wet_towel", "wet_towel_on_floor-05.flac", g=-3), T(KI, "impactSoft_heavy_003.ogg", role="body")]],
    "p": P(ONE, hp=45, max_s=0.6, lufs=-17.0), "volume": 0.65}

# ------------------------------------------------------------------ keys AudioManager also asks for
# step/glass: hard shoe on a slab with a faint glassy tick
RECIPES["step/glass"] = {
    "takes": [[T("oga_haeldb_steps", f"step_metal{n}.ogg", g=4), T(KI, f"impactGlass_light_00{k}.ogg", g=-14, role="tick")]
              for n, k in ((" (2)", 0), (" (3)", 2), (" (4)", 3), ("", 4))],
    "p": P(ONE, hp=90, max_s=0.35, fade_out_ms=70, lufs=-18.0), "volume": 0.30}
# SoundMat.Water: placing/scooping water and the (rare) dig/break of a water block
RECIPES["place/water"] = {
    "takes": [T("oga_rd_water", "splash_02.ogg"), T("oga_rd_water", "splash_08.ogg")],
    "p": P(ONE, hp=90, max_s=0.48, fade_out_ms=150, lufs=-19.0), "volume": 0.55}
RECIPES["dig/water"] = {
    "takes": [T("oga_rd_water", "splash_10.ogg"), T("oga_rd_water", "splash_09.ogg")],
    "p": P(ONE, hp=90, max_s=0.35, fade_out_ms=80, lufs=-20.0), "volume": 0.5}
RECIPES["break/water"] = {
    "takes": [T("oga_rd_water", "splash_05.ogg"), T("oga_rd_water", "splash_11.ogg")],
    "p": P(ONE, hp=80, max_s=0.9, fade_out_ms=200, lufs=-17.0), "volume": 0.6}

# ------------------------------------------------------------------ water
RECIPES["water/splash_in"] = {
    "takes": [T("oga_rd_water", "splash_01.ogg"), T("oga_rd_water", "splash_07.ogg"),
              T("oga_peludo", "splash1_0.wav"), T("oga_ezwa_splash", "water_splash-01.flac")],
    "p": P(ONE, hp=70, max_s=1.6, fade_out_ms=300, lufs=-16.0), "volume": 0.7}
RECIPES["water/splash_out"] = {  # climbing out: a slosh and the water running off
    "takes": [T("oga_peludo", "splash2_0.wav"), T("oga_rd_water", "splash_13.ogg"),
              T("oga_ezwa_splash", "water_splash-04.flac")],
    "p": P(ONE, hp=90, lp=9000, max_s=1.5, fade_out_ms=300, lufs=-19.0), "volume": 0.6}

# ------------------------------------------------------------------ torch
RECIPES["torch/place"] = {      # wooden stick knocked in, then the flame catching
    "takes": [[T(KI, "impactWood_light_002.ogg"), T("oga_flare", "ignition.flac", at=0.06, g=-6, role="ignite")],
              [T("oga_rd_rpg", "item_wood_02.ogg"), T("oga_catching_fire", "flame_0.ogg", at=0.05, g=-4, role="ignite")]],
    "p": P(ONE, max_s=1.2, fade_out_ms=250, lufs=-20.0), "volume": 0.5}
RECIPES["torch/crackle"] = {    # positional loop: mono
    "kind": "bed", "loop": True, "fmt": "wav", "volume": 0.45,
    "beds": [[{"take": T("oga_fireplace", "fire.wav"), "hp": 120, "role": "fire"},
              {"take": T("oga_fire_crackling", "fire-1.wav"), "hp": 400, "gain": -6, "role": "crackles"}]],
    "p": {"loop_s": 24.0, "xfade_s": 2.0, "lufs": -24.0, "hp": 80.0, "mono": True}}

# ------------------------------------------------------------------ wildlife
RECIPES["bird/chirp"] = {       # songbird phrases (isaiah658's cleaned field recording)
    "takes": [T("oga_isaiah_birds", "birds-isaiah*", ev=i, evp={"over_floor_db": 14, "min_gap_ms": 150,
                                                                  "max_len_ms": 2500})
              for i in (2, 4, 7, 10, 14, 17)],
    "p": P(ONE, hp=1200, max_s=2.5, fade_out_ms=200, lufs=-20.0, trim_db=-50, lead_db=-20), "volume": 0.5}
RECIPES["deer/call"] = {        # an elk (wapiti) bellow -- the only public-domain deer call found
    "takes": [T("pd_elk", "Elkbellow.ogg", t0=0.0, t1=4.6)],
    "p": P(ONE, hp=150, max_s=4.6, fade_out_ms=400, lufs=-16.0), "volume": 0.7}
RECIPES["rabbit/hop"] = {       # soft thump on turf with a tick of grass
    "takes": [[T(KI, "footstep_carpet_000.ogg"), T(RQ, "rustle16.flac", t1=0.12, g=-14, role="grass")],
              [T(KI, "footstep_carpet_001.ogg"), T(RQ, "rustle17.flac", t1=0.12, g=-14, role="grass")],
              [T("oga_haeldb_steps", "step_cloth1.ogg", g=8), T(RQ, "rustle19.flac", t1=0.10, g=-16, role="grass")],
              [T(KI, "impactSoft_medium_000.ogg", rate=1.3, g=-4), T(RQ, "rustle15.flac", t1=0.10, g=-16, role="grass")]],
    "p": P(ONE, hp=90, max_s=0.30, fade_out_ms=60, lufs=-26.0), "volume": 0.45}
