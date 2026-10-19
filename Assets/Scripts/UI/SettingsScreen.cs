using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;
using Wildmere.Core;

namespace Wildmere.UI
{
    /// <summary>
    /// Tabbed settings (Graphics / Audio / Controls). Every edit applies live (debounced
    /// <see cref="GameSettings.NotifyChanged"/>); closing the screen persists with
    /// <see cref="GameSettings.Save"/>. Editing any preset-driven graphics field switches the preset to Custom.
    /// </summary>
    public sealed class SettingsScreen : UiScreen
    {
        public override string Id => "settings";
        public enum Tab { Graphics, Audio, Controls, Updates }

        readonly Dictionary<Tab, Button> mTabs = new Dictionary<Tab, Button>();
        readonly Dictionary<Tab, ScrollView> mPages = new Dictionary<Tab, ScrollView>();
        readonly List<Action> mRefresh = new List<Action>();
        readonly Dictionary<Control, Button> mBindButtons = new Dictionary<Control, Button>();
        Button mReset;
        public Tab Current { get; private set; } = Tab.Graphics;
        public WmChoice PresetChoice { get; private set; }
        public WmSlider ViewDistance { get; private set; }
        bool mDirty;
        float mDirtyTimer;
        Control? mCapture;
        int mCaptureFrame;
        float mPulse;

        static GameSettings S => GameSettings.Current;

        public override void Build()
        {
            Root.Add(Ui.Div("fill", "scrim"));
            var center = Root.Append(Ui.Div("fill", "center"));
            var wrap = center.Append(Ui.Panel(out var panel, "settings-panel"));
            wrap.AddToClassList("anim-pop");

            var head = panel.Append(Ui.Div("settings-head"));
            head.Add(Ui.Text("Settings", "panel__title"));
            var tabs = head.Append(Ui.Div("tabs"));
            foreach (Tab t in Enum.GetValues(typeof(Tab)))
            {
                var tab = t;
                var b = tabs.Append(new Button(() => { UiSound.Play(UiSound.Click); Show(tab); }) { text = t.ToString() }.Cls("tab"));
                b.RegisterCallback<PointerEnterEvent>(_ => UiSound.Play(UiSound.Hover));
                mTabs[t] = b;
            }

            var body = panel.Append(Ui.Div("settings-body"));
            foreach (Tab t in Enum.GetValues(typeof(Tab)))
            {
                var sv = Ui.Scroll(body);
                sv.contentContainer.AddToClassList("settings-page");
                mPages[t] = sv;
            }
            BuildGraphics(mPages[Tab.Graphics].contentContainer);
            BuildAudio(mPages[Tab.Audio].contentContainer);
            BuildControls(mPages[Tab.Controls].contentContainer);
            BuildUpdates(mPages[Tab.Updates].contentContainer);

            var foot = panel.Append(Ui.Div("settings-foot"));
            mReset = foot.Append(Ui.Btn("Reset controls to defaults", ResetControls, null, "btn--ghost", "btn--small"));
            foot.Add(Ui.Div("grow"));
            foot.Add(Ui.Text("Changes apply instantly", "t-small", "t-faint", "gap-r-16"));
            var done = foot.Append(Ui.Btn("Done", () => UI.Pop(this), UiSound.Close, "btn--primary"));
            done.style.minWidth = 150;
        }

        // ------------------------------------------------------------------ rows

        VisualElement Section(VisualElement page, string title)
        {
            return page.Append(Ui.Text(title, "t-micro", "section-title"));
        }

        VisualElement Row(VisualElement page, string label, string hint, VisualElement control)
        {
            var row = page.Append(Ui.Div("setting-row"));
            var text = row.Append(Ui.Div("setting-row__text").Ignore());
            text.Add(Ui.Text(label, "setting-row__label").Ignore());
            if (!string.IsNullOrEmpty(hint)) text.Add(Ui.Text(hint, "setting-row__hint").Ignore());
            var ctl = row.Append(Ui.Div("setting-row__control"));
            ctl.Add(control);
            if (control is WmSlider || control is WmChoice || control is WmStepper) control.style.flexGrow = 1;
            return row;
        }

        WmSwitch Switch(VisualElement page, string label, string hint, Func<bool> get, Action<bool> set, bool preset)
        {
            var sw = new WmSwitch();
            sw.Changed += v => { set(v); Edited(preset); };
            mRefresh.Add(() => sw.Value = get());
            Row(page, label, hint, sw);
            return sw;
        }

        WmChoice Choice(VisualElement page, string label, string hint, string[] opts, Func<int> get, Action<int> set, bool preset)
        {
            var c = new WmChoice(opts);
            c.Changed += i => { set(i); Edited(preset); };
            mRefresh.Add(() => c.Index = get());
            Row(page, label, hint, c);
            return c;
        }

        WmSlider Slider(VisualElement page, string label, string hint, float min, float max, float step, Func<float, string> fmt,
            Func<float> get, Action<float> set, bool preset, bool liveNotify = false)
        {
            var s = new WmSlider(min, max, step, fmt);
            s.Changed += v => { set(v); if (preset) MakeCustom(); if (liveNotify) MarkDirty(0.06f); };
            s.Committed += () => Edited(preset);
            mRefresh.Add(() => s.Value = get());
            Row(page, label, hint, s);
            return s;
        }

        void BuildGraphics(VisualElement p)
        {
            PresetChoice = new WmChoice(new[] { "LOW", "MEDIUM", "HIGH", "ULTRA", "CUSTOM" }, passiveIndex: 4, accent: true);
            PresetChoice.Changed += i =>
            {
                S.ApplyPreset((GraphicsPreset)i);
                RefreshAll();
                MarkDirty(0.05f);
            };
            mRefresh.Add(() => PresetChoice.Index = (int)S.Preset);
            Row(p, "Quality preset", "A starting point. Adjust anything below to make it your own.", PresetChoice).AddToClassList("preset-row");

            Section(p, "DISPLAY");
            Choice(p, "Window mode", null, new[] { "WINDOWED", "BORDERLESS", "FULLSCREEN" }, () => S.Fullscreen, v => S.Fullscreen = v, false);
            Switch(p, "Vertical sync", "Matches your display's refresh to prevent tearing.", () => S.VSync, v => S.VSync = v, false);
            var caps = new[] { 0, 30, 60, 90, 120, 144, 165, 240 };
            var capNames = new[] { "Unlimited", "30 fps", "60 fps", "90 fps", "120 fps", "144 fps", "165 fps", "240 fps" };
            var stepper = new WmStepper(capNames);
            stepper.Changed += i => { S.FpsCap = caps[i]; Edited(false); };
            mRefresh.Add(() => { int k = Array.IndexOf(caps, S.FpsCap); stepper.Index = k < 0 ? 0 : k; });
            Row(p, "Frame rate limit", null, stepper);
            Slider(p, "Render scale", "Lower is faster; the interface stays sharp.", 0.5f, 1.5f, 0.05f, v => $"{v * 100f:0}%", () => S.RenderScale, v => S.RenderScale = v, true);
            Slider(p, "Brightness", null, -1f, 1f, 0.05f, v => Mathf.Abs(v) < 0.001f ? "0" : $"{(v > 0 ? "+" : "")}{v * 100f:0}", () => S.Brightness, v => S.Brightness = v, false);
            Slider(p, "Field of view", null, 60f, 110f, 1f, v => $"{v:0}°", () => S.Fov, v => S.Fov = v, false);

            Section(p, "WORLD");
            ViewDistance = Slider(p, "View distance", "How far the world draws, in chunks of 16 blocks.", 4f, 20f, 1f, v => $"{v:0} chunks", () => S.ViewDistance, v => S.ViewDistance = Mathf.RoundToInt(v), true);
            Choice(p, "Shadows", null, new[] { "OFF", "LOW", "MEDIUM", "HIGH" }, () => S.ShadowQuality, v => S.ShadowQuality = v, true);
            Slider(p, "Shadow distance", null, 32f, 200f, 8f, v => $"{v:0} m", () => S.ShadowDistance, v => S.ShadowDistance = v, true);
            Choice(p, "Anti-aliasing", null, new[] { "OFF", "FXAA", "MSAA 2×", "MSAA 4×" }, () => S.Antialiasing, v => S.Antialiasing = v, true);
            Switch(p, "Ambient occlusion", "Soft contact shadows in corners and crevices.", () => S.Ssao, v => S.Ssao = v, true);
            Switch(p, "Bloom", "A gentle glow around bright light.", () => S.Bloom, v => S.Bloom = v, true);
            Switch(p, "Fancy leaves", "See-through foliage instead of solid leaf blocks.", () => S.FancyLeaves, v => S.FancyLeaves = v, true);
            Switch(p, "Clouds", null, () => S.Clouds, v => S.Clouds = v, true);
            Choice(p, "Weather particles", null, new[] { "LOW", "MEDIUM", "HIGH" }, () => S.WeatherParticles, v => S.WeatherParticles = v, true);
            Switch(p, "Water reflections", null, () => S.WaterReflections, v => S.WaterReflections = v, true);

            Section(p, "CAMERA");
            Switch(p, "View bobbing", "Sway the camera gently as you walk.", () => S.HeadBob, v => S.HeadBob = v, false);
            Switch(p, "Camera shake", "Shake on hard landings and damage.", () => S.CameraShake, v => S.CameraShake = v, false);
        }

        Label mUpdStatus;
        Button mUpdCheck, mUpdInstall;

        void BuildUpdates(VisualElement p)
        {
            Section(p, "UPDATES");
            Switch(p, "Download updates automatically",
                "New versions download in the background and install when you quit or restart. Each update is signature-checked before it installs.",
                () => S.AutoUpdate, v => S.AutoUpdate = v, false);
            var actions = Ui.Div("row");
            mUpdCheck = actions.Append(Ui.Btn("Check now", () => Updater.Instance?.CheckNow(), UiSound.Click, "btn--small"));
            mUpdInstall = actions.Append(Ui.Btn("Restart to update", () => Updater.Instance?.InstallAndRestart(), UiSound.Confirm, "btn--primary", "btn--small"));
            mUpdInstall.style.marginLeft = 10;
            Row(p, $"Version {GameRoot.Version}", null, actions);
            mUpdStatus = p.Append(Ui.Text("", "setting-row__hint"));
            mUpdStatus.style.marginTop = 6;
            mRefresh.Add(RefreshUpdates);
            if (Updater.Instance != null) Updater.Instance.Changed += RefreshUpdates;
        }

        void RefreshUpdates()
        {
            var u = Updater.Instance;
            if (mUpdStatus == null) return;
            string s;
            switch (u != null ? u.State : Updater.Phase.Unsupported)
            {
                case Updater.Phase.Checking: s = "Checking for updates…"; break;
                case Updater.Phase.UpToDate: s = "You're on the latest version."; break;
                case Updater.Phase.Available: s = $"Wildmere {u.Latest} is available ({u.DownloadSize / 1048576f:F0} MB)."; break;
                case Updater.Phase.Downloading: s = $"Downloading Wildmere {u.Latest}… {u.Progress * 100f:F0}%"; break;
                case Updater.Phase.Ready: s = $"Wildmere {u.Latest} is downloaded and verified. Restart to install it, or it installs when you quit."; break;
                case Updater.Phase.Error: s = u.Message ?? "Something went wrong. Wildmere will try again later."; break;
                case Updater.Phase.Unsupported: s = u?.Message ?? "Automatic updates are not available."; break;
                default: s = "Wildmere checks for updates automatically."; break;
            }
            mUpdStatus.text = s;
            bool ready = u != null && u.State == Updater.Phase.Ready;
            mUpdInstall.style.display = ready ? DisplayStyle.Flex : DisplayStyle.None;
            mUpdCheck.SetEnabled(u != null && u.State != Updater.Phase.Checking && u.State != Updater.Phase.Downloading && u.State != Updater.Phase.Unsupported);
        }

        void BuildAudio(VisualElement p)
        {
            Section(p, "VOLUME");
            Func<float, string> pct = v => $"{v * 100f:0}%";
            Slider(p, "Master", null, 0f, 1f, 0.01f, pct, () => S.MasterVolume, v => S.MasterVolume = v, false, true);
            Slider(p, "Music", null, 0f, 1f, 0.01f, pct, () => S.MusicVolume, v => S.MusicVolume = v, false, true);
            Slider(p, "Ambience", "Wind, water, birdsong and the rest of the wild.", 0f, 1f, 0.01f, pct, () => S.AmbienceVolume, v => S.AmbienceVolume = v, false, true);
            Slider(p, "Effects", "Footsteps, mining, building and creatures.", 0f, 1f, 0.01f, pct, () => S.EffectsVolume, v => S.EffectsVolume = v, false, true);
            Slider(p, "Interface", "Clicks and menu sounds.", 0f, 1f, 0.01f, pct, () => S.UiVolume, v => S.UiVolume = v, false, true);
        }

        void BuildControls(VisualElement p)
        {
            Section(p, "MOUSE");
            Slider(p, "Look sensitivity", null, 0.1f, 3f, 0.05f, v => $"×{v:0.00}", () => S.MouseSensitivity, v => S.MouseSensitivity = v, false);
            Switch(p, "Invert vertical look", null, () => S.InvertY, v => S.InvertY = v, false);
            Switch(p, "Auto-jump", "Step up single blocks automatically while walking.", () => S.AutoJump, v => S.AutoJump = v, false);

            Section(p, "KEYS");
            foreach (Control c in Enum.GetValues(typeof(Control)))
            {
                var ctl = c;
                var b = new Button(() => BeginCapture(ctl)).Cls("keybind");
                b.RegisterCallback<PointerEnterEvent>(_ => UiSound.Play(UiSound.Hover));
                mBindButtons[c] = b;
                mRefresh.Add(() => b.text = KeyName(S.Key(ctl)));
                Row(p, ControlName(c), null, b);
            }
        }

        // ------------------------------------------------------------------ behaviour

        public void Show(Tab t)
        {
            Current = t;
            CancelCapture();
            foreach (var kv in mTabs) kv.Value.EnableInClassList("tab--on", kv.Key == t);
            foreach (var kv in mPages) kv.Value.style.display = kv.Key == t ? DisplayStyle.Flex : DisplayStyle.None;
            mReset.style.visibility = t == Tab.Controls ? Visibility.Visible : Visibility.Hidden;
            mPages[t].scrollOffset = Vector2.zero;
        }

        protected override void OnShow()
        {
            RefreshAll();
            Show(Current);
            DefaultFocus = mTabs[Current];
        }

        protected override void OnHide()
        {
            CancelCapture();
            mDirty = false;
            GameSettings.Save();
        }

        public void RefreshAll()
        {
            foreach (var r in mRefresh) r();
        }

        void MakeCustom()
        {
            if (S.Preset == GraphicsPreset.Custom) return;
            S.Preset = GraphicsPreset.Custom;
            PresetChoice.Index = (int)GraphicsPreset.Custom;
        }

        void Edited(bool preset)
        {
            if (preset) MakeCustom();
            MarkDirty(0.08f);
        }

        void MarkDirty(float delay)
        {
            mDirty = true;
            mDirtyTimer = delay;
        }

        public override void Tick(float dt)
        {
            if (mDirty)
            {
                mDirtyTimer -= dt;
                if (mDirtyTimer <= 0f) { mDirty = false; GameSettings.NotifyChanged(); }
            }
            if (mCapture.HasValue && Time.frameCount > mCaptureFrame)
            {
                mPulse += dt;
                mBindButtons[mCapture.Value].EnableInClassList("keybind--pulse", Mathf.Repeat(mPulse, 1f) > 0.5f);
                foreach (var k in kKeys)
                {
                    if (!Input.GetKeyDown(k)) continue;
                    if (k == KeyCode.Escape && mCapture.Value != Control.Pause) break; // handled by OnBack
                    Bind(mCapture.Value, k);
                    break;
                }
            }
        }

        public void BeginCapture(Control c)
        {
            CancelCapture();
            mCapture = c;
            mCaptureFrame = Time.frameCount;
            mPulse = 0;
            var b = mBindButtons[c];
            b.text = "Press a key…";
            b.AddToClassList("keybind--listening");
            UiSound.Play(UiSound.Click);
        }

        void CancelCapture()
        {
            if (!mCapture.HasValue) return;
            var b = mBindButtons[mCapture.Value];
            b.RemoveFromClassList("keybind--listening");
            b.RemoveFromClassList("keybind--pulse");
            b.text = KeyName(S.Key(mCapture.Value));
            mCapture = null;
        }

        public bool Capturing => mCapture.HasValue;

        /// <summary>Bind a key; if another action already uses it, the two swap.</summary>
        public void Bind(Control c, KeyCode k)
        {
            var old = S.Key(c);
            foreach (Control o in Enum.GetValues(typeof(Control)))
                if (o != c && S.Key(o) == k) S.Bind(o, old);
            S.Bind(c, k);
            CancelCapture();
            mCapture = null;
            RefreshAll();
            UiSound.Play(UiSound.Confirm);
            MarkDirty(0.05f);
        }

        void ResetControls()
        {
            S.ResetBindings();
            S.MouseSensitivity = 1f;
            S.InvertY = false;
            S.AutoJump = false;
            RefreshAll();
            MarkDirty(0.05f);
        }

        public override bool OnBack()
        {
            if (mCapture.HasValue)
            {
                if (mCapture.Value == Control.Pause) Bind(Control.Pause, KeyCode.Escape);
                else CancelCapture();
                UI.EscConsumedFrame = Time.frameCount;
                return true;
            }
            UI.Pop(this);
            return true;
        }

        public override bool CapturesMouse => true;

        // ------------------------------------------------------------------ names

        static readonly KeyCode[] kKeys = BuildKeys();

        static KeyCode[] BuildKeys()
        {
            var l = new List<KeyCode>();
            foreach (KeyCode k in Enum.GetValues(typeof(KeyCode)))
                if (k != KeyCode.None && (int)k < (int)KeyCode.JoystickButton0 && !l.Contains(k)) l.Add(k);
            return l.ToArray();
        }

        public static string ControlName(Control c) => c switch
        {
            Control.Forward => "Move forward",
            Control.Back => "Move back",
            Control.Left => "Strafe left",
            Control.Right => "Strafe right",
            Control.Jump => "Jump / swim up",
            Control.Sprint => "Sprint",
            Control.Crouch => "Sneak",
            Control.Inventory => "Inventory & crafting",
            Control.Drop => "Drop item",
            Control.Pause => "Pause / back",
            Control.Attack => "Mine / attack",
            Control.Use => "Use / place",
            Control.PickBlock => "Pick block",
            Control.Screenshot => "Screenshot",
            Control.ToggleHud => "Hide interface",
            _ => c.ToString(),
        };

        public static string KeyName(KeyCode k)
        {
            switch (k)
            {
                case KeyCode.Mouse0: return "Left Click";
                case KeyCode.Mouse1: return "Right Click";
                case KeyCode.Mouse2: return "Middle Click";
                case KeyCode.Mouse3: return "Mouse 4";
                case KeyCode.Mouse4: return "Mouse 5";
                case KeyCode.Mouse5: return "Mouse 6";
                case KeyCode.Mouse6: return "Mouse 7";
                case KeyCode.LeftControl: return "Left Ctrl";
                case KeyCode.RightControl: return "Right Ctrl";
                case KeyCode.LeftCommand: return Application.platform == RuntimePlatform.OSXPlayer ? "Left Cmd" : "Left Win";
                case KeyCode.RightCommand: return Application.platform == RuntimePlatform.OSXPlayer ? "Right Cmd" : "Right Win";
                case KeyCode.LeftAlt: return Application.platform == RuntimePlatform.OSXPlayer ? "Left Option" : "Left Alt";
                case KeyCode.RightAlt: return Application.platform == RuntimePlatform.OSXPlayer ? "Right Option" : "Right Alt";
                case KeyCode.Escape: return "Esc";
                case KeyCode.Return: return "Enter";
                case KeyCode.BackQuote: return "`";
                case KeyCode.Minus: return "-";
                case KeyCode.Equals: return "=";
                case KeyCode.LeftBracket: return "[";
                case KeyCode.RightBracket: return "]";
                case KeyCode.Semicolon: return ";";
                case KeyCode.Quote: return "'";
                case KeyCode.Comma: return ",";
                case KeyCode.Period: return ".";
                case KeyCode.Slash: return "/";
                case KeyCode.Backslash: return "\\";
                case KeyCode.UpArrow: return "Up";
                case KeyCode.DownArrow: return "Down";
                case KeyCode.LeftArrow: return "Left";
                case KeyCode.RightArrow: return "Right";
                case KeyCode.None: return "Unbound";
            }
            var s = k.ToString();
            if (s.StartsWith("Alpha") && s.Length == 6) return s.Substring(5);
            if (s.StartsWith("Keypad")) return "Num " + Spaced(s.Substring(6));
            return Spaced(s);
        }

        static string Spaced(string s)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (i > 0 && char.IsUpper(s[i]) && !char.IsUpper(s[i - 1])) sb.Append(' ');
                sb.Append(s[i]);
            }
            return sb.ToString();
        }
    }
}
