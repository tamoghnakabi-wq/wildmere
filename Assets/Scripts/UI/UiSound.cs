using System;
using UnityEngine;

namespace Wildmere.UI
{
    /// <summary>
    /// The interface's only coupling to audio. The UI calls <see cref="Play"/> with one of the names
    /// below; the audio module subscribes to <see cref="Requested"/> (or replaces <see cref="Handler"/>)
    /// and plays the matching clip. With nobody listening it is a no-op.
    ///   ui/click  ui/hover  ui/open  ui/close  ui/error  ui/confirm  item/craft
    /// </summary>
    public static class UiSound
    {
        public const string Click = "ui/click", Hover = "ui/hover", Open = "ui/open", Close = "ui/close",
            Error = "ui/error", Confirm = "ui/confirm", Craft = "item/craft";

        /// <summary>Raised for every UI sound request (name as above).</summary>
        public static event Action<string> Requested;

        /// <summary>Optional single handler (e.g. a playback function); called before <see cref="Requested"/>.</summary>
        public static Action<string> Handler;

        /// <summary>Number of requests so far (lets tests verify the hooks fire).</summary>
        public static int Count { get; private set; }

        static float sLastHover;

        public static void Play(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            if (name == Hover)
            {
                // Sweeping the pointer across a list should not machine-gun the hover sound.
                if (Time.unscaledTime - sLastHover < 0.045f) return;
                sLastHover = Time.unscaledTime;
            }
            Count++;
            try
            {
                Handler?.Invoke(name);
                Requested?.Invoke(name);
            }
            catch (Exception e) { Debug.LogWarning($"WM_UI sound handler threw for {name}: {e.Message}"); }
        }
    }
}
