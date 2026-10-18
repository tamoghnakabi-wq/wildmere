#!/usr/bin/env python3
"""Download Wildmere's source recordings and record their provenance.

Every source is CC0 1.0 (public-domain dedication). Each archive or file is saved
into its own directory under art-source/audio/downloads/<source id>/ (gitignored);
archives are extracted into a fresh, empty sibling directory "<name>.d/". Nothing
from a download is ever executed -- files are only decoded by ffmpeg in build.py.

The provenance of every download (page URL, author, licence, file URL, sha256,
licence files found inside archives) is written to art-source/audio/sources.json,
which is committed.

    python3 -I art-source/audio/fetch.py            # fetch everything missing
    python3 -I art-source/audio/fetch.py <id> ...   # fetch only these sources
"""
import hashlib, json, os, subprocess, sys, time, urllib.parse, urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
DL = os.path.join(HERE, "downloads")
UA = {"User-Agent": "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 "
                    "(KHTML, like Gecko) Chrome/124.0 Safari/537.36"}
WM_UA = {"User-Agent": "WildmereAudioFetch/1.0 (game asset provenance; github.com/tamoghnakabi-wq) python-urllib"}
OGA = "https://opengameart.org/sites/default/files/"
CC0 = "CC0 1.0"
PD = "Public domain (PD-self dedication)"

# id, site, title, author, page URL, licence, [file URLs]
SOURCES = [
    # ---------------------------------------------------------------- Kenney (CC0)
    ("kenney_impact", "kenney.nl", "Impact Sounds", "Kenney (kenney.nl)",
     "https://kenney.nl/assets/impact-sounds", CC0,
     ["https://kenney.nl/media/pages/assets/impact-sounds/87b4ddecda-1677589768/kenney_impact-sounds.zip"]),
    ("kenney_rpg", "kenney.nl", "RPG Audio", "Kenney (kenney.nl)",
     "https://kenney.nl/assets/rpg-audio", CC0,
     ["https://kenney.nl/media/pages/assets/rpg-audio/8e99002d76-1677590336/kenney_rpg-audio.zip"]),
    ("kenney_interface", "kenney.nl", "Interface Sounds", "Kenney (kenney.nl)",
     "https://kenney.nl/assets/interface-sounds", CC0,
     ["https://kenney.nl/media/pages/assets/interface-sounds/fa43c1dd4d-1677589452/kenney_interface-sounds.zip"]),
    ("kenney_ui", "kenney.nl", "UI Audio", "Kenney (kenney.nl)",
     "https://kenney.nl/assets/ui-audio", CC0,
     ["https://kenney.nl/media/pages/assets/ui-audio/490d233f68-1677590494/kenney_ui-audio.zip"]),
    # ------------------------------------------------------------ OpenGameArt (CC0)
    ('oga_kdd_steps', "opengameart.org", 'Different steps on wood, stone, leaves, gravel and mud', 'TinyWorlds',
     "https://opengameart.org/content/different-steps-on-wood-stone-leaves-gravel-and-mud", CC0,
     ['https://opengameart.org/sites/default/files/%5Bkdd%5DDifferentSteps_0.zip']),
    ('oga_fantozzi', "opengameart.org", "Fantozzi's Footsteps (Grass/Sand & Stone)", 'qubodup',
     "https://opengameart.org/content/fantozzis-footsteps-grasssand-stone", CC0,
     ['https://opengameart.org/sites/default/files/Fantozzi-footsteps.7z']),
    ('oga_corsica_snow', "opengameart.org", '42 Snow and Gravel Footsteps', 'qubodup',
     "https://opengameart.org/content/42-snow-and-gravel-footsteps", CC0,
     ['https://opengameart.org/sites/default/files/corsica_s-walking_in_snow.7z']),
    ('oga_ignasd_snow', "opengameart.org", 'Walking on snow sound', 'IgnasD',
     "https://opengameart.org/content/walking-on-snow-sound", CC0,
     ['https://opengameart.org/sites/default/files/SnowWalk.zip']),
    ('oga_qubodup_wetsnow', "opengameart.org", '9 wet snow steps', 'qubodup',
     "https://opengameart.org/content/9-wet-snow-steps", CC0,
     ['https://opengameart.org/sites/default/files/qubodup-wet_snow_steps.7z']),
    ('oga_qubodup_drysnow', "opengameart.org", '4 dry snow steps', 'qubodup',
     "https://opengameart.org/content/4-dry-snow-steps", CC0,
     ['https://opengameart.org/sites/default/files/qubodup-snow_steps_dry_0.7z']),
    ('oga_mikeask_wood', "opengameart.org", 'Steps in wood floor', 'mikeask',
     "https://opengameart.org/content/steps-in-wood-floor", CC0,
     ['https://opengameart.org/sites/default/files/steps%20in%20wood%20floor.wav']),
    ('oga_peludo', "opengameart.org", 'Water Splash and sand footsteps', 'Peludo',
     "https://opengameart.org/content/water-splash-and-sand-footsteps", CC0,
     ['https://opengameart.org/sites/default/files/splash1_0.wav', 'https://opengameart.org/sites/default/files/splash2_0.wav', 'https://opengameart.org/sites/default/files/sand_footsteps_0.mp3']),
    ('oga_haeldb_steps', "opengameart.org", 'Footsteps Leather, Cloth, Armor', 'HaelDB',
     "https://opengameart.org/content/footsteps-leather-cloth-armor", CC0,
     ['https://opengameart.org/sites/default/files/footsteps.zip']),  # page offers: OGA-BY 3.0 CC0; CC0 chosen
    ('oga_qubodup_rustle', "opengameart.org", '20 Rustles of dry leaves', 'qubodup',
     "https://opengameart.org/content/20-rustles-dry-leaves", CC0,
     ['https://opengameart.org/sites/default/files/qubodup-rustle.7z']),
    ('oga_rd_mud', "opengameart.org", '25 CC0 mud sfx', 'rubberduck',
     "https://opengameart.org/content/25-cc0-mud-sfx", CC0,
     ['https://opengameart.org/sites/default/files/25-CC0-mud-sfx.zip']),
    ('oga_ignasd_steps', "opengameart.org", 'Step sound (walking)', 'IgnasD',
     "https://opengameart.org/content/step-sound-walking", CC0,
     ['https://opengameart.org/sites/default/files/Ejimas1.zip']),
    ('oga_haeldb_random', "opengameart.org", 'Random Sounds Samples', 'HaelDB',
     "https://opengameart.org/content/random-sounds-samples", CC0,
     ['https://opengameart.org/sites/default/files/cricket%20ambienc276.ogg', 'https://opengameart.org/sites/default/files/full%20steps%20stereo.ogg', 'https://opengameart.org/sites/default/files/moving%20leaves%20stereo.ogg']),  # page offers: OGA-BY 3.0 CC0; CC0 chosen
    ('oga_shovel', "opengameart.org", 'Shovel Sound', 'themightyglider',
     "https://opengameart.org/content/shovel-sound", CC0,
     ['https://opengameart.org/sites/default/files/shovel_0.ogg']),
    ('oga_mining', "opengameart.org", 'Mining sample', 'jordan4ibanez',
     "https://opengameart.org/content/mining-sample", CC0,
     ['https://opengameart.org/sites/default/files/mining_1.ogg']),
    ('oga_rock_break', "opengameart.org", 'Breaking Rock', 'themightyglider',
     "https://opengameart.org/content/breaking-rock", CC0,
     ['https://opengameart.org/sites/default/files/rock_break_0.ogg']),
    ('oga_boulder', "opengameart.org", 'Moving Boulder', 'themightyglider',
     "https://opengameart.org/content/moving-boulder", CC0,
     ['https://opengameart.org/sites/default/files/boulder_drop_0.ogg', 'https://opengameart.org/sites/default/files/move_boulder_0.ogg']),
    ('oga_cave_in', "opengameart.org", 'Cave In', 'StarNinjas',
     "https://opengameart.org/content/cave-in", CC0,
     ['https://opengameart.org/sites/default/files/cave_in_-_starninjas_0.ogg']),
    ('oga_stone_moved', "opengameart.org", 'Stone, rock or wood moved sound', 'pauliuw',
     "https://opengameart.org/content/stone-rock-or-wood-moved-sound", CC0,
     ['https://opengameart.org/sites/default/files/stone_rock_or_wood_moved.mp3']),
    ('oga_rd_breaking', "opengameart.org", '75 CC0 breaking / falling / hit sfx', 'rubberduck',
     "https://opengameart.org/content/75-cc0-breaking-falling-hit-sfx", CC0,
     ['https://opengameart.org/sites/default/files/sfx_breaking_and_falling.zip']),
    ('oga_nu_crunch', "opengameart.org", '5 break, crunch impacts', 'qubodup',
     "https://opengameart.org/content/5-break-crunch-impacts", CC0,
     ['https://opengameart.org/sites/default/files/independent_nu_ljudbank-break_crunch_impact.7z']),
    ('oga_nu_wood', "opengameart.org", '35 wooden cracks/hits/destructions', 'qubodup',
     "https://opengameart.org/content/35-wooden-crackshitsdestructions", CC0,
     ['https://opengameart.org/sites/default/files/independent_nu_ljudbank-wood_crack_hit_destruction.7z']),
    ('oga_rd_woodmetal', "opengameart.org", '100 CC0 metal and wood SFX', 'rubberduck',
     "https://opengameart.org/content/100-cc0-metal-and-wood-sfx", CC0,
     ['https://opengameart.org/sites/default/files/100-CC0-wood-metal-SFX.zip']),
    ('oga_rd_100', "opengameart.org", '100 CC0 SFX', 'rubberduck',
     "https://opengameart.org/content/100-cc0-sfx", CC0,
     ['https://opengameart.org/sites/default/files/100-CC0-SFX_0.zip']),
    ('oga_rd_100v2', "opengameart.org", '100 CC0 SFX #2', 'rubberduck',
     "https://opengameart.org/content/100-cc0-sfx-2", CC0,
     ['https://opengameart.org/sites/default/files/sfx_100_v2.zip']),
    ('oga_glass_break', "opengameart.org", 'Glass Break', 'TinyWorlds',
     "https://opengameart.org/content/glass-break", CC0,
     ['https://opengameart.org/sites/default/files/glass_breaking.wav']),
    ('oga_ice_shatter', "opengameart.org", 'Ice breaking/shattering', 'IgnasD',
     "https://opengameart.org/content/ice-breakingshattering", CC0,
     ['https://opengameart.org/sites/default/files/IceShatters_0.zip']),
    ('oga_tree_chop', "opengameart.org", 'tree chop fall thud', 'kheetor',
     "https://opengameart.org/content/tree-chop-fall-thud", CC0,
     ['https://opengameart.org/sites/default/files/chop-tree-fall.ogg']),
    ('oga_workshop', "opengameart.org", '68 Workshop Sounds', 'bart',
     "https://opengameart.org/content/68-workshop-sounds", CC0,
     ['https://opengameart.org/sites/default/files/workshop.7z']),
    ('oga_qubodup_impact', "opengameart.org", 'Impact', 'qubodup',
     "https://opengameart.org/content/impact", CC0,
     ['https://opengameart.org/sites/default/files/qubodupImpact.7z']),
    ('oga_ogrebane_wm2', "opengameart.org", 'Wood and Metal Sound Effects: Volume 2', 'Ogrebane',
     "https://opengameart.org/content/wood-and-metal-sound-effects-volume-2", CC0,
     ['https://opengameart.org/sites/default/files/wood_and_metal_vol.2_0.zip']),
    ('oga_crunch_cucumber', "opengameart.org", 'Crunch sounds (cucumber)', 'cogitollc',
     "https://opengameart.org/content/crunch-sounds-cucumber", CC0,
     ['https://opengameart.org/sites/default/files/crunch_1_0.ogg', 'https://opengameart.org/sites/default/files/crunch_2_0.ogg', 'https://opengameart.org/sites/default/files/crunch_3_0.ogg', 'https://opengameart.org/sites/default/files/crunch_4_0.ogg', 'https://opengameart.org/sites/default/files/crunch_5_0.ogg', 'https://opengameart.org/sites/default/files/crunch_6_0.ogg']),
    ('oga_rd_rpg', "opengameart.org", '80 CC0 RPG SFX', 'rubberduck',
     "https://opengameart.org/content/80-cc0-rpg-sfx", CC0,
     ['https://opengameart.org/sites/default/files/80-CC0-RPG-SFX_0.zip']),
    ('oga_spring_various', "opengameart.org", 'Various Sound Effects', 'Spring Spring',
     "https://opengameart.org/content/various-sound-effects-0", CC0,
     ['https://opengameart.org/sites/default/files/break_stone.wav', 'https://opengameart.org/sites/default/files/crush.wav', 'https://opengameart.org/sites/default/files/fall_0.wav', 'https://opengameart.org/sites/default/files/rustling_of_the_weeds.wav', 'https://opengameart.org/sites/default/files/small_rock_impact.wav', 'https://opengameart.org/sites/default/files/tap_stone.wav', 'https://opengameart.org/sites/default/files/uff.wav']),
    ('oga_amb_morning', "opengameart.org", 'AMB Morning Sounds (Perfect Loop)', 'Kresiek The Furry',
     "https://opengameart.org/content/amb-morning-sounds-perfect-loop", CC0,
     ['https://opengameart.org/sites/default/files/amb_morning.wav']),
    ('oga_amb_outside', "opengameart.org", 'AMB Outside 1', 'Kresiek The Furry',
     "https://opengameart.org/content/amb-outside-1", CC0,
     ['https://opengameart.org/sites/default/files/amb_outdoor1_loop.wav']),
    ('oga_park', "opengameart.org", 'Park ambiences', 'Thimras',
     "https://opengameart.org/content/park-ambiences", CC0,
     ['https://opengameart.org/sites/default/files/park_ambience_birds.wav', 'https://opengameart.org/sites/default/files/park_ambience_river.wav', 'https://opengameart.org/sites/default/files/park_ambience_wind.wav']),
    ('oga_forest_birds', "opengameart.org", 'Forest bird sounds', 'pauliuw',
     "https://opengameart.org/content/forest-bird-sounds", CC0,
     ['https://opengameart.org/sites/default/files/forest_birds.7z']),
    ('oga_isaiah_birds', "opengameart.org", 'Ambient Bird Sounds', 'isaiah658',
     "https://opengameart.org/content/ambient-bird-sounds", CC0,
     ['https://opengameart.org/sites/default/files/birds-isaiah658_0.ogg']),
    ('oga_syncopika_birds', "opengameart.org", 'Bird chirping sounds', 'syncopika',
     "https://opengameart.org/content/bird-chirping-sounds", CC0,
     ['https://opengameart.org/sites/default/files/birdchirping071414.wav']),
    ('oga_crickets_loop', "opengameart.org", 'Crickets Ambient Noise - loopable', 'Wolfgang_',
     "https://opengameart.org/content/crickets-ambient-noise-loopable", CC0,
     ['https://opengameart.org/sites/default/files/crickets_1.mp3']),
    ('oga_syncopika_crickets', "opengameart.org", 'cricket sounds', 'syncopika',
     "https://opengameart.org/content/cricket-sounds", CC0,
     ['https://opengameart.org/sites/default/files/cricketsounds090613.wav']),
    ('oga_syncopika_cicada', "opengameart.org", 'cicada sounds', 'syncopika',
     "https://opengameart.org/content/cicada-sounds", CC0,
     ['https://opengameart.org/sites/default/files/082526-cicadasounds_0.ogg', 'https://opengameart.org/sites/default/files/082526-cicadasounds2_0.ogg']),
    ('oga_drip_loop', "opengameart.org", 'Dripping water loop', 'qubodup',
     "https://opengameart.org/content/dripping-water-loop", CC0,
     ['https://opengameart.org/sites/default/files/atmosbasement.mp3_.flac']),
    ('oga_water_drops', "opengameart.org", 'Water Drops', 'CeoxBlackj',
     "https://opengameart.org/content/water-drops", CC0,
     ['https://opengameart.org/sites/default/files/water%20drops.mp3']),
    ('oga_dungeon', "opengameart.org", 'Loopable Dungeon Ambience', 'JaggedStone',
     "https://opengameart.org/content/loopable-dungeon-ambience", CC0,
     ['https://opengameart.org/sites/default/files/dungeon_ambient_1_0.ogg']),
    ('oga_vistula', "opengameart.org", 'Sea and river wave sounds', 'RandomMind',
     "https://opengameart.org/content/sea-and-river-wave-sounds", CC0,
     ['https://opengameart.org/sites/default/files/VistulaShort_0.mp3']),
    ('oga_waves_transitking', "opengameart.org", 'Water Waves', 'qubodup',
     "https://opengameart.org/content/water-waves", CC0,
     ['https://opengameart.org/sites/default/files/wave_01_cc0-11505__transitking__wavesound.flac', 'https://opengameart.org/sites/default/files/wave_02_cc0-11505__transitking__wavesound.flac', 'https://opengameart.org/sites/default/files/wave_03_cc0-11505__transitking__wavesound.flac', 'https://opengameart.org/sites/default/files/wave_04_cc0-11505__transitking__wavesound.flac']),
    ('oga_waves_jasinski', "opengameart.org", 'Beach Ocean Waves', 'qubodup',
     "https://opengameart.org/content/beach-ocean-waves", CC0,
     ['https://opengameart.org/sites/default/files/wave_01_cc0-18363__jasinski__alkaibeach.flac', 'https://opengameart.org/sites/default/files/wave_02_cc0-18363__jasinski__alkaibeach.flac', 'https://opengameart.org/sites/default/files/wave_03_cc0-18363__jasinski__alkaibeach.flac', 'https://opengameart.org/sites/default/files/wave_04_cc0-18363__jasinski__alkaibeach.flac']),
    ('oga_rain_ylmir', "opengameart.org", 'Rain (loopable)', 'Ylmir',
     "https://opengameart.org/content/rain-loopable", CC0,
     ['https://opengameart.org/sites/default/files/Rain%20OGG.zip']),
    ('oga_rain_kresiek1', "opengameart.org", 'AMB Rain Loop 1', 'Kresiek The Furry',
     "https://opengameart.org/content/amb-rain-loop-1", CC0,
     ['https://opengameart.org/sites/default/files/amb_rain_loop_1.wav']),
    ('oga_rain_kresiek2', "opengameart.org", 'AMB Rain Loop 2', 'Kresiek The Furry',
     "https://opengameart.org/content/amb-rain-loop-2", CC0,
     ['https://opengameart.org/sites/default/files/amb_rain2.flac']),
    ('oga_rain_ove', "opengameart.org", 'Rain (Ambient, Not Loopable) 2 Versions Available.', 'OveMelaa',
     "https://opengameart.org/content/rain-ambient-not-loopable-2-versions-available", CC0,
     ['https://opengameart.org/sites/default/files/Ove%20Melaa%20-%20Rainy%20%28NOT%20loopable%29%20Long%20Version.ogg']),
    ('oga_rain_gutter', "opengameart.org", 'Rain in the Gutter Loop', 'Ogrebane',
     "https://opengameart.org/content/rain-gutter-loop", CC0,
     ['https://opengameart.org/sites/default/files/rain-gutter-loop_0.mp3']),
    ('oga_rd_loops', "opengameart.org", '30 CC0 SFX loops', 'rubberduck',
     "https://opengameart.org/content/30-cc0-sfx-loops", CC0,
     ['https://opengameart.org/sites/default/files/sfx_loops.zip']),
    ('oga_rain_thunder', "opengameart.org", 'Rain + Long Thunder', 'WuxiaScrub',
     "https://opengameart.org/content/rain-long-thunder", CC0,
     ['https://opengameart.org/sites/default/files/rain-thunder.ogg']),
    ('oga_ignasd_wind', "opengameart.org", 'Wind', 'IgnasD',
     "https://opengameart.org/content/wind", CC0,
     ['https://opengameart.org/sites/default/files/wind.zip']),
    ('oga_mild_wind', "opengameart.org", 'Mild Wind Background Noise', 'Bashar3A',
     "https://opengameart.org/content/mild-wind-background-noise", CC0,
     ['https://opengameart.org/sites/default/files/wind%20background%20noise%202.wav']),
    ('oga_short_wind', "opengameart.org", 'Short wind sound', 'remaxim',
     "https://opengameart.org/content/short-wind-sound", CC0,
     ['https://opengameart.org/sites/default/files/short%20wind%20sound.wav']),
    ('oga_underwater', "opengameart.org", 'underwater or space engine rumble', 'gmason',
     "https://opengameart.org/content/underwater-or-space-engine-rumble", CC0,
     ['https://opengameart.org/sites/default/files/underwater_or_space_engine_0.ogg', 'https://opengameart.org/sites/default/files/deep_rumble.ogg']),
    ('oga_bubbles', "opengameart.org", 'Bubble Sound Effects', 'BMacZero',
     "https://opengameart.org/content/bubble-sound-effects", CC0,
     ['https://opengameart.org/sites/default/files/bubbles-single2.wav', 'https://opengameart.org/sites/default/files/bubbles-single1.wav', 'https://opengameart.org/sites/default/files/bubbles-single3.wav', 'https://opengameart.org/sites/default/files/bubbles-loop1-amp.wav', 'https://opengameart.org/sites/default/files/bubbles-loop2-amp.wav']),
    ('oga_tree_creak', "opengameart.org", 'Tree Creaking', 'AntumDeluge',
     "https://opengameart.org/content/tree-creaking", CC0,
     ['https://opengameart.org/sites/default/files/tree_creak.flac']),
    ('oga_rd_water', "opengameart.org", '40 CC0 water / splash / slime SFX', 'rubberduck',
     "https://opengameart.org/content/40-cc0-water-splash-slime-sfx", CC0,
     ['https://opengameart.org/sites/default/files/water-splash-slime-sfx.zip']),
    ('oga_ezwa_splash', "opengameart.org", '6 Short water splashes', 'qubodup',
     "https://opengameart.org/content/6-short-water-splashes", CC0,
     ['https://opengameart.org/sites/default/files/ezwa-water_splash.7z']),
    ('oga_skippy', "opengameart.org", 'Skippy Fish Water Sound Collection', 'jcpmcdonald',
     "https://opengameart.org/content/skippy-fish-water-sound-collection", CC0,
     ['https://opengameart.org/sites/default/files/bubbles.wav', 'https://opengameart.org/sites/default/files/water.wav', 'https://opengameart.org/sites/default/files/swim.wav', 'https://opengameart.org/sites/default/files/eating.wav', 'https://opengameart.org/sites/default/files/waterReentry.wav', 'https://opengameart.org/sites/default/files/scream.wav']),
    ('oga_qubodup_screams', "opengameart.org", '15 vocal male strain/hurt/pain/jump sounds', 'qubodup',
     "https://opengameart.org/content/15-vocal-male-strainhurtpainjump-sounds", CC0,
     ['https://opengameart.org/sites/default/files/slightscreams.7z']),
    ('oga_emopreben_player', "opengameart.org", 'Playersounds 1 - By Emopreben', 'EmoPreben',
     "https://opengameart.org/content/playersounds-1-by-emopreben", CC0,
     ['https://opengameart.org/sites/default/files/Playersounds%20-%20Wav.zip']),
    ('oga_emopreben_pain', "opengameart.org", 'Pain sounds by EmoPreben', 'EmoPreben',
     "https://opengameart.org/content/pain-sounds-by-emopreben", CC0,
     ['https://opengameart.org/sites/default/files/painsounds.zip']),
    ('oga_manhurt', "opengameart.org", 'Man hurt sounds', 'Aharabada',
     "https://opengameart.org/content/man-hurt-sounds", CC0,
     ['https://opengameart.org/sites/default/files/ManHurt.ogg']),
    ('oga_jump_landing', "opengameart.org", 'Jump Landing', 'Macro',
     "https://opengameart.org/content/jump-landing", CC0,
     ['https://opengameart.org/sites/default/files/Jump%201.wav', 'https://opengameart.org/sites/default/files/Jump%202.wav', 'https://opengameart.org/sites/default/files/Jump%203.wav']),
    ('oga_jumpland', "opengameart.org", 'Jump Landing Sound', 'qubodup',
     "https://opengameart.org/content/jump-landing-sound", CC0,
     ['https://opengameart.org/sites/default/files/jumpland.wav']),
    ('oga_ignasd_jump', "opengameart.org", 'Jumping man sounds', 'IgnasD',
     "https://opengameart.org/content/jumping-man-sounds", CC0,
     ['https://opengameart.org/sites/default/files/JumpSoundsMan.zip']),
    ('oga_wet_towel', "opengameart.org", '12 wet towel hitting/falling/punching floor sounds', 'qubodup',
     "https://opengameart.org/content/12-wet-towel-hittingfallingpunching-floor-sounds", CC0,
     ['https://opengameart.org/sites/default/files/wet_towel_on_floor.7z']),
    ('oga_qubodup_punch', "opengameart.org", 'Punch', 'qubodup',
     "https://opengameart.org/content/punch", CC0,
     ['https://opengameart.org/sites/default/files/qubodupPunch.7z']),
    ('oga_fabric', "opengameart.org", 'Fabric Rustling', 'Iochi Glaucus',
     "https://opengameart.org/content/fabric-rustling", CC0,
     ['https://opengameart.org/sites/default/files/rustling.ogg']),
    ('oga_fire_crackling', "opengameart.org", 'Fire Crackling', 'AntumDeluge',
     "https://opengameart.org/content/fire-crackling", CC0,
     ['https://opengameart.org/sites/default/files/fire-1.wav']),
    ('oga_fireplace', "opengameart.org", 'Fireplace Sound loop', 'PagDev',
     "https://opengameart.org/content/fireplace-sound-loop", CC0,
     ['https://opengameart.org/sites/default/files/fire.wav']),
    ('oga_catching_fire', "opengameart.org", 'Catching fire', 'themightyglider',
     "https://opengameart.org/content/catching-fire", CC0,
     ['https://opengameart.org/sites/default/files/flame_0.ogg']),
    ('oga_flare', "opengameart.org", 'Flare ignition', 'qubodup',
     "https://opengameart.org/content/flare-ignition", CC0,
     ['https://opengameart.org/sites/default/files/ignition.flac']),
    ('oga_blacksmith', "opengameart.org", "Blacksmith's Hammer", 'VishwaJai',
     "https://opengameart.org/content/blacksmiths-hammer", CC0,
     ['https://opengameart.org/sites/default/files/blacksmithhammer.wav']),
    ('oga_sack_gold', "opengameart.org", 'Sack of Gold', 'Amarikah',
     "https://opengameart.org/content/sack-of-gold", CC0,
     ['https://opengameart.org/sites/default/files/gold_sack.wav']),
    ('oga_m_meadow', "opengameart.org", 'Meadow Thoughts', 'Écrivain',
     "https://opengameart.org/content/meadow-thoughts", CC0,
     ['https://opengameart.org/sites/default/files/Meadow%20Thoughts.ogg']),
    ('oga_m_bluebonnet', "opengameart.org", 'Bluebonnet', 'Kistol',
     "https://opengameart.org/content/bluebonnet", CC0,
     ['https://opengameart.org/sites/default/files/bluebonnet_in_b_major_2.ogg']),
    ('oga_m_another_august', "opengameart.org", 'Another August', 'cynicmusic',
     "https://opengameart.org/content/another-august", CC0,
     ['https://opengameart.org/sites/default/files/013_Another_August_0.mp3']),
    ('oga_m_catmint', "opengameart.org", 'Catmint', 'Kistol',
     "https://opengameart.org/content/catmint", CC0,
     ['https://opengameart.org/sites/default/files/catmint_in_c_major_3.ogg', 'https://opengameart.org/sites/default/files/catmint_in_c_major_looped.ogg']),
    ('oga_m_forget_me_not', "opengameart.org", 'Forget Me Not', 'Kistol',
     "https://opengameart.org/content/forget-me-not", CC0,
     ['https://opengameart.org/sites/default/files/forget_me_not_in_f_major_0.ogg', 'https://opengameart.org/sites/default/files/forget_me_not_in_f_major_looped.ogg']),
    ('oga_m_apple_cider', "opengameart.org", 'Apple Cider', 'Zane Little Music',
     "https://opengameart.org/content/apple-cider", CC0,
     ['https://opengameart.org/sites/default/files/apple_cider.ogg']),
    ('oga_m_november_snow', "opengameart.org", 'November Snow', 'cynicmusic',
     "https://opengameart.org/content/november-snow", CC0,
     ['https://opengameart.org/sites/default/files/155%20November_snow-33_tape_leveled.mp3']),
    ('oga_winter_wind', "opengameart.org", 'Winter Wind', 'wipics',
     "https://opengameart.org/content/winter-wind", CC0,
     ['https://opengameart.org/sites/default/files/winter-wind_0.mp3', 'https://opengameart.org/sites/default/files/winter-wind-short.mp3']),
    # ------------------------------------------------- Wikimedia Commons (CC0)
    ("cm_howling_wind", "commons.wikimedia.org", "Howling wind", "Tvabutzku1234",
     "https://commons.wikimedia.org/wiki/File:Howling_wind.ogg", CC0,
     ["https://upload.wikimedia.org/wikipedia/commons/2/2d/Howling_wind.ogg"]),
    ("cm_nightjar", "commons.wikimedia.org", "Caprimulgus europaeus (nightjar song)", "ElsaDono",
     "https://commons.wikimedia.org/wiki/File:Caprimulgus_europaeus.ogg", CC0,
     ["https://upload.wikimedia.org/wikipedia/commons/d/d8/Caprimulgus_europaeus.ogg"]),
    ("cm_light_rain", "commons.wikimedia.org", "Light Rain Distant Thunder July 5th 2016", "kvgarlic",
     "https://commons.wikimedia.org/wiki/File:Light_Rain_Distant_Thunder_July_5th_2016.wav", CC0,
     ["https://upload.wikimedia.org/wikipedia/commons/b/b6/Light_Rain_Distant_Thunder_July_5th_2016.wav"]),
    ("cm_swale", "commons.wikimedia.org", "Swale (small swale trickling into a stream)", "Ksd5",
     "https://commons.wikimedia.org/wiki/File:Swale.ogg", CC0,
     ["https://upload.wikimedia.org/wikipedia/commons/8/84/Swale.ogg"]),
    # ------------- Wikimedia Commons (public-domain dedication, PD-self; used only where CC0 fell short)
    ("pd_tonitrus", "commons.wikimedia.org", "Tonitrus (thunder, Southern Finland)", "Mysid",
     "https://commons.wikimedia.org/wiki/File:Tonitrus.ogg", PD,
     ["https://upload.wikimedia.org/wikipedia/commons/c/c8/Tonitrus.ogg"]),
    ("pd_ezwa_storm", "commons.wikimedia.org", "Rain, thunder and other noises / Rain, thunder and birds / Rain and thunder / Thunder and rain on a veranda", "ezwa",
     "https://commons.wikimedia.org/wiki/File:Rain_thunder_and_other_noises.ogg", PD,
     ["https://upload.wikimedia.org/wikipedia/commons/f/f3/Rain_thunder_and_other_noises.ogg",
      "https://upload.wikimedia.org/wikipedia/commons/a/ab/Rain_thunder_and_birds.ogg",
      "https://upload.wikimedia.org/wikipedia/commons/b/bb/Rain_and_thunder_%281%29.ogg",
      "https://upload.wikimedia.org/wikipedia/commons/e/e7/Thunder_and_rain_on_a_v.ogg"]),
    ("pd_caesar_thunder", "commons.wikimedia.org", "Rain and thunder", "Caesar",
     "https://commons.wikimedia.org/wiki/File:Rain_and_thunder.ogg", PD,
     ["https://upload.wikimedia.org/wikipedia/commons/4/42/Rain_and_thunder.ogg"]),
    ("pd_dsw4", "commons.wikimedia.org", "Waves / Forest lawn creek / Walking through woods", "Dsw4",
     "https://commons.wikimedia.org/wiki/File:Waves.ogg", PD,
     ["https://upload.wikimedia.org/wikipedia/commons/1/1f/Waves.ogg",
      "https://upload.wikimedia.org/wikipedia/commons/9/9e/Forest_lawn_creek.ogg",
      "https://upload.wikimedia.org/wikipedia/commons/2/20/Walking_through_woods.ogg"]),
    ("pd_moose", "commons.wikimedia.org", "Moose mate", "Bwells",
     "https://commons.wikimedia.org/wiki/File:Moose_mate.ogg", PD,
     ["https://upload.wikimedia.org/wikipedia/commons/f/fd/Moose_mate.ogg"]),
    ("pd_night", "commons.wikimedia.org", "Country night noise", "hc",
     "https://commons.wikimedia.org/wiki/File:Country_night_noise.ogg", PD,
     ["https://upload.wikimedia.org/wikipedia/commons/f/fb/Country_night_noise.ogg"]),
    ("pd_grass_fire", "commons.wikimedia.org", "Dry grass burning in open fireplace", "ezwa",
     "https://commons.wikimedia.org/wiki/File:Dry_grass_burning_in_open_fireplace.ogg", PD,
     ["https://upload.wikimedia.org/wikipedia/commons/d/d8/Dry_grass_burning_in_open_fireplace.ogg"]),
    ("pd_shore", "commons.wikimedia.org", "On a pebble beach / Water over rocks as tide recedes", "earthcalling",
     "https://commons.wikimedia.org/wiki/File:On_a_pebble_beach.ogg", PD,
     ["https://upload.wikimedia.org/wikipedia/commons/7/73/On_a_pebble_beach.ogg",
      "https://upload.wikimedia.org/wikipedia/commons/6/60/Water_over_rocks_as_tide_recedes.ogg"]),
]


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for b in iter(lambda: f.read(1 << 20), b""):
            h.update(b)
    return h.hexdigest()


def fetch(url, dest):
    if os.path.exists(dest) and os.path.getsize(dest) > 0:
        return False
    # Wikimedia asks bots for a descriptive User-Agent (and rate-limits fast clients)
    hdr = WM_UA if "wikimedia.org" in url else UA
    req = urllib.request.Request(url, headers=hdr)
    tmp = dest + ".part"
    with urllib.request.urlopen(req, timeout=300) as r, open(tmp, "wb") as f:
        while True:
            b = r.read(1 << 20)
            if not b:
                break
            f.write(b)
    os.replace(tmp, dest)
    return True


def extract(path):
    """Extract an archive into a new, empty '<name>.d' directory next to it."""
    low = path.lower()
    if not low.endswith((".zip", ".7z", ".rar", ".tar.gz", ".tgz", ".tar")):
        return None
    into = path + ".d"
    if os.path.isdir(into) and os.listdir(into):
        return into
    os.makedirs(into, exist_ok=True)
    r = subprocess.run(["bsdtar", "-xf", path, "-C", into], capture_output=True)
    if r.returncode != 0:
        print("    !! extract failed:", r.stderr.decode()[:200])
    return into


def licence_files(root):
    found = []
    for dp, _, fns in os.walk(root):
        for fn in fns:
            if any(k in fn.lower() for k in ("licen", "readme", "credits", "copying")) and \
                    fn.lower().endswith((".txt", ".md", "")) and os.path.getsize(os.path.join(dp, fn)) < 20000:
                p = os.path.join(dp, fn)
                txt = open(p, "rb").read().decode("utf-8", "ignore")
                found.append({"file": os.path.relpath(p, DL), "text": txt.strip()[:1500]})
    return found


def main(only):
    os.makedirs(DL, exist_ok=True)
    out_path = os.path.join(HERE, "sources.json")
    prov = json.load(open(out_path)) if os.path.exists(out_path) else {}
    for sid, site, title, author, page, lic, urls in SOURCES:
        if only and sid not in only:
            continue
        d = os.path.join(DL, sid)
        os.makedirs(d, exist_ok=True)
        files = []
        for u in urls:
            name = urllib.parse.unquote(u.rstrip("/").split("/")[-1])
            local = os.path.join(d, name)
            try:
                if fetch(u, local):
                    time.sleep(3.0 if "opengameart" in u else 8.0 if "wikimedia" in u else 1.0)
            except Exception as e:  # noqa: BLE001
                print(f"  !! {sid}: {u}: {e}")
                continue
            ex = extract(local)
            files.append({"url": u, "file": name, "bytes": os.path.getsize(local),
                          "sha256": sha256(local), "extracted": bool(ex)})
        prov[sid] = {"site": site, "title": title, "author": author, "page": page,
                     "licence": lic, "files": files, "licence_files": licence_files(d)}
        print(f"{sid:28s} {len(files)}/{len(urls)} files")
        json.dump(prov, open(out_path, "w"), indent=1, sort_keys=True)


if __name__ == "__main__":
    main(set(sys.argv[1:]))
