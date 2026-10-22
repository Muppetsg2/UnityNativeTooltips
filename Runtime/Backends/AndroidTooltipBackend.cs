#if UNITY_ANDROID
using System;
using System.Collections.Generic;
using NativeTooltips.Internal;
using UnityEngine;

namespace NativeTooltips.Backends
{
    /// <summary>
    /// Android: recreates the framework tooltip (android.view.View#setTooltipText, API 26+) the same
    /// way AOSP's TooltipPopup does: a PopupWindow with a TextView whose background and text colour come
    /// from the theme attributes <c>android:tooltipFrameBackground</c> and
    /// <c>android:tooltipForegroundColor</c>. Device makers (Samsung One UI, Pixel, etc.) style these
    /// their own way, so the bubble matches the rest of the system.
    ///
    /// There is no public "show this view's tooltip now" API, and overlaying real Views with tooltipText
    /// would block touch input for Unity — so the bubble is built from the same resources instead.
    /// Everything goes through JNI from C# (AndroidJavaObject) — no .java/.aar files, no R8/ProGuard issues.
    ///
    /// Style support: everything except the Windows-only options. Sizes/padding keep the theme's
    /// background drawable; colors replace it with a GradientDrawable.
    /// </summary>
    internal sealed class AndroidTooltipBackend : ITooltipBackend
    {
        // Values from AOSP frameworks/base/core/res/res/values/dimens.xml and layout/tooltip.xml
        const float PaddingHorizontalDp = 16f;
        const float PaddingVerticalDp = 6.5f;
        const float TextSizeSp = 14f;
        const float MaxWidthDp = 256f;
        const float CornerRadiusDp = 2f;
        const float MarginDp = 8f;
        const float YOffsetTouchDp = 16f;
        const float PreciseAnchorThresholdDp = 96f;
        const float PreciseAnchorExtraOffsetDp = 8f;

        AndroidJavaObject _activity;

        // used on the UI thread only
        AndroidJavaObject _popup;
        AndroidJavaObject _textView;
        AndroidJavaObject _themeBackground;
        AndroidJavaObject _themeTextColors;
        int _lastX, _lastY;

        float _longPressTimeout = 0.5f;

        public string Name => "Android framework tooltip style (PopupWindow)";
        public bool IsNative => true;
        public TooltipFeature Features => TooltipFeature.AllCustom;
        public bool UsesSystemHoverDelay(in TooltipRequest request) => false;
        public float InitialDelay => _longPressTimeout; // View shows hover tooltips after getLongPressTimeout()
        public float ReshowDelay => 0.1f;
        public float AutoPopDelay => 15f;               // ViewConfiguration.getHoverTooltipHideTimeout()
        public float LongPressTimeout => _longPressTimeout;

        /// <summary>Immutable copy of a request for the UI thread.</summary>
        sealed class Job
        {
            public string Text, Title;
            public List<RichTextRun> TextRuns, TitleRuns;
            public TooltipStyle Style;
            public TooltipTrigger Trigger;
            public Rect Anchor;
            public bool HasAnchor;
            public Vector2 Pointer;
            public bool IsUpdate;
            public bool Haptic;
        }

        public bool Initialize()
        {
            using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                _activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
            if (_activity == null) return false;

            using (var vc = new AndroidJavaClass("android.view.ViewConfiguration"))
                _longPressTimeout = Mathf.Max(0.1f, vc.CallStatic<int>("getLongPressTimeout") / 1000f);

            return true;
        }

        public void Show(in TooltipRequest request)
        {
            if (_activity == null) return;

            var job = new Job
            {
                Text = request.Text,
                Title = request.Title,
                TextRuns = request.TextRuns != null ? new List<RichTextRun>(request.TextRuns) : null,
                TitleRuns = request.TitleRuns != null ? new List<RichTextRun>(request.TitleRuns) : null,
                Style = (request.Style ?? TooltipSettings.Style).Clone(), // the UI thread must not see later edits
                Trigger = request.Trigger,
                Anchor = request.Anchor,
                HasAnchor = request.HasAnchor,
                Pointer = request.Pointer,
                IsUpdate = request.IsUpdate,
                Haptic = request.Trigger == TooltipTrigger.LongPress && TooltipSettings.HapticFeedback
            };

            RunOnUiThread(() => ShowOnUiThread(job));
        }

        public void Hide()
        {
            if (_activity == null) return;
            RunOnUiThread(HideOnUiThread);
        }

        public void Tick() { }

        public void Shutdown()
        {
            if (_activity == null) return;
            var activity = _activity;
            RunOnUiThread(() =>
            {
                HideOnUiThread();
                _popup?.Dispose();
                _textView?.Dispose();
                _themeBackground?.Dispose();
                _themeTextColors?.Dispose();
                _popup = _textView = _themeBackground = _themeTextColors = null;
            });
            _activity = null;
            activity.Dispose();
        }

        // ------------------------------------------------------------------ UI thread

        void RunOnUiThread(Action action)
        {
            _activity.Call("runOnUiThread", new AndroidJavaRunnable(() =>
            {
                try { action(); }
                catch (Exception e) { Debug.LogWarning("[NativeTooltips] Android: " + e.Message); }
            }));
        }

        void ShowOnUiThread(Job job)
        {
            using (var content = FindContentView())
            {
                if (content == null) return;
                using (var token = content.Call<AndroidJavaObject>("getWindowToken"))
                    if (token == null) return; // the window doesn't exist (yet / anymore)

                float density;
                using (var res = _activity.Call<AndroidJavaObject>("getResources"))
                using (var dm = res.Call<AndroidJavaObject>("getDisplayMetrics"))
                    density = dm.Get<float>("density");

                if (_textView == null) CreateViews();

                TooltipStyle style = job.Style;
                ApplyStyle(style, density);
                SetText(job, style.ResolveFontSize(TextSizeSp));

                int maxWidth = Mathf.RoundToInt(style.ResolveMaxWidth(MaxWidthDp) * density);
                _textView.Call("setMaxWidth", maxWidth);

                int pw, ph;
                using (var spec = new AndroidJavaClass("android.view.View$MeasureSpec"))
                {
                    int wSpec = spec.CallStatic<int>("makeMeasureSpec", maxWidth, unchecked((int)0x80000000)); // AT_MOST
                    int hSpec = spec.CallStatic<int>("makeMeasureSpec", 0, 0);                                  // UNSPECIFIED
                    _textView.Call("measure", wSpec, hSpec);
                    pw = _textView.Call<int>("getMeasuredWidth");
                    ph = _textView.Call<int>("getMeasuredHeight");
                }

                bool showing = _popup.Call<bool>("isShowing");
                if (job.IsUpdate && showing)
                {
                    _popup.Call("update", _lastX, _lastY, pw, ph); // only the text changed — keep the position
                    return;
                }

                // Unity view area in window coordinates.
                int left, top, width, height;
                using (var r = new AndroidJavaObject("android.graphics.Rect"))
                {
                    content.Call<bool>("getGlobalVisibleRect", r);
                    left = r.Get<int>("left");
                    top = r.Get<int>("top");
                    width = r.Call<int>("width");
                    height = r.Call<int>("height");
                }

                var pointer = new Vector2(left + job.Pointer.x * width, top + job.Pointer.y * height);
                var anchor = new Rect(left + job.Anchor.x * width, top + job.Anchor.y * height,
                    job.Anchor.width * width, job.Anchor.height * height);
                bool fromTouch = job.Trigger == TooltipTrigger.LongPress;

                // AOSP: for large views, anchor at the touch point rather than the view edge.
                if (fromTouch && job.HasAnchor && anchor.height >= PreciseAnchorThresholdDp * density)
                {
                    float extra = PreciseAnchorExtraOffsetDp * density;
                    anchor = new Rect(pointer.x, pointer.y - extra, 0f, extra * 2f);
                }

                TooltipPlacement placement = TooltipLayout.Resolve(style.Placement, job.Trigger, job.HasAnchor);
                float margin = MarginDp * density;
                Vector2 pos = TooltipLayout.Place(new LayoutInput
                {
                    Placement = placement,
                    Pointer = pointer,
                    CursorHeight = (fromTouch ? YOffsetTouchDp : 20f) * density,
                    HasAnchor = job.HasAnchor,
                    Anchor = anchor,
                    Size = new Vector2(pw, ph),
                    Offset = TooltipLayout.OffsetToPixels(style.Offset, density),
                    // Touch: keep the bubble clear of the finger.
                    Gap = (fromTouch ? YOffsetTouchDp : TooltipLayout.IsElementPlacement(placement) ? 8f : 4f) * density,
                    WorkArea = new Rect(left + margin, top + margin, width - 2f * margin, height - 2f * margin),
                    CenterOnPointer = true
                });

                _lastX = Mathf.RoundToInt(pos.x);
                _lastY = Mathf.RoundToInt(pos.y);

                if (showing)
                    _popup.Call("update", _lastX, _lastY, pw, ph);
                else
                    _popup.Call("showAtLocation", content, 0 /* Gravity.NO_GRAVITY */, _lastX, _lastY);

                if (job.Haptic) content.Call<bool>("performHapticFeedback", 0 /* HapticFeedbackConstants.LONG_PRESS */);
            }
        }

        void HideOnUiThread()
        {
            if (_popup != null && _popup.Call<bool>("isShowing")) _popup.Call("dismiss");
        }

        const int SpanExclusiveExclusive = 33;

        void SetText(Job job, float baseSp)
        {
            bool hasTitle = !string.IsNullOrEmpty(job.Title);
            if (!hasTitle && job.TextRuns == null)
            {
                _textView.Call("setText", job.Text ?? string.Empty);
                return;
            }

            using (var sb = new AndroidJavaObject("android.text.SpannableStringBuilder"))
            {
                int length = 0;
                if (hasTitle)
                {
                    // Bold title line.
                    int start = length;
                    length = AppendRuns(sb, length, job.TitleRuns ?? TooltipRichText.Plain(job.Title), baseSp);
                    using (var bold = new AndroidJavaObject("android.text.style.StyleSpan", 1 /* Typeface.BOLD */))
                        sb.Call("setSpan", bold, start, length, SpanExclusiveExclusive);
                    length = Append(sb, length, "\n");
                }

                AppendRuns(sb, length, job.TextRuns ?? TooltipRichText.Plain(job.Text), baseSp);
                _textView.Call("setText", sb);
            }
        }

        static int Append(AndroidJavaObject sb, int length, string text)
        {
            using (sb.Call<AndroidJavaObject>("append", text)) { }
            return length + text.Length; // Java and C# strings are both UTF-16
        }

        static int AppendRuns(AndroidJavaObject sb, int length, List<RichTextRun> runs, float baseSp)
        {
            foreach (RichTextRun run in runs)
            {
                if (string.IsNullOrEmpty(run.Text)) continue;
                int start = length;
                length = Append(sb, length, run.Text);

                int style = (run.Bold ? 1 : 0) | (run.Italic ? 2 : 0); // Typeface.BOLD / ITALIC / BOLD_ITALIC
                if (style != 0) SetSpan(sb, new AndroidJavaObject("android.text.style.StyleSpan", style), start, length);
                if (run.Underline) SetSpan(sb, new AndroidJavaObject("android.text.style.UnderlineSpan"), start, length);
                if (run.Strikethrough) SetSpan(sb, new AndroidJavaObject("android.text.style.StrikethroughSpan"), start, length);
                if (run.HasColor) SetSpan(sb, new AndroidJavaObject("android.text.style.ForegroundColorSpan", ToArgb(run.Color)), start, length);
                if (run.HasSize)
                {
                    // AbsoluteSizeSpan(size, dip = true): density-independent pixels, matching the style's points.
                    int size = Mathf.Max(1, Mathf.RoundToInt(run.ResolveSize(baseSp)));
                    SetSpan(sb, new AndroidJavaObject("android.text.style.AbsoluteSizeSpan", size, true), start, length);
                }
            }

            return length;
        }

        static void SetSpan(AndroidJavaObject sb, AndroidJavaObject span, int start, int end)
        {
            using (span) sb.Call("setSpan", span, start, end, SpanExclusiveExclusive);
        }

        void ApplyStyle(TooltipStyle style, float density)
        {
            // Background --------------------------------------------------------------
            AndroidJavaObject background = null;
            bool ownsBackground = false;
            if (style.OverrideColors)
            {
                background = new AndroidJavaObject("android.graphics.drawable.GradientDrawable");
                background.Call("setColor", ToArgb(style.BackgroundColor));
                background.Call("setCornerRadius", style.ResolveCornerRadius(CornerRadiusDp) * density);
                ownsBackground = true;
            }
            else if (style.CornerRadius >= 0f && _themeBackground != null)
            {
                background = CopyWithRadius(_themeBackground, style.CornerRadius * density);
                ownsBackground = background != null;
            }

            if (background == null)
            {
                background = _themeBackground ?? FallbackBackground(density);
                ownsBackground = _themeBackground == null;
            }

            _textView.Call("setBackground", background);
            if (ownsBackground) background.Dispose();

            // Text --------------------------------------------------------------------
            if (style.OverrideColors) _textView.Call("setTextColor", ToArgb(style.TextColor));
            else if (_themeTextColors != null) _textView.Call("setTextColor", _themeTextColors);
            else _textView.Call("setTextColor", unchecked((int)0xFFFFFFFF));

            _textView.Call("setTextSize", 2 /* COMPLEX_UNIT_SP */, style.ResolveFontSize(TextSizeSp));
            using (var typeface = new AndroidJavaClass("android.graphics.Typeface"))
            using (var face = typeface.GetStatic<AndroidJavaObject>(style.Bold ? "DEFAULT_BOLD" : "DEFAULT"))
                _textView.Call("setTypeface", face);

            int ph = Mathf.RoundToInt(style.ResolvePaddingX(PaddingHorizontalDp) * density);
            int pv = Mathf.RoundToInt(style.ResolvePaddingY(PaddingVerticalDp) * density);
            _textView.Call("setPadding", ph, pv, ph, pv);
        }

        static AndroidJavaObject CopyWithRadius(AndroidJavaObject drawable, float radiusPx)
        {
            try
            {
                using (var state = drawable.Call<AndroidJavaObject>("getConstantState"))
                {
                    if (state == null) return null;
                    AndroidJavaObject copy;
                    using (var fresh = state.Call<AndroidJavaObject>("newDrawable"))
                        copy = fresh.Call<AndroidJavaObject>("mutate");
                    string cls;
                    using (var klass = copy.Call<AndroidJavaObject>("getClass"))
                        cls = klass.Call<string>("getName");

                    if (cls == "android.graphics.drawable.GradientDrawable")
                    {
                        copy.Call("setCornerRadius", radiusPx);
                        return copy;
                    }

                    copy.Dispose(); // not a shape — keep the theme's own drawable
                    return null;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        static AndroidJavaObject FallbackBackground(float density)
        {
            // AOSP res/drawable/tooltip_frame_dark.xml: #E6616161, 2dp radius
            var d = new AndroidJavaObject("android.graphics.drawable.GradientDrawable");
            d.Call("setColor", unchecked((int)0xE6616161));
            d.Call("setCornerRadius", CornerRadiusDp * density);
            return d;
        }

        static int ToArgb(Color c)
        {
            Color32 c32 = c;
            return (c32.a << 24) | (c32.r << 16) | (c32.g << 8) | c32.b;
        }

        AndroidJavaObject FindContentView()
        {
            const int androidRIdContent = 0x01020002; // android.R.id.content
            var view = _activity.Call<AndroidJavaObject>("findViewById", androidRIdContent);
            if (view != null) return view;
            using (var window = _activity.Call<AndroidJavaObject>("getWindow"))
                return window.Call<AndroidJavaObject>("getDecorView");
        }

        void CreateViews()
        {
            _textView = new AndroidJavaObject("android.widget.TextView", _activity);
            _themeBackground = ResolveThemeDrawable("tooltipFrameBackground");
            _themeTextColors = ResolveThemeColorStateList("tooltipForegroundColor");

            _textView.Call("setMaxLines", 8);
            using (var truncate = new AndroidJavaClass("android.text.TextUtils$TruncateAt"))
            using (var end = truncate.GetStatic<AndroidJavaObject>("END"))
                _textView.Call("setEllipsize", end);

            // PopupWindow(View contentView, int width, int height, boolean focusable)
            _popup = new AndroidJavaObject("android.widget.PopupWindow", _textView, -2, -2, false);
            _popup.Call("setTouchable", false);
            _popup.Call("setFocusable", false);
            _popup.Call("setOutsideTouchable", false);
            using (var style = new AndroidJavaClass("android.R$style"))
                _popup.Call("setAnimationStyle", style.GetStatic<int>("Animation_Toast"));
        }

        AndroidJavaObject ResolveThemeDrawable(string attrName)
        {
            try
            {
                int attr;
                using (var attrs = new AndroidJavaClass("android.R$attr"))
                    attr = attrs.GetStatic<int>(attrName); // API 26+, throws on older versions
                using (var theme = _activity.Call<AndroidJavaObject>("getTheme"))
                using (var a = theme.Call<AndroidJavaObject>("obtainStyledAttributes", new[] { attr }))
                {
                    var d = a.Call<AndroidJavaObject>("getDrawable", 0);
                    a.Call("recycle");
                    return d;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        AndroidJavaObject ResolveThemeColorStateList(string attrName)
        {
            try
            {
                int attr;
                using (var attrs = new AndroidJavaClass("android.R$attr"))
                    attr = attrs.GetStatic<int>(attrName);
                using (var theme = _activity.Call<AndroidJavaObject>("getTheme"))
                using (var a = theme.Call<AndroidJavaObject>("obtainStyledAttributes", new[] { attr }))
                {
                    var c = a.Call<AndroidJavaObject>("getColorStateList", 0);
                    a.Call("recycle");
                    return c;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
#endif
