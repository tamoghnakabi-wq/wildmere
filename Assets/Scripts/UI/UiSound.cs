using System;
using UnityEngine;
using Wildmere.Core;

namespace Wildmere.UI
{
    /// <summary>
    /// Thin UI wrapper over <see cref="SoundHub.Play"/> (the audio module plays the clips). Adds
    /// hover rate-limiting and a request counter for tests. Keys:
    ///   ui/click  ui/hover  ui/open  ui/close  ui/error  ui/confirm  item/craft
    /// </summary>
    public static class UiSound
    {
        public const string Click = "ui/click", Hover = "ui/hover", Open = "ui/open", Close = "ui/close",
            Error = "ui/error", Confirm = "ui/confirm", Craft = "item/craft";

        /// <summary>Raised for every request (tests / tooling); playback goes through SoundHub.</summary>
        public static event Action<string> Requested;

        /// <summary>Number of requests so far (lets tests verify the hooks fire).</summary>
        public static int Count { get; private set; }

        static float sLastHover;

        public static void Play(string name, float volume = 1f)
        {
            if (string.IsNullOrEmpty(name)) return;
            if (name == Hover)
            {
                // Sweeping the pointer across a list should not machine-gun the hover sound.
                if (Time.unscaledTime - sLastHover < 0.045f) return;
                sLastHover = Time.unscaledTime;
                volume *= 0.6f;
            }
            Count++;
            try
            {
                SoundHub.Play(name, volume);
                Requested?.Invoke(name);
            }
            catch (Exception e) { Debug.LogWarning($"WM_UI sound listener threw for {name}: {e.Message}"); }
        }
    }
}
