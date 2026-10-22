using System;
using NativeTooltips.Internal;
using UnityEngine;

namespace NativeTooltips
{
    /// <summary>
    /// Entry point of the plugin. Most projects never need to call it directly — just add the
    /// <see cref="NativeTooltip"/> component (UGUI / objects with colliders) or the
    /// <see cref="NativeTooltipManipulator"/> (UI Toolkit).
    /// </summary>
    public static class Tooltip
    {
        /// <summary>True when the active backend uses the operating system's native API (not the IMGUI fallback).</summary>
        public static bool IsNative => TooltipDriver.Instance.Backend.IsNative;

        /// <summary>Name of the active backend, e.g. "Win32 (comctl32 tooltips_class32)".</summary>
        public static string BackendName => TooltipDriver.Instance.Backend.Name;

        /// <summary>Style features the active backend can honour.</summary>
        public static TooltipFeature SupportedFeatures => TooltipDriver.Instance.Backend.Features;

        /// <summary>
        /// True when the active backend honours all of the given style feature(s), e.g.
        /// <c>Tooltip.Supports(TooltipFeature.FontSize | TooltipFeature.Colors)</c>.
        /// </summary>
        public static bool Supports(TooltipFeature feature) => (SupportedFeatures & feature) == feature;

        /// <summary>
        /// Shows a tooltip immediately (no delay) at the cursor on desktop, or at the last pointer
        /// position on touch devices. It stays until <see cref="Hide"/> is called or until
        /// <paramref name="duration"/> seconds elapse (0 = no limit).
        /// </summary>
        public static void Show(string text, float duration = 0f, TooltipStyle style = null,
            string title = null, TooltipIcon icon = TooltipIcon.None)
        {
            TooltipDriver.Instance.ShowManual(text, null, duration, style, title, icon);
        }

        /// <summary>Shows a tooltip anchored to a screen rectangle (pixels, origin bottom-left — same as <c>Input.mousePosition</c>).</summary>
        public static void Show(string text, Rect screenRect, float duration = 0f, TooltipStyle style = null,
            string title = null, TooltipIcon icon = TooltipIcon.None)
        {
            TooltipDriver.Instance.ShowManual(text, screenRect, duration, style, title, icon);
        }

        /// <summary>Shows a tooltip anchored to a UGUI element.</summary>
        public static void Show(string text, RectTransform anchor, float duration = 0f, TooltipStyle style = null,
            string title = null, TooltipIcon icon = TooltipIcon.None)
        {
            if (anchor == null) throw new ArgumentNullException(nameof(anchor));
            Rect? rect = ScreenRectUtility.TryGetScreenRect(anchor, out var r) ? r : (Rect?)null;
            TooltipDriver.Instance.ShowManual(text, rect, duration, style, title, icon);
        }

        /// <summary>Hides the tooltip (both manually shown and hover tooltips).</summary>
        public static void Hide()
        {
            if (TooltipDriver.HasInstance) TooltipDriver.Instance.HideAll();
        }

        /// <summary>
        /// Initializes the backend up front (e.g. on a loading screen) so the first hover does not
        /// pay the cost of creating the native window.
        /// </summary>
        public static void Initialize()
        {
            _ = TooltipDriver.Instance;
        }

        /// <summary>
        /// Shuts down the current backend and creates a new one (e.g. after changing
        /// <see cref="TooltipSettings.ForceFallback"/>).
        /// </summary>
        public static void Reinitialize()
        {
            if (TooltipDriver.HasInstance) TooltipDriver.Instance.RecreateBackend();
        }

        // ---------- API for custom tooltip sources (ITooltipSource) ----------

        /// <summary>The cursor (or pen) entered the source. <paramref name="screenPosition"/> is in screen pixels, (0,0) = bottom-left.</summary>
        public static void PointerEnter(ITooltipSource source, Vector2 screenPosition, bool isTouch)
            => TooltipDriver.Instance.PointerEnter(source, screenPosition, isTouch);

        public static void PointerMove(ITooltipSource source, Vector2 screenPosition, bool isTouch)
        {
            if (TooltipDriver.HasInstance) TooltipDriver.Instance.PointerMove(source, screenPosition, isTouch);
        }

        public static void PointerExit(ITooltipSource source, bool isTouch)
        {
            if (TooltipDriver.HasInstance) TooltipDriver.Instance.PointerExit(source, isTouch);
        }

        /// <summary>
        /// Press. For touch it starts the long-press timer; for a mouse it hides the tooltip until the
        /// pointer leaves the element (standard OS behaviour). <paramref name="onLongPress"/> is invoked
        /// when a long-press tooltip is shown — e.g. to cancel the click.
        /// </summary>
        public static void PointerDown(ITooltipSource source, Vector2 screenPosition, bool isTouch, Action onLongPress = null)
            => TooltipDriver.Instance.PointerDown(source, screenPosition, isTouch, onLongPress);

        public static void PointerUp(ITooltipSource source, bool isTouch)
        {
            if (TooltipDriver.HasInstance) TooltipDriver.Instance.PointerUp(source, isTouch);
        }

        /// <summary>Call when the source gets disabled or destroyed.</summary>
        public static void Release(ITooltipSource source)
        {
            if (TooltipDriver.HasInstance) TooltipDriver.Instance.Release(source);
        }
    }

    /// <summary>
    /// A tooltip source — anything that can be hovered: a UGUI element, a UI Toolkit element,
    /// a 3D object, a region drawn with IMGUI, etc.
    /// </summary>
    public interface ITooltipSource
    {
        /// <summary>Current text. Empty/null = no tooltip. Read every frame, so changes show up live.</summary>
        string TooltipText { get; }

        /// <summary>Whether the source still exists and is active.</summary>
        bool IsTooltipActive { get; }

        /// <summary>Element rectangle in screen pixels (origin bottom-left).</summary>
        bool TryGetScreenRect(out Rect screenRect);
    }

    /// <summary>Global settings. Can be changed at any time (e.g. in Awake of the first scene).</summary>
    public static class TooltipSettings
    {
        /// <summary>Global on/off switch.</summary>
        public static bool Enabled = true;

        /// <summary>
        /// Default style for every tooltip that doesn't set its own. Modify it in place
        /// (<c>TooltipSettings.Style.FontScale = 1.25f;</c>) or assign a new one / an asset's style.
        /// </summary>
        public static TooltipStyle Style
        {
            get => s_Style ?? (s_Style = new TooltipStyle());
            set => s_Style = value;
        }

        static TooltipStyle s_Style;

        /// <summary>When macOS and WebGL replace the system tooltip with a native-looking custom bubble.</summary>
        public static CustomWindowMode CustomWindowMode = CustomWindowMode.WhenNeeded;

        /// <summary>
        /// Interpret Unity-style rich text tags in tooltip texts and titles: <c>&lt;b&gt;</c>, <c>&lt;i&gt;</c>,
        /// <c>&lt;u&gt;</c>, <c>&lt;s&gt;</c>, <c>&lt;color=#FF0000&gt;</c>, <c>&lt;size=16&gt;</c>, <c>&lt;noparse&gt;</c>.
        /// Text without tags is unaffected (and stays on the plain native path). When off, tags are shown literally.
        /// </summary>
        public static bool RichText = true;

        /// <summary>Use the IMGUI tooltip when the native backend is unavailable (e.g. no GTK, missing macOS bundle).</summary>
        public static bool UseFallbackWhenNativeUnavailable = true;

        /// <summary>Always use the IMGUI tooltip (useful for testing). Call <see cref="Tooltip.Reinitialize"/> after changing.</summary>
        public static bool ForceFallback = false;

        /// <summary>
        /// The Editor uses the IMGUI tooltip by default. On Windows and Linux the native backend can be
        /// enabled in the Editor too (the tooltip appears at the cursor, so it works fine over the Game view).
        /// </summary>
        public static bool UseNativeInEditor = false;

        /// <summary>Hover delay before showing (s). &lt; 0 = system value (Windows: TTDT_INITIAL, usually 0.5 s).</summary>
        public static float InitialDelayOverride = -1f;

        /// <summary>Time after which a hover tooltip hides by itself (s). &lt; 0 = system value, 0 = never.</summary>
        public static float AutoPopDelayOverride = -1f;

        /// <summary>Long-press duration (s). &lt; 0 = system value (Android: ViewConfiguration.getLongPressTimeout()).</summary>
        public static float LongPressTimeoutOverride = -1f;

        /// <summary>After the finger is lifted the tooltip hides after this time (Android: 1.5 s).</summary>
        public static float TouchHideDelay = 1.5f;

        /// <summary>Like Win32: the delay restarts while the cursor keeps moving over the element.</summary>
        public static bool RequireStationaryPointer = true;

        /// <summary>Cursor movement tolerance (px) below which the cursor counts as stationary.</summary>
        public static float StationaryTolerance = 3f;

        /// <summary>When a long-press shows a tooltip, cancel the button click (like Android's View).</summary>
        public static bool SuppressClickAfterLongPress = true;

        /// <summary>Haptic feedback when a long-press tooltip is shown (Android/iOS).</summary>
        public static bool HapticFeedback = true;

        /// <summary>Maximum tooltip width in points. Shortcut for <c>TooltipSettings.Style.MaxWidth</c> (0 = platform default).</summary>
        [Obsolete("Use TooltipSettings.Style.MaxWidth (or a per-tooltip TooltipStyle) instead.")]
        public static float MaxWidth
        {
            get => Style.MaxWidth;
            set => Style.MaxWidth = value;
        }

        /// <summary>Log backend selection diagnostics to the console.</summary>
        public static bool VerboseLogging = false;
    }

    /// <summary>What triggered the tooltip.</summary>
    public enum TooltipTrigger
    {
        Hover,
        LongPress,
        Manual
    }
}
