"""Ambience beds (imported by recipes.py): stereo, seamless loops, Ogg Vorbis q6.

Each bed is a sum of layers; every layer is turned into a seamless loop of the bed's
exact length (equal-power crossfade at its own seam, or a tiled shorter loop), so
the sum is seamless too. Times in `search` are where in the source to look for the
steadiest seam.
"""
from recipes import T, RECIPES


def L(take, **o):
    d = {"take": take}
    d.update(o)
    return d


def BED(layers, loop_s=50.0, lufs=-22.0, volume=0.6, **p):
    pp = {"loop_s": loop_s, "xfade_s": 3.0, "lufs": lufs, "hp": 30.0, "codec_margin_db": 1.0}
    pp.update(p)
    return {"kind": "bed", "beds": [layers], "p": pp, "volume": volume, "loop": True, "fmt": "ogg"}


PARK = "oga_park"

# birds in a copse of trees (Adelaide park), traffic rumble filtered out
RECIPES["amb/forest_day"] = BED([
    L(T(PARK, "park_ambience_birds.wav", t0=130, t1=215), hp=180, eq=[(3000, 1.5, 0.8)]),
    # leaves: the airy part of the field-wind recording, very low
    L(T(PARK, "park_ambience_wind.wav", t0=190, t1=250), hp=900, lp=9000, gain=-16, role="leaf air"),
], loop_s=55, lufs=-24, volume=0.65)

# open grass: gentle wind with daytime insects
RECIPES["amb/plains_day"] = BED([
    L(T(PARK, "park_ambience_wind.wav", t0=40, t1=105), hp=260, lp=10000, gain=0, role="wind in grass"),
    L(T("oga_syncopika_cicada", "*cicadasounds2*", t0=6), hp=1200, gain=-9, role="insects"),
    L(T(PARK, "park_ambience_birds.wav", t0=300, t1=400), hp=1500, lp=6000, gain=-14, role="distant birds"),
], loop_s=56, lufs=-25, volume=0.6)

# crickets (pseudo-stereo from a mono backyard take) + a distant nightjar
RECIPES["amb/night"] = BED([
    L(T("oga_syncopika_crickets", "cricket*"), hp=1500, role="crickets"),
    L(T("oga_crickets_loop", "crickets_1.mp3"), hp=1800, gain=-8, role="crickets, wide"),
    L(T("cm_nightjar", "Caprimulgus_europaeus.ogg"), hp=400, lp=6000, gain=-16, role="distant nightjar"),
], loop_s=35.5, lufs=-28, volume=0.6)

# deep, dark and calm: low wind through big trees, far birds, an occasional creak
RECIPES["amb/dark_forest"] = BED([
    L(T(PARK, "park_ambience_wind.wav", t0=150, t1=250), hp=60, lp=700, gain=0, role="low wind"),
    L(T(PARK, "park_ambience_birds.wav", t0=20, t1=120), hp=600, lp=2500, gain=-12, role="far birds"),
    L(T("oga_tree_creak", "tree_creak.flac", t0=0.0, t1=3.0), insert=[14.0, 41.0], lp=4000, gain=-12,
      role="tree creak"),
], loop_s=58, lufs=-28, volume=0.6)

# dry, hissing wind over open sand
RECIPES["amb/desert_wind"] = BED([
    L(T(PARK, "park_ambience_wind.wav", t0=0, t1=100), hp=300, lp=9000, gain=0, role="wind"),
    L(T("cm_howling_wind", "Howling_wind.ogg", t0=60), hp=900, lp=5000, gain=-10, role="whistle"),
], loop_s=52, lufs=-24, volume=0.6)

# cold howl (wind howling around a building) with open-air hiss
RECIPES["amb/snow_wind"] = BED([
    L(T("cm_howling_wind", "Howling_wind.ogg"), hp=120, lp=7500, role="howl"),
    L(T(PARK, "park_ambience_wind.wav", t0=190, t1=256), hp=400, lp=10000, gain=-10, role="air"),
], loop_s=58, lufs=-23, volume=0.6)

# drips in a stone basement, sparse heavier drops, low room tone
RECIPES["amb/cave"] = BED([
    L(T("oga_drip_loop", "atmosbasement*"), hp=150, role="drips"),
    L(T("oga_water_drops", "water drops.mp3", t0=60, t1=150), hp=300, lp=8000, gain=-14, role="far drops"),
    L(T("oga_underwater", "deep_rumble.ogg"), hp=30, lp=180, gain=-14, role="room tone"),
], loop_s=40, lufs=-30, volume=0.65)

# muffled ocean (recorded on a windy day, low-passed by the recordist) + bubbles
RECIPES["amb/underwater"] = BED([
    L(T("oga_underwater", "underwater_or_space_engine*"), hp=25, lp=900, role="muffled sea"),
    L(T("oga_bubbles", "bubbles-loop1-amp.wav"), lp=2500, gain=-14, role="bubbles"),
    L(T("oga_rd_water", "loop_bubbles_02.ogg"), lp=2000, gain=-16, role="bubbles"),
], loop_s=48, lufs=-24, volume=0.6)

# a small river falling over a low step, birds and frogs around it
RECIPES["amb/river"] = BED([
    L(T(PARK, "park_ambience_river.wav", t0=240, t1=330), hp=120),
], loop_s=55, lufs=-23, volume=0.65)

# gentle waves lapping on the Vistula shore
RECIPES["amb/ocean_shore"] = BED([
    L(T("oga_vistula", "VistulaShort*", t0=75, t1=135), hp=60),
], loop_s=52, lufs=-23, volume=0.65)

# light rain in deep woods (two shotgun mics), the thunder-free stretch
RECIPES["amb/rain_light"] = BED([
    L(T("cm_light_rain", "Light_Rain*", t0=29.5, t1=68.5), hp=120, role="light rain"),
], loop_s=34, lufs=-25, volume=0.6, xfade_s=2.5)

# heavy rain: dense rain + a closer downpour layer + gutter splashes
RECIPES["amb/rain_heavy"] = BED([
    L(T("oga_rain_kresiek2", "amb_rain2.flac", t0=40, t1=110), hp=80, role="rain"),
    L(T("oga_rain_ylmir", "2.ogg"), hp=150, gain=-4, role="downpour"),
    L(T("oga_rain_gutter", "rain-gutter-loop*"), hp=120, gain=-10, role="gutter"),
], loop_s=54, lufs=-20, volume=0.6)

# strong gusty wind with a whistle on top
RECIPES["amb/wind_strong"] = BED([
    L(T(PARK, "park_ambience_wind.wav", t0=100, t1=170), hp=160, lp=11000, role="gusts"),
    L(T("cm_howling_wind", "Howling_wind.ogg", t0=20), hp=200, lp=7000, gain=-6, role="howl"),
], loop_s=55, lufs=-20, volume=0.6)
