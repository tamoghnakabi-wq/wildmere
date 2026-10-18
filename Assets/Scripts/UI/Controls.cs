using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Wildmere.UI
{
    /// <summary>Rounded track slider with a value readout. Drag, click, or arrow keys when focused.</summary>
    public sealed class WmSlider : VisualElement
    {
        public readonly float Min, Max, Step;
        readonly Func<float, string> mFormat;
        readonly VisualElement mTrack, mFill, mThumb;
        readonly Label mValueLabel;
        float mValue;
        bool mDragging;

        public event Action<float> Changed;     // every change (live)
        public event Action Committed;          // pointer released / key step

        public WmSlider(float min, float max, float step, Func<float, string> format)
        {
            Min = min; Max = max; Step = step; mFormat = format;
            AddToClassList("slider");
            focusable = true;
            mTrack = this.Add(Ui.Div("slider__track"));
            var rail = mTrack.Add(Ui.Div("slider__rail").Ignore());
            mFill = rail.Add(Ui.Div("slider__fill").Ignore());
            mThumb = mTrack.Add(Ui.Div("slider__thumb").Ignore());
            mValueLabel = this.Add(Ui.Text("", "slider__value").Ignore());

            mTrack.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button != 0) return;
                mDragging = true;
                mTrack.CapturePointer(e.pointerId);
                SetFromLocal(e.localPosition.x);
                Focus();
                e.StopPropagation();
            });
            mTrack.RegisterCallback<PointerMoveEvent>(e => { if (mDragging && mTrack.HasPointerCapture(e.pointerId)) SetFromLocal(e.localPosition.x); });
            mTrack.RegisterCallback<PointerUpEvent>(e =>
            {
                if (!mDragging) return;
                mDragging = false;
                mTrack.ReleasePointer(e.pointerId);
                UiSound.Play(UiSound.Click);
                Committed?.Invoke();
            });
            mTrack.RegisterCallback<PointerCaptureOutEvent>(_ => { if (mDragging) { mDragging = false; Committed?.Invoke(); } });
            RegisterCallback<NavigationMoveEvent>(e =>
            {
                if (e.direction == NavigationMoveEvent.Direction.Left || e.direction == NavigationMoveEvent.Direction.Right)
                {
                    float dir = e.direction == NavigationMoveEvent.Direction.Left ? -1 : 1;
                    float step = Step > 0 ? Step : (Max - Min) / 20f;
                    SetValue(mValue + dir * step, true);
                    Committed?.Invoke();
                    e.StopPropagation();
                    focusController?.IgnoreEvent(e);
                }
            });
            RegisterCallback<PointerEnterEvent>(_ => UiSound.Play(UiSound.Hover));
            RegisterCallback<GeometryChangedEvent>(_ => Sync());
        }

        public float Value { get => mValue; set => SetValue(value, false); }

        void SetFromLocal(float x)
        {
            float w = mTrack.layout.width;
            if (w <= 1f) return;
            SetValue(Mathf.Lerp(Min, Max, Mathf.Clamp01(x / w)), true);
        }

        public void SetValue(float v, bool notify)
        {
            if (Step > 0) v = Min + Mathf.Round((v - Min) / Step) * Step;
            v = Mathf.Clamp(v, Min, Max);
            bool changed = Mathf.Abs(v - mValue) > 1e-5f;
            mValue = v;
            Sync();
            if (changed && notify) Changed?.Invoke(v);
        }

        void Sync()
        {
            float t = Max > Min ? (mValue - Min) / (Max - Min) : 0f;
            mFill.style.width = Length.Percent(t * 100f);
            mThumb.style.left = Length.Percent(t * 100f);
            mValueLabel.text = mFormat != null ? mFormat(mValue) : mValue.ToString("0.##");
        }
    }

    /// <summary>On/off switch.</summary>
    public sealed class WmSwitch : VisualElement
    {
        bool mValue;
        public event Action<bool> Changed;

        public WmSwitch()
        {
            AddToClassList("switch");
            focusable = true;
            Add(Ui.Div("switch__knob").Ignore());
            this.AddManipulator(new Clickable(() => Toggle()));
            RegisterCallback<NavigationSubmitEvent>(_ => Toggle());
            RegisterCallback<PointerEnterEvent>(_ => UiSound.Play(UiSound.Hover));
        }

        public bool Value
        {
            get => mValue;
            set { mValue = value; EnableInClassList("switch--on", value); }
        }

        public void Toggle()
        {
            Value = !mValue;
            UiSound.Play(UiSound.Click);
            Changed?.Invoke(mValue);
        }
    }

    /// <summary>
    /// Segmented control. An optional passive option (e.g. "CUSTOM") can be shown selected but is not
    /// clickable.
    /// </summary>
    public sealed class WmChoice : VisualElement
    {
        readonly Button[] mOpts;
        readonly int mPassive;
        int mIndex = -1;
        public event Action<int> Changed;

        public WmChoice(string[] options, int passiveIndex = -1, bool accent = false)
        {
            AddToClassList("choice");
            mPassive = passiveIndex;
            mOpts = new Button[options.Length];
            for (int i = 0; i < options.Length; i++)
            {
                int k = i;
                var b = new Button { text = options[i] };
                b.AddToClassList("choice__opt");
                if (accent) b.AddToClassList("choice__opt--accent");
                if (i == passiveIndex) { b.AddToClassList("choice__opt--passive"); b.focusable = false; b.pickingMode = PickingMode.Ignore; }
                else
                {
                    b.clicked += () => { if (mIndex == k) return; UiSound.Play(UiSound.Click); Index = k; Changed?.Invoke(k); };
                    b.RegisterCallback<PointerEnterEvent>(_ => UiSound.Play(UiSound.Hover));
                }
                mOpts[i] = b;
                Add(b);
            }
        }

        public int Index
        {
            get => mIndex;
            set
            {
                mIndex = value;
                for (int i = 0; i < mOpts.Length; i++)
                {
                    mOpts[i].EnableInClassList("choice__opt--on", i == value);
                    if (i == mPassive) mOpts[i].style.display = i == value ? DisplayStyle.Flex : DisplayStyle.None;
                }
            }
        }

        /// <summary>Programmatic pick (tests and keyboard).</summary>
        public void Pick(int i)
        {
            if (i < 0 || i >= mOpts.Length || i == mPassive) return;
            Index = i;
            Changed?.Invoke(i);
        }
    }

    /// <summary>‹ value › stepper for longer option lists.</summary>
    public sealed class WmStepper : VisualElement
    {
        readonly string[] mOptions;
        readonly Label mValue;
        int mIndex;
        public event Action<int> Changed;

        public WmStepper(string[] options)
        {
            mOptions = options;
            AddToClassList("stepper");
            focusable = true;
            var prev = this.Add(new Button(() => Step(-1)) { text = "‹" }.Cls("stepper__btn"));
            prev.focusable = false;
            mValue = this.Add(Ui.Text("", "stepper__value").Ignore());
            var next = this.Add(new Button(() => Step(1)) { text = "›" }.Cls("stepper__btn"));
            next.focusable = false;
            RegisterCallback<NavigationMoveEvent>(e =>
            {
                if (e.direction == NavigationMoveEvent.Direction.Left) Step(-1);
                else if (e.direction == NavigationMoveEvent.Direction.Right) Step(1);
                else return;
                e.StopPropagation();
                focusController?.IgnoreEvent(e);
            });
        }

        public int Index
        {
            get => mIndex;
            set { mIndex = Mathf.Clamp(value, 0, mOptions.Length - 1); mValue.text = mOptions[mIndex]; }
        }

        void Step(int d)
        {
            int n = Mathf.Clamp(mIndex + d, 0, mOptions.Length - 1);
            if (n == mIndex) { UiSound.Play(UiSound.Error); return; }
            UiSound.Play(UiSound.Click);
            Index = n;
            Changed?.Invoke(n);
        }
    }
}
