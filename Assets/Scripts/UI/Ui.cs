using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Wildmere.UI
{
    /// <summary>
    /// Tiny element-building vocabulary so screens read like markup. Styling lives in
    /// Resources/UI/Wildmere.uss; code only adds classes (and sets genuinely dynamic values).
    /// </summary>
    public static class Ui
    {
        public static VisualElement Div(params string[] classes)
        {
            var e = new VisualElement();
            foreach (var c in classes) if (!string.IsNullOrEmpty(c)) e.AddToClassList(c);
            return e;
        }

        public static Label Text(string text, params string[] classes)
        {
            var l = new Label(text);
            foreach (var c in classes) if (!string.IsNullOrEmpty(c)) l.AddToClassList(c);
            return l;
        }

        public static T Append<T>(this VisualElement parent, T child) where T : VisualElement
        {
            parent.Add(child);
            return child;
        }

        public static T Cls<T>(this T e, params string[] classes) where T : VisualElement
        {
            foreach (var c in classes) if (!string.IsNullOrEmpty(c)) e.AddToClassList(c);
            return e;
        }

        public static T Ignore<T>(this T e) where T : VisualElement
        {
            e.pickingMode = PickingMode.Ignore;
            return e;
        }

        /// <summary>A button with the house click/hover sounds. <paramref name="sound"/> null = ui/click.</summary>
        public static Button Btn(string text, Action onClick, string sound, params string[] classes)
        {
            var b = new Button { text = text };
            b.AddToClassList("btn");
            foreach (var c in classes) if (!string.IsNullOrEmpty(c)) b.AddToClassList(c);
            Wire(b, onClick, sound);
            return b;
        }

        /// <summary>Hook click + hover sounds onto any button-like element.</summary>
        public static void Wire(Button b, Action onClick, string sound = null)
        {
            b.clicked += () =>
            {
                UiSound.Play(sound ?? UiSound.Click);
                onClick?.Invoke();
            };
            b.RegisterCallback<PointerEnterEvent>(_ => { if (b.enabledInHierarchy) UiSound.Play(UiSound.Hover); });
        }

        /// <summary>Big text item for the main/pause menus (accent bar slides in on hover).</summary>
        public static Button MenuItem(string text, Action onClick, string sub = null, string sound = null)
        {
            var b = new Button();
            b.AddToClassList("menu-item");
            b.Add(Div("menu-item__bar").Ignore());
            b.Add(Text(text, "menu-item__text").Ignore());
            if (sub != null) b.Add(Text(sub, "menu-item__sub").Ignore());
            Wire(b, onClick, sound);
            return b;
        }

        /// <summary>A dark rounded panel with a soft drop shadow behind it.</summary>
        public static VisualElement Panel(out VisualElement panel, params string[] panelClasses)
        {
            var wrap = Div("panel-wrap");
            var shadow = wrap.Append(Div("panel-shadow").Ignore());
            shadow.style.backgroundImage = UiTextures.PanelShadow;
            panel = wrap.Append(Div("panel"));
            foreach (var c in panelClasses) if (!string.IsNullOrEmpty(c)) panel.AddToClassList(c);
            return wrap;
        }

        /// <summary>Vertical scroll view with the house thin scroll indicator.</summary>
        public static ScrollView Scroll(VisualElement host, params string[] classes)
        {
            var sv = new ScrollView(ScrollViewMode.Vertical)
            {
                verticalScrollerVisibility = ScrollerVisibility.Hidden,
                horizontalScrollerVisibility = ScrollerVisibility.Hidden,
                mouseWheelScrollSize = 64f,
            };
            sv.AddToClassList("scroll");
            foreach (var c in classes) if (!string.IsNullOrEmpty(c)) sv.AddToClassList(c);
            host.Add(sv);
            var bar = Div("scroll-bar").Ignore();
            var thumb = bar.Append(Div("scroll-bar__thumb").Ignore());
            // The indicator lives inside the scroll view's hierarchy but outside the content container.
            sv.hierarchy.Add(bar);
            void Sync()
            {
                float view = sv.contentViewport.layout.height, content = sv.contentContainer.layout.height;
                if (float.IsNaN(view) || float.IsNaN(content) || content <= view + 1f) { bar.style.display = DisplayStyle.None; return; }
                bar.style.display = DisplayStyle.Flex;
                float track = Mathf.Max(1f, bar.layout.height);
                float h = Mathf.Max(28f, track * view / content);
                float max = content - view;
                float t = max > 0 ? Mathf.Clamp01(sv.scrollOffset.y / max) : 0f;
                thumb.style.height = h;
                thumb.style.top = (track - h) * t;
            }
            sv.verticalScroller.valueChanged += _ => Sync();
            sv.contentContainer.RegisterCallback<GeometryChangedEvent>(_ => Sync());
            sv.RegisterCallback<GeometryChangedEvent>(_ => Sync());
            return sv;
        }

        public static string Plural(int n, string one, string many) => n == 1 ? one : many;

        public static string Duration(double seconds)
        {
            if (seconds < 60) return "under a minute";
            int m = (int)(seconds / 60);
            if (m < 60) return $"{m} min";
            int h = m / 60; m %= 60;
            return m == 0 ? $"{h} h" : $"{h} h {m} min";
        }

        public static string Ago(DateTime utc)
        {
            var d = DateTime.UtcNow - utc;
            if (d.TotalSeconds < 0) d = TimeSpan.Zero;
            if (d.TotalMinutes < 1) return "just now";
            if (d.TotalMinutes < 60) { int m = (int)d.TotalMinutes; return $"{m} {Plural(m, "minute", "minutes")} ago"; }
            if (d.TotalHours < 24) { int h = (int)d.TotalHours; return $"{h} {Plural(h, "hour", "hours")} ago"; }
            if (d.TotalDays < 2) return "yesterday";
            if (d.TotalDays < 14) return $"{(int)d.TotalDays} days ago";
            return utc.ToLocalTime().ToString("d MMM yyyy");
        }
    }
}
