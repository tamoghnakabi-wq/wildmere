using System;
using UnityEngine;

namespace Wildmere.Core
{
    /// <summary>
    /// Decoupled sound requests. Gameplay code fires named sounds ("deer/call", "rabbit/hop", "bird/chirp",
    /// "water/splash_in", ...; names match Resources/Audio/audio.json keys) and the audio module subscribes
    /// and plays them. With no audio module present these are no-ops.
    /// </summary>
    public static class SoundHub
    {
        /// <summary>(key, world position, volume 0..1)</summary>
        public static event Action<string, Vector3, float> Positional;
        /// <summary>(key, volume 0..1) — non-positional (UI, stingers).</summary>
        public static event Action<string, float> Flat;

        public static void At(string key, Vector3 pos, float volume = 1f) => Positional?.Invoke(key, pos, volume);
        public static void Play(string key, float volume = 1f) => Flat?.Invoke(key, volume);
    }
}
